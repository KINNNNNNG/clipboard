$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot

try {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-toolchain.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Toolchain verification failed.' }

    & cargo fmt --all --check
    if ($LASTEXITCODE -ne 0) { throw 'cargo fmt failed.' }

    & cargo clippy --workspace --all-targets -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw 'cargo clippy failed.' }

    & cargo test --workspace --all-targets
    if ($LASTEXITCODE -ne 0) { throw 'cargo test failed.' }

    & cargo test -p clipboard-sync --test recovery_code --test diagnostics --test protocol --test directory_transport
    if ($LASTEXITCODE -ne 0) { throw 'Clipboard sync protocol verification failed.' }

    & cargo test -p clipboard-core --test sync_foundation
    if ($LASTEXITCODE -ne 0) { throw 'Clipboard core sync verification failed.' }

    & cargo build -p clipboard-ffi
    if ($LASTEXITCODE -ne 0) { throw 'clipboard-ffi build failed.' }

    & dotnet run --project (Join-Path $repositoryRoot 'src\Clipboard.FfiSmoke\Clipboard.FfiSmoke.csproj')
    if ($LASTEXITCODE -ne 0) { throw '.NET FFI smoke test failed.' }

    Write-Host 'Clipboard core verification passed.'
}
finally {
    Pop-Location
}
