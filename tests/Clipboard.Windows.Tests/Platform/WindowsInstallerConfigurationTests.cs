using System.IO;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class WindowsInstallerConfigurationTests
{
    [Fact]
    public void PackageScriptPublishesSelfContainedReleaseAndWritesChecksums()
    {
        string script = ReadFixture("package-windows.ps1");

        Assert.Contains("WindowsAppSDKSelfContained=true", script);
        Assert.Contains("'publish'", script);
        Assert.Contains("SHA256SUMS.txt", script);
        Assert.Contains("installer\\Clipboard.iss", script);
        Assert.Contains("SkipInstaller", script);
    }

    [Fact]
    public void InstallerTargetsCurrentUserX64AndPreservesUserData()
    {
        string installer = ReadFixture("Clipboard.iss");

        Assert.Contains("PrivilegesRequired=lowest", installer);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\Clipboard", installer);
        Assert.Contains("ArchitecturesAllowed=x64compatible", installer);
        Assert.Contains("Uninstallable=yes", installer);
        Assert.Contains("Clipboard.Windows.exe", installer);
    }

    [Fact]
    public void ReleaseWorkflowPublishesVersionTagsWithWritePermission()
    {
        string workflow = ReadFixture("release.yml");

        Assert.Contains("tags:", workflow);
        Assert.Contains("v*", workflow);
        Assert.Contains("contents: write", workflow);
        Assert.Contains("package-windows.ps1", workflow);
        Assert.Contains("SHA256SUMS.txt", workflow);
        Assert.Contains("choco install innosetup", workflow);
        Assert.Contains("runs-on: ubuntu-22.04", workflow);
        Assert.Contains("runs-on: windows-2022", workflow);
        Assert.Contains("core:", workflow);
        Assert.Contains("windows-client:", workflow);
        Assert.Contains("release:", workflow);
        Assert.Contains("needs: [core, windows-client]", workflow);
        Assert.DoesNotContain("Install Windows App Runtime", workflow);
        Assert.Contains("artifacts/release/Clipboard-Setup-v${{ steps.version.outputs.version }}.exe", workflow);
        Assert.DoesNotContain("release-files/installer/", workflow);
    }

    private static string ReadFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
