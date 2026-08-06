# Windows 系统文件类型图标实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让文件历史卡片按 Windows 文件关联显示真实系统文件类型图标，同时保持面板立即出现、列表虚拟化安全、文件路径不出现在搜索和同步边界内。

**Architecture:** Windows 客户端根据代表文件名生成稳定的图标缓存键；平台层用 `SHGetFileInfo(SHGFI_USEFILEATTRIBUTES)` 对虚拟文件名取得 `HICON`，在后台转换成 BGRA 像素；视图层只为已创建的可见卡片异步创建 `SoftwareBitmapSource`，用 128 项 LRU 和按键合并的异步请求复用结果。加载失败时由现有 Fluent glyph 继续显示，且通过卡片 ID 与缓存键双重校验避免列表回收造成错位。

**Tech Stack:** .NET 8, WinUI 3 / Windows App SDK 1.8, x64 Windows 11, Win32 Shell/GDI P/Invoke, xUnit。

---

## 文件边界

本计划只修改 Windows 客户端和 Windows 测试项目，不修改 Rust Core、数据库迁移、C ABI、同步队列或文件路径 DTO。

- Modify: `src/Clipboard.Windows/ViewModels/ClipboardItemViewModel.cs`，增加扩展名归一化和图标缓存键。
- Modify: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardDisplayFormatterTests.cs`，覆盖文件名规则。
- Create: `src/Clipboard.Windows/Views/FileIconCache.cs`，提供 UI 线程使用的 128 项 LRU、失败 TTL 和按键合并。
- Create: `tests/Clipboard.Windows.Tests/Views/FileIconCacheTests.cs`，覆盖缓存命中、并发、淘汰和失败重试。
- Create: `src/Clipboard.Windows/Platform/FileTypeIconProvider.cs`，定义像素记录、提供器接口和 `SHGetFileInfo` 请求映射。
- Create: `src/Clipboard.Windows/Platform/ShellIconNativeApi.cs`，实现 Shell/GDI P/Invoke、32 位顶向下 DIB 转 BGRA 和原生句柄释放。
- Create: `tests/Clipboard.Windows.Tests/Platform/FileTypeIconProviderTests.cs`，使用假 Native API 验证虚拟名称、属性、失败和句柄释放。
- Create: `src/Clipboard.Windows/Views/FileIconLoadTracker.cs`，记录卡片 ID 与缓存键，阻断虚拟化旧请求覆盖新卡片。
- Create: `src/Clipboard.Windows/Views/SoftwareBitmapSourceFactory.cs`，在 UI 线程把 BGRA 像素转换成 `SoftwareBitmapSource`。
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml`，在固定 42 x 42 区域增加系统图标 Image 和 Fluent 降级 glyph。
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml.cs`，只在文件卡片可见时调度加载，并在结果返回前校验上下文。
- Modify: `tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs`，锁定固定尺寸、绑定和生命周期事件。
- Create: `tests/Clipboard.Windows.Tests/Views/FileIconLoadTrackerTests.cs`，覆盖文件图标请求的卡片回收。

## Task 1: 生成稳定的文件图标缓存键

**Files:**

- Modify: `src/Clipboard.Windows/ViewModels/ClipboardItemViewModel.cs:62-69`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardDisplayFormatterTests.cs`

- [ ] **Step 1: 写失败测试。** 在现有文件卡片测试后加入以下理论测试，确保文件名只影响缓存键，不把路径写入任何显示属性：

```csharp
[Theory]
[InlineData("file", "report.DOCX", "file:.docx")]
[InlineData("file", "archive.tar.gz", "file:.gz")]
[InlineData("file", ".gitignore", "file")]
[InlineData("file", "README", "file")]
[InlineData("file", "report.", "file")]
[InlineData("file", @"C:\\Temp\\manual.PDF", "file:.pdf")]
[InlineData("directory", "Photos", "directory")]
public void File_icon_cache_key_normalizes_only_the_file_type(
    string representativeKind,
    string representativeName,
    string expected)
{
    var item = new ClipboardItemViewModel(new ClipboardItemDto(
        Guid.NewGuid(), "file_bundle", representativeName, "explorer.exe", 100,
        false, null, null, null, null, 1, representativeName, representativeKind));

    Assert.Equal(expected, item.FileIconCacheKey);
    Assert.Equal(representativeName, item.FileNameSummary);
}
```

