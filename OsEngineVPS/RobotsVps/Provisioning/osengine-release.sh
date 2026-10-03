#!/bin/bash
# osengine-release — updates the OsEngine build of the VPS terminals from the newest SIGNED server release on GitHub.
# Installed as /usr/local/bin/osengine-release (by "Deploy / repair server" or by hand); used from the phone app and by hand.
#
# Usage:
#   osengine-release check                 versions installed in every terminal and the newest release; changes nothing
#   osengine-release apply [--only <svc>]  starts the update in the background (a systemd unit, so it survives a lost
#                                          SSH connection) and returns at once; progress: "status"
#   osengine-release status                RUNNING/IDLE, result of the last update and its log
#
# How it is trusted: the release package is signed on the developer PC with an ed25519 key (ssh-keygen -Y sign); this
# server holds only the public half (/etc/osengine/allowed_signers). A package that is not signed by that key is rejected,
# so a stolen GitHub account cannot put code on this server. Each terminal is then updated by osengine-update.sh
# (unpack next to the current build, switch, wait for the MCP API, automatic rollback), one after another; the first
# failure stops the run, so the other terminals are not touched.
#
# Output: lines that start with a marker word — INSTALLED, LATEST, STATE, RESULT, ERROR, STARTED, RUNNING, IDLE,
# LASTRESULT, and the STEP/OK/SKIP/WARN/FAIL/DONE lines of osengine-update.sh (prefixed with [terminal]).

set -u

CONF=/etc/osengine/release.conf
STATE=/var/lib/osengine-release
UPDATER=/usr/local/lib/osengine/osengine-update.sh
SIGNERS=/etc/osengine/allowed_signers
REPO=InterVictor/OsEngineVPS
TAG_PREFIX=server-
ASSET=osengine-headless-linux-x64.tgz
# shellcheck disable=SC1090
[ -f "$CONF" ] && . "$CONF"

SELF=$(readlink -f "$0")

