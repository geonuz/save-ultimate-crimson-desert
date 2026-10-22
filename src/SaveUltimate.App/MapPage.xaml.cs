using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MapPage : UserControl
{
    private readonly List<(MapAction Action, TextBlock Label, TextBlock Count, Button Button)> actions = [];
    private MainViewModel Model => (MainViewModel)DataContext;
    public MapPage()
    {
        InitializeComponent();
        foreach (string group in new[] { "places", "abyss", "collectibles", "crafting", "resources" })
        {
            var body = new StackPanel { Margin = new(12) };
            body.Children.Add(new TextBlock { Text = Tr.Text("map.effect_" + group), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
            foreach (var pair in MapAction.All.Select((a, i) => (a, i)).Where(p => p.a.Group == group))
            {
                var row = new Grid { Margin = new(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                var count = new TextBlock { MinWidth = 70, Margin = new(8, 0, 8, 0), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = Tr.Text("map.registered") };
                var button = new Button { Content = Tr.Text("map.activate_all"), Style = (Style)FindResource("AccentButton"), MinWidth = 100, IsEnabled = false, Tag = pair.i };
                button.Click += async (_, _) => await Model.ActivateMapAsync(pair.i);
                Grid.SetColumn(count, 1); Grid.SetColumn(button, 2); row.Children.Add(label); row.Children.Add(count); row.Children.Add(button); body.Children.Add(row);
                actions.Add((pair.a, label, count, button));
                if (pair.a.ChallengeRoots is { Length: > 0 }) body.Children.Add(new TextBlock { Text = Tr.Text("map.effect_cresset"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) });
            }
            (group is "places" or "resources" ? LeftCards : RightCards).Children.Add(new GroupBox { Header = Tr.Text("map.group_" + group), Content = body, Margin = new(0, 0, 0, 12) });
        }
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Map)) Refresh(); }
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
        bool Learned(uint k) => state?.Knowledge.GetValueOrDefault(k) > 0;
        foreach (var (a, label, countLabel, button) in actions)
        {
            uint[] counted = a.CountKeys ?? a.Keys;
            int total = counted.Length, count = counted.Count(Learned);
            bool complete = state is not null && a.Keys.All(Learned);
            if (a.Mode == "icons")
            {
                total = a.Scenes.Length;
                count = a.Scenes.Count(s => state?.Discoveries.TryGetValue(s, out var record) == true && (a.Pivots is null || record.Pivot is not null));
                complete &= count == total;
            }
            string name = a.LabelKey is not null ? Tr.Text(a.LabelKey) : GameText.Display(a.Names.GetValueOrDefault(Tr.Language, a.Names["en"]));
            if (a.LabelRef is { Length: 2 } reference) name = GameText.Display(Tr.Name(reference[0].GetString()!, reference[1].ToString(), name));
            if (state is not null && a.Mode == "locations" && a.JournalKeys.Length != total)
            {
                int journal = a.JournalKeys.Count(Learned);
                name += "\n" + Tr.Format("map.journal_counts", ("count", journal), ("total", a.JournalKeys.Length), ("other", count - journal), ("other_total", total - a.JournalKeys.Length));
            }
            label.Text = name; countLabel.Text = state is null ? "—" : $"{count}/{total}";
            if (a.ChallengeRoots is not null) complete &= a.ChallengeRoots.All(k => state?.Progress.Mission.GetValueOrDefault(k)?.State == 5);
            button.IsEnabled = state is not null && !complete;
        }
        Reveal.IsEnabled = state is not null && !state.Fog.Full;
        FogStatus.Text = Tr.Text(state is null ? "map.load_prompt" : state.Fog.Full ? "map.fog_full" : state.Fog.Empty ? "map.fog_empty" : "map.fog_partial");
    }
}
