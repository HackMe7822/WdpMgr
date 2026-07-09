using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClearDisplayAffinity
{
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

        [DllImport("user32.dll")]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }

    internal static class Program
    {
        internal const string TaskName = "ClearDisplayAffinity";
        internal const uint WDA_NONE = 0x0;

        internal static string InstallPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClearDisplayAffinity");
                return Path.Combine(dir, "ClearDisplayAffinity.exe");
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--service-loop")
            {
                RunServiceLoop();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void RunServiceLoop()
        {
            while (true)
            {
                try
                {
                    Native.EnumWindows((hWnd, lParam) =>
                    {
                        uint affinity;
                        if (Native.GetWindowDisplayAffinity(hWnd, out affinity) && affinity != WDA_NONE)
                        {
                            Native.SetWindowDisplayAffinity(hWnd, WDA_NONE);
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                catch
                {
                    // keep looping even if a single pass fails
                }

                Thread.Sleep(1000);
            }
        }

        internal static Tuple<int, string> RunHidden(string exe, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return Tuple.Create(p.ExitCode, stdout + stderr);
            }
        }

        internal static bool IsInstalled()
        {
            var result = RunHidden("schtasks.exe", string.Format("/query /tn \"{0}\"", TaskName));
            return result.Item1 == 0;
        }
    }

    internal class MainForm : Form
    {
        private readonly Label _status;
        private readonly Button _installBtn;
        private readonly Button _uninstallBtn;

        public MainForm()
        {
            Text = "Clear Display Affinity";
            Width = 380;
            Height = 180;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var title = new Label
            {
                Text = "Removes screen-capture-exclusion from windows",
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 40,
            };

            _status = new Label
            {
                Text = "Checking status...",
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 30,
            };

            var panel = new Panel { Dock = DockStyle.Fill };

            _installBtn = new Button { Text = "Install", Width = 140, Height = 36 };
            _uninstallBtn = new Button { Text = "Uninstall", Width = 140, Height = 36 };
            _installBtn.Location = new System.Drawing.Point(30, 30);
            _uninstallBtn.Location = new System.Drawing.Point(190, 30);
            _installBtn.Click += InstallBtn_Click;
            _uninstallBtn.Click += UninstallBtn_Click;

            panel.Controls.Add(_installBtn);
            panel.Controls.Add(_uninstallBtn);

            Controls.Add(panel);
            Controls.Add(_status);
            Controls.Add(title);

            Load += (s, e) => RefreshStatus();
        }

        private void RefreshStatus()
        {
            _status.Text = Program.IsInstalled()
                ? "Status: installed, running in background"
                : "Status: not installed";
        }

        private void InstallBtn_Click(object sender, EventArgs e)
        {
            try
            {
                string installPath = Program.InstallPath;
                string installDir = Path.GetDirectoryName(installPath);
                Directory.CreateDirectory(installDir);

                string selfPath = Application.ExecutablePath;
                if (!string.Equals(Path.GetFullPath(selfPath), Path.GetFullPath(installPath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(selfPath, installPath, true);
                }

                string createArgs = string.Format(
                    "/create /tn \"{0}\" /tr \"\\\"{1}\\\" --service-loop\" /sc onlogon /rl highest /f",
                    Program.TaskName, installPath);

                var createResult = Program.RunHidden("schtasks.exe", createArgs);
                if (createResult.Item1 != 0)
                {
                    MessageBox.Show(this, "Failed to create scheduled task:\n" + createResult.Item2,
                        "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Program.RunHidden("schtasks.exe", string.Format("/run /tn \"{0}\"", Program.TaskName));

                MessageBox.Show(this,
                    "Installed. It is running in the background now and will auto-start at every logon.",
                    "Installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Install failed:\n" + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                RefreshStatus();
            }
        }

        private void UninstallBtn_Click(object sender, EventArgs e)
        {
            try
            {
                Program.RunHidden("schtasks.exe", string.Format("/end /tn \"{0}\"", Program.TaskName));
                Program.RunHidden("schtasks.exe", string.Format("/delete /tn \"{0}\" /f", Program.TaskName));

                string installPath = Program.InstallPath;
                int currentPid = Process.GetCurrentProcess().Id;

                foreach (var p in Process.GetProcessesByName("ClearDisplayAffinity"))
                {
                    try
                    {
                        if (p.Id == currentPid) continue;
                        string modulePath;
                        try { modulePath = p.MainModule.FileName; }
                        catch { continue; }

                        if (string.Equals(modulePath, installPath, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                    }
                    catch { /* process may have exited or be inaccessible */ }
                }

                for (int i = 0; i < 5 && File.Exists(installPath); i++)
                {
                    try { File.Delete(installPath); }
                    catch { Thread.Sleep(300); }
                }

                MessageBox.Show(this, "Uninstalled. Scheduled task and background copy removed.",
                    "Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Uninstall failed:\n" + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                RefreshStatus();
            }
        }
    }
}