# name service base port — one line per terminal; base and port come from the unit's ExecStart (--root, --mcp-port)
terminals() {
    local f svc ex root port name
    for f in /etc/systemd/system/osengine*.service; do
        [ -f "$f" ] || continue
        svc=$(basename "$f" .service)
        ex=$(grep '^ExecStart=' "$f")
        root=$(echo "$ex" | sed -n 's/.*--root \([^ ]*\).*/\1/p')
        port=$(echo "$ex" | sed -n 's/.*--mcp-port \([0-9]*\).*/\1/p')
        [ -n "$root" ] || continue
        if [ "$svc" = osengine ]; then name=main; else name=${svc#osengine-}; fi
        echo "$name $svc $(dirname "$root") ${port:-6500}"
    done
}

# the newest non-draft release whose tag starts with TAG_PREFIX and which carries the package and its signature:
# tag|published_at|first line of the notes|package url|signature url
latest_release() {
    curl -fsS --max-time 20 -H 'Accept: application/vnd.github+json' \
        "https://api.github.com/repos/$REPO/releases?per_page=30" 2>/dev/null | python3 -c '
import json, sys
prefix, asset = sys.argv[1], sys.argv[2]
try:
    data = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for r in data:
    if r.get("draft") or r.get("prerelease"):
        continue
    tag = r.get("tag_name", "")
    if not tag.startswith(prefix):
        continue
    urls = {a["name"]: a["browser_download_url"] for a in r.get("assets", [])}
    if asset not in urls or asset + ".sig" not in urls:
        continue
    notes = (r.get("body") or "").strip().splitlines()
    first = (notes[0] if notes else "").replace("|", " ")
    print("|".join([tag, r.get("published_at", ""), first, urls[asset], urls[asset + ".sig"]]))
    break
' "$TAG_PREFIX" "$ASSET"
}

cmd_check() {
    echo "CONFIG $REPO"
    local name svc base port v st
    while read -r name svc base port; do
        v=$(cut -c1-8 "$base/app/.package-sha256" 2>/dev/null)
        st=$(systemctl is-active "$svc" 2>/dev/null)
        echo "INSTALLED $name ${v:-none} ${st:-unknown}"
    done < <(terminals)

    local rel tag pub note pkg sig version need=0
    rel=$(latest_release)
    if [ -z "$rel" ]; then
        echo "ERROR no signed server release found in $REPO (or GitHub cannot be reached)"
        return 0   # the ERROR line is the answer; a non-zero exit would hide it from SSH clients
    fi
    IFS='|' read -r tag pub note pkg sig <<<"$rel"
    version=${tag#"$TAG_PREFIX"}
    echo "LATEST $version $pub $note"
    while read -r name svc base port; do
        v=$(cut -c1-8 "$base/app/.package-sha256" 2>/dev/null)
        if [ "$v" = "$version" ]; then
            echo "STATE $name current"
        else
            echo "STATE $name update"
            need=$((need + 1))
        fi
    done < <(terminals)
    if [ "$need" -gt 0 ]; then echo "RESULT update-available"; else echo "RESULT up-to-date"; fi
}

cmd_apply() {
    local only=""
    [ "${1:-}" = "--only" ] && only=${2:-}
    if systemctl is-active --quiet osengine-release-apply; then
        echo "ERROR an update is already running"
        return 0
    fi
    mkdir -p "$STATE"
    : > "$STATE/status.log"
    rm -f "$STATE/result"
    # a transient systemd unit: the update goes on even if this SSH session is lost
    if systemd-run --quiet --unit=osengine-release-apply --collect "$SELF" run ${only:+--only "$only"}; then
        echo "STARTED"
    else
        echo "ERROR cannot start the update"
        return 0
    fi
}

cmd_run() {
    local only=""
    [ "${1:-}" = "--only" ] && only=${2:-}
    local LOG="$STATE/status.log"
    mkdir -p "$STATE"
    log() { echo "$*" >> "$LOG"; }
    finish() { # ok|fail
        echo "$1 $(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$STATE/result"
        log "DONE $1"
        [ "$1" = ok ] && find "$STATE/dl" -mindepth 1 -maxdepth 1 -type d ! -name "${TAG:-none}" -exec rm -rf {} + 2>/dev/null
        [ "$1" = ok ] && exit 0
        exit 1
    }

    log "STEP 1/3 looking for the newest release"
    local rel pub note pkg sig
    rel=$(latest_release)
    [ -n "$rel" ] || { log "FAIL no signed server release found in $REPO (or GitHub cannot be reached)"; finish fail; }
    IFS='|' read -r TAG pub note pkg sig <<<"$rel"
    local version=${TAG#"$TAG_PREFIX"}
    log "OK $TAG ($note)"

    log "STEP 2/3 downloading and verifying the package"
    local dir="$STATE/dl/$TAG"
    mkdir -p "$dir"
    curl -fL --retry 3 --max-time 900 -sS -o "$dir/$ASSET" "$pkg" || { log "FAIL download of the package"; finish fail; }
    curl -fL --retry 3 --max-time 60 -sS -o "$dir/$ASSET.sig" "$sig" || { log "FAIL download of the signature"; finish fail; }
    local full
    full=$(sha256sum "$dir/$ASSET" | cut -c1-64)
    [ "${full:0:8}" = "$version" ] || { log "FAIL the package checksum ${full:0:8} does not match the release tag $version"; finish fail; }
    [ -f "$SIGNERS" ] || { log "FAIL no $SIGNERS — the signing key of this server is not installed"; finish fail; }
    if ssh-keygen -Y verify -f "$SIGNERS" -I osengine-release -n osengine-release -s "$dir/$ASSET.sig" < "$dir/$ASSET" > /dev/null 2>&1; then
        log "OK signature is valid, build $version"
    else
        log "FAIL the signature is NOT valid — package rejected, nothing was changed"
        rm -rf "$dir"
        finish fail
    fi
    [ -x "$UPDATER" ] || { log "FAIL $UPDATER is missing"; finish fail; }

    log "STEP 3/3 updating the terminals one by one"
    local name svc base port rc done_count=0
    while read -r name svc base port; do
        if [ -n "$only" ] && [ "$only" != "$svc" ] && [ "$only" != "$name" ]; then continue; fi
        log "STEP [$name] $svc"
        OSENGINE_BASE="$base" OSENGINE_SERVICE="$svc" OSENGINE_MCP_PORT="$port" bash "$UPDATER" "$dir/$ASSET" 2>&1 \
            | sed -u "s/^/[$name] /" >> "$LOG"
        rc=${PIPESTATUS[0]}
        if [ "$rc" -ne 0 ]; then
            log "FAIL [$name] the update failed (the terminal was rolled back); the other terminals were not touched"
            finish fail
        fi
        done_count=$((done_count + 1))
    done < <(terminals)
    [ "$done_count" -gt 0 ] || { log "FAIL no terminal to update${only:+ ($only)}"; finish fail; }
    finish ok
}

cmd_status() {
    if systemctl is-active --quiet osengine-release-apply; then echo "RUNNING"; else echo "IDLE"; fi
    if [ -f "$STATE/result" ]; then echo "LASTRESULT $(cat "$STATE/result")"; else echo "LASTRESULT none"; fi
    [ -f "$STATE/status.log" ] && tail -n 80 "$STATE/status.log"
    return 0
}

case "${1:-}" in
    check)  cmd_check ;;
    apply)  shift; cmd_apply "$@" ;;
    run)    shift; cmd_run "$@" ;;
    status) cmd_status ;;
    *)      sed -n '2,12p' "$SELF" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
