$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dataDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    'clipboard-history-performance-' + [Guid]::NewGuid().ToString('N'))
$exitCode = 0
$locationPushed = $false
$cleanupFailed = $false

try {
    Push-Location $repositoryRoot
    $locationPushed = $true

    [void](New-Item -ItemType Directory -Path $dataDirectory -Force)

    & cargo build -p clipboard-ffi --release
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-windows-client-toolchain.ps1') -Configuration Release
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        & cargo build -p clipboard-core --release --bin prepare_history_performance
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        $fixtureBinary = Join-Path $repositoryRoot 'target\release\prepare_history_performance.exe'
        & $fixtureBinary --data-dir $dataDirectory | Out-Null
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        & dotnet restore 'tests\Clipboard.Windows.Performance\Clipboard.Windows.Performance.csproj'
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0) {
        & dotnet run --project 'tests\Clipboard.Windows.Performance\Clipboard.Windows.Performance.csproj' -c Release --no-restore -- --data-dir $dataDirectory --vault-id 00000000-0000-0000-0000-000000005a17 --configuration Release
        $exitCode = $LASTEXITCODE
    }
}
catch {
    if ($exitCode -eq 0) {
        $exitCode = 1
    }

    [Console]::Error.WriteLine('Windows history diagnostics could not start.')
}
finally {
    if ($locationPushed) {
        try {
            Pop-Location
        }
        catch {
            $cleanupFailed = $true
        }
    }

    try {
        if (Test-Path -LiteralPath $dataDirectory) {
            Remove-Item -LiteralPath $dataDirectory -Recurse -Force
        }
    }
    catch {
        $cleanupFailed = $true
    }
}

if ($cleanupFailed) {
    [Console]::Error.WriteLine('Windows history diagnostics cleanup failed.')
    if ($exitCode -eq 0) {
        $exitCode = 1
    }
}

if ($exitCode -ne 0) {
    exit $exitCode
}
