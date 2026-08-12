# 实时同步 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在可同步剪贴板数据变更后，使用三秒合并窗口和十五秒最小自动同步间隔执行远程同步。

**Architecture:** 新建无 UI 依赖的 `RealtimeSyncCoordinator`，从保存的同步设置和凭据构建远端请求，串行调用核心同步并记录脱敏日志。共享 `RemoteSyncRequestFactory` 给设置页和调度器，应用以组合观察者接收捕获通知。

**Tech Stack:** .NET 8、WinUI 3、xUnit、`ClipboardCoreClient`、`ClientSettingsStore`、`SyncCredentialStore`、`FileGlobalLog`。

---

## 文件结构

- Create: `src/Clipboard.Windows/Platform/RemoteSyncRequestFactory.cs`，负责生成 WebDAV/OSS `RemoteConfigDto`。
- Create: `src/Clipboard.Windows/Platform/RealtimeSyncCoordinator.cs`，负责调度、同步和日志。
- Modify: `src/Clipboard.Windows/Platform/ClipboardCaptureCoordinator.cs`，增加组合观察者。
- Modify: `src/Clipboard.Windows/ViewModels/SettingsViewModel.cs`，使用请求工厂。
- Modify: `src/Clipboard.Windows/App.xaml.cs`，创建、接入和释放协调器。
- Create: `tests/Clipboard.Windows.Tests/Platform/RealtimeSyncCoordinatorTests.cs`，验证调度语义。

### Task 1: 共享远端请求构建

**Files:** Create `src/Clipboard.Windows/Platform/RemoteSyncRequestFactory.cs`; Modify `src/Clipboard.Windows/ViewModels/SettingsViewModel.cs:620-662`; Test `tests/Clipboard.Windows.Tests/ViewModels/SettingsViewModelTests.cs`.

- [ ] **Step 1: 写失败测试。**

```csharp
[Fact]
public async Task Remote_request_factory_builds_webdav_endpoint_from_saved_credentials()
{
    RemoteConfigDto remote = await RemoteSyncRequestFactory.CreateAsync(WebDavSettings(), new FakeCredentials("alice", "secret"));
    Assert.Equal("https://sync.example.test/folder/", remote.Endpoint);
    Assert.Equal("alice", remote.Username);
}
```

- [ ] **Step 2: 运行红灯。** Run `dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --filter FullyQualifiedName~Remote_request_factory_builds_webdav_endpoint_from_saved_credentials --no-restore`，预期缺少 `RemoteSyncRequestFactory`。

- [ ] **Step 3: 实现最小工厂。**

```csharp
internal static class RemoteSyncRequestFactory
{
    public static async Task<RemoteConfigDto> CreateAsync(SyncSettings sync, ISyncCredentialStore credentials, CancellationToken token = default)
    {
        sync.Validate();
        SyncCredentials saved = await credentials.LoadAsync(sync.CredentialProfileId!, token) ?? throw new InvalidOperationException();
        return sync.Provider == "oss" ? CreateOss(sync, saved) : CreateWebDav(sync, saved);
    }

    private static RemoteConfigDto CreateOss(SyncSettings sync, SyncCredentials saved) =>
        new("oss", 1, sync.Endpoint, Region: sync.Region, Bucket: sync.Bucket, Prefix: sync.Prefix,
            AccessKeyId: saved.Account, AccessKeySecret: saved.Secret);

    private static RemoteConfigDto CreateWebDav(SyncSettings sync, SyncCredentials saved)
    {
        Uri endpoint = new(sync.Endpoint.TrimEnd('/') + "/");
        Uri remote = new(endpoint, sync.RootPath!.TrimStart('/'));
        if (remote.Scheme != endpoint.Scheme || remote.Host != endpoint.Host || remote.Port != endpoint.Port)
            throw new InvalidOperationException();
        return new("webdav", 1, remote.ToString(), Username: saved.Account, Password: saved.Secret);
    }
}
```

`CreateWebDav` 必须保留现有的同源 URL 检查；`CreateOss` 仅写入现有 `Region`、`Bucket`、`Prefix` 与保存的密钥。

