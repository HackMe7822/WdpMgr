// WdpMgr License Server — ASP.NET Core 8
// License types: lifetime | temp (fixed date) | days (N days from activation) | hr (per-seat)

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

// ── Config ────────────────────────────────────────────────────────────────────
string masterKey  = Env("WDPMGR_ADMIN_KEY", "changeme");
string dbPath     = Env("WDPMGR_DB_PATH",   "wdpmgr.db");
int    port       = int.TryParse(Env("PORT","5000"), out var _p) ? _p : 5000;
string firstUser  = Env("WDPMGR_FIRST_USER", "");
string firstPass  = Env("WDPMGR_FIRST_PASS", "");

if (masterKey == "changeme") Console.WriteLine("[WARN] Set WDPMGR_ADMIN_KEY environment variable!");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Host.UseWindowsService(o => o.ServiceName = "WdpMgrServer");
var app = builder.Build();

// ── DB init ───────────────────────────────────────────────────────────────────
DB.Init(dbPath, firstUser, firstPass);
Console.WriteLine($"[INFO] DB: {Path.GetFullPath(dbPath)}  |  http://localhost:{port}");

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions {
    OnPrepareResponse = ctx => {
        // JS/CSS/HTML: no caching so updates are always picked up
        var ext = Path.GetExtension(ctx.File.Name).ToLowerInvariant();
        if (ext is ".js" or ".css" or ".html") {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"]        = "no-cache";
        }
    }
});

// ── Auth ──────────────────────────────────────────────────────────────────────
bool AdminOk(HttpContext ctx) {
    if (!ctx.Request.Headers.TryGetValue("X-Admin-Key", out var v)) return false;
    string key = v.ToString();
    if (key == masterKey) return true;
    using var db = DB.Open(dbPath);
    return DB.ApiKeyValid(db, key);
}
IResult Unauth() => Results.Json(new { error = "Unauthorized" }, statusCode: 401);

// ── Admin login (username + password → api key) ───────────────────────────────
app.MapPost("/api/admin/login", async (HttpContext ctx) => {
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    string username = S(doc.RootElement, "username");
    string password = S(doc.RootElement, "password");
    using var db    = DB.Open(dbPath);
    var key = DB.Login(db, username, password);
    if (key == null) return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
    return Results.Json(new { apiKey = key });
});

// ── Admin users ───────────────────────────────────────────────────────────────
app.MapGet("/api/admin/users", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetAdminUsers(db));
});
app.MapPost("/api/admin/users", async (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    string username = S(doc.RootElement, "username");
    string password = S(doc.RootElement, "password");
    string role     = S(doc.RootElement, "role", "admin");
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        return Results.Json(new { error = "username and password required" }, statusCode: 400);
    using var db = DB.Open(dbPath);
    if (DB.UserExists(db, username))
        return Results.Json(new { error = "username already exists" }, statusCode: 409);
    string id     = Guid.NewGuid().ToString();
    string apiKey = Guid.NewGuid().ToString("N");
    DB.CreateAdminUser(db, id, username, password, role, apiKey);
    return Results.Json(new { id, username, role, apiKey });
});
app.MapDelete("/api/admin/users/{id}", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.DeleteAdminUser(db, id);
    return Results.Json(new { ok = true });
});
app.MapPost("/api/admin/users/{id}/reset-key", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db  = DB.Open(dbPath);
    string newKey = Guid.NewGuid().ToString("N");
    DB.ResetApiKey(db, id, newKey);
    return Results.Json(new { apiKey = newKey });
});

// ── Apps management ───────────────────────────────────────────────────────────
app.MapGet("/api/admin/apps", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetApps(db));
});
app.MapPost("/api/admin/apps", async (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    string name = S(doc.RootElement, "name");
    string desc = S(doc.RootElement, "description");
    if (string.IsNullOrWhiteSpace(name))
        return Results.Json(new { error = "name required" }, statusCode: 400);
    string id = Guid.NewGuid().ToString();
    using var db = DB.Open(dbPath);
    DB.CreateApp(db, id, name, desc);
    return Results.Json(new { id, name, description = desc });
});
app.MapDelete("/api/admin/apps/{id}", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.DeleteApp(db, id);
    return Results.Json(new { ok = true });
});

// ── Stats ─────────────────────────────────────────────────────────────────────
app.MapGet("/api/admin/stats", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetStats(db));
});

