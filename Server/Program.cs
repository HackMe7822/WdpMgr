// WdpMgr License Server - ASP.NET Core 8 minimal API
// SQLite auto-created on first run, RSA-2048 key pair generated on first start.

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

// ── Config ────────────────────────────────────────────────────────────────────
string adminKey = Environment.GetEnvironmentVariable("WDPMGR_ADMIN_KEY") ?? "changeme";
string dbPath   = Environment.GetEnvironmentVariable("WDPMGR_DB_PATH")   ?? "wdpmgr.db";
int    port     = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var pp) ? pp : 5000;

if (adminKey == "changeme")
    Console.WriteLine("[WARN] Using default admin key — set WDPMGR_ADMIN_KEY environment variable!");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");
// Enables running as a Windows Service (no-op when run interactively)
builder.Host.UseWindowsService(o => o.ServiceName = "WdpMgrServer");
var app = builder.Build();

// ── Init DB on startup ────────────────────────────────────────────────────────
DB.Init(dbPath);
Console.WriteLine($"[INFO] DB ready: {Path.GetFullPath(dbPath)}");
Console.WriteLine($"[INFO] Listening on http://localhost:{port}");

// ── Serve admin panel ─────────────────────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Auth helper ───────────────────────────────────────────────────────────────
bool AdminOk(HttpContext ctx) =>
    ctx.Request.Headers.TryGetValue("X-Admin-Key", out var v) && v.ToString() == adminKey;

IResult Unauth() => Results.Json(new { error = "Unauthorized" }, statusCode: 401);

// ── Admin: stats ──────────────────────────────────────────────────────────────
app.MapGet("/api/admin/stats", (HttpContext ctx) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetStats(db));
});

// ── Admin: list licenses ──────────────────────────────────────────────────────
app.MapGet("/api/admin/licenses", (HttpContext ctx) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetLicenses(db));
});

// ── Admin: create license ──────────────────────────────────────────────────────
app.MapPost("/api/admin/licenses", async (HttpContext ctx) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var r = doc.RootElement;
    string label  = S(r, "label");
    string type   = S(r, "type", "lifetime");
    string expiry = S(r, "expiry");
    string notes  = S(r, "notes");
    int maxAct    = r.TryGetProperty("maxActivations", out var ma) && ma.TryGetInt32(out var mi) ? mi : 1;
    if (string.IsNullOrWhiteSpace(label))
        return Results.Json(new { error = "label is required" }, statusCode: 400);
    if (type != "lifetime" && type != "temp")
        return Results.Json(new { error = "type must be 'lifetime' or 'temp'" }, statusCode: 400);

    string id     = Guid.NewGuid().ToString();
    string issued = DateTime.UtcNow.ToString("yyyy-MM-dd");
    using var db  = DB.Open(dbPath);
    DB.CreateLicense(db, id, label, type, expiry, issued, maxAct, notes);
    return Results.Json(new { id, label, type, expiry, issued, maxActivations = maxAct, notes, revoked = false });
});

// ── Admin: revoke license ──────────────────────────────────────────────────────
app.MapDelete("/api/admin/licenses/{id}", (HttpContext ctx, string id) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    if (DB.GetLicenseById(db, id) == null)
        return Results.Json(new { error = "not found" }, statusCode: 404);
    DB.RevokeLicense(db, id);
    return Results.Json(new { ok = true });
});

// ── Admin: list machines ───────────────────────────────────────────────────────
app.MapGet("/api/admin/machines", (HttpContext ctx) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(DB.GetMachines(db));
});

// ── Admin: revoke machine ──────────────────────────────────────────────────────
app.MapDelete("/api/admin/machines/{id}", (HttpContext ctx, string id) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    DB.RevokeMachine(db, id);
    return Results.Json(new { ok = true });
});

// ── Admin: download .lic file ──────────────────────────────────────────────────
app.MapGet("/api/admin/licenses/{id}/download", (HttpContext ctx, string id) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var db  = DB.Open(dbPath);
    var lic = DB.GetLicenseById(db, id);
    if (lic == null) return Results.Json(new { error = "not found" }, statusCode: 404);
    if (lic.Revoked) return Results.Json(new { error = "license is revoked" }, statusCode: 400);
    string serverUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    string content   = RsaSvc.GenerateLicFile(db, lic, serverUrl);
    ctx.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"wdp.lic\"");
    return Results.Content(content, "text/plain");
});

// ── Admin: get RSA public key (XML, .NET 4.0 compatible) ──────────────────────
app.MapGet("/api/admin/publickey", (HttpContext ctx) =>
{
    if (!AdminOk(ctx)) return Unauth();
    using var db = DB.Open(dbPath);
    return Results.Json(new { publicKeyXml = RsaSvc.GetPublicKeyXml(db) });
});