- [ ] **Step 4: 将 `SettingsViewModel.BuildRemoteAsync` 委托给工厂并运行** `dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --filter "SettingsViewModelTests|Remote_request_factory" --no-restore`；预期通过现有 WebDAV/OSS 序列化测试。

- [ ] **Step 5: 提交。** Run `git add src/Clipboard.Windows/Platform/RemoteSyncRequestFactory.cs src/Clipboard.Windows/ViewModels/SettingsViewModel.cs tests/Clipboard.Windows.Tests/ViewModels/SettingsViewModelTests.cs` then `git commit -m "refactor: share remote sync request construction"`.

### Task 2: 调度器合并、限频和补跑

**Files:** Create `src/Clipboard.Windows/Platform/RealtimeSyncCoordinator.cs`; Create `tests/Clipboard.Windows.Tests/Platform/RealtimeSyncCoordinatorTests.cs`.

- [ ] **Step 1: 写三秒合并的失败测试。**

```csharp
[Fact]
public async Task Notifications_inside_debounce_window_run_one_sync()
{
    var delay = new ControlledDelay();
    var core = new FakeRemoteSyncClient();
    await using var coordinator = CreateCoordinator(core, delay);
    coordinator.NotifyCaptured("text");
    coordinator.NotifyCaptured("image");
    await delay.ReleaseNextAsync();
    await core.WaitForCallCountAsync(1);
    Assert.Equal(1, core.CallCount);
}
```

- [ ] **Step 2: 运行红灯。** Run `dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --filter FullyQualifiedName~Notifications_inside_debounce_window_run_one_sync --no-restore`，预期缺少 `RealtimeSyncCoordinator`。

- [ ] **Step 3: 实现最小接口和调度器。**

```csharp
internal interface IRealtimeSyncClient { Task<SyncResponseDto> SyncAsync(SyncRemoteRequestDto request, CancellationToken token); }
internal interface IRealtimeSyncDelay { Task DelayAsync(TimeSpan delay, CancellationToken token); }
internal interface IRealtimeSyncSettingsNotifier { void OnSyncSettingsSaved(bool enabled); }
internal sealed class RealtimeSyncCoordinator : IAsyncDisposable
{
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);
    public void NotifyCaptured(string kind) { if (kind is "text" or "image") ScheduleDebouncedRun(); }
}
```

`ScheduleDebouncedRun` 用锁保护 `_pending`、`_running` 和可取消延迟令牌；每个新通知取消旧延迟。延迟结束后用一个 `SemaphoreSlim` 串行进入同步。

- [ ] **Step 4: 写最小间隔和运行中补跑的失败测试。**

```csharp
[Fact]
public async Task Notification_during_running_sync_runs_once_after_minimum_interval()
{
    var delay = new ControlledDelay(); var core = new BlockingRemoteSyncClient();
    await using var coordinator = CreateCoordinator(core, delay);
    coordinator.NotifyCaptured("text"); await delay.ReleaseNextAsync(); await core.WaitForCallCountAsync(1);
    coordinator.NotifyCaptured("text"); core.ReleaseCurrentCall(); await delay.ReleaseNextAsync();
    await core.WaitForCallCountAsync(2);
}
```

- [ ] **Step 5: 实现限频与补跑。** 每次自动网络调用开始时记录 `_lastAutomaticRunUtc`。距该时间不足十五秒时保留待同步状态并等待剩余时间。运行中收到通知只置 `_pending = true`；`finally` 中在不再施加三秒延迟的情况下补跑或等待剩余间隔。通过 `TimeProvider` 读取当前时间。

- [ ] **Step 6: 运行** `dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --filter FullyQualifiedName~RealtimeSyncCoordinatorTests --no-restore`，预期合并、串行、限频和补跑通过；然后提交 `git commit -m "feat: schedule realtime remote sync"`。

### Task 3: 禁用、日志和应用接入

**Files:** Modify `src/Clipboard.Windows/Platform/ClipboardCaptureCoordinator.cs:12-18`, `src/Clipboard.Windows/App.xaml.cs:20-120`, `src/Clipboard.Windows/Platform/RealtimeSyncCoordinator.cs`; Test `tests/Clipboard.Windows.Tests/Platform/ClipboardCaptureCoordinatorTests.cs` and `tests/Clipboard.Windows.Tests/Platform/RealtimeSyncCoordinatorTests.cs`.

