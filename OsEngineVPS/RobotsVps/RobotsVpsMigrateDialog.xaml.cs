using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // Where to move a terminal and under which name; the checks are the ones of VpsMigration.Check
    public partial class RobotsVpsMigrateDialog : Window
    {
        private readonly string _sourceKey;
        private readonly List<VpsProfile> _targets;

        public string TargetVpsId { get; private set; }
        public string TargetName { get; private set; }

        public RobotsVpsMigrateDialog(string sourceKey)
        {
            InitializeComponent();
            _sourceKey = sourceKey;

            VpsRemoteSession.SplitKey(sourceKey, out string sourceVps, out string terminal);
            VpsInstance source = VpsRemoteSession.InstanceOf(sourceKey);
            string title = source?.Title ?? terminal;

            TextBlockIntro.Text = $"Move the terminal \"{title}\" of VPS \"{VpsProfiles.NameOf(sourceVps)}\" to another VPS. Its robots, bots, settings, journals, "
                + "connectors and keys go with it (the data is carried through this computer).";

            _targets = VpsProfiles.All.Where(p => p.Id != sourceVps).ToList();

            foreach (VpsProfile profile in _targets)
            {
                bool connected = VpsRemoteSession.SshOfVps(profile.Id) != null;
                ComboBoxTarget.Items.Add(profile.Name + (connected ? "" : "  (not connected over SSH)"));
            }

            if (_targets.Count > 0) ComboBoxTarget.SelectedIndex = 0;

            // the name on the target: the same technical name; the main terminal (taken there) gets its shown name
            string suggestion = string.Equals(terminal, VpsRemoteSession.MainInstance, System.StringComparison.OrdinalIgnoreCase)
                ? new string(title.ToLowerInvariant().Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-').ToArray())
                : terminal;
            TextBoxName.Text = VpsInstances.IsValidName(suggestion) ? suggestion : "";

            TextBlockSteps.Text = "What happens: 1) a new empty terminal is created on the target; 2) the source is stopped (its robots stop managing positions); "
                + "3) its data is packed and downloaded; 4) it is put into the new terminal, which starts; 5) the source is left stopped with its autostart OFF "
                + "(its data is kept). If something fails before the data moved, the source is started again.\n\n"
                + "Exchange keys that are bound to an IP address (Binance): add the IP of the target VPS to the key before you migrate, otherwise the "
                + "connector there will not connect.";

            if (_targets.Count == 0)
            {
                TextBlockError.Text = "There is no other VPS: add one with \"+\" in Settings first.";
                ButtonOk.IsEnabled = false;
            }

            ButtonOk.Click += (s, e) =>
            {
                if (ComboBoxTarget.SelectedIndex < 0) return;

                MigrationPlan plan = new MigrationPlan
                {
                    SourceKey = _sourceKey,
                    TargetVpsId = _targets[ComboBoxTarget.SelectedIndex].Id,
                    TargetName = TextBoxName.Text.Trim().ToLowerInvariant()
                };

                string error = VpsMigration.Check(plan);

                if (error != null)
                {
                    TextBlockError.Text = error;
                    return;
                }

                TargetVpsId = plan.TargetVpsId;
                TargetName = plan.TargetName;
                DialogResult = true;
            };
        }
    }
}