- [ ] **Step 2: 运行测试确认失败。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~File_icon_cache_key_normalizes_only_the_file_type`

Expected: FAIL，因为 `FileIconCacheKey` 尚不存在。

- [ ] **Step 3: 实现最小逻辑。** 在 `ClipboardItemViewModel` 增加：

```csharp
public string FileIconCacheKey => BuildFileIconCacheKey(
    Item.RepresentativeKind,
    Item.RepresentativeName);

internal static string BuildFileIconCacheKey(string? representativeKind, string? name)
{
    if (string.Equals(representativeKind, "directory", StringComparison.OrdinalIgnoreCase))
    {
        return "directory";
    }

    string leaf = string.IsNullOrWhiteSpace(name)
        ? string.Empty
        : Path.GetFileName(name.Trim());
    int dot = leaf.LastIndexOf('.');
    if (dot <= 0 || dot == leaf.Length - 1)
    {
        return "file";
    }

    return $"file:{leaf[dot..].ToLowerInvariant()}";
}
```

`Path.GetFileName` 只处理代表名称，返回值永远不会传给 Shell；Shell 层只接受本计划定义的 `file:*` 键。

- [ ] **Step 4: 运行测试确认通过。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~File_icon_cache_key_normalizes_only_the_file_type|FullyQualifiedName~File_bundle_card_uses_fluent_summary"`

Expected: PASS。

- [ ] **Step 5: 提交。**

```powershell
git add src/Clipboard.Windows/ViewModels/ClipboardItemViewModel.cs tests/Clipboard.Windows.Tests/ViewModels/ClipboardDisplayFormatterTests.cs
git commit -m "feat(windows): derive file icon cache keys"
```

## Task 2: 实现带失败 TTL 的异步 LRU 缓存

**Files:**

- Create: `src/Clipboard.Windows/Views/FileIconCache.cs`
- Test: `tests/Clipboard.Windows.Tests/Views/FileIconCacheTests.cs`

- [ ] **Step 1: 写失败测试。** 新建测试，使用手动时间提供器和计数器验证四个契约：

```csharp
[Fact]
public async Task Overlapping_requests_for_one_key_share_one_loader()
{
    var cache = new FileIconCache<string>(128, TimeProvider.System, TimeSpan.FromMinutes(5));
    var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    int calls = 0;
    Task<string?> Load()
    {
        calls++;
        return gate.Task.ContinueWith(static task => (string?)task.Result);
    }

    Task<string?> first = cache.GetAsync("file:.docx", Load);
    Task<string?> second = cache.GetAsync("file:.docx", Load);
    Assert.Same(first, second);
    gate.SetResult("icon");

    Assert.Equal("icon", await first);
    Assert.Equal(1, calls);
}

[Fact]
public async Task Failure_is_cached_for_five_minutes_then_retried()
{
    var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
    var cache = new FileIconCache<string>(128, time, TimeSpan.FromMinutes(5));
    int calls = 0;
    Task<string?> Load()
    {
        calls++;
        return Task.FromResult<string?>(null);
    }

    Assert.Null(await cache.GetAsync("file:.unknown", Load));
    Assert.Null(await cache.GetAsync("file:.unknown", Load));
    Assert.Equal(1, calls);
    time.Advance(TimeSpan.FromMinutes(5));
    Assert.Null(await cache.GetAsync("file:.unknown", Load));
    Assert.Equal(2, calls);
}

[Fact]
public async Task Capacity_evicts_the_least_recently_used_key()
{
    var cache = new FileIconCache<string>(2, TimeProvider.System, TimeSpan.FromMinutes(5));
    int calls = 0;
    Task<string?> Load(string key) => Task.FromResult<string?>($"{key}-{++calls}");

    await cache.GetAsync("file:.one", () => Load("one"));
    await cache.GetAsync("file:.two", () => Load("two"));
    await cache.GetAsync("file:.one", () => Load("one"));
    await cache.GetAsync("file:.three", () => Load("three"));
    await cache.GetAsync("file:.two", () => Load("two"));

    Assert.Equal(4, calls);
}
```

