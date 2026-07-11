#Requires -RunAsAdministrator
# WdpMgr — Update script (no config prompts, keeps existing service settings)
# One-liner: Set-ExecutionPolicy Bypass -Scope Process -Force; iex (irm 'https://raw.githubusercontent.com/HackMe7822/WdpMgr/master/update.ps1')

param(
    [string]$InstallDir  = "C:\WdpMgrServer",
    [string]$ServiceName = "WdpMgrServer"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Info { param($m) Write-Host "  [INFO]  $m" -ForegroundColor Cyan }
function OK   { param($m) Write-Host "  [ OK ]  $m" -ForegroundColor Green }
function Fail { param($m) Write-Host "`n  [ERR]  $m`n" -ForegroundColor Red; Read-Host "Press ENTER to exit"; exit 1 }

Clear-Host
Write-Host ""
Write-Host "  ╔══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "  ║         WdpMgr Server — Updater                     ║" -ForegroundColor Cyan
Write-Host "  ╚══════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $InstallDir)) { Fail "Install dir not found: $InstallDir — run install.ps1 first." }

# ── Stop service ───────────────────────────────────────────────────────────────
Info "Stopping service..."
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") { Stop-Service $ServiceName -Force; Start-Sleep 2 }
OK "Service stopped"

# ── Pull latest code ───────────────────────────────────────────────────────────
Info "Downloading latest code from GitHub..."
$repoDir = "$InstallDir\repo"
$zipFile = "$env:TEMP\wdpmgr_update.zip"
if (Test-Path $repoDir) { Remove-Item $repoDir -Recurse -Force }
Invoke-WebRequest "https://github.com/HackMe7822/WdpMgr/archive/refs/heads/master.zip" `
    -OutFile $zipFile -UseBasicParsing
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zipFile, "$InstallDir\_tmp")
Move-Item "$InstallDir\_tmp\WdpMgr-master" $repoDir
Remove-Item "$InstallDir\_tmp" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipFile -Force -ErrorAction SilentlyContinue
OK "Latest code downloaded"

# ── Rebuild server ─────────────────────────────────────────────────────────────
Info "Building server..."
& dotnet publish "$repoDir\Server\WdpMgrServer.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o "$InstallDir\app" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "Build failed. Check output above." }
OK "Server built"

# ── Copy client base EXEs ─────────────────────────────────────────────────────
$dataDir = "$InstallDir\data"
@(
    @{ Src = "$repoDir\ClearDisplayAffinity\WdpMgr.exe";   Dst = "$dataDir\WdpMgr_base.exe"   },
    @{ Src = "$repoDir\WinOverlay\WinOverlay.exe";          Dst = "$dataDir\WinOverlay_base.exe" }
) | ForEach-Object {
    if (Test-Path $_.Src) {
        Copy-Item $_.Src $_.Dst -Force
        OK "$([IO.Path]::GetFileName($_.Dst)) updated ($([math]::Round((Get-Item $_.Dst).Length/1KB,1)) KB)"
    } else {
        Write-Host "  [WARN]  $([IO.Path]::GetFileName($_.Src)) not in repo — keeping existing" -ForegroundColor Yellow
    }
}

# ── Restart service ────────────────────────────────────────────────────────────
Info "Starting service..."
Start-Service $ServiceName; Start-Sleep 3
$st = (Get-Service $ServiceName).Status
if ($st -eq "Running") { OK "Service running" } else { Fail "Service failed to start — check Event Viewer." }

Write-Host ""
Write-Host "  ══════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Update complete! Hard-refresh the admin panel (Ctrl+Shift+R)" -ForegroundColor Green
Write-Host "  ══════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
