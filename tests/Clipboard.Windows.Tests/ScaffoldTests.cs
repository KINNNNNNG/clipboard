using Xunit;

namespace Clipboard.Windows.Tests;

public sealed class ScaffoldTests
{
    [Fact]
    public void Test_process_is_64_bit()
    {
        Assert.True(Environment.Is64BitProcess);
    }
}
