// The list of VPS this computer is connected to: a name for every VPS (changed by the user, e.g. the provider's name) and the
// files of its settings. Kept on this computer only (Engine\VpsProfiles.txt, one "id|name" per line); nothing is written to a server.
// The first VPS (id "1") keeps the settings file of the single-VPS versions (Engine\RobotsVpsSettings.txt), so nothing has to be migrated.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    internal sealed class VpsProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    internal static class VpsProfiles
    {
        public const string FirstId = "1";
        private const string ListFile = @"Engine\VpsProfiles.txt";
        private const string CommonFile = @"Engine\VpsCommonSettings.txt";
        private const string LegacySettingsFile = @"Engine\RobotsVpsSettings.txt";
        private static readonly object Locker = new object();
        private static List<VpsProfile> _items;

        /// <summary>raised (on any thread) when a VPS is added, renamed or removed</summary>
        public static event Action Changed;

        public static IReadOnlyList<VpsProfile> All
        {
            get
            {
                lock (Locker)
                {
                    Load();
                    return _items.Select(i => new VpsProfile { Id = i.Id, Name = i.Name }).ToList();
                }
            }
        }

        public static string NameOf(string id)
        {
            lock (Locker)
            {
                Load();
                return _items.FirstOrDefault(i => i.Id == id)?.Name ?? ("VPS " + id);
            }
        }

        /// <summary>the settings file of a VPS (the first one keeps the old file name)</summary>
        public static string SettingsFileFor(string id) =>
            id == FirstId ? LegacySettingsFile : @"Engine\RobotsVpsSettings_" + id + ".txt";

        /// <summary>a stable ASCII part of the connection names for AI agents (.mcp.json): v2, v3...; the first VPS has none</summary>
        public static string Slug(string id) => "v" + id;

        // what a person may call a VPS: 1-30 characters, no '|' and no line breaks (the file format)
        public static bool IsValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 30 && name.IndexOfAny(new[] { '|', '\r', '\n', '\0', '\t' }) < 0;

        public static string CheckName(string name, string exceptId)
        {
            if (!IsValidName(name)) return "The name must be 1-30 characters, without '|' and line breaks";

            lock (Locker)
            {
                Load();
                if (_items.Any(i => i.Id != exceptId && string.Equals(i.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return "Another VPS already has this name";
            }

            return null;
        }

        public static VpsProfile Add(string name)
        {
            string error = CheckName(name, null);
            if (error != null) throw new InvalidOperationException(error);

            VpsProfile profile;

            lock (Locker)
            {
                Load();
                int id = _items.Select(i => int.TryParse(i.Id, out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
                profile = new VpsProfile { Id = id.ToString(), Name = name.Trim() };
                _items.Add(profile);
                Save();
            }

            Changed?.Invoke();
            return new VpsProfile { Id = profile.Id, Name = profile.Name };
        }

        public static void Rename(string id, string name)
        {
            string error = CheckName(name, id);
            if (error != null) throw new InvalidOperationException(error);

            lock (Locker)
            {
                Load();
                VpsProfile profile = _items.FirstOrDefault(i => i.Id == id);
                if (profile == null) throw new InvalidOperationException("The VPS is not in the list");
                profile.Name = name.Trim();
                Save();
            }

            Changed?.Invoke();
        }

        /// <summary>Removes the VPS from this computer's list and deletes its settings file (address, encrypted password and key). The server is not touched.</summary>
        public static void Remove(string id)
        {
            lock (Locker)
            {
                Load();
                if (_items.Count <= 1) throw new InvalidOperationException("The last VPS cannot be removed");
                _items.RemoveAll(i => i.Id == id);
                Save();

                try { if (File.Exists(SettingsFileFor(id))) File.Delete(SettingsFileFor(id)); } catch { /* the entry is gone, a leftover file is harmless */ }
            }

            Changed?.Invoke();
        }

        /// <summary>the first local port base (6510, 6610, 6710 ...) that no VPS of the list uses yet; a terminal takes base + (its VPS port - 6500)</summary>
        public static int NextLocalPortBase()
        {
            HashSet<int> used = new HashSet<int>();

            lock (Locker)
            {
                Load();

                foreach (VpsProfile profile in _items)
                {
                    try
                    {
                        string file = SettingsFileFor(profile.Id);
                        string[] lines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();
                        if (lines.Length > 5 && int.TryParse(lines[5], out int port)) used.Add(port);
                    }
                    catch { /* an unreadable file: its port is not counted */ }
                }
            }

            int candidate = 6510;
            while (used.Any(p => Math.Abs(p - candidate) < 100)) candidate += 100;
            return candidate;
        }

        // ---- settings common to all VPS: the folders that get .mcp.json ----

        public static List<string> LoadMcpFolders()
        {
            try
            {
                string text = null;

                if (File.Exists(CommonFile))
                {
                    string[] lines = File.ReadAllLines(CommonFile);
                    text = lines.Length > 0 ? lines[0] : "";
                }
                else if (File.Exists(LegacySettingsFile))
                {
                    // taken over from the single-VPS settings file (line 13)
                    string[] lines = File.ReadAllLines(LegacySettingsFile);
                    text = lines.Length > 12 ? lines[12] : "";
                }

                List<string> folders = (text ?? "").Split(';').Select(f => f.Trim()).Where(f => f.Length > 0)
                    .Select(f => f.EndsWith(".mcp.json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(f) : f)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // taken over from the single-VPS file: the common file is made now, the old line is not needed any more
                if (!File.Exists(CommonFile) && folders.Count > 0) SaveMcpFolders(folders);

                return folders;
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void SaveMcpFolders(IEnumerable<string> folders)
        {
            Directory.CreateDirectory("Engine");
            File.WriteAllLines(CommonFile, new[] { string.Join(";", folders) }, new UTF8Encoding(false));
        }

        // ---- storage ----

        private static void Load()
        {
            if (_items != null) return;

            _items = new List<VpsProfile>();

            try
            {
                if (File.Exists(ListFile))
                {
                    foreach (string line in File.ReadAllLines(ListFile, Encoding.UTF8))
                    {
                        int bar = line.IndexOf('|');
                        if (bar > 0 && line.Substring(0, bar).Trim().All(char.IsDigit))
                            _items.Add(new VpsProfile { Id = line.Substring(0, bar).Trim(), Name = line.Substring(bar + 1).Trim() });
                    }
                }
            }
            catch { /* an unreadable list starts again with the first VPS */ }

            if (_items.Count == 0)
            {
                _items.Add(new VpsProfile { Id = FirstId, Name = "VPS 1" });
            }
        }

        private static void Save()
        {
            Directory.CreateDirectory("Engine");
            File.WriteAllLines(ListFile, _items.Select(i => i.Id + "|" + i.Name), new UTF8Encoding(false));
        }
    }
}
