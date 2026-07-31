using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

// ── Win32 ─────────────────────────────────────────────────────────────────────
static class NativeMethods
{
    public const uint WDA_NONE              = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    public const uint MOD_CONTROL            = 0x0002;
    public const uint MOD_ALT               = 0x0001;
    public const int  WM_HOTKEY             = 0x0312;
    public const int  WM_WTSSESSION_CHANGE  = 0x02B1;
    public const int  WTS_REMOTE_CONNECT    = 0x3;
    public const int  WTS_REMOTE_DISCONNECT = 0x4;
    public const uint NOTIFY_FOR_ALL_SESSIONS = 1;
    public const int  HOTKEY_TOGGLE         = 1;

    [DllImport("user32.dll")]   public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")]   public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]   public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("wtsapi32.dll")] public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);
    [DllImport("wtsapi32.dll")] public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr hModule);
}

// ── License ───────────────────────────────────────────────────────────────────
class LicenseData
{
    public string Id = "", Type = "", Expiry = "", Issued = "", Server = "", Sig = "", PubKeyXml = "", AppId = "";
    public int DurationDays = 0;
}

// ── Program ───────────────────────────────────────────────────────────────────
static class Program
{
    internal static string Wv2Dir;
    internal static bool   Wv2DllLoaded = false;
    internal static string Wv2BrowserExeFolder = null; // non-null → pass to CreateAsync instead of relying on registry
    internal static string LogPath = Path.Combine(Path.GetTempPath(), "WinOverlay_diag.txt");

