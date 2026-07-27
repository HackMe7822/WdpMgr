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
    public const int  GWL_EXSTYLE            = -20;
    public const int  WS_EX_LAYERED          = 0x80000;
    public const uint LWA_ALPHA              = 0x00000002;
    public const uint MOD_CONTROL            = 0x0002;
    public const uint MOD_ALT               = 0x0001;
    public const int  WM_HOTKEY             = 0x0312;
    public const int  HOTKEY_TOGGLE         = 1;

    [DllImport("user32.dll")]   public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")]   public static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]   public static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]   public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")]   public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]   public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);
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
    internal static string Wv2Dir; // temp dir holding extracted WebView2 DLLs

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // IE11 fallback emulation key (used only if WebView2 init fails)
        try {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true)
                ?? Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            key?.SetValue(Path.GetFileName(Application.ExecutablePath), 11001, RegistryValueKind.DWord);
        } catch { }

        // Extract bundled WebView2 DLLs BEFORE any WebView2 type is JIT-compiled
        ExtractBundledDlls();

        LicenseData lic = ReadEmbeddedLicense() ?? ReadLicFile();
        if (lic == null) { Msg("No valid license found.\n\nPlace a .lic file next to WinOverlay.exe or use a licensed EXE."); return; }

        string err = ValidateLicense(lic);
        if (err != null) { Msg("License error: " + err); return; }

        string status = CheckIn(lic);
        if (status == "revoked") { Msg("License has been revoked."); return; }
        if (status == "expired")  { Msg("License has expired."); return; }
        if (status == "invalid")  { Msg("License rejected by server."); return; }

        // RunForm is in a separate non-inlined method so WebView2 types are only
        // JIT-compiled AFTER ExtractBundledDlls() has registered the AssemblyResolve hook
        RunForm(lic);
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
            if (hLib == IntPtr.Zero) errors.AppendLine("LoadLibrary WebView2Loader.dll FAILED (error " + Marshal.GetLastWin32Error() + ")");

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
    internal CoreWebView2Environment _wv2Env;

    public OverlayForm(LicenseData lic)
    {
        _lic = lic;
        Text            = "WinOverlay";
        Size            = new Size(1280, 800);
        MinimumSize     = new Size(400, 300);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        TopMost         = true;
        BackColor       = Color.Black;

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

    // ── WebView2 auto-install ─────────────────────────────────────────────────
    static string DetectOrInstallWebView2()
    {
        // Try to detect existing runtime
        string ver = null;
        string detectErr = null;
        try { ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) { detectErr = ex.GetType().Name + ": " + ex.Message; }

        if (!string.IsNullOrEmpty(ver)) return ver;

        // Not found — offer to install
        string msg = "WebView2 Runtime not found (required for Chrome rendering).\n";
        if (!string.IsNullOrEmpty(detectErr)) msg += "Detail: " + detectErr + "\n";
        msg += "\nDownload and install automatically?\n(~2 MB from Microsoft — internet required)";

        if (MessageBox.Show(msg, "WinOverlay — Install WebView2",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return null;

        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var wc = new WebClient())
                wc.DownloadFile("https://go.microsoft.com/fwlink/p/?LinkId=2124703", tmp);

            var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(tmp, "/install /silent")
                { UseShellExecute = true });
            p?.WaitForExit(120000);

            // Retry detection
            string ver2 = null;
            try { ver2 = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (!string.IsNullOrEmpty(ver2)) return ver2;

            MessageBox.Show("WebView2 installed. Please restart WinOverlay for Chrome rendering to activate.",
                "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Install failed: " + ex.Message + "\n\nFalling back to IE11 mode.",
                "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
    }

    // ── System tray ──────────────────────────────────────────────────────────
    void SetupTray()
    {
        _tray = new NotifyIcon
        {
            Text    = "WinOverlay  (Ctrl+Alt+W to hide/show)",
            Icon    = SystemIcons.Application,
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide   Ctrl+Alt+W", null, (s, e) => ToggleVisibility());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close WinOverlay", null, (s, e) => Close());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => ToggleVisibility();
    }

    // Ctrl+Alt+W: fully hides WinOverlay from screen AND taskbar (invisible in remote sessions)
    void ToggleVisibility()
    {
        if (Visible)
        {
            ShowInTaskbar = false;
            Hide();
            _tray.ShowBalloonTip(2000, "WinOverlay Hidden",
                "Press Ctrl+Alt+W or double-click tray icon to restore.", ToolTipIcon.Info);
        }
        else
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    void BuildToolbar()
    {
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(30, 30, 30) };

        _urlBox = new TextBox { Left = 8, Top = 6, Width = 900, Height = 24,
            BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        _urlBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Navigate(_urlBox.Text.Trim()); e.SuppressKeyPress = true; } };

        var btnGo     = MakeBtn("Go", 40, Color.FromArgb(60, 60, 200));
        var btnOpDown = MakeBtn("−",  28, Color.FromArgb(80, 80, 80));
        var btnOpUp   = MakeBtn("+",  28, Color.FromArgb(80, 80, 80));
        var btnClose  = MakeBtn("✕",  28, Color.FromArgb(180, 40, 40));

        btnGo.Click     += (s, e) => Navigate(_urlBox.Text.Trim());
        btnOpDown.Click += (s, e) => SetOpacity(Math.Max(40,  _opacity - 20));
        btnOpUp.Click   += (s, e) => SetOpacity(Math.Min(255, _opacity + 20));
        btnClose.Click  += (s, e) => Close();

        _toolbar.Resize += (s, e) => LayoutBtns(btnGo, btnOpDown, btnOpUp, btnClose);
        LayoutBtns(btnGo, btnOpDown, btnOpUp, btnClose);

        _toolbar.Controls.AddRange(new Control[] { _urlBox, btnGo, btnOpDown, btnOpUp, btnClose });
        Controls.Add(_toolbar);
    }

    void LayoutBtns(Button go, Button od, Button ou, Button cl)
    {
        _urlBox.Width = _toolbar.Width - 140;
        go.Left = _urlBox.Right + 4;  go.Top = 4;
        od.Left = go.Right + 4;       od.Top = 4;
        ou.Left = od.Right + 2;       ou.Top = 4;
        cl.Left = ou.Right + 6;       cl.Top = 4;
    }

    async System.Threading.Tasks.Task InitWv2Async()
    {
        try
        {
            // Persistent profile so cookies/logins survive across runs
            string profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinOverlay_profile");
            _wv2Env = await CoreWebView2Environment.CreateAsync(null, profileDir, null);
            await _wv2.EnsureCoreWebView2Async(_wv2Env);

            var cfg = _wv2.CoreWebView2.Settings;
            cfg.IsScriptEnabled              = true;
            cfg.AreDefaultScriptDialogsEnabled   = true;
            cfg.IsWebMessageEnabled              = true;
            cfg.AreDefaultContextMenusEnabled    = true;
            cfg.IsStatusBarEnabled               = false;
            cfg.AreBrowserAcceleratorKeysEnabled = false;

            _wv2.CoreWebView2.NewWindowRequested += OnPopup;
            _wv2.NavigationCompleted += (s, e) => {
                try { if (_wv2?.Source != null) BeginInvoke((Action)(() => { if (!IsDisposed) _urlBox.Text = _wv2.Source.ToString(); })); } catch { }
            };
            _wv2Ready = true;
            if (_pendingUrl != null) { _wv2.CoreWebView2.Navigate(_pendingUrl); _pendingUrl = null; }
        }
        catch
        {
            try { if (_wv2 != null) { Controls.Remove(_wv2); _wv2.Dispose(); _wv2 = null; } } catch { }
            if (!IsDisposed) BeginInvoke((Action)UseWebBrowser);
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
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_NONE);
        int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_LAYERED);
        NativeMethods.SetLayeredWindowAttributes(Handle, 0, _opacity, NativeMethods.LWA_ALPHA);
        // Ctrl+Alt+W — hide/show WinOverlay (useful when an admin remotes in)
        NativeMethods.RegisterHotKey(Handle, NativeMethods.HOTKEY_TOGGLE,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, (uint)Keys.W);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == NativeMethods.HOTKEY_TOGGLE)
            ToggleVisibility();
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
        int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_LAYERED);
        NativeMethods.SetLayeredWindowAttributes(Handle, 0, _opacity, NativeMethods.LWA_ALPHA);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _checkinTimer?.Stop();
        if (IsHandleCreated) NativeMethods.UnregisterHotKey(Handle, NativeMethods.HOTKEY_TOGGLE);
        _tray?.Dispose();
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
        // Nested popups just navigate in this same window
        Wv2.CoreWebView2.NewWindowRequested += (s, e) => { e.Handled = true; Wv2.CoreWebView2.Navigate(e.Uri); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_NONE);
    }
}
