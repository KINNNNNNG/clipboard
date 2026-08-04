using System.Xml.Linq;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class XamlResourceConfigurationTests
{
    [Fact]
    public void AppResourcesMergeWinUiControlThemeResources()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml");
        XDocument document = XDocument.Load(path);

        XElement? resources = document
            .Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "XamlControlsResources");

        Assert.NotNull(resources);
        Assert.Equal("using:Microsoft.UI.Xaml.Controls", resources.Name.NamespaceName);
    }
}
