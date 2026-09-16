using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace ECAD.Desktop;

internal static class WorkspaceTheme
{
    public static void Apply(Window window)
    {
        window.Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        { ["WorkspaceSurface"] = new SolidColorBrush(Color.Parse("#f8fafc")), ["WorkspaceText"] = new SolidColorBrush(Color.Parse("#19283b")), ["WorkspaceControl"] = Brushes.White };
        window.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
        { ["WorkspaceSurface"] = new SolidColorBrush(Color.Parse("#1a2230")), ["WorkspaceText"] = new SolidColorBrush(Color.Parse("#e4eaf2")), ["WorkspaceControl"] = new SolidColorBrush(Color.Parse("#293649")) };
        window.Styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(Window.BackgroundProperty, new DynamicResourceExtension("WorkspaceSurface")), new Setter(Window.ForegroundProperty, new DynamicResourceExtension("WorkspaceText")) } });
        window.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(Button.FontSizeProperty, 13d), new Setter(Button.BackgroundProperty, new DynamicResourceExtension("WorkspaceControl")), new Setter(Button.MinHeightProperty, 32d) } });
        window.Styles.Add(new Style(x => x.OfType<TabItem>()) { Setters = { new Setter(TabItem.FontSizeProperty, 13d), new Setter(TabItem.PaddingProperty, new Thickness(8, 6)) } });
        window.Styles.Add(new Style(x => x.OfType<TextBox>()) { Setters = { new Setter(TextBox.FontSizeProperty, 13d), new Setter(TextBox.MinHeightProperty, 32d) } });
    }
}
