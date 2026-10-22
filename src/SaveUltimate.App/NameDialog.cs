using System.Windows;
using System.Windows.Controls;

namespace SaveUltimate.App;

internal sealed class NameDialog : Window
{
    private readonly TextBox input;
    public string Value => input.Text.Trim();
    public NameDialog(string prompt, string current)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = Tr.Text("merc.rename_title"); Width = 460; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        input = new TextBox { Text = current, Margin = new(0, 12, 0, 12) }; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = Tr.Text("OK"), IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => { if (Value.Length > 0 && !Value.Contains('\0')) DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = Tr.Text("Cancel"), IsCancel = true, MinWidth = 80, Margin = new(8, 0, 0, 0) });
        panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
