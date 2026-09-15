param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',
    [string]$OutputRoot = '',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\windows'
}

$publishRoot = Join-Path $OutputRoot 'publish'
$installerRoot = Join-Path $OutputRoot 'installer'
$checksumPath = Join-Path $OutputRoot 'SHA256SUMS.txt'
$projectPath = Join-Path $repositoryRoot 'src\Clipboard.Windows\Clipboard.Windows.csproj'
$installerScript = Join-Path $repositoryRoot 'installer\Clipboard.iss'
$executablePath = Join-Path $publishRoot 'Clipboard.Windows.exe'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "命令执行失败（退出码 $LASTEXITCODE）：$Command $($Arguments -join ' ')"
    }
}

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    foreach ($path in @($publishRoot, $installerRoot)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    if (Test-Path -LiteralPath $checksumPath) {
        Remove-Item -LiteralPath $checksumPath -Force
    }

    Invoke-CheckedCommand -Command 'cargo' -Arguments @('build', '-p', 'clipboard-ffi', '--release')
    Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $publishRoot,
        '-p:Platform=x64',
        '-p:WindowsAppSDKSelfContained=true',
        '-p:WindowsAppSdkBootstrapInitialize=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version"
    )

    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "发布目录缺少客户端程序：$executablePath"
    }

    $forbiddenExtensions = @('.pfx', '.p12', '.pem', '.key', '.snk')
    $forbidden = Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in $forbiddenExtensions }
    if ($forbidden) {
        throw "发布目录包含敏感签名文件：$($forbidden.FullName -join ', ')"
    }

    $installerPath = $null
    if (-not $SkipInstaller) {
        if (-not (Test-Path -LiteralPath $installerScript)) {
            throw "安装器脚本不存在：$installerScript"
        }
        $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
        if ($null -eq $iscc) {
            $knownIscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
            if (Test-Path -LiteralPath $knownIscc) {
                $iscc = Get-Item -LiteralPath $knownIscc
            }
        }
        if ($null -eq $iscc) {
            throw '未找到 Inno Setup 编译器。请安装 Inno Setup 6，或使用 -SkipInstaller 只生成自包含发布目录。'
        }

        $isccPath = if ($iscc.PSObject.Properties.Name -contains 'Source') { $iscc.Source } else { $iscc.FullName }
        Invoke-CheckedCommand -Command $isccPath -Arguments @(
            "/DAppVersion=$Version",
            "/DSourceDir=$publishRoot",
            "/DOutputDir=$installerRoot",
            $installerScript
        )

        $installerPath = Join-Path $installerRoot "Clipboard-Setup-v$Version.exe"
        if (-not (Test-Path -LiteralPath $installerPath)) {
            throw "安装器编译完成但未找到产物：$installerPath"
        }
    }

    $checksumTarget = if ($null -ne $installerPath) { $installerPath } else { $executablePath }
    $checksum = (Get-FileHash -LiteralPath $checksumTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $([System.IO.Path]::GetFileName($checksumTarget))" | Set-Content -LiteralPath $checksumPath -Encoding ascii

    Write-Host "Windows 发布完成：$OutputRoot"
    if ($null -ne $installerPath) {
        Write-Host "安装器：$installerPath"
    }
    Write-Host "校验文件：$checksumPath"
}
finally {
    Pop-Location
}