`ManualTimeProvider` 提供 `Advance`，异常测试还要确认 loader 抛出异常时返回 null 且写入失败缓存，不把异常传播到面板。

```csharp
private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now += value;
}
```

- [ ] **Step 2: 运行测试确认失败。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~FileIconCacheTests`

Expected: 编译失败，因为 `FileIconCache<T>` 尚不存在。

- [ ] **Step 3: 实现缓存。** 新建 `FileIconCache<T>`，限定只从 UI 线程调用；缓存值为 `(T? Value, DateTimeOffset RetryAfter)`，成功值使用 `DateTimeOffset.MaxValue`，失败值使用当前时间加失败 TTL。通过 `TaskCompletionSource<T?>` 先放入 `_inFlight` 再启动 loader，保证同步完成的 loader 也不会留下重复请求：

```csharp
namespace Clipboard.Windows.Views;

internal sealed class FileIconCache<T> where T : class
{
    private readonly BoundedLruCache<string, Entry> _entries;
    private readonly Dictionary<string, Task<T?>> _inFlight = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _failureTtl;

    public FileIconCache(int capacity, TimeProvider timeProvider, TimeSpan failureTtl)
    {
        _entries = new BoundedLruCache<string, Entry>(capacity);
        _timeProvider = timeProvider;
        _failureTtl = failureTtl;
    }

    public Task<T?> GetAsync(string key, Func<Task<T?>> loader)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_entries.TryGetValue(key, out Entry? cached)
            && (cached.Value is not null || now < cached.RetryAfter))
        {
            return Task.FromResult(cached.Value);
        }

        if (_inFlight.TryGetValue(key, out Task<T?>? pending))
        {
            return pending;
        }

        var completion = new TaskCompletionSource<T?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight.Add(key, completion.Task);
        _ = PopulateAsync(key, loader, completion);
        return completion.Task;
    }

    private async Task PopulateAsync(
        string key,
        Func<Task<T?>> loader,
        TaskCompletionSource<T?> completion)
    {
        T? value = null;
        try
        {
            value = await loader().ConfigureAwait(true);
        }
        catch
        {
            value = null;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        _entries.Set(key, new Entry(
            value,
            value is null ? now.Add(_failureTtl) : DateTimeOffset.MaxValue));
        _inFlight.Remove(key);
        completion.TrySetResult(value);
    }

    private sealed record Entry(T? Value, DateTimeOffset RetryAfter);
}
```

- [ ] **Step 4: 运行缓存测试。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~FileIconCacheTests`

Expected: PASS，且异常、失败结果都不向调用方抛出。

- [ ] **Step 5: 提交。**

```powershell
git add src/Clipboard.Windows/Views/FileIconCache.cs tests/Clipboard.Windows.Tests/Views/FileIconCacheTests.cs
git commit -m "feat(windows): cache file type icon loads"
```

## Task 3: 实现 Shell 图标和 GDI 像素转换

**Files:**

- Create: `src/Clipboard.Windows/Platform/FileTypeIconProvider.cs`
- Create: `src/Clipboard.Windows/Platform/ShellIconNativeApi.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/FileTypeIconProviderTests.cs`

- [ ] **Step 1: 写失败测试。** 使用假 Native API 验证提供器绝不把代表名称当真实路径传入 Shell，并在成功、空图标和像素转换异常时释放 `HICON`：

