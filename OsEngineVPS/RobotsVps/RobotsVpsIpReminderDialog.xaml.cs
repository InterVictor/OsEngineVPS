using System;
using System.Windows;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // The reminder before a migration: the IP address of the target VPS has to be in the exchange key (Binance binds keys to IP
    // addresses). "Continue" works only after the user ticks that it was done.
    public partial class RobotsVpsIpReminderDialog : Window
    {
        public RobotsVpsIpReminderDialog(string targetVpsName, string ipAddress)
        {
            InitializeComponent();
            TextBlockTarget.Text = $"Target VPS \"{targetVpsName}\", its IP address:";
            TextBoxIp.Text = ipAddress;

            ButtonCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(ipAddress);
                    ButtonCopy.Content = "Copied";
                }
                catch (Exception)
                {
                    TextBoxIp.SelectAll();
                    TextBoxIp.Focus();
                }
            };

            CheckBoxDone.Click += (s, e) => ButtonContinue.IsEnabled = CheckBoxDone.IsChecked == true;
            ButtonContinue.Click += (s, e) => DialogResult = true;
        }
    }
}
