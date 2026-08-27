// KernelBypass.exe — Kernel-level SetWindowDisplayAffinity bypass
// Same license system as WdpMgr / WinOverlay (WDPMGR_LICENSE_V1 format)
// AppId slug: "kbypass"
//
// Usage:
//   KernelBypass.exe --install    Install and start as Windows service
//   KernelBypass.exe --uninstall  Stop and remove service
//   KernelBypass.exe --run        Run interactively (debug/test)
//   KernelBypass.exe              Run as Windows service (called by SCM)
//
// Requires: kbypass.sys in same directory as this exe (built with WDK)
// Requires: test signing enabled or production EV cert on the driver

using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

// ── License data ─────────────────────────────────────────────────────────────
class LicenseData {
    public string Id = "", Type = "", Expiry = "", Issued = "", Server = "",
                  Relay = "", Sig = "", PubKeyXml = "", AppId = "";
    public int DurationDays = 0;
}

// ── IOCTL constants (must match kbypass.h) ────────────────────────────────────
static class KBypassIoctl {
    const uint FILE_DEVICE_UNKNOWN = 0x00000022;
    static uint CTL(uint fn) => (FILE_DEVICE_UNKNOWN << 16) | (fn << 2);
    public static readonly uint Activate   = CTL(0x900);
    public static readonly uint Deactivate = CTL(0x901);
    public static readonly uint Status     = CTL(0x902);
}

// ── Win32 P/Invoke ─────────────────────────────────────────────────────────────
static class NM {
    public const uint SC_MANAGER_ALL_ACCESS   = 0xF003F;
    public const uint SERVICE_ALL_ACCESS      = 0xF01FF;
    public const uint SERVICE_KERNEL_DRIVER   = 0x00000001;
    public const uint SERVICE_DEMAND_START    = 0x00000003;
    public const uint SERVICE_ERROR_NORMAL    = 0x00000001;
    public const uint SERVICE_CONTROL_STOP    = 0x00000001;
    public const uint ERROR_SERVICE_EXISTS    = 1073;
    public const uint ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const uint SE_PRIVILEGE_ENABLED    = 2;