```csharp
[Fact]
public void Extension_uses_virtual_placeholder_and_normal_file_attributes()
{
    var native = new FakeShellIconNativeApi { Icon = (nint)42 };
    var provider = new ShellFileTypeIconProvider(native);

    ShellIconPixels? result = provider.Load("file:.docx");

    Assert.NotNull(result);
    Assert.Equal("placeholder.docx", native.Path);
    Assert.Equal(ShellFileTypeIconProvider.FileAttributeNormal, native.Attributes);
    Assert.Equal((nint)42, Assert.Single(native.DestroyedIcons));
}

[Fact]
public void Directory_uses_virtual_directory_attributes()
{
    var native = new FakeShellIconNativeApi { Icon = (nint)43 };
    new ShellFileTypeIconProvider(native).Load("directory");

    Assert.Equal("placeholder", native.Path);
    Assert.Equal(ShellFileTypeIconProvider.FileAttributeDirectory, native.Attributes);
}

[Fact]
public void Native_failure_and_conversion_exception_return_no_icon_and_release_handle()
{
    var native = new FakeShellIconNativeApi
    {
        Icon = (nint)44,
        ThrowOnCopy = true,
    };

    Assert.Null(new ShellFileTypeIconProvider(native).Load("file:.pdf"));
    Assert.Equal((nint)44, Assert.Single(native.DestroyedIcons));
}

[Fact]
public void Production_native_api_returns_a_32_pixel_icon_or_a_safe_null_result()
{
    ShellIconPixels? result = new ShellFileTypeIconProvider(
        new ShellIconNativeApi()).Load("file:.txt");

    Assert.True(result is null || (result.Width == 32
        && result.Height == 32
        && result.BgraPixels.Length == 32 * 32 * 4));
}
```

Fake API 的 `CopyIconPixels` 返回固定 `new ShellIconPixels(32, 32, new byte[32 * 32 * 4])`，并记录 `Path`、`Attributes` 和 `DestroyedIcons`。测试类内的 Fake API 必须完整实现以下最小接口：

```csharp
private sealed class FakeShellIconNativeApi : IShellIconNativeApi
{
    public nint Icon { get; init; }
    public bool ThrowOnCopy { get; init; }
    public string? Path { get; private set; }
    public uint Attributes { get; private set; }
    public List<nint> DestroyedIcons { get; } = [];

    public nint GetFileIcon(string virtualName, uint attributes)
    {
        Path = virtualName;
        Attributes = attributes;
        return Icon;
    }

    public ShellIconPixels CopyIconPixels(nint icon, int size)
    {
        if (ThrowOnCopy)
        {
            throw new InvalidOperationException("conversion failed");
        }
        return new ShellIconPixels(size, size, new byte[size * size * 4]);
    }

    public void DestroyIcon(nint icon) => DestroyedIcons.Add(icon);
}
```

- [ ] **Step 2: 运行测试确认失败。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~FileTypeIconProviderTests`

Expected: 编译失败，因为提供器和像素类型尚不存在。

- [ ] **Step 3: 添加平台抽象和提供器。** `FileTypeIconProvider.cs` 定义：

```csharp
namespace Clipboard.Windows.Platform;

internal sealed record ShellIconPixels(int Width, int Height, byte[] BgraPixels);

internal interface IShellIconNativeApi
{
    nint GetFileIcon(string virtualName, uint attributes);
    ShellIconPixels CopyIconPixels(nint icon, int size);
    void DestroyIcon(nint icon);
}

internal sealed class ShellFileTypeIconProvider
{
    internal const uint FileAttributeNormal = 0x80;
    internal const uint FileAttributeDirectory = 0x10;
    private readonly IShellIconNativeApi _native;

    public ShellFileTypeIconProvider(IShellIconNativeApi native)
    {
        _native = native;
    }

