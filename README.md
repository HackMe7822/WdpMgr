# WdpMgr — Windows Display Policy Manager

RSA-licensed tool that prevents `SetWindowDisplayAffinity` (WDA) from hiding windows from screen capture. Consists of a license server, a Windows client (`WdpMgr.exe`), and a Mac client (`MacWdpMgr`).

---

## Server — Fresh Install

Run on any Windows Server / VM as Administrator:

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
iex (irm 'https://raw.githubusercontent.com/HackMe7822/WdpMgr/master/install.ps1')
```

The script will prompt for:
- Admin key (or auto-generate one)
- First admin username + password
- Cloudflare Tunnel option:
  - **`y`** — create a new dedicated tunnel
  - **`e`** — add WdpMgr ingress to an existing tunnel (e.g. same machine as MeshCentral)
  - **`n`** — skip (accessible on `http://localhost:5000` only)

Installs to `C:\WdpMgrServer` by default. DB stored at `C:\WdpMgrServer\data\wdpmgr.db`.

After install → **Admin Panel → Settings → Show Key** to get the RSA public key, then embed it into `WdpMgr.exe` using `set-pubkey.bat`.

---

## Server — Migrate to a New Machine

> **Critical:** The RSA key pair lives inside `wdpmgr.db`. Copying the DB preserves all issued licenses. Without it, new keys are generated and every `wdp.lic` in the field becomes invalid.

### Step 1 — Copy the database from the old server

On the old server, find and copy:
```
C:\WdpMgrServer\data\wdpmgr.db
```
Transfer it to the new machine (network share, USB, etc.) and place it at:
```
C:\WdpMgrServer\data\wdpmgr.db
```
Create the folder first if needed:
```powershell
New-Item -ItemType Directory -Force "C:\WdpMgrServer\data"
```

### Step 2 — Run the install script on the new machine

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
iex (irm 'https://raw.githubusercontent.com/HackMe7822/WdpMgr/master/install.ps1')
```

- When asked about Cloudflare → choose **`e`** if this machine already runs a cloudflared tunnel (e.g. MeshCentral on the same VM). Enter the WdpMgr hostname.
- The service will start, find the existing DB, and **skip RSA key generation** — all old licenses remain valid.

### Step 3 — Verify the new server

Open the admin panel at the new URL and confirm licenses are visible.

### Step 4 — Stop the old server

On the old machine:
```powershell
Stop-Service WdpMgrServer -Force
sc.exe config WdpMgrServer start= disabled
```

### Step 5 — Distribute new EXEs (only if server URL changed)

If the Cloudflare hostname stayed the same, existing `WdpMgr.exe` installs continue working — no redistribution needed.

If the hostname changed, rebuild with the updated server URL embedded in the license and push new EXEs to clients.

---

## Mac Client

Install on any Mac (builds from source, requires Xcode command line tools):

```bash
curl -fsSL https://raw.githubusercontent.com/HackMe7822/WdpMgr/master/MacClearDA/install-mac.sh | bash
```

After install, paste the RSA public key from **Admin Panel → Settings → Show Key** into `MacWdpMgr.swift` at `let RSA_PUBLIC_KEY = "..."`, then rebuild:

```bash
swiftc MacWdpMgr.swift -framework AppKit -framework Security -o MacWdpMgr
```

---

## Windows Client

`WdpMgr.exe` is distributed to end-users with a `wdp.lic` license file.

License types: `lifetime`, `temp`, `days:<n>`, `hr:<n>`

The license server URL and RSA public key are embedded at build time. Use `set-pubkey.bat` to inject the public key after building.

---

## Admin Panel

| Route | Description |
|---|---|
| `GET /api/admin/publickey` | Returns RSA public key XML |
| `POST /api/admin/licenses` | Issue a new license |
| `POST /api/admin/licenses/{id}/revoke` | Revoke a license |
| `GET /api/checkin` | Client check-in endpoint |
