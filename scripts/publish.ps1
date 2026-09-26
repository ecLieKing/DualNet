param(
  [string]$DotNetExe = "dotnet",
  [string]$Version = "1.3.1"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$workRoot = Join-Path $repoRoot "work"
$stageRoot = Join-Path $workRoot "publish-stage"
$outputsRoot = Join-Path $repoRoot "outputs"
$releaseRoot = Join-Path $outputsRoot "双网通-v$Version-win-x64"
$zipPath = Join-Path $outputsRoot "双网通-v$Version-win-x64.zip"

if (-not (Test-Path -LiteralPath $DotNetExe)) {
  $resolved = Get-Command $DotNetExe -ErrorAction Stop
  $DotNetExe = $resolved.Source
}

New-Item -ItemType Directory -Force -Path $workRoot,$outputsRoot | Out-Null
foreach ($target in @($stageRoot, $releaseRoot)) {
  $parent = [IO.Path]::GetDirectoryName($target)
  if ($parent -notin @($workRoot, $outputsRoot)) { throw "发布清理路径越界：$target" }
  if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

& $DotNetExe publish (Join-Path $repoRoot "src\DualNet.App\DualNet.App.csproj") `
  -c Release -r win-x64 --self-contained true --nologo `
  -o $stageRoot `
  -p:Version=$Version `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishTrimmed=false `
  -p:DebugType=None `
  -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败。" }

$exe = Join-Path $stageRoot "双网通.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "发布结果中没有找到双网通.exe。" }
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $releaseRoot "双网通.exe")

$hash = Get-FileHash -LiteralPath (Join-Path $releaseRoot "双网通.exe") -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  双网通.exe" | Set-Content -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Encoding UTF8
Compress-Archive -Path (Join-Path $releaseRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Output "发布完成：$releaseRoot"
Write-Output "便携包：$zipPath"
Write-Output "SHA256：$($hash.Hash.ToLowerInvariant())"
