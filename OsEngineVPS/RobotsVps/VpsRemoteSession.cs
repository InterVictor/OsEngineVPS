using System;
using System.Collections.Generic;
using System.Linq;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Shares the remote MCP sessions with the independent Robots.VPS workspaces. This computer may be connected to several VPS,
    /// and one VPS can run several independent OsEngine terminals (systemd services osengine, osengine-&lt;name&gt;). The connection
    /// panel of every VPS publishes here its MCP clients (one per terminal) and its SSH access; each Robots.VPS workspace is bound
    /// to one terminal by its KEY: the plain terminal name for the first VPS (as in the single-VPS versions, so saved layouts and
    /// names stay valid), "&lt;VPS id&gt;/&lt;terminal name&gt;" for the others.
    /// </summary>
    public static class VpsRemoteSession
    {
        /// <summary>the terminal of the original single-terminal layout (/opt/osengine, service "osengine")</summary>
        public const string MainInstance = "main";

        private sealed class VpsEntry
        {
            public Dictionary<string, RemoteMcpClient> Clients = new Dictionary<string, RemoteMcpClient>(StringComparer.OrdinalIgnoreCase);
            public VpsSshCredentials Ssh;
            public Func<string, System.Threading.Tasks.Task<string>> Run;
            public IReadOnlyList<VpsInstance> Instances = new List<VpsInstance>();
            public Dictionary<string, (int LocalPort, string Key)> McpEntries = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly object Locker = new object();
        private static readonly Dictionary<string, VpsEntry> Vps = new Dictionary<string, VpsEntry>();

        /// <summary>raised (on any thread) whenever the set of terminals, their clients or their shown names change</summary>
        public static event Action InstancesChanged;

        // ---- keys ----

        /// <summary>the key of a terminal: its plain name for the first VPS, "id/name" for the others</summary>
        public static string Key(string vpsId, string terminalName) =>
            vpsId == VpsProfiles.FirstId ? terminalName : vpsId + "/" + terminalName;

        public static void SplitKey(string key, out string vpsId, out string terminalName)
        {
            int slash = key == null ? -1 : key.IndexOf('/');

            if (slash < 0)
            {
                vpsId = VpsProfiles.FirstId;
                terminalName = key;
            }
            else
            {
                vpsId = key.Substring(0, slash);
                terminalName = key.Substring(slash + 1);
            }
        }

        // ---- terminals ----

        /// <summary>keys of the connected terminals: the VPS in the order of the list, the main terminal of each first</summary>
        public static IReadOnlyList<string> InstanceNames
        {
            get
            {
                List<string> order = VpsProfiles.All.Select(p => p.Id).ToList();

                lock (Locker)
                {
                    return Vps.OrderBy(v => order.IndexOf(v.Key) < 0 ? int.MaxValue : order.IndexOf(v.Key))
                        .SelectMany(v => v.Value.Clients.Keys
                            .OrderBy(n => string.Equals(n, MainInstance, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .Select(n => Key(v.Key, n)))
                        .ToList();
                }
            }
        }

        /// <summary>the terminal key of a client from GetClient, or null</summary>
        public static string GetInstanceName(RemoteMcpClient client)
        {
            lock (Locker)
            {
                foreach (KeyValuePair<string, VpsEntry> vps in Vps)
                {
                    string name = vps.Value.Clients.FirstOrDefault(c => ReferenceEquals(c.Value, client)).Key;
                    if (name != null) return Key(vps.Key, name);
                }

                return null;
            }
        }

        public static RemoteMcpClient GetClient(string key)
        {
            if (key == null) return null;
            SplitKey(key, out string vpsId, out string name);

            lock (Locker)
            {
                return Vps.TryGetValue(vpsId, out VpsEntry entry) && entry.Clients.TryGetValue(name, out RemoteMcpClient client) ? client : null;
            }
        }

        /// <summary>replaces the MCP clients of one VPS (keys: plain terminal names); null = the VPS is disconnected</summary>
        public static void SetClients(string vpsId, IDictionary<string, RemoteMcpClient> clients)
        {
            lock (Locker)
            {
                VpsEntry entry = Entry(vpsId);
                entry.Clients = clients == null
                    ? new Dictionary<string, RemoteMcpClient>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, RemoteMcpClient>(clients, StringComparer.OrdinalIgnoreCase);
            }

            InstancesChanged?.Invoke();
        }

        // ---- SSH access of the VPS (lets other windows reach the terminals' files) ----

        internal static void SetSsh(string vpsId, VpsSshCredentials credentials, Func<string, System.Threading.Tasks.Task<string>> run, IReadOnlyList<VpsInstance> instances)
        {
            string before;
            string after;

            lock (Locker)
            {
                VpsEntry entry = Entry(vpsId);
                before = Fingerprint(entry.Instances);
                entry.Ssh = credentials;
                entry.Run = run;
                entry.Instances = instances ?? new List<VpsInstance>();
                after = Fingerprint(entry.Instances);
            }

            // a terminal was renamed (here or on another computer): the tabs must show the new name
            if (before != after)
            {
                InstancesChanged?.Invoke();
            }
        }

        private static string Fingerprint(IReadOnlyList<VpsInstance> instances) => string.Join("|", instances.Select(i => i.Name + "=" + i.Title));

        /// <summary>the SSH access and the terminals of a VPS by its id; null if the VPS is not connected over SSH of this program</summary>
        internal static (VpsSshCredentials Ssh, Func<string, System.Threading.Tasks.Task<string>> Run, IReadOnlyList<VpsInstance> Instances)? SshOfVps(string vpsId)
        {
            lock (Locker)
            {
                return Vps.TryGetValue(vpsId, out VpsEntry entry) && entry.Ssh != null && entry.Run != null
                    ? (entry.Ssh, entry.Run, entry.Instances)
                    : ((VpsSshCredentials, Func<string, System.Threading.Tasks.Task<string>>, IReadOnlyList<VpsInstance>)?)null;
            }
        }

        internal static VpsSshCredentials SshCredentialsFor(string key) => EntryOf(key)?.Ssh;
        internal static Func<string, System.Threading.Tasks.Task<string>> SshRunFor(string key) => EntryOf(key)?.Run;
        internal static IReadOnlyList<VpsInstance> InstancesFor(string key) => EntryOf(key)?.Instances ?? new List<VpsInstance>();

        /// <summary>the terminal behind a key (null if the VPS is not connected by SSH)</summary>
        internal static VpsInstance InstanceOf(string key)
        {
            SplitKey(key, out _, out string name);
            return InstancesFor(key).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static VpsEntry EntryOf(string key)
        {
            if (key == null) return null;
            SplitKey(key, out string vpsId, out _);

            lock (Locker)
            {
                return Vps.TryGetValue(vpsId, out VpsEntry entry) ? entry : null;
            }
        }

        private static VpsEntry Entry(string vpsId)
        {
            if (!Vps.TryGetValue(vpsId, out VpsEntry entry))
            {
                entry = new VpsEntry();
                Vps[vpsId] = entry;
            }

            return entry;
        }

        // ---- names ----

        /// <summary>
        /// The name a person sees: the terminal's shown name (its technical name until somebody renames it) and, when this
        /// computer has several VPS, the name of the VPS before it ("provider · terminal").
        /// </summary>
        internal static string TitleOf(string key)
        {
            SplitKey(key, out string vpsId, out string name);
            VpsInstance instance = InstancesFor(key).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            string title = instance?.Title ?? name;
            return VpsProfiles.All.Count > 1 ? VpsProfiles.NameOf(vpsId) + " · " + title : title;
        }

        internal static void RaiseInstancesChanged() => InstancesChanged?.Invoke();

        // ---- connections of AI agents (.mcp.json) ----

        /// <summary>the local end of the SSH tunnel and the key of every connected terminal of a VPS (keys: plain terminal names)</summary>
        internal static void SetMcpEntries(string vpsId, IReadOnlyDictionary<string, (int LocalPort, string Key)> entries)
        {
            lock (Locker)
            {
                Entry(vpsId).McpEntries = entries == null
                    ? new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, (int, string)>(entries, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>every connected terminal of every VPS with its port and key, and the keys of all the terminals that exist</summary>
        internal static (Dictionary<string, (int LocalPort, string Key)> Connected, List<string> Existing, HashSet<string> KnownVps) AllMcpEntries()
        {
            Dictionary<string, (int LocalPort, string Key)> connected = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
            List<string> existing = new List<string>();
            HashSet<string> known = new HashSet<string>();

            lock (Locker)
            {
                foreach (KeyValuePair<string, VpsEntry> vps in Vps)
                {
                    foreach (KeyValuePair<string, (int LocalPort, string Key)> entry in vps.Value.McpEntries)
                    {
                        connected[Key(vps.Key, entry.Key)] = entry.Value;
                    }

                    existing.AddRange(vps.Value.Instances.Select(i => Key(vps.Key, i.Name)));
                    if (vps.Value.Instances.Count > 0) known.Add(vps.Key);
                }
            }

            return (connected, existing, known);
        }
    }
}