    internal static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n"); } catch { }
    }

    // Returns true if something was installed and the process should restart to pick it up.
    static bool EnsurePrerequisites()
    {
        bool installed = false;

        // 1. VC++ 2015-2022 runtime (required by WebView2Loader.dll)
        var hVc = NativeMethods.LoadLibrary("MSVCP140.dll");
        bool hasVcrt = hVc != IntPtr.Zero;
        if (hasVcrt) NativeMethods.FreeLibrary(hVc);
        if (!hasVcrt)
        {
            Log("PREREQ: VC++ runtime missing — installing silently...");
            int code = InstallPrereq("https://aka.ms/vs/17/release/vc_redist.x64.exe",
                                     "vc_redist.x64.exe", "/install /quiet /norestart");
            Log("PREREQ: VC++ installer exit=" + code);
            installed = true;
        }

        // 2. WebView2 Evergreen Runtime
        if (!IsWebView2Installed())
        {
            // 0x80040c01 = EdgeUpdate blocked by policy (common on Windows Server).
            // Set allow-install policy in registry BEFORE running the installer.
            try
            {
                using var pol = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\EdgeUpdate", true);
                pol?.SetValue("InstallDefault", 1, RegistryValueKind.DWord);
                pol?.SetValue("Install{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", 1, RegistryValueKind.DWord);
                Log("PREREQ: EdgeUpdate policy set to allow");
            }
            catch (Exception ex) { Log("PREREQ: EdgeUpdate policy write failed (not admin?): " + ex.Message); }

            Log("PREREQ: WebView2 not installed — trying standalone installer (~150 MB)...");
            int code = InstallPrereq("https://go.microsoft.com/fwlink/p/?LinkId=2124701",
                                     "MicrosoftEdgeWebview2RuntimeInstaller_x64.exe", "/silent /install");
            Log("PREREQ: standalone exit=" + code + " registry=" + IsWebView2Installed());

            if (IsWebView2Installed()) { installed = true; Log("PREREQ: WebView2 installed OK"); }
            else Log("PREREQ: WebView2 install failed (code=" + code + ") — will try Edge path fallback");
        }

        return installed;
    }

    static bool IsWebView2Installed()
    {
        const string sub32 = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        const string sub64 = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        // Check HKLM (system-wide) and HKCU (per-user install)
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var sub in new[] { sub32, sub64 })
            {
                try
                {
                    using var k = hive.OpenSubKey(sub);
                    var pv = k?.GetValue("pv") as string;
                    if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return true;
                }
                catch { }
            }
        }
        return false;
    }

    // Returns installer exit code, or -1 on download/launch failure.
    static int InstallPrereq(string url, string fileName, string installArgs)
    {
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), fileName);
            Log("PREREQ: downloading " + url);
            using (var wc = new WebClient())
                wc.DownloadFile(url, tmp);
            Log("PREREQ: running " + fileName);
            var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(tmp, installArgs) { UseShellExecute = true });
            p?.WaitForExit(300000); // 5-minute timeout (standalone is ~150 MB)
            return p?.ExitCode ?? 0;
        }
        catch (Exception ex)
        {
            Log("PREREQ: exception: " + ex.Message);
            return -1;
        }
    }

    [STAThread]
    static void Main()
    {
        try { File.WriteAllText(LogPath, "=== WinOverlay Startup " + DateTime.Now + " ===\r\n"); } catch { }
        Log("OS: " + Environment.OSVersion + "  x64=" + Environment.Is64BitOperatingSystem);
        Log("EXE: " + Application.ExecutablePath);
        Log("WV2 installed=" + IsWebView2Installed());

        // Silently install any missing prerequisites (VC++ runtime, WebView2).
        // If anything was installed, restart so the new runtime is fully loaded.
        if (EnsurePrerequisites())
        {
            Log("PREREQ: restarting to pick up installed runtimes...");
            try { System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true }); }
            catch (Exception ex) { Log("PREREQ: restart failed: " + ex.Message); }
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true)
                ?? Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            key?.SetValue(Path.GetFileName(Application.ExecutablePath), 11001, RegistryValueKind.DWord);
        } catch { }

        Log("Calling ExtractBundledDlls...");
        ExtractBundledDlls();
        Log("ExtractBundledDlls done. Wv2DllLoaded=" + Wv2DllLoaded + "  Wv2Dir=" + Wv2Dir);

        LicenseData lic = ReadEmbeddedLicense() ?? ReadLicFile();
        if (lic == null) { Log("LICENSE: not found"); Msg("No valid license found.\n\nPlace a .lic file next to WinOverlay.exe or use a licensed EXE."); return; }
        Log("LICENSE: id=" + lic.Id + " type=" + lic.Type + " expiry=" + lic.Expiry);

        string err = ValidateLicense(lic);
        if (err != null) { Log("LICENSE INVALID: " + err); Msg("License error: " + err); return; }
        Log("LICENSE: signature valid");

        string status = CheckIn(lic);
        Log("CHECKIN: " + status);
        if (status == "revoked") { Msg("License has been revoked."); return; }
        if (status == "expired")  { Msg("License has expired."); return; }
        if (status == "invalid")  { Msg("License rejected by server."); return; }

        Log("Calling RunForm...");
        RunForm(lic);
        Log("RunForm returned (app exited)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RunForm(LicenseData lic) => Application.Run(new OverlayForm(lic));

    static void Msg(string m) => MessageBox.Show(m, "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error);

    // ── Bundle extraction ─────────────────────────────────────────────────────
    static void ExtractBundledDlls()
    {
        var errors = new System.Text.StringBuilder();
        try
        {
            Wv2Dir = Path.Combine(Path.GetTempPath(), "WinOverlay_wv2_64");
            Directory.CreateDirectory(Wv2Dir);

            var asm = Assembly.GetExecutingAssembly();
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "WinOverlay.WebView2Loader.dll",  "WebView2Loader.dll" },
                { "WinOverlay.WV2Core.dll",         "Microsoft.Web.WebView2.Core.dll" },
                { "WinOverlay.WV2WinForms.dll",     "Microsoft.Web.WebView2.WinForms.dll" },
            };

            foreach (var kv in map)
            {
                string dest = Path.Combine(Wv2Dir, kv.Value);
                if (!File.Exists(dest))
                {
                    using var s = asm.GetManifestResourceStream(kv.Key);
                    if (s == null) { errors.AppendLine("MISSING resource: " + kv.Key); continue; }
                    using var f = File.Create(dest);
                    s.CopyTo(f);
                }
            }

            NativeMethods.SetDllDirectory(Wv2Dir);
            var hLib = NativeMethods.LoadLibrary(Path.Combine(Wv2Dir, "WebView2Loader.dll"));
            if (hLib == IntPtr.Zero)
            {
                int win32err = Marshal.GetLastWin32Error();
                errors.AppendLine("LoadLibrary WebView2Loader.dll failed (error " + win32err + ")");
            }
            else
            {
                Wv2DllLoaded = true;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    string name = new AssemblyName(e.Name).Name;
                    string path = Path.Combine(Wv2Dir, name + ".dll");
                    return File.Exists(path) ? Assembly.LoadFile(path) : null;
                }
                catch { return null; }
            };
        }
        catch (Exception ex) { errors.AppendLine("ExtractBundledDlls exception: " + ex.Message); }

        if (errors.Length > 0)
            MessageBox.Show("WebView2 DLL setup issue:\n\n" + errors + "\n\nWill fall back to IE11.",
                "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ── Read license ──────────────────────────────────────────────────────────
    static LicenseData ReadEmbeddedLicense()
    {
        try
        {
            string path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            string tail  = Encoding.UTF8.GetString(bytes, Math.Max(0, bytes.Length - 8192), Math.Min(8192, bytes.Length));
            int begin = tail.IndexOf("WDPMGR_LIC_BEGIN");
            int end   = tail.IndexOf("WDPMGR_LIC_END");
            if (begin < 0 || end <= begin) return null;
            return Parse(tail.Substring(begin + 16, end - begin - 16));
        }
        catch { return null; }
    }

    static LicenseData ReadLicFile()
    {
        try
        {
            string dir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? "";
            foreach (string f in Directory.GetFiles(dir, "*.lic"))
            {
                var ld = Parse(File.ReadAllText(f));
                if (ld != null && !string.IsNullOrEmpty(ld.Id)) return ld;
            }
        }
        catch { }
        return null;
    }

    static LicenseData Parse(string block)
    {
        var ld = new LicenseData();
        foreach (string line in block.Replace("\r\n", "\n").Split('\n'))
        {
            int ci = line.IndexOf('='); if (ci < 0) continue;
            string k = line.Substring(0, ci).Trim(), v = line.Substring(ci + 1).Trim();
            switch (k)
            {
                case "id":           ld.Id           = v; break;
                case "type":         ld.Type         = v; break;
                case "expiry":       ld.Expiry       = v; break;
                case "issued":       ld.Issued       = v; break;
                case "server":       ld.Server       = v; break;
                case "sig":          ld.Sig          = v; break;
                case "durationDays": int.TryParse(v, out ld.DurationDays); break;
                case "appId":        ld.AppId        = v; break;
                case "pubkey":
                    try { ld.PubKeyXml = Encoding.UTF8.GetString(Convert.FromBase64String(v)); } catch { ld.PubKeyXml = v; }
                    break;
            }
        }
        return string.IsNullOrEmpty(ld.Id) ? null : ld;
    }

    // ── Validate ──────────────────────────────────────────────────────────────
    static string ValidateLicense(LicenseData lic)
    {
        if (string.IsNullOrEmpty(lic.PubKeyXml)) return "missing public key";
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(lic.PubKeyXml);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(lic.Id), Convert.FromBase64String(lic.Sig),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return "signature invalid";
        }
        catch { return "signature check failed"; }
        if (lic.Type == "temp" && !string.IsNullOrEmpty(lic.Expiry))
            if (DateTime.TryParse(lic.Expiry, out var ed) && ed.ToUniversalTime() < DateTime.UtcNow)
                return "expired";
        return null;
    }

    // ── Check-in ──────────────────────────────────────────────────────────────
    internal static string CheckIn(LicenseData lic)
    {
        if (string.IsNullOrEmpty(lic.Server) || lic.Server.StartsWith("REPLACE")) return "ok";
        try
        {
            string fp   = GetFingerprint();
            string json = "{\"licenseId\":\"" + Esc(lic.Id) + "\","
                        + "\"fingerprint\":\"" + fp + "\","
                        + "\"hostname\":\"" + Esc(Environment.MachineName) + "\","
                        + "\"windowsUser\":\"" + Esc(Environment.UserName) + "\","
                        + "\"appId\":\"winoverlay\"}";
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            wc.Encoding = Encoding.UTF8;
            string resp = wc.UploadString(lic.Server.TrimEnd('/') + "/api/checkin", json);
            int si = resp.IndexOf("\"status\":\"");
            if (si >= 0) { int q1 = si + 10, q2 = resp.IndexOf('"', q1); if (q2 > q1) return resp.Substring(q1, q2 - q1); }
            return "ok";
        }
        catch { return "ok"; }
    }

    static string GetFingerprint()
    {
        try
        {
            string cpu = WmiGet("Win32_Processor", "ProcessorId");
            string mb  = WmiGet("Win32_BaseBoard", "SerialNumber");
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(cpu + "|" + mb));
            return BitConverter.ToString(h).Replace("-", "").ToLower().Substring(0, 32);
        }
        catch { return "unknown"; }
    }

    static string WmiGet(string cls, string prop)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT " + prop + " FROM " + cls);
            foreach (var o in s.Get()) return o[prop]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }

    static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}