// ── Licenses ──────────────────────────────────────────────────────────────────
app.MapGet("/api/admin/licenses", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetLicenses(db));
});
app.MapPost("/api/admin/licenses", async (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r          = doc.RootElement;
    string label   = S(r, "label");
    string type    = S(r, "type", "lifetime");   // lifetime|temp|days|hr
    string expiry  = S(r, "expiry");             // for temp
    string notes   = S(r, "notes");
    string appId   = S(r, "appId");
    int maxAct     = I(r, "maxActivations", 1);  // machines (or seats for hr)
    int durDays    = I(r, "durationDays", 0);    // for days type
    if (string.IsNullOrWhiteSpace(label))
        return Results.Json(new { error = "label required" }, statusCode: 400);
    if (!new[]{"lifetime","temp","days","hr"}.Contains(type))
        return Results.Json(new { error = "type must be lifetime|temp|days|hr" }, statusCode: 400);
    if (type == "temp" && string.IsNullOrWhiteSpace(expiry))
        return Results.Json(new { error = "expiry required for temp license" }, statusCode: 400);
    if (type == "days" && durDays < 1)
        return Results.Json(new { error = "durationDays >= 1 required for days license" }, statusCode: 400);
    string id     = Guid.NewGuid().ToString();
    string issued = DateTime.UtcNow.ToString("yyyy-MM-dd");
    using var db  = DB.Open(dbPath);
    DB.CreateLicense(db, id, label, type, expiry, issued, maxAct, durDays, appId, notes);
    return Results.Json(new { id, label, type, expiry, issued, maxActivations=maxAct, durationDays=durDays, appId, notes, revoked=false });
});
app.MapDelete("/api/admin/licenses/{id}", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.RevokeLicense(db, id);
    return Results.Json(new { ok = true });
});
app.MapPut("/api/admin/licenses/{id}", async (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var doc   = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r           = doc.RootElement;
    string label    = S(r, "label");
    string expiry   = S(r, "expiry");
    string notes    = S(r, "notes");
    int maxAct      = I(r, "maxActivations", 1);
    int durDays     = I(r, "durationDays", 0);
    if (string.IsNullOrWhiteSpace(label))
        return Results.Json(new { error = "label required" }, statusCode: 400);
    using var db = DB.Open(dbPath);
    DB.UpdateLicense(db, id, label, expiry, notes, maxAct, durDays);
    return Results.Json(new { ok = true });
});
app.MapPost("/api/admin/licenses/{id}/reactivate", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.ReactivateLicense(db, id);
    return Results.Json(new { ok = true });
});
app.MapPost("/api/admin/licenses/{id}/block", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.RevokeLicense(db, id);
    return Results.Json(new { ok = true });
});
app.MapDelete("/api/admin/licenses/{id}/purge", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.PurgeLicense(db, id);
    return Results.Json(new { ok = true });
});

// ── Machines ──────────────────────────────────────────────────────────────────
app.MapGet("/api/admin/machines", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetMachines(db));
});
app.MapDelete("/api/admin/machines/{id}", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.DeleteMachine(db, id);
    return Results.Json(new { ok = true });
});

// ── Download bundled EXE (base EXE + embedded license + pubkey) ───────────────
app.MapGet("/api/admin/licenses/{id}/download", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    var lic = DB.GetLicenseById(db, id);
    if (lic == null) return Results.Json(new { error = "not found" }, statusCode: 404);
    if (lic.Revoked) return Results.Json(new { error = "revoked" }, statusCode: 400);
    string baseExePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "WdpMgr_base.exe");
    if (!File.Exists(baseExePath))
        return Results.Json(new { error = "Base WdpMgr.exe not uploaded yet. Go to Settings → Upload EXE." }, statusCode: 400);
    string serverUrl = DB.GetSetting(db, "server_url");
    if (string.IsNullOrEmpty(serverUrl)) {
        string proto = ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var xfp2) ? xfp2.ToString() : ctx.Request.Scheme;
        serverUrl = $"{proto}://{ctx.Request.Host}";
    }
    byte[] bundled = RsaSvc.GenerateBundledExe(db, lic, serverUrl, File.ReadAllBytes(baseExePath));
    string fname = "WdpMgr_" + new string(lic.Label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".exe";
    return Results.File(bundled, "application/octet-stream", fname);
});

// ── EXE upload ────────────────────────────────────────────────────────────────
app.MapPost("/api/admin/exe/upload", async (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    if (!ctx.Request.HasFormContentType) return Results.BadRequest();
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files["exe"];
    if (file == null || file.Length == 0) return Results.Json(new { error = "no file" }, statusCode: 400);
    string dest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "WdpMgr_base.exe");
    using var fs = File.Create(dest);
    await file.CopyToAsync(fs);
    return Results.Json(new { ok = true, size = file.Length });
});

