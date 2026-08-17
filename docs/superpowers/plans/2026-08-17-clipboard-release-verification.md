# Windows 发布验证基线实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 让 Windows 发布验证能释放旧实例文件锁、验证新实例替换旧实例，并检查测试、构建与敏感产物边界。

**架构：** `scripts/verify-windows-client.ps1` 保持为唯一验证入口。脚本只处理与正式 Debug x64 exe 完全同路径的客户端进程，构建前清理旧实例，GUI smoke 通过连续启动两个实例验证运行时的 `PreviousInstanceCloser`，并在 `finally` 中回收所有本轮启动的进程。C# 静态契约测试读取复制到输出目录的脚本，防止发布门被后续编辑移除。

**技术栈：** PowerShell 7、.NET 8、xUnit、WinUI 3、Cargo、Windows App Runtime 1.8。

---

## 文件结构

```text
scripts/verify-windows-client.ps1
    阶段五唯一验证入口；旧实例清理、产物检查、双实例 GUI smoke。
tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj
    把验证脚本作为 Fixture 复制到测试输出目录。
tests/Clipboard.Windows.Tests/Platform/VerificationScriptTests.cs
    锁定验证脚本必须保留的安全与单实例门。
README.md
    说明完整阶段五验证命令与 -SkipGuiSmoke 的用途。
```

### Task 1：锁定验证脚本契约

**文件：**
- Modify: `tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj:31-46`
- Create: `tests/Clipboard.Windows.Tests/Platform/VerificationScriptTests.cs`

- [x] **Step 1：添加验证脚本 Fixture 与失败测试。**

在测试项目的现有 `<ItemGroup>` 中添加：

```xml
<Content Include="..\..\scripts\verify-windows-client.ps1"
         Link="Fixtures\verify-windows-client.ps1"
         CopyToOutputDirectory="PreserveNewest" />
```

创建测试文件，读取 Fixture 后锁定以下发布门：

```csharp
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class VerificationScriptTests
{
    [Fact]
    public void Windows_release_verification_keeps_instance_and_artifact_gates()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "verify-windows-client.ps1");
        string script = File.ReadAllText(path);

        Assert.Contains("function Get-ClientProcesses", script);
        Assert.Contains("function Stop-ExistingClientInstances", script);
        Assert.Contains("function Assert-ReleaseArtifact", script);
        Assert.Contains("PreviousInstanceCloserTests", script);
        Assert.Contains("GlobalLogTests", script);
        Assert.Contains("$firstProcess", script);
        Assert.Contains("$secondProcess", script);
        Assert.DoesNotContain("Stop-Process -Name", script);
    }
}
```

- [x] **Step 2：运行红灯测试。**

运行：

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~VerificationScriptTests"
```

预期：失败，提示尚未包含 `Get-ClientProcesses`，证明现有脚本未实现阶段五发布门。

- [x] **Step 3：提交测试基线。**

```powershell
git add tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj tests/Clipboard.Windows.Tests/Platform/VerificationScriptTests.cs
git commit -m "test: 锁定发布验证脚本门"
```

### Task 2：实现同路径进程清理与产物检查

**文件：**
- Modify: `scripts/verify-windows-client.ps1:1-55`

- [x] **Step 1：在脚本顶部定义受限进程与产物帮助函数。**

在 `$ErrorActionPreference = 'Stop'` 后加入以下函数；`Get-ClientProcesses` 只返回 `.Path` 等于 `$executable` 的进程，绝不按名称批量结束：

```powershell
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
```

- [x] **Step 2：在 Core 验证前计算 exe 并清理同路径旧实例。**

在 `Push-Location $repositoryRoot` 后、任何会写入 `bin` 的命令前添加：

```powershell
$executable = Join-Path $repositoryRoot `
    'src\Clipboard.Windows\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\Clipboard.Windows.exe'
Stop-ExistingClientInstances -Executable $executable
```

保留现有 Core、文件工作流、工具链、restore、全量测试和构建命令。将 Windows 定向测试过滤器扩展为：

```powershell
--filter 'ClipboardCoreClientTests|SettingsViewModelTests|SyncCredentialStoreTests|SourceApplicationResolverTests|ClipboardCaptureCoordinatorTests|ClipboardDisplayFormatterTests|ClipboardPanelViewModelTests|XamlResourceConfigurationTests|PasteCoordinatorTests|GlobalLogTests|PreviousInstanceCloserTests|VerificationScriptTests'
```

- [x] **Step 3：在构建成功后检查正式产物。**

紧随现有 `dotnet build` 成功判断后添加：

```powershell
Assert-ReleaseArtifact -Executable $executable
```

- [x] **Step 4：运行绿灯测试。**

