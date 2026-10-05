using System.Windows;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // one line of text: the new shown name of a terminal
    public partial class RobotsVpsRenameDialog : Window
    {
        public string NewName { get; private set; }

        public RobotsVpsRenameDialog(string currentName, string technicalName)
        {
            InitializeComponent();
            TextBlockHint.Text = "New name of the terminal \"" + technicalName + "\" (1-30 characters). Only the shown name changes: "
                + "the service on the VPS, its folder and the connection name for AI agents stay as they are.";
            TextBoxName.Text = currentName;
            TextBoxName.SelectAll();
            Loaded += (s, e) => TextBoxName.Focus();

            ButtonOk.Click += (s, e) =>
            {
                string name = TextBoxName.Text.Trim();

                if (!VpsInstances.IsValidDisplayName(name))
                {
                    TextBlockError.Text = "1-30 characters, without '=', '|' and line breaks";
                    return;
                }

                NewName = name;
                DialogResult = true;
            };
        }
    }
}
