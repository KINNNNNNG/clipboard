using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class VerificationScriptTests
{
    private static string LoadScript()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "verify-windows-client.ps1");

        return File.ReadAllText(path);
    }

    [Fact]
    public void VerificationScript_ContainsReleaseVerificationContract()
    {
        var script = LoadScript();

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
