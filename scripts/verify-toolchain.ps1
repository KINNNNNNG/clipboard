$ErrorActionPreference = 'Stop'

$rustVersion = (& rustc --version).Trim()
if ($rustVersion -notmatch '^rustc 1\.88\.') {
    throw "Rust 1.88.x is required; found: $rustVersion"
}

$sdkOutput = & dotnet --list-sdks
if (-not ($sdkOutput | Select-String '^8\.0\.')) {
    throw '.NET 8 SDK is required; no 8.0 SDK was found.'
}

$targetList = & rustup target list --installed
if ($targetList -notcontains 'x86_64-pc-windows-msvc') {
    throw 'Rust target x86_64-pc-windows-msvc is required.'
}

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer vswhere.exe is required.'
}

$buildTools = (& $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    Microsoft.VisualStudio.Component.Windows11SDK.26100 `
    -property installationPath).Trim()
if (-not $buildTools) {
    throw 'Visual Studio 2022 Build Tools with MSVC and Windows SDK 26100 is required.'
}

$msbuild = Join-Path $buildTools 'MSBuild\Current\Bin\MSBuild.exe'
$sdkResourceCompiler = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\rc.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild was not found at $msbuild"
}
if (-not (Test-Path -LiteralPath $sdkResourceCompiler)) {
    throw "Windows SDK 26100 resource compiler was not found at $sdkResourceCompiler"
}

$perl = [Environment]::GetEnvironmentVariable('OPENSSL_SRC_PERL', 'User')
if (-not $perl -or -not (Test-Path -LiteralPath $perl)) {
    throw 'OPENSSL_SRC_PERL must point to a complete Perl installation.'
}

Write-Host "Rust: $rustVersion"
Write-Host "Visual Studio Build Tools: $buildTools"
Write-Host "OpenSSL Perl: $perl"
Write-Host 'Toolchain verification passed.'
