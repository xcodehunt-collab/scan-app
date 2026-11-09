using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace ScanApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new Form
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
            form.Controls.Add(web);

            form.KeyDown += (s, e) =>
            {
                if (e.Control && e.Shift && e.KeyCode == Keys.Q) form.Close();
                if (e.Control && e.Shift && e.KeyCode == Keys.R) web.Reload();
            };

            form.Shown += async (s, e) =>
            {
                await web.EnsureCoreWebView2Async();
                var cfg = web.CoreWebView2.Settings;
                cfg.AreDefaultContextMenusEnabled = false;
                cfg.AreDevToolsEnabled = false;
                cfg.IsStatusBarEnabled = false;
                cfg.IsZoomControlEnabled = false;
                cfg.AreBrowserAcceleratorKeysEnabled = false;

                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("ScanApp.index.html"))
                using (var reader = new StreamReader(stream))
                    web.NavigateToString(reader.ReadToEnd());
            };

            Application.Run(form);
        }
    }
}