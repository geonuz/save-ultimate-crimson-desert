using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CDSE.Core.Saves;

namespace CDSE.App;

/// <summary>
/// Where a dye code is pasted. The clipboard may hold something newer than the code by now, so it fills the box only while it is a
/// dye code; otherwise the box starts empty for a paste (Win+V picks an older one from the clipboard history).
/// </summary>
public sealed class DyeCodeDialog : Window
{
    public TextBox Input { get; }
    public DyeEntry[]? Entries { get; private set; }
    private readonly TextBlock state;
    private readonly Button ok;

    public DyeCodeDialog(string clipboard)
    {
        SetResourceReference(StyleProperty, "DialogWindow");
        Title = Tr.Text("dye.title"); Width = 520; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = Tr.Text("dye.paste_prompt"), TextWrapping = TextWrapping.Wrap });
        // Messages break a long code over lines; the reader skips the breaks.
        Input = new TextBox { Height = 120, VerticalContentAlignment = VerticalAlignment.Top, Margin = new(0, 12, 0, 8), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(Input, Tr.Text("dye.paste_prompt"));
        panel.Children.Add(Input);
        state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        panel.Children.Add(state);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        ok = new Button { Content = Tr.Text("OK"), IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => { if (Entries is not null) DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = Tr.Text("Cancel"), IsCancel = true, MinWidth = 80, Margin = new(8, 0, 0, 0) });
        panel.Children.Add(buttons); Content = panel;
        Input.TextChanged += (_, _) => Check();
        Input.Text = MainViewModel.ReadDyeCode(clipboard, out _) is null ? "" : clipboard.Trim();
        Check();
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); Input.ScrollToHome(); };
    }

    private void Check()
    {
        bool empty = string.IsNullOrWhiteSpace(Input.Text);
        string problem = "";
        Entries = empty ? null : MainViewModel.ReadDyeCode(Input.Text, out problem);
        ok.IsEnabled = Entries is not null;
        state.Text = empty ? Tr.Text("dye.paste_hint") : Entries is { } entries ? Tr.Format("dye.paste_ready", ("count", entries.Length.ToString("N0"))) : problem;
        state.SetResourceReference(TextBlock.ForegroundProperty, Entries is null && !empty ? "error" : "text_dim");
    }
}
