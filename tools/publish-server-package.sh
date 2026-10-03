#!/bin/bash
# Publishes the VPS server package (built by headless/build-package.sh) as a SIGNED release on GitHub, so that the
# terminals on the VPS can update themselves from it ("osengine-release apply", started from the phone app or by hand).
#
# Usage: bash tools/publish-server-package.sh [package.tgz]
#   package   default OsEngineVPS/bin/Debug/VpsServer/osengine-headless-linux-x64.tgz
#   RELEASE_NOTE   one line shown to the user before the update (default: build id and commit)
#   RELEASE_SIGN_KEY  signing key (default ~/.ssh/osengine_release_sign — stays on this PC, the VPS holds only the public half
#                     OsEngineVPS/RobotsVps/Provisioning/release-signers)
#   RELEASE_REPO   default InterVictor/OsEngineVPS
#
# The release is public (the repo is public): the package holds the compiled OsEngine server build only — no robots,
# settings or keys. Tag: server-<first 8 characters of the package SHA-256>; publishing the same package twice does nothing.
# Publishing makes it available to the phone; it does NOT restart anything on the VPS.

set -euo pipefail

FORK=$(cd "$(dirname "$0")/.." && { pwd -W 2>/dev/null || pwd; })
PKG=${1:-$FORK/OsEngineVPS/bin/Debug/VpsServer/osengine-headless-linux-x64.tgz}
KEY=${RELEASE_SIGN_KEY:-$HOME/.ssh/osengine_release_sign}
REPO=${RELEASE_REPO:-InterVictor/OsEngineVPS}
SIGNERS=$FORK/OsEngineVPS/RobotsVps/Provisioning/release-signers

[ -f "$PKG" ] || { echo "No package: $PKG (build it: bash headless/build-package.sh)"; exit 1; }
[ -f "$KEY" ] || { echo "No signing key $KEY"; exit 1; }
command -v gh > /dev/null || { echo "GitHub CLI (gh) is not installed"; exit 1; }

# The package is a build of modified OsEngine. The OsEngine license (LICENSE, 3.2 and 3.3) allows modified code for personal /
# internal use only, so it must NOT go to a public repository without the right holder's permission (a public release was
# taken down on 2026-10-02 for this reason). Publishing is refused while the repository is public.
if [ "$(gh repo view "$REPO" --json isPrivate -q .isPrivate 2> /dev/null)" != "true" ] && [ "${RELEASE_ALLOW_PUBLIC:-}" != "1" ]; then
    echo "Refused: $REPO is public. Make it private, or set RELEASE_ALLOW_PUBLIC=1 once the right holder has allowed it."
    exit 1
fi

SHA=$(sha256sum "$PKG" | cut -c1-64)
VERSION=${SHA:0:8}
TAG=server-$VERSION

if gh release view "$TAG" -R "$REPO" > /dev/null 2>&1; then
    echo "Already published: $TAG — nothing to do."
    exit 0
fi

# The release tag points to the head of the PUBLISHED branch; the server sources of this checkout must equal it
# (documents may differ), otherwise the package would not match the commit named in the release.
BRANCH=${RELEASE_BRANCH:-osengine-vps}
COMMIT=$(git -C "$FORK" ls-remote origin "refs/heads/$BRANCH" | cut -f1)
[ -n "$COMMIT" ] || { echo "No branch $BRANCH on GitHub — push it first."; exit 1; }
if ! git -C "$FORK" cat-file -e "$COMMIT^{commit}" 2> /dev/null \
    || ! git -C "$FORK" diff --quiet "$COMMIT" HEAD -- project/OsEngine headless ':!project/OsEngine/bin' \
    || [ -n "$(git -C "$FORK" status --porcelain --untracked-files=no -- project/OsEngine headless ':!project/OsEngine/bin' ':!headless/.gitignore' 2> /dev/null)" ]; then
    echo "The server sources here differ from GitHub $BRANCH (${COMMIT:0:8}) — commit and push them first."
    exit 1
fi

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
cp "$PKG" "$WORK/osengine-headless-linux-x64.tgz"
ssh-keygen -q -Y sign -f "$KEY" -n osengine-release "$WORK/osengine-headless-linux-x64.tgz"
ssh-keygen -Y verify -f "$SIGNERS" -I osengine-release -n osengine-release \
    -s "$WORK/osengine-headless-linux-x64.tgz.sig" < "$WORK/osengine-headless-linux-x64.tgz" > /dev/null \
    || { echo "The signature does not verify with $SIGNERS — wrong signing key?"; exit 1; }

NOTE=${RELEASE_NOTE:-"Server build $VERSION from osengine-vps@${COMMIT:0:8}, $(date -u +%Y-%m-%d)"}
gh release create "$TAG" "$WORK/osengine-headless-linux-x64.tgz" "$WORK/osengine-headless-linux-x64.tgz.sig" \
    -R "$REPO" --target "$COMMIT" --title "Server build $VERSION" \
    --notes "$NOTE

SHA-256 $SHA
Installed on the VPS by \`osengine-release apply\` (signature checked on the server)."

echo "Published $TAG: https://github.com/$REPO/releases/tag/$TAG"