// ── EXE upload status ─────────────────────────────────────────────────────────
app.MapGet("/api/admin/exe/info", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "WdpMgr_base.exe");
    bool exists = File.Exists(p);
    return Results.Json(new { exists, size = exists ? new FileInfo(p).Length : 0 });
});

// ── Machine revoke (triggers self-destruct on next check-in) ──────────────────
app.MapPost("/api/admin/machines/{id}/revoke", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.RevokeMachine(db, id);
    return Results.Json(new { ok = true });
});
app.MapPost("/api/admin/machines/{id}/activate", (HttpContext ctx, string id) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.ActivateMachine(db, id);
    return Results.Json(new { ok = true });
});

// ── Public key ────────────────────────────────────────────────────────────────
app.MapGet("/api/admin/publickey", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(new { publicKeyXml = RsaSvc.GetPublicKeyXml(db) });
});

// ── Settings ──────────────────────────────────────────────────────────────────
app.MapGet("/api/admin/settings", (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    string exePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "WdpMgr_base.exe");
    bool exeUploaded = File.Exists(exePath);
    long exeSize     = exeUploaded ? new FileInfo(exePath).Length : 0;
    // Auto-detect public URL (respects Cloudflare X-Forwarded-Proto)
    string proto = ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var xfp) ? xfp.ToString() : ctx.Request.Scheme;
    string detectedUrl = $"{proto}://{ctx.Request.Host}";
    return Results.Json(new {
        adminKey    = masterKey,
        serverUrl   = DB.GetSetting(db, "server_url"),
        detectedUrl,
        exeUploaded, exeSize
    });
});
app.MapPost("/api/admin/settings", async (HttpContext ctx) => {
    if (!AdminOk(ctx)) return Unauth();
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    string serverUrl = S(doc.RootElement, "serverUrl");
    using var db = DB.Open(dbPath);
    DB.SetSetting(db, "server_url", serverUrl.TrimEnd('/'));
    return Results.Json(new { ok = true });
});

