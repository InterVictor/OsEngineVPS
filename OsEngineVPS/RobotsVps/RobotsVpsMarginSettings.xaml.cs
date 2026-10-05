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

        // the screener policy: the same mode and leverage for every security that gets into the screener later
        private Func<(string BotId, string TabName)?> _policyTarget;
        private bool _policyLoaded;
        private bool _policyLoading;
        private readonly DispatcherTimer _policySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };

        public RobotsVpsMarginSettings()
        {
            InitializeComponent();
            ComboBoxMode.Items.Add("Isolated");
            ComboBoxMode.Items.Add("Cross");
            ComboBoxMode.SelectedItem = "Cross";
            ButtonApply.Click += ButtonApply_Click;
            _refreshTimer.Tick += async (s, e) => { _refreshTimer.Stop(); await RefreshStateAsync(); };

            CheckBoxPolicy.Click += async (s, e) => await CheckBoxPolicy_ClickAsync();
            _policySaveTimer.Tick += async (s, e) => { _policySaveTimer.Stop(); await SavePolicyAsync(); };
            ComboBoxMode.SelectionChanged += (s, e) => SchedulePolicySave();
            TextBoxLeverage.TextChanged += (s, e) => SchedulePolicySave();
        }

        // only the screener window calls it: it adds the check box of the policy under the fields
        public void InitPolicy(Func<(string BotId, string TabName)?> target)
        {
            _policyTarget = target;
            CheckBoxPolicy.Visibility = Visibility.Visible;
            TextBlockPolicy.Visibility = Visibility.Visible;
            TextBlockState.Margin = new Thickness(10, 124, 10, 0);
            Height = 156;
        }

        private void SchedulePolicySave()
        {
            if (_policyTarget == null || _policyLoading || CheckBoxPolicy.IsChecked != true)
            {
                return;
            }

            _policySaveTimer.Stop();
            _policySaveTimer.Start();
        }

        private async Task LoadPolicyAsync()
        {
            (string BotId, string TabName)? target = _policyTarget?.Invoke();

            if (target == null || _client == null || !_client.IsConnected)
            {
                return;
            }

            try
            {
                JsonElement answer = await _client.CallToolAsync("bot_screener_get_margin_policy", new { bot_id = target.Value.BotId, tab_name = target.Value.TabName });
                _policyLoading = true;
                bool enabled = answer.TryGetProperty("enabled", out JsonElement en) && en.ValueKind == JsonValueKind.True;
                CheckBoxPolicy.IsChecked = enabled;

                if (enabled)
                {
                    string mode = answer.TryGetProperty("margin_mode", out JsonElement m) ? m.GetString() : "Cross";
                    ComboBoxMode.SelectedItem = mode == "Isolated" ? "Isolated" : "Cross";
                    TextBoxLeverage.Text = answer.TryGetProperty("leverage", out JsonElement l) && l.TryGetInt32(out int lev) ? lev.ToString() : "3";
                }

                TextBlockPolicy.Text = answer.TryGetProperty("status", out JsonElement st) ? st.GetString() : string.Empty;
                _policyLoading = false;
                _policyLoaded = true;
            }
            catch (Exception ex)
            {
                _policyLoading = false;
                _policyLoaded = true;
                TextBlockPolicy.Text = "The policy is not available: " + ex.Message;
            }
        }

        private async Task CheckBoxPolicy_ClickAsync()
        {
            if (CheckBoxPolicy.IsChecked == true)
            {
                string mode = ComboBoxMode.SelectedItem as string;

                if (mode == null || !int.TryParse(TextBoxLeverage.Text.Trim(), out int leverage) || leverage < 1)
                {
                    CheckBoxPolicy.IsChecked = false;
                    System.Windows.MessageBox.Show("Choose Isolated or Cross and enter the leverage as a whole number, 1 or more.", "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string question = "From now on every NEW security that gets into this screener will be set on the exchange to " + mode + ", leverage " + leverage + "x, "
                    + "automatically (checked once a minute).\n\nThe securities that are in the screener now are not touched (use the button for them). "
                    + "The settings belong to the exchange account. If the server blocks margin changes, nothing is sent.\n\nSwitch the policy on?";

                if (System.Windows.MessageBox.Show(question, "VPS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    CheckBoxPolicy.IsChecked = false;
                    return;
                }
            }

            await SavePolicyAsync();
        }

        private async Task SavePolicyAsync()
        {
            (string BotId, string TabName)? target = _policyTarget?.Invoke();

            if (target == null || _client == null || !_client.IsConnected)
            {
                return;
            }

            string modeText = ComboBoxMode.SelectedItem as string ?? "Cross";
            int leverage = int.TryParse(TextBoxLeverage.Text.Trim(), out int value) && value >= 1 ? value : 3;

            try
            {
                JsonElement answer = await _client.CallToolAsync("bot_screener_set_margin_policy",
                    new { bot_id = target.Value.BotId, tab_name = target.Value.TabName, enabled = CheckBoxPolicy.IsChecked == true, margin_mode = modeText, leverage });
                TextBlockPolicy.Text = answer.TryGetProperty("status", out JsonElement st) ? st.GetString() : string.Empty;
            }
            catch (Exception ex)
            {
                TextBlockPolicy.Text = "The policy was not saved: " + ex.Message;
                System.Windows.MessageBox.Show("The policy was not saved: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

            if (_policyTarget != null && !_policyLoaded)
            {
                await LoadPolicyAsync();
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
