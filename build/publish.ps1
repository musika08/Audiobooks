# Builds a self-contained, portable TTSApp into .\publish
# Usage:  powershell -ExecutionPolicy Bypass -File build\publish.ps1
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "TTSApp\TTSApp.csproj"
$out  = Join-Path $root "publish"

# Read the application version from the project file so installer/README stay in sync.
[xml]$csproj = Get-Content $proj
$version = $csproj.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = $csproj.Project.PropertyGroup.AssemblyVersion
}
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = "1.0.0"
}
$version = $version.Trim()

Write-Host "Publishing self-contained win-x64 build (version $version)..." -ForegroundColor Cyan

# Clean output directory so stale files from previous builds cannot be packaged.
if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}
New-Item -ItemType Directory -Path $out | Out-Null

dotnet publish $proj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$version -o $out

# Trim non-Windows native runtimes that .NET copies but Windows never uses.
$rt = Join-Path $out "runtimes"
foreach ($d in "android-arm64","linux-arm64","linux-x64","osx-arm64","osx-x64","win-x86","win-arm64") {
    $p = Join-Path $rt $d
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}

$size = [math]::Round((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 0)
Write-Host "Done. Output: $out  (~$size MB)" -ForegroundColor Green
Write-Host "Build installer with: iscc build\installer.iss /DAppVersion=$version" -ForegroundColor Green
