using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class SkillPage : UserControl
{
    private sealed record Choice(uint Key, string Name);
    private sealed record Node(Button Button, uint Key, Point Position, Brush Color, bool Changed, bool Matches);
    private readonly int owner;
    private readonly List<Node> nodes = [];
    private readonly MatrixTransform transform = new();
    private MainViewModel? model;
    private uint? root, key;
    private bool rendering, first = true;
    private bool? below;
    private Point? pan;
    private Rect bounds;
    private SkillCatalog Catalog => SkillCatalog.Shared;
    public SkillPage(int owner)
    {
        this.owner = owner;
        InitializeComponent();
        Tree.RenderTransform = transform;
        Loaded += (_, _) =>
        {
            model = DataContext as MainViewModel;
            if (model is not null) model.PropertyChanged += OnModelChanged;
            Render();
        };
        // As in the original, reset the view once the tree first has a laid-out size.
        Viewport.SizeChanged += (_, _) => { if (first) first = !Fit(true); };
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= OnModelChanged; };
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Skills) or nameof(MainViewModel.SkillBaseline)) Render();
        if (e.PropertyName == nameof(MainViewModel.Busy)) UpdateDetail();
    }
    private void OnFilter(object sender, SelectionChangedEventArgs e) { if (Tree is not null) Render(); }
    private void OnSearch(object sender, TextChangedEventArgs e) { if (Tree is not null) Render(); }
    private void Render()
    {
        if (rendering) return;
        rendering = true;
        try
        {
            nodes.Clear(); Tree.Children.Clear();
            var state = model?.Skills;
            var definitions = Catalog.Trees[owner].Where(n => n.Key is not 40906 and not 1001051).ToArray();
            var positions = definitions.ToDictionary(n => n.Id, n => new Point(n.Xy[0], n.Xy[1]));
            int unplaced = 0;
            foreach (var n in definitions.Where(n => n.Key is not null && n.Xy[0] == 0 && n.Xy[1] == 0)) positions[n.Id] = new(unplaced++ * 260, 0);
            foreach (var definition in definitions)
            {
                var position = positions[definition.Id];
                var color = new SolidColorBrush(Color.FromArgb((byte)(definition.Color[3] * 255),
                    (byte)(definition.Color[0] * 255), (byte)(definition.Color[1] * 255), (byte)(definition.Color[2] * 255)));
                foreach (var guide in definition.Guides)
                    if (positions.TryGetValue(guide.Parent, out var parent))
                        Tree.Children.Add(new Line { X1 = parent.X, Y1 = parent.Y, X2 = position.X, Y2 = position.Y, Stroke = color, StrokeThickness = 2, Opacity = .45, IsHitTestVisible = false });
                if (definition.Key is not uint nodeKey) continue;
                var skill = Catalog.Skills[nodeKey];
                uint[] related = Catalog.Related(nodeKey);
                uint[] watchKeys = related.Where(k => Catalog.Skills[k].Watch).ToArray();
                bool observed = state is not null && watchKeys.Length > 0 && watchKeys.All(state.Observed);
                bool changed = state is not null && related.Any(k => state.Level(k) != model?.SkillBaseline?.GetValueOrDefault(k));
                bool available = state is not null && TryPlan(state, nodeKey, false, out _);
                bool matches = (Filter.SelectedIndex == 0 || watchKeys.Length > 0) && related.Any(k => ItemCatalog.Shared.KnowledgeName(k).Contains(Search.Text, StringComparison.OrdinalIgnoreCase));
                var image = SkillIcon(nodeKey, 36);
                var button = new Button
                {
                    Width = 40, Height = 40, Padding = new(0), Foreground = Brushes.White,
                    Background = state?.Level(nodeKey) > 0 ? new SolidColorBrush(Color.FromRgb((byte)(color.Color.R / 2.8), (byte)(color.Color.G / 2.8), (byte)(color.Color.B / 2.8))) : new SolidColorBrush(Color.FromRgb(32, 39, 51)),
                    BorderBrush = changed ? Brushes.DeepSkyBlue : color, BorderThickness = new(changed ? 4 : 2),
                    Template = (ControlTemplate)Resources["NodeTemplate"], Tag = nodeKey,
                    Content = image is null ? new TextBlock { Text = "◇", FontSize = 23 } : new Image { Source = image, Width = 36, Height = 36 },
                    Opacity = matches ? 1 : .18, IsEnabled = matches,
                    ToolTip = $"{ItemCatalog.Shared.KnowledgeName(nodeKey)}\n{state?.Level(nodeKey).ToString() ?? "—"} / {skill.Levels.Length}" +
                        (available ? Tr.Text("\nAvailable to invest") : Tr.Text("\nCannot invest yet")) + (changed ? Tr.Text("\nChanged") : "") +
                        string.Concat(watchKeys.Select(k => $"\n{ItemCatalog.Shared.KnowledgeName(k)}: {(state?.Observed(k) == true ? Tr.Text("skills.observed_short") : Tr.Text("Not observed"))}"))
                };
                System.Windows.Automation.AutomationProperties.SetName(button, ItemCatalog.Shared.KnowledgeName(nodeKey));
                button.Click += (_, _) => Select(nodeKey);
                Place(button, position.X - 20, position.Y - 20);
                if (changed)
                {
                    button.ApplyTemplate();
                    ((Ellipse)button.Template.FindName("Ring", button)).StrokeDashArray = new([2, 1]);
                }
                nodes.Add(new(button, nodeKey, position, color, changed, matches));
                Label($"{state?.Level(nodeKey).ToString() ?? "—"} / {skill.Levels.Length}" + (changed ? " *" : ""), position.X - 40, position.Y + 22, 80, changed ? Brushes.DeepSkyBlue : Brushes.White);
                if (available) Label("+", position.X - 30, position.Y - 35, 20, Brushes.DeepSkyBlue);
                if (watchKeys.Length > 0) Label(observed ? "◉" : "◎", position.X + 10, position.Y - 35, 20, observed ? Brushes.LightGreen : Brushes.LightGray);
            }
            bounds = nodes.Count == 0 ? new(0, 0, 1, 1) : new Rect(new Point(nodes.Min(n => n.Position.X) - 65, nodes.Min(n => n.Position.Y) - 50),
                new Point(nodes.Max(n => n.Position.X) + 65, nodes.Max(n => n.Position.Y) + 60));
            Counts.Text = Tr.Count(nodes.Count, nodes.Count(n => n.Matches));
            Balance.Text = state is null ? Tr.Text("Load a save file.") :
                Tr.Format("port.skill_balance", ("artifacts", state.Inventory.GetValueOrDefault(75001u).ToString("N0")), ("bonds", state.Points[owner].Current.ToString("N0")), ("spent", state.Paid.Values.Where(p => p.Owner == owner).Sum(p => (decimal)p.Spent).ToString("N0")));
            if (root is not null && !nodes.Any(n => n.Key == root && n.Matches)) root = key = null;
            UpdateRelated();
        }
        finally { rendering = false; }
        UpdateDetail();
    }
    private static System.Windows.Media.Imaging.BitmapImage? GameIcon(uint key, uint size) =>
        ItemImages.GetFile(System.IO.Path.Combine(AppContext.BaseDirectory, "game_data", "images", "skills", $"{key}.png"), size, trim: true);
    // Tree nodes need a glyph to be clickable; the detail panel shows only real game icons.
    private static System.Windows.Media.Imaging.BitmapImage? SkillIcon(uint key, uint size) => GameIcon(key, size)
        ?? ItemImages.GetFile(System.IO.Path.Combine(AppContext.BaseDirectory, "resources", "assets", "skill-generic.svg"), size);
    private void Place(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); Tree.Children.Add(element); }
    private void Label(string text, double x, double y, double width, Brush color) =>
        Place(new TextBlock { Text = text, Width = width, TextAlignment = TextAlignment.Center, Foreground = color, FontSize = 13, IsHitTestVisible = false }, x, y);
    private static bool TryPlan(SkillState state, uint key, bool watch, out string text)
    {
        try
        {
            var plan = SkillPlan.Create(state, key, watch ? 1 : state.Level(key) + 1, watch);
            text = watch ? "" : Tr.Format("port.skill_cost", ("artifacts", plan.Artifacts), ("bonds", plan.Bonds), ("remaining", plan.Points[plan.Owner].Current)) +
                string.Concat(plan.Costs.Where(p => p.Key != 75001 && p.Value > 0).Select(p => $"\n{ItemCatalog.Shared.Name(p.Key)} × {p.Value}"));
            return true;
        }
        catch (InvalidOperationException error) { text = error.Message; return false; }
    }
    private void Select(uint selected)
    {
        root = selected;
        rendering = true; UpdateRelated(); rendering = false;
        UpdateDetail();
    }
    private void UpdateRelated()
    {
        uint? previous = key;
        var choices = root is uint r ? Catalog.Related(r).Where(k => Filter.SelectedIndex == 0 || Catalog.Skills[k].Watch)
            .Select(k => new Choice(k, ItemCatalog.Shared.KnowledgeName(k) + (Catalog.Skills[k].Watch ? model?.Skills?.Observed(k) == true ? Tr.Text(" · Observed") : Tr.Text(" · Not observed") : ""))).ToArray() : [];
        Related.ItemsSource = choices;
        Related.SelectedItem = choices.FirstOrDefault(c => c.Key == previous) ?? choices.FirstOrDefault();
        key = (Related.SelectedItem as Choice)?.Key;
        Related.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnRelated(object sender, SelectionChangedEventArgs e)
    {
        if (rendering) return;
        key = (Related.SelectedItem as Choice)?.Key;
        UpdateDetail();
    }
    private void UpdateDetail()
    {
        if (Invest is null) return;
        Invest.Visibility = Watch.Visibility = Visibility.Collapsed;
        Invest.IsEnabled = Watch.IsEnabled = false;
        Description.Text = Requirements.Text = Preview.Text = WatchPreview.Text = Current.Text = "";
        Icon.Source = null;
        ObserveBadge.Visibility = Visibility.Collapsed; WatchSummary.Text = "";
        Title.Text = Tr.Text("Select a skill.");
        var missing = new HashSet<uint>();
        if (key is uint selected)
        {
            var definition = Catalog.Skills[selected];
            var state = model?.Skills;
            uint current = state?.Level(selected) ?? 0;
            int target = Math.Min((int)current + 1, definition.Levels.Length);
            var level = definition.Levels[target - 1];
            Title.Text = ItemCatalog.Shared.KnowledgeName(selected);
            Current.Text = state is null ? Tr.Text("Load a save file.") : Tr.Format("port.levels", ("level", current), ("max", definition.Levels.Length));
            Description.Text = Tr.Name("skill_descriptions", $"{selected}:{target}", level.Description[Tr.Language == "ko" ? 1 : 0]);
            Icon.Source = GameIcon(selected, 64);
            bool invest = current < definition.Levels.Length && level.Routes.Any(r => r.Mode == 3);
            bool watch = current == 0 && definition.Watch;
            Invest.Visibility = invest ? Visibility.Visible : Visibility.Collapsed;
            Watch.Visibility = watch ? Visibility.Visible : Visibility.Collapsed;
            var routes = level.Routes.Where(r => r.Mode == 3).ToArray();
            if (routes.Length == 0) routes = level.Routes.Where(r => r.Mode is 0 or 5).ToArray();
            var requirements = routes.SelectMany(r => r.Requires.Concat(r.Unlocks)).Select(r => (Key: r[0], Level: r[1])).Distinct().OrderBy(r => r.Key).ToArray();
            Requirements.Text = requirements.Length == 0 ? "" : Tr.Text("Requirements\n") + string.Join("\n", requirements.Select(r => $"{ItemCatalog.Shared.KnowledgeName(r.Key)} · {r.Level}"));
            if (state is not null)
            {
                missing = requirements.Where(r => state.Level(r.Key) < r.Level).Select(r => r.Key).ToHashSet();
                if (invest) { bool valid = TryPlan(state, selected, false, out string text); Preview.Text = text; Invest.IsEnabled = valid && model?.Busy == false; }
                if (watch) { bool valid = TryPlan(state, selected, true, out string text); WatchPreview.Text = text; Watch.IsEnabled = valid && model?.Busy == false; }
                if (definition.Watch)
                {
                    bool observed = state.Observed(selected);
                    string tone = observed ? "success" : "warning";
                    ObserveText.Text = Tr.Text(observed ? "skills.observed_short" : "skills.unobserved_short");
                    ObserveText.SetResourceReference(TextBlock.ForegroundProperty, tone); ObserveBadge.SetResourceReference(Border.BorderBrushProperty, tone);
                    ObserveBadge.Visibility = Visibility.Visible;
                }
                // Every node with linked skills learned by observation shows the same count its ◎/◉ mark is based on.
                uint[] related = root is uint r ? Catalog.Related(r) : [];
                uint[] watchKeys = related.Where(k => Catalog.Skills[k].Watch).ToArray();
                if (related.Length > 1 && watchKeys.Length > 0)
                {
                    int done = watchKeys.Count(state.Observed);
                    WatchSummary.Text = Tr.Format("skills.watch_summary", ("done", done), ("total", watchKeys.Length));
                    WatchSummary.SetResourceReference(TextBlock.ForegroundProperty, done == watchKeys.Length ? "success" : "warning");
                }
            }
        }
        foreach (var node in nodes)
        {
            bool required = Catalog.Related(node.Key).Any(missing.Contains);
            node.Button.BorderBrush = node.Key == root ? Brushes.White : node.Changed ? Brushes.DeepSkyBlue : required ? Brushes.Gold : node.Color;
            node.Button.BorderThickness = new(node.Key == root || node.Changed || required ? 4 : 2);
            node.Button.IsEnabled = (node.Matches || required) && model?.Busy != true;
            node.Button.Opacity = node.Matches || required ? 1 : .18;
        }
    }
    private async void OnInvest(object sender, RoutedEventArgs e) { if (key is uint k && model is not null) await model.LearnSkillAsync(k, false); }
    private async void OnWatch(object sender, RoutedEventArgs e) { if (key is uint k && model is not null) await model.LearnSkillAsync(k, true); }
    public bool Fit(bool reset)
    {
        if (Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1 || bounds.IsEmpty) return false;
        double scale = Math.Min(Viewport.ActualWidth / bounds.Width, Viewport.ActualHeight / bounds.Height);
        if (reset) scale = Math.Max(1, scale);
        var center = reset && nodes.FirstOrDefault(n => n.Key == root) is { } node ? node.Position : new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        transform.Matrix = new(scale, 0, 0, scale, Viewport.ActualWidth / 2 - center.X * scale, Viewport.ActualHeight / 2 - center.Y * scale);
        return true;
    }
    private void OnFit(object sender, RoutedEventArgs e) => Fit(false);
    private void OnReset(object sender, RoutedEventArgs e) => Fit(true);
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var matrix = transform.Matrix;
        if (matrix.M11 * factor is > .15 and < 3)
        {
            var point = e.GetPosition(Viewport); matrix.ScaleAt(factor, factor, point.X, point.Y); transform.Matrix = matrix;
        }
        e.Handled = true;
    }
    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Viewport && e.OriginalSource != Tree) return;
        pan = e.GetPosition(Viewport); Viewport.CaptureMouse(); e.Handled = true;
    }
    private void OnPanEnd(object sender, MouseButtonEventArgs e) { pan = null; Viewport.ReleaseMouseCapture(); }
    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (pan is not Point previous) return;
        var point = e.GetPosition(Viewport); var matrix = transform.Matrix;
        matrix.Translate(point.X - previous.X, point.Y - previous.Y); transform.Matrix = matrix; pan = point;
    }
    private void OnSplitSize(object sender, SizeChangedEventArgs e)
    {
        bool next = e.NewSize.Width < 900;
        if (below == next) return;
        below = next;
        Split.ColumnDefinitions[1].Width = new(next ? 0 : 5);
        Split.ColumnDefinitions[2].Width = new(next ? 0 : 310);
        // Stacked like the original DetailSplit: tree and details share 420:240, and the tree keeps a usable height.
        Split.RowDefinitions[0].Height = new(next ? 420 : 1, GridUnitType.Star);
        Split.RowDefinitions[0].MinHeight = next ? Viewport.MinHeight : 0;
        Split.RowDefinitions[1].Height = new(next ? 5 : 0);
        Split.RowDefinitions[2].Height = next ? new(240, GridUnitType.Star) : new(0);
        Grid.SetColumn(Details, next ? 0 : 2); Grid.SetRow(Details, next ? 2 : 0);
        Grid.SetColumn(Splitter, next ? 0 : 1); Grid.SetRow(Splitter, next ? 1 : 0);
        Splitter.ResizeDirection = next ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        Splitter.Width = next ? double.NaN : 5; Splitter.Height = next ? 5 : double.NaN;
    }
}
