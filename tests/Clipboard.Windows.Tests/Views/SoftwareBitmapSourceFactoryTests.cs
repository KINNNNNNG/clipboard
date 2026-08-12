using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class SoftwareBitmapSourceFactoryTests
{
    [Fact]
    public async Task Png_source_factory_rejects_empty_payload_before_ui_activation()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SoftwareBitmapSourceFactory.CreateFromPngAsync([]));
    }
}
