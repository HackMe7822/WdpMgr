using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ── Win32 ─────────────────────────────────────────────────────────────────────
static class NativeMethods
{
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    public const int  GWL_EXSTYLE            = -20;
    public const int  WS_EX_LAYERED          = 0x80000;
    public const uint LWA_ALPHA              = 0x00000002;

    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")] public static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
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
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Force IE11 rendering engine (default is IE7 which breaks modern sites)
        try {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", true)
                ?? Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            key?.SetValue(Path.GetFileName(Application.ExecutablePath), 11001, RegistryValueKind.DWord);
        } catch { }

        LicenseData lic = ReadEmbeddedLicense() ?? ReadLicFile();
        if (lic == null) { Msg("No valid license found.\n\nPlace a .lic file next to WinOverlay.exe or use a licensed EXE."); return; }

        string err = ValidateLicense(lic);
        if (err != null) { Msg("License error: " + err); return; }

        string status = CheckIn(lic);
        if (status == "revoked") { Msg("License has been revoked."); return; }
        if (status == "expired") { Msg("License has expired."); return; }
        if (status == "invalid") { Msg("License rejected by server."); return; }

        Application.Run(new OverlayForm(lic));
    }

    static void Msg(string m) => MessageBox.Show(m, "WinOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error);

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
    private WebBrowser  _browser;
    private LicenseData _lic;
    private System.Windows.Forms.Timer _checkinTimer;
    private TextBox     _urlBox;
    private byte        _opacity = 240;

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

        // Toolbar
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(30, 30, 30) };

        _urlBox = new TextBox { Left = 8, Top = 6, Width = 900, Height = 24, BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        _urlBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Navigate(_urlBox.Text.Trim()); e.SuppressKeyPress = true; } };

        var btnGo = MakeBtn("Go", 40, Color.FromArgb(60, 60, 200));
        btnGo.Click += (s, e) => Navigate(_urlBox.Text.Trim());

        var btnOpDown = MakeBtn("−", 28, Color.FromArgb(80, 80, 80));
        btnOpDown.Click += (s, e) => SetOpacity(Math.Max(40, _opacity - 20));

        var btnOpUp = MakeBtn("+", 28, Color.FromArgb(80, 80, 80));
        btnOpUp.Click += (s, e) => SetOpacity(Math.Min(255, _opacity + 20));

        var btnClose = MakeBtn("✕", 28, Color.FromArgb(180, 40, 40));
        btnClose.Click += (s, e) => Close();

        // Position controls
        toolbar.Resize += (s, e) =>
        {
            _urlBox.Width    = toolbar.Width - 140;
            btnGo.Left       = _urlBox.Right + 4;
            btnOpDown.Left   = btnGo.Right + 4;
            btnOpUp.Left     = btnOpDown.Right + 2;
            btnClose.Left    = btnOpUp.Right + 6;
        };
        _urlBox.Width    = 900;
        btnGo.Left       = _urlBox.Right + 4; btnGo.Top = 4;
        btnOpDown.Left   = btnGo.Right + 4;   btnOpDown.Top = 4;
        btnOpUp.Left     = btnOpDown.Right + 2; btnOpUp.Top = 4;
        btnClose.Left    = btnOpUp.Right + 6;  btnClose.Top = 4;

        toolbar.Controls.AddRange(new Control[] { _urlBox, btnGo, btnOpDown, btnOpUp, btnClose });

        // Browser — built-in, zero dependencies
        _browser = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };
        _browser.Navigated += (s, e) => { if (_browser.Url != null) _urlBox.Text = _browser.Url.ToString(); };

        Controls.Add(_browser);
        Controls.Add(toolbar);
        toolbar.BringToFront();

        // Check-in timer every 5 min
        _checkinTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _checkinTimer.Tick += (s, e) =>
        {
            string st = Program.CheckIn(_lic);
            if (st == "revoked" || st == "expired" || st == "invalid")
            { MessageBox.Show("License " + st + ". Closing.", "WinOverlay"); Close(); }
        };
        _checkinTimer.Start();
    }

    Button MakeBtn(string text, int width, Color bg)
    {
        return new Button { Text = text, Width = width, Height = 28, BackColor = bg, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyAffinity();
    }

    void ApplyAffinity()
    {
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_LAYERED);
        NativeMethods.SetLayeredWindowAttributes(Handle, 0, _opacity, NativeMethods.LWA_ALPHA);
    }

    void Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // already a full URL
        }
        else if (!url.Contains(" ") && url.Contains("."))
        {
            url = "https://" + url;
        }
        else
        {
            url = "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
        }
        _urlBox.Text = url;
        _browser.Navigate(url);
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
