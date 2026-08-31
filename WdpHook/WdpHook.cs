/*
 * WdpHook.exe  —  No-injection replacement for WdpMgr
 *
 * Same license server / admin control / self-destruct as WdpMgr.
 * Instead of injecting a DLL:
 *   • Global WH_KEYBOARD_LL + WH_MOUSE_LL hooks strip LLMHF_INJECTED
 *     so MeshCentral SendInput() looks like hardware to lockdown browsers.
 *   • EnumWindows poll (150 ms) calls SetWindowDisplayAffinity(0) on every
 *     window — fixes the black screen (WDA_MONITOR) without injection.
 *
 * Modes:
 *   (no args / UAC-elevated)  → GUI: install / uninstall
 *   /service                  → Windows service: license + spawn hooks child
 *   /hooks                    → user-session helper: LL hooks + WDA poll
 *   /install  /uninstall      → scripted
 */

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WdpHook
{
    // =========================================================================
    // Windows Service — runs as SYSTEM (Session 0)
    // Handles: license check, spawning user-session /hooks child, self-destruct
    // =========================================================================
    internal sealed class HookService : ServiceBase
    {
        private Thread _licThread, _spawnThread;
        private volatile bool _stop;
        private volatile int  _hooksPid;

        public HookService() { ServiceName = "WdpHook"; CanStop = true; }

        protected override void OnStart(string[] args)
        {
            _stop = false;
            Program.Log("=== WdpHook service started PID=" + Process.GetCurrentProcess().Id + " ===");

            LicenseData lic;
            if (!Program.ReadLicense(out lic)) { Program.Log("No license — stopping"); Stop(); return; }
            if (!Program.VerifyLicense(lic))   { Program.Log("Bad sig — stopping");    Stop(); return; }

            string status = Program.CheckIn(lic);
            Program.Log("Initial check-in: " + status);
            if (status == "expired" || status == "revoked" || status == "invalid")
            { Program.Log("Rejected — self-removing"); Program.SelfDestruct(); Environment.Exit(0); return; }

            Program.StartLicenseLoop(lic, () => { _stop = true; });

            _spawnThread = new Thread(SpawnLoop) { IsBackground = true, Name = "SpawnLoop" };
            _spawnThread.Start();
        }

        protected override void OnStop()
        {
            _stop = true;
            Program.Log("Service stopping");
            // Kill the hooks child if alive
            try { if (_hooksPid > 0) Process.GetProcessById(_hooksPid).Kill(); } catch { }
        }

        private void SpawnLoop()
        {
            while (!_stop)
            {
                try
                {
                    if (_hooksPid > 0)
                    {
                        try { var p = Process.GetProcessById(_hooksPid); } // throws if dead
                        catch { _hooksPid = 0; Program.Log("Hooks child died — will respawn"); }
                    }
                    if (_hooksPid == 0) SpawnHooksChild();
                }
                catch (Exception ex) { Program.Log("SpawnLoop error: " + ex.Message); }
                Thread.Sleep(10000);
            }
        }

        private void SpawnHooksChild()
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF) { Program.Log("No active console session"); return; }

            IntPtr hToken = IntPtr.Zero;
            if (!WTSQueryUserToken(sessionId, out hToken))
            { Program.Log("WTSQueryUserToken failed err=" + Marshal.GetLastWin32Error()); return; }

            try
            {
                IntPtr hDup = IntPtr.Zero;
                if (!DuplicateTokenEx(hToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out hDup))
                { Program.Log("DuplicateTokenEx failed err=" + Marshal.GetLastWin32Error()); return; }
                try
                {
                    IntPtr env = IntPtr.Zero;
                    CreateEnvironmentBlock(out env, hDup, false);

                    string exe = "\"" + Program.ExePath + "\" /hooks";
                    var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = "winsta0\\default" };
                    PROCESS_INFORMATION pi;
                    bool ok = CreateProcessAsUserW(hDup, null, exe, IntPtr.Zero, IntPtr.Zero,
                        false, CREATE_UNICODE_ENV, env, null, ref si, out pi);
                    if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);

                    if (ok)
                    {
                        _hooksPid = (int)pi.dwProcessId;
                        Program.Log("Spawned /hooks child PID=" + _hooksPid);
                        CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
                    }
                    else
                        Program.Log("CreateProcessAsUser failed err=" + Marshal.GetLastWin32Error());
                }
                finally { CloseHandle(hDup); }
            }
            finally { CloseHandle(hToken); }
        }

        // --- P/Invoke for user-session spawning ---
        const uint TOKEN_ALL_ACCESS      = 0xF01FF;
        const int  SecurityImpersonation = 2;
        const int  TokenPrimary          = 1;
        const uint CREATE_UNICODE_ENV    = 0x00000400;

        [DllImport("wtsapi32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll")] static extern bool WTSQueryUserToken(uint s, out IntPtr t);
        [DllImport("advapi32.dll")] static extern bool DuplicateTokenEx(IntPtr h, uint a, IntPtr s, int i, int t, out IntPtr d);
        [DllImport("userenv.dll")]  static extern bool CreateEnvironmentBlock(out IntPtr e, IntPtr t, bool b);
        [DllImport("userenv.dll")]  static extern bool DestroyEnvironmentBlock(IntPtr e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
        static extern bool CreateProcessAsUserW(IntPtr hToken, string app, string cmd,
            IntPtr pa, IntPtr ta, bool inh, uint flags, IntPtr env, string dir,
            ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars,dwFillAttribute,dwFlags;
            public short wShowWindow,cbReserved2; public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError; }
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess,hThread; public uint dwProcessId,dwThreadId; }
    }

    // =========================================================================
    // Hooks mode — runs in the USER SESSION (spawned by service, or run-once)
    // Does: WDA clear loop + global LL hooks (strips LLMHF_INJECTED)
    // =========================================================================
    internal static class HooksMode
    {
        static IntPtr  _hKbd, _hMouse;
        static HookProc _kbdProc, _mouseProc;  // hold refs — GC must not collect
        static int     _mainTid;
        const  int     WM_REHOOK = 0x8001;

        delegate IntPtr HookProc(int n, IntPtr w, IntPtr l);

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
        [DllImport("user32.dll")] static extern bool   UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool   GetWindowDisplayAffinity(IntPtr hw, out uint aff);
        [DllImport("user32.dll")] static extern bool   SetWindowDisplayAffinity(IntPtr hw, uint aff);
        [DllImport("user32.dll")] static extern bool   EnumWindows(EnumWndProc fn, IntPtr lp);
        [DllImport("user32.dll")] static extern bool   PostThreadMessage(int tid, int msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern bool GetMessage(ref MSG m, IntPtr h, uint f, uint t);
        [DllImport("kernel32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("kernel32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        delegate bool EnumWndProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public uint time; public int x,y; }

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT pt; public uint data, flags, time; public UIntPtr extra; }

        static IntPtr KbdProc(int n, IntPtr w, IntPtr l)
        {
            if (n == 0 && l != IntPtr.Zero) {
                var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
                if ((s.flags & 0x12) != 0) { s.flags &= ~0x12u; Marshal.StructureToPtr(s, l, false); }
            }
            return CallNextHookEx(_hKbd, n, w, l);
        }

        static IntPtr MouseProc(int n, IntPtr w, IntPtr l)
        {
            if (n == 0 && l != IntPtr.Zero) {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                if ((s.flags & 0x03) != 0) { s.flags &= ~0x03u; Marshal.StructureToPtr(s, l, false); }
            }
            return CallNextHookEx(_hMouse, n, w, l);
        }

        static void Rehook()
        {
            if (_hKbd   != IntPtr.Zero) { UnhookWindowsHookEx(_hKbd);   _hKbd   = IntPtr.Zero; }
            if (_hMouse != IntPtr.Zero) { UnhookWindowsHookEx(_hMouse); _hMouse = IntPtr.Zero; }
            IntPtr hMod = GetModuleHandle(null);
            _hKbd   = SetWindowsHookEx(13, _kbdProc,   hMod, 0);
            _hMouse = SetWindowsHookEx(14, _mouseProc, hMod, 0);
            Program.Log("Hooks: kbd=" + _hKbd + " mouse=" + _hMouse);
        }

        public static void Run()
        {
            _mainTid  = Thread.CurrentThread.ManagedThreadId;
            _kbdProc   = KbdProc;
            _mouseProc = MouseProc;

            Program.Log("=== /hooks started PID=" + Process.GetCurrentProcess().Id + " ===");
            Rehook();

            new Thread(Worker) { IsBackground = true, Name = "HooksWorker" }.Start();

            // Native message loop (Application.Run would also work but this keeps it minimal)
            MSG msg = new MSG();
            while (GetMessage(ref msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WM_REHOOK) Rehook();
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            UnhookWindowsHookEx(_hKbd);
            UnhookWindowsHookEx(_hMouse);
        }

        static void Worker()
        {
            uint lastProcs = 0;
            int  cycles    = 0;
            while (true)
            {
                Thread.Sleep(150);

                // WDA clear
                EnumWindows((hw, _) => {
                    uint aff;
                    if (GetWindowDisplayAffinity(hw, out aff) && aff != 0)
                    {
                        SetWindowDisplayAffinity(hw, 0);
                        Program.Log("WDA cleared hwnd=0x" + hw.ToString("X"));
                    }
                    return true;
                }, IntPtr.Zero);

                // Detect new processes → re-register hooks so we stay FIRST in LIFO chain
                uint procs = 0;
                try { procs = (uint)Process.GetProcesses().Length; } catch { }
                if (procs != lastProcs && lastProcs != 0)
                {
                    lastProcs = procs;
                    Thread.Sleep(600);
                    PostThreadMessage(_mainTid, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);
                }
                else if (lastProcs == 0) lastProcs = procs;

                if (++cycles % 400 == 0)
                    Program.Log("heartbeat cycles=" + cycles + " procs=" + procs);
            }
        }
    }

    // =========================================================================
    // License structs + all shared utilities
    // =========================================================================
    internal struct LicenseData
    {
        public string Id, Type, Expiry, Issued, DurationDays, AppId, Server, Relay, Sig, PubKey;
    }

    internal static class Program
    {
        internal const  string SvcName    = "WdpHook";
        internal static string ExePath    = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        static   string _destExe  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WdpHook.exe");
        internal static string LogPath    = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WdpHook.log");
        static   string _statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WdpHook.state");

        // Replace with your server's RSA public key XML.
        // Use the same key as WdpMgr — license files are interchangeable.
        internal const string RSA_PUBLIC_KEY_XML = "REPLACE_WITH_SERVER_PUBLIC_KEY";

        internal static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n"); }
            catch { }
        }

        // ── License ──────────────────────────────────────────────────────────
        internal static bool ReadLicense(out LicenseData lic)
        {
            lic = new LicenseData();
            try
            {
                // 1. wdp.lic next to EXE
                string licPath = Path.Combine(Path.GetDirectoryName(ExePath) ?? "", "wdp.lic");
                string content = null;
                if (File.Exists(licPath))
                    content = File.ReadAllText(licPath);
                else
                {
                    // 2. Embedded in EXE tail
                    const string BEGIN = "WDPMGR_LIC_BEGIN\n", END = "WDPMGR_LIC_END";
                    long len   = new FileInfo(ExePath).Length;
                    int  tail  = (int)Math.Min(8192, len);
                    byte[] buf = new byte[tail];
                    using (var fs = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { fs.Seek(-tail, SeekOrigin.End); fs.Read(buf, 0, tail); }
                    string s  = Encoding.UTF8.GetString(buf);
                    int si = s.LastIndexOf(BEGIN), ei = s.LastIndexOf(END);
                    if (si >= 0 && ei > si)
                        content = s.Substring(si + BEGIN.Length, ei - si - BEGIN.Length);
                }
                if (content == null || !content.Contains("WDPMGR_LICENSE_V1")) return false;
                foreach (string raw in content.Split('\n'))
                {
                    string line = raw.Trim('\r', ' ');
                    int eq = line.IndexOf('='); if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    switch (k) {
                        case "id":           lic.Id           = v; break;
                        case "type":         lic.Type         = v; break;
                        case "expiry":       lic.Expiry       = v; break;
                        case "issued":       lic.Issued       = v; break;
                        case "durationDays": lic.DurationDays = v; break;
                        case "appId":        lic.AppId        = v; break;
                        case "server":       lic.Server       = v; break;
                        case "relay":        lic.Relay        = v; break;
                        case "pubkey":       lic.PubKey       = v; break;
                        case "sig":          lic.Sig          = v; break;
                    }
                }
                return !string.IsNullOrEmpty(lic.Id) && !string.IsNullOrEmpty(lic.Sig);
            }
            catch (Exception ex) { Log("ReadLicense: " + ex.Message); return false; }
        }

        internal static bool VerifyLicense(LicenseData lic)
        {
            string key = RSA_PUBLIC_KEY_XML;
            if (!string.IsNullOrEmpty(lic.PubKey))
                try { key = Encoding.UTF8.GetString(Convert.FromBase64String(lic.PubKey)); } catch { return false; }
            if (key == "REPLACE_WITH_SERVER_PUBLIC_KEY") return true; // dev mode
            try {
                byte[] data = Encoding.UTF8.GetBytes(lic.Id);
                byte[] sig  = Convert.FromBase64String(lic.Sig);
                using (var rsa = new RSACryptoServiceProvider()) {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(key);
                    return rsa.VerifyData(data, "SHA256", sig);
                }
            } catch (Exception ex) { Log("VerifyLicense: " + ex.Message); return false; }
        }

        private const string RELAY_URL_DEFAULT = "https://wdp-manager.pulkitarora-782.workers.dev";

        internal static string CheckIn(LicenseData lic)
        {
            if (string.IsNullOrEmpty(lic.Server) || lic.Server.StartsWith("REPLACE")) return "ok";
            string fp   = GetFingerprint();
            string json = "{\"licenseId\":\"" + Esc(lic.Id) + "\",\"fingerprint\":\"" + fp
                        + "\",\"hostname\":\"" + Esc(Environment.MachineName)
                        + "\",\"windowsUser\":\"" + Esc(WmiGet("Win32_ComputerSystem", "UserName"))
                        + "\",\"appId\":\"wdphook\"}";
            string relay = !string.IsNullOrEmpty(lic.Relay) ? lic.Relay : RELAY_URL_DEFAULT;
            foreach (string srv in new[] { lic.Server, relay })
            {
                try {
                    var wc = new System.Net.WebClient();
                    wc.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
                    wc.Encoding = Encoding.UTF8;
                    string resp = wc.UploadString(srv.TrimEnd('/') + "/api/checkin", json);
                    int q1 = resp.IndexOf("\"status\":"); if (q1 < 0) return "ok";
                    q1 = resp.IndexOf('"', q1 + 9); int q2 = resp.IndexOf('"', q1 + 1);
                    return (q1 >= 0 && q2 > q1) ? resp.Substring(q1 + 1, q2 - q1 - 1) : "ok";
                } catch { }
            }
            return "offline";
        }

        internal static void StartLicenseLoop(LicenseData lic, Action onRevoke)
        {
            new Thread(() => {
                while (true) {
                    string s = CheckIn(lic); Log("LicenseLoop: " + s);
                    if (s == "expired" || s == "revoked" || s == "invalid")
                    { Log("Revoked — self-destruct"); SelfDestruct(); onRevoke?.Invoke(); Environment.Exit(0); }
                    Thread.Sleep(TimeSpan.FromMinutes(5));
                }
            }) { IsBackground = true, Name = "LicLoop" }.Start();
        }

        static string GetFingerprint()
        {
            try {
                string s = WmiGet("Win32_BaseBoard","SerialNumber")
                         + WmiGet("Win32_Processor","ProcessorId")
                         + WmiGet("Win32_OperatingSystem","SerialNumber");
                byte[] h = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(h).Replace("-","").ToLowerInvariant();
            } catch { return "unknown"; }
        }

        static string WmiGet(string cls, string prop)
        {
            try {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT " + prop + " FROM " + cls))
                foreach (System.Management.ManagementObject o in s.Get())
                { object v = o[prop]; if (v != null) { string r = v.ToString().Trim(); if (r != "") return r; } }
            } catch { }
            return "unknown";
        }

        static string Esc(string s) => s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","").Replace("\n","");

        // ── Self-destruct ─────────────────────────────────────────────────────
        internal static void SelfDestruct()
        {
            // Remove startup items
            try { using (var k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)) k?.DeleteValue("WdpHook", false); } catch { }
            // Remove files
            try { File.Delete(LogPath); } catch { }
            try { File.Delete(_statePath); } catch { }
            // Schedule service removal + EXE deletion (runs as SYSTEM, no UAC)
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 >nul & sc stop WdpHook >nul 2>&1 & sc delete WdpHook >nul 2>&1 & del /f /q \""
                + _destExe + "\"") { CreateNoWindow = true, UseShellExecute = false });
        }

        // ── Service install / uninstall ───────────────────────────────────────
        internal static bool InstallService(out string error)
        {
            error = null;
            try { File.Copy(ExePath, _destExe, overwrite: true); }
            catch (Exception ex) { error = "Copy failed: " + ex.Message; return false; }
            string exe = "\"" + _destExe + "\"";
            if (!RunSc("create " + SvcName + " binPath= " + exe + " start= auto DisplayName= \"Windows Display Hook\" obj= LocalSystem"))
            { error = "sc create failed — run as Administrator"; return false; }
            RunSc("description " + SvcName + " \"Manages display and input policy settings.\"");
            RunSc("start " + SvcName);
            return true;
        }

        internal static void UninstallService()
        {
            RunSc("stop "   + SvcName);
            Thread.Sleep(1200);
            RunSc("delete " + SvcName);
            try { if (File.Exists(_destExe)) File.Delete(_destExe); } catch { }
        }

        internal static string GetServiceStatus()
        {
            try { using (var sc = new ServiceController(SvcName)) { sc.Refresh(); return sc.Status.ToString(); } }
            catch (InvalidOperationException) { return "Not installed"; }
            catch { return "Unknown"; }
        }

        static bool RunSc(string args)
        {
            try {
                var p = Process.Start(new ProcessStartInfo("sc.exe", args)
                    { UseShellExecute = false, CreateNoWindow = true });
                p.WaitForExit(); return p.ExitCode == 0;
            } catch { return false; }
        }

        internal static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                    .IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // ── Entry point ───────────────────────────────────────────────────────
        [STAThread]
        static int Main(string[] args)
        {
            // Service mode — launched by SCM
            if (!Environment.UserInteractive)
            {
                ServiceBase.Run(new HookService());
                return 0;
            }

            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            // User-session hooks mode — spawned by service
            if (arg == "/hooks" || arg == "-hooks")
            {
                HooksMode.Run();
                return 0;
            }

            // Scripted
            if (arg == "/install" || arg == "-install")
            { string e; InstallService(out e); return 0; }
            if (arg == "/uninstall" || arg == "-uninstall")
            { UninstallService(); return 0; }

            // GUI — must be admin
            if (!IsAdmin())
            {
                try { Process.Start(new ProcessStartInfo(ExePath)
                    { UseShellExecute = true, Verb = "runas", Arguments = string.Join(" ", args) }); }
                catch { }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }

    // =========================================================================
    // GUI — same look / buttons as WdpMgr
    // =========================================================================
    internal sealed class MainForm : Form
    {
        private Label  _lbl;
        private Button _btnInstall, _btnUninstall, _btnLog, _btnClose;
        private readonly Color BG = Color.FromArgb(16,51,100), YELLOW = Color.FromArgb(255,220,100);

        public MainForm()
        {
            Text = "Windows Display Hook"; ClientSize = new System.Drawing.Size(480, 200);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; BackColor = BG;

            var desc = new Label { Text =
                "WdpHook manages display affinity and input policy without DLL injection.\n" +
                "Install as a service to activate automatically at each Windows start.",
                ForeColor = Color.White, Location = new System.Drawing.Point(12,14),
                Size = new System.Drawing.Size(456,60), AutoSize = false };
            Controls.Add(desc);

            _lbl = new Label { ForeColor = YELLOW, Location = new System.Drawing.Point(12,82),
                Size = new System.Drawing.Size(456,20), AutoSize = false };
            Controls.Add(_lbl);

            var sep = new Panel { BackColor = Color.FromArgb(38,78,130),
                Location = new System.Drawing.Point(0,108), Size = new System.Drawing.Size(480,1) };
            Controls.Add(sep);

            _btnInstall   = MkBtn("Install",   120, 124, DoInstall);
            _btnUninstall = MkBtn("Uninstall", 232, 124, DoUninstall);
            _btnLog       = MkBtn("View Log",  344, 124, () => { try { Process.Start(Program.LogPath); } catch { } });
            _btnClose     = MkBtn("Close",     344, 158, Application.Exit);
            Controls.Add(_btnInstall); Controls.Add(_btnUninstall);
            Controls.Add(_btnLog); Controls.Add(_btnClose);

            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (_,__) => RefreshStatus(); t.Start();
            RefreshStatus();
        }

        Button MkBtn(string text, int x, int y, Action click)
        {
            var b = new Button { Text = text, Location = new System.Drawing.Point(x,y),
                Size = new System.Drawing.Size(100,28), FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White, BackColor = Color.FromArgb(38,78,130) };
            b.Click += (_,__) => click();
            return b;
        }

        void RefreshStatus() => _lbl.Text = "Service: " + Program.GetServiceStatus();

        void DoInstall()
        {
            LicenseData lic;
            if (!Program.ReadLicense(out lic) || !Program.VerifyLicense(lic))
            { MessageBox.Show("No valid license found in this EXE.", "License Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            string preCheck = Program.CheckIn(lic);
            if (preCheck == "revoked" || preCheck == "expired" || preCheck == "invalid" || preCheck == "wrong_machine")
            { MessageBox.Show("License rejected by server: " + preCheck, "License Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (Program.GetServiceStatus() != "Not installed") { Program.UninstallService(); Thread.Sleep(1500); }
            string err;
            bool ok = Program.InstallService(out err);
            RefreshStatus();
            MessageBox.Show(ok ? "Installed and started." : "Error: " + err,
                ok ? "Success" : "Error", MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        void DoUninstall()
        {
            if (MessageBox.Show("Remove WdpHook service?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Program.UninstallService();
            Program.SelfDestruct();
            Application.Exit();
        }
    }
}