// ── Client: check-in ──────────────────────────────────────────────────────────
app.MapPost("/api/checkin", async (HttpContext ctx) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r = doc.RootElement;
        string licId = S(r, "licenseId");
        string fp    = S(r, "fingerprint");
        string host  = S(r, "hostname");
        string ip    = ctx.Connection.RemoteIpAddress?.ToString() ?? "";

        if (string.IsNullOrEmpty(licId))
            return Results.Json(new { status = "invalid", message = "missing licenseId" });

        using var db = DB.Open(dbPath);
        var lic = DB.GetLicenseById(db, licId);
        if (lic == null) return Results.Json(new { status = "invalid",  message = "unknown license" });
        if (lic.Revoked) return Results.Json(new { status = "revoked" });
        if (lic.IsExpired()) return Results.Json(new { status = "expired" });

        var machine = DB.GetMachineByLicAndFp(db, licId, fp);
        if (machine == null)
        {
            int cur = DB.GetActivationCount(db, licId);
            if (cur >= lic.MaxActivations)
                return Results.Json(new { status = "wrong_machine", message = "max activations reached" });
            DB.CreateMachine(db, Guid.NewGuid().ToString(), licId, fp, host, ip);
        }
        else
        {
            if (machine.Status == "revoked")
                return Results.Json(new { status = "revoked" });
            if (!string.IsNullOrEmpty(machine.Fingerprint) && machine.Fingerprint != fp)
                return Results.Json(new { status = "wrong_machine" });
            DB.UpdateMachineCheckin(db, machine.Id, host, ip);
        }

        return Results.Json(new { status = "ok" });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] checkin: {ex.Message}");
        return Results.Json(new { status = "invalid" }, statusCode: 400);
    }
});

app.Run();

// ── JSON helper ───────────────────────────────────────────────────────────────
static string S(JsonElement e, string key, string def = "") =>
    e.TryGetProperty(key, out var v) ? v.GetString() ?? def : def;


