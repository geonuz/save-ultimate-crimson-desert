using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SaveUltimate.App;

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Ui.Setup(this, "About", 800, 640);
        var root = new DockPanel { Margin = new(20) }; Content = root;
        var heading = Ui.Text("SaveUltimate · C#"); heading.SetResourceReference(FrameworkElement.StyleProperty, "PageTitle"); heading.Margin = new(0, 0, 0, 16); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var close = Ui.Button("Close", (_, _) => Close()); close.HorizontalAlignment = HorizontalAlignment.Right; close.IsCancel = true; DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var tabs = new TabControl(); root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = Tr.Text("Credits"), Content = Ui.Scroll(RichTextBlock.Create(Tr.Text("about.credits"))) });
        var licenses = new DockPanel(); var selector = new ComboBox { Margin = new(0, 8, 0, 12) }; DockPanel.SetDock(selector, Dock.Top); licenses.Children.Add(selector);
        var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; licenses.Children.Add(text);
        string directory = Path.Combine(AppContext.BaseDirectory, "licenses");
        selector.ItemsSource = Directory.Exists(directory) ? new DirectoryInfo(directory).GetFiles() : []; selector.DisplayMemberPath = nameof(FileInfo.Name);
        selector.SelectionChanged += (_, _) => { try { text.Text = File.ReadAllText(((FileInfo)selector.SelectedItem).FullName); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text.Text = Tr.Text("Notice file unavailable. Reinstall the complete application."); } };
        if (selector.Items.Count > 0) selector.SelectedIndex = 0;
        tabs.Items.Add(new TabItem { Header = Tr.Text("Licenses"), Content = licenses });
    }
}
