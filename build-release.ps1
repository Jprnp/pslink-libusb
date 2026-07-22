<#
  Assembles a distributable release bundle in .\release\PulseEliteCompanion\ plus a .zip.
  The bundle = the published single-file exe + setup.ps1 + the driver files. A user just
  unzips it and runs setup.ps1.

  Run from the repo root:  powershell -ExecutionPolicy Bypass -File build-release.ps1
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "app\PulseTray\PulseTray.csproj"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }

Write-Host "Publishing (self-contained, single-file, compressed)..."
& $dotnet publish $proj -c Release
$pub = Join-Path $root "app\PulseTray\bin\Release\net10.0-windows\win-x64\publish\PulseElite.exe"
if (-not (Test-Path $pub)) { throw "published exe not found: $pub" }

$out = Join-Path $root "release\PulseEliteCompanion"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $out "driver") | Out-Null

Copy-Item $pub $out
Copy-Item (Join-Path $root "app\setup.ps1") $out
foreach ($f in "PulseElite.inf", "install.ps1", "uninstall.ps1") {
    Copy-Item (Join-Path $root "app\driver\$f") (Join-Path $out "driver")
}

$zip = Join-Path $root "release\PulseEliteCompanion.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

Write-Host ""
Write-Host "Bundle folder: $out"
Write-Host "Zip (upload this as a GitHub Release asset): $zip"
