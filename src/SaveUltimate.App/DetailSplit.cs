using System.Windows;
using System.Windows.Controls;

namespace SaveUltimate.App;

public sealed class DetailSplit : Grid
{
    private readonly GridSplitter splitter = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private bool? below;
    public DetailSplit()
    {
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = new(5) }); ColumnDefinitions.Add(new() { Width = new(340), MinWidth = 280 });
        RowDefinitions.Add(new()); RowDefinitions.Add(new() { Height = new(0) }); RowDefinitions.Add(new() { Height = new(0) });
        Loaded += (_, _) => { if (!Children.Contains(splitter)) Children.Add(splitter); ArrangePanels(ActualWidth); };
        SizeChanged += (_, e) => ArrangePanels(e.NewSize.Width);
    }
    private void ArrangePanels(double width)
    {
        if (Children.Count < 2 || Children[1] is not FrameworkElement detail) return;
        bool vertical = width < 900;
        if (below == vertical) return;
        below = vertical;
        ColumnDefinitions[1].Width = new(vertical ? 0 : 5);
        ColumnDefinitions[2].MinWidth = vertical ? 0 : 280;
        ColumnDefinitions[2].Width = new(vertical ? 0 : 340);
        RowDefinitions[0].Height = new(420, GridUnitType.Star);
        RowDefinitions[1].Height = new(vertical ? 5 : 0);
        RowDefinitions[2].Height = vertical ? new(240, GridUnitType.Star) : new(0);
        SetColumn(detail, vertical ? 0 : 2); SetRow(detail, vertical ? 2 : 0);
        SetColumn(splitter, vertical ? 0 : 1); SetRow(splitter, vertical ? 1 : 0);
        splitter.Width = vertical ? double.NaN : 5; splitter.Height = vertical ? 5 : double.NaN;
        splitter.ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        if (detail is ScrollViewer scroll) scroll.Padding = vertical ? new(0, 8, 0, 0) : new(12, 0, 0, 0);
    }
}
