$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Windows 11 is required.'
}

$osVersion = [Environment]::OSVersion.Version
if ($osVersion.Build -lt 22000) {
    throw "Windows 11 build 22000 or newer is required; found: $osVersion"
}

if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Windows x64 is required.'
}

$sdkOutput = & dotnet --list-sdks
if (-not ($sdkOutput | Select-String '^8\.0\.')) {
    throw '.NET 8 SDK is required.'
}

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$buildTools = (& $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools `
    -requires Microsoft.Component.MSBuild `
    Microsoft.VisualStudio.Component.Windows11SDK.26100 `
    -property installationPath).Trim()
if (-not $buildTools) {
    throw 'Visual Studio 2022 Build Tools with MSBuild and Windows SDK 26100 is required.'
}

$msbuild = Join-Path $buildTools 'MSBuild\Current\Bin\MSBuild.exe'
$msbuildVersion = (& $msbuild -version -nologo).Trim()
if ($msbuildVersion -notmatch '^17\.14\.') {
    throw "MSBuild 17.14.x is required; found: $msbuildVersion"
}

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0'
if (-not (Test-Path -LiteralPath $sdkRoot)) {
    throw "Windows SDK 10.0.26100.0 was not found at $sdkRoot"
}

$appRuntime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' |
    Where-Object { $_.Architecture -eq 'X64' } |
    Sort-Object Version -Descending |
    Select-Object -First 1
if (-not $appRuntime) {
    throw 'Microsoft Windows App Runtime 1.8 x64 is required.'
}

$ffiLibrary = Join-Path $PSScriptRoot '..\target\debug\clipboard_ffi.dll'
if (-not (Test-Path -LiteralPath $ffiLibrary)) {
    throw 'target/debug/clipboard_ffi.dll is required; run cargo build -p clipboard-ffi.'
}

Write-Host "Windows: $osVersion"
Write-Host "MSBuild: $msbuildVersion"
Write-Host "Windows App Runtime: $($appRuntime.Version)"
Write-Host 'Windows client toolchain verification passed.'
