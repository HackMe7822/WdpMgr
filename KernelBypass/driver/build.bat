@echo off
:: Build kbypass.sys using MSBuild + WDK
:: Requires: Visual Studio 2022 + WDK 11 installed
::
:: Output: x64\Release\kbypass.sys
:: After building, copy kbypass.sys next to KernelBypass.exe
:: (or embed as resource in the .csproj — see README)
::
:: Enable test signing on target machine:
::   bcdedit /set testsigning on
::   [reboot]
::
:: Then the C# service will install and start the driver automatically.

where msbuild >nul 2>&1
if errorlevel 1 (
    echo [ERROR] MSBuild not found. Run from Developer Command Prompt.
    exit /b 1
)

msbuild kbypass.vcxproj /p:Configuration=Release /p:Platform=x64
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo.
echo [OK] Build succeeded.
echo [OK] Driver: x64\Release\kbypass.sys
echo.
echo Next steps:
echo   1. Copy kbypass.sys to same folder as KernelBypass.exe
echo   2. On target machine: bcdedit /set testsigning on  [reboot]
echo   3. Run KernelBypass.exe --install as Administrator
