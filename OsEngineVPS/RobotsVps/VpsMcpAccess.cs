// "MCP access" for AI agents, common to all VPS: the folders whose .mcp.json gets a connection for every connected terminal
// of every VPS (the local end of its SSH tunnel and its key). Every VPS panel calls UpdateAll() after it reads its terminals.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    internal static class VpsMcpAccess
    {
        private static List<string> _folders;

        /// <summary>a line for the event log (on any thread)</summary>
        public static event Action<string> Logged;

        public static List<string> Folders
        {
            get
            {
                if (_folders == null) _folders = VpsProfiles.LoadMcpFolders();
                return _folders;
            }
        }

        public static void SetFolders(IEnumerable<string> folders)
        {
            _folders = folders.ToList();
            VpsProfiles.SaveMcpFolders(_folders);
        }

        /// <summary>saves the list after the dialog edited it in place</summary>
        public static void Save() => VpsProfiles.SaveMcpFolders(Folders);

        public static string Text() => Folders.Count == 0
            ? "No MCP access to the VPS terminals"
            : "MCP access in: " + string.Join(", ", Folders);

        /// <summary>a VPS was removed from the list: its connections go from the .mcp.json files</summary>
        public static void RemoveVps(string vpsId)
        {
            foreach (string folder in Folders.Where(Directory.Exists))
            {
                try
                {
                    List<string> removed = McpJsonConfig.RemoveVps(folder, vpsId);

                    if (removed.Count > 0)
                    {
                        Logged?.Invoke($"MCP access in {folder}: {string.Join(", ", removed)} removed (the VPS left the list)");
                    }
                }
                catch (Exception ex)
                {
                    Logged?.Invoke($"MCP access in {folder}: .mcp.json not updated: {ex.Message}");
                }
            }
        }

        public static void UpdateAll()
        {
            (Dictionary<string, (int LocalPort, string Key)> connected, List<string> existing, HashSet<string> known) = VpsRemoteSession.AllMcpEntries();

            foreach (string folder in Folders.Where(Directory.Exists))
            {
                try
                {
                    List<string> changes = McpJsonConfig.Update(folder, connected, existing, known);

                    if (changes.Count > 0)
                    {
                        Logged?.Invoke($"MCP access in {folder}: {string.Join(", ", changes)} (seen by new sessions there)");
                    }
                }
                catch (Exception ex)
                {
                    Logged?.Invoke($"MCP access in {folder}: .mcp.json not updated: {ex.Message}");
                }
            }
        }
    }
}