    public ShellIconPixels? Load(string cacheKey)
    {
        string virtualName;
        uint attributes;
        if (string.Equals(cacheKey, "directory", StringComparison.Ordinal))
        {
            virtualName = "placeholder";
            attributes = FileAttributeDirectory;
        }
        else if (cacheKey.StartsWith("file:", StringComparison.Ordinal)
            && cacheKey.Length > "file:".Length)
        {
            virtualName = $"placeholder{cacheKey["file:".Length..]}";
            attributes = FileAttributeNormal;
        }
        else if (string.Equals(cacheKey, "file", StringComparison.Ordinal))
        {
            virtualName = "placeholder";
            attributes = FileAttributeNormal;
        }
        else
        {
            return null;
        }

        nint icon = nint.Zero;
        try
        {
            icon = _native.GetFileIcon(virtualName, attributes);
            return icon == nint.Zero ? null : _native.CopyIconPixels(icon, 32);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icon != nint.Zero)
            {
                _native.DestroyIcon(icon);
            }
        }
    }
}
```

- [ ] **Step 4: 实现真实 Native API。** `ShellIconNativeApi.cs` 使用 `SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES` 调用 `SHGetFileInfoW`；`SHFILEINFO.hIcon` 转换流程固定为：获取屏幕 DC、创建兼容 DC、创建宽高 32、负高度、32bpp、`BI_RGB` 的 top-down DIB、选入 DIB、清零像素、`DrawIconEx(..., DI_NORMAL)`、复制 `32 * 32 * 4` 字节、恢复旧对象，然后在 `finally` 按顺序 `DeleteObject`、`DeleteDC`、`ReleaseDC`。`GetFileIcon` 返回空句柄时不执行转换。所有 P/Invoke 放在该文件的 `ShellIconNativeApi` 内，不污染已有 `Platform/NativeMethods.cs`。

实现必须包含以下关键常量和结构，避免依赖 `System.Drawing`：

```csharp
private const uint ShgfiIcon = 0x100;
private const uint ShgfiLargeIcon = 0x0;
private const uint ShgfiUseFileAttributes = 0x10;
private const uint DibRgbColors = 0;
private const uint BiRgb = 0;
private const uint DiNormal = 0x3;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
private struct SHFILEINFO
{
    internal nint HIcon;
    internal int IIcon;
    internal uint Attributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
}

