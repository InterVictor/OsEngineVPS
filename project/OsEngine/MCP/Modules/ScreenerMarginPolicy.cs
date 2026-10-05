// Screener margin policy (OsEngineVPS fork): margin mode and leverage that every NEW security of a screener gets on the exchange.
// A screener (BotTabScreener) adds securities by itself; a new symbol of an exchange often comes with isolated margin and a high
// leverage, and a position on it is then liquidated by the exchange on a small price move. The policy is switched on per screener
// from the "Data settings" window; the terminal then sets the mode and the leverage of each security it has not handled yet
// (AServer.SetMarginInfo, so the connector's own block switch is respected).
//
// The policy does not touch the securities that are already in the screener when it is switched on (the window has a button
// for them), writes nothing to the exchange for a connector that cannot do it, and gives up on a security after 3 attempts.
// Stored in Engine/VpsScreenerMarginPolicy.json, checked once a minute.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.OsTrader;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Tab;

namespace OsEngine.MCP.Modules
{
    public static class ScreenerMarginPolicy
    {
        private const int CycleSeconds = 60;
        private const int MaxSecuritiesPerCycle = 5;
        private const int MaxAttempts = 3;

        private static readonly object Locker = new object();
        private static Dictionary<string, PolicyItem> _items;
        private static Thread _thread;

        public sealed class PolicyItem
        {
            public string BotId { get; set; }
            public string TabName { get; set; }
            public bool Enabled { get; set; }
            public bool Isolated { get; set; }
            public int Leverage { get; set; } = 3;
            public List<string> Known { get; set; } = new List<string>();
            public Dictionary<string, int> Attempts { get; set; } = new Dictionary<string, int>();
            public string Status { get; set; } = string.Empty;
        }

        private static string FilePath => Path.Combine("Engine", "VpsScreenerMarginPolicy.json");

        private static string Key(string botId, string tabName) => botId + "|" + tabName;

        public static void Start()
        {
            lock (Locker)
            {
                if (_thread != null)
                {
                    return;
                }

                _thread = new Thread(Worker) { IsBackground = true, Name = "ScreenerMarginPolicy" };
                _thread.Start();
            }
        }

        // ---- API for the MCP tools ----

        public static PolicyItem Get(string botId, string tabName)
        {
            lock (Locker)
            {
                Load();
                return _items.TryGetValue(Key(botId, tabName), out PolicyItem item) ? Copy(item) : new PolicyItem { BotId = botId, TabName = tabName };
            }
        }

        // markCurrentAsKnown: the securities that are in the screener now are not touched by the policy
        public static PolicyItem Set(string botId, string tabName, bool enabled, bool isolated, int leverage, bool markCurrentAsKnown, IEnumerable<string> currentSecurities)
        {
            if (leverage < 1)
            {
                throw new ArgumentException("leverage must be a whole number, 1 or more");
            }

            lock (Locker)
            {
                Load();
                string key = Key(botId, tabName);

                if (!_items.TryGetValue(key, out PolicyItem item))
                {
                    item = new PolicyItem { BotId = botId, TabName = tabName };
                    _items[key] = item;
                }

                bool wasEnabled = item.Enabled;
                item.Enabled = enabled;
                item.Isolated = isolated;
                item.Leverage = leverage;

                if (enabled && (markCurrentAsKnown || !wasEnabled) && currentSecurities != null)
                {
                    foreach (string name in currentSecurities)
                    {
                        if (!string.IsNullOrEmpty(name) && !item.Known.Contains(name))
                        {
                            item.Known.Add(name);
                        }
                    }
                }

                item.Status = enabled ? "On: new securities get " + (isolated ? "Isolated" : "Cross") + ", " + leverage + "x" : "Off";
                Save();
                return Copy(item);
            }
        }

        // ---- worker ----

        private static void Worker()
        {
            Thread.Sleep(TimeSpan.FromSeconds(45));

            while (true)
            {
                try
                {
                    Cycle();
                }
                catch (Exception ex)
                {
                    ServerMaster.SendNewLogMessage("Screener margin policy: " + ex.Message, LogMessageType.Error);
                }

                Thread.Sleep(TimeSpan.FromSeconds(CycleSeconds));
            }
        }

