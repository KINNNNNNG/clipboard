using System.IO;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class ToolchainVerificationTests
{
    [Fact]
    public void ToolchainVerificationAcceptsAnyVisualStudioEdition()
    {
        string script = ReadFixture("verify-toolchain.ps1");

        Assert.DoesNotContain("Microsoft.VisualStudio.Product.BuildTools", script);
        Assert.Contains("function Find-VisualStudioRoot", script);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", script);
        Assert.Contains(@"VC\Tools\MSVC", script);
        Assert.Contains(@"bin\Hostx64\x64\cl.exe", script);
        Assert.Contains("10.0.26100.0", script);
    }

    private static string ReadFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