运行：

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~VerificationScriptTests"
```

预期：通过，且脚本包含受限清理、产物检查、日志脱敏和旧实例定向测试门。

- [x] **Step 5：提交脚本门。**

```powershell
git add scripts/verify-windows-client.ps1 tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj tests/Clipboard.Windows.Tests/Platform/VerificationScriptTests.cs
git commit -m "build: 加固 Windows 发布验证门"
```

### Task 3：验证双实例 GUI 启动与优雅回收

**文件：**
- Modify: `scripts/verify-windows-client.ps1:after Assert-ReleaseArtifact`

- [x] **Step 1：替换单实例 GUI smoke。**

将现有单个 `$process` 的 `if (-not $SkipGuiSmoke)` 块替换为：

```powershell
if (-not $SkipGuiSmoke) {
    $firstProcess = $null
    $secondProcess = $null
    try {
        $workingDirectory = Split-Path -Parent $executable
        $firstProcess = Start-Process -FilePath $executable -ArgumentList '--show' `
            -WorkingDirectory $workingDirectory -PassThru
        Start-Sleep -Seconds 5
        $firstProcess.Refresh()
        if ($firstProcess.HasExited) {
            throw "First GUI smoke process exited with code $($firstProcess.ExitCode)."
        }

        $secondProcess = Start-Process -FilePath $executable -ArgumentList '--show' `
            -WorkingDirectory $workingDirectory -PassThru
        Start-Sleep -Seconds 5
        $firstProcess.Refresh()
        $secondProcess.Refresh()
        if (-not $firstProcess.HasExited) {
            throw 'The second client instance did not close the first instance.'
        }
        if ($secondProcess.HasExited) {
            throw "Second GUI smoke process exited with code $($secondProcess.ExitCode)."
        }

        $active = Get-ClientProcesses -Executable $executable
        if ($active.Count -ne 1 -or $active[0].Id -ne $secondProcess.Id) {
            throw 'GUI smoke expected exactly one active client instance.'
        }
    }
    finally {
        foreach ($process in @($firstProcess, $secondProcess)) {
            if ($null -ne $process) {
                Stop-ClientProcess -Process $process
            }
        }
    }
}
```

- [x] **Step 2：运行不含 GUI 的完整验证。**

先确保关闭手动启动的客户端，然后运行：

```powershell
pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke
```

预期：所有 Core、同步、FFI、定向 Windows、全量 Windows 与构建检查通过；运行中的同路径旧客户端不会导致构建锁错误。

- [x] **Step 3：运行完整 GUI 验证。**

```powershell
pwsh -NoProfile -File scripts/verify-windows-client.ps1
```

预期：第一个客户端启动后保持运行，第二个客户端自动关闭第一个，之后第二个被脚本优雅回收；脚本以 0 退出。

- [x] **Step 4：提交 GUI 验收逻辑。**

```powershell
git add scripts/verify-windows-client.ps1
git commit -m "test: 验证客户端二次启动替换旧实例"
```

### Task 4：更新验收文档并完成阶段五基线

**文件：**
- Modify: `README.md:31-44`
- Modify: `docs/superpowers/plans/2026-08-17-clipboard-release-verification.md`

- [x] **Step 1：更新 README 验证说明。**

将验证段落补充为：

```markdown
发布前运行：

```powershell
pwsh -NoProfile -File scripts/verify-windows-client.ps1
```

该命令会关闭同路径旧客户端、执行 Core 与 Windows 全量验证、检查 Debug x64 产物不含证书或私钥，并连续启动两个客户端验证第二个实例会关闭第一个。CI 使用 `-SkipGuiSmoke` 跳过桌面交互 smoke，其余检查保持执行。
```

- [x] **Step 2：勾选已执行计划步骤并记录验证结果。**

仅在每条对应命令实际成功后，将本计划中的步骤标记为 `- [x]`；在文档末尾追加实际命令、日期、通过的测试数量和 GUI smoke 进程替换结果。不要记录剪贴板正文、文件路径、远端地址、凭据或密钥。

- [x] **Step 3：最终检查并提交。**

```powershell
git diff --check
git status --short
git add README.md docs/superpowers/plans/2026-08-17-clipboard-release-verification.md
git commit -m "docs: 记录阶段五发布验证基线"
```

预期：只有计划定义的验证脚本、测试 Fixture、测试、README 与计划文档发生变化；既有同步、剪贴板和 UI 功能改动不被回退或混入。

## 实际验收记录

- 日期：2026-08-17。
- `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~VerificationScriptTests"`：1/1 通过。
- `pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`：退出码 0；Windows 定向测试 126/126、全量测试 196/196；Debug x64 构建 0 警告、0 错误；敏感产物扫描通过。
- `pwsh -NoProfile -File scripts/verify-windows-client.ps1`：退出码 0；同样通过 Core、同步、FFI、Windows 测试和构建；GUI smoke 确认第二个实例关闭第一个，脚本回收后同路径活动进程数为 0。

## 计划自审

- 覆盖设计中的同路径旧实例清理、产物检查、定向测试、双实例 smoke、优雅回收和 README 说明。
- 所有生产行为均由 Task 1 的失败测试先锁定；集成脚本由 Task 3 的无 GUI和 GUI 两条命令验证。
- 文件名、函数名、过滤器和命令在各任务中保持一致。
- 不包含性能、DPI、MSIX、真实远端验收或跨设备文件同步任务。