// ── Client check-in ───────────────────────────────────────────────────────────
app.MapPost("/api/checkin", async (HttpContext ctx) => {
    try {
        using var doc     = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r             = doc.RootElement;
        string licId      = S(r, "licenseId");
        string fp         = S(r, "fingerprint");
        string host       = S(r, "hostname");
        string winUser    = S(r, "windowsUser");   // for hr-type seat tracking
        string appId      = S(r, "appId");
        string ip         = ctx.Connection.RemoteIpAddress?.ToString() ?? "";

        if (string.IsNullOrEmpty(licId))
            return Results.Json(new { status="invalid", message="missing licenseId" });

        using var db = DB.Open(dbPath);
        var lic = DB.GetLicenseById(db, licId);
        if (lic == null) return Results.Json(new { status="invalid",  message="unknown license" });
        if (lic.Revoked) return Results.Json(new { status="revoked" });

        // Type-specific expiry
        // Expiry check — applies to temp and hr (optional expiry on hr)
        if (!string.IsNullOrEmpty(lic.Expiry) && (lic.Type == "temp" || lic.Type == "hr"))
            if (DateTime.TryParse(lic.Expiry, out var ed) && ed.ToUniversalTime() < DateTime.UtcNow)
                return Results.Json(new { status="expired" });

        if (lic.Type == "days") {
            if (!string.IsNullOrEmpty(lic.ActivatedAt)) {
                if (DateTime.TryParse(lic.ActivatedAt, out var ad)) {
                    if (DateTime.UtcNow > ad.AddHours(lic.DurationDays))  // DurationDays stored as hours
                        return Results.Json(new { status="expired" });
                }
            }
        }

        // App-scope check
        if (!string.IsNullOrEmpty(lic.AppId) && !string.IsNullOrEmpty(appId) && lic.AppId != appId)
            return Results.Json(new { status="invalid", message="app mismatch" });

        // HR type: seat key = fingerprint+windowsUser
        string seatKey = lic.Type == "hr" ? $"{fp}|{winUser}" : fp;

        var machine = DB.GetMachineByLicAndSeat(db, licId, seatKey);
        if (machine == null) {
            int cur = DB.GetActivationCount(db, licId);
            if (lic.MaxActivations > 0 && cur >= lic.MaxActivations)
                return Results.Json(new { status="wrong_machine", message="max activations reached" });
            // First activation of a days-license: record activated_at
            if (lic.Type == "days" && string.IsNullOrEmpty(lic.ActivatedAt))
                DB.SetActivatedAt(db, licId, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            DB.CreateMachine(db, Guid.NewGuid().ToString(), licId, seatKey, host, winUser, ip);
        } else {
            if (machine.Status == "revoked") return Results.Json(new { status="revoked" });
            DB.UpdateMachineCheckin(db, machine.Id, host, ip);
        }

        // Return remaining days for days-type
        object extra = new { };
        if (lic.Type == "days" && !string.IsNullOrEmpty(lic.ActivatedAt) && DateTime.TryParse(lic.ActivatedAt, out var act))
            extra = new { hoursRemaining = (int)(act.AddHours(lic.DurationDays) - DateTime.UtcNow).TotalHours };

        return Results.Json(new { status="ok", licenseType=lic.Type, extra });
    }
    catch (Exception ex) {
        Console.WriteLine($"[ERR] checkin: {ex.Message}");
        return Results.Json(new { status="invalid" }, statusCode: 400);
    }
});

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────
static string S(JsonElement e, string k, string d = "") => e.TryGetProperty(k, out var v) ? v.GetString() ?? d : d;
static int    I(JsonElement e, string k, int d = 0)     => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : d;
static string Env(string k, string d = "")              => System.Environment.GetEnvironmentVariable(k) ?? d;


// ═══════════════════════════════════════════════════════════════════════════════
// Database
// ═══════════════════════════════════════════════════════════════════════════════
static class DB
{
    public static SqliteConnection Open(string path) {
        var c = new SqliteConnection($"Data Source={path}"); c.Open(); return c;
    }

    public static void Init(string path, string firstUser, string firstPass) {
        using var db = Open(path);
        // Core tables
        Exec(db, @"
            CREATE TABLE IF NOT EXISTS rsa_keys (
                id INTEGER PRIMARY KEY, private_key TEXT NOT NULL, public_key TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS admin_users (
                id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL, api_key TEXT NOT NULL UNIQUE,
                role TEXT NOT NULL DEFAULT 'admin',
                created_at TEXT NOT NULL, last_login TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS apps (
                id TEXT PRIMARY KEY, name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS licenses (
                id TEXT PRIMARY KEY, label TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT 'lifetime',
                expiry TEXT NOT NULL DEFAULT '',
                issued TEXT NOT NULL,
                revoked INTEGER NOT NULL DEFAULT 0,
                max_activations INTEGER NOT NULL DEFAULT 1,
                duration_days INTEGER NOT NULL DEFAULT 0,
                activated_at TEXT NOT NULL DEFAULT '',
                app_id TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS machines (
                id TEXT PRIMARY KEY,
                license_id TEXT NOT NULL,
                seat_key TEXT NOT NULL DEFAULT '',
                hostname TEXT NOT NULL DEFAULT '',
                windows_user TEXT NOT NULL DEFAULT '',
                ip_address TEXT NOT NULL DEFAULT '',
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'active'
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL DEFAULT ''
            );
        ");
        // Add columns if upgrading from older schema
        foreach (var col in new[]{
            ("licenses","duration_days","INTEGER NOT NULL DEFAULT 0"),
            ("licenses","activated_at", "TEXT NOT NULL DEFAULT ''"),
            ("licenses","app_id",       "TEXT NOT NULL DEFAULT ''"),
            ("machines","seat_key",     "TEXT NOT NULL DEFAULT ''"),
            ("machines","windows_user", "TEXT NOT NULL DEFAULT ''"),
        }) {
            try { Exec(db, $"ALTER TABLE {col.Item1} ADD COLUMN {col.Item2} {col.Item3}"); } catch {}
        }
        RsaSvc.EnsureKeys(db);
        // Seed first admin user
        if (!string.IsNullOrEmpty(firstUser) && !string.IsNullOrEmpty(firstPass) && !UserExists(db, firstUser)) {
            string id = Guid.NewGuid().ToString();
            string ak = Guid.NewGuid().ToString("N");
            CreateAdminUser(db, id, firstUser, firstPass, "superadmin", ak);
            Console.WriteLine($"[INFO] First admin user created: {firstUser}");
        }
    }

    static void Exec(SqliteConnection db, string sql) {
        using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery();
    }

    // ── Stats ──────────────────────────────────────────────────────────────────
    public static object GetStats(SqliteConnection db) {
        long Q(string sql) { using var c=db.CreateCommand(); c.CommandText=sql; return (long)c.ExecuteScalar()!; }
        return new {
            totalLicenses   = Q("SELECT COUNT(*) FROM licenses"),
            activeLicenses  = Q("SELECT COUNT(*) FROM licenses WHERE revoked=0"),
            expiredLicenses = Q("SELECT COUNT(*) FROM licenses WHERE revoked=0 AND type='temp' AND expiry<>'' AND expiry < date('now')"),
            revokedLicenses = Q("SELECT COUNT(*) FROM licenses WHERE revoked=1"),
            activeMachines  = Q("SELECT COUNT(*) FROM machines WHERE status='active'"),
            totalApps       = Q("SELECT COUNT(*) FROM apps"),
            totalAdminUsers = Q("SELECT COUNT(*) FROM admin_users")
        };
    }

    // ── Admin users ────────────────────────────────────────────────────────────
    public static bool UserExists(SqliteConnection db, string username) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM admin_users WHERE username=$u";
        c.Parameters.AddWithValue("$u", username);
        return (long)c.ExecuteScalar()! > 0;
    }

    public static bool ApiKeyValid(SqliteConnection db, string key) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM admin_users WHERE api_key=$k";
        c.Parameters.AddWithValue("$k", key);
        return (long)c.ExecuteScalar()! > 0;
    }

    public static string? Login(SqliteConnection db, string username, string password) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id, password_hash, api_key FROM admin_users WHERE username=$u";
        c.Parameters.AddWithValue("$u", username);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        string id   = r.GetString(0);
        string hash = r.GetString(1);
        string key  = r.GetString(2);
        if (!VerifyPassword(password, hash)) return null;
        using var u = db.CreateCommand();
        u.CommandText = "UPDATE admin_users SET last_login=$t WHERE id=$id";
        u.Parameters.AddWithValue("$t",  DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        u.Parameters.AddWithValue("$id", id);
        u.ExecuteNonQuery();
        return key;
    }

    public static List<object> GetAdminUsers(SqliteConnection db) {
        var list = new List<object>();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id,username,role,created_at,last_login FROM admin_users ORDER BY created_at";
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new { id=r.GetString(0), username=r.GetString(1), role=r.GetString(2),
                           createdAt=r.GetString(3), lastLogin=r.GetString(4) });
        return list;
    }

    public static void CreateAdminUser(SqliteConnection db, string id, string username, string password, string role, string apiKey) {
        using var c = db.CreateCommand();
        c.CommandText = @"INSERT INTO admin_users(id,username,password_hash,api_key,role,created_at)
                          VALUES($id,$u,$h,$k,$r,$t)";
        c.Parameters.AddWithValue("$id", id);
        c.Parameters.AddWithValue("$u",  username);
        c.Parameters.AddWithValue("$h",  HashPassword(password));
        c.Parameters.AddWithValue("$k",  apiKey);
        c.Parameters.AddWithValue("$r",  role);
        c.Parameters.AddWithValue("$t",  DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        c.ExecuteNonQuery();
    }

    public static void DeleteAdminUser(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "DELETE FROM admin_users WHERE id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
    }

    public static void ResetApiKey(SqliteConnection db, string id, string newKey) {
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE admin_users SET api_key=$k WHERE id=$id";
        c.Parameters.AddWithValue("$k", newKey); c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
    }

    static string HashPassword(string pw) {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        var dk = new Rfc2898DeriveBytes(pw, salt, 100000, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(dk.GetBytes(32));
    }

    static bool VerifyPassword(string pw, string stored) {
        var p = stored.Split(':');
        if (p.Length != 2) return false;
        try {
            byte[] salt = Convert.FromBase64String(p[0]);
            byte[] exp  = Convert.FromBase64String(p[1]);
            var dk = new Rfc2898DeriveBytes(pw, salt, 100000, HashAlgorithmName.SHA256);
            return dk.GetBytes(32).SequenceEqual(exp);
        } catch { return false; }
    }

    // ── Apps ───────────────────────────────────────────────────────────────────
    public static List<object> GetApps(SqliteConnection db) {
        var list = new List<object>();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id,name,description,created_at FROM apps ORDER BY name";
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new { id=r.GetString(0), name=r.GetString(1), description=r.GetString(2), createdAt=r.GetString(3) });
        return list;
    }

    public static void CreateApp(SqliteConnection db, string id, string name, string desc) {
        using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO apps(id,name,description,created_at) VALUES($id,$n,$d,$t)";
        c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$n", name);
        c.Parameters.AddWithValue("$d",  desc);
        c.Parameters.AddWithValue("$t",  DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        c.ExecuteNonQuery();
    }

    public static void DeleteApp(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "DELETE FROM apps WHERE id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
    }

    // ── Licenses ───────────────────────────────────────────────────────────────
    public record LicenseRow(string Id, string Label, string Type, string Expiry, string Issued,
        bool Revoked, int MaxActivations, int DurationDays, string ActivatedAt, string AppId, string Notes);

    public static List<object> GetLicenses(SqliteConnection db) {
        var list = new List<object>();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT l.id,l.label,l.type,l.expiry,l.issued,l.revoked,l.max_activations,
                                 l.duration_days,l.activated_at,l.app_id,l.notes,
                                 a.name, COUNT(m.id) as seats
                          FROM licenses l
                          LEFT JOIN apps a ON a.id=l.app_id
                          LEFT JOIN machines m ON m.license_id=l.id AND m.status='active'
                          GROUP BY l.id ORDER BY l.issued DESC";
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new {
                id=r.GetString(0), label=r.GetString(1), type=r.GetString(2),
                expiry=r.GetString(3), issued=r.GetString(4), revoked=r.GetInt32(5)==1,
                maxActivations=r.GetInt32(6), durationDays=r.GetInt32(7),
                activatedAt=r.GetString(8), appId=r.GetString(9), notes=r.GetString(10),
                appName=r.IsDBNull(11)?"":r.GetString(11),
                activeSeats=r.GetInt32(12)
            });
        return list;
    }

    public static LicenseRow? GetLicenseById(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id,label,type,expiry,issued,revoked,max_activations,duration_days,activated_at,app_id,notes FROM licenses WHERE id=$id";
        c.Parameters.AddWithValue("$id", id);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new LicenseRow(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
            r.GetString(4),r.GetInt32(5)==1,r.GetInt32(6),r.GetInt32(7),r.GetString(8),r.GetString(9),r.GetString(10));
    }

    public static void CreateLicense(SqliteConnection db, string id, string label, string type,
        string expiry, string issued, int maxAct, int durDays, string appId, string notes) {
        using var c = db.CreateCommand();
        c.CommandText = @"INSERT INTO licenses(id,label,type,expiry,issued,max_activations,duration_days,app_id,notes)
                          VALUES($id,$l,$t,$e,$i,$m,$d,$a,$n)";
        c.Parameters.AddWithValue("$id", id);  c.Parameters.AddWithValue("$l",  label);
        c.Parameters.AddWithValue("$t",  type); c.Parameters.AddWithValue("$e",  expiry);
        c.Parameters.AddWithValue("$i",  issued); c.Parameters.AddWithValue("$m",  maxAct);
        c.Parameters.AddWithValue("$d",  durDays); c.Parameters.AddWithValue("$a",  appId);
        c.Parameters.AddWithValue("$n",  notes);
        c.ExecuteNonQuery();
    }

    public static void UpdateLicense(SqliteConnection db, string id, string label, string expiry, string notes, int maxAct, int durDays) {
        using var c = db.CreateCommand();
        c.CommandText = @"UPDATE licenses SET label=$l,expiry=$e,notes=$n,max_activations=$m,duration_days=$d WHERE id=$id";
        c.Parameters.AddWithValue("$l",  label);
        c.Parameters.AddWithValue("$e",  expiry);
        c.Parameters.AddWithValue("$n",  notes);
        c.Parameters.AddWithValue("$m",  maxAct);
        c.Parameters.AddWithValue("$d",  durDays);
        c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
    }

    public static void ReactivateLicense(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE licenses SET revoked=0 WHERE id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
        c.CommandText = "UPDATE machines SET status='active' WHERE license_id=$id AND status='revoked'";
        c.ExecuteNonQuery();
    }

    public static void PurgeLicense(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "DELETE FROM machines WHERE license_id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
        c.CommandText = "DELETE FROM licenses WHERE id=$id";
        c.ExecuteNonQuery();
    }

    public static void RevokeLicense(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE licenses SET revoked=1 WHERE id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
        c.CommandText = "UPDATE machines SET status='revoked' WHERE license_id=$id";
        c.ExecuteNonQuery();
    }

    public static void SetActivatedAt(SqliteConnection db, string id, string date) {
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE licenses SET activated_at=$d WHERE id=$id AND activated_at=''";
        c.Parameters.AddWithValue("$d", date); c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
    }

    // ── Machines ───────────────────────────────────────────────────────────────
    public record MachineRow(string Id, string LicenseId, string SeatKey, string Hostname,
        string WindowsUser, string IpAddress, string FirstSeen, string LastSeen, string Status);

    public static List<object> GetMachines(SqliteConnection db) {
        var list = new List<object>();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT m.id,m.license_id,l.label,m.seat_key,m.hostname,m.windows_user,
                                 m.ip_address,m.first_seen,m.last_seen,m.status,
                                 l.type,l.expiry,l.duration_days,l.activated_at
                          FROM machines m LEFT JOIN licenses l ON l.id=m.license_id
                          ORDER BY m.last_seen DESC";
        using var r = c.ExecuteReader();
        while (r.Read()) {
            string licType   = r.IsDBNull(10) ? "" : r.GetString(10);
            string expiry    = r.IsDBNull(11) ? "" : r.GetString(11);
            int    durDays   = r.IsDBNull(12) ? 0  : r.GetInt32(12);
            string actAt     = r.IsDBNull(13) ? "" : r.GetString(13);
            int? daysLeft = null; // null = unlimited (lifetime or hr with no expiry); value = minutes remaining
            if (licType == "temp" && !string.IsNullOrEmpty(expiry) && DateTime.TryParse(expiry, out var ed))
                daysLeft = (int)(ed.ToUniversalTime() - DateTime.UtcNow).TotalMinutes;
            else if (licType == "days" && !string.IsNullOrEmpty(actAt) && DateTime.TryParse(actAt, out var ad))
                daysLeft = (int)(ad.AddHours(durDays) - DateTime.UtcNow).TotalMinutes;
            else if (licType == "hr" && !string.IsNullOrEmpty(expiry) && DateTime.TryParse(expiry, out var hred))
                daysLeft = (int)(hred.ToUniversalTime() - DateTime.UtcNow).TotalMinutes;
            list.Add(new {
                id=r.GetString(0), licenseId=r.GetString(1),
                licenseLabel=r.IsDBNull(2)?"":r.GetString(2),
                seatKey=r.GetString(3), hostname=r.GetString(4),
                windowsUser=r.GetString(5), ipAddress=r.GetString(6),
                firstSeen=r.GetString(7), lastSeen=r.GetString(8), status=r.GetString(9),
                licenseType=licType, daysLeft
            });
        }
        return list;
    }

    public static MachineRow? GetMachineByLicAndSeat(SqliteConnection db, string licId, string seatKey) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT id,license_id,seat_key,hostname,windows_user,ip_address,first_seen,last_seen,status FROM machines WHERE license_id=$lic AND seat_key=$sk LIMIT 1";
        c.Parameters.AddWithValue("$lic", licId); c.Parameters.AddWithValue("$sk", seatKey);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new MachineRow(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
            r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8));
    }

    public static int GetActivationCount(SqliteConnection db, string licId) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM machines WHERE license_id=$lic AND status='active'";
        c.Parameters.AddWithValue("$lic", licId);
        return (int)(long)c.ExecuteScalar()!;
    }

    public static void CreateMachine(SqliteConnection db, string id, string licId,
        string seatKey, string host, string winUser, string ip) {
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var c = db.CreateCommand();
        c.CommandText = @"INSERT INTO machines(id,license_id,seat_key,hostname,windows_user,ip_address,first_seen,last_seen)
                          VALUES($id,$lic,$sk,$h,$w,$ip,$t,$t)";
        c.Parameters.AddWithValue("$id",  id);   c.Parameters.AddWithValue("$lic", licId);
        c.Parameters.AddWithValue("$sk",  seatKey); c.Parameters.AddWithValue("$h",   host);
        c.Parameters.AddWithValue("$w",   winUser); c.Parameters.AddWithValue("$ip",  ip);
        c.Parameters.AddWithValue("$t",   now);
        c.ExecuteNonQuery();
    }

    public static void UpdateMachineCheckin(SqliteConnection db, string id, string host, string ip) {
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE machines SET last_seen=$t,hostname=$h,ip_address=$ip WHERE id=$id";
        c.Parameters.AddWithValue("$t", now); c.Parameters.AddWithValue("$h", host);
        c.Parameters.AddWithValue("$ip", ip); c.Parameters.AddWithValue("$id", id);
        c.ExecuteNonQuery();
    }

    public static void RevokeMachine(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE machines SET status='revoked' WHERE id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
    }

    public static void ActivateMachine(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "UPDATE machines SET status='active' WHERE id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
    }

    public static void DeleteMachine(SqliteConnection db, string id) {
        using var c = db.CreateCommand();
        c.CommandText = "DELETE FROM machines WHERE id=$id";
        c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
    }


    // ── Settings key-value ─────────────────────────────────────────────────────
    public static string GetSetting(SqliteConnection db, string key) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT value FROM settings WHERE key=$k";
        c.Parameters.AddWithValue("$k", key);
        return (string?)c.ExecuteScalar() ?? "";
    }
    public static void SetSetting(SqliteConnection db, string key, string value) {
        using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", value);
        c.ExecuteNonQuery();
    }
}


