# M-DeskCal 제거 스크립트
# 사용법: 설정 > 앱 에서 제거하거나  powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-RemoveData] [-Silent]
param(
    [switch]$RemoveData,        # 일정·할 일 등 사용자 데이터(%AppData%\DeskCal)까지 삭제
    [switch]$Silent,            # 질문 없이 진행 (데이터는 -RemoveData 가 없으면 보존)
    [string]$InstallDirArg = "" # 내부용: 임시 복사본으로 다시 실행될 때 설치 폴더를 받는다
)
$ErrorActionPreference = "Stop"
$installDir = if ($InstallDirArg) { $InstallDirArg } else { $PSScriptRoot }

# 설치 폴더 안의 스크립트는 자기 자신이 든 폴더를 지우기 어려우므로 임시 폴더로 복사해 다시 실행한다
if (-not $InstallDirArg) {
    $tmp = Join-Path $env:TEMP ("m-deskcal-uninstall-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".ps1")
    Copy-Item $MyInvocation.MyCommand.Path $tmp
    $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$tmp`"", "-InstallDirArg", "`"$installDir`"")
    if ($RemoveData) { $a += "-RemoveData" }
    if ($Silent) { $a += "-Silent" }
    Start-Process powershell.exe -ArgumentList $a -Wait -NoNewWindow
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    return
}

Write-Host "M-DeskCal 을(를) 제거합니다: $installDir" -ForegroundColor Cyan

Get-Process -Name "DeskCal.App", "deskcal-mcp" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host "실행 중인 $($_.ProcessName) 을(를) 종료합니다."; $_.Kill(); $_.WaitForExit(10000) | Out-Null }

# 자동 실행 항목은 이 설치를 가리킬 때만 지운다
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$cur = (Get-ItemProperty -Path $run -Name "M-DeskCal" -ErrorAction SilentlyContinue)."M-DeskCal"
if ($cur -and $cur.StartsWith("`"$installDir", [StringComparison]::OrdinalIgnoreCase)) { Remove-ItemProperty -Path $run -Name "M-DeskCal" -Force }

Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\M-DeskCal.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\M-DeskCal" -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }

$data = Join-Path $env:APPDATA "DeskCal"
if (Test-Path $data) {
    if (-not $RemoveData -and -not $Silent) {
        $ans = Read-Host "일정·할 일 등 사용자 데이터($data)도 삭제할까요? 되돌릴 수 없습니다. (y/N)"
        if ($ans -match "^[yY]") { $RemoveData = $true }
    }
    if ($RemoveData) { Remove-Item $data -Recurse -Force; Write-Host "사용자 데이터를 삭제했습니다." }
    else { Write-Host "사용자 데이터는 그대로 두었습니다: $data" }
}
Write-Host "제거를 마쳤습니다." -ForegroundColor Green
