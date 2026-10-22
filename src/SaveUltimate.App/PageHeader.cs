using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace SaveUltimate.App;

public sealed class PresentConverter : IValueConverter
{
    public static PresentConverter Shared { get; } = new();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}

public sealed class PageHeader : DockPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).UpdateState()));
    public static readonly DependencyProperty HelpTopicProperty = DependencyProperty.Register(nameof(HelpTopic), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty LoadCommandProperty = DependencyProperty.Register(nameof(LoadCommand), typeof(ICommand), typeof(PageHeader));
    public static readonly DependencyProperty LoadedStateProperty = DependencyProperty.Register(nameof(LoadedState), typeof(bool), typeof(PageHeader), new PropertyMetadata(false, (d, _) => ((PageHeader)d).UpdateState()));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string HelpTopic { get => (string)GetValue(HelpTopicProperty); set => SetValue(HelpTopicProperty, value); }
    public ICommand? LoadCommand { get => (ICommand?)GetValue(LoadCommandProperty); set => SetValue(LoadCommandProperty, value); }
    public bool LoadedState { get => (bool)GetValue(LoadedStateProperty); set => SetValue(LoadedStateProperty, value); }
    private readonly TextBlock state = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 8, 0) };
    private Button load;
    public Button LoadButton
    {
        get => load;
        set { Children.Remove(load); load = value; SetDock(load, Dock.Right); Children.Insert(0, load); }
    }
    public PageHeader()
    {
        load = new Button { Content = Tr.Text("ui.load_data") }; load.SetBinding(Button.CommandProperty, new Binding(nameof(LoadCommand)) { Source = this });
        SetDock(load, Dock.Right); Children.Add(load);
        state.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = load });
        var help = Ui.Button("ui.help", (_, _) => (Window.GetWindow(this) as MainWindow)?.ShowHelp(HelpTopic)); help.Margin = new(6, 0, 6, 0);
        SetDock(help, Dock.Right); Children.Add(help);
        state.SetResourceReference(StyleProperty, "SecondaryText"); SetDock(state, Dock.Right); Children.Add(state);
        var title = new TextBlock(); title.SetResourceReference(StyleProperty, "PageTitle"); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this }); Children.Add(title);
        UpdateState();
    }
    private void UpdateState() => state.Text = Tr.Text(LoadedState ? "ui.loaded" : "ui.not_loaded");
}