        private static void Cycle()
        {
            OsTraderMaster master = OsTraderMaster.Master;

            if (master == null || master.PanelsArray == null)
            {
                return;
            }

            List<PolicyItem> active;

            lock (Locker)
            {
                Load();
                active = _items.Values.Where(i => i.Enabled).Select(Copy).ToList();
            }

            if (active.Count == 0)
            {
                return;
            }

            foreach (BotPanel bot in master.PanelsArray.ToList())
            {
                if (bot?.TabsScreener == null)
                {
                    continue;
                }

                foreach (BotTabScreener screener in bot.TabsScreener.ToList())
                {
                    PolicyItem policy = active.FirstOrDefault(p => p.BotId == bot.NameStrategyUniq && p.TabName == screener.TabName);

                    if (policy != null)
                    {
                        Apply(policy, screener);
                    }
                }
            }
        }

        private static void Apply(PolicyItem policy, BotTabScreener screener)
        {
            List<BotTabSimple> tabs = screener.Tabs?.ToList() ?? new List<BotTabSimple>();
            int handled = 0;

            foreach (BotTabSimple tab in tabs)
            {
                string name = tab?.Connector?.SecurityName;
                AServer server = tab?.Connector?.MyServer as AServer;

                if (string.IsNullOrEmpty(name) || server == null || server.ServerStatus != ServerConnectStatus.Connect)
                {
                    continue;
                }

                lock (Locker)
                {
                    if (policy.Known.Contains(name))
                    {
                        continue;
                    }
                }

                if (handled >= MaxSecuritiesPerCycle)
                {
                    break;
                }

                handled++;
                bool ok = server.SetMarginInfo(name, policy.Isolated, policy.Leverage, out string message);

                lock (Locker)
                {
                    PolicyItem item = _items[Key(policy.BotId, policy.TabName)];

                    if (ok)
                    {
                        item.Known.Add(name);
                        item.Status = "New security " + name + ": " + message;
                        ServerMaster.SendNewLogMessage("Screener " + screener.TabName + ", margin policy: " + message, LogMessageType.System);
                    }
                    else if (message != null && (message.Contains("blocked") || message.Contains("cannot change") || message.Contains("not connected")))
                    {
                        // the connector does not allow it at all (or is not ready): nothing to count, say it once
                        string status = "Waiting: " + message;

                        if (item.Status != status)
                        {
                            item.Status = status;
                            ServerMaster.SendNewLogMessage("Screener " + screener.TabName + ", margin policy is not applied to new securities: " + message, LogMessageType.System);
                        }

                        Save();
                        return;
                    }
                    else
                    {
                        item.Attempts.TryGetValue(name, out int attempts);
                        attempts++;
                        item.Attempts[name] = attempts;

                        if (attempts >= MaxAttempts)
                        {
                            item.Known.Add(name);
                            item.Status = "Not set for " + name + " after " + attempts + " attempts: " + message;
                            ServerMaster.SendNewLogMessage("Screener " + screener.TabName + ", margin policy gave up on " + name + ": " + message, LogMessageType.Error);
                        }
                        else
                        {
                            item.Status = "Retrying " + name + " (" + attempts + "/" + MaxAttempts + "): " + message;
                        }
                    }

                    Save();
                }

                Thread.Sleep(300);
            }
        }

        // ---- storage ----

        private static void Load()
        {
            if (_items != null)
            {
                return;
            }

            _items = new Dictionary<string, PolicyItem>();

            try
            {
                if (File.Exists(FilePath))
                {
                    List<PolicyItem> list = JsonSerializer.Deserialize<List<PolicyItem>>(File.ReadAllText(FilePath));

                    foreach (PolicyItem item in list ?? new List<PolicyItem>())
                    {
                        _items[Key(item.BotId, item.TabName)] = item;
                    }
                }
            }
            catch (Exception ex)
            {
                ServerMaster.SendNewLogMessage("Screener margin policy: the settings file is not readable, " + ex.Message, LogMessageType.Error);
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory("Engine");
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_items.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                ServerMaster.SendNewLogMessage("Screener margin policy: could not save, " + ex.Message, LogMessageType.Error);
            }
        }

        private static PolicyItem Copy(PolicyItem item)
        {
            return new PolicyItem
            {
                BotId = item.BotId,
                TabName = item.TabName,
                Enabled = item.Enabled,
                Isolated = item.Isolated,
                Leverage = item.Leverage,
                Known = item.Known.ToList(),
                Attempts = new Dictionary<string, int>(item.Attempts),
                Status = item.Status
            };
        }
    }
}
