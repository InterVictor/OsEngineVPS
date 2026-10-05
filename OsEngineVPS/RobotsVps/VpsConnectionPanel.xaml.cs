/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OsEngine.Alerts;
using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// VPS connection and administration window: SSH tunnel + MCP connection to a remote (headless)
    /// OsEngine and its event log. Robots and connectors of the remote terminal are managed from the
    /// Robots.VPS workspace (RobotsVpsLiteClone), not here.
    /// Окно подключения и администрирования VPS: SSH-туннель, подключение MCP к удалённому OsEngine и
    /// лог событий. Роботами и коннекторами удалённого терминала управляет окно Роботы.VPS.
    /// </summary>
    public partial class VpsConnectionPanel : UserControl
    {
        private readonly string _settingsFile;
        private readonly string _profileId;

        private SshTunnel _sshTunnel;
        private readonly ObservableCollection<LogRow> _log = new ObservableCollection<LogRow>();

        /// <summary>a line for the common log of the Settings window: VPS id, time, text (on the UI thread)</summary>
        public static event Action<string, DateTime, string> Logged;

        public VpsConnectionPanel(string profileId)
        {
            _profileId = profileId;
            _settingsFile = VpsProfiles.SettingsFileFor(profileId);

            InitializeComponent();

            LogDataGrid.ItemsSource = _log;
            DataGridTerminals.ItemsSource = _terminalRows;
            UpdatePackageText();

            LoadSettings();
            UpdateComputerKeyStatus();
        }

        public string ProfileId => _profileId;
        public string Host => TextBoxSshHost.Text.Trim();
        public string StatusText => LabelStatus.Content?.ToString() ?? "";
        public int ConnectedTerminalCount => _terminals.Count;
        public bool AutoConnect => CheckBoxAutoConnectSsh.IsChecked == true;
        public bool IsConnectedToVps => IsConnected;

        /// <summary>how this computer logs in to this VPS: its own registered key, a key file, or only the password (for the table of all VPS)</summary>
        public string LoginText =>
            _computerKey != null ? "own key registered"
            : !string.IsNullOrWhiteSpace(TextBoxSshKeyPath.Text) ? "key file"
            : !string.IsNullOrEmpty(PasswordBoxSshPassword.Password) ? "password only"
            : "not set";

        public void ShutdownConnection()
        {
            Disconnect();
        }

        /// <summary>true if the "connect automatically on program start" box of this VPS was ticked when the settings were saved</summary>
        public static bool IsAutoConnectOnStartupEnabled(string profileId)
        {
            try
            {
                string file = VpsProfiles.SettingsFileFor(profileId);
                string[] lines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();
                return lines.Length > 7 && bool.TryParse(lines[7], out bool enabled) && enabled;
            }
            catch { return false; }
        }

        public void StartAutomaticConnect()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CheckBoxAutoConnectSsh.IsChecked == true) ButtonConnect_Click(null, null);
            }));
        }

        private void CheckBoxAutoConnectSsh_Click(object sender, RoutedEventArgs e) => SaveSettings();

        #region Settings (Url + API key; excluded from git like other Engine\* connector settings)

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFile))
                {
                    // a new VPS: its own range of local ports, so that its tunnel does not meet the tunnels of the others
                    if (_profileId != VpsProfiles.FirstId)
                    {
                        TextBoxSshLocalPort.Text = VpsProfiles.NextLocalPortBase().ToString(CultureInfo.InvariantCulture);
                        SaveSettings();
                    }

                    return;
                }

                string[] lines = File.ReadAllLines(_settingsFile);

                if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    TextBoxUrl.Text = lines[0];
                }

                if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]))
                {
                    PasswordBoxApiKey.Password = UnprotectSecret(lines[1]);
                }

                if (lines.Length > 2) TextBoxSshHost.Text = lines[2];
                if (lines.Length > 3) TextBoxSshUser.Text = lines[3];
                // an empty key path is a valid choice (password or registered key only) — keep it empty
                if (lines.Length > 4) TextBoxSshKeyPath.Text = lines[4];
                if (lines.Length > 5 && !string.IsNullOrWhiteSpace(lines[5])) TextBoxSshLocalPort.Text = lines[5];
                if (lines.Length > 6 && !string.IsNullOrWhiteSpace(lines[6])) TextBoxSshRemotePort.Text = lines[6];
                if (lines.Length > 7 && bool.TryParse(lines[7], out bool autoConnect)) CheckBoxAutoConnectSsh.IsChecked = autoConnect;
                if (lines.Length > 8 && !string.IsNullOrWhiteSpace(lines[8])) PasswordBoxSshPassword.Password = UnprotectSecret(lines[8]);
                if (lines.Length > 9 && !string.IsNullOrWhiteSpace(lines[9])) _computerKey = UnprotectSecret(lines[9]);
                if (lines.Length > 10) _computerKeyComment = lines[10];
                if (lines.Length > 11 && bool.TryParse(lines[11], out bool dailyBackup)) CheckBoxDailyBackup.IsChecked = dailyBackup;
                // line 13 (the MCP access folders) belongs to all VPS now: VpsMcpAccess / Engine\VpsCommonSettings.txt
            }
            catch (Exception ex)
            {
                AppendLog("Settings load failed: " + ex.Message);
            }
        }

        // Secrets (MCP API key, SSH password) are stored encrypted for the current Windows user (DPAPI).
        // Values saved by older versions in plain text are still read and get encrypted on the next save.
        private const string ProtectedPrefix = "dpapi:";

        private static string ProtectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(data);
        }

        private static string UnprotectSecret(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return stored ?? "";
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(ProtectedPrefix.Length)), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory("Engine");

                // line 13 (the MCP access folders of the single-VPS versions) is not ours any more, but it stays as it was
                string oldMcpFolders = "";

                try
                {
                    string[] old = File.Exists(_settingsFile) ? File.ReadAllLines(_settingsFile) : Array.Empty<string>();
                    if (old.Length > 12) oldMcpFolders = old[12];
                }
                catch { /* a new file: nothing to keep */ }

                File.WriteAllLines(_settingsFile, new[]
                {
                    TextBoxUrl.Text.Trim(),
                    ProtectSecret(PasswordBoxApiKey.Password),
                    TextBoxSshHost.Text.Trim(),
                    TextBoxSshUser.Text.Trim(),
                    TextBoxSshKeyPath.Text.Trim(),
                    TextBoxSshLocalPort.Text.Trim(),
                    TextBoxSshRemotePort.Text.Trim(),
                    (CheckBoxAutoConnectSsh.IsChecked == true).ToString(),
                    ProtectSecret(PasswordBoxSshPassword.Password),
                    ProtectSecret(_computerKey ?? ""),
                    _computerKeyComment ?? "",
                    (CheckBoxDailyBackup.IsChecked == true).ToString(),
                    oldMcpFolders
                });
            }
            catch (Exception ex)
            {
                AppendLog("Settings save failed: " + ex.Message);
            }
        }

        #endregion

        #region Server: deploy over SSH + SSH key of this computer

        // Registered SSH key of this computer (private key text, DPAPI-encrypted in the settings file) and its
        // comment in the server's ~/.ssh/authorized_keys (osengine-client-<computer>-<date>).
        private string _computerKey;
        private string _computerKeyComment;

        private VpsSshCredentials CreateCredentials() =>
            VpsSshCredentials.Create(TextBoxSshHost.Text, TextBoxSshUser.Text, TextBoxSshKeyPath.Text,
                _computerKey, PasswordBoxSshPassword.Password, LogFromAnyThread);

        private void LogFromAnyThread(string message) =>
            Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));

        private void UpdateComputerKeyStatus()
        {
            TextBlockComputerKey.Text = _computerKey != null
                ? $"SSH key of this computer: registered ({_computerKeyComment}). The root password is not stored."
                : "SSH key of this computer: not registered — connect once with the root password";
        }

        private async Task RegisterComputerKeyAsync(VpsSshCredentials credentials)
        {
            try
            {
                (string privateKey, string comment) = await new VpsProvisioner(credentials, LogFromAnyThread)
                    .RegisterThisComputerAsync(CancellationToken.None).ConfigureAwait(true);

                _computerKey = privateKey;
                _computerKeyComment = comment;
                PasswordBoxSshPassword.Password = "";
                SaveSettings();
                UpdateComputerKeyStatus();
                AppendLog("From now on this computer logs in with its own key; the root password was removed from the settings");
            }
            catch (Exception ex)
            {
                AppendLog("Could not register the SSH key of this computer (the password keeps working): " + ex.Message);
            }
        }

        private void ButtonComputers_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            // the keys this computer logs in with: its registered key and the key file, if any
            string[] mine =
            {
                VpsComputers.Fingerprint(_computerKey, null),
                VpsComputers.Fingerprint(null, Environment.ExpandEnvironmentVariables(TextBoxSshKeyPath.Text.Trim().Trim('"')))
            };

            RobotsVpsComputersUi window = new RobotsVpsComputersUi(_sshTunnel.RunCommandAsync, mine, AppendLog) { Owner = Window.GetWindow(this) };
            window.ShowDialog();
        }

        private async void ButtonDeployServer_Click(object sender, RoutedEventArgs e)
        {
            VpsSshCredentials credentials;

            try
            {
                credentials = CreateCredentials();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Set up OsEngine on {credentials.HostId} as {credentials.User}?\n\n"
                + "Time zone UTC + NTP, firewall (only SSH open), fail2ban, service user, OsEngine build, "
                + "robot scripts, MCP key and the systemd service. Parts that already exist are kept as they are.");
            confirm.ShowDialog();

            if (!confirm.UserAcceptAction)
            {
                return;
            }

            ButtonDeployServer.IsEnabled = false;
            SaveSettings();

            try
            {
                AppendLog("=== Deploy / repair server " + credentials.HostId + " ===");
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread).DeployAsync(CancellationToken.None)).ConfigureAwait(true);

                if (_computerKey == null && credentials.Password != null)
                {
                    await RegisterComputerKeyAsync(credentials).ConfigureAwait(true);
                }

                if (!IsConnected)
                {
                    ButtonConnect_Click(null, null);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Deploy failed: " + ex.Message);
            }
            finally
            {
                ButtonDeployServer.IsEnabled = true;
            }
        }

        #endregion

        #region Connect / Disconnect (one SSH tunnel, one MCP client per VPS terminal)

        // A running terminal on the VPS and its MCP client. The main terminal uses the ports of the window fields
        // (local 6510 -> VPS 6500); an extra terminal on VPS port P gets local port <Local port> + (P - 6500),
        // all carried by the same SSH connection.
        private sealed class TerminalConnection
        {
            public string Name;
            public int LocalPort;
            public string ApiKey;
            public RemoteMcpClient Client;
            public bool Reconnecting;
        }

        private readonly Dictionary<string, TerminalConnection> _terminals =
            new Dictionary<string, TerminalConnection>(StringComparer.OrdinalIgnoreCase);

        private List<VpsInstance> _instances = new List<VpsInstance>();
        private DispatcherTimer _terminalsTimer;
        private bool _syncingTerminals;
        private bool _noServiceLogged;

        private bool IsConnected => _terminals.Count > 0 || _sshTunnel != null;

        private async void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            string url = TextBoxUrl.Text.Trim();
            string apiKey = PasswordBoxApiKey.Password;

            if (string.IsNullOrWhiteSpace(TextBoxSshHost.Text))
            {
                MessageBox.Show("Enter the SSH host of this VPS");
                return;
            }

            ButtonConnect.IsEnabled = false;
            SetStatus("Connecting...", Brushes.Orange);

            try
            {
                SaveSettings();

                if (string.IsNullOrWhiteSpace(TextBoxSshHost.Text))
                {
                    // no SSH: direct MCP URL + the API Key box, main terminal only
                    await ConnectTerminalAsync(VpsRemoteSession.MainInstance, url, apiKey, 0).ConfigureAwait(true);
                }
                else
                {
                    if (!int.TryParse(TextBoxSshLocalPort.Text, out int localPort)
                        || !int.TryParse(TextBoxSshRemotePort.Text, out int remotePort))
                    {
                        throw new FormatException("SSH local and VPS API ports must be whole numbers");
                    }

                    VpsSshCredentials credentials = CreateCredentials();
                    _sshTunnel = await SshTunnel.StartAsync(credentials, localPort, remotePort, LogFromAnyThread).ConfigureAwait(true);
                    TextBoxUrl.Text = $"http://127.0.0.1:{localPort}/api/v2/mcp";

                    if (!_sshTunnel.StartedByThisWindow)
                    {
                        // someone else's tunnel on the local port: no SSH commands, main terminal via the API Key box
                        AppendLog("SSH tunnel already running — only the main terminal is available");
                        await ConnectTerminalAsync(VpsRemoteSession.MainInstance, TextBoxUrl.Text, apiKey, localPort).ConfigureAwait(true);
                    }
                    else
                    {
                        // First login with the root password from this computer: create and register its own key,
                        // so the password is not needed (nor stored) from now on.
                        if (_computerKey == null && !string.IsNullOrEmpty(PasswordBoxSshPassword.Password))
                        {
                            await RegisterComputerKeyAsync(credentials).ConfigureAwait(true);
                        }

                        await SyncTerminalsAsync().ConfigureAwait(true);

                        _terminalsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                        _terminalsTimer.Tick += async (s, args) => await SyncTerminalsAsync().ConfigureAwait(true);
                        _terminalsTimer.Start();
                    }
                }

                SaveSettings();
                ButtonDisconnect.IsEnabled = true;
                UpdateOverallStatus();
            }
            catch (Exception ex)
            {
                DisconnectCore();
                ButtonConnect.IsEnabled = true;
                SetStatus("Disconnected", Brushes.Gray);
                AppendLog("Connect failed: " + ex.Message);
            }
        }

        // Brings the MCP connections in line with the terminals on the VPS: connects running ones that are not
        // connected yet, drops those that were stopped or removed. Runs on connect and every 10 s.
        private async Task SyncTerminalsAsync()
        {
            SshTunnel tunnel = _sshTunnel;

            if (tunnel == null || !tunnel.StartedByThisWindow || _syncingTerminals)
            {
                return;
            }

            _syncingTerminals = true;

            try
            {
                List<VpsInstance> instances = await VpsInstances.ListAsync(tunnel.RunCommandAsync).ConfigureAwait(true);

                if (!ReferenceEquals(tunnel, _sshTunnel))
                {
                    return; // disconnected meanwhile
                }

                _instances = instances;
                VpsRemoteSession.SetSsh(_profileId, CreateCredentials(), tunnel.RunCommandAsync, instances);

                await SampleMetricsAsync(tunnel, instances).ConfigureAwait(true);
                RunDailyBackupIfDue(instances);

                if (instances.Count == 0 && !_noServiceLogged)
                {
                    _noServiceLogged = true;
                    AppendLog("No OsEngine terminal found on the VPS — use \"Deploy / repair server\"");
                }

                int localBase = int.Parse(TextBoxSshLocalPort.Text, CultureInfo.InvariantCulture);
                bool changed = false;

                foreach (VpsInstance instance in instances.Where(i => i.IsActive && !_terminals.ContainsKey(i.Name)))
                {
                    int localPort = instance.IsMain ? localBase : localBase + (instance.Port - VpsInstances.MainPort);

                    try
                    {
                        if (!instance.IsMain)
                        {
                            tunnel.AddForward(localPort, instance.Port);
                        }

                        string key = await VpsInstances.ReadKeyAsync(tunnel.RunCommandAsync, instance).ConfigureAwait(true);
                        await ConnectTerminalAsync(instance.Name, $"http://127.0.0.1:{localPort}/api/v2/mcp", key, localPort).ConfigureAwait(true);

                        if (instance.IsMain) PasswordBoxApiKey.Password = key;
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Terminal \"{instance.Title}\": could not connect: {ex.Message}");
                    }
                }

                foreach (string name in _terminals.Keys.Where(n => !instances.Any(i => i.IsActive && string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    TerminalConnection terminal = _terminals[name];
                    DropTerminal(terminal);
                    AppendLog($"Terminal \"{name}\" is not running — disconnected");
                    changed = true;
                }

                if (changed)
                {
                    PublishSession();
                }

                UpdateMcpJsonConfig(instances);
                RenderTerminals();
                UpdateOverallStatus();
            }
            catch (Exception ex)
            {
                AppendLog("Could not read the terminals of the VPS: " + ex.Message);
            }
            finally
            {
                _syncingTerminals = false;
            }
        }

        private async Task ConnectTerminalAsync(string name, string url, string apiKey, int localPort)
        {
            RemoteMcpClient client = new RemoteMcpClient(url, apiKey);
            TerminalConnection terminal = new TerminalConnection { Name = name, LocalPort = localPort, ApiKey = apiKey, Client = client };

            client.EventReceived += (eventName, payload) => Client_EventReceived(terminal, eventName, payload);
            client.Disconnected += ex => Client_Disconnected(terminal, ex);
            client.Reconnected += () => Client_Reconnected(terminal);

            try
            {
                await client.ConnectAsync().ConfigureAwait(true);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _terminals[name] = terminal;
            PublishSession();
            AppendLog(TerminalPrefix(name) + "connected to " + url);
        }

        private void DropTerminal(TerminalConnection terminal)
        {
            _terminals.Remove(terminal.Name);
            terminal.Client.Dispose();

            if (!string.Equals(terminal.Name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase))
            {
                _sshTunnel?.RemoveForward(terminal.LocalPort);
            }
        }

        // The connections of AI agents (.mcp.json of the folders of "MCP access...", common to all VPS): this VPS hands over
        // the local end of the tunnel and the key of each connected terminal, the common writer puts them in the files.
        private void UpdateMcpJsonConfig(List<VpsInstance> instances)
        {
            PublishMcpEntries();
            VpsMcpAccess.UpdateAll();
        }

        private void PublishMcpEntries()
        {
            VpsRemoteSession.SetMcpEntries(_profileId, _terminals.Values
                .Where(t => t.LocalPort > 0 && !string.IsNullOrEmpty(t.ApiKey))
                .ToDictionary(t => t.Name, t => (t.LocalPort, t.ApiKey), StringComparer.OrdinalIgnoreCase));
        }

        private void PublishSession()
        {
            VpsRemoteSession.SetClients(_profileId, _terminals.ToDictionary(t => t.Key, t => t.Value.Client, StringComparer.OrdinalIgnoreCase));
            PublishMcpEntries();
        }

        private void ButtonDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void Disconnect()
        {
            DisconnectCore();
            ButtonConnect.IsEnabled = true;
            ButtonDisconnect.IsEnabled = false;
            SetStatus("Disconnected", Brushes.Gray);
        }

        private void DisconnectCore()
        {
            _terminalsTimer?.Stop();
            _terminalsTimer = null;

            foreach (TerminalConnection terminal in _terminals.Values.ToList())
            {
                terminal.Client.Dispose();
            }

            _terminals.Clear();
            VpsRemoteSession.SetClients(_profileId, null);
            VpsRemoteSession.SetMcpEntries(_profileId, null);
            VpsRemoteSession.SetSsh(_profileId, null, null, null);

            if (_sshTunnel != null)
            {
                bool stoppedTunnel = _sshTunnel.StartedByThisWindow;
                _sshTunnel.Dispose();
                _sshTunnel = null;
                if (stoppedTunnel) AppendLog("SSH tunnel stopped");
            }

            _instances = new List<VpsInstance>();
            _noServiceLogged = false;
            ResetMonitoring();
            RenderTerminals();
        }

        // prefix log lines with the terminal name only when the VPS runs several terminals
        private string TerminalPrefix(string name) =>
            _instances.Count > 1 || !string.Equals(name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase)
                ? $"[{name}] " : "";

        // Raised on every failed retry (~2 s) while a terminal is unreachable — log only the first one.
        private void Client_Disconnected(TerminalConnection terminal, Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                if (!terminal.Reconnecting)
                {
                    terminal.Reconnecting = true;
                    AppendLog(TerminalPrefix(terminal.Name) + "connection lost, reconnecting: " + ex.Message);
                }

                UpdateOverallStatus();
            });
        }

        private void Client_Reconnected(TerminalConnection terminal)
        {
            Dispatcher.Invoke(() =>
            {
                terminal.Reconnecting = false;
                AppendLog(TerminalPrefix(terminal.Name) + "connection restored");
                UpdateOverallStatus();
            });
        }

        private static readonly HashSet<string> RoutineEvents = new HashSet<string>(StringComparer.Ordinal)
        {
            "heartbeat",
            "server_instance.portfolio.updated",
            "server_instance.security.updated",
            "server_instance.log",
            "tester.test.progress",
            "optimizer.test.progress"
        };

        private void Client_EventReceived(TerminalConnection terminal, string eventName, JsonElement payload)
        {
            Dispatcher.Invoke(() =>
            {
                if (eventName == "alert.raised")
                {
                    string botName = payload.TryGetProperty("bot_name", out JsonElement bot) ? bot.GetString() : "VPS";
                    string message = payload.TryGetProperty("message", out JsonElement text) ? text.GetString() : "";
                    string time = payload.TryGetProperty("time", out JsonElement timestamp) ? timestamp.GetString() : DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    AlertMessageManager.ThrowRemoteAlert(botName, message, time);
                    AppendLog(TerminalPrefix(terminal.Name) + "emergency alert received: " + botName);
                    return;
                }

                // Routine events the server sends every few seconds (it marks them "info"/"debug"): heartbeat, portfolio
                // and security refreshes, connector log lines (shown in Robots.VPS -> Server log), test progress —
                // they would bury the meaningful ones. Kept: connection status, terminal start/stop, alerts, test results.
                if (RoutineEvents.Contains(eventName)) return;

                if (eventName == "server_instance.status_changed")
                {
                    string type = payload.TryGetProperty("type", out JsonElement t) ? t.GetString() : "?";
                    string number = payload.TryGetProperty("number", out JsonElement n) ? n.ToString() : "?";
                    string status = payload.TryGetProperty("status", out JsonElement s) ? s.GetString() : "?";
                    AppendLog(TerminalPrefix(terminal.Name) + type + " #" + number + ": " + status);
                    return;
                }

                AppendLog(TerminalPrefix(terminal.Name) + eventName);
            });
        }

        private void UpdateOverallStatus()
        {
            if (!IsConnected)
            {
                SetStatus("Disconnected", Brushes.Gray);
            }
            else if (_terminals.Values.Any(t => t.Reconnecting))
            {
                SetStatus("Reconnecting...", Brushes.Orange);
            }
            else if (_terminals.Count == 0)
            {
                SetStatus("SSH connected, no terminal running", Brushes.Orange);
            }
            else
            {
                SetStatus(_terminals.Count == 1 ? "Connected" : $"Connected ({_terminals.Count} terminals)", Brushes.Green);
            }
        }

        private void SetStatus(string text, Brush color)
        {
            LabelStatus.Content = text;
            EllipseStatus.Fill = color;
        }

        #endregion

        #region Terminals on the VPS (systemd services osengine / osengine-<name>)

        private readonly ObservableCollection<TerminalRow> _terminalRows = new ObservableCollection<TerminalRow>();

        private void RenderTerminals()
        {
            string selected = (DataGridTerminals.SelectedItem as TerminalRow)?.Name;
            _terminalRows.Clear();

            foreach (VpsInstance instance in _instances)
            {
                _terminalRows.Add(new TerminalRow
                {
                    Name = instance.Name,
                    Title = instance.Title,
                    Port = instance.Port,
                    State = instance.State,
                    Memory = instance.MemoryText,
                    Build = string.IsNullOrEmpty(instance.Build) ? "—" : instance.Build,
                    Cpu = _terminalCpu.TryGetValue(instance.Name, out double cpu) && !double.IsNaN(cpu) && instance.IsActive
                        ? cpu.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "—",
                    Connection = _terminals.TryGetValue(instance.Name, out TerminalConnection t)
                        ? (t.Reconnecting ? "reconnecting" : "connected")
                        : "—"
                });
            }

            DataGridTerminals.SelectedItem = _terminalRows.FirstOrDefault(r => r.Name == selected);
        }

        private VpsInstance SelectedInstance()
        {
            string name = (DataGridTerminals.SelectedItem as TerminalRow)?.Name;
            VpsInstance instance = _instances.FirstOrDefault(i => i.Name == name);

            if (instance == null)
            {
                MessageBox.Show("Select a terminal in the list first");
            }

            return instance;
        }

        private bool EnsureSshCommands()
        {
            if (_sshTunnel != null && _sshTunnel.StartedByThisWindow)
            {
                return true;
            }

            MessageBox.Show("Connect to the VPS over SSH first");
            return false;
        }

        private async Task RunTerminalActionAsync(string what, Func<Task> action)
        {
            PanelTerminalButtons.IsEnabled = false;

            try
            {
                AppendLog(what + "...");
                await action().ConfigureAwait(true);
                AppendLog(what + ": done");
            }
            catch (Exception ex)
            {
                AppendLog(what + " failed: " + ex.Message);
            }
            finally
            {
                PanelTerminalButtons.IsEnabled = true;
                await SyncTerminalsAsync().ConfigureAwait(true);
            }
        }

        private async void ButtonTerminalStart_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;
            await RunTerminalActionAsync($"Starting terminal \"{instance.Title}\"",
                () => VpsInstances.StartAsync(_sshTunnel.RunCommandAsync, instance)).ConfigureAwait(true);
        }

        private async void ButtonTerminalStop_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;

            AcceptDialogUi confirm = new AcceptDialogUi($"Stop terminal \"{instance.Title}\"? Its robots stop trading until it is started again.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunTerminalActionAsync($"Stopping terminal \"{instance.Title}\"",
                () => VpsInstances.StopAsync(_sshTunnel.RunCommandAsync, instance)).ConfigureAwait(true);
        }

        private async void ButtonTerminalRestart_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;

            AcceptDialogUi confirm = new AcceptDialogUi($"Restart terminal \"{instance.Title}\"? Its robots are stopped and started again.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunTerminalActionAsync($"Restarting terminal \"{instance.Title}\"",
                () => VpsInstances.RestartAsync(_sshTunnel.RunCommandAsync, instance)).ConfigureAwait(true);
        }

        private async void ButtonTerminalRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;

            if (instance.IsMain)
            {
                MessageBox.Show("The main terminal cannot be removed");
                return;
            }

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Remove terminal \"{instance.Title}\"?\n\nIts service is stopped and deleted. Its data (robots, settings, journals) "
                + "is not deleted but moved to /opt/osengine-removed on the VPS.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunTerminalActionAsync($"Removing terminal \"{instance.Title}\"", async () =>
            {
                string moved = await VpsInstances.RemoveAsync(_sshTunnel.RunCommandAsync, instance).ConfigureAwait(true);
                AppendLog($"Data of terminal \"{instance.Title}\" moved to {moved.Trim()}");
            }).ConfigureAwait(true);
        }

        // Migration of the selected terminal to another VPS of the list; the steps and their safety rules are in VpsMigration
        private async void ButtonTerminalMigrate_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            VpsInstance instance = SelectedInstance();
            if (instance == null) return;

            string sourceKey = VpsRemoteSession.Key(_profileId, instance.Name);
            RobotsVpsMigrateDialog dialog = new RobotsVpsMigrateDialog(sourceKey) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;

            MigrationPlan plan = new MigrationPlan { SourceKey = sourceKey, TargetVpsId = dialog.TargetVpsId, TargetName = dialog.TargetName };
            string sourceVps = VpsProfiles.NameOf(_profileId);
            string targetVps = VpsProfiles.NameOf(plan.TargetVpsId);

            // the open positions stay on the exchange; the robots do not manage them while the terminal is being moved
            RemoteMcpClient client = instance.IsActive ? VpsRemoteSession.GetClient(sourceKey) : null;
            List<string> positions = client == null ? null : await VpsMigration.OpenPositionsAsync(client).ConfigureAwait(true);

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Migrate terminal \"{instance.Title}\" from VPS \"{sourceVps}\" to VPS \"{targetVps}\" as \"{plan.TargetName}\"?\n\n"
                + (instance.IsActive ? "The source terminal is stopped for the move and stays stopped with its autostart OFF (its data is kept). " : "The source terminal is not running. ")
                + "The new terminal starts with all the data. This usually takes a few minutes.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            if (instance.IsActive && (positions == null || positions.Count > 0))
            {
                string text = positions == null
                    ? "The open positions of this terminal could not be read."
                    : $"The terminal has {positions.Count} open position(s):\n  " + string.Join("\n  ", positions.Take(12)) + (positions.Count > 12 ? "\n  ..." : "");

                AcceptDialogUi second = new AcceptDialogUi(
                    text + "\n\nThe positions stay on the exchange, but while the terminal is moved (usually 2–5 minutes) its robots do NOT manage them: "
                    + "no exits, no stops. Migrate anyway?");
                second.ShowDialog();
                if (!second.UserAcceptAction) return;
            }

            PanelTerminalButtons.IsEnabled = false;
            PanelMaintenanceButtons.IsEnabled = false;
            AppendLog($"=== Migration of terminal \"{instance.Title}\" to VPS \"{targetVps}\" as \"{plan.TargetName}\" ===");

            try
            {
                (MigrationOutcome outcome, string info) = await Task.Run(() => VpsMigration.RunAsync(plan, LogFromAnyThread, CancellationToken.None)).ConfigureAwait(true);
                AppendLog((outcome == MigrationOutcome.Done ? "Migration finished: " : "Migration did not finish: ") + info);
                MessageBox.Show(Window.GetWindow(this), info, outcome == MigrationOutcome.Done ? "Migration finished" : "Migration did not finish",
                    MessageBoxButton.OK, outcome == MigrationOutcome.Done ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppendLog("Migration failed: " + ex.Message);
                MessageBox.Show(Window.GetWindow(this), ex.Message, "Migration failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                PanelTerminalButtons.IsEnabled = true;
                PanelMaintenanceButtons.IsEnabled = true;
                await SyncTerminalsAsync().ConfigureAwait(true);
            }
        }

        private async void ButtonTerminalRename_Click(object sender, RoutedEventArgs e)
        {
            VpsInstance instance = SelectedInstance();

            if (instance != null)
            {
                await RenameTerminalAsync(instance.Name);
            }
        }

        // also called from the tabs of the main window (context menu "Rename...")
        public async Task RenameTerminalAsync(string terminalName)
        {
            if (!EnsureSshCommands()) return;

            VpsInstance instance = _instances.FirstOrDefault(i => string.Equals(i.Name, terminalName, StringComparison.OrdinalIgnoreCase));

            if (instance == null)
            {
                MessageBox.Show("The terminal was not found on the VPS");
                return;
            }

            RobotsVpsRenameDialog dialog = new RobotsVpsRenameDialog(instance.Title, instance.Name) { Owner = Window.GetWindow(this) ?? Application.Current.MainWindow };

            if (dialog.ShowDialog() != true) return;

            string oldTitle = instance.Title;

            try
            {
                // the list was read again while the dialog was open
                instance = _instances.FirstOrDefault(i => string.Equals(i.Name, instance.Name, StringComparison.OrdinalIgnoreCase)) ?? instance;

                AppendLog($"Renaming terminal \"{oldTitle}\" to \"{dialog.NewName}\"...");
                await VpsInstances.RenameAsync(_sshTunnel.RunCommandAsync, _instances, instance, dialog.NewName).ConfigureAwait(true);
                AppendLog($"Terminal \"{instance.Name}\" is shown as \"{instance.Title}\" now");
                RenderTerminals();
                VpsRemoteSession.RaiseInstancesChanged();
                await SyncTerminalsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog($"Renaming terminal \"{oldTitle}\" failed: {ex.Message}");
                MessageBox.Show("The terminal was not renamed: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ButtonTerminalAdd_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            string name = TextBoxNewTerminal.Text.Trim().ToLowerInvariant();

            if (!VpsInstances.IsValidName(name))
            {
                MessageBox.Show("Terminal name: 1–20 characters, latin letters, digits and '-', not \"main\"");
                return;
            }

            if (_instances.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"Terminal \"{name}\" already exists");
                return;
            }

            int port = VpsInstances.NextFreePort(_instances);

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Create terminal \"{name}\" on the VPS?\n\nIts own folder {VpsInstances.BaseFolderFor(name)}, service "
                + $"{VpsInstances.ServiceFor(name)}, MCP port {port}. The build and the robot scripts are copied from the main "
                + "terminal; robots, connectors and keys start empty. Each terminal needs RAM (about 150–400 MB with robots).");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials;

            try
            {
                credentials = CreateCredentials();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            await RunTerminalActionAsync($"Creating terminal \"{name}\"", async () =>
            {
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .DeployAsync(name, port, CancellationToken.None)).ConfigureAwait(true);
                TextBoxNewTerminal.Text = "";
            }).ConfigureAwait(true);
        }

        #endregion

        #region Maintenance: update build, upload robots, clean logs, reboot VPS

        private void UpdatePackageText()
        {
            try
            {
                string version = VpsProvisioner.LocalPackageVersion();
                string path = VpsProvisioner.LocalPath(VpsProvisioner.PackageFileName);

                TextBlockPackage.Text = version == null
                    ? "No build package in the VpsServer folder — \"Update build\" is not available"
                    : $"Build package on this computer: {version} ({File.GetLastWriteTime(path):dd.MM.yyyy HH:mm})";
            }
            catch (Exception ex)
            {
                TextBlockPackage.Text = "Build package: " + ex.Message;
            }
        }

        // The terminal selected in the list, or the only one when the VPS runs just one.
        private VpsInstance SelectedOrOnlyInstance()
        {
            if (DataGridTerminals.SelectedItem == null && _instances.Count == 1)
            {
                return _instances[0];
            }

            return SelectedInstance();
        }

        private async Task RunMaintenanceAsync(Func<Task> action)
        {
            PanelMaintenanceButtons.IsEnabled = false;
            PanelTerminalButtons.IsEnabled = false;

            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog("Failed: " + ex.Message);
            }
            finally
            {
                PanelMaintenanceButtons.IsEnabled = true;
                PanelTerminalButtons.IsEnabled = true;
                await SyncTerminalsAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// The update of this VPS as a step of "Update all" of the Settings window: the same update as the "Update build" button,
        /// but it first shows the open positions of the terminals and asks the confirmation for THIS VPS by name.
        /// Returns what happened and a short text for the summary.
        /// </summary>
        public async Task<(VpsUpdateOutcome Outcome, string Info)> UpdateBuildForAllAsync(string vpsName, int number, int total)
        {
            if (_sshTunnel == null || !_sshTunnel.StartedByThisWindow)
            {
                return (VpsUpdateOutcome.Skipped, "not connected over SSH");
            }

            UpdatePackageText();
            string version = VpsProvisioner.LocalPackageVersion();

            if (version == null)
            {
                return (VpsUpdateOutcome.Failed, "no build package on this computer");
            }

            // a stopped terminal stays stopped: the update restarts the service, so only running ones are updated
            List<VpsInstance> running = _instances.Where(i => i.IsActive).ToList();
            List<VpsInstance> toUpdate = running.Where(i => i.Build != version).ToList();

            if (toUpdate.Count == 0)
            {
                return (VpsUpdateOutcome.NothingToDo, running.Count == 0 ? "no terminal is running" : $"already on build {version}");
            }

            string list = string.Join("\n", toUpdate.Select(i => $"  {i.Title}: {(string.IsNullOrEmpty(i.Build) ? "unknown" : i.Build)} -> {version}"));
            string positions = await DescribeOpenPositionsAsync(running).ConfigureAwait(true);

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Update VPS \"{vpsName}\" ({number} of {total})?\n\n{list}\n\nOpen positions now:\n{positions}\n\n"
                + "Terminals are updated one by one; each one's robots stop for about 10–30 s and do not manage their positions meanwhile. "
                + "If a terminal does not start with the new build, its previous build is put back automatically. "
                + "Robots, settings, journals and keys are not touched.");
            confirm.ShowDialog();

            if (!confirm.UserAcceptAction)
            {
                return (VpsUpdateOutcome.Declined, "not confirmed");
            }

            VpsSshCredentials credentials = CreateCredentials();
            string error = null;

            await RunMaintenanceAsync(async () =>
            {
                AppendLog($"=== Update build {version} (Update all) ===");

                try
                {
                    await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                        .UpdateBuildAsync(toUpdate, CancellationToken.None)).ConfigureAwait(true);
                    AppendLog("Build update finished");
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    AppendLog("Failed: " + ex.Message);
                }
            }).ConfigureAwait(true);

            return error == null
                ? (VpsUpdateOutcome.Done, $"{toUpdate.Count} terminal(s) updated to {version}")
                : (VpsUpdateOutcome.Failed, error);
        }

        // "name: none" / "name: 2 (BTCUSDT, ETHUSDT)" for every running terminal, read from its journals over MCP
        private async Task<string> DescribeOpenPositionsAsync(IEnumerable<VpsInstance> instances)
        {
            List<string> lines = new List<string>();

            foreach (VpsInstance instance in instances)
            {
                try
                {
                    RemoteMcpClient client = VpsRemoteSession.GetClient(VpsRemoteSession.Key(_profileId, instance.Name));

                    if (client == null)
                    {
                        lines.Add($"  {instance.Title}: unknown (no MCP connection)");
                        continue;
                    }

                    JsonElement answer = await client.CallToolAsync("bot_journal_get_open_positions", new { limit = 500 }).ConfigureAwait(true);
                    List<string> names = new List<string>();

                    if (answer.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement position in positions.EnumerateArray())
                        {
                            names.Add(position.TryGetProperty("security_name", out JsonElement security) ? security.GetString() : "?");
                        }
                    }

                    lines.Add(names.Count == 0 ? $"  {instance.Title}: none" : $"  {instance.Title}: {names.Count} ({string.Join(", ", names.Take(6))}{(names.Count > 6 ? ", ..." : "")})");
                }
                catch (Exception ex)
                {
                    lines.Add($"  {instance.Title}: unknown ({ex.Message})");
                }
            }

            return lines.Count == 0 ? "  —" : string.Join("\n", lines);
        }

        private async void ButtonUpdateBuild_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            UpdatePackageText();
            string version = VpsProvisioner.LocalPackageVersion();

            if (version == null)
            {
                MessageBox.Show("Put the server build package into the VpsServer folder first:\n"
                    + VpsProvisioner.LocalPath(VpsProvisioner.PackageFileName));
                return;
            }

            // a stopped terminal stays stopped: the update restarts the service, so only running ones are updated
            List<VpsInstance> running = _instances.Where(i => i.IsActive).ToList();
            List<VpsInstance> toUpdate = running.Where(i => i.Build != version).ToList();

            if (toUpdate.Count == 0)
            {
                MessageBox.Show(running.Count == 0 ? "No terminal is running" : $"All running terminals already run build {version}");
                return;
            }

            string list = string.Join("\n", toUpdate.Select(i => $"  {i.Name}: {(string.IsNullOrEmpty(i.Build) ? "unknown" : i.Build)} -> {version}"));
            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Update the OsEngine build?\n\n{list}\n\nTerminals are updated one by one; each one's robots stop for about "
                + "10–30 s. If a terminal does not start with the new build, its previous build is put back automatically. "
                + "Robots, settings, journals and keys are not touched.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                AppendLog($"=== Update build {version} ===");
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .UpdateBuildAsync(toUpdate, CancellationToken.None)).ConfigureAwait(true);
                AppendLog("Build update finished");
            }).ConfigureAwait(true);
        }

        // File manager of the VPS (data folders of the terminals, trash, removed terminals); opens on the robots folder of
        // the selected terminal. A change to robot scripts there makes the terminal re-read them (no restart) and checks they compile.
        private void ButtonFiles_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            if (_instances.Count == 0)
            {
                MessageBox.Show("No OsEngine terminal found on the VPS — use \"Deploy / repair server\" first");
                return;
            }

            VpsInstance initial = (DataGridTerminals.SelectedItem as TerminalRow) is TerminalRow row
                ? _instances.FirstOrDefault(i => i.Name == row.Name)
                : _instances[0];

            VpsSshCredentials credentials;

            try
            {
                credentials = CreateCredentials();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            RobotsVpsFilesUi window = new RobotsVpsFilesUi(credentials, _sshTunnel.RunCommandAsync, _instances.ToList(), initial,
                ApplyRobotChangesAsync, LogFromAnyThread) { Owner = Window.GetWindow(this) };
            window.Show();
        }

        // Called by the file manager after robot scripts were changed: the terminal re-reads them without a restart
        // (running robots keep working), then the added or replaced ones are checked to compile. Never restarts on its
        // own — a terminal that cannot re-read (an older build, no answer) is left to the Restart button.
        private async Task ApplyRobotChangesAsync(VpsInstance instance, List<string> classNames)
        {
            AppendLog($"=== Robot scripts changed on terminal \"{instance.Title}\" ===");

            RemoteMcpClient client = VpsRemoteSession.GetClient(VpsRemoteSession.Key(_profileId, instance.Name));

            try
            {
                if (client == null || !client.IsConnected) throw new InvalidOperationException("the terminal is not connected");
                await client.CallToolAsync("wiki_robots_reload_scripts", new { }).ConfigureAwait(true);
                AppendLog($"Terminal \"{instance.Title}\" re-read the robot scripts (running robots keep their version)");
            }
            catch (Exception ex)
            {
                AppendLog($"Terminal \"{instance.Title}\" could not re-read the robot scripts ({ex.Message}) — "
                    + "restart it with the Restart button to pick up the changes");
                return;
            }

            if (classNames.Count > 0)
            {
                await CheckRobotsCompileAsync(instance, classNames).ConfigureAwait(true);
            }
        }

        // After the scripts are re-read asks the terminal to build each changed robot (wiki_robot_info compiles a script on
        // demand). A compile error is taken from the service journal, where BotFactory writes the compiler output.
        private async Task CheckRobotsCompileAsync(VpsInstance instance, List<string> classNames)
        {
            List<string> pending = new List<string>(classNames);
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            bool firstTry = true;

            while (pending.Count > 0 && DateTime.UtcNow < deadline)
            {
                if (!firstTry)
                {
                    // the terminal did not answer (reconnecting) — wait and reconnect
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                    await SyncTerminalsAsync().ConfigureAwait(true);
                }

                firstTry = false;

                RemoteMcpClient client = VpsRemoteSession.GetClient(VpsRemoteSession.Key(_profileId, instance.Name));
                if (client == null || !client.IsConnected) continue;

                foreach (string className in pending.ToList())
                {
                    try
                    {
                        await client.CallToolAsync("wiki_robot_info", new { class_name = className, is_script = true }).ConfigureAwait(true);
                        AppendLog($"Robot {className}: compiled OK");
                        pending.Remove(className);
                    }
                    catch (Exception ex) when (ex.Message.Contains("MCP tool", StringComparison.Ordinal))
                    {
                        // the terminal answered: the script does not build — show the compiler output
                        string details = "";
                        try
                        {
                            details = (await _sshTunnel.RunCommandAsync(
                                $"journalctl -u {instance.Service} --since '-5 min' --no-pager | grep -A4 -E 'compilation problem \\(Path: Custom/Robots/(.*/)?{className}\\.cs' | tail -5 || true")
                                .ConfigureAwait(true)).Trim();
                        }
                        catch
                        {
                            // the journal is only a hint
                        }

                        AppendLog($"Robot {className}: DOES NOT COMPILE — {ex.Message}" + (details.Length > 0 ? "\n" + details : ""));
                        pending.Remove(className);
                    }
                    catch
                    {
                        // terminal still starting — try again
                        break;
                    }
                }
            }

            foreach (string className in pending)
            {
                AppendLog($"Robot {className}: could not check — the terminal did not answer in time");
            }
        }

        private async void ButtonCleanLogs_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedOrOnlyInstance();
            if (instance == null) return;

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Clean the logs of terminal \"{instance.Title}\"?\n\nIts OsEngine log files are deleted except today's; "
                + "the system journal of the VPS keeps the last 7 days. Robots keep working.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                string report = await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .CleanLogsAsync(instance, CancellationToken.None)).ConfigureAwait(true);
                AppendLog($"Logs of terminal \"{instance.Title}\" cleaned: {report}");
            }).ConfigureAwait(true);
        }

        private async void ButtonRebootVps_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Reboot the whole VPS {TextBoxSshHost.Text.Trim()}?\n\nAll terminals and their robots stop and start again with the "
                + "server, usually within 1–2 minutes. The connection comes back by itself.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread).RebootAsync(CancellationToken.None)).ConfigureAwait(true);
                AppendLog("VPS is rebooting — the connection comes back by itself in 1–2 minutes");
            }).ConfigureAwait(true);
        }

        #endregion

        #region Monitoring (CPU / RAM / disk of the VPS, per-terminal load) and data backups

        private VpsMonitor _monitor = new VpsMonitor();
        private readonly Dictionary<string, double> _terminalCpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // CPU alert only after a whole minute above the limit (6 samples x 10 s): short spikes are normal
        private readonly VpsAlarm _cpuAlarm = new VpsAlarm(90, 6);
        private readonly VpsAlarm _ramAlarm = new VpsAlarm(85, 1);
        private readonly VpsAlarm _diskAlarm = new VpsAlarm(90, 1);

        private bool _backupRunning;

        private async Task SampleMetricsAsync(SshTunnel tunnel, List<VpsInstance> instances)
        {
            VpsMetrics metrics;

            try
            {
                metrics = await _monitor.SampleAsync(tunnel.RunCommandAsync).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                TextBlockVpsInfo.Text = "Monitoring: " + ex.Message;
                return;
            }

            foreach (VpsInstance instance in instances)
            {
                _terminalCpu[instance.Name] = _monitor.ServiceCpuPercent(instance.Service, instance.CpuNanoseconds, metrics.Cores);
            }

            SparklineCpu.Add(metrics.CpuPercent);
            SparklineRam.Add(metrics.RamPercent);
            SparklineDisk.Add(metrics.DiskPercent);

            TextBlockCpu.Text = double.IsNaN(metrics.CpuPercent)
                ? $"CPU: — ({metrics.Cores} cores)"
                : $"CPU: {metrics.CpuPercent:0} % ({metrics.Cores} cores, load {metrics.Load1.ToString("0.00", CultureInfo.InvariantCulture)})";
            TextBlockRam.Text = $"RAM: {metrics.RamPercent:0} % ({VpsMonitor.FormatBytes(metrics.RamUsed)} of {VpsMonitor.FormatBytes(metrics.RamTotal)})"
                + (metrics.SwapTotal > 0 ? $", swap {VpsMonitor.FormatBytes(metrics.SwapUsed)}" : "");
            TextBlockDisk.Text = $"Disk: {metrics.DiskPercent:0} % ({VpsMonitor.FormatBytes(metrics.DiskUsed)} of {VpsMonitor.FormatBytes(metrics.DiskTotal)})";
            TextBlockVpsInfo.Text = "Uptime: " + (metrics.Uptime.TotalDays >= 1
                ? $"{(int)metrics.Uptime.TotalDays} d {metrics.Uptime.Hours} h"
                : $"{metrics.Uptime.Hours} h {metrics.Uptime.Minutes} min");

            if (_cpuAlarm.Check(metrics.CpuPercent))
                RaiseVpsAlert($"CPU load {metrics.CpuPercent:0} % for over a minute (limit {_cpuAlarm.Limit:0} %)");
            if (_ramAlarm.Check(metrics.RamPercent))
                RaiseVpsAlert($"Memory {metrics.RamPercent:0} % used: {VpsMonitor.FormatBytes(metrics.RamUsed)} of {VpsMonitor.FormatBytes(metrics.RamTotal)} (limit {_ramAlarm.Limit:0} %)");
            if (_diskAlarm.Check(metrics.DiskPercent))
                RaiseVpsAlert($"Disk {metrics.DiskPercent:0} % full: {VpsMonitor.FormatBytes(metrics.DiskTotal - metrics.DiskUsed)} free (limit {_diskAlarm.Limit:0} %) — clean the logs");
        }

        private void RaiseVpsAlert(string message)
        {
            string source = "VPS " + TextBoxSshHost.Text.Trim();
            AlertMessageManager.ThrowRemoteAlert(source, message, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            AppendLog("ALERT: " + message);
        }

        private void ResetMonitoring()
        {
            _monitor = new VpsMonitor();
            _terminalCpu.Clear();
            SparklineCpu.Clear();
            SparklineRam.Clear();
            SparklineDisk.Clear();
            TextBlockCpu.Text = "CPU: —";
            TextBlockRam.Text = "RAM: —";
            TextBlockDisk.Text = "Disk: —";
            TextBlockVpsInfo.Text = "Uptime: —";
        }

        private void CheckBoxDailyBackup_Click(object sender, RoutedEventArgs e) => SaveSettings();

        // While connected: one running terminal per 10 s tick whose newest local backup is older than a day.
        private void RunDailyBackupIfDue(List<VpsInstance> instances)
        {
            if (CheckBoxDailyBackup.IsChecked != true || _backupRunning)
            {
                return;
            }

            string host = TextBoxSshHost.Text.Trim();

            VpsInstance due = instances.FirstOrDefault(instance =>
            {
                if (!instance.IsActive) return false;
                string folder = VpsProvisioner.BackupFolder(host, instance.Name);
                DateTime newest = Directory.Exists(folder)
                    ? new DirectoryInfo(folder).GetFiles("*.tgz").Select(f => f.LastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max()
                    : DateTime.MinValue;
                return DateTime.Now - newest > TimeSpan.FromDays(1);
            });

            if (due != null)
            {
                _ = BackupTerminalAsync(due, "Daily backup");
            }
        }

        private async Task BackupTerminalAsync(VpsInstance instance, string what)
        {
            if (_backupRunning) return;
            _backupRunning = true;

            try
            {
                VpsSshCredentials credentials = CreateCredentials();
                AppendLog($"{what} of terminal \"{instance.Title}\"...");
                string file = await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .BackupAsync(instance, CancellationToken.None)).ConfigureAwait(true);
                AppendLog($"{what} of terminal \"{instance.Title}\" saved: {file} ({new FileInfo(file).Length / 1024} KB)");
            }
            catch (Exception ex)
            {
                AppendLog($"{what} of terminal \"{instance.Title}\" failed: {ex.Message}");
            }
            finally
            {
                _backupRunning = false;
            }
        }

        private async void ButtonBackupData_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedOrOnlyInstance();
            if (instance == null) return;

            if (_backupRunning)
            {
                MessageBox.Show("A backup is already running");
                return;
            }

            PanelBackupButtons.IsEnabled = false;

            try
            {
                await BackupTerminalAsync(instance, "Backup").ConfigureAwait(true);
            }
            finally
            {
                PanelBackupButtons.IsEnabled = true;
            }
        }

        private async void ButtonRestoreData_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedOrOnlyInstance();
            if (instance == null) return;

            string folder = VpsProvisioner.BackupFolder(TextBoxSshHost.Text.Trim(), instance.Name);
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Backup to restore into terminal \"{instance.Title}\"",
                Filter = "OsEngine data backup (*.tgz)|*.tgz",
                InitialDirectory = Directory.Exists(folder) ? folder : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VpsBackups")
            };

            if (dialog.ShowDialog() != true) return;
            string archive = dialog.FileName;

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Restore terminal \"{instance.Title}\" from\n{Path.GetFileName(archive)}?\n\n"
                + "The terminal is stopped, its data (bots, settings, journals, robot scripts, MCP key) is replaced with the "
                + "backup and it is started again. The current data is NOT deleted: it stays on the VPS as "
                + "data.before-restore-<time>. Open positions of its robots are only what the backup contains.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                AppendLog($"=== Restore terminal \"{instance.Title}\" from {Path.GetFileName(archive)} ===");
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .RestoreAsync(instance, archive, CancellationToken.None)).ConfigureAwait(true);

                // the MCP key may have changed with the data: reconnect this terminal with the key now on the VPS
                if (_terminals.TryGetValue(instance.Name, out TerminalConnection terminal))
                {
                    DropTerminal(terminal);
                    PublishSession();
                }
            }).ConfigureAwait(true);
        }

        #endregion

        #region Small helpers

        private void AppendLog(string message)
        {
            DateTime time = DateTime.Now;
            _log.Insert(0, new LogRow { Time = time, Message = message });
            Logged?.Invoke(_profileId, time, message);

            while (_log.Count > 500)
            {
                _log.RemoveAt(_log.Count - 1);
            }

            ServerMaster.SendNewLogMessage("RobotsVps: " + message, LogMessageType.System);
        }

        #endregion
    }

    /// <summary>how the update of one VPS ended in "Update all"</summary>
    public enum VpsUpdateOutcome
    {
        /// <summary>not connected by SSH of this program: nothing to do</summary>
        Skipped,
        /// <summary>every running terminal already runs the local build (or none runs)</summary>
        NothingToDo,
        /// <summary>the user did not confirm this VPS</summary>
        Declined,
        Done,
        Failed
    }

    public class LogRow
    {
        public DateTime Time { get; set; }
        public string Message { get; set; }
    }

    public class TerminalRow
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public int Port { get; set; }
        public string State { get; set; }
        public string Memory { get; set; }
        public string Build { get; set; }
        public string Cpu { get; set; }
        public string Connection { get; set; }
    }
}
