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
    public void History_list_uses_native_presenter_selection()
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
        XElement template = style
            .Descendants()
            .Single(element => element.Name.LocalName == "ControlTemplate");
        Assert.NotNull(template.Descendants().SingleOrDefault(
            element => element.Name.LocalName == "ListViewItemPresenter"));
        Assert.DoesNotContain(template.Descendants(), element =>
            element.Name.LocalName == "VisualStateManager");
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
    public void Settings_window_switches_provider_specific_sync_fields()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SettingsWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement provider = document.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "SyncProviderComboBox");
        Assert.Equal("SyncProviderComboBox_SelectionChanged", provider.Attribute("SelectionChanged")?.Value);
        Assert.NotNull(document.Descendants()
            .SingleOrDefault(element => element.Attribute(x + "Name")?.Value == "WebDavFields"));
        Assert.NotNull(document.Descendants()
            .SingleOrDefault(element => element.Attribute(x + "Name")?.Value == "OssFields"));
    }

    [Fact]
    public void Settings_window_keeps_fixed_tabs_without_add_or_close_buttons()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SettingsWindow.xaml");
        XDocument document = XDocument.Load(path);

        XElement tabView = document.Descendants()
            .Single(element => element.Name.LocalName == "TabView");
        XElement[] tabs = tabView.Elements()
            .Where(element => element.Name.LocalName == "TabViewItem")
            .ToArray();

        Assert.Equal("False", tabView.Attribute("IsAddTabButtonVisible")?.Value);
        Assert.Equal(2, tabs.Length);
        Assert.All(tabs, tab => Assert.Equal("False", tab.Attribute("IsClosable")?.Value));
    }

    [Fact]
    public void History_list_uses_the_system_focus_visual_on_the_native_presenter()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XElement list = document
            .Descendants()
            .Single(element => element.Name.LocalName == "ListView");
        XElement style = list
            .Descendants()
            .Single(element => element.Name.LocalName == "Style");
        XElement presenter = list
            .Descendants()
            .Single(element => element.Name.LocalName == "ListViewItemPresenter");

        Assert.Equal(
            "True",
            style.Descendants()
                .Single(element => element.Name.LocalName == "Setter"
                    && element.Attribute("Property")?.Value == "UseSystemFocusVisuals")
                .Attribute("Value")?.Value);
        Assert.Equal(
            "1",
            style.Descendants()
                .Single(element => element.Name.LocalName == "Setter"
                    && element.Attribute("Property")?.Value == "FocusVisualMargin")
                .Attribute("Value")?.Value);
        Assert.Equal(
            "2",
            style.Descendants()
                .Single(element => element.Name.LocalName == "Setter"
                    && element.Attribute("Property")?.Value == "FocusVisualPrimaryThickness")
                .Attribute("Value")?.Value);
        Assert.Equal(
            "1",
            style.Descendants()
                .Single(element => element.Name.LocalName == "Setter"
                    && element.Attribute("Property")?.Value == "FocusVisualSecondaryThickness")
                .Attribute("Value")?.Value);

        Assert.Equal("Transparent", presenter.Attribute("SelectedBackground")?.Value);
        Assert.Equal("Transparent", presenter.Attribute("SelectedPointerOverBackground")?.Value);
        Assert.Equal("Transparent", presenter.Attribute("SelectedPressedBackground")?.Value);
        Assert.Equal("Transparent", presenter.Attribute("SelectedBorderBrush")?.Value);
        Assert.Equal("0", presenter.Attribute("SelectedBorderThickness")?.Value);
        Assert.Equal("False", presenter.Attribute("SelectionCheckMarkVisualEnabled")?.Value);
        Assert.Equal("False", presenter.Attribute("SelectionIndicatorVisualEnabled")?.Value);
        Assert.Equal(
            "{TemplateBinding FocusVisualPrimaryBrush}",
            presenter.Attribute("FocusVisualPrimaryBrush")?.Value);
        Assert.Equal(
            "{TemplateBinding FocusVisualSecondaryBrush}",
            presenter.Attribute("FocusVisualSecondaryBrush")?.Value);
        Assert.Equal(
            "{ThemeResource ListViewItemFocusBorderBrush}",
            presenter.Attribute("FocusBorderBrush")?.Value);
        Assert.Equal(
            "{ThemeResource ListViewItemFocusSecondaryBorderBrush}",
            presenter.Attribute("FocusSecondaryBorderBrush")?.Value);
    }

    [Fact]
    public void Show_panel_focuses_the_first_history_item_after_refresh()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml.cs");
        string source = File.ReadAllText(path);
        int showPanelStart = source.IndexOf("public void ShowPanel()", StringComparison.Ordinal);
        int hidePanelStart = source.IndexOf("public void HidePanel()", showPanelStart);

        Assert.True(showPanelStart >= 0);
        Assert.True(hidePanelStart > showPanelStart);
        string showPanel = source[showPanelStart..hidePanelStart];

        Assert.Contains("RefreshAsync", showPanel);
        Assert.Contains("FocusSelectedHistoryItem", showPanel);
        Assert.DoesNotContain("SearchBox.Focus", showPanel);
    }

    [Fact]
    public void History_load_error_uses_a_reserved_row_above_the_footer()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement errorPanel = document.Descendants().Single(
            element => element.Attribute(x + "Name")?.Value == "HistoryErrorPanel");
        XElement footer = document.Descendants().Single(
            element => element.Attribute("Text")?.Value == "本机历史").Parent!;
        XElement errorRow = errorPanel.Parent!;

        Assert.Equal("3", errorRow.Attribute("Grid.Row")?.Value);
        Assert.Equal(
            "{Binding HasErrorMessage, Converter={StaticResource BooleanVisibilityConverter}}",
            errorPanel.Attribute("Visibility")?.Value);
        Assert.Equal("4", footer.Attribute("Grid.Row")?.Value);
        Assert.Equal("WrapWholeWords", errorPanel.Descendants()
            .Single(element => element.Attribute("Text")?.Value == "{Binding ErrorMessage}")
            .Attribute("TextWrapping")?.Value);
    }

    [Fact]
    public void History_card_spacing_lives_outside_the_selected_surface()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement template = document.Descendants().Single(
            element => element.Attribute(x + "Key")?.Value == "ClipboardItemTemplate");
        XElement card = template.Elements().Single(element => element.Name.LocalName == "Border");
        XElement list = document.Descendants().Single(element => element.Name.LocalName == "ListView");
        XElement marginSetter = list.Descendants().Single(
            element => element.Name.LocalName == "Setter"
                && element.Attribute("Property")?.Value == "Margin");

        Assert.Equal("0", card.Attribute("Margin")?.Value);
        Assert.Equal("0,0,0,8", marginSetter.Attribute("Value")?.Value);
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

    [Fact]
    public void Log_window_exposes_filter_refresh_copy_and_clear_controls()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LogWindow.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.NotNull(document.Descendants().SingleOrDefault(
            element => element.Attribute(x + "Name")?.Value == "LevelComboBox"));
        Assert.NotNull(document.Descendants().SingleOrDefault(
            element => element.Attribute(x + "Name")?.Value == "FilterComboBox"));
        Assert.NotNull(document.Descendants().SingleOrDefault(
            element => element.Attribute(x + "Name")?.Value == "AutoRefreshToggle"));
        Assert.Contains(document.Descendants(), element => element.Attribute("Click")?.Value == "CopyButton_Click");
        Assert.Contains(document.Descendants(), element => element.Attribute("Click")?.Value == "ClearButton_Click");
    }
}
