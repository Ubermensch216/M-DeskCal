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

# 앱과 MCP 서버를 같은 폴더에 게시한다 (공통 런타임 파일은 한 벌)
foreach ($p in @("src\DeskCal.App\DeskCal.App.csproj", "src\DeskCal.Mcp\DeskCal.Mcp.csproj")) {
    dotnet publish (Join-Path $root $p) -c Release -r win-x64 --self-contained true -o (Join-Path $stage "app") -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}
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
