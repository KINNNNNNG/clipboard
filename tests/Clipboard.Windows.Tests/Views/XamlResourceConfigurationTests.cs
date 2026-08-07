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
    public void History_list_uses_single_selection_with_selection_chrome()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XElement list = document
            .Descendants()
            .Single(element => element.Name.LocalName == "ListView");

        Assert.Equal("Single", list.Attribute("SelectionMode")?.Value);
        Assert.Equal("HistoryList_SelectionChanged", list.Attribute("SelectionChanged")?.Value);
        XElement style = list
            .Descendants()
            .Single(element => element.Name.LocalName == "Style");
        Assert.Equal("ListViewItem", style.Attribute("TargetType")?.Value);
        Assert.NotNull(style.Descendants().SingleOrDefault(
            element => element.Name.LocalName == "ControlTemplate"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement chrome = style
            .Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "SelectionChrome");
        Assert.Contains("IsSelected", chrome.Attribute("Visibility")?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void History_list_uses_root_keyboard_navigation_without_duplicate_preview_handler()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);

        XElement root = Assert.IsType<XElement>(document.Root).Elements().Single();
        XElement list = document
            .Descendants()
            .Single(element => element.Name.LocalName == "ListView");

        Assert.Equal("Root_KeyDown", root.Attribute("KeyDown")?.Value);
        Assert.Null(list.Attribute("PreviewKeyDown"));
    }

    [Fact]
    public void History_list_binds_selection_chrome_to_the_container_selection_property()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement list = document
            .Descendants()
            .Single(element => element.Name.LocalName == "ListView");
        XElement chrome = list
            .Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "SelectionChrome");

        Assert.Contains("IsSelected", chrome.Attribute("Visibility")?.Value, StringComparison.Ordinal);
        Assert.Contains("TemplatedParent", chrome.Attribute("Visibility")?.Value, StringComparison.Ordinal);
        Assert.Equal("0,0,0,8", chrome.Attribute("Margin")?.Value);
        Assert.DoesNotContain(list.Descendants(), element => element.Name.LocalName == "VisualStateManager");
    }

    [Fact]
    public void Main_window_has_file_filter_and_file_card_bindings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement fileFilter = document
            .Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "FileKindCheckBox");
        Assert.Equal("文件", fileFilter.Attribute("Content")?.Value);
        Assert.Equal("True", fileFilter.Attribute("IsChecked")?.Value);

        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "FontIcon"),
            element => element.Attribute("Glyph")?.Value == "{Binding FileIconGlyph}");
        XElement fallback = document
            .Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "FallbackFileIcon");
        Assert.Equal("{Binding FileIconGlyph}", fallback.Attribute("Glyph")?.Value);
        XElement systemIcon = document
            .Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "SystemFileIcon");
        Assert.Equal("32", systemIcon.Attribute("Width")?.Value);
        Assert.Equal("32", systemIcon.Attribute("Height")?.Value);
        Assert.Equal("Collapsed", systemIcon.Attribute("Visibility")?.Value);
        Assert.Equal("FileIcon_Loaded", systemIcon.Attribute("Loaded")?.Value);
        Assert.Equal(
            "FileIcon_DataContextChanged",
            systemIcon.Attribute("DataContextChanged")?.Value);
        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "TextBlock"),
            element => element.Attribute("Text")?.Value == "{Binding FileNameSummary}");
        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "TextBlock"),
            element => element.Attribute("Text")?.Value == "原路径不可用");
        XElement preview = document
            .Descendants()
            .Single(element => element.Attribute("Text")?.Value == "{Binding DisplayPreview}");
        Assert.Contains("ConverterParameter=text", preview.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void Main_window_uses_a_native_opaque_surface()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);

        XElement window = Assert.IsType<XElement>(document.Root);
        Assert.Equal("Window", window.Name.LocalName);

        XElement root = Assert.Single(window.Elements());
        Assert.Equal(
            "Root",
            root.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value);
        Assert.Equal("Grid", root.Name.LocalName);
        Assert.Equal(
            "{ThemeResource ApplicationPageBackgroundThemeBrush}",
            root.Attribute("Background")?.Value);
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "DesktopAcrylicBackdrop");
    }
}