// ═══════════════════════════════════════════════════════════════════════════════
// Database
// ═══════════════════════════════════════════════════════════════════════════════
static class DB
{
    public static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return conn;
    }

    public static void Init(string path)
    {
        using var db = Open(path);
        Exec(db, @"
            CREATE TABLE IF NOT EXISTS rsa_keys (
                id          INTEGER PRIMARY KEY,
                private_key TEXT NOT NULL,
                public_key  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS licenses (
                id              TEXT PRIMARY KEY,
                label           TEXT NOT NULL,
                type            TEXT NOT NULL DEFAULT 'lifetime',
                expiry          TEXT NOT NULL DEFAULT '',
                issued          TEXT NOT NULL,
                revoked         INTEGER NOT NULL DEFAULT 0,
                max_activations INTEGER NOT NULL DEFAULT 1,
                notes           TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS machines (
                id          TEXT PRIMARY KEY,
                license_id  TEXT NOT NULL,
                fingerprint TEXT NOT NULL DEFAULT '',
                hostname    TEXT NOT NULL DEFAULT '',
                ip_address  TEXT NOT NULL DEFAULT '',
                first_seen  TEXT NOT NULL,
                last_seen   TEXT NOT NULL DEFAULT '',
                status      TEXT NOT NULL DEFAULT 'active'
            );
        ");
        RsaSvc.EnsureKeys(db);
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── Stats ──────────────────────────────────────────────────────────────────
    public static object GetStats(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM licenses";
        long total = (long)cmd.ExecuteScalar()!;

        cmd.CommandText = "SELECT COUNT(*) FROM licenses WHERE revoked=0 AND (type='lifetime' OR (type='temp' AND (expiry='' OR expiry >= date('now'))))";
        long active = (long)cmd.ExecuteScalar()!;

        cmd.CommandText = "SELECT COUNT(*) FROM licenses WHERE type='temp' AND expiry<>'' AND expiry < date('now') AND revoked=0";
        long expired = (long)cmd.ExecuteScalar()!;

        cmd.CommandText = "SELECT COUNT(*) FROM licenses WHERE revoked=1";
        long revoked = (long)cmd.ExecuteScalar()!;

        cmd.CommandText = "SELECT COUNT(*) FROM machines WHERE status='active'";
        long machines = (long)cmd.ExecuteScalar()!;

        return new { total, active, expired, revoked, machines };
    }

    // ── Licenses ───────────────────────────────────────────────────────────────
    public record LicenseRow(string Id, string Label, string Type, string Expiry, string Issued,
        bool Revoked, int MaxActivations, string Notes)
    {
        public bool IsExpired() =>
            Type == "temp" && !string.IsNullOrEmpty(Expiry) &&
            DateTime.TryParse(Expiry, out var d) && d.Date < DateTime.UtcNow.Date;
    }

    public static List<object> GetLicenses(SqliteConnection db)
    {
        var list = new List<object>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,label,type,expiry,issued,revoked,max_activations,notes FROM licenses ORDER BY issued DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new {
                id             = r.GetString(0),
                label          = r.GetString(1),
                type           = r.GetString(2),
                expiry         = r.GetString(3),
                issued         = r.GetString(4),
                revoked        = r.GetInt32(5) == 1,
                maxActivations = r.GetInt32(6),
                notes          = r.GetString(7)
            });
        return list;
    }

    public static LicenseRow? GetLicenseById(SqliteConnection db, string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,label,type,expiry,issued,revoked,max_activations,notes FROM licenses WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new LicenseRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetInt32(5) == 1, r.GetInt32(6), r.GetString(7));
    }

    public static void CreateLicense(SqliteConnection db, string id, string label, string type,
        string expiry, string issued, int maxAct, string notes)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"INSERT INTO licenses (id,label,type,expiry,issued,max_activations,notes)
                            VALUES ($id,$label,$type,$expiry,$issued,$maxAct,$notes)";
        cmd.Parameters.AddWithValue("$id",     id);
        cmd.Parameters.AddWithValue("$label",  label);
        cmd.Parameters.AddWithValue("$type",   type);
        cmd.Parameters.AddWithValue("$expiry", expiry);
        cmd.Parameters.AddWithValue("$issued", issued);
        cmd.Parameters.AddWithValue("$maxAct", maxAct);
        cmd.Parameters.AddWithValue("$notes",  notes);
        cmd.ExecuteNonQuery();
    }

    public static void RevokeLicense(SqliteConnection db, string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE licenses SET revoked=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        // Revoke all machines on this license
        cmd.CommandText = "UPDATE machines SET status='revoked' WHERE license_id=$id";
        cmd.ExecuteNonQuery();
    }

    // ── Machines ───────────────────────────────────────────────────────────────
    public record MachineRow(string Id, string LicenseId, string Fingerprint, string Hostname,
        string IpAddress, string FirstSeen, string LastSeen, string Status);

    public static List<object> GetMachines(SqliteConnection db)
    {
        var list = new List<object>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"SELECT m.id, m.license_id, l.label, m.fingerprint, m.hostname,
                                   m.ip_address, m.first_seen, m.last_seen, m.status
                            FROM machines m
                            LEFT JOIN licenses l ON l.id=m.license_id
                            ORDER BY m.last_seen DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new {
                id          = r.GetString(0),
                licenseId   = r.GetString(1),
                licenseLabel= r.IsDBNull(2) ? "" : r.GetString(2),
                fingerprint = r.GetString(3),
                hostname    = r.GetString(4),
                ipAddress   = r.GetString(5),
                firstSeen   = r.GetString(6),
                lastSeen    = r.GetString(7),
                status      = r.GetString(8)
            });
        return list;
    }

    public static MachineRow? GetMachineByLicAndFp(SqliteConnection db, string licId, string fp)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,license_id,fingerprint,hostname,ip_address,first_seen,last_seen,status FROM machines WHERE license_id=$lic AND (fingerprint=$fp OR fingerprint='') LIMIT 1";
        cmd.Parameters.AddWithValue("$lic", licId);
        cmd.Parameters.AddWithValue("$fp",  fp);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new MachineRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7));
    }

    public static int GetActivationCount(SqliteConnection db, string licId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM machines WHERE license_id=$lic AND status='active'";
        cmd.Parameters.AddWithValue("$lic", licId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    public static void CreateMachine(SqliteConnection db, string id, string licId, string fp,
        string host, string ip)
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"INSERT INTO machines (id,license_id,fingerprint,hostname,ip_address,first_seen,last_seen)
                            VALUES ($id,$lic,$fp,$host,$ip,$now,$now)";
        cmd.Parameters.AddWithValue("$id",   id);
        cmd.Parameters.AddWithValue("$lic",  licId);
        cmd.Parameters.AddWithValue("$fp",   fp);
        cmd.Parameters.AddWithValue("$host", host);
        cmd.Parameters.AddWithValue("$ip",   ip);
        cmd.Parameters.AddWithValue("$now",  now);
        cmd.ExecuteNonQuery();
    }

    public static void UpdateMachineCheckin(SqliteConnection db, string id, string host, string ip)
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE machines SET last_seen=$now, hostname=$host, ip_address=$ip WHERE id=$id";
        cmd.Parameters.AddWithValue("$now",  now);
        cmd.Parameters.AddWithValue("$host", host);
        cmd.Parameters.AddWithValue("$ip",   ip);
        cmd.Parameters.AddWithValue("$id",   id);
        cmd.ExecuteNonQuery();
    }

    public static void RevokeMachine(SqliteConnection db, string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE machines SET status='revoked' WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}


