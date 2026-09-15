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
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", script);
        Assert.Contains("Microsoft.VisualStudio.Component.Windows11SDK.26100", script);
        Assert.Contains("if ($visualStudio) { $visualStudio.Trim() } else { '' }", script);
    }

    private static string ReadFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
