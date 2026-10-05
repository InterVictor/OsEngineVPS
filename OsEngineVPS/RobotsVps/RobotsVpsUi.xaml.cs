/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OsEngine.Entity;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Settings of OsEngineVPS: the part common to all VPS on top (the list of VPS with their state, the MCP access of AI agents, the
    /// build on this computer), a tab per VPS below (VpsConnectionPanel: SSH, terminals, maintenance, monitoring, backups), and the
    /// common event log. The last tab "+" adds a VPS; the context menu of a tab renames or removes it. Closing the window only hides
    /// it: the connections of all VPS stay up.
    /// Настройки OsEngineVPS: вверху общее для всех VPS, ниже вкладки VPS, внизу общий журнал.
    /// </summary>
    public partial class RobotsVpsUi : Window
    {
        private readonly Dictionary<string, VpsConnectionPanel> _panels = new Dictionary<string, VpsConnectionPanel>();
        private readonly Dictionary<string, TabItem> _tabs = new Dictionary<string, TabItem>();
        private readonly ObservableCollection<CommonLogRow> _log = new ObservableCollection<CommonLogRow>();
        private readonly ObservableCollection<VpsRow> _rows = new ObservableCollection<VpsRow>();
        private TabItem _addTab;
        private TabItem _previousTab;
        private bool _handlingAddTab;

        public RobotsVpsUi()
        {
            InitializeComponent();

            LogDataGrid.ItemsSource = _log;
            DataGridVps.ItemsSource = _rows;

            // the MCP access folders of the single-VPS file are taken over first
            _ = VpsMcpAccess.Folders;

            foreach (VpsProfile profile in VpsProfiles.All)
            {
                AddTab(profile);
            }

            _addTab = new TabItem { Header = "+", ToolTip = "Add a VPS" };
            TabControlVps.Items.Add(_addTab);
            TabControlVps.SelectedIndex = 0;
            _previousTab = TabControlVps.SelectedItem as TabItem;
            TabControlVps.SelectionChanged += TabControlVps_SelectionChanged;

            VpsConnectionPanel.Logged += OnPanelLogged;
            VpsMcpAccess.Logged += message => Dispatcher.BeginInvoke(new Action(() => AddLog("", DateTime.Now, message)));

            TextBlockMcpAccess.Text = VpsMcpAccess.Text();
            UpdatePackageText();
            RefreshOverview();

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) => RefreshOverview();
            timer.Start();

            Closing += (s, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }

        // ---- what the main window needs ----

        public void ShutdownConnection()
        {
            foreach (VpsConnectionPanel panel in _panels.Values.ToList())
            {
                panel.ShutdownConnection();
            }
        }

        /// <summary>true if the "connect automatically on program start" box is ticked for at least one VPS</summary>
        public static bool IsAutoConnectOnStartupEnabled() =>
            VpsProfiles.All.Any(p => VpsConnectionPanel.IsAutoConnectOnStartupEnabled(p.Id));

        public void StartAutomaticConnect()
        {
            foreach (VpsConnectionPanel panel in _panels.Values.Where(p => p.AutoConnect).ToList())
            {
                panel.StartAutomaticConnect();
            }
        }

        /// <summary>renames a terminal by its key (see VpsRemoteSession.Key); the SSH of its VPS must be connected</summary>
        public Task RenameTerminalAsync(string key)
        {
            VpsRemoteSession.SplitKey(key, out string vpsId, out string terminal);

            return _panels.TryGetValue(vpsId, out VpsConnectionPanel panel)
                ? panel.RenameTerminalAsync(terminal)
                : Task.CompletedTask;
        }

        // ---- the tabs of the VPS ----

        private void AddTab(VpsProfile profile)
        {
            VpsConnectionPanel panel = new VpsConnectionPanel(profile.Id);
            ScrollViewer scroll = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                // the panel takes the width of the window (a horizontal scroll would give its tables an endless width)
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            TabItem tab = new TabItem { Header = profile.Name, Content = scroll, Tag = profile.Id };

            MenuItem rename = new MenuItem { Header = "Rename..." };
            rename.Click += (s, e) => RenameVps(profile.Id);
            MenuItem remove = new MenuItem { Header = "Remove from the list..." };
            remove.Click += (s, e) => RemoveVps(profile.Id);
            tab.ContextMenu = new ContextMenu { Items = { rename, remove } };

            _panels[profile.Id] = panel;
            _tabs[profile.Id] = tab;

            int index = _addTab == null ? TabControlVps.Items.Count : TabControlVps.Items.IndexOf(_addTab);
            TabControlVps.Items.Insert(index, tab);
        }

        private void TabControlVps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != TabControlVps || _handlingAddTab) return;

            if (TabControlVps.SelectedItem == _addTab)
            {
                _handlingAddTab = true;

                try
                {
                    VpsProfile created = AddVps();
                    TabControlVps.SelectedItem = created != null && _tabs.TryGetValue(created.Id, out TabItem tab) ? tab : _previousTab;
                }
                finally
                {
                    _handlingAddTab = false;
                }
            }

            _previousTab = TabControlVps.SelectedItem as TabItem;
        }

        private VpsProfile AddVps()
        {
            RobotsVpsRenameDialog dialog = new RobotsVpsRenameDialog("New VPS",
                "Name of the new VPS, for example the name of the provider (1-30 characters). Its address and login are entered in its tab.",
                string.Empty, name => VpsProfiles.CheckName(name, null)) { Owner = this };

            if (dialog.ShowDialog() != true) return null;

            try
            {
                VpsProfile profile = VpsProfiles.Add(dialog.NewName);
                AddTab(profile);
                AddLog("", DateTime.Now, $"VPS \"{profile.Name}\" added; enter its address and connect");
                RefreshOverview();
                VpsRemoteSession.RaiseInstancesChanged();
                return profile;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        private void RenameVps(string id)
        {
            RobotsVpsRenameDialog dialog = new RobotsVpsRenameDialog("Rename the VPS",
                "New name of the VPS (1-30 characters), for example the name of the provider. It is the name on this computer only: nothing changes on the server.",
                VpsProfiles.NameOf(id), name => VpsProfiles.CheckName(name, id)) { Owner = this };

            if (dialog.ShowDialog() != true) return;

            try
            {
                string old = VpsProfiles.NameOf(id);
                VpsProfiles.Rename(id, dialog.NewName);
                _tabs[id].Header = dialog.NewName;
                AddLog("", DateTime.Now, $"VPS \"{old}\" is called \"{dialog.NewName}\" now");
                RefreshOverview();
                VpsRemoteSession.RaiseInstancesChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RemoveVps(string id)
        {
            if (_panels.Count <= 1)
            {
                MessageBox.Show(this, "The last VPS cannot be removed from the list.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string name = VpsProfiles.NameOf(id);
            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Remove VPS \"{name}\" from the list?\n\nThe connection to it is closed and its settings on this computer (address, user, saved password) are deleted. "
                + "The server and its terminals are NOT touched: they keep running.");
            confirm.ShowDialog();

            if (!confirm.UserAcceptAction) return;

            try
            {
                _panels[id].ShutdownConnection();
                VpsProfiles.Remove(id);
                VpsMcpAccess.RemoveVps(id);
                TabControlVps.Items.Remove(_tabs[id]);
                _tabs.Remove(id);
                _panels.Remove(id);
                _previousTab = _tabs.Values.FirstOrDefault();
                TabControlVps.SelectedItem = _previousTab;
                AddLog("", DateTime.Now, $"VPS \"{name}\" removed from the list (the server was not touched)");
                RefreshOverview();
                VpsRemoteSession.RaiseInstancesChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---- the common part ----

        private void RefreshOverview()
        {
            _rows.Clear();

            foreach (VpsProfile profile in VpsProfiles.All)
            {
                if (!_panels.TryGetValue(profile.Id, out VpsConnectionPanel panel)) continue;

                _rows.Add(new VpsRow
                {
                    Name = profile.Name,
                    Host = string.IsNullOrEmpty(panel.Host) ? "—" : panel.Host,
                    Status = panel.StatusText,
                    Terminals = panel.ConnectedTerminalCount,
                    Auto = panel.AutoConnect ? "yes" : ""
                });
            }
        }

        private void ButtonMcpAccess_Click(object sender, RoutedEventArgs e)
        {
            RobotsVpsMcpAccessUi window = new RobotsVpsMcpAccessUi(VpsMcpAccess.Folders, () =>
            {
                VpsMcpAccess.Save();
                TextBlockMcpAccess.Text = VpsMcpAccess.Text();
                VpsMcpAccess.UpdateAll();
            }) { Owner = this };
            window.ShowDialog();
        }

        private void UpdatePackageText()
        {
            try
            {
                string version = VpsProvisioner.LocalPackageVersion();
                string path = VpsProvisioner.LocalPath(VpsProvisioner.PackageFileName);

                TextBlockPackage.Text = version == null
                    ? "No build package in the VpsServer folder: \"Update build\" is not available"
                    : $"Build package on this computer: {version} ({File.GetLastWriteTime(path):dd.MM.yyyy HH:mm})";
            }
            catch (Exception ex)
            {
                TextBlockPackage.Text = "Build package: " + ex.Message;
            }
        }

        // ---- the common log ----

        private void OnPanelLogged(string vpsId, DateTime time, string message) =>
            Dispatcher.BeginInvoke(new Action(() => AddLog(VpsProfiles.NameOf(vpsId), time, message)));

        private void AddLog(string vps, DateTime time, string message)
        {
            _log.Insert(0, new CommonLogRow { Time = time, Vps = vps, Message = message });

            while (_log.Count > 1000)
            {
                _log.RemoveAt(_log.Count - 1);
            }
        }

        public sealed class CommonLogRow
        {
            public DateTime Time { get; set; }
            public string Vps { get; set; }
            public string Message { get; set; }
        }

        public sealed class VpsRow
        {
            public string Name { get; set; }
            public string Host { get; set; }
            public string Status { get; set; }
            public int Terminals { get; set; }
            public string Auto { get; set; }
        }
    }
}
