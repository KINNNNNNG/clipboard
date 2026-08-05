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

    [Fact]
    public void History_list_uses_a_flat_unselected_item_container()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XElement list = document
            .Descendants()
            .Single(element => element.Name.LocalName == "ListView");

        Assert.Equal("None", list.Attribute("SelectionMode")?.Value);
        XElement style = list
            .Descendants()
            .Single(element => element.Name.LocalName == "Style");
        Assert.Equal("ListViewItem", style.Attribute("TargetType")?.Value);
        Assert.NotNull(style.Descendants().SingleOrDefault(
            element => element.Name.LocalName == "ControlTemplate"));
    }

    [Fact]
    public void Main_window_does_not_add_a_system_backdrop_behind_the_opaque_panel()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);

        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "DesktopAcrylicBackdrop");
    }

    [Fact]
    public void Main_window_uses_a_transparent_host_and_inset_rounded_surface()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);

        XElement root = document
            .Descendants()
            .Single(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "Root");
        Assert.Equal(
            "Transparent",
            root.Attribute("Background")?.Value);
        Assert.Equal("0", root.Attribute("CornerRadius")?.Value);
        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == "Border"
                && element.Attribute("CornerRadius")?.Value == "12");
    }
}
