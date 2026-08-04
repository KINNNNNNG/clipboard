using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class PasteCoordinatorTests
{
    [Fact]
    public async Task Text_paste_writes_hides_restores_and_sends_input_in_order()
    {
        var events = new List<string>();
        var suppression = new ClipboardSuppression();
        var content = new FakeContentReader(events);
        var writer = new FakeClipboardWriter(events);
        var foreground = new FakeForegroundWindowService(events)
        {
            RestoreResult = true,
            SentInputCount = 4,
        };
        var coordinator = CreateCoordinator(
            content,
            writer,
            foreground,
            suppression,
            () => events.Add("hide"));
        var item = TextItem("原始窗口中的完整正文");

        PasteResult result = await coordinator.PasteAsync(item, new nint(42));

        Assert.Equal(PasteResultKind.Pasted, result.Kind);
        Assert.Equal(item.Id, result.ItemId);
        Assert.Equal(
            ["write_text", "hide", "restore", "send_input"],
            events);
        Assert.Equal(item.Preview, writer.Text);
        Assert.True(suppression.TryConsumeText(item.Preview));
    }

    [Fact]
    public async Task Image_paste_reads_png_and_registers_image_suppression()
    {
        var events = new List<string>();
        byte[] png = [0x89, 0x50, 0x4e, 0x47];
        var suppression = new ClipboardSuppression();
        var content = new FakeContentReader(events)
        {
            ImagePng = png,
        };
        var writer = new FakeClipboardWriter(events);
        var foreground = new FakeForegroundWindowService(events)
        {
            RestoreResult = true,
            SentInputCount = 4,
        };
        var coordinator = CreateCoordinator(
            content,
            writer,
            foreground,
            suppression,
            () => events.Add("hide"));
        var item = ImageItem();

        PasteResult result = await coordinator.PasteAsync(item, new nint(42));

        Assert.Equal(PasteResultKind.Pasted, result.Kind);
        Assert.Equal(item.Id, content.LastReadImageId);
        Assert.Equal(png, writer.ImagePng);
        Assert.True(suppression.TryConsumeImage(png));
    }

    [Fact]
    public async Task Missing_original_window_keeps_clipboard_and_requires_manual_paste()
    {
        var events = new List<string>();
        var writer = new FakeClipboardWriter(events);
        var foreground = new FakeForegroundWindowService(events)
        {
            RestoreResult = true,
            SentInputCount = 4,
        };
        var coordinator = CreateCoordinator(
            new FakeContentReader(events),
            writer,
            foreground,
            new ClipboardSuppression(),
            () => events.Add("hide"));

        PasteResult result = await coordinator.PasteAsync(TextItem("保留在系统剪贴板"), nint.Zero);

        Assert.Equal(PasteResultKind.ManualPasteRequired, result.Kind);
        Assert.Equal(["write_text", "hide"], events);
        Assert.Equal(0, foreground.RestoreCalls);
        Assert.Equal(0, foreground.SendInputCalls);
    }

    [Fact]
    public async Task Failed_window_restore_never_sends_input()
    {
        var events = new List<string>();
        var foreground = new FakeForegroundWindowService(events)
        {
            RestoreResult = false,
            SentInputCount = 4,
        };
        var coordinator = CreateCoordinator(
            new FakeContentReader(events),
            new FakeClipboardWriter(events),
            foreground,
            new ClipboardSuppression(),
            () => events.Add("hide"));

        PasteResult result = await coordinator.PasteAsync(TextItem("窗口恢复失败"), new nint(42));

        Assert.Equal(PasteResultKind.ManualPasteRequired, result.Kind);
        Assert.Equal(1, foreground.RestoreCalls);
        Assert.Equal(0, foreground.SendInputCalls);
    }

    [Fact]
    public async Task Partial_send_input_is_manual_paste_fallback()
    {
        var foreground = new FakeForegroundWindowService([])
        {
            RestoreResult = true,
            SentInputCount = 3,
        };
        var coordinator = CreateCoordinator(
            new FakeContentReader([]),
            new FakeClipboardWriter([]),
            foreground,
            new ClipboardSuppression(),
            static () => { });

        PasteResult result = await coordinator.PasteAsync(TextItem("输入数量不足"), new nint(42));

        Assert.Equal(PasteResultKind.ManualPasteRequired, result.Kind);
        Assert.Equal(1, foreground.SendInputCalls);
    }

    [Fact]
    public async Task Empty_content_is_rejected_without_writing_clipboard()
    {
        var events = new List<string>();
        var writer = new FakeClipboardWriter(events);
        var coordinator = CreateCoordinator(
            new FakeContentReader(events),
            writer,
            new FakeForegroundWindowService(events),
            new ClipboardSuppression(),
            () => events.Add("hide"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.PasteAsync(TextItem(string.Empty), new nint(42)));

        Assert.Empty(events);
        Assert.Null(writer.Text);
        Assert.Null(writer.ImagePng);
    }

    [Fact]
    public async Task Image_read_failure_is_rejected_without_writing_clipboard()
    {
        var events = new List<string>();
        var content = new FakeContentReader(events)
        {
            ImageFailure = new InvalidDataException("invalid image"),
        };
        var writer = new FakeClipboardWriter(events);
        var coordinator = CreateCoordinator(
            content,
            writer,
            new FakeForegroundWindowService(events),
            new ClipboardSuppression(),
            () => events.Add("hide"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            coordinator.PasteAsync(ImageItem(), new nint(42)));

        Assert.Equal(["read_image"], events);
        Assert.Null(writer.ImagePng);
    }

    [Fact]
    public async Task Failed_image_write_removes_the_pending_suppression_token()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47];
        var events = new List<string>();
        var suppression = new ClipboardSuppression();
        var writer = new FakeClipboardWriter(events)
        {
            Failure = new InvalidDataException("PNG decode failed"),
        };
        var coordinator = CreateCoordinator(
            new FakeContentReader(events) { ImagePng = png },
            writer,
            new FakeForegroundWindowService(events),
            suppression,
            static () => { });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            coordinator.PasteAsync(ImageItem(), new nint(42)));

        Assert.False(suppression.TryConsumeImage(png));
    }

    private static PasteCoordinator CreateCoordinator(
        IClipboardItemContentReader content,
        IClipboardWriter writer,
        IForegroundWindowService foreground,
        ClipboardSuppression suppression,
        Action hidePanel) =>
        new(content, writer, foreground, suppression, hidePanel);

    private static ClipboardItemDto TextItem(string text) =>
        new(Guid.NewGuid(), "text", text, "notepad.exe", 100, false, null, null, null);

    private static ClipboardItemDto ImageItem() =>
        new(Guid.NewGuid(), "image", "image 640x480", "mspaint.exe", 100, false, 640, 480, 4);

    private sealed class FakeContentReader(List<string> events) : IClipboardItemContentReader
    {
        public byte[] ImagePng { get; init; } = [0x89, 0x50, 0x4e, 0x47];
        public Exception? ImageFailure { get; init; }
        public Guid? LastReadImageId { get; private set; }

        public Task<byte[]> ReadImageAsync(Guid itemId, CancellationToken cancellationToken = default)
        {
            events.Add("read_image");
            LastReadImageId = itemId;
            return ImageFailure is null
                ? Task.FromResult(ImagePng.ToArray())
                : Task.FromException<byte[]>(ImageFailure);
        }

        public Task<FileBundleResponseDto> ReadFileBundleAsync(
            Guid itemId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<FileBundleResponseDto>(
                new NotSupportedException("file bundle content is not configured"));
    }

    private sealed class FakeClipboardWriter(List<string> events) : IClipboardWriter
    {
        public string? Text { get; private set; }
        public byte[]? ImagePng { get; private set; }
        public Exception? Failure { get; init; }

        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            events.Add("write_text");
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }
            Text = text;
            return Task.CompletedTask;
        }

        public Task WriteImageAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken = default)
        {
            events.Add("write_image");
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }
            ImagePng = png.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeForegroundWindowService(List<string> events)
        : IForegroundWindowService
    {
        public bool RestoreResult { get; init; }
        public uint SentInputCount { get; init; }
        public int RestoreCalls { get; private set; }
        public int SendInputCalls { get; private set; }

        public Task<bool> RestoreAsync(nint originalHwnd, CancellationToken cancellationToken = default)
        {
            events.Add("restore");
            RestoreCalls++;
            return Task.FromResult(RestoreResult);
        }

        public uint SendPasteInput()
        {
            events.Add("send_input");
            SendInputCalls++;
            return SentInputCount;
        }
    }
}
