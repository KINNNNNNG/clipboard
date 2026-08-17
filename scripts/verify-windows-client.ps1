param(
    [switch]$SkipGuiSmoke
)

$ErrorActionPreference = 'Stop'

function Get-ClientProcesses {
    param([Parameter(Mandatory)][string]$Executable)

    return @(Get-Process -Name 'Clipboard.Windows' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $Executable })
}

function Stop-ClientProcess {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)

    if ($Process.HasExited) { return }
    [void]$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(2000)) {
        $Process.Kill()
        $Process.WaitForExit()
    }
}

function Stop-ExistingClientInstances {
    param([Parameter(Mandatory)][string]$Executable)

    foreach ($process in Get-ClientProcesses -Executable $Executable) {
        Stop-ClientProcess -Process $process
    }
}

function Assert-ReleaseArtifact {
    param([Parameter(Mandatory)][string]$Executable)

    if (-not (Test-Path -LiteralPath $Executable)) {
        throw "Windows client executable was not found at $Executable"
    }

    $forbiddenExtensions = @('.pfx', '.p12', '.pem', '.key', '.snk')
    $forbidden = Get-ChildItem -LiteralPath (Split-Path -Parent $Executable) -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in $forbiddenExtensions }
    if ($forbidden) {
        throw "Sensitive signing material was found in the client output: $($forbidden.FullName -join ', ')"
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot

try {
    $executable = Join-Path $repositoryRoot 'src\Clipboard.Windows\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\Clipboard.Windows.exe'
    Stop-ExistingClientInstances -Executable $executable

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
        --filter 'ClipboardCoreClientTests|SettingsViewModelTests|SyncCredentialStoreTests|SourceApplicationResolverTests|ClipboardCaptureCoordinatorTests|ClipboardDisplayFormatterTests|ClipboardPanelViewModelTests|XamlResourceConfigurationTests|PasteCoordinatorTests|GlobalLogTests|PreviousInstanceCloserTests|VerificationScriptTests'
    if ($LASTEXITCODE -ne 0) { throw 'Windows targeted tests failed.' }

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

    Assert-ReleaseArtifact -Executable $executable

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
