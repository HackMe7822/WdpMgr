#Requires -RunAsAdministrator
<#
.SYNOPSIS
  WdpMgr Server — One-click deployment for Windows Server 2019/2022
.EXAMPLE
  .\deploy.ps1
  .\deploy.ps1 -InstallDir "D:\WdpMgrServer" -Port 5001
#>
param(
    [string]$InstallDir   = "C:\WdpMgrServer",
    [string]$ServiceName  = "WdpMgrServer",
    [string]$CF_TunnelName = "wdpmgr-tunnel",
    [int]   $Port         = 5000
)

$ErrorActionPreference = "Stop"

function Info  { param($m) Write-Host "  [INFO]  $m" -ForegroundColor Cyan }
function OK    { param($m) Write-Host "  [ OK ]  $m" -ForegroundColor Green }
function Warn  { param($m) Write-Host "  [WARN]  $m" -ForegroundColor Yellow }
function Fail  { param($m) Write-Host "  [ERR ]  $m" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║   WdpMgr Server — Windows Deployment        ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── Collect configuration ──────────────────────────────────────────────────────
Write-Host "── Configuration ──────────────────────────────────────" -ForegroundColor DarkGray

$AdminKey = Read-Host "Admin key (leave blank to auto-generate)"
if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    $AdminKey = -join ((65..90) + (97..122) + (48..57) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    Warn "Generated admin key: $AdminKey"
}

$CF_ApiToken  = Read-Host "Cloudflare API token (Zone Read + DNS Edit)"
$CF_AccountId = Read-Host "Cloudflare Account ID"
$CF_ZoneId    = Read-Host "Cloudflare Zone ID"
$CF_Domain    = Read-Host "Your domain (e.g. example.com)"
$CF_Subdomain = Read-Host "Subdomain prefix (e.g. wdpmgr)"
$CF_FullDomain = "$CF_Subdomain.$CF_Domain"

Write-Host ""
Info "Will deploy to:  https://$CF_FullDomain"
$confirm = Read-Host "Continue? [y/N]"
if ($confirm -notmatch "^[Yy]$") { Write-Host "Aborted."; exit 0 }

# ── Install .NET 8 ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "── Installing .NET 8 ─────────────────────────────────────" -ForegroundColor DarkGray

$dotnetOk = $false
try {
    $ver = (& dotnet --version 2>$null)
    if ($ver -match "^8\.") { OK ".NET $ver already installed"; $dotnetOk = $true }
} catch {}

if (-not $dotnetOk) {
    Info "Downloading .NET 8 ASP.NET Core Runtime installer..."
    $dotnetUrl = "https://dot.net/v1/dotnet-install.ps1"
    $dotnetInstall = "$env:TEMP\dotnet-install.ps1"
    Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetInstall -UseBasicParsing
    & $dotnetInstall -Runtime "aspnetcore" -Version "8.0" -InstallDir "C:\dotnet8"
    $env:PATH = "C:\dotnet8;" + $env:PATH
    [System.Environment]::SetEnvironmentVariable("PATH", "C:\dotnet8;" + [System.Environment]::GetEnvironmentVariable("PATH","Machine"), "Machine")

    # Check for full SDK (needed to build)
    Info "Downloading .NET 8 SDK..."
    & $dotnetInstall -Version "8.0" -InstallDir "C:\dotnet8"
    OK ".NET 8 SDK installed"
}

# ── Set up install directory and clone/update repo ─────────────────────────────
Write-Host ""
Write-Host "── Setting up app directory ───────────────────────────────" -ForegroundColor DarkGray

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Info "Created $InstallDir"
}

$repoUrl = "https://github.com/HackMe7822/WdpMgr.git"
$repoDir = "$InstallDir\repo"

if (Test-Path "$repoDir\.git") {
    Info "Updating existing repo..."
    & git -C $repoDir pull --quiet
} else {
    Info "Cloning repo..."
    & git clone $repoUrl $repoDir --quiet
}
if ($LASTEXITCODE -ne 0) { Fail "git clone/pull failed" }
OK "Repo ready at $repoDir"

# ── Build server ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "── Building server ────────────────────────────────────────" -ForegroundColor DarkGray

$publishDir = "$InstallDir\app"
Info "Publishing (win-x64, self-contained)..."
& dotnet publish "$repoDir\Server\WdpMgrServer.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -o $publishDir --nologo -v quiet

if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed" }
OK "Published to $publishDir\WdpMgrServer.exe"

# ── Create data directory and env file ─────────────────────────────────────────
$dataDir = "$InstallDir\data"
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }

