using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace ScanApp
{
    internal static class Program
    {
        private const string DefaultPhone = "1-800-555-0199";
        private const string ConfigFileName = "support.txt";

        private static string _phone = DefaultPhone;
        private static NotifyIcon _tray;
        private static Form _form;
        private static bool _notified;

        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _phone = LoadPhone();

            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = Screen.PrimaryScreen.Bounds,
                WindowState = FormWindowState.Maximized,
                BackColor = Color.FromArgb(0, 120, 215),
                Text = "System Integrity Check",
                KeyPreview = true
            };

            var web = new WebView2 { Dock = DockStyle.Fill };
            _form.Controls.Add(web);

            BuildTray();

            _form.KeyDown += (s, e) =>
            {
                if (e.Control && e.Shift && e.KeyCode == Keys.Q) _form.Close();
                if (e.Control && e.Shift && e.KeyCode == Keys.R) web.Reload();
            };

            _form.FormClosing += (s, e) =>
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            };

            _form.Shown += async (s, e) =>
            {
                await web.EnsureCoreWebView2Async();
                var cfg = web.CoreWebView2.Settings;
                cfg.AreDefaultContextMenusEnabled = false;
                cfg.AreDevToolsEnabled = false;
                cfg.IsStatusBarEnabled = false;
                cfg.IsZoomControlEnabled = false;
                cfg.AreBrowserAcceleratorKeysEnabled = false;

                web.CoreWebView2.WebMessageReceived += (ms, me) =>
                {
                    string msg;
                    try { msg = me.TryGetWebMessageAsString(); } catch { return; }
                    if (msg == "scan-complete") ShowAlert();
                };

                string html;
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("ScanApp.index.html"))
                using (var reader = new StreamReader(stream))
                    html = reader.ReadToEnd().Replace("{{PHONE}}", _phone);

                web.NavigateToString(html);
            };

            Application.Run(_form);
        }

        private static string LoadPhone()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch { }
            return DefaultPhone;
        }

        private static void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Error,
                Text = "System Integrity Check",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show support number", null, (s, e) => ShowNumber());
            menu.Items.Add("Copy number", null, (s, e) => CopyNumber());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Restart scan", null, (s, e) => _form.Close());
            menu.Items.Add("Exit", null, (s, e) => Application.Exit());

            _tray.ContextMenuStrip = menu;
            _tray.BalloonTipClicked += (s, e) => ShowNumber();
        }

        private static void ShowAlert()
        {
            if (_notified) return;
            _notified = true;

            _tray.BalloonTipTitle = "System Integrity Check";
            _tray.BalloonTipText = "42 faults could not be repaired automatically. Call support: " + _phone;
            _tray.BalloonTipIcon = ToolTipIcon.Error;
            _tray.ShowBalloonTip(10000);
        }

        private static void CopyNumber()
        {
            try { Clipboard.SetText(_phone); } catch { }
        }

        private static void ShowNumber()
        {
            CopyNumber();
            MessageBox.Show(_form,
                "Support number (copied to clipboard):\n\n" + _phone,
                "Support", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
