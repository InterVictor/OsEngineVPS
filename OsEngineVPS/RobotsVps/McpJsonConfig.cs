/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Keeps the VPS terminals in the project MCP config of AI agents (.mcp.json, read by Claude Code and other MCP clients): every terminal gets an entry
    /// "osengine-server" (main) / "osengine-server-<name>" pointing at its local end of the SSH tunnel, with its key.
    /// Other entries of the file are left as they are.
    /// Держит терминалы VPS в конфиге MCP ИИ-агентов (.mcp.json): у каждого терминала своя запись с портом
    /// туннеля и ключом. Остальные записи файла не трогаются.
    /// </summary>
    internal static class McpJsonConfig
    {
        private const string MainServerName = "osengine-server";

        // The name of the connection of a terminal key (see VpsRemoteSession.Key). The first VPS keeps the names of the
        // single-VPS versions: osengine-server (main), osengine-server-<terminal>. The other VPS: osengine-server-v<id>-<terminal>
        // (the id, not the name of the VPS: renaming a VPS does not change what the agents are connected to).
        public static string ServerName(string key)
        {
            VpsRemoteSession.SplitKey(key, out string vpsId, out string terminal);
            string prefix = vpsId == VpsProfiles.FirstId ? MainServerName : MainServerName + "-" + VpsProfiles.Slug(vpsId);

            return string.Equals(terminal, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase) && vpsId == VpsProfiles.FirstId
                ? MainServerName
                : prefix + "-" + terminal;
        }

        // the reverse of ServerName: the terminal key of a connection name of ours
        private static string KeyOf(string serverName)
        {
            if (string.Equals(serverName, MainServerName, StringComparison.OrdinalIgnoreCase)) return VpsRemoteSession.MainInstance;

            string rest = serverName.Substring(MainServerName.Length + 1);
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(rest, "^v(\\d+)-(.+)$");

            return match.Success && match.Groups[1].Value != VpsProfiles.FirstId
                ? VpsRemoteSession.Key(match.Groups[1].Value, match.Groups[2].Value)
                : rest;
        }

        public static string FileFor(string folder) => Path.Combine(folder, ".mcp.json");

        /// <summary>Terminal names (main, binance, ...) that the file of <paramref name="folder"/> gives to its MCP clients.</summary>
        public static List<string> ListTerminals(string folder)
        {
            JsonObject servers = ReadServers(FileFor(folder), out _);

            return servers.Select(s => s.Key)
                .Where(IsOurs)
                .Select(KeyOf)
                .ToList();
        }

        /// <summary>Removes all VPS terminal entries from the file of <paramref name="folder"/>; other entries stay.</summary>
        public static void RemoveAll(string folder)
        {
            string path = FileFor(folder);
            JsonObject servers = ReadServers(path, out JsonObject root);
            List<string> ours = servers.Select(s => s.Key).Where(IsOurs).ToList();

            if (ours.Count == 0)
            {
                return;
            }

            foreach (string name in ours) servers.Remove(name);
            Write(path, root);
        }

        /// <summary>Removes the entries of one VPS (the terminals of the VPS with this id) from the file of <paramref name="folder"/>; the others stay.</summary>
        public static List<string> RemoveVps(string folder, string vpsId)
        {
            string path = FileFor(folder);
            JsonObject servers = ReadServers(path, out JsonObject root);
            List<string> mine = servers.Select(s => s.Key).Where(IsOurs).Where(n =>
            {
                VpsRemoteSession.SplitKey(KeyOf(n), out string id, out _);
                return id == vpsId;
            }).ToList();

            foreach (string name in mine) servers.Remove(name);
            if (mine.Count > 0) Write(path, root);
            return mine;
        }

        private static bool IsOurs(string name) =>
            string.Equals(name, MainServerName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(MainServerName + "-", StringComparison.OrdinalIgnoreCase);

        private static JsonObject ReadServers(string path, out JsonObject root)
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                : new JsonObject();

            if (root == null)
            {
                throw new InvalidDataException("not a JSON object");
            }

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            return servers;
        }

        private static void Write(string path, JsonObject root)
        {
            // relaxed escaping: keys stay readable ("+" is not escaped)
            string json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = "\r\n",
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            string temp = path + ".tmp";
            File.WriteAllText(temp, json + "\r\n", new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        /// <summary>
        /// Writes <paramref name="connected"/> (terminal key → local port and key) into the file of
        /// <paramref name="folder"/> and removes the terminals that no longer exist on their VPS (not in
        /// <paramref name="existing"/>); only the VPS in <paramref name="knownVps"/> (those whose terminals are known at the
        /// moment) are checked, so a VPS that is not connected keeps its entries. A stopped terminal stays.
        /// Returns what changed, empty if nothing did.
        /// </summary>
        public static List<string> Update(string folder, IReadOnlyDictionary<string, (int LocalPort, string Key)> connected,
            IEnumerable<string> existing, ISet<string> knownVps)
        {
            string path = FileFor(folder);
            JsonObject servers = ReadServers(path, out JsonObject root);
            List<string> changes = new List<string>();

            foreach (KeyValuePair<string, (int LocalPort, string Key)> terminal in connected)
            {
                string name = ServerName(terminal.Key);
                JsonObject entry = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = $"http://localhost:{terminal.Value.LocalPort}/api/v2/mcp",
                    ["headers"] = new JsonObject { ["X-Api-Key"] = terminal.Value.Key }
                };

                if (servers[name] is JsonObject old && JsonNode.DeepEquals(old, entry))
                {
                    continue;
                }

                changes.Add(terminal.Key + (servers.ContainsKey(name) ? " updated" : " added"));
                servers[name] = entry;
            }

            HashSet<string> keep = new HashSet<string>(existing.Select(ServerName), StringComparer.OrdinalIgnoreCase);

            foreach (string name in servers.Select(s => s.Key)
                .Where(n => n.StartsWith(MainServerName + "-", StringComparison.OrdinalIgnoreCase) && !keep.Contains(n)).ToList())
            {
                VpsRemoteSession.SplitKey(KeyOf(name), out string vpsId, out _);

                if (!knownVps.Contains(vpsId))
                {
                    continue; // that VPS is not connected now: nothing is known about its terminals
                }

                servers.Remove(name);
                changes.Add(name.Substring(MainServerName.Length + 1) + " removed");
            }

            if (changes.Count > 0)
            {
                Write(path, root);
            }

            return changes;
        }
    }
}