$envFile = "$InstallDir\wdpmgr.env"
@"
WDPMGR_ADMIN_KEY=$AdminKey
WDPMGR_DB_PATH=$dataDir\wdpmgr.db
PORT=$Port
"@ | Set-Content -Path $envFile -Encoding UTF8
# Restrict read permissions on env file (admin only)
$acl = Get-Acl $envFile
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators","FullControl","Allow")
$acl.AddAccessRule($rule)
Set-Acl -Path $envFile -AclObject $acl -ErrorAction SilentlyContinue
OK "Env file: $envFile"

# ── Install app as Windows Service ─────────────────────────────────────────────
Write-Host ""
Write-Host "── Installing Windows Service ─────────────────────────────" -ForegroundColor DarkGray

$exePath = "$publishDir\WdpMgrServer.exe"

# Stop and remove old service if exists
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Info "Stopping existing $ServiceName service..."
    if ($svc.Status -eq "Running") { Stop-Service $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Create service (runs as LocalSystem, auto-start)
Info "Creating service $ServiceName..."
& sc.exe create $ServiceName `
    binPath= "`"$exePath`"" `
    start= auto `
    DisplayName= "WdpMgr License Server" | Out-Null

& sc.exe description $ServiceName "Windows Display Policy Manager License Server" | Out-Null

# Set environment variables for the service via registry
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$envVars = @(
    "WDPMGR_ADMIN_KEY=$AdminKey",
    "WDPMGR_DB_PATH=$dataDir\wdpmgr.db",
    "PORT=$Port"
)
Set-ItemProperty -Path $regPath -Name "Environment" -Value $envVars -Type MultiString

Start-Service $ServiceName
Start-Sleep -Seconds 3
$svcStatus = (Get-Service -Name $ServiceName).Status
if ($svcStatus -eq "Running") { OK "Service $ServiceName is running" }
else { Warn "Service status: $svcStatus — check: Get-EventLog -LogName Application -Source WdpMgrServer -Newest 10" }

# ── Install cloudflared ─────────────────────────────────────────────────────────
Write-Host ""
Write-Host "── Installing cloudflared ─────────────────────────────────" -ForegroundColor DarkGray

$cfDir = "$InstallDir\cloudflared"
if (-not (Test-Path $cfDir)) { New-Item -ItemType Directory -Path $cfDir -Force | Out-Null }
$cfExe = "$cfDir\cloudflared.exe"

if (-not (Test-Path $cfExe)) {
    Info "Downloading cloudflared for Windows..."
    Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" `
        -OutFile $cfExe -UseBasicParsing
    OK "cloudflared downloaded"
} else {
    OK "cloudflared already present"
}

# ── Create Cloudflare Tunnel ───────────────────────────────────────────────────
Write-Host ""
Write-Host "── Setting up Cloudflare Tunnel ───────────────────────────" -ForegroundColor DarkGray

$cfConfigDir = "$InstallDir\cloudflared-config"
if (-not (Test-Path $cfConfigDir)) { New-Item -ItemType Directory -Path $cfConfigDir -Force | Out-Null }

$headers = @{
    "Authorization" = "Bearer $CF_ApiToken"
    "Content-Type"  = "application/json"
}

# Check for existing tunnel
Info "Checking for existing tunnel '$CF_TunnelName'..."
$tunnelRes = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/accounts/$CF_AccountId/cfd_tunnel?name=$CF_TunnelName" `
    -Headers $headers -Method GET -ErrorAction SilentlyContinue
$tunnelId = $tunnelRes.result[0].id

if (-not $tunnelId) {
    Info "Creating new Cloudflare Tunnel..."
    $secret = -join ((65..90) + (97..122) + (48..57) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    $secretB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($secret))
    $body = @{ name = $CF_TunnelName; tunnel_secret = $secretB64 } | ConvertTo-Json
    $createRes = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/accounts/$CF_AccountId/cfd_tunnel" `
        -Headers $headers -Method POST -Body $body -ErrorAction Stop
    $tunnelId = $createRes.result.id
    if (-not $tunnelId) { Fail "Failed to create tunnel. Check API token has 'Cloudflare Tunnel:Edit' permission." }

    # Write credentials file
    @{
        AccountTag   = $CF_AccountId
        TunnelID     = $tunnelId
        TunnelName   = $CF_TunnelName
        TunnelSecret = $secretB64
    } | ConvertTo-Json | Set-Content -Path "$cfConfigDir\$tunnelId.json" -Encoding UTF8
    OK "Tunnel created: $tunnelId"
} else {
    OK "Reusing existing tunnel: $tunnelId"
}

