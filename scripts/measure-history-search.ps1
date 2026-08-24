$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exitCode = 0
$locationPushed = $false
$cleanupFailed = $false

try {
    Push-Location $repositoryRoot
    $locationPushed = $true

    & cargo test -p clipboard-core --release --test history_search_performance -- --ignored --test-threads=1 --nocapture
    $exitCode = $LASTEXITCODE
}
catch {
    if ($exitCode -eq 0) {
        $exitCode = 1
    }

    [Console]::Error.WriteLine('History search performance gate could not start.')
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
}

if ($cleanupFailed) {
    [Console]::Error.WriteLine('History search performance cleanup failed.')
    if ($exitCode -eq 0) {
        $exitCode = 1
    }
}

if ($exitCode -ne 0) {
    exit $exitCode
}