// ═══════════════════════════════════════════════════════════════════════════════
// RSA Service
// ═══════════════════════════════════════════════════════════════════════════════
static class RsaSvc
{
    public static void EnsureKeys(SqliteConnection db) {
        using var chk = db.CreateCommand();
        chk.CommandText = "SELECT COUNT(*) FROM rsa_keys";
        if ((long)chk.ExecuteScalar()! > 0) return;
        Console.WriteLine("[INFO] Generating RSA-2048 key pair...");
        using var rsa  = RSA.Create(2048);
        var prms       = rsa.ExportParameters(true);
        using var cmd  = db.CreateCommand();
        cmd.CommandText = "INSERT INTO rsa_keys(private_key,public_key) VALUES($priv,$pub)";
        cmd.Parameters.AddWithValue("$priv", ToXml(prms, true));
        cmd.Parameters.AddWithValue("$pub",  ToXml(prms, false));
        cmd.ExecuteNonQuery();
        Console.WriteLine("[INFO] RSA keys stored. Fetch public key from /api/admin/publickey");
    }

    public static string GetPublicKeyXml(SqliteConnection db) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT public_key FROM rsa_keys LIMIT 1";
        return (string?)c.ExecuteScalar() ?? "";
    }

    static string GetPrivKeyXml(SqliteConnection db) {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT private_key FROM rsa_keys LIMIT 1";
        return (string?)c.ExecuteScalar() ?? "";
    }

    public static byte[] GenerateBundledExe(SqliteConnection db, DB.LicenseRow lic, string serverUrl, byte[] baseExe) {
        string licText   = GenerateLicFile(db, lic, serverUrl);
        string pubKeyXml = GetPublicKeyXml(db);
        string pubKeyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pubKeyXml));
        // Append embedded block after PE — Windows ignores trailing data
        string tail = "\nWDPMGR_LIC_BEGIN\n" + licText + "pubkey=" + pubKeyB64 + "\nWDPMGR_LIC_END\n";
        byte[] tailBytes = Encoding.UTF8.GetBytes(tail);
        byte[] result = new byte[baseExe.Length + tailBytes.Length];
        Buffer.BlockCopy(baseExe,  0, result, 0,             baseExe.Length);
        Buffer.BlockCopy(tailBytes, 0, result, baseExe.Length, tailBytes.Length);
        return result;
    }

    public static string GenerateLicFile(SqliteConnection db, DB.LicenseRow lic, string serverUrl) {
        // Payload includes durationDays so client can validate days-type locally
        string payload = $"{lic.Id}|{lic.Type}|{lic.Expiry}|{lic.Issued}|{lic.DurationDays}";
        var parms = XmlToParams(GetPrivKeyXml(db), true);
        using var rsa = RSA.Create(); rsa.ImportParameters(parms);
        byte[] sig = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"WDPMGR_LICENSE_V1\r\n" +
               $"id={lic.Id}\r\n" +
               $"type={lic.Type}\r\n" +
               $"expiry={lic.Expiry}\r\n" +
               $"issued={lic.Issued}\r\n" +
               $"durationDays={lic.DurationDays}\r\n" +
               $"appId={lic.AppId}\r\n" +
               $"server={serverUrl}\r\n" +
               $"sig={Convert.ToBase64String(sig)}\r\n";
    }

    static string ToXml(RSAParameters p, bool priv) {
        var sb = new StringBuilder("<RSAKeyValue>");
        sb.Append($"<Modulus>{B64(p.Modulus!)}</Modulus><Exponent>{B64(p.Exponent!)}</Exponent>");
        if (priv) {
            sb.Append($"<P>{B64(p.P!)}</P><Q>{B64(p.Q!)}</Q><DP>{B64(p.DP!)}</DP>");
            sb.Append($"<DQ>{B64(p.DQ!)}</DQ><InverseQ>{B64(p.InverseQ!)}</InverseQ><D>{B64(p.D!)}</D>");
        }
        return sb.Append("</RSAKeyValue>").ToString();
    }

    static RSAParameters XmlToParams(string xml, bool priv) {
        var root = XDocument.Parse(xml).Root!;
        var p = new RSAParameters { Modulus=GB(root,"Modulus"), Exponent=GB(root,"Exponent") };
        if (priv) { p.P=GB(root,"P"); p.Q=GB(root,"Q"); p.DP=GB(root,"DP"); p.DQ=GB(root,"DQ"); p.InverseQ=GB(root,"InverseQ"); p.D=GB(root,"D"); }
        return p;
    }

    static string B64(byte[] b) => Convert.ToBase64String(b);
    static byte[] GB(XElement e, string t) => Convert.FromBase64String(e.Element(t)!.Value);
}