# Write config.yml
@"
tunnel: $tunnelId
credentials-file: $($cfConfigDir.Replace('\','/'))\$tunnelId.json

ingress:
  - hostname: $CF_FullDomain
    service: http://localhost:$Port
  - service: http_status:404
"@ | Set-Content -Path "$cfConfigDir\config.yml" -Encoding UTF8
OK "cloudflared config: $cfConfigDir\config.yml"

# ── Create CNAME DNS record (idempotent) ───────────────────────────────────────
$cnameTarget = "$tunnelId.cfargotunnel.com"
Info "Setting up CNAME: $CF_FullDomain → $cnameTarget"

$existing = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records?type=CNAME&name=$CF_FullDomain" `
    -Headers $headers -Method GET -ErrorAction SilentlyContinue
$recordId = $existing.result[0].id

$dnsBody = @{
    type    = "CNAME"
    name    = $CF_Subdomain
    content = $cnameTarget
    proxied = $true
} | ConvertTo-Json

if (-not $recordId) {
    Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records" `
        -Headers $headers -Method POST -Body $dnsBody | Out-Null
    OK "CNAME record created"
} else {
    Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones/$CF_ZoneId/dns_records/$recordId" `
        -Headers $headers -Method PUT -Body $dnsBody | Out-Null
    OK "CNAME record updated"
}

# ── Install cloudflared as Windows Service ──────────────────────────────────────
Write-Host ""
Write-Host "── Installing cloudflared service ─────────────────────────" -ForegroundColor DarkGray

$cfSvc = Get-Service -Name "cloudflared" -ErrorAction SilentlyContinue
if ($cfSvc) {
    Info "Removing old cloudflared service..."
    if ($cfSvc.Status -eq "Running") { Stop-Service "cloudflared" -Force }
    & $cfExe service uninstall 2>$null | Out-Null
    Start-Sleep -Seconds 2
}

Info "Installing cloudflared service..."
& $cfExe --config "$cfConfigDir\config.yml" service install
if ($LASTEXITCODE -eq 0) {
    Start-Service "cloudflared" -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    $cfStatus = (Get-Service "cloudflared" -ErrorAction SilentlyContinue).Status
    if ($cfStatus -eq "Running") { OK "cloudflared tunnel is running" }
    else { Warn "cloudflared service status: $cfStatus — check Event Viewer" }
} else {
    Warn "cloudflared service install failed — try manually: $cfExe service install"
}

# ── Firewall: block direct port access (only tunnel allowed) ───────────────────
netsh advfirewall firewall delete rule name="WdpMgrServer" 2>$null | Out-Null
netsh advfirewall firewall add rule name="WdpMgrServer" dir=in action=block protocol=TCP localport=$Port remoteip="!LocalSubnet" 2>$null | Out-Null

# ── Summary ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "  ║              Deployment Complete!                   ║" -ForegroundColor Green
Write-Host "  ╠══════════════════════════════════════════════════════╣" -ForegroundColor Green
Write-Host ("  ║  Admin Panel:  https://$CF_FullDomain".PadRight(55) + "║") -ForegroundColor Green
Write-Host ("  ║  Admin Key:    $AdminKey".PadRight(55) + "║") -ForegroundColor Green
Write-Host ("  ║  DB Path:      $dataDir\wdpmgr.db".PadRight(55) + "║") -ForegroundColor Green
Write-Host ("  ║  Tunnel ID:    $tunnelId".PadRight(55) + "║") -ForegroundColor Green
Write-Host "  ╠══════════════════════════════════════════════════════╣" -ForegroundColor Green
Write-Host "  ║  Next steps:                                        ║" -ForegroundColor Green
Write-Host "  ║  1. Open admin panel → Settings → Load Public Key  ║" -ForegroundColor Green
Write-Host "  ║  2. Run tools\set-pubkey.bat on your build machine  ║" -ForegroundColor Green
Write-Host "  ║  3. Create licenses → Download wdp.lic → distribute║" -ForegroundColor Green
Write-Host "  ╚══════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  Service management:" -ForegroundColor DarkGray
Write-Host "    Start:   Start-Service $ServiceName" -ForegroundColor DarkGray
Write-Host "    Stop:    Stop-Service $ServiceName" -ForegroundColor DarkGray
Write-Host "    Logs:    Get-EventLog -LogName Application -Source WdpMgrServer -Newest 20" -ForegroundColor DarkGray
Write-Host ""