- [ ] **Step 1: 写禁用与文件包的失败测试。**

```csharp
[Fact]
public async Task Disabled_sync_and_file_bundle_notifications_do_not_call_remote()
{
    var core = new FakeRemoteSyncClient();
    await using var coordinator = CreateCoordinator(core, new ControlledDelay(), ClientSettings.Default);
    coordinator.NotifyCaptured("file_bundle"); coordinator.NotifyCaptured("text"); await Task.Yield();
    Assert.Equal(0, core.CallCount);
}
```

- [ ] **Step 2: 实现组合观察者与禁用检查。**

```csharp
internal sealed class CompositeCaptureObserver(params IClipboardCaptureObserver[] observers) : IClipboardCaptureObserver
{
    public void OnCaptured(CaptureNotification value) { foreach (var observer in observers) observer.OnCaptured(value); }
    public void OnFailure(CaptureFailure value) { foreach (var observer in observers) observer.OnFailure(value); }
}
```

协调器实现 `IClipboardCaptureObserver`，且只处理 `text`、`image`。每次真正网络调用前读取设置；`Sync` 为 null 或未启用时清除待处理状态并返回。

- [ ] **Step 3: 写禁用即时取消的失败测试并实现设置通知。**

```csharp
[Fact]
public async Task Disabling_sync_cancels_a_pending_debounce()
{
    var delay = new ControlledDelay(); var core = new FakeRemoteSyncClient();
    await using var coordinator = CreateCoordinator(core, delay, EnabledSettings());
    coordinator.NotifyCaptured("text"); coordinator.OnSyncSettingsSaved(false);
    await delay.ReleaseNextAsync(); await Task.Yield();
    Assert.Equal(0, core.CallCount);
}
```

`RealtimeSyncCoordinator.OnSyncSettingsSaved(false)` 取消当前延迟令牌并清除 `_pending`。给 `SettingsViewModel` 增加可选 `IRealtimeSyncSettingsNotifier` 构造函数参数；只有 `SaveSyncAsync` 成功后才调用 `OnSyncSettingsSaved(sync?.Enabled == true)`。应用将协调器传给设置视图模型，既有测试构造方式保持兼容。

- [ ] **Step 4: 写失败日志与后续重试测试。**

```csharp
[Fact]
public async Task Failed_sync_logs_fixed_fields_and_later_notification_retries()
{
    var core = new FakeRemoteSyncClient { NextResponse = new SyncResponseDto(0, 0, 0, "remote", null, "http_403", "webdav_put_pending") };
    var log = new FakeGlobalLog(); await using var coordinator = CreateCoordinator(core, new ControlledDelay(), log: log);
    await TriggerOneRunAsync(coordinator); core.NextResponse = new SyncResponseDto(0, 0, 1, null, null, null, null);
    await TriggerOneRunAsync(coordinator); Assert.Equal(2, core.CallCount);
}
```

- [ ] **Step 5: 实现日志和错误隔离。** 每轮写 `sync.realtime.start`、`sync.realtime.end`，字段仅使用 `provider`、`status`、`count`、`error_category`、`error_code`、`error_detail`、`error_operation`。异常与远端失败均在本轮结束，`finally` 始终恢复可调度状态。

- [ ] **Step 6: 接入应用。**

```csharp
_realtimeSync = new RealtimeSyncCoordinator(_core, _settingsStore, new SyncCredentialStore(), _globalLog);
_capture = new ClipboardCaptureCoordinator(reader, _core, new SourceApplicationResolver(), suppression,
    new CompositeCaptureObserver(MainWindow, _realtimeSync), retentionPolicy: retentionPolicy);
```

在 `OnExit` 中先等待 `_realtimeSync.DisposeAsync()`，再释放 `_core`。

- [ ] **Step 7: 运行验证和提交。** Run `dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --filter "ClipboardCaptureCoordinatorTests|RealtimeSyncCoordinatorTests" --no-restore`, then `pwsh -NoProfile -File scripts\verify-windows-client.ps1 -SkipGuiSmoke`; expected `Clipboard Windows client verification passed.` 启动最新 EXE 检查其保持运行，之后提交 `git commit -m "feat: enable realtime sync in desktop app"`。