// ── Overlay Form ──────────────────────────────────────────────────────────────
class OverlayForm : Form
{
    private WebView2   _wv2;
    private WebBrowser _wb;
    private bool       _wv2Ready;
    private string     _pendingUrl;
    private Panel      _toolbar;
    private LicenseData _lic;
    private System.Windows.Forms.Timer _checkinTimer;
    private TextBox    _urlBox;
    private byte       _opacity = 240;
    private NotifyIcon _tray;
    private bool       _autoHidden = false; // true when hidden by remote-session detection
    internal CoreWebView2Environment _wv2Env;
    private Bitmap     _screenshot = null;
    private bool       _dragging     = false;
    private Point      _dragStart;
    private Point      _formStart;
    private bool       _clickThrough = false;
    private bool       _ctAuto       = true;  // JS drives click-through automatically
    private Button     _btnCt;

    public OverlayForm(LicenseData lic)
    {
        _lic = lic;
        Text            = "WinOverlay";
        Size            = new Size(1280, 800);
        MinimumSize     = new Size(400, 300);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar   = false;
        TopMost         = true;
        BackColor       = Color.White;
        // Set before handle creation so WinForms puts WS_EX_LAYERED in CreateParams
        // (setting it after handle creation via SetWindowLong causes WebView2 to render black)
        Opacity         = _opacity / 255.0;

        // Set form icon from embedded app icon
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; } catch { }
        SetupTray();
        BuildToolbar();

