#Requires -RunAsAdministrator
# WdpMgr Server — Fresh-VM installer (no git required)
# One-liner: Set-ExecutionPolicy Bypass -Scope Process -Force; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; iex (irm 'https://raw.githubusercontent.com/HackMe7822/WdpMgr/master/install.ps1')

param(
    [string]$InstallDir   = "C:\WdpMgrServer",
    [string]$ServiceName  = "WdpMgrServer",
    [int]   $Port         = 5000
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Info  { param($m) Write-Host "  [INFO]  $m" -ForegroundColor Cyan }
function OK    { param($m) Write-Host "  [ OK ]  $m" -ForegroundColor Green }
function Warn  { param($m) Write-Host "  [WARN]  $m" -ForegroundColor Yellow }
function Fail  { param($m) Write-Host "`n  [ERR]  $m`n" -ForegroundColor Red; Read-Host "Press ENTER to exit"; exit 1 }

Clear-Host
Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║    WdpMgr License Server — Fresh VM Installer       ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── Config ─────────────────────────────────────────────────────────────────────
Write-Host "  ── Step 1/6: Configuration ────────────────────────────────" -ForegroundColor DarkGray

$AdminKey = Read-Host "  Admin key (blank = auto-generate)"
if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    $AdminKey = -join ((48..57)+(65..90)+(97..122) | Get-Random -Count 32 | % { [char]$_ })
    Warn "Auto-generated admin key: $AdminKey  ← SAVE THIS"
}

$FirstUser = Read-Host "  First admin username (for panel login)"
if ([string]::IsNullOrWhiteSpace($FirstUser)) { $FirstUser = "admin" }
$FirstPass = Read-Host "  First admin password" -AsSecureString
$FirstPassPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($FirstPass))

Write-Host ""
Write-Host "  Cloudflare Tunnel options:" -ForegroundColor DarkGray
Write-Host "    y = create / reuse a named tunnel (full setup)" -ForegroundColor DarkGray
Write-Host "    e = add WdpMgr ingress to EXISTING tunnel config" -ForegroundColor DarkGray
Write-Host "    n = skip" -ForegroundColor DarkGray
$UseCF = Read-Host "  Cloudflare Tunnel? [y/e/N]"
$CF_ApiToken=$CF_AccountId=$CF_ZoneId=$CF_Domain=$CF_Subdomain=$CF_Hostname=""
if ($UseCF -match "^[Yy]$") {
    $CF_ApiToken  = Read-Host "  Cloudflare API token (Zone Read + DNS Edit + Tunnel:Edit)"
    $CF_AccountId = Read-Host "  Cloudflare Account ID"
    $CF_ZoneId    = Read-Host "  Cloudflare Zone ID"
    $CF_Domain    = Read-Host "  Domain (e.g. example.com)"
    $CF_Subdomain = Read-Host "  Subdomain prefix (e.g. wdpmgr)"
} elseif ($UseCF -match "^[Ee]$") {
    $CF_Hostname  = Read-Host "  Full hostname to use (e.g. wdpmgr.example.com)"
    Write-Host "  (Optional) For auto DNS CNAME — leave blank to add manually:" -ForegroundColor DarkGray
    $CF_ApiToken  = Read-Host "  Cloudflare API token (blank = skip DNS)"
    if ($CF_ApiToken) {
        $CF_ZoneId = Read-Host "  Cloudflare Zone ID"
    }
}

Write-Host ""
$confirm = Read-Host "  Install to $InstallDir ? [y/N]"
if ($confirm -notmatch "^[Yy]$") { exit 0 }

