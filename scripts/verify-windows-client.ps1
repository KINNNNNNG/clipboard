param(
    [switch]$SkipGuiSmoke
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot

try {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-core.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Clipboard core verification failed.' }

    & cargo test -p clipboard-core --test file_bundle_workflow
    if ($LASTEXITCODE -ne 0) { throw 'File bundle workflow verification failed.' }

    & cargo test -p clipboard-core --test file_cache_workflow
    if ($LASTEXITCODE -ne 0) { throw 'Favorite file cache workflow verification failed.' }

    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-windows-client-toolchain.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Windows client toolchain verification failed.' }

    & dotnet restore 'src\Clipboard.Windows\Clipboard.Windows.csproj'
    if ($LASTEXITCODE -ne 0) { throw 'Windows client restore failed.' }

    & dotnet test 'tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj' `
        -c Debug `
        -p:Platform=x64 `
        -p:WindowsAppSDKSelfContained=true `
        --no-restore `
        --filter 'ClipboardCoreClientTests|SourceApplicationResolverTests|ClipboardCaptureCoordinatorTests|ClipboardDisplayFormatterTests|ClipboardPanelViewModelTests|XamlResourceConfigurationTests|PasteCoordinatorTests'
    if ($LASTEXITCODE -ne 0) { throw 'Task 4 Windows targeted tests failed.' }

    & dotnet test 'tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj' `
        -c Debug `
        -p:Platform=x64 `
        -p:WindowsAppSDKSelfContained=true `
        --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Windows client tests failed.' }

    & dotnet build 'src\Clipboard.Windows\Clipboard.Windows.csproj' `
        -c Debug `
        -p:Platform=x64 `
        -p:WindowsAppSDKSelfContained=true `
        -p:WindowsAppSdkBootstrapInitialize=false `
        --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Windows client build failed.' }

    if (-not $SkipGuiSmoke) {
        $executable = Join-Path $repositoryRoot `
            'src\Clipboard.Windows\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\Clipboard.Windows.exe'
        if (-not (Test-Path -LiteralPath $executable)) {
            throw "Windows client executable was not found at $executable"
        }

        $process = Start-Process -FilePath $executable -ArgumentList '--show' -PassThru
        try {
            Start-Sleep -Seconds 10
            $process.Refresh()
            if ($process.HasExited) {
                throw "Windows client GUI smoke process exited with code $($process.ExitCode)."
            }
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit()
            }
        }
    }

    Write-Host 'Clipboard Windows client verification passed.'
}
finally {
    Pop-Location
}