// ═══════════════════════════════════════════════════════════════════════════════
// RSA Service
// ═══════════════════════════════════════════════════════════════════════════════
static class RsaSvc
{
    // Generate RSA-2048 key pair on first run; store as XML strings compatible
    // with .NET Framework 4.0 RSACryptoServiceProvider.FromXmlString().
    public static void EnsureKeys(SqliteConnection db)
    {
        using var check = db.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM rsa_keys";
        long count = (long)check.ExecuteScalar()!;
        if (count > 0) return;

        Console.WriteLine("[INFO] Generating RSA-2048 key pair (first run)...");
        using var rsa    = RSA.Create(2048);
        var parms        = rsa.ExportParameters(true);
        string privXml   = ParamsToXml(parms, includePrivate: true);
        string pubXml    = ParamsToXml(parms, includePrivate: false);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO rsa_keys (private_key, public_key) VALUES ($priv, $pub)";
        cmd.Parameters.AddWithValue("$priv", privXml);
        cmd.Parameters.AddWithValue("$pub",  pubXml);
        cmd.ExecuteNonQuery();
        Console.WriteLine("[INFO] RSA key pair stored. Fetch public key from /api/admin/publickey");
    }

    public static string GetPublicKeyXml(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT public_key FROM rsa_keys LIMIT 1";
        return (string?)cmd.ExecuteScalar() ?? "";
    }

    static string GetPrivateKeyXml(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT private_key FROM rsa_keys LIMIT 1";
        return (string?)cmd.ExecuteScalar() ?? "";
    }

    public static string GenerateLicFile(SqliteConnection db, DB.LicenseRow lic, string serverUrl)
    {
        string payload = $"{lic.Id}|{lic.Type}|{lic.Expiry}|{lic.Issued}";
        byte[] data    = Encoding.UTF8.GetBytes(payload);

        string privXml = GetPrivateKeyXml(db);
        var parms      = XmlToParams(privXml, includePrivate: true);
        using var rsa  = RSA.Create();
        rsa.ImportParameters(parms);
        byte[] sig     = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"WDPMGR_LICENSE_V1\r\n" +
               $"id={lic.Id}\r\n" +
               $"type={lic.Type}\r\n" +
               $"expiry={lic.Expiry}\r\n" +
               $"issued={lic.Issued}\r\n" +
               $"server={serverUrl}\r\n" +
               $"sig={Convert.ToBase64String(sig)}\r\n";
    }

    // Build XML format that .NET 4.0 RSACryptoServiceProvider.FromXmlString() can consume
    static string ParamsToXml(RSAParameters p, bool includePrivate)
    {
        var sb = new StringBuilder("<RSAKeyValue>");
        sb.Append($"<Modulus>{B64(p.Modulus!)}</Modulus>");
        sb.Append($"<Exponent>{B64(p.Exponent!)}</Exponent>");
        if (includePrivate)
        {
            sb.Append($"<P>{B64(p.P!)}</P>");
            sb.Append($"<Q>{B64(p.Q!)}</Q>");
            sb.Append($"<DP>{B64(p.DP!)}</DP>");
            sb.Append($"<DQ>{B64(p.DQ!)}</DQ>");
            sb.Append($"<InverseQ>{B64(p.InverseQ!)}</InverseQ>");
            sb.Append($"<D>{B64(p.D!)}</D>");
        }
        sb.Append("</RSAKeyValue>");
        return sb.ToString();
    }

    static RSAParameters XmlToParams(string xml, bool includePrivate)
    {
        var doc  = XDocument.Parse(xml);
        var root = doc.Root!;
        var p    = new RSAParameters
        {
            Modulus  = GetB(root, "Modulus"),
            Exponent = GetB(root, "Exponent")
        };
        if (includePrivate)
        {
            p.P        = GetB(root, "P");
            p.Q        = GetB(root, "Q");
            p.DP       = GetB(root, "DP");
            p.DQ       = GetB(root, "DQ");
            p.InverseQ = GetB(root, "InverseQ");
            p.D        = GetB(root, "D");
        }
        return p;
    }

    static string B64(byte[] b) => Convert.ToBase64String(b);
    static byte[] GetB(XElement e, string tag) => Convert.FromBase64String(e.Element(tag)!.Value);
}
