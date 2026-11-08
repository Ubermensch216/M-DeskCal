# M-DeskCal 설치 스크립트 (현재 Windows 사용자용 — 관리자 권한이 필요 없습니다)
# 사용법:  Install.cmd 를 더블클릭하거나  powershell -ExecutionPolicy Bypass -File install.ps1 [-InstallDir 경로] [-Silent]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\M-DeskCal"),
    [switch]$Silent,        # 질문 없이 설치하고 실행하지 않음
    [switch]$NoStartMenu
)
$ErrorActionPreference = "Stop"
$payload = Join-Path $PSScriptRoot "app"
if (-not (Test-Path (Join-Path $payload "DeskCal.App.exe"))) { throw "설치 파일(app 폴더)을 찾을 수 없습니다. zip 을 모두 풀고 다시 실행하세요." }
$exe = Join-Path $InstallDir "DeskCal.App.exe"
$version = (Get-Item (Join-Path $payload "DeskCal.App.exe")).VersionInfo.ProductVersion

Write-Host "M-DeskCal $version 을(를) 설치합니다." -ForegroundColor Cyan
Write-Host "설치 위치: $InstallDir"

# 실행 중이면 닫는다 (같은 폴더의 프로그램만)
Get-Process -Name "DeskCal.App", "deskcal-mcp" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host "실행 중인 $($_.ProcessName) 을(를) 종료합니다."; $_.Kill(); $_.WaitForExit(10000) | Out-Null }

# 파일 복사 (이전 버전 파일은 지우고 새로 복사. 사용자 데이터는 %AppData%\DeskCal 에 있어 건드리지 않음)
if (Test-Path $InstallDir) { Remove-Item (Join-Path $InstallDir "*") -Recurse -Force }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $payload "*") $InstallDir -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $InstallDir -Force

# 시작 메뉴 바로 가기
if (-not $NoStartMenu) {
    $lnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\M-DeskCal.lnk"
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $exe; $s.WorkingDirectory = $InstallDir; $s.Description = "M-DeskCal 바탕화면 달력"; $s.IconLocation = "$exe,0"
    $s.Save()
}

# '설정 > 앱' 에 표시되는 제거 정보
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\M-DeskCal"
New-Item -Path $key -Force | Out-Null
$size = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$props = @{
    DisplayName = "M-DeskCal"; DisplayVersion = $version; Publisher = "M-DeskCal"; InstallLocation = $InstallDir
    DisplayIcon = "$exe,0"; EstimatedSize = $size; NoModify = 1; NoRepair = 1
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
}
foreach ($k in $props.Keys) { New-ItemProperty -Path $key -Name $k -Value $props[$k] -Force | Out-Null }

# 자동 실행을 이미 켜 두었다면(이전 설치) 새 경로로 갱신 — 앱도 시작할 때 스스로 고치지만 재부팅 전에 맞춰 둔다
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Get-ItemProperty -Path $run -Name "M-DeskCal" -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $run -Name "M-DeskCal" -Value "`"$exe`" --autostart"
}

Write-Host "설치를 마쳤습니다." -ForegroundColor Green
if (-not $Silent) {
    $a = Read-Host "지금 실행할까요? (Y/n)"
    if ($a -eq "" -or $a -match "^[yY]") { Start-Process $exe }
}
