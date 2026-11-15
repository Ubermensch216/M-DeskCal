# 배포용 zip 을 만든다: 런타임 포함(.NET 설치 불필요) + 설치/제거 스크립트 + 사용자 가이드.
# 사용법:  powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-Out release]
param([string]$Out = "release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = $props.Project.PropertyGroup.Version
$name = "M-DeskCal-$version-win-x64"
$stage = Join-Path $root "$Out\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "app") | Out-Null

# 앱과 MCP 서버를 같은 폴더에 게시한다 (공통 런타임 파일은 한 벌, 미사용 어셈블리·R2R 중복·외국어 리소스는 제거)
$trimArgs = @(
    "-p:SatelliteResourceLanguages=ko",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:_SuppressWpfTrimError=true",
    "-p:_SuppressWinFormsTrimError=true",
    "-p:PublishTrimmed=true",
    "-p:TrimMode=partial",
    "-p:BuiltInComInteropSupport=true",
    "-p:NoWarn=IL2026%3BIL2050%3BIL2062%3BIL2067%3BIL2070%3BIL2072%3BIL2075%3BIL2087%3BIL2104"
)
dotnet publish (Join-Path $root "src\DeskCal.App\DeskCal.App.csproj") -c Release -r win-x64 --self-contained true @trimArgs -o (Join-Path $stage "app") -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed: DeskCal.App" }

$mcpTmp = Join-Path $stage "mcp-tmp"
dotnet publish (Join-Path $root "src\DeskCal.Mcp\DeskCal.Mcp.csproj") -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o $mcpTmp -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed: DeskCal.Mcp" }
Copy-Item (Join-Path $mcpTmp "deskcal-mcp.*") (Join-Path $stage "app") -Force
Remove-Item $mcpTmp -Recurse -Force

Get-ChildItem (Join-Path $stage "app") -Filter *.pdb | Remove-Item -Force   # 디버그 기호는 배포하지 않는다

Copy-Item (Join-Path $root "tools\installer\*") $stage -Force
Copy-Item (Join-Path $root "docs\USER_GUIDE.md") (Join-Path $stage "사용자 가이드.md") -Force
Copy-Item (Join-Path $root "docs\RELEASE_NOTES.md") (Join-Path $stage "릴리스 노트.md") -Force

$zip = Join-Path $root "$Out\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ASCII
"{0}  {1:N1} MB`nSHA-256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash
