using System.Windows;
using System.Windows.Controls;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MapPage : UserControl
{
    // Actions are read by index on every refresh: their DLC-free form depends on the game install.
    private readonly List<(int Index, TextBlock Label, Border Badge, TextBlock Count, Button Button)> actions = [];
    private MainViewModel Model => (MainViewModel)DataContext;
    public MapPage()
    {
        InitializeComponent();
        foreach (string group in new[] { "resources", "abyss", "crafting", "collectibles" })
        {
            var body = new StackPanel { Margin = new(12) };
            foreach (var pair in MapAction.All.Select((a, i) => (a, i)).Where(p => p.a.Group == group && p.a.Mode != "knowledge" && p.a.SceneNames is null))
            {
                var row = new Grid { Margin = new(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                // Badges name what the group also changes in the adventure journal: its knowledge or its challenges.
                // Fixed captions are bound, not copied, so they follow a language switch.
                Border Badge(string? text, string? hint)
                {
                    var content = new TextBlock { FontSize = 12 }; content.SetResourceReference(TextBlock.ForegroundProperty, "accent");
                    var border = new Border { CornerRadius = new(9), Padding = new(8, 1, 8, 1), BorderThickness = new(1), Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = content };
                    if (text is not null) Tr.Bind(content, TextBlock.TextProperty, text);
                    if (hint is not null) Tr.Bind(border, ToolTipProperty, hint);
                    border.SetResourceReference(Border.BorderBrushProperty, "accent"); DockPanel.SetDock(border, Dock.Right);
                    return border;
                }
                var badge = Badge(null, null); badge.Visibility = Visibility.Collapsed;
                var name = new DockPanel(); name.Children.Add(badge);
                if (pair.a.ChallengeRoots is { Length: > 0 }) name.Children.Add(Badge("map.challenge_badge", "map.challenge_badge_hint"));
                name.Children.Add(label);
                var count = new TextBlock { MinWidth = 70, Margin = new(8, 0, 8, 0), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Tr.Bind(count, ToolTipProperty, "map.registered");
                var button = new Button { Style = (Style)FindResource("AccentButton"), MinWidth = 100, IsEnabled = false, Tag = pair.i };
                Tr.Bind(button, ContentProperty, "map.activate_all");
                button.Click += async (_, _) => await Model.ActivateMapAsync(pair.i);
                Grid.SetColumn(count, 1); Grid.SetColumn(button, 2); row.Children.Add(name); row.Children.Add(count); row.Children.Add(button); body.Children.Add(row);
                actions.Add((pair.i, label, badge, count, button));
            }
            var card = new GroupBox { Content = body, Margin = new(0, 0, 0, 12) }; Tr.Bind(card, HeaderedContentControl.HeaderProperty, "map.group_" + group);
            (group is "resources" ? LeftCards : RightCards).Children.Add(card);
        }
        Ui.Follow(this, nameof(MainViewModel.Map), Refresh);
    }
    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool below = ActualWidth < 1100;
        Cards.ColumnDefinitions[1].Width = new(below ? 0 : 16);
        Cards.ColumnDefinitions[2].Width = below ? new(0) : new(1, GridUnitType.Star);
        Grid.SetColumn(RightCards, below ? 0 : 2); Grid.SetRow(RightCards, below ? 1 : 0);
    }
    private void Refresh()
    {
        var state = Model.Map;
        foreach (var (index, label, badge, countLabel, button) in actions)
        {
            var a = MapAction.All[index];
            bool Learned(uint k) => state?.Knowledge.GetValueOrDefault(k) >= a.TargetLevel(k);
            uint[] counted = a.CountKeys ?? a.Keys;
            int total = counted.Length, count = counted.Count(Learned);
            bool complete = state is not null && a.Keys.All(Learned);
            if (a.Mode == "icons")
            {
                total = a.Scenes.Length;
                count = a.Scenes.Count(s => state?.Discoveries.TryGetValue(s, out var record) == true && (a.Pivots is null || record.Pivot is not null));
                complete &= count == total;
            }
            string name = MapRow.ActionName(a);
            bool partial = a.JournalKeys.Length < counted.Length;
            badge.Visibility = a.JournalKeys.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)badge.Child).Text = Tr.Text(partial ? "map.journal_badge_partial" : "map.journal_badge");
            int journal = a.JournalKeys.Count(Learned);
            badge.ToolTip = !partial ? Tr.Text("map.journal_badge_hint") : state is null ? null
                : Tr.Format("map.journal_counts", ("count", journal), ("total", a.JournalKeys.Length), ("other", count - journal), ("other_total", total - a.JournalKeys.Length));
            label.Text = name; countLabel.Text = state is null ? "—" : $"{count}/{total}";
            if (a.ChallengeRoots is not null) complete &= a.ChallengeRoots.All(k => state?.Progress.Mission.GetValueOrDefault(k)?.State == 5);
            button.IsEnabled = state is not null && !complete;
        }
        Reveal.IsEnabled = state is not null && !state.Fog.Full;
        FogStatus.Text = Tr.Text(state is null ? "map.load_prompt" : state.Fog.Full ? "map.fog_full" : state.Fog.Empty ? "map.fog_empty" : "map.fog_partial");
    }
}
