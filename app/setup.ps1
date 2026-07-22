<#
  Pulse Elite Companion — one-shot installer.

  Steps:
    1) install the WinUSB driver on the PS Link dongle's control interface (one UAC prompt);
    2) copy the app to %LOCALAPPDATA%\PulseEliteCompanion;
    3) enable start-with-Windows and launch it.

  Run (no admin needed — the driver step asks for UAC by itself):
    right-click this file > "Run with PowerShell"
  or:
    powershell -ExecutionPolicy Bypass -File setup.ps1

  Expected next to this script (a release bundle): PulseElite.exe and driver\ .
  Uninstall: run driver\uninstall.ps1, delete the install folder, remove the Run key.
#>
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$appName = "PulseEliteCompanion"

Write-Host "[1/3] Installing the WinUSB driver (a UAC prompt will appear)..."
& (Join-Path $here "driver\install.ps1")

Write-Host "[2/3] Installing the app..."
Get-Process -Name PulseElite -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$dest = Join-Path $env:LOCALAPPDATA $appName
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $here "PulseElite.exe") $dest -Force
$exe = Join-Path $dest "PulseElite.exe"

Write-Host "[3/3] Enabling start-with-Windows and launching..."
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
New-ItemProperty -Path $run -Name $appName -Value "`"$exe`"" -PropertyType String -Force | Out-Null
Start-Process $exe

Write-Host ""
Write-Host "Done. Pulse Elite Companion is running in your system tray." -ForegroundColor Green
Write-Host "Installed to: $dest"
