using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;

namespace SaveUltimate.App;

public sealed class PresentConverter : IValueConverter
{
    public static PresentConverter Shared { get; } = new();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The top row of every page: the page's filters on the left (the last one, usually the search box, fills) and
/// load state, help and load on the right. When the filters and a usable search box do not fit beside the
/// buttons, the filters move to a row of their own below them.
/// </summary>
[ContentProperty(nameof(Filters))]
public sealed class PageHeader : Grid
{
    public static readonly DependencyProperty HelpTopicProperty = DependencyProperty.Register(nameof(HelpTopic), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty LoadCommandProperty = DependencyProperty.Register(nameof(LoadCommand), typeof(ICommand), typeof(PageHeader));
    public static readonly DependencyProperty LoadedStateProperty = DependencyProperty.Register(nameof(LoadedState), typeof(bool), typeof(PageHeader), new PropertyMetadata(false, (d, _) => ((PageHeader)d).UpdateState()));
    public string HelpTopic { get => (string)GetValue(HelpTopicProperty); set => SetValue(HelpTopicProperty, value); }
    public ICommand? LoadCommand { get => (ICommand?)GetValue(LoadCommandProperty); set => SetValue(LoadCommandProperty, value); }
    public bool LoadedState { get => (bool)GetValue(LoadedStateProperty); set => SetValue(LoadedStateProperty, value); }
    private readonly TextBlock state = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 8, 0) };
    private readonly FilterPanel filters = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private Button load;
    public UIElementCollection Filters => filters.Children;
    public Button LoadButton
    {
        get => load;
        set { buttons.Children.Remove(load); load = value; buttons.Children.Add(load); state.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = load }); }
    }
    public PageHeader()
    {
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(filters); SetColumn(buttons, 1); Children.Add(buttons);
        state.SetResourceReference(StyleProperty, "SecondaryText"); buttons.Children.Add(state);
        var help = Ui.Button("ui.help", (_, _) => (Window.GetWindow(this) as MainWindow)?.ShowHelp(HelpTopic)); help.Margin = new(0, 0, 6, 0);
        buttons.Children.Add(help);
        load = new Button(); Tr.Bind(load, ContentControl.ContentProperty, "ui.load_data"); load.SetBinding(Button.CommandProperty, new Binding(nameof(LoadCommand)) { Source = this });
        buttons.Children.Add(load); state.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = load });
        UpdateState();
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        buttons.Measure(infinite);
        bool below = filters.Children.Count > 0 && buttons.DesiredSize.Width + filters.FixedWidth + FilterPanel.FillMinimum > constraint.Width;
        if (GetRow(filters) != (below ? 1 : 0))
        {
            SetRow(filters, below ? 1 : 0); SetColumnSpan(filters, below ? 2 : 1);
            filters.Margin = new(0, below ? 12 : 0, 0, 0);
        }
        return base.MeasureOverride(constraint);
    }
    private void UpdateState() => Tr.Bind(state, TextBlock.TextProperty, LoadedState ? "ui.loaded" : "ui.not_loaded");
}

/// <summary>
/// Lays the filters out in a row, the last one (the search box) filling the rest. When the search box would get less
/// than its minimum width, the last two (its label and the box) move to a line of their own.
/// </summary>
public sealed class FilterPanel : Panel
{
    internal const double FillMinimum = 240, LineGap = 12;
    private UIElement[] Shown => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
    private bool wrapped;
    /// <summary>The width every filter but the filling one needs on one line.</summary>
    internal double FixedWidth
    {
        get
        {
            var shown = Shown; var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
            foreach (var child in shown.SkipLast(1)) child.Measure(infinite);
            return shown.SkipLast(1).Sum(c => c.DesiredSize.Width);
        }
    }
    protected override Size MeasureOverride(Size available)
    {
        var shown = Shown;
        if (shown.Length == 0) return new();
        double fixedWidth = FixedWidth;
        wrapped = shown.Length > 2 && double.IsFinite(available.Width) && fixedWidth + FillMinimum > available.Width;
        double before = wrapped ? shown[^2].DesiredSize.Width : fixedWidth;
        double fill = double.IsFinite(available.Width) ? Math.Max(0, available.Width - before) : FillMinimum;
        shown[^1].Measure(new Size(fill, double.PositiveInfinity));
        var first = wrapped ? shown[..^2] : shown;
        double height = first.Max(c => c.DesiredSize.Height);
        if (wrapped) height += LineGap + Math.Max(shown[^2].DesiredSize.Height, shown[^1].DesiredSize.Height);
        return new(double.IsFinite(available.Width) ? available.Width : before + shown[^1].DesiredSize.Width, height);
    }
    protected override Size ArrangeOverride(Size final)
    {
        var shown = Shown;
        if (shown.Length == 0) return final;
        var first = wrapped ? shown[..^2] : shown;
        double line = first.Max(c => c.DesiredSize.Height), x = 0;
        foreach (var child in first.SkipLast(wrapped ? 0 : 1)) { child.Arrange(new Rect(x, 0, child.DesiredSize.Width, line)); x += child.DesiredSize.Width; }
        double y = 0;
        if (wrapped)
        {
            y = line + LineGap; x = 0; line = Math.Max(shown[^2].DesiredSize.Height, shown[^1].DesiredSize.Height);
            shown[^2].Arrange(new Rect(0, y, shown[^2].DesiredSize.Width, line)); x = shown[^2].DesiredSize.Width;
        }
        shown[^1].Arrange(new Rect(x, y, Math.Max(0, final.Width - x), line));
        return final;
    }
}