# ── .NET 8 ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ── Step 2/6: .NET 8 SDK ───────────────────────────────────" -ForegroundColor DarkGray
$dotnetDir = "C:\dotnet8"
$dotnetOk  = $false
try { $v = & dotnet --version 2>$null; if ($v -match "^8\.") { OK ".NET $v already present"; $dotnetOk=$true } } catch {}
if (-not $dotnetOk) {
    Info "Downloading dotnet-install.ps1..."
    $ins = "$env:TEMP\dotnet-install.ps1"
    Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $ins -UseBasicParsing
    Info "Installing .NET 8 SDK..."
    & $ins -Channel "8.0" -InstallDir $dotnetDir | Out-Null
    $env:PATH = "$dotnetDir;$env:PATH"
    [Environment]::SetEnvironmentVariable("PATH","$dotnetDir;" + [Environment]::GetEnvironmentVariable("PATH","Machine"),"Machine")
    OK ".NET 8 SDK installed to $dotnetDir"
}

# ── Download repo ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ── Step 3/6: Downloading WdpMgr ───────────────────────────" -ForegroundColor DarkGray
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

$zipUrl  = "https://github.com/HackMe7822/WdpMgr/archive/refs/heads/master.zip"
$zipFile = "$env:TEMP\wdpmgr.zip"
$repoDir = "$InstallDir\repo"

Info "Downloading from GitHub..."
Invoke-WebRequest -Uri $zipUrl -OutFile $zipFile -UseBasicParsing
Info "Extracting..."
if (Test-Path $repoDir) { Remove-Item $repoDir -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zipFile, "$InstallDir\_tmp")
Move-Item "$InstallDir\_tmp\WdpMgr-master" $repoDir
Remove-Item "$InstallDir\_tmp" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipFile -Force -ErrorAction SilentlyContinue
OK "Repo extracted to $repoDir"

# ── Build ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ── Step 4/6: Building server ───────────────────────────────" -ForegroundColor DarkGray
$publishDir = "$InstallDir\app"
Info "Publishing win-x64 self-contained binary..."
& dotnet publish "$repoDir\Server\WdpMgrServer.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $publishDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "Build failed. Check output above." }
OK "Built: $publishDir\WdpMgrServer.exe"

# ── Windows Service ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ── Step 5/6: Windows Service ───────────────────────────────" -ForegroundColor DarkGray
$dataDir = "$InstallDir\data"
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory $dataDir -Force | Out-Null }

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") { Stop-Service $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null; Start-Sleep 2
}

$exePath = "$publishDir\WdpMgrServer.exe"
& sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "WdpMgr License Server" | Out-Null
& sc.exe description $ServiceName "Windows Display Policy Manager — License & Activation Server" | Out-Null

# Store first admin user as env var for server to seed on first start
$envVars = @(
    "WDPMGR_ADMIN_KEY=$AdminKey",
    "WDPMGR_DB_PATH=$dataDir\wdpmgr.db",
    "PORT=$Port",
    "WDPMGR_FIRST_USER=$FirstUser",
    "WDPMGR_FIRST_PASS=$FirstPassPlain"
)
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name "Environment" -Value $envVars -Type MultiString

Start-Service $ServiceName; Start-Sleep 3
$st = (Get-Service $ServiceName).Status
if ($st -eq "Running") { OK "Service running on http://localhost:$Port" }
else { Warn "Service status: $st — check Event Viewer > Application" }

# ── Cloudflare ─────────────────────────────────────────────────────────────────
$CF_FullDomain = ""
$tunnelId      = ""

