using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SaveUltimate.App;

internal static class Ui
{
    public static Button Button(string key, RoutedEventHandler action, string? style = null)
    {
        var button = new Button { Content = Tr.Text(key), Margin = new(0, 0, 6, 0) };
        if (style is not null) button.SetResourceReference(FrameworkElement.StyleProperty, style);
        button.Click += action; return button;
    }
    public static TextBlock Text(string key) => new() { Text = Tr.Text(key), TextWrapping = TextWrapping.Wrap };
    public static Window? Owner => Application.Current?.Dispatcher.CheckAccess() == true
        ? Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive) : null;
    public static void Setup(Window window, string title, double width, double height)
    {
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        window.Title = Tr.Text(title); window.Width = width; window.Height = height;
        window.MinWidth = Math.Min(width, 640); window.MinHeight = Math.Min(height, 400);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Loaded += (_, _) => { if (window.Content is FrameworkElement content) content.SetResourceReference(FrameworkElement.LayoutTransformProperty, "UiTransform"); };
        if (Owner is { } owner && owner != window) window.Owner = owner;
    }
    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
}

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string title = "SaveUltimate", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None, Dictionary<MessageBoxResult, string>? labels = null) => Show(Ui.Owner, message, title, buttons, image, defaultResult, labels);
    public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None, Dictionary<MessageBoxResult, string>? labels = null)
    {
        var dialog = new Window { SizeToContent = SizeToContent.Height, MaxHeight = 650, ResizeMode = ResizeMode.NoResize, Owner = owner };
        Ui.Setup(dialog, title, 540, 230);
        var body = new DockPanel { Margin = new(20) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var text = Ui.Text(message); text.Text = message; body.Children.Add(Ui.Scroll(text)); dialog.Content = body;
        MessageBoxResult result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        var choices = buttons switch { MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel], _ => [MessageBoxResult.OK] };
        foreach (var choice in choices)
        {
            var button = Ui.Button(labels?.GetValueOrDefault(choice) ?? choice.ToString(), (_, _) => { result = choice; dialog.DialogResult = true; });
            button.IsDefault = choice == (defaultResult == MessageBoxResult.None ? choices[^1] : defaultResult);
            button.IsCancel = choice == MessageBoxResult.Cancel || buttons == MessageBoxButton.YesNo && choice == MessageBoxResult.No;
            footer.Children.Add(button);
        }
        dialog.ShowDialog(); return result;
    }
}
