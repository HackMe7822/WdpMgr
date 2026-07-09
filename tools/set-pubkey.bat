@echo off
:: set-pubkey.bat — Embed RSA public key from server into WdpMgr.cs and recompile
:: Usage: set-pubkey.bat <path-to-WdpMgr.cs>
::
:: 1. Go to your admin panel → Settings → Load Public Key → Copy
:: 2. When prompted, paste the XML key and press Enter twice
:: 3. The script patches WdpMgr.cs and recompiles WdpMgr.exe

setlocal enabledelayedexpansion

set "SRC=%~dp0..\ClearDisplayAffinity\WdpMgr.cs"
if not exist "%SRC%" (
    echo ERROR: Cannot find %SRC%
    echo Make sure set-pubkey.bat is in the tools\ subfolder.
    pause & exit /b 1
)

echo ============================================================
echo  WdpMgr — RSA Public Key Updater
echo ============================================================
echo.
echo Paste the RSA public key XML from the admin panel.
echo (Settings -^> Load Public Key -^> Copy)
echo Press ENTER twice when done.
echo.
set "PUBKEY="
:input_loop
set "LINE="
set /p LINE=""
if defined LINE (
    set "PUBKEY=!PUBKEY!!LINE!"
    goto input_loop
)

if not defined PUBKEY (
    echo ERROR: No key entered.
    pause & exit /b 1
)

echo.
echo Key received ^(first 40 chars^): !PUBKEY:~0,40!...
echo.

:: Use PowerShell to do the replacement (handles long strings reliably)
powershell -NoProfile -Command ^
    "$src = '%SRC%'.Replace('\','\\');" ^
    "$key = '%PUBKEY%';" ^
    "$content = Get-Content $src -Raw;" ^
    "$old = 'REPLACE_WITH_SERVER_PUBLIC_KEY';" ^
    "if ($content -notmatch [regex]::Escape($old)) { Write-Host 'ERROR: Placeholder not found in source. Already patched?'; exit 1; }" ^
    "$content = $content.Replace($old, $key);" ^
    "Set-Content -Path $src -Value $content -NoNewline -Encoding UTF8;" ^
    "Write-Host 'Source file patched.';"

if errorlevel 1 ( pause & exit /b 1 )

echo.
echo Recompiling WdpMgr.exe...

:: Try finding csc.exe in common VS / Build Tools locations
set "CSC="
for %%P in (
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
    "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
) do (
    if exist %%P ( set "CSC=%%~P" & goto found_csc )
)
:found_csc

if not defined CSC (
    echo ERROR: csc.exe not found. Install .NET Framework SDK or Visual Studio Build Tools.
    pause & exit /b 1
)

set "DIR=%~dp0..\ClearDisplayAffinity"
pushd "%DIR%"

"%CSC%" /nologo /optimize+ /target:winexe /win32manifest:app.manifest ^
    /reference:System.ServiceProcess.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Management.dll ^
    /out:WdpMgr.exe WdpMgr.cs

if errorlevel 1 (
    popd
    echo.
    echo ERROR: Compilation failed. Check the output above.
    pause & exit /b 1
)

popd

echo.
echo ============================================================
echo  Done!  WdpMgr.exe rebuilt with embedded RSA public key.
echo  Distribute WdpMgr.exe + wdp.lic together to each machine.
echo ============================================================
pause
