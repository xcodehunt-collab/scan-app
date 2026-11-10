using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScanApp
{
    internal static class Autostart
    {
        private const string TaskName = "SystemIntegrityCheck";
        private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
        private const string ConfigFileName = "autostart.txt";
        private const string LogFileName = "autostart.log";

        private static bool _explorerLaunched;

        internal static string ExePath
        {
            get { return Environment.ProcessPath ?? Application.ExecutablePath; }
        }

        private static string WorkDir
        {
            get
            {
                var dir = Path.GetDirectoryName(ExePath);
                return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
            }
        }

        // ================= public entry points =================

        /// <summary>Installs autostart if it is not already installed. Returns true if this
        /// instance should exit because an elevated relaunch was started.</summary>
        internal static bool EnsureInstalled()
        {
            if (!Flag("enabled", true)) { Log("disabled by config"); return false; }

            if (TaskExists())
            {
                if (Flag("shell", true)) EnsureShell();
                return false;
            }

            if (!IsAdmin())
            {
                if (RelaunchElevated("--install-autostart")) return true;
                Log("install skipped: not elevated");
                return false;
            }

            if (CreateTask())
            {
                Log("task created");
                if (Flag("shell", true)) EnsureShell();
            }
            else
            {
                Log("task creation failed");
            }
            return false;
        }

        /// <summary>Removes the task and gives the shell back. Caller should be elevated.</summary>
        internal static void Uninstall()
        {
            DeleteTask();
            if (OwnsShell())
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(WinlogonKey, true))
                        if (key != null) key.SetValue("Shell", "explorer.exe", RegistryValueKind.String);
                }
                catch (Exception ex) { Log("shell restore failed: " + ex.Message); }
            }
            LaunchExplorer();
            Log("uninstalled");
        }

        /// <summary>Called when the app closes normally: brings the desktop back for this
        /// session but leaves the registry value so the next boot is still kiosk.</summary>
        internal static void OnExit()
        {
            if (!Flag("shell", true)) return;
            if (!OwnsShell()) return;
            LaunchExplorer();
        }

        // ================= elevation =================

        internal static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        internal static bool RelaunchElevated(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(ExePath, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = WorkDir
                };
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        // ================= scheduled task =================

        private static bool TaskExists()
        {
            return Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"");
        }

        private static void DeleteTask()
        {
            Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F");
        }

        private static bool CreateTask()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "sic-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(tmp, BuildXml(), Encoding.Unicode);
                return Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
            }
            catch (Exception ex)
            {
                Log("xml write failed: " + ex.Message);
                return false;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static bool Run(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log(file + " failed: " + ex.Message);
                return false;
            }
        }

        private static string BuildXml()
        {
            bool admin = IsAdmin();
            int delay = DelaySeconds;

            string principal = admin
                ? "<GroupId>S-1-5-32-544</GroupId>"
                : "<UserId>" + SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) + "</UserId>";

            string delayXml = delay > 0
                ? "<Delay>PT" + delay + "S</Delay>"
                : "";

            string watchdogXml = Flag("watchdog", true)
                ? "<TimeTrigger>" +
                      "<StartBoundary>" + DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss") + "</StartBoundary>" +
                      "<Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
                      "<Enabled>true</Enabled>" +
                  "</TimeTrigger>"
                : "";

            return
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo>\r\n" +
"    <Author>" + SecurityElement.Escape(Environment.UserName) + "</Author>\r\n" +
"    <Description>System Integrity Check</Description>\r\n" +
"  </RegistrationInfo>\r\n" +
"  <Triggers>\r\n" +
"    <LogonTrigger><Enabled>true</Enabled>" + delayXml + "</LogonTrigger>\r\n" +
        watchdogXml + "\r\n" +
"  </Triggers>\r\n" +
"  <Principals>\r\n" +
"    <Principal id=\"Author\">\r\n" +
"      " + principal + "\r\n" +
"      <RunLevel>" + (admin ? "HighestAvailable" : "LeastPrivilege") + "</RunLevel>\r\n" +
"    </Principal>\r\n" +
"  </Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <RestartOnFailure><Interval>PT10S</Interval><Count>999</Count></RestartOnFailure>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd></IdleSettings>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\">\r\n" +
"    <Exec>\r\n" +
"      <Command>" + SecurityElement.Escape(ExePath) + "</Command>\r\n" +
"      <WorkingDirectory>" + SecurityElement.Escape(WorkDir) + "</WorkingDirectory>\r\n" +
"    </Exec>\r\n" +
"  </Actions>\r\n" +
"</Task>\r\n";
        }

        // ================= shell replacement =================

        private static void EnsureShell()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(WinlogonKey, true))
                {
                    if (key == null) return;
                    var current = key.GetValue("Shell") as string;

                    if (!string.IsNullOrEmpty(current) &&
                        current.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0) return;

                    // don't steal the shell from a third-party one
                    if (!string.IsNullOrEmpty(current) &&
                        !current.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return;

                    key.SetValue("Shell", "\"" + ExePath + "\"", RegistryValueKind.String);
                    Log("shell replaced");
                }
            }
            catch (Exception ex) { Log("shell set failed: " + ex.Message); }
        }

        private static bool OwnsShell()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(WinlogonKey, false))
                {
                    var current = key == null ? null : key.GetValue("Shell") as string;
                    return !string.IsNullOrEmpty(current) &&
                           current.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static void LaunchExplorer()
        {
            if (_explorerLaunched) return;
            _explorerLaunched = true;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            catch (Exception ex) { Log("explorer launch failed: " + ex.Message); }
        }

        // ================= config =================

        private static string ReadSetting(string key, string fallback)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
                if (!File.Exists(path)) return fallback;

                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                    var value = line.Substring(eq + 1).Trim();
                    return value.Length > 0 ? value : fallback;
                }
            }
            catch { }
            return fallback;
        }

        private static bool Flag(string key, bool fallback)
        {
            var v = ReadSetting(key, fallback ? "1" : "0");
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                             || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int DelaySeconds
        {
            get
            {
                int d;
                if (!int.TryParse(ReadSetting("delay_seconds", "15"), out d)) return 15;
                return Math.Max(0, Math.Min(600, d));
            }
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, LogFileName),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
