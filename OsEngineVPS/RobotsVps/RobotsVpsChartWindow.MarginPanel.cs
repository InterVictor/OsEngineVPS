// Margin mode and leverage of the security of this chart (Binance Futures and other connectors that report them).
// The strip sits under the header of the side tabs (Market depth / Alerts / Grid / Control). It reads the data through
// the MCP tool server_instance_get_margin_info and writes through server_instance_set_margin_info; the server side refuses
// to write while the server parameter "Block margin and leverage changes" is on (the default), so a connected account
// that must not be touched stays untouched.

using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    public partial class RobotsVpsChartWindow
    {
        private const double MarginPanelHeight = 54;
        private static readonly TimeSpan MarginRefreshPeriod = TimeSpan.FromSeconds(10);

        private string _marginServerType;
        private int _marginServerNumber;
        private DateTime _lastMarginRefresh = DateTime.MinValue;
        private bool _marginInputsTouched;
        private bool _marginSettingInputs;
        private bool _marginBusy;
        private bool _marginPanelReady;

        private void InitMarginPanel()
        {
            // a screener window shows the table of securities, there is no single security to show margin for
            if (_isScreener)
            {
                return;
            }

            BorderMarginPanel.Visibility = Visibility.Visible;
            ComboBoxMarginMode.Items.Add("Isolated");
            ComboBoxMarginMode.Items.Add("Cross");
            ComboBoxMarginMode.SelectionChanged += (s, e) => { if (!_marginSettingInputs) _marginInputsTouched = true; };
            TextBoxMarginLeverage.TextChanged += (s, e) => { if (!_marginSettingInputs) _marginInputsTouched = true; };
            ButtonApplyMargin.Click += ButtonApplyMargin_Click;

            // the panel goes right under the header of the side tabs, the content of every tab moves down by its height
            Dispatcher.BeginInvoke(new Action(PlaceMarginPanel), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void PlaceMarginPanel()
        {
            if (_marginPanelReady)
            {
                return;
            }

            double headerHeight = 26;
            TabPanel header = FindChild<TabPanel>(TabControlControl);

            if (header != null && header.ActualHeight > 0)
            {
                headerHeight = header.ActualHeight + header.Margin.Top + header.Margin.Bottom + 2;
            }

            BorderMarginPanel.Margin = new Thickness(0, headerHeight, 0, 0);

            foreach (object item in TabControlControl.Items)
            {
                TabItem tab = item as TabItem;
                FrameworkElement content = tab?.Content as FrameworkElement;

                if (content != null)
                {
                    content.Margin = new Thickness(content.Margin.Left, content.Margin.Top + MarginPanelHeight, content.Margin.Right, content.Margin.Bottom);
                }
            }

            _marginPanelReady = true;
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is T match)
                {
                    return match;
                }

                T inner = FindChild<T>(child);

                if (inner != null)
                {
                    return inner;
                }
            }

            return null;
        }

        // the snapshot of the chart tells which server the tab trades through; "BinanceFutures" or "BinanceFutures_1"
        private void ReadMarginTarget(JsonElement snapshot)
        {
            string type = ReadText(snapshot, "server_type");

            if (string.IsNullOrEmpty(type))
            {
                return;
            }

            _marginServerType = type;
            _marginServerNumber = 0;
            string full = ReadText(snapshot, "server_full_name");

            if (full.StartsWith(type + "_", StringComparison.Ordinal)
                && int.TryParse(full.Substring(type.Length + 1), out int number))
            {
                _marginServerNumber = number;
            }
        }

        private async Task RefreshMarginInfoAsync()
        {
            if (_isScreener || _marginBusy || string.IsNullOrEmpty(_marginServerType) || string.IsNullOrEmpty(_securityName))
            {
                return;
            }

            if (DateTime.UtcNow - _lastMarginRefresh < MarginRefreshPeriod)
            {
                return;
            }

            _lastMarginRefresh = DateTime.UtcNow;

            try
            {
                JsonElement answer = await _client.CallToolAsync("server_instance_get_margin_info",
                    new { type = _marginServerType, number = _marginServerNumber, security_names = new[] { _securityName } });

                if (answer.TryGetProperty("securities", out JsonElement securities)
                    && securities.ValueKind == JsonValueKind.Array
                    && securities.GetArrayLength() > 0)
                {
                    ShowMarginInfo(securities[0]);
                }
                else
                {
                    TextBlockMarginState.Text = "Margin settings: the connector gives no data for " + _securityName + " (yet)";
                }
            }
            catch (Exception ex)
            {
                // the tool is new: a server that was not updated answers "unknown tool"
                TextBlockMarginState.Text = "Margin settings are not available: " + ex.Message;
            }
        }

        private void ShowMarginInfo(JsonElement info)
        {
            bool isolated = ReadBool(info, "is_isolated");
            decimal leverage = ReadDecimal(info, "leverage");
            string mode = isolated ? "Isolated" : "Cross";

            string age = string.Empty;

            if (DateTime.TryParse(ReadString(info, "time_update"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime updated))
            {
                int seconds = (int)Math.Max(0, (DateTime.UtcNow - updated).TotalSeconds);
                age = ", updated " + (seconds < 90 ? seconds + " s" : seconds / 60 + " min") + " ago";
            }

            TextBlockMarginState.Text = "On the exchange: " + mode + ", " + leverage.ToString("0.##", CultureInfo.InvariantCulture) + "x" + age;
            TextBlockMarginState.Foreground = isolated && leverage >= 20 ? Brushes.OrangeRed : Brushes.Gray;

            // do not overwrite what the user is typing
            if (!_marginInputsTouched)
            {
                _marginSettingInputs = true;
                ComboBoxMarginMode.SelectedItem = mode;
                TextBoxMarginLeverage.Text = leverage.ToString("0.##", CultureInfo.InvariantCulture);
                _marginSettingInputs = false;
            }
        }

        private async void ButtonApplyMargin_Click(object sender, RoutedEventArgs e)
        {
            if (_marginBusy)
            {
                return;
            }

            if (string.IsNullOrEmpty(_marginServerType) || string.IsNullOrEmpty(_securityName))
            {
                System.Windows.MessageBox.Show("The security of this chart is not known yet, try in a few seconds.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string mode = ComboBoxMarginMode.SelectedItem as string;

            if (mode == null || !int.TryParse(TextBoxMarginLeverage.Text.Trim(), out int leverage) || leverage < 1)
            {
                System.Windows.MessageBox.Show("Choose Isolated or Cross and enter the leverage as a whole number, 1 or more.", "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string question = _securityName + " on " + _marginServerType + ": " + mode + ", leverage " + leverage + "x.\n\n"
                + "This is a setting of the exchange account: every terminal and manual trading on this account will see it. "
                + "The exchange does not change the margin mode while the security has an open position or order.\n\nSend to the exchange?";

            if (System.Windows.MessageBox.Show(question, "VPS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _marginBusy = true;
            ButtonApplyMargin.IsEnabled = false;

            try
            {
                JsonElement answer = await _client.CallToolAsync("server_instance_set_margin_info",
                    new { type = _marginServerType, number = _marginServerNumber, security_names = new[] { _securityName }, margin_mode = mode, leverage });

                string message = "No answer";
                bool ok = false;

                if (answer.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                {
                    ok = ReadBool(results[0], "ok");
                    message = ReadString(results[0], "message");
                }

                TextBlockMarginState.Text = (ok ? "Done: " : "Not done: ") + message;
                TextBlockMarginState.Foreground = ok ? Brushes.Gray : Brushes.OrangeRed;

                if (ok)
                {
                    _marginInputsTouched = false;
                    _lastMarginRefresh = DateTime.MinValue;
                }
                else
                {
                    System.Windows.MessageBox.Show(message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _marginBusy = false;
                ButtonApplyMargin.IsEnabled = true;
            }
        }
    }
}