    [DllImport("advapi32.dll", CharSet=CharSet.Auto)]
    public static extern IntPtr OpenSCManager(string m, string d, uint a);
    [DllImport("advapi32.dll", CharSet=CharSet.Auto)]
    public static extern IntPtr CreateService(IntPtr h, string n, string d, uint a,
        uint t, uint s, uint e, string b, string lg, IntPtr tag, string dep,
        string su, string pw);
    [DllImport("advapi32.dll", CharSet=CharSet.Auto)]
    public static extern IntPtr OpenService(IntPtr h, string n, uint a);
    [DllImport("advapi32.dll")]
    public static extern bool StartService(IntPtr h, uint c, string[] a);
    [DllImport("advapi32.dll")]
    public static extern bool ControlService(IntPtr h, uint c, ref SERVICE_STATUS s);
    [DllImport("advapi32.dll")]
    public static extern bool DeleteService(IntPtr h);
    [DllImport("advapi32.dll")]
    public static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)]
    public static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sec,
        uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool DeviceIoControl(IntPtr h, uint c, byte[] i, uint il,
        byte[] o, uint ol, out uint ret, IntPtr ov);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError=true)]
    public static extern bool OpenProcessToken(IntPtr h, uint a, out IntPtr t);
    [DllImport("advapi32.dll", CharSet=CharSet.Auto, SetLastError=true)]
    public static extern bool LookupPrivilegeValue(string s, string n, out LUID l);
    [DllImport("advapi32.dll", SetLastError=true)]
    public static extern bool AdjustTokenPrivileges(IntPtr t, bool d,
        ref TOKEN_PRIVILEGES p, uint l, IntPtr pr, IntPtr rl);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)] public struct SERVICE_STATUS {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted,
                    dwWin32ExitCode, dwServiceSpecificExitCode,
                    dwCheckPoint, dwWaitHint;
    }
    [StructLayout(LayoutKind.Sequential)] public struct LUID {
        public uint LowPart; public int HighPart;
    }
    [StructLayout(LayoutKind.Sequential)] public struct TOKEN_PRIVILEGES {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Main program
// ═══════════════════════════════════════════════════════════════════════════════
static class Program {
    const string SVC_NAME      = "KernelBypass";
    const string SVC_DISPLAY   = "Kernel Bypass Service";
    const string APP_SLUG      = "kbypass";
    const string LOG_PATH      = @"C:\ProgramData\KernelBypass\kbypass.log";
    const string DRIVER_NAME   = "kbypass.sys";

    static string ExeDir => Path.GetDirectoryName(
        System.Reflection.Assembly.GetExecutingAssembly().Location)!;
    static string DriverSysPath => Path.Combine(ExeDir, DRIVER_NAME);

    static IntPtr   g_DevHandle  = IntPtr.Zero;
    static Thread   g_LicLoop    = null;
    static LicenseData g_License = null;
    static volatile bool g_Stop  = false;

    [STAThread]
    static int Main(string[] args) {
        string arg = args.Length > 0 ? args[0].ToLower() : "";
        switch (arg) {
            case "--install":   return Install();
            case "--uninstall": return Uninstall();
            case "--run":       return RunInteractive();
            default:
                ServiceBase.Run(new KBService());
                return 0;
        }
    }

    // ── Install ────────────────────────────────────────────────────────────────
    static int Install() {
        Log("=== Install ===");
        if (!File.Exists(DriverSysPath)) {
            Console.WriteLine($"[ERROR] {DRIVER_NAME} not found next to exe: {DriverSysPath}");
            return 1;
        }
        LicenseData lic;
        if (!ReadLicense(out lic)) { Console.WriteLine("[ERROR] No license found in exe."); return 1; }
        if (!VerifyLicense(lic))   { Console.WriteLine("[ERROR] License signature invalid."); return 1; }

        // Install kernel driver service
        if (!InstallDriver()) { Console.WriteLine("[ERROR] Driver install failed."); return 1; }

        // Install this exe as a Windows service
        var mgr = NM.OpenSCManager(null, null, NM.SC_MANAGER_ALL_ACCESS);
        if (mgr == IntPtr.Zero) { Console.WriteLine("[ERROR] OpenSCManager failed"); return 1; }
        var svc = NM.CreateService(mgr, SVC_NAME, SVC_DISPLAY,
            NM.SERVICE_ALL_ACCESS, 0x10 /*own process*/, 3 /*demand*/,
            NM.SERVICE_ERROR_NORMAL, System.Reflection.Assembly.GetExecutingAssembly().Location,
            null, IntPtr.Zero, null, null, null);
        if (svc == IntPtr.Zero && Marshal.GetLastWin32Error() != NM.ERROR_SERVICE_EXISTS)
            Console.WriteLine("[WARN] CreateService: " + Marshal.GetLastWin32Error());
        if (svc != IntPtr.Zero) NM.CloseServiceHandle(svc);
        NM.CloseServiceHandle(mgr);

        // Start the service
        mgr = NM.OpenSCManager(null, null, NM.SC_MANAGER_ALL_ACCESS);
        svc = NM.OpenService(mgr, SVC_NAME, NM.SERVICE_ALL_ACCESS);
        if (svc != IntPtr.Zero) {
            NM.StartService(svc, 0, null);
            NM.CloseServiceHandle(svc);
        }
        NM.CloseServiceHandle(mgr);
        Console.WriteLine("[OK] Installed and started.");
        return 0;
    }

    // ── Uninstall ──────────────────────────────────────────────────────────────
    static int Uninstall() {
        Log("=== Uninstall ===");
        StopAndDeleteService(SVC_NAME);
        StopAndDeleteService("kbypass"); // kernel driver service
        Console.WriteLine("[OK] Uninstalled.");
        return 0;
    }

    static void StopAndDeleteService(string name) {
        var mgr = NM.OpenSCManager(null, null, NM.SC_MANAGER_ALL_ACCESS);
        if (mgr == IntPtr.Zero) return;
        var svc = NM.OpenService(mgr, name, NM.SERVICE_ALL_ACCESS);
        if (svc != IntPtr.Zero) {
            var st = new NM.SERVICE_STATUS();
            NM.ControlService(svc, NM.SERVICE_CONTROL_STOP, ref st);
            Thread.Sleep(1500);
            NM.DeleteService(svc);
            NM.CloseServiceHandle(svc);
        }
        NM.CloseServiceHandle(mgr);
    }

    // ── Interactive run (--run, debug/test) ────────────────────────────────────
    static int RunInteractive() {
        Log("=== Interactive run ===");
        Console.CancelKeyPress += (s, e) => { g_Stop = true; e.Cancel = true; };
        RunWorker();
        return 0;
    }

    // ── Core worker (called by service OnStart or --run) ───────────────────────
    public static void RunWorker() {
        try { WorkerInner(); }
        catch (Exception ex) { Log("WORKER CRASH: " + ex.Message + "\n" + ex.StackTrace); }
    }

    static void WorkerInner() {
        // 1. License
        LicenseData lic;
        if (!ReadLicense(out lic)) { Log("No license — stopping"); return; }
        if (!VerifyLicense(lic))   { Log("License sig invalid — stopping"); return; }
        Log("License OK: id=" + lic.Id + " type=" + lic.Type);

        string status = CheckIn(lic);
        Log("CheckIn: " + status);
        if (status == "expired" || status == "revoked" || status == "invalid") {
            Log("License rejected (" + status + ") — self-removing");
            SelfDestruct();
            return;
        }

        g_License = lic;

        // 2. Install + start kernel driver
        if (!File.Exists(DriverSysPath)) {
            Log("[ERROR] " + DRIVER_NAME + " not found at: " + DriverSysPath);
            return;
        }
        AcquireLoadDriver();
        if (!InstallDriver()) { Log("[ERROR] Driver install failed"); return; }
        if (!StartDriver())   { Log("[ERROR] Driver start failed");   return; }

        // 3. Open device + activate
        g_DevHandle = NM.CreateFile(@"\\.\KernelBypass", 0xC0000000/*RW*/,
            0, IntPtr.Zero, 3/*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (g_DevHandle == new IntPtr(-1)) {
            Log("[ERROR] Cannot open \\.\KernelBypass: " + Marshal.GetLastWin32Error());
            g_DevHandle = IntPtr.Zero;
        } else {
            SendIoctl(KBypassIoctl.Activate);
            Log("Bypass ACTIVATED — driver running");
        }

        // 4. Periodic license check-in loop
        g_LicLoop = new Thread(() => LicenseLoop(lic)) { IsBackground = true };
        g_LicLoop.Start();

        // 5. Keep alive until stop
        while (!g_Stop) Thread.Sleep(500);

        // 6. Cleanup
        Log("Stopping...");
        if (g_DevHandle != IntPtr.Zero) {
            SendIoctl(KBypassIoctl.Deactivate);
            NM.CloseHandle(g_DevHandle);
            g_DevHandle = IntPtr.Zero;
        }
        StopAndDeleteService("kbypass");
        Log("Stopped.");
    }

    // ── Driver SCM management ──────────────────────────────────────────────────
    static bool InstallDriver() {
        var mgr = NM.OpenSCManager(null, null, NM.SC_MANAGER_ALL_ACCESS);
        if (mgr == IntPtr.Zero) { Log("OpenSCManager failed: " + Marshal.GetLastWin32Error()); return false; }
        var svc = NM.OpenService(mgr, "kbypass", NM.SERVICE_ALL_ACCESS);
        if (svc == IntPtr.Zero) {
            // Create
            svc = NM.CreateService(mgr, "kbypass", "KernelBypass Driver",
                NM.SERVICE_ALL_ACCESS, NM.SERVICE_KERNEL_DRIVER,
                NM.SERVICE_DEMAND_START, NM.SERVICE_ERROR_NORMAL,
                DriverSysPath, null, IntPtr.Zero, null, null, null);
            if (svc == IntPtr.Zero) {
                uint err = (uint)Marshal.GetLastWin32Error();
                if (err != NM.ERROR_SERVICE_EXISTS) {
                    Log("CreateService failed: " + err);
                    NM.CloseServiceHandle(mgr);
                    return false;
                }
                svc = NM.OpenService(mgr, "kbypass", NM.SERVICE_ALL_ACCESS);
            }
        }
        if (svc != IntPtr.Zero) NM.CloseServiceHandle(svc);
        NM.CloseServiceHandle(mgr);
        return true;
    }

    static bool StartDriver() {
        var mgr = NM.OpenSCManager(null, null, NM.SC_MANAGER_ALL_ACCESS);
        if (mgr == IntPtr.Zero) return false;
        var svc = NM.OpenService(mgr, "kbypass", NM.SERVICE_ALL_ACCESS);
        bool ok = false;
        if (svc != IntPtr.Zero) {
            ok = NM.StartService(svc, 0, null);
            uint err = (uint)Marshal.GetLastWin32Error();
            if (!ok && err == NM.ERROR_SERVICE_ALREADY_RUNNING) ok = true;
            if (!ok) Log("StartService(kbypass) failed: " + err);
            NM.CloseServiceHandle(svc);
        }
        NM.CloseServiceHandle(mgr);
        if (ok) Thread.Sleep(500); // give driver time to initialize
        return ok;
    }

    // ── IOCTL helper ──────────────────────────────────────────────────────────
    static bool SendIoctl(uint code) {
        if (g_DevHandle == IntPtr.Zero) return false;
        uint ret = 0;
        return NM.DeviceIoControl(g_DevHandle, code, null, 0, null, 0, out ret, IntPtr.Zero);
    }

    // ── License loop ──────────────────────────────────────────────────────────
    static void LicenseLoop(LicenseData lic) {
        while (!g_Stop) {
            for (int i = 0; i < 300 && !g_Stop; i++) Thread.Sleep(1000); // 5 min
            if (g_Stop) break;
            string st = CheckIn(lic);
            Log("Periodic check-in: " + st);
            if (st == "expired" || st == "revoked" || st == "invalid") {
                Log("License rejected (" + st + ") — self-removing");
                if (g_DevHandle != IntPtr.Zero) {
                    SendIoctl(KBypassIoctl.Deactivate);
                    NM.CloseHandle(g_DevHandle);
                    g_DevHandle = IntPtr.Zero;
                }
                StopAndDeleteService("kbypass");
                SelfDestruct();
                return;
            }
        }
    }

    // ── License reading from exe tail ──────────────────────────────────────────
    public static bool ReadLicense(out LicenseData lic) {
        lic = new LicenseData();
        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string text;
        try { text = File.ReadAllText(exePath, Encoding.UTF8); }
        catch { return false; }
        int start = text.IndexOf("WDPMGR_LIC_BEGIN", StringComparison.Ordinal);
        int end   = text.IndexOf("WDPMGR_LIC_END",   StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start) return false;
        string block = text.Substring(start + "WDPMGR_LIC_BEGIN".Length, end - start - "WDPMGR_LIC_BEGIN".Length);
        foreach (string rawLine in block.Split('\n')) {
            string line = rawLine.Trim().Trim('\r');
            if (!line.Contains("=")) continue;
            int eq = line.IndexOf('=');
            string k = line.Substring(0, eq).Trim();
            string v = line.Substring(eq + 1).Trim();
            switch (k) {
                case "id":           lic.Id           = v; break;
                case "type":         lic.Type         = v; break;
                case "expiry":       lic.Expiry       = v; break;
                case "issued":       lic.Issued       = v; break;
                case "server":       lic.Server       = v; break;
                case "relay":        lic.Relay        = v; break;
                case "sig":          lic.Sig          = v; break;
                case "durationDays": int.TryParse(v, out lic.DurationDays); break;
                case "appId":        lic.AppId        = v; break;
                case "pubkey":
                    lic.PubKeyXml = Encoding.UTF8.GetString(Convert.FromBase64String(v)); break;
            }
        }
        return !string.IsNullOrEmpty(lic.Id) && !string.IsNullOrEmpty(lic.PubKeyXml);
    }

    // ── RSA signature verification ────────────────────────────────────────────
    public static bool VerifyLicense(LicenseData lic) {
        try {
            using var rsa = RSA.Create();
            rsa.ImportParameters(XmlToParams(lic.PubKeyXml, false));
            byte[] payload = Encoding.UTF8.GetBytes(lic.Id);
            byte[] sig     = Convert.FromBase64String(lic.Sig);
            return rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        } catch { return false; }
    }

    static RSAParameters XmlToParams(string xml, bool priv) {
        var root = XDocument.Parse(xml).Root!;
        byte[] GB(string t) => Convert.FromBase64String(root.Element(t)!.Value);
        var p = new RSAParameters { Modulus = GB("Modulus"), Exponent = GB("Exponent") };
        if (priv) { p.P=GB("P"); p.Q=GB("Q"); p.DP=GB("DP"); p.DQ=GB("DQ");
                    p.InverseQ=GB("InverseQ"); p.D=GB("D"); }
        return p;
    }

    // ── Server check-in ────────────────────────────────────────────────────────
    public static string CheckIn(LicenseData lic) {
        string[] servers = string.IsNullOrEmpty(lic.Relay)
            ? new[] { lic.Server }
            : new[] { lic.Relay, lic.Server };
        foreach (string srv in servers) {
            try {
                string fp   = GetFingerprint();
                string host = Environment.MachineName;
                string body = $"{{\"licenseId\":\"{lic.Id}\",\"fingerprint\":\"{fp}\"," +
                              $"\"hostname\":\"{Esc(host)}\",\"windowsUser\":\"\"," +
                              $"\"appId\":\"{APP_SLUG}\"}}";
                var req = (HttpWebRequest)WebRequest.Create(srv + "/api/checkin");
                req.Method      = "POST";
                req.ContentType = "application/json";
                req.Timeout     = 10000;
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using var res  = (HttpWebResponse)req.GetResponse();
                using var sr   = new StreamReader(res.GetResponseStream()!);
                string json    = sr.ReadToEnd();
                if (json.Contains("\"ok\""))      return "ok";
                if (json.Contains("\"expired\"")) return "expired";
                if (json.Contains("\"revoked\"")) return "revoked";
                if (json.Contains("\"wrong_machine\"")) return "wrong_machine";
                return "invalid";
            } catch (Exception ex) {
                Log("CheckIn error: " + ex.Message);
            }
        }
        return "error";
    }

    // ── HWID fingerprint (same logic as WdpMgr) ────────────────────────────────
    static string GetFingerprint() {
        string raw = Environment.MachineName + "|" + GetHardwareId();
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)))
                           .Replace("-","").ToLower().Substring(0, 32);
    }

    static string GetHardwareId() {
        try {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return (string?)key?.GetValue("MachineGuid") ?? "unknown";
        } catch { return "unknown"; }
    }

    // ── Self-destruct (removes service + exe) ─────────────────────────────────
    static void SelfDestruct() {
        g_Stop = true;
        try {
            StopAndDeleteService(SVC_NAME);
            StopAndDeleteService("kbypass");
        } catch { }
        try {
            string bat = Path.Combine(Path.GetTempPath(), "kb_remove.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                $"timeout /t 3 /nobreak >nul\r\n" +
                $"del /f /q \"{System.Reflection.Assembly.GetExecutingAssembly().Location}\"\r\n" +
                $"del /f /q \"{DriverSysPath}\"\r\n" +
                $"del /f /q \"%~f0\"\r\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName  = "cmd.exe",
                Arguments = "/c \"" + bat + "\"",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
        } catch { }
        Environment.Exit(0);
    }

    // ── SeLoadDriverPrivilege ─────────────────────────────────────────────────
    static void AcquireLoadDriver() {
        try {
            NM.OpenProcessToken(NM.GetCurrentProcess(), 0x20 /*TOKEN_ADJUST_PRIVILEGES*/, out var tok);
            NM.LookupPrivilegeValue(null, "SeLoadDriverPrivilege", out var luid);
            var tp = new NM.TOKEN_PRIVILEGES {
                PrivilegeCount = 1, Luid = luid,
                Attributes = NM.SE_PRIVILEGE_ENABLED
            };
            NM.AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            NM.CloseHandle(tok);
        } catch { }
    }

    // ── Logging ────────────────────────────────────────────────────────────────
    public static void Log(string msg) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(LOG_PATH)!);
            File.AppendAllText(LOG_PATH,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
        } catch { }
        Console.WriteLine(msg);
    }

    static string Esc(string s) => s.Replace("\"","\\\"").Replace("\\","\\\\");
}

// ═══════════════════════════════════════════════════════════════════════════════
// Windows Service wrapper
// ═══════════════════════════════════════════════════════════════════════════════
class KBService : ServiceBase {
    Thread _thread;
    public KBService() { ServiceName = "KernelBypass"; CanStop = true; }

    protected override void OnStart(string[] args) {
        _thread = new Thread(Program.RunWorker) { IsBackground = true, Name = "KBWorker" };
        _thread.Start();
    }

    protected override void OnStop() {
        // g_Stop is set by StopAndDeleteService calling SCM stop, thread will exit
        if (_thread != null) _thread.Join(8000);
    }
}
