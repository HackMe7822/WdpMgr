using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// ── Win32 imports ─────────────────────────────────────────────────────────────
static class NativeMethods
{
    public const uint WDA_NONE               = 0x00000000;
    public const uint WDA_MONITOR            = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")] public static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    public const int GWL_EXSTYLE   = -20;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const uint LWA_ALPHA    = 0x00000002;
}

// ── License data ──────────────────────────────────────────────────────────────
class LicenseData
{
    public string Id           = "";
    public string Type         = "";
    public string Expiry       = "";
    public string Issued       = "";
    public string Server       = "";
    public string Sig          = "";
    public string PubKeyXml    = "";
    public int    DurationDays = 0;
    public string AppId        = "";
}

// ── Entry point ───────────────────────────────────────────────────────────────
static class Program
{
    internal static string LicFilePath = "";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Try loading license from embedded tail first, then from .lic file next to EXE
        LicenseData lic = ReadEmbeddedLicense() ?? ReadLicFile();
        if (lic == null)
        {
            MessageBox.Show(
                "No valid license found.\n\nPlace a .lic file next to WinOverlay.exe or use a licensed EXE.",
                "WinOverlay — License Missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string err = ValidateLicense(lic);
        if (err != null)
        {
            MessageBox.Show("License error: " + err, "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Initial check-in
        string status = CheckIn(lic);
        if (status == "revoked")
        { MessageBox.Show("License has been revoked.", "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (status == "expired")
        { MessageBox.Show("License has expired.", "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (status == "invalid")
        { MessageBox.Show("License rejected by server.", "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        Application.Run(new OverlayForm(lic));
    }

    // ── License reading ───────────────────────────────────────────────────────
    static LicenseData ReadEmbeddedLicense()
    {
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return null;
            byte[] bytes = File.ReadAllBytes(exePath);
            string tail  = Encoding.UTF8.GetString(bytes, Math.Max(0, bytes.Length - 8192), Math.Min(8192, bytes.Length));
            int begin = tail.IndexOf("WDPMGR_LIC_BEGIN");
            int end   = tail.IndexOf("WDPMGR_LIC_END");
            if (begin < 0 || end <= begin) return null;
            string block = tail.Substring(begin + "WDPMGR_LIC_BEGIN".Length, end - begin - "WDPMGR_LIC_BEGIN".Length);
            return ParseLicBlock(block);
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
                LicenseData ld = ParseLicBlock(File.ReadAllText(f));
                if (ld != null && !string.IsNullOrEmpty(ld.Id)) { LicFilePath = f; return ld; }
            }
        }
        catch { }
        return null;
    }

    static LicenseData ParseLicBlock(string block)
    {
        var ld = new LicenseData();
        foreach (string line in block.Replace("\r\n", "\n").Split('\n'))
        {
            int ci = line.IndexOf('=');
            if (ci < 0) continue;
            string k = line.Substring(0, ci).Trim();
            string v = line.Substring(ci + 1).Trim();
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

    // ── License validation ────────────────────────────────────────────────────
    static string ValidateLicense(LicenseData lic)
    {
        if (string.IsNullOrEmpty(lic.PubKeyXml)) return "missing public key";
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(lic.PubKeyXml);
            byte[] payload = Encoding.UTF8.GetBytes(lic.Id);
            byte[] sig     = Convert.FromBase64String(lic.Sig);
            if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return "signature invalid";
        }
        catch { return "signature check failed"; }

        if (lic.Type == "temp" && !string.IsNullOrEmpty(lic.Expiry))
            if (DateTime.TryParse(lic.Expiry, out var ed) && ed.ToUniversalTime() < DateTime.UtcNow)
                return "expired";

        if (lic.Type == "days" && lic.DurationDays > 0)
        {
            // Days-type expiry validated by server check-in
        }

        return null;
    }

    // ── Server check-in ───────────────────────────────────────────────────────
    internal static string CheckIn(LicenseData lic)
    {
        if (string.IsNullOrEmpty(lic.Server) || lic.Server.StartsWith("REPLACE")) return "ok";
        try
        {
            string fp   = GetFingerprint();
            string host = EscapeJson(Environment.MachineName);
            string user = EscapeJson(Environment.UserName);
            string json = "{\"licenseId\":\"" + EscapeJson(lic.Id) + "\","
                        + "\"fingerprint\":\"" + fp + "\","
                        + "\"hostname\":\"" + host + "\","
                        + "\"windowsUser\":\"" + user + "\","
                        + "\"appId\":\"winoverlay\"}";
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            wc.Encoding = Encoding.UTF8;
            string resp = wc.UploadString(lic.Server.TrimEnd('/') + "/api/checkin", json);
            int si = resp.IndexOf("\"status\":");
            if (si >= 0)
            {
                int q1 = resp.IndexOf('"', si + 9);
                int q2 = q1 >= 0 ? resp.IndexOf('"', q1 + 1) : -1;
                if (q1 >= 0 && q2 > q1) return resp.Substring(q1 + 1, q2 - q1 - 1);
            }
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
            string raw = cpu + "|" + mb;
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return BitConverter.ToString(h).Replace("-","").ToLower().Substring(0, 32);
        }
        catch { return "unknown"; }
    }

    static string WmiGet(string cls, string prop)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT " + prop + " FROM " + cls);
            foreach (var obj in searcher.Get())
                return obj[prop]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }

    static string EscapeJson(string s) =>
        (s ?? "").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","\\r");

    internal static void Log(string msg) =>
        System.Diagnostics.Debug.WriteLine("[WinOverlay] " + msg);
}

// ── Overlay Form ──────────────────────────────────────────────────────────────
class OverlayForm : Form
{
    private WebView2    _browser;
    private LicenseData _lic;
    private System.Windows.Forms.Timer _checkinTimer;
    private System.Windows.Forms.Timer _resizeTimer;
    private Panel _toolbar;
    private TextBox _urlBox;
    private byte _opacity = 240;

    public OverlayForm(LicenseData lic)
    {
        _lic = lic;
        InitForm();
    }

    void InitForm()
    {
        Text            = "WinOverlay";
        Size            = new Size(1280, 800);
        MinimumSize     = new Size(400, 300);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        TopMost         = true;
        BackColor       = Color.Black;

        // Make window invisible to screen capture
        ApplyDisplayAffinity();

        // Toolbar
        _toolbar            = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(30,30,30) };
        _urlBox             = new TextBox { Left=8, Top=6, Width=Width-220, Height=24, BackColor=Color.FromArgb(50,50,50), ForeColor=Color.White, BorderStyle=BorderStyle.FixedSingle };
        _urlBox.KeyDown    += (s,e) => { if (e.KeyCode==Keys.Enter) { Navigate(_urlBox.Text.Trim()); e.SuppressKeyPress=true; } };
        var btnGo           = new Button { Text="Go", Left=_urlBox.Right+4, Top=4, Width=40, Height=28, BackColor=Color.FromArgb(60,60,200), ForeColor=Color.White, FlatStyle=FlatStyle.Flat };
        btnGo.Click        += (s,e) => Navigate(_urlBox.Text.Trim());
        var btnOpDown       = new Button { Text="−", Left=btnGo.Right+4, Top=4, Width=28, Height=28, BackColor=Color.FromArgb(80,80,80), ForeColor=Color.White, FlatStyle=FlatStyle.Flat };
        btnOpDown.Click    += (s,e) => SetOpacity(Math.Max(40, _opacity - 20));
        var btnOpUp         = new Button { Text="+", Left=btnOpDown.Right+2, Top=4, Width=28, Height=28, BackColor=Color.FromArgb(80,80,80), ForeColor=Color.White, FlatStyle=FlatStyle.Flat };
        btnOpUp.Click      += (s,e) => SetOpacity(Math.Min(255, _opacity + 20));
        var btnClose        = new Button { Text="✕", Left=btnOpUp.Right+6, Top=4, Width=28, Height=28, BackColor=Color.FromArgb(180,40,40), ForeColor=Color.White, FlatStyle=FlatStyle.Flat };
        btnClose.Click     += (s,e) => Close();
        _toolbar.Controls.AddRange(new Control[]{_urlBox, btnGo, btnOpDown, btnOpUp, btnClose});
        _toolbar.Resize    += (s,e) => { _urlBox.Width = _toolbar.Width - 130; };
        Controls.Add(_toolbar);

        // WebView2 browser
        _browser = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_browser);

        // Resize timer to reanchor browser after drag
        _resizeTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _resizeTimer.Tick += (s,e) => { _resizeTimer.Stop(); ApplyDisplayAffinity(); };
        Resize += (s,e) => { _resizeTimer.Stop(); _resizeTimer.Start(); };

        // Periodic check-in every 5 minutes
        _checkinTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _checkinTimer.Tick += (s,e) => {
            string st = Program.CheckIn(_lic);
            if (st == "revoked" || st == "expired" || st == "invalid")
            { MessageBox.Show("License " + st + ". Closing.", "WinOverlay"); Close(); }
        };
        _checkinTimer.Start();

        // Init WebView2 async
        InitWebView();
    }

    async void InitWebView()
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinOverlay");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _browser.EnsureCoreWebView2Async(env);
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _browser.CoreWebView2.NavigationCompleted += (s,e) => {
                if (_browser.Source != null) _urlBox.Text = _browser.Source.ToString();
            };
            Navigate("about:blank");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "WebView2 init failed: " + ex.Message + "\n\nInstall Microsoft Edge WebView2 Runtime.",
                "WinOverlay Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    void Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        _urlBox.Text = url;
        try { _browser.CoreWebView2?.Navigate(url); } catch { }
    }

    void ApplyDisplayAffinity()
    {
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        // Also set layered for opacity support
        int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_LAYERED);
        NativeMethods.SetLayeredWindowAttributes(Handle, 0, _opacity, NativeMethods.LWA_ALPHA);
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
        base.OnFormClosed(e);
    }
}
