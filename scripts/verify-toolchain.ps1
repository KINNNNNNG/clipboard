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

function Find-VisualStudioRoot {
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -prerelease `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath
        $resolved = if ($found) { $found.Trim() } else { '' }
        if ($resolved) {
            return $resolved
        }
    }

    # The installer registry state is not always readable, so fall back to the standard install
    # roots and require the MSVC toolset itself rather than a specific detection mechanism.
    $candidates = @()
    foreach ($root in @(
        'C:\Program Files\Microsoft Visual Studio',
        'C:\Program Files (x86)\Microsoft Visual Studio')) {
        foreach ($product in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            foreach ($edition in Get-ChildItem -LiteralPath $product.FullName -Directory -ErrorAction SilentlyContinue) {
                $msbuild = Join-Path $edition.FullName 'MSBuild\Current\Bin\MSBuild.exe'
                if (-not (Test-Path -LiteralPath $msbuild)) {
                    continue
                }
                $toolset = Get-ChildItem -LiteralPath (Join-Path $edition.FullName 'VC\Tools\MSVC') `
                    -Directory -ErrorAction SilentlyContinue |
                    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe') } |
                    Sort-Object Name -Descending |
                    Select-Object -First 1
                if ($toolset) {
                    $candidates += [pscustomobject]@{
                        Path = $edition.FullName
                        Toolset = $toolset.Name
                    }
                }
            }
        }
    }

    # Prefer the newest MSVC toolset when several Visual Studio versions are installed.
    if ($candidates) {
        return ($candidates | Sort-Object Toolset -Descending | Select-Object -First 1).Path
    }
    return ''
}

$visualStudioPath = Find-VisualStudioRoot
if (-not $visualStudioPath) {
    throw 'A Visual Studio installation with MSVC and Windows SDK 26100 is required.'
}

$msbuild = Join-Path $visualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'
$sdkInclude = 'C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0'
$sdkResourceCompiler = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\rc.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild was not found at $msbuild"
}
if (-not (Test-Path -LiteralPath $sdkInclude)) {
    throw "Windows SDK 26100 headers were not found at $sdkInclude"
}
if (-not (Test-Path -LiteralPath $sdkResourceCompiler)) {
    throw "Windows SDK 26100 resource compiler was not found at $sdkResourceCompiler"
}

$perl = [Environment]::GetEnvironmentVariable('OPENSSL_SRC_PERL', 'Process')
if (-not $perl) {
    $perl = [Environment]::GetEnvironmentVariable('OPENSSL_SRC_PERL', 'User')
}
if (-not $perl -or -not (Test-Path -LiteralPath $perl)) {
    throw 'OPENSSL_SRC_PERL must point to a complete Perl installation.'
}

Write-Host "Rust: $rustVersion"
Write-Host "Visual Studio: $visualStudioPath"
Write-Host "OpenSSL Perl: $perl"
Write-Host 'Toolchain verification passed.'
