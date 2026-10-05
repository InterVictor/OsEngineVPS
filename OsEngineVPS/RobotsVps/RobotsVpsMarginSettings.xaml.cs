// Margin mode and leverage on the exchange for the securities of one tab, used by the two "Data settings" windows
// (simple tab: its security, screener: the securities ticked in the window). Reads through server_instance_get_margin_info,
// writes through server_instance_set_margin_info; the server refuses while its parameter "Block margin and leverage changes"
// is on, and the refusal is shown here. Nothing is sent without the "Set on the exchange" button.
// Defaults of the fields: Cross and leverage 3.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    public partial class RobotsVpsMarginSettings : UserControl
    {
        private const int PackSize = 8;

        private RemoteMcpClient _client;
        private Func<(string Type, int Number)?> _serverProvider;
        private Func<List<string>> _securitiesProvider;
        private readonly DispatcherTimer _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private bool _busy;

        public RobotsVpsMarginSettings()
        {
            InitializeComponent();
            ComboBoxMode.Items.Add("Isolated");
            ComboBoxMode.Items.Add("Cross");
            ComboBoxMode.SelectedItem = "Cross";
            ButtonApply.Click += ButtonApply_Click;
            _refreshTimer.Tick += async (s, e) => { _refreshTimer.Stop(); await RefreshStateAsync(); };
        }

        public void Init(RemoteMcpClient client, Func<(string Type, int Number)?> serverProvider, Func<List<string>> securitiesProvider)
        {
            _client = client;
            _serverProvider = serverProvider;
            _securitiesProvider = securitiesProvider;
        }

        // the window calls it when the server or the securities change; many calls in a row give one request
        public void ScheduleRefresh()
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        public async Task RefreshStateAsync()
        {
            if (_busy || _client == null || !_client.IsConnected)
            {
                return;
            }

            (string Type, int Number)? server = _serverProvider?.Invoke();
            List<string> names = _securitiesProvider?.Invoke() ?? new List<string>();

            if (server == null || string.IsNullOrEmpty(server.Value.Type) || names.Count == 0)
            {
                TextBlockState.Text = "Margin settings: choose the server and the securities";
                TextBlockState.Foreground = Brushes.Gray;
                return;
            }

            try
            {
                int known = 0, isolated = 0, unknown = 0;
                decimal minLeverage = decimal.MaxValue, maxLeverage = 0;

                for (int start = 0; start < names.Count; start += 200)
                {
                    List<string> pack = names.Skip(start).Take(200).ToList();
                    JsonElement answer = await _client.CallToolAsync("server_instance_get_margin_info",
                        new { type = server.Value.Type, number = server.Value.Number, security_names = pack });

                    unknown += ReadInt(answer, "unknown_count");

                    if (answer.TryGetProperty("securities", out JsonElement securities) && securities.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in securities.EnumerateArray())
                        {
                            known++;

                            if (item.TryGetProperty("is_isolated", out JsonElement iso) && iso.ValueKind == JsonValueKind.True)
                            {
                                isolated++;
                            }

                            if (item.TryGetProperty("leverage", out JsonElement lev) && lev.TryGetDecimal(out decimal value))
                            {
                                minLeverage = Math.Min(minLeverage, value);
                                maxLeverage = Math.Max(maxLeverage, value);
                            }
                        }
                    }
                }

                if (known == 0)
                {
                    TextBlockState.Text = "On the exchange: no data for " + names.Count + " securities (the connector does not report it, or it is not received yet)";
                    TextBlockState.Foreground = Brushes.Gray;
                    return;
                }

                StringBuilder text = new StringBuilder("On the exchange (" + known + " of " + names.Count + " securities): ");
                text.Append(isolated == 0 ? "all Cross" : isolated == known ? "all Isolated" : "Isolated " + isolated + ", Cross " + (known - isolated));
                text.Append(", leverage ");
                text.Append(minLeverage == maxLeverage
                    ? minLeverage.ToString("0.##", CultureInfo.InvariantCulture) + "x"
                    : minLeverage.ToString("0.##", CultureInfo.InvariantCulture) + "-" + maxLeverage.ToString("0.##", CultureInfo.InvariantCulture) + "x");

                if (unknown > 0)
                {
                    text.Append("; no data for " + unknown);
                }

                TextBlockState.Text = text.ToString();
                TextBlockState.Foreground = isolated > 0 && maxLeverage >= 20 ? Brushes.OrangeRed : Brushes.Gray;
            }
            catch (Exception ex)
            {
                TextBlockState.Text = "Margin settings are not available: " + ex.Message;
                TextBlockState.Foreground = Brushes.Gray;
            }
        }

        private async void ButtonApply_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            (string Type, int Number)? server = _serverProvider?.Invoke();
            List<string> names = _securitiesProvider?.Invoke() ?? new List<string>();

            if (server == null || string.IsNullOrEmpty(server.Value.Type) || names.Count == 0)
            {
                System.Windows.MessageBox.Show("Choose the server and at least one security first.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string mode = ComboBoxMode.SelectedItem as string;

            if (mode == null || !int.TryParse(TextBoxLeverage.Text.Trim(), out int leverage) || leverage < 1)
            {
                System.Windows.MessageBox.Show("Choose Isolated or Cross and enter the leverage as a whole number, 1 or more.", "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string question = names.Count + " security(ies) on " + server.Value.Type + ": " + mode + ", leverage " + leverage + "x.\n\n"
                + "This is a setting of the exchange account: every terminal and manual trading on this account will see it. "
                + "The exchange does not change the margin mode of a security that has an open position or order.\n\nSend to the exchange?";

            if (System.Windows.MessageBox.Show(question, "VPS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _busy = true;
            ButtonApply.IsEnabled = false;
            int done = 0;
            List<string> failures = new List<string>();

            try
            {
                for (int start = 0; start < names.Count; start += PackSize)
                {
                    List<string> pack = names.Skip(start).Take(PackSize).ToList();
                    TextBlockState.Text = "Sending to the exchange: " + Math.Min(start + pack.Count, names.Count) + " of " + names.Count + "...";
                    TextBlockState.Foreground = Brushes.Gray;

                    JsonElement answer = await _client.CallToolAsync("server_instance_set_margin_info",
                        new { type = server.Value.Type, number = server.Value.Number, security_names = pack, margin_mode = mode, leverage });

                    done += ReadInt(answer, "done");
                    bool refusedAll = false;

                    if (answer.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in results.EnumerateArray())
                        {
                            if (item.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True)
                            {
                                continue;
                            }

                            string name = item.TryGetProperty("security_name", out JsonElement n) ? n.GetString() : "?";
                            string message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() : "";
                            failures.Add(name + ": " + message);
                        }

                        // the server stops after the first refusal that is the same for all (blocked, not connected)
                        refusedAll = results.GetArrayLength() < pack.Count;
                    }

                    if (refusedAll)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }

            _busy = false;
            ButtonApply.IsEnabled = true;

            if (failures.Count == 0)
            {
                TextBlockState.Text = "Done: " + done + " of " + names.Count + " securities set to " + mode + ", " + leverage + "x";
                TextBlockState.Foreground = Brushes.Gray;
            }
            else
            {
                TextBlockState.Text = "Set " + done + " of " + names.Count + ". Not done: " + failures.Count;
                TextBlockState.Foreground = Brushes.OrangeRed;
                string shown = string.Join("\n", failures.Take(12)) + (failures.Count > 12 ? "\n... and " + (failures.Count - 12) + " more" : "");
                System.Windows.MessageBox.Show("Set " + done + " of " + names.Count + ".\n\nNot done:\n" + shown, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ScheduleRefresh();
        }

        private static int ReadInt(JsonElement item, string name) =>
            item.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;
    }
}