if ($UseCF -match "^[Yy]$") {
    Write-Host ""
    Write-Host "  ── Step 6/6: Cloudflare Tunnel (new/reuse) ─────────────────" -ForegroundColor DarkGray
    $CF_FullDomain = "$CF_Subdomain.$CF_Domain"
    $cfDir = "$InstallDir\cloudflared"; if (-not (Test-Path $cfDir)) { New-Item $cfDir -ItemType Directory -Force | Out-Null }
    $cfExe = "$cfDir\cloudflared.exe"
    if (-not (Test-Path $cfExe)) {
        Info "Downloading cloudflared..."
        Invoke-WebRequest "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" `
            -OutFile $cfExe -UseBasicParsing
    }
    $hdrs = @{ "Authorization"="Bearer $CF_ApiToken"; "Content-Type"="application/json" }
    $cfConfigDir = "$InstallDir\cf-config"; if (-not (Test-Path $cfConfigDir)) { New-Item $cfConfigDir -ItemType Directory -Force | Out-Null }

    $tr = Invoke-RestMethod "https://api.cloudflare.com/client/v4/accounts/$CF_AccountId/cfd_tunnel?name=wdpmgr-tunnel" `
        -Headers $hdrs -Method GET -EA SilentlyContinue
    $tunnelId = $tr.result[0].id

    if (-not $tunnelId) {
        $sec = -join ((48..57)+(65..90)+(97..122) | Get-Random -Count 32 | % { [char]$_ })
        $secB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sec))
        $cr = Invoke-RestMethod "https://api.cloudflare.com/client/v4/accounts/$CF_AccountId/cfd_tunnel" `
            -Headers $hdrs -Method POST -Body (@{name="wdpmgr-tunnel";tunnel_secret=$secB64}|ConvertTo-Json) -EA Stop
        $tunnelId = $cr.result.id
        @{AccountTag=$CF_AccountId;TunnelID=$tunnelId;TunnelName="wdpmgr-tunnel";TunnelSecret=$secB64} `
            | ConvertTo-Json | Set-Content "$cfConfigDir\$tunnelId.json" -Encoding UTF8
        OK "Tunnel created: $tunnelId"
    } else { OK "Reusing tunnel: $tunnelId" }

    @"
