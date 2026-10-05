// Several independent OsEngine terminals on one VPS: discovery and control over SSH.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // One terminal = one systemd service with its own folder, MCP port and MCP key:
    //   main   -> service osengine,        /opt/osengine,        port 6500 (the original single-terminal layout)
    //   <name> -> service osengine-<name>, /opt/osengine-<name>, port 6501, 6502, ...
    internal sealed class VpsInstance
    {
        // the stable technical name (service osengine / osengine-<name>); never changes, the folders, ports and the
        // names of the AI-agent connections (.mcp.json) are built from it
        public string Name { get; set; }

        // the name shown to people (OsEngineVPS tabs, settings); kept on the VPS in /etc/osengine/names.conf so that every
        // computer sees the same. Empty = the technical name
        public string DisplayName { get; set; }

        public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
        public string Service { get; set; }
        public int Port { get; set; }
        public string DataRoot { get; set; }
        public string KeyFile { get; set; }
        public string State { get; set; }
        public long MemoryBytes { get; set; }

        // first 8 chars of the SHA-256 of the installed build package ("" = installed before versioning)
        public string Build { get; set; }

        // total CPU time used by the service (systemd CPUUsageNSec); the load is the difference between two readings
        public long CpuNanoseconds { get; set; }

        public bool IsMain => string.Equals(Name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase);
        public bool IsActive => State == "active";
        public string BaseFolder => VpsInstances.BaseFolderFor(Name);
        public string MemoryText => MemoryBytes > 0 ? (MemoryBytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " MB" : "—";
    }

    internal static class VpsInstances
    {
        public const int MainPort = 6500;

        public static string ServiceFor(string name) =>
            string.Equals(name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase) ? "osengine" : "osengine-" + name;

        public static string BaseFolderFor(string name) => "/opt/" + ServiceFor(name);

        // short, file-name and service-name safe
        public static bool IsValidName(string name) =>
            !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,19}$")
            && !string.Equals(name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase)
            && name != "removed";

        // Reads every /etc/systemd/system/osengine*.service: the terminal's data root, MCP port and key file come
        // from its ExecStart line, so the hand-written unit of the first VPS is recognized as well.
        public static async Task<List<VpsInstance>> ListAsync(Func<string, Task<string>> run)
        {
            const string script =
                "for f in /etc/systemd/system/osengine*.service; do " +
                "[ -f \"$f\" ] || continue; " +
                "svc=$(basename \"$f\" .service); " +
                "ex=$(grep '^ExecStart=' \"$f\"); " +
                "root=$(echo \"$ex\" | sed -n 's/.*--root \\([^ ]*\\).*/\\1/p'); " +
                "port=$(echo \"$ex\" | sed -n 's/.*--mcp-port \\([0-9]*\\).*/\\1/p'); " +
                "key=$(echo \"$ex\" | sed -n 's/.*--mcp-key-file \\([^ ]*\\).*/\\1/p'); " +
                "state=$(systemctl is-active \"$svc\"); " +
                "mem=$(systemctl show \"$svc\" -p MemoryCurrent --value); " +
                "ver=$(cut -c1-8 \"$(dirname \"$root\")/app/.package-sha256\" 2>/dev/null); " +
                "cpu=$(systemctl show \"$svc\" -p CPUUsageNSec --value); " +
                "echo \"$svc|$port|$root|$key|$state|$mem|$ver|$cpu\"; " +
                "done; [ -f /etc/osengine/names.conf ] && sed 's/^/NAMES|/' /etc/osengine/names.conf; true";

            string output = await run(script).ConfigureAwait(false);
            List<VpsInstance> result = new List<VpsInstance>();
            List<string> displayNames = new List<string>();

            foreach (string line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                if (line.StartsWith("NAMES|", StringComparison.Ordinal))
                {
                    displayNames.Add(line.Substring("NAMES|".Length));
                    continue;
                }

                string[] parts = line.Split('|');
                if (parts.Length < 6 || !int.TryParse(parts[1], out int port)) continue;

                string service = parts[0];
                string name = service == "osengine" ? VpsRemoteSession.MainInstance
                    : service.StartsWith("osengine-", StringComparison.Ordinal) ? service.Substring("osengine-".Length) : null;
                if (name == null) continue;

                result.Add(new VpsInstance
                {
                    Name = name,
                    Service = service,
                    Port = port,
                    DataRoot = parts[2],
                    KeyFile = parts[3],
                    State = parts[4],
                    MemoryBytes = long.TryParse(parts[5], out long memory) ? memory : 0,
                    Build = parts.Length > 6 ? parts[6] : "",
                    CpuNanoseconds = parts.Length > 7 && long.TryParse(parts[7], out long cpu) ? cpu : 0
                });
            }

            foreach (VpsInstance instance in result)
            {
                string line = displayNames.FirstOrDefault(n => n.StartsWith(instance.Service + "=", StringComparison.Ordinal));
                instance.DisplayName = line == null ? null : line.Substring(instance.Service.Length + 1).Trim();
            }

            return result.OrderBy(i => i.IsMain ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // what a person may call a terminal: any letters (also Cyrillic), digits, spaces, '-', '_' ...; the file format of
        // names.conf (one "service=name" line) forbids '=', '|' and line breaks
        public static bool IsValidDisplayName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 30 && name.IndexOfAny(new[] { '=', '|', '\r', '\n', '\0', '\t' }) < 0;

        // Writes the display name of one terminal into /etc/osengine/names.conf (the names of all the others are kept).
        // Only the shown name changes: the service, the folder, the MCP port and the connection names of the AI agents stay.
        public static async Task RenameAsync(Func<string, Task<string>> run, IReadOnlyList<VpsInstance> instances, VpsInstance target, string displayName)
        {
            string title = (displayName ?? string.Empty).Trim();

            if (!IsValidDisplayName(title))
            {
                throw new InvalidOperationException("The name must be 1-30 characters, without '=', '|' and line breaks");
            }

            if (instances.Any(i => !ReferenceEquals(i, target)
                && (string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Name, title, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException("Another terminal already has this name");
            }

            System.Text.StringBuilder text = new System.Text.StringBuilder();

            foreach (VpsInstance instance in instances)
            {
                string value = ReferenceEquals(instance, target) ? title : instance.Title;

                if (!string.Equals(value, instance.Name, StringComparison.OrdinalIgnoreCase))
                {
                    text.Append(instance.Service).Append('=').Append(value).Append('\n');
                }
            }

            // base64: the name may hold quotes, spaces or Cyrillic letters
            string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
            await run("mkdir -p /etc/osengine && echo " + encoded + " | base64 -d > /etc/osengine/names.conf.tmp && mv /etc/osengine/names.conf.tmp /etc/osengine/names.conf && chmod 644 /etc/osengine/names.conf").ConfigureAwait(false);

            target.DisplayName = string.Equals(title, target.Name, StringComparison.OrdinalIgnoreCase) ? null : title;
        }

        public static int NextFreePort(IEnumerable<VpsInstance> existing)
        {
            int port = MainPort + 1;
            HashSet<int> used = new HashSet<int>(existing.Select(i => i.Port));
            while (used.Contains(port)) port++;
            return port;
        }

        public static async Task<string> ReadKeyAsync(Func<string, Task<string>> run, VpsInstance instance)
        {
            return (await run("cat " + Quote(instance.KeyFile)).ConfigureAwait(false)).Trim();
        }

        public static Task StartAsync(Func<string, Task<string>> run, VpsInstance instance) =>
            run("systemctl start " + instance.Service);

        public static Task StopAsync(Func<string, Task<string>> run, VpsInstance instance) =>
            run("systemctl stop " + instance.Service);

        // stop may hang on a stuck process — systemd kills it after TimeoutStopSec (60 s) anyway
        public static Task RestartAsync(Func<string, Task<string>> run, VpsInstance instance) =>
            run("systemctl restart " + instance.Service);

        // Removes the service. The data (robots, settings, journals, MCP key) is not deleted but moved to
        // /opt/osengine-removed/<name>-<time> — only the build (app) is dropped, it can be reinstalled any time.
        public static Task<string> RemoveAsync(Func<string, Task<string>> run, VpsInstance instance)
        {
            if (instance.IsMain) throw new InvalidOperationException("The main terminal cannot be removed");

            string trash = $"/opt/osengine-removed/{instance.Name}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
            string script =
                "set -e; " +
                $"systemctl stop {instance.Service} || true; " +
                $"systemctl disable {instance.Service} >/dev/null 2>&1 || true; " +
                $"rm -f /etc/systemd/system/{instance.Service}.service; " +
                "systemctl daemon-reload; " +
                $"mkdir -p /opt/osengine-removed; " +
                $"rm -rf {Quote(instance.BaseFolder + "/app")}; " +
                $"mv {Quote(instance.BaseFolder)} {Quote(trash)}; " +
                $"echo {Quote(trash)}";

            return run(script);
        }

        private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    }
}