[StructLayout(LayoutKind.Sequential)]
private struct BITMAPINFOHEADER
{
    internal uint Size;
    internal int Width;
    internal int Height;
    internal ushort Planes;
    internal ushort BitCount;
    internal uint Compression;
    internal uint ImageSize;
    internal int XPelsPerMeter;
    internal int YPelsPerMeter;
    internal uint ClrUsed;
    internal uint ClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
private struct BITMAPINFO
{
    internal BITMAPINFOHEADER Header;
}
```

使用 `unsafe` 只用于把 DIB 内存清零；`Clipboard.Windows.csproj` 已允许 unsafe。`DrawIconEx` 或任一 GDI 创建失败时抛出普通异常，由上层提供器转为降级 glyph，同时 `finally` 释放所有已经创建的句柄。

- [ ] **Step 5: 运行提供器测试和纯 Windows 像素冒烟。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~FileTypeIconProviderTests`

Expected: PASS，虚拟名称只出现 `placeholder`，没有代表文件路径。

同一条命令还会执行 `Production_native_api_returns_a_32_pixel_icon_or_a_safe_null_result`。该测试不写数据库、不读真实文件；若 Shell 没有 `.txt` 图标关联，允许返回 null，但进程不能崩溃。

- [ ] **Step 6: 提交。**

```powershell
git add src/Clipboard.Windows/Platform/FileTypeIconProvider.cs src/Clipboard.Windows/Platform/ShellIconNativeApi.cs tests/Clipboard.Windows.Tests/Platform/FileTypeIconProviderTests.cs
git commit -m "feat(windows): load system file type icons"
```

## Task 4: 接入可见卡片和 WinUI 图像源

**Files:**

- Create: `src/Clipboard.Windows/Views/FileIconLoadTracker.cs`
- Create: `src/Clipboard.Windows/Views/SoftwareBitmapSourceFactory.cs`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml:42-50`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml.cs:21-22,293-364`
- Test: `tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Views/FileIconLoadTrackerTests.cs`

- [ ] **Step 1: 写失败测试。** 新建 `FileIconLoadTrackerTests`，覆盖相同扩展名但不同卡片 ID 的回收场景：

```csharp
[Fact]
public void Recycled_card_replaces_pending_request_even_when_cache_key_is_same()
{
    var tracker = new FileIconLoadTracker();
    Guid first = Guid.NewGuid();
    Guid second = Guid.NewGuid();

    Assert.True(tracker.Begin(first, "file:.docx"));
    Assert.True(tracker.Begin(second, "file:.docx"));
    Assert.False(tracker.IsCurrent(first, "file:.docx"));
    Assert.True(tracker.IsCurrent(second, "file:.docx"));
}
```

在 `XamlResourceConfigurationTests` 增加断言：文件卡片仍保留 `42` 宽高；存在 `FileIconGlyph` 绑定；存在 `FileIcon_Loaded` 和 `FileIcon_DataContextChanged`；系统 Image 固定 `32` 宽高且初始 `Collapsed`。

- [ ] **Step 2: 运行测试确认失败。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~FileIconLoadTrackerTests|FullyQualifiedName~XamlResourceConfigurationTests"`

Expected: 编译或断言失败，因为 tracker、事件和 XAML Image 尚不存在。

- [ ] **Step 3: 实现 tracker 和像素工厂。** `FileIconLoadTracker` 保存 `Guid? ItemId` 与 `string? CacheKey`，提供 `Begin(Guid,string)`、`IsCurrent(Guid,string)`。`SoftwareBitmapSourceFactory.CreateAsync` 使用 `SoftwareBitmap.CreateCopyFromBuffer(pixels.BgraPixels.AsBuffer(), BitmapPixelFormat.Bgra8, pixels.Width, pixels.Height, BitmapAlphaMode.Premultiplied)`，创建 `SoftwareBitmapSource` 后调用 `SetBitmapAsync`，并在 `using` 中释放 `SoftwareBitmap`。

- [ ] **Step 4: 修改 XAML，保持占位区域稳定。** 把文件图标区域改为：

```xml
<Border Width="42" Height="42"
        Background="{ThemeResource SubtleFillColorSecondaryBrush}"
        CornerRadius="4">
    <Grid HorizontalAlignment="Center" VerticalAlignment="Center">
        <FontIcon x:Name="FallbackFileIcon"
                  FontSize="22"
                  Glyph="{Binding FileIconGlyph}" />
        <Image x:Name="SystemFileIcon"
               Width="32"
               Height="32"
               Stretch="Uniform"
               Visibility="Collapsed"
               Loaded="FileIcon_Loaded"
               DataContextChanged="FileIcon_DataContextChanged" />
    </Grid>
</Border>
```

系统 Image 成功时可见、Fluent glyph 隐藏；失败时 Image 保持折叠、glyph 可见。不得改变外层 42 x 42 尺寸。

- [ ] **Step 5: 在 MainWindow 中接入惰性加载。** 增加字段：

```csharp
private readonly FileIconCache<SoftwareBitmapSource> _fileIconCache =
    new(128, TimeProvider.System, TimeSpan.FromMinutes(5));
private readonly ShellFileTypeIconProvider _fileIconProvider =
    new(new ShellIconNativeApi());
```

增加两个事件入口并实现以下流程：只有 `DataContext` 是文件卡片时才继续；从 `FileIconCacheKey` 取得 key；在 Image.Tag 上保存 tracker；先显示 Fluent glyph；调用 `_fileIconCache.GetAsync(key, async () => { ShellIconPixels? pixels = await Task.Run(() => _fileIconProvider.Load(key)); return pixels is null ? null : await SoftwareBitmapSourceFactory.CreateAsync(pixels); })`；结果返回后调用 `IsCurrentFileIcon` 校验 tracker、卡片 ID、缓存键和 `DataContext`，通过后设置 `Image.Source` 并隐藏 fallback。任何异常由缓存转为 null，不能调用 `SetStatus`、关闭面板或影响粘贴。

使用以下辅助方法定位同一图标 Grid 中的 fallback，避免透明系统图标和 glyph 叠加：

```csharp
private static void ApplyFileIcon(Image image, ImageSource? source)
{
    image.Source = source;
    image.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
    if (image.Parent is Panel panel
        && panel.Children.OfType<FontIcon>().SingleOrDefault() is FontIcon fallback)
    {
        fallback.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
```

`MainWindow.xaml.cs` 现有图片预览逻辑保持不变；文件图标使用独立的 tracker、缓存和事件，不能复用图片内容缓存的 Guid 键。

- [ ] **Step 6: 运行视图测试。**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~FileIconLoadTrackerTests|FullyQualifiedName~XamlResourceConfigurationTests"`

Expected: PASS。

- [ ] **Step 7: 提交。**

```powershell
git add src/Clipboard.Windows/Views/FileIconLoadTracker.cs src/Clipboard.Windows/Views/SoftwareBitmapSourceFactory.cs src/Clipboard.Windows/Views/MainWindow.xaml src/Clipboard.Windows/Views/MainWindow.xaml.cs tests/Clipboard.Windows.Tests/Views/FileIconLoadTrackerTests.cs tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs
git commit -m "feat(windows): render system icons for visible file cards"
```

## Task 5: 全量验证和手工验收

**Files:**

- No production file changes unless a test exposes a regression.

- [ ] **Step 1: 检查运行中的客户端。** 构建前执行 `Get-Process Clipboard.Windows -ErrorAction SilentlyContinue`；若进程存在，先通过应用自身退出或正常关闭，确认 `Clipboard.Windows.exe` 和 `clipboard_ffi.dll` 不再被锁定，再开始构建。不得使用强制删除或回滚命令。

- [ ] **Step 2: 运行 Windows 客户端完整验证。**

Run: `pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`

Expected: 工具链、Windows 测试、Rust Core 测试和 x64 构建全部通过。

- [ ] **Step 3: 运行最终 x64 构建。**

Run: `dotnet build src/Clipboard.Windows/Clipboard.Windows.csproj -c Debug -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --no-restore`

Expected: `Build succeeded`，无新增编译警告。

- [ ] **Step 4: 手工验收可见文件类型。** 启动构建产物，复制 `.docx`、`.pdf`、`.zip`、普通文件夹、无扩展名文件和包含多个条目的文件束；确认图标与资源管理器当前文件关联一致，多文件仍显示“共 N 项”。快速滚动列表时确认卡片不会出现上一条记录的图标。

- [ ] **Step 5: 手工验收性能和隐私边界。** 在 100%、150%、200% DPI 下打开 `Win+V` 面板，确认面板不等待 Shell 图标、42 x 42 区域不跳动、失败时仍显示 Fluent glyph；搜索、粘贴和同步行为不变，路径不进入搜索响应、日志或同步队列。检查同一扩展名大量出现时只产生一次成功 Shell 请求。

- [ ] **Step 6: 最终提交检查。**

Run: `git diff --check; git status --short --branch; git log --oneline -6`

Expected: 工作区干净，历史包含本计划的四个功能提交，未出现 Rust、数据库或 C ABI 的意外变更。

## 计划自审

- 规格覆盖：Shell 虚拟名称和 `SHGFI_USEFILEATTRIBUTES` 在 Task 3；BGRA/DIB 和句柄释放在 Task 3；128 项 LRU、并发合并、失败 5 分钟 TTL 在 Task 2；可见项惰性加载、UI 线程 `SoftwareBitmapSource`、虚拟化防错位在 Task 4；降级 glyph、固定尺寸和 DPI/性能验收在 Task 4-5；不触碰同步和路径隐私边界在文件边界与 Task 5。
- 占位扫描：计划没有 `TODO`、`TBD` 或未定义的“稍后处理”步骤；每个生产文件和测试文件均有明确路径、行为和命令。
- 类型一致性：Task 1 的 `FileIconCacheKey` 被 Task 4 使用；Task 3 的 `ShellIconPixels` 和 `ShellFileTypeIconProvider.Load` 被 Task 4 使用；Task 4 的 tracker 签名与测试一致。
- 范围检查：所有任务均属于 Windows 文件类型图标这一项独立功能，未扩展到真实路径图标、缩略图、`.lnk` 解析或跨设备文件同步。