        string wv2Ver = DetectOrInstallWebView2();

        if (!string.IsNullOrEmpty(wv2Ver))
        {
            Text = "WinOverlay [Chrome " + wv2Ver + "]";
            _wv2 = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_wv2);
            _toolbar.BringToFront();
            _ = InitWv2Async();
        }
        else
        {
            Text = "WinOverlay [IE11]";
            UseWebBrowser();
        }

        _checkinTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _checkinTimer.Tick += (s, e) =>
        {
            string st = Program.CheckIn(_lic);
            if (st == "revoked" || st == "expired" || st == "invalid")
            { MessageBox.Show("License " + st + ". Closing.", "WinOverlay"); Close(); }
        };
        _checkinTimer.Start();
    }

    // ── WebView2 detection ────────────────────────────────────────────────────
    // Prerequisites (VC++ runtime, WebView2) are installed automatically in
    // Program.EnsurePrerequisites() before this is ever called. This method
    // only detects the installed runtime and returns its version string,
    // or null to fall back to IE11.
    static string DetectOrInstallWebView2()
    {
        if (!Program.Wv2DllLoaded)
        {
            Program.Log("DetectOrInstallWebView2: Wv2DllLoaded=false → IE11 fallback");
            return null;
        }

        string ver = null;
        try { ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
        if (!string.IsNullOrEmpty(ver)) return ver;

        ver = TryDetectFromEdgePaths();
        if (!string.IsNullOrEmpty(ver)) return ver;

        Program.Log("DetectOrInstallWebView2: runtime not found after prereq check → IE11 fallback");
        return null;
    }

    static string TryDetectFromEdgePaths()
    {
        var basePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application"),
            // x64 Edge path (common on Windows Server 2022)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     "Microsoft", "Edge", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "EdgeWebView", "Application"),
        };
        foreach (var basePath in basePaths)
        {
            if (!Directory.Exists(basePath)) continue;
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                try
                {
                    string v = CoreWebView2Environment.GetAvailableBrowserVersionString(dir);
                    if (!string.IsNullOrEmpty(v))
                    {
                        // Store path so InitWv2Async can pass it to CreateAsync
                        Program.Wv2BrowserExeFolder = dir;
                        Program.Log("WV2: found runtime at " + dir);
                        return v;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    // ── System tray ──────────────────────────────────────────────────────────
    void SetupTray()
    {
        Icon appIcon = null;
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        _tray = new NotifyIcon
        {
            Text    = "WinOverlay  (Ctrl+Alt+W to hide/show)",
            Icon    = appIcon ?? SystemIcons.Application,
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide   Ctrl+Alt+W", null, (s, e) => ToggleVisibility());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close WinOverlay", null, (s, e) => Close());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => ToggleVisibility();
    }

    // Manual toggle — hides/shows from everywhere (local + remote)
    // Also used as the manual override when auto-hide is active
    void ToggleVisibility()
    {
        if (Visible)
        {
            _autoHidden = false; // manual hide overrides auto state
            ShowInTaskbar = false;
            Hide();
            _tray.ShowBalloonTip(2000, "WinOverlay Hidden",
                "Press Ctrl+Alt+W or double-click tray icon to restore.", ToolTipIcon.Info);
        }
        else
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    // Auto-hide triggered by remote session connect (WTS event)
    void AutoHide()
    {
        if (!Visible) return;
        _autoHidden = true;
        ShowInTaskbar = false;
        Hide();
    }

    // Auto-restore when remote session disconnects
    void AutoRestore()
    {
        if (!_autoHidden) return;
        _autoHidden = false;
        Show();
        WindowState = FormWindowState.Normal;
    }

    void BuildToolbar()
    {
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(30, 30, 30) };

        _urlBox = new TextBox { Left = 8, Top = 6, Width = 900, Height = 24,
            BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        _urlBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Navigate(_urlBox.Text.Trim()); e.SuppressKeyPress = true; } };

        var btnGo    = MakeBtn("Go",  40, Color.FromArgb(60,  60, 200));
        var btnSnap  = MakeBtn("📷",  32, Color.FromArgb(40, 100,  40));
        var btnPaste = MakeBtn("📋",  32, Color.FromArgb(30,  80, 140));
        _btnCt       = MakeBtn("⊕",  32, Color.FromArgb(80,  80,  80));
        var btnOpDown = MakeBtn("−",  28, Color.FromArgb(80,  80,  80));
        var btnOpUp   = MakeBtn("+",  28, Color.FromArgb(80,  80,  80));
        var btnClose  = MakeBtn("✕",  28, Color.FromArgb(180, 40,  40));

        btnGo.Click     += (s, e) => Navigate(_urlBox.Text.Trim());
        btnSnap.Click   += (s, e) => TakeScreenshot(btnSnap);
        btnPaste.Click  += (s, e) => PasteScreenshot();
        _btnCt.Click    += (s, e) => ToggleClickThrough();
        btnOpDown.Click += (s, e) => SetOpacity(Math.Max(40,  _opacity - 20));
        btnOpUp.Click   += (s, e) => SetOpacity(Math.Min(255, _opacity + 20));
        btnClose.Click  += (s, e) => Close();

        _toolbar.Resize += (s, e) => LayoutBtns(btnGo, btnSnap, btnPaste, _btnCt, btnOpDown, btnOpUp, btnClose);
        LayoutBtns(btnGo, btnSnap, btnPaste, _btnCt, btnOpDown, btnOpUp, btnClose);

        _toolbar.Controls.AddRange(new Control[] { _urlBox, btnGo, btnSnap, btnPaste, _btnCt, btnOpDown, btnOpUp, btnClose });
        Controls.Add(_toolbar);
    }

    void LayoutBtns(Button go, Button snap, Button paste, Button ct, Button od, Button ou, Button cl)
    {
        _urlBox.Width  = _toolbar.Width - 252;
        go.Left    = _urlBox.Right + 4;  go.Top    = 4;
        snap.Left  = go.Right    + 4;    snap.Top  = 4;
        paste.Left = snap.Right  + 2;    paste.Top = 4;
        ct.Left    = paste.Right + 6;    ct.Top    = 4;
        od.Left    = ct.Right    + 6;    od.Top    = 4;
        ou.Left    = od.Right    + 2;    ou.Top    = 4;
        cl.Left    = ou.Right    + 6;    cl.Top    = 4;
    }

    void ToggleClickThrough()
    {
        if (_ctAuto)
        {
            // Switch from auto → manual locked ON (always pass-through)
            _ctAuto = false; _clickThrough = true;
        }
        else if (_clickThrough)
        {
            // Locked ON → locked OFF (always capture)
            _clickThrough = false;
        }
        else
        {
            // Locked OFF → back to auto
            _ctAuto = true;
        }
        UpdateCtButton();
    }

    void UpdateCtButton()
    {
        if (_btnCt == null || _btnCt.IsDisposed) return;
        if (_ctAuto)
        {
            _btnCt.Text      = "⊕";
            _btnCt.BackColor = _clickThrough
                ? Color.FromArgb(0, 160, 0)       // auto: currently passing through
                : Color.FromArgb(60, 60, 60);      // auto: currently capturing
        }
        else if (_clickThrough)
        {
            _btnCt.Text      = "⊙";
            _btnCt.BackColor = Color.FromArgb(0, 130, 180); // locked pass-through
        }
        else
        {
            _btnCt.Text      = "⊗";
            _btnCt.BackColor = Color.FromArgb(150, 50, 50); // locked capture
        }
    }

    // ── Screenshot capture ────────────────────────────────────────────────────
    async void TakeScreenshot(Button btn)
    {
        btn.Enabled = false;

        // WDA_EXCLUDEFROMCAPTURE means CopyFromScreen already sees through this window —
        // no need to hide/show, no flicker. Just capture directly.
        try
        {
            Rectangle vscr = SystemInformation.VirtualScreen;
            var bmp = new Bitmap(vscr.Width, vscr.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vscr.Location, Point.Empty, vscr.Size,
                                 System.Drawing.CopyPixelOperation.SourceCopy);
            _screenshot?.Dispose();
            _screenshot = bmp;
            Program.Log("Screenshot: " + vscr.Width + "x" + vscr.Height);

            Clipboard.SetImage(_screenshot);
            btn.BackColor = Color.FromArgb(0, 140, 0);
            _ = System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                BeginInvoke((Action)(() => { if (!IsDisposed) btn.BackColor = Color.FromArgb(40, 100, 40); })));
        }
        catch (Exception ex) { Program.Log("Screenshot failed: " + ex.Message); }
        finally { btn.Enabled = true; }

        await System.Threading.Tasks.Task.CompletedTask; // keeps async signature for consistency
    }

    async void PasteScreenshot()
    {
        if (_screenshot == null) { Program.Log("Paste: no screenshot yet"); return; }

        try { Clipboard.SetImage(_screenshot); }
        catch (Exception ex) { Program.Log("Paste clipboard: " + ex.Message); return; }

        // Focus the browser control, let focus settle, then send Ctrl+V
        if (_wv2 != null && _wv2Ready)
        {
            _wv2.Focus();
            await System.Threading.Tasks.Task.Delay(80);
            SendKeys.Send("^v");
            Program.Log("Paste: Ctrl+V → WebView2");
        }
        else if (_wb != null)
        {
            _wb.Focus();
            await System.Threading.Tasks.Task.Delay(80);
            SendKeys.Send("^v");
        }
    }

    async System.Threading.Tasks.Task InitWv2Async()
    {
        try
        {
            string profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinOverlay_profile3");
            Program.Log("WV2: creating environment, profile=" + profileDir +
                        " browserExeFolder=" + (Program.Wv2BrowserExeFolder ?? "(system)"));
            var opts = new CoreWebView2EnvironmentOptions(
                "--disable-gpu --disable-gpu-compositing --use-gl=swiftshader");
            // Pass Wv2BrowserExeFolder when WebView2 was found via Edge path rather than registry
            _wv2Env = await CoreWebView2Environment.CreateAsync(Program.Wv2BrowserExeFolder, profileDir, opts);
            Program.Log("WV2: environment created, version=" + _wv2Env.BrowserVersionString);
            await _wv2.EnsureCoreWebView2Async(_wv2Env);
            Program.Log("WV2: CoreWebView2 ready");

            // Force light theme regardless of Windows system theme
            _wv2.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;

            var cfg = _wv2.CoreWebView2.Settings;
            cfg.IsScriptEnabled              = true;
            cfg.AreDefaultScriptDialogsEnabled   = true;
            cfg.IsWebMessageEnabled              = true;
            cfg.AreDefaultContextMenusEnabled    = true;
            cfg.IsStatusBarEnabled               = false;
            cfg.AreBrowserAcceleratorKeysEnabled = true;
            cfg.IsPasswordAutosaveEnabled        = true;
            cfg.IsGeneralAutofillEnabled         = true;
            // Use actual installed WebView2 version so UA matches Sec-CH-UA client hints
            string ver = _wv2Env.BrowserVersionString; // e.g. "150.0.4078.105"
            string major = ver.Contains(".") ? ver.Substring(0, ver.IndexOf('.')) : ver;
            cfg.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + major + ".0.0.0 Safari/537.36";

            // Spoofing + auto click-through detection.
            // IMPORTANT: capture postMessage BEFORE deleting window.chrome.webview,
            // otherwise the auto-detection calls silently fail.
            await _wv2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
var __wv2post=null;
try{__wv2post=window.chrome.webview.postMessage.bind(window.chrome.webview);}catch(e){}
try{delete window.chrome.webview;}catch(e){}
try{Object.defineProperty(navigator,'webdriver',{get:function(){return undefined;}});}catch(e){}
try{localStorage.setItem('theme','light');}catch(e){}
(function(){
    var last=null;
    function isInteractive(el){
        var n=el;
        while(n&&n.tagName){
            var t=n.tagName.toUpperCase(),
                r=(n.getAttribute&&n.getAttribute('role'))||'',
                ce=n.contentEditable;
            if(t==='INPUT'||t==='TEXTAREA'||t==='BUTTON'||t==='SELECT'||t==='A'||
               r==='button'||r==='textbox'||r==='combobox'||r==='searchbox'||
               ce==='true'||ce==='plaintext-only'){return true;}
            n=n.parentElement;
        }
        return false;
    }
    document.addEventListener('mousemove',function(e){
        var s=isInteractive(document.elementFromPoint(e.clientX,e.clientY))?'capture':'passthrough';
        if(s!==last){last=s;if(__wv2post)__wv2post(s);}
    },{passive:true});
    document.addEventListener('mouseleave',function(){
        if(last!=='passthrough'){last='passthrough';if(__wv2post)__wv2post('passthrough');}
    });
})();");

            // Receive auto click-through signals from the page
            _wv2.CoreWebView2.WebMessageReceived += (s, e) => {
                try {
                    string msg = e.TryGetWebMessageAsString();
                    BeginInvoke((Action)(() => {
                        if (_ctAuto) {
                            _clickThrough = (msg == "passthrough");
                            UpdateCtButton();
                        }
                    }));
                } catch { }
            };

            _wv2.CoreWebView2.NewWindowRequested += OnPopup;

            _wv2.NavigationCompleted += (s, e) => {
                try {
                    if (_wv2?.Source != null) {
                        bool ok = e.IsSuccess;
                        string url = _wv2.Source.ToString();
                        string errInfo = ok ? "" : " [ERR " + (int)e.WebErrorStatus + " " + e.WebErrorStatus + "]";
                        BeginInvoke((Action)(() => {
                            if (IsDisposed) return;
                            _urlBox.Text = url;
                            if (!ok) Text = "WinOverlay" + errInfo;
                        }));
                    }
                } catch { }
            };
            _wv2Ready = true;
            string navUrl = _pendingUrl ?? "https://www.google.com";
            Program.Log("WV2: navigating to " + navUrl);
            _wv2.CoreWebView2.Navigate(navUrl);
            _pendingUrl = null;
        }
        catch (Exception ex)
        {
            string msg = ex.GetType().Name + ": " + ex.Message;
            Program.Log("WV2 FAILED: " + msg);
            try { if (_wv2 != null) { Controls.Remove(_wv2); _wv2.Dispose(); _wv2 = null; } } catch { }
            if (!IsDisposed) BeginInvoke((Action)(() => {
                MessageBox.Show("WebView2 failed to start:\n\n" + msg + "\n\nLog: " + Program.LogPath,
                    "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UseWebBrowser();
            }));
        }
    }

    async void OnPopup(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var popup = new WinOverlayPopup();
            popup.Show(this);
            await popup.InitAsync(_wv2Env);
            e.NewWindow = popup.Wv2.CoreWebView2;
            e.Handled   = true;
        }
        catch { e.Handled = false; }
        finally { deferral.Complete(); }
    }

    void UseWebBrowser()
    {
        _wb = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };
        _wb.Navigated += (s, e) => { try { if (_wb?.Url != null) _urlBox.Text = _wb.Url.ToString(); } catch { } };
        Controls.Add(_wb);
        _toolbar.BringToFront();
        if (_pendingUrl != null) { _wb.Navigate(_pendingUrl); _pendingUrl = null; }
    }

    Button MakeBtn(string text, int width, Color bg) =>
        new Button { Text = text, Width = width, Height = 28, BackColor = bg, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Exclude from all screen capture paths (RDP, Zoom/Teams share, browser getDisplayMedia,
        // OBS, PrintScreen, etc.) while remaining fully visible on the physical display.
        // WDA_EXCLUDEFROMCAPTURE requires Windows 10 2004+ (build 19041); falls back gracefully.
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        NativeMethods.RegisterHotKey(Handle, NativeMethods.HOTKEY_TOGGLE,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, (uint)Keys.W);
        // Register for Terminal Services (RDP) session change events
        try { NativeMethods.WTSRegisterSessionNotification(Handle, NativeMethods.NOTIFY_FOR_ALL_SESSIONS); } catch { }
    }

    protected override void WndProc(ref Message m)
    {
        // Custom caption drag — prevents black ghost caused by WDA_EXCLUDEFROMCAPTURE:
        // DWM can't render the live drag preview (content is excluded), so we handle
        // the move ourselves and suppress the default DWM preview entirely.
        const int WM_NCHITTEST      = 0x0084;
        const int HTTRANSPARENT     = -1;
        const int WM_NCLBUTTONDOWN  = 0x00A1;
        const int WM_MOUSEMOVE      = 0x0200;
        const int WM_LBUTTONUP      = 0x0202;
        const int WM_CAPTURECHANGED = 0x0215;
        const int HTCAPTION         = 2;

        // Click-through: return HTTRANSPARENT for content area so mouse events
        // fall through to whatever window is behind the overlay.
        // Toolbar area always stays interactive so the user can toggle back.
        if (m.Msg == WM_NCHITTEST && _clickThrough)
        {
            int lp     = m.LParam.ToInt32();
            int sx     = (short)(lp & 0xFFFF);
            int sy     = (short)((lp >> 16) & 0xFFFF);
            Point cpt  = PointToClient(new Point(sx, sy));
            if (_toolbar == null || cpt.Y >= _toolbar.Bottom)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
        }

        if (m.Msg == WM_NCLBUTTONDOWN && m.WParam.ToInt32() == HTCAPTION)
        {
            _dragging  = true;
            _dragStart = Control.MousePosition;
            _formStart = Location;
            Capture    = true;
            return; // suppress default (which triggers DWM ghost)
        }
        if (m.Msg == WM_MOUSEMOVE && _dragging)
        {
            Point cur = Control.MousePosition;
            Location  = new Point(_formStart.X + cur.X - _dragStart.X,
                                  _formStart.Y + cur.Y - _dragStart.Y);
            return;
        }
        if ((m.Msg == WM_LBUTTONUP || m.Msg == WM_CAPTURECHANGED) && _dragging)
        {
            _dragging = false;
            Capture   = false;
            // fall through to base
        }

        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == NativeMethods.HOTKEY_TOGGLE)
        {
            ToggleVisibility();
        }
        else if (m.Msg == NativeMethods.WM_WTSSESSION_CHANGE)
        {
            int ev = m.WParam.ToInt32();
            if (ev == NativeMethods.WTS_REMOTE_CONNECT)
                BeginInvoke((Action)AutoHide);
            else if (ev == NativeMethods.WTS_REMOTE_DISCONNECT)
                BeginInvoke((Action)AutoRestore);
        }
        base.WndProc(ref m);
    }

    void Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        { /* full URL */ }
        else if (!url.Contains(" ") && url.Contains("."))
            url = "https://" + url;
        else
            url = "https://www.google.com/search?q=" + Uri.EscapeDataString(url);

        _urlBox.Text = url;

        if (_wv2 != null)
        { if (_wv2Ready) _wv2.CoreWebView2.Navigate(url); else _pendingUrl = url; }
        else if (_wb != null)
            _wb.Navigate(url);
        else
            _pendingUrl = url;
    }

    void SetOpacity(int val)
    {
        _opacity = (byte)val;
        Opacity = _opacity / 255.0;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _checkinTimer?.Stop();
        if (IsHandleCreated)
        {
            NativeMethods.UnregisterHotKey(Handle, NativeMethods.HOTKEY_TOGGLE);
            try { NativeMethods.WTSUnRegisterSessionNotification(Handle); } catch { }
        }
        _tray?.Dispose();
        _screenshot?.Dispose();
        base.OnFormClosed(e);
    }
}

// ── Popup window (login / OAuth flows) ───────────────────────────────────────
class WinOverlayPopup : Form
{
    internal WebView2 Wv2 { get; } = new WebView2 { Dock = DockStyle.Fill };

    public WinOverlayPopup()
    {
        Text            = "WinOverlay";
        Size            = new Size(520, 640);
        MinimumSize     = new Size(400, 400);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        TopMost         = true;
        Controls.Add(Wv2);
    }

    public async System.Threading.Tasks.Task InitAsync(CoreWebView2Environment env)
    {
        await Wv2.EnsureCoreWebView2Async(env);
        Wv2.CoreWebView2.Settings.IsScriptEnabled            = true;
        Wv2.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
        Wv2.CoreWebView2.WindowCloseRequested += (s, e) => { if (!IsDisposed) BeginInvoke((Action)Close); };
        Wv2.CoreWebView2.NewWindowRequested += (s, e) => { e.Handled = true; Wv2.CoreWebView2.Navigate(e.Uri); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }
}
