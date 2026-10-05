using System;
using System.Windows;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // one line of text: a new name (of a terminal or of a VPS) with an explanation and a check of the entered name
    public partial class RobotsVpsRenameDialog : Window
    {
        private readonly Func<string, string> _check;

        public string NewName { get; private set; }

        // the new shown name of a terminal
        public RobotsVpsRenameDialog(string currentName, string technicalName)
            : this("Rename", "New name of the terminal \"" + technicalName + "\" (1-30 characters). Only the shown name changes: "
                + "the service on the VPS, its folder and the connection name for AI agents stay as they are.",
                currentName,
                name => VpsInstances.IsValidDisplayName(name) ? null : "1-30 characters, without '=', '|' and line breaks")
        {
        }

        // title: the window title; hint: the explanation above the field; check: returns the error text or null
        public RobotsVpsRenameDialog(string title, string hint, string initial, Func<string, string> check)
        {
            InitializeComponent();
            _check = check;
            Title = title;
            TextBlockHint.Text = hint;
            TextBoxName.Text = initial;
            TextBoxName.SelectAll();
            Loaded += (s, e) => TextBoxName.Focus();

            ButtonOk.Click += (s, e) =>
            {
                string name = TextBoxName.Text.Trim();
                string error = _check(name);

                if (error != null)
                {
                    TextBlockError.Text = error;
                    return;
                }

                NewName = name;
                DialogResult = true;
            };
        }
    }
}