tunnel: $tunnelId
credentials-file: $($cfConfigDir.Replace('\','/'))\$tunnelId.json
ingress:
  - hostname: $CF_FullDomain
    service: http://localhost:$Port
  - service: http_status:404
"@ | Set-Content "$cfConfigDir\config.yml" -Encoding UTF8

    # CNAME record
    $cname = "$tunnelId.cfargotunnel.com"
    $ex = Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records?type=CNAME&name=$CF_FullDomain" `
        -Headers $hdrs -Method GET -EA SilentlyContinue
    $rid = $ex.result[0].id
    $dns = @{type="CNAME";name=$CF_Subdomain;content=$cname;proxied=$true} | ConvertTo-Json
    if (-not $rid) { Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records" -Headers $hdrs -Method POST -Body $dns | Out-Null; OK "CNAME created" }
    else           { Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records/$rid" -Headers $hdrs -Method PUT  -Body $dns | Out-Null; OK "CNAME updated" }

    # cloudflared as service
    $cfSvc = Get-Service "cloudflared" -EA SilentlyContinue
    if ($cfSvc) { if ($cfSvc.Status -eq "Running") { Stop-Service "cloudflared" -Force }; & $cfExe service uninstall 2>$null; Start-Sleep 2 }
    & $cfExe --config "$cfConfigDir\config.yml" service install
    Start-Service "cloudflared" -EA SilentlyContinue; Start-Sleep 3
    $cfSt = (Get-Service "cloudflared" -EA SilentlyContinue).Status
    if ($cfSt -eq "Running") { OK "Cloudflare Tunnel running → https://$CF_FullDomain" }
    else { Warn "cloudflared status: $cfSt" }

} elseif ($UseCF -match "^[Ee]$") {
    Write-Host ""
    Write-Host "  ── Step 6/6: Cloudflare Tunnel (existing / auto-create) ────" -ForegroundColor DarkGray
    $CF_FullDomain = $CF_Hostname

    # ── Locate or download cloudflared.exe ──────────────────────────────────
    $cfExe = $null
    $cfSearchPaths = @(
        (Get-Command cloudflared -EA SilentlyContinue | Select-Object -Exp Source),
        "C:\Program Files\cloudflared\cloudflared.exe",
        "C:\cloudflared\cloudflared.exe",
        "$env:ProgramFiles\cloudflared\cloudflared.exe",
        "$InstallDir\cloudflared\cloudflared.exe"
    )
    foreach ($p in $cfSearchPaths) { if ($p -and (Test-Path $p)) { $cfExe = $p; break } }
    if ($cfExe) { OK "Found cloudflared: $cfExe" } else {
        Info "cloudflared not found — downloading..."
        $cfDir = "$InstallDir\cloudflared"
        if (-not (Test-Path $cfDir)) { New-Item $cfDir -ItemType Directory -Force | Out-Null }
        $cfExe = "$cfDir\cloudflared.exe"
        Invoke-WebRequest "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" `
            -OutFile $cfExe -UseBasicParsing
        OK "cloudflared downloaded to $cfExe"
    }

    # ── Locate existing config.yml ───────────────────────────────────────────
    $cfConfigFile = $null
    $cfConfigCandidates = @(
        "C:\Windows\System32\config\systemprofile\.cloudflared\config.yml",
        "C:\ProgramData\cloudflared\config.yml",
        "$env:USERPROFILE\.cloudflared\config.yml",
        "C:\cloudflared\config.yml"
    )
    foreach ($p in $cfConfigCandidates) { if (Test-Path $p) { $cfConfigFile = $p; break } }
    if ($cfConfigFile) { OK "Found config: $cfConfigFile" }
    else {
        $cfConfigFile = "C:\Windows\System32\config\systemprofile\.cloudflared\config.yml"
        $cfConfigDir2 = Split-Path $cfConfigFile
        if (-not (Test-Path $cfConfigDir2)) { New-Item $cfConfigDir2 -ItemType Directory -Force | Out-Null }
        Warn "No config.yml found — will create one at $cfConfigFile"
    }

    # ── Read existing config, get tunnel ID ──────────────────────────────────
    $cfRaw = if (Test-Path $cfConfigFile) { Get-Content $cfConfigFile -Raw } else { "" }
    if ($cfRaw -match 'tunnel:\s*([a-f0-9\-]{36})') { $tunnelId = $Matches[1]; Info "Existing tunnel ID: $tunnelId" }

    # ── If no tunnel ID found and API token supplied → create new tunnel ─────
    if (-not $tunnelId) {
        if ($CF_ApiToken) {
            $CF_AccountId2 = Read-Host "  Cloudflare Account ID (needed to create tunnel)"
            $hdrs2 = @{ "Authorization"="Bearer $CF_ApiToken"; "Content-Type"="application/json" }
            $sec   = -join ((48..57)+(65..90)+(97..122) | Get-Random -Count 32 | % { [char]$_ })
            $secB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sec))
            $cr    = Invoke-RestMethod "https://api.cloudflare.com/client/v4/accounts/$CF_AccountId2/cfd_tunnel" `
                         -Headers $hdrs2 -Method POST `
                         -Body (@{name="wdpmgr-tunnel";tunnel_secret=$secB64}|ConvertTo-Json) -EA Stop
            $tunnelId = $cr.result.id
            $cfCredDir = Split-Path $cfConfigFile
            @{AccountTag=$CF_AccountId2;TunnelID=$tunnelId;TunnelName="wdpmgr-tunnel";TunnelSecret=$secB64} `
                | ConvertTo-Json | Set-Content "$cfCredDir\$tunnelId.json" -Encoding UTF8
            # Write fresh config
            @"
tunnel: $tunnelId
credentials-file: $($cfCredDir.Replace('\','/'))\$tunnelId.json
ingress:
  - service: http_status:404
"@ | Set-Content $cfConfigFile -Encoding UTF8
            $cfRaw = Get-Content $cfConfigFile -Raw
            OK "New tunnel created: $tunnelId"
        } else {
            Warn "No tunnel ID in config and no API token — cannot create tunnel. Provide token next run."
        }
    }

    # ── Add/update WdpMgr ingress entry (idempotent) ─────────────────────────
    if ($cfRaw) {
        $newEntry = "  - hostname: $CF_Hostname`n    service: http://localhost:$Port"
        $lines    = ($cfRaw -replace "`r`n","`n") -split "`n"
        $outLines = [System.Collections.Generic.List[string]]::new()
        $skip     = $false
        foreach ($line in $lines) {
            if ($line -match "hostname:\s*$([regex]::Escape($CF_Hostname))") { $skip = $true; continue }
            if ($skip -and $line -match "^\s+service:") { $skip = $false; continue }
            if ($skip) { continue }
            if ($line -match "^\s*-\s+service:\s+http_status") { $outLines.Add($newEntry) }
            $outLines.Add($line)
        }
        # If no catch-all was found, append
        if (-not ($outLines -join "`n" | Select-String "hostname:\s*$([regex]::Escape($CF_Hostname))")) {
            $outLines.Add($newEntry)
        }
        ($outLines -join "`n").TrimEnd() | Set-Content $cfConfigFile -Encoding UTF8
        OK "Ingress rule added/updated in $cfConfigFile"
    }

    # ── CNAME DNS record ──────────────────────────────────────────────────────
    if ($CF_ApiToken -and $CF_ZoneId -and $tunnelId) {
        $hdrs  = @{ "Authorization"="Bearer $CF_ApiToken"; "Content-Type"="application/json" }
        $cname = "$tunnelId.cfargotunnel.com"
        $sub   = ($CF_Hostname -split '\.')[0]
        $ex    = Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records?type=CNAME&name=$CF_Hostname" `
                     -Headers $hdrs -Method GET -EA SilentlyContinue
        $rid   = $ex.result[0].id
        $dns   = @{type="CNAME";name=$sub;content=$cname;proxied=$true} | ConvertTo-Json
        if (-not $rid) { Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records" -Headers $hdrs -Method POST -Body $dns | Out-Null; OK "CNAME created → $CF_Hostname" }
        else            { Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records/$rid" -Headers $hdrs -Method PUT  -Body $dns | Out-Null; OK "CNAME updated → $CF_Hostname" }
    } elseif ($tunnelId) {
        Warn "Add DNS manually: CNAME  $CF_Hostname  →  $tunnelId.cfargotunnel.com  (Proxied ON)"
    }

    # ── Restart cloudflared service ───────────────────────────────────────────
    $cfSvc = Get-Service "cloudflared" -EA SilentlyContinue
    if ($cfSvc) {
        Info "Restarting cloudflared service..."
        if ($cfSvc.Status -eq "Running") { Stop-Service "cloudflared" -Force; Start-Sleep 2 }
        Start-Service "cloudflared" -EA SilentlyContinue; Start-Sleep 3
        $cfSt = (Get-Service "cloudflared" -EA SilentlyContinue).Status
        if ($cfSt -eq "Running") { OK "cloudflared restarted → https://$CF_FullDomain" }
        else { Warn "cloudflared status: $cfSt — check: sc query cloudflared" }
    } elseif ($cfExe) {
        Info "Registering cloudflared as service..."
        & $cfExe --config $cfConfigFile service install
        Start-Service "cloudflared" -EA SilentlyContinue; Start-Sleep 3
        $cfSt = (Get-Service "cloudflared" -EA SilentlyContinue).Status
        if ($cfSt -eq "Running") { OK "cloudflared installed and running → https://$CF_FullDomain" }
        else { Warn "cloudflared service status: $cfSt" }
    } else { Warn "Could not start cloudflared — run manually: cloudflared tunnel --config $cfConfigFile run" }
}

# ── Summary ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "  ║                 Installation Complete!                  ║" -ForegroundColor Green
Write-Host "  ╠══════════════════════════════════════════════════════════╣" -ForegroundColor Green
$url = if ($CF_FullDomain) { "https://$CF_FullDomain" } else { "http://localhost:$Port" }
Write-Host ("  ║  Admin Panel:  $url".PadRight(61) + "║") -ForegroundColor Green
Write-Host ("  ║  Login:        $FirstUser / (your password)".PadRight(61) + "║") -ForegroundColor Green
Write-Host ("  ║  Master Key:   $AdminKey".PadRight(61) + "║") -ForegroundColor Green
Write-Host ("  ║  DB:           $dataDir\wdpmgr.db".PadRight(61) + "║") -ForegroundColor Green
Write-Host "  ╠══════════════════════════════════════════════════════════╣" -ForegroundColor Green
Write-Host "  ║  Next: Settings → Load Public Key → set-pubkey.bat     ║" -ForegroundColor Green
Write-Host "  ╚══════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Read-Host "  Press ENTER to finish"
