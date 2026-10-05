// Migration of one terminal from one VPS to another (both connected over SSH by this program). The terminal's data goes through
// this computer: a new terminal is created on the target, the source is stopped (a consistent snapshot, and no two copies trade
// with the same keys), its data is packed and downloaded, restored into the new terminal and the new terminal is started. The
// source stays stopped AND its autostart is switched off (its data is kept), so it cannot start by itself after a reboot.
//
// Safety rules of the order:
//  - the target is made first (the long step; the source keeps running meanwhile);
//  - if anything fails before the data was put into the new terminal, the source is started again;
//  - if the new terminal started with the data but cannot be verified, the source is NOT started again (two terminals with the same
//    exchange keys would trade twice): the user decides, the result says so.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    internal sealed class MigrationPlan
    {
        public string SourceKey;
        public string TargetVpsId;
        public string TargetName;
    }

    internal enum MigrationOutcome
    {
        /// <summary>the new terminal runs with the data, the source is stopped and disabled</summary>
        Done,
        /// <summary>failed before the data moved; the source runs again (or was never stopped)</summary>
        FailedSourceRunning,
        /// <summary>the data was put into the new terminal but it could not be verified; the source stays stopped</summary>
        FailedNeedsAttention
    }

    internal static class VpsMigration
    {
        /// <summary>
        /// Checks the plan without changing anything. Returns the error text, or null if the migration can start.
        /// </summary>
        public static string Check(MigrationPlan plan)
        {
            VpsRemoteSession.SplitKey(plan.SourceKey, out string sourceVps, out _);
            VpsInstance source = VpsRemoteSession.InstanceOf(plan.SourceKey);
            var target = VpsRemoteSession.SshOfVps(plan.TargetVpsId);

            if (source == null) return "The source terminal is not known (is its VPS connected over SSH?)";
            if (plan.TargetVpsId == sourceVps) return "Choose another VPS: the target must differ from the source";
            if (target == null) return "The target VPS is not connected over SSH: connect it in its tab first";
            if (!target.Value.Instances.Any(i => i.IsMain)) return "The target VPS has no OsEngine server yet: use \"Deploy / repair server\" in its tab first";
            if (!VpsInstances.IsValidName(plan.TargetName)) return "Name of the new terminal: 1-20 characters, latin letters, digits and '-', not \"main\"";
            if (target.Value.Instances.Any(i => string.Equals(i.Name, plan.TargetName, StringComparison.OrdinalIgnoreCase)))
                return $"The target VPS already has a terminal \"{plan.TargetName}\"";

            return null;
        }

        /// <summary>the open positions of a terminal ("SYMBOL (robot)"), read over MCP; null if they cannot be read</summary>
        public static async Task<List<string>> OpenPositionsAsync(RemoteMcpClient client)
        {
            try
            {
                JsonElement answer = await client.CallToolAsync("bot_journal_get_open_positions", new { limit = 500 }).ConfigureAwait(false);
                List<string> result = new List<string>();

                if (answer.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in positions.EnumerateArray())
                    {
                        string security = p.TryGetProperty("security_name", out JsonElement s) ? s.GetString() : "?";
                        string bot = p.TryGetProperty("bot_name", out JsonElement b) ? b.GetString() : "?";
                        result.Add($"{security} ({bot})");
                    }
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<(MigrationOutcome Outcome, string Info)> RunAsync(MigrationPlan plan, Action<string> log, CancellationToken cancel)
        {
            string error = Check(plan);
            if (error != null) throw new InvalidOperationException(error);

            VpsInstance source = VpsRemoteSession.InstanceOf(plan.SourceKey);
            VpsSshCredentials sourceSsh = VpsRemoteSession.SshCredentialsFor(plan.SourceKey);
            Func<string, Task<string>> sourceRun = VpsRemoteSession.SshRunFor(plan.SourceKey);
            var target = VpsRemoteSession.SshOfVps(plan.TargetVpsId).Value;
            string sourceTitle = source.Title;
            bool sourceWasRunning = source.IsActive;
            bool sourceStopped = false;
            bool dataMoved = false;
            bool createdOnTarget = false;

            try
            {
                // 1. the new terminal on the target (empty): the build and the robot scripts come from the main terminal there
                int port = VpsInstances.NextFreePort(target.Instances);
                log($"[1/6] Creating terminal \"{plan.TargetName}\" on the target VPS (MCP port {port})...");
                await Task.Run(() => new VpsProvisioner(target.Ssh, log).DeployAsync(plan.TargetName, port, cancel), cancel).ConfigureAwait(false);

                createdOnTarget = true;
                List<VpsInstance> targetInstances = await VpsInstances.ListAsync(target.Run).ConfigureAwait(false);
                VpsInstance created = targetInstances.FirstOrDefault(i => string.Equals(i.Name, plan.TargetName, StringComparison.OrdinalIgnoreCase));
                if (created == null) throw new InvalidOperationException("The new terminal does not show up on the target VPS");

                // 2. the source stops: from here on its robots do not manage positions until the new terminal runs
                if (sourceWasRunning)
                {
                    log($"[2/6] Stopping the source terminal \"{sourceTitle}\" (it saves its data first)...");
                    // from the moment the stop is sent the source counts as stopped: a failure from here on starts it again
                    sourceStopped = true;
                    await VpsInstances.StopAsync(sourceRun, source).ConfigureAwait(false);

                    // "systemctl is-active" answers with exit code 3 for a stopped service: that is the expected case
                    string state = (await sourceRun($"systemctl is-active {source.Service} || true").ConfigureAwait(false)).Trim();
                    if (state == "active") throw new InvalidOperationException("The source terminal did not stop");
                }
                else
                {
                    log("[2/6] The source terminal is not running: nothing to stop.");
                }

                // 3. a snapshot of the stopped source (consistent), downloaded to this computer
                log("[3/6] Packing the data of the source and downloading it to this computer...");
                string archive = await new VpsProvisioner(sourceSsh, log).BackupAsync(source, cancel).ConfigureAwait(false);
                log("      archive: " + archive);

                // 4. the data goes into the new terminal (it is stopped for the swap and started again; a failed restore puts its old data back)
                log("[4/6] Putting the data into the new terminal...");
                await new VpsProvisioner(target.Ssh, log).RestoreAsync(created, archive, cancel).ConfigureAwait(false);
                dataMoved = true;

                // 5. the new terminal answers
                log("[5/6] Waiting for the new terminal to start...");
                bool up = await WaitForTerminalAsync(target.Run, created, cancel).ConfigureAwait(false);

                if (!up)
                {
                    return (MigrationOutcome.FailedNeedsAttention,
                        $"The new terminal \"{plan.TargetName}\" has the data but does not answer. The source \"{sourceTitle}\" stays STOPPED (two copies would trade twice). "
                        + $"Look at the target (journalctl -u {created.Service}), then start one of the two.");
                }

                // the shown name travels with the terminal
                if (!string.Equals(sourceTitle, source.Name, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        List<VpsInstance> fresh = await VpsInstances.ListAsync(target.Run).ConfigureAwait(false);
                        VpsInstance now = fresh.FirstOrDefault(i => string.Equals(i.Name, plan.TargetName, StringComparison.OrdinalIgnoreCase));
                        if (now != null) await VpsInstances.RenameAsync(target.Run, fresh, now, sourceTitle).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log("      the shown name was not copied: " + ex.Message);
                    }
                }

                // 6. the source cannot start by itself any more (its data stays)
                log("[6/6] Switching off the autostart of the source terminal (its data is kept)...");
                await sourceRun($"systemctl disable {source.Service} >/dev/null 2>&1; echo done").ConfigureAwait(false);

                return (MigrationOutcome.Done, $"Terminal \"{sourceTitle}\" now runs on the target VPS as \"{plan.TargetName}\". The source is stopped and its autostart is off; its data is kept.");
            }
            catch (Exception ex)
            {
                if (dataMoved)
                {
                    return (MigrationOutcome.FailedNeedsAttention,
                        $"{ex.Message}. The data is already in the new terminal; the source \"{sourceTitle}\" stays STOPPED (two copies would trade twice).");
                }

                string restarted = "";

                if (sourceStopped)
                {
                    try
                    {
                        await VpsInstances.StartAsync(sourceRun, source).ConfigureAwait(false);
                        restarted = " The source terminal was started again.";
                    }
                    catch (Exception startEx)
                    {
                        restarted = " The source terminal could NOT be started again: " + startEx.Message + " Start it by hand.";
                    }
                }

                string leftover = createdOnTarget
                    ? $" An empty terminal \"{plan.TargetName}\" was created on the target VPS: remove it there if it is not needed."
                    : "";

                return (MigrationOutcome.FailedSourceRunning, ex.Message + "." + restarted + leftover);
            }
        }

        // the service is active and the MCP API answers (401 = it is up, a key is needed) on the target
        private static async Task<bool> WaitForTerminalAsync(Func<string, Task<string>> run, VpsInstance instance, CancellationToken cancel)
        {
            string command =
                $"for i in $(seq 1 60); do s=$(systemctl is-active {instance.Service}); " +
                $"c=$(curl -s -o /dev/null -w '%{{http_code}}' -X POST -H 'Content-Type: application/json' -d '{{}}' http://127.0.0.1:{instance.Port}/api/v2/mcp); " +
                "if [ \"$s\" = active ] && { [ \"$c\" = 401 ] || [ \"$c\" = 200 ]; }; then echo UP; exit 0; fi; sleep 2; done; echo DOWN";

            string answer = await run(command).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            return answer.Contains("UP");
        }
    }
}
