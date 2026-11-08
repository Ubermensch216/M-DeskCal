# Builds a runnable folder of M-DeskCal (framework-dependent: needs the .NET 10 Desktop Runtime on the target PC).
# Usage:  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Out dist]
param([string]$Out = "dist")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root $Out
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
dotnet publish (Join-Path $root "src\DeskCal.App\DeskCal.App.csproj") -c Release -r win-x64 --self-contained false -o $target -nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Write-Host "Published to $target"
Get-ChildItem $target | Select-Object Name, Length | Format-Table -AutoSize
