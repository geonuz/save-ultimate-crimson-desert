using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CDSE.Core.Catalogs;
using CDSE.Core.Saves;

namespace CDSE.App;

/// <summary>What a dye file holds and, entry by entry, which owned item it goes to or whether a new one is made.</summary>
public partial class DyeImportWindow : Window
{
    /// <summary>An owned item an entry can dye, with where it is, its enhancement and its dye now; Item is null for a new one.</summary>
    public sealed record Option(InventoryItem? Item, string Text, string Enchant, Brush[] Swatches);

    public sealed partial class Row(DyeEntry entry, Option[] options, string? problem, Action<Row, Option?> chosen) : ObservableObject
    {
        public DyeEntry Entry { get; } = entry;
        public Option[] Options { get; } = options;
        public string? Problem { get; } = problem;
        public bool Possible => Problem is null;
        // Only checked entries are imported: an unchecked one leaves every item as it is, whatever its target says.
        [ObservableProperty, NotifyPropertyChangedFor(nameof(Result))] private bool isChecked;
        [ObservableProperty, NotifyPropertyChangedFor(nameof(Result))] private Option? choice;
        partial void OnChoiceChanged(Option? oldValue, Option? newValue) => chosen(this, oldValue);
        public string Name => ItemCatalog.Shared.InternalName(Entry.Item).Length > 0 ? ItemCatalog.Shared.Name(Entry.Item) : $"{Entry.Name} ({Entry.Item})";
        public Brush[] Swatches => DyeText.Swatches(Entry.Parts);
        public ImageSource? Icon => ItemImages.Get(Entry.Item, 64);
        public string Result => Problem is { } problem ? Tr.Text("dye.problem." + problem)
            : !IsChecked ? Tr.Text("dye.result.skip")
            : Choice?.Item is null ? Tr.Text("dye.create") : Tr.Text(Entry.Parts.Length == 0 ? "dye.result.clear" : "dye.result.apply");
    }
    private readonly Row[] rows;
    private readonly MainViewModel model;
    private bool ready;
    public DyeTarget[] Targets => rows.Where(r => r.IsChecked && r.Possible && r.Choice is not null).Select(r => new DyeTarget(r.Entry, r.Choice!.Item, null)).ToArray();

    public DyeImportWindow(MainViewModel model, IReadOnlyList<DyeEntry> entries)
    {
        this.model = model;
        var owned = model.DyeCandidates();
        var dyes = model.ReadDyes().DistinctBy(d => (d.Item.Number, d.Item.Key)).ToDictionary(d => (d.Item.Number, d.Item.Key), d => d.Parts);
        Option[] Options(DyeEntry entry) =>
        [
            .. owned.Where(i => i.Key == entry.Item).Select(i => new Option(i, DyeText.Where(i), i.EnchantLevel is ushort level ? $"+{level}" : "",
                DyeText.Swatches(dyes.GetValueOrDefault((i.Number, i.Key)) ?? []))),
            .. ItemRules.CanAdd(entry.Item) ? new[] { new Option(null, Tr.Text("dye.target.new"), "", []) } : []
        ];
        // Alone, an entry is only refused for its parts or for an item the player has none of and cannot make.
        rows = entries.Select(e => model.PlanDyes([e], true)[0].Problem is { } problem ? new Row(e, [], problem, Chosen) : new Row(e, Options(e), null, Chosen)).ToArray();
        InitializeComponent();
        PictureColumn.Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => (Content as FrameworkElement)?.SetResourceReference(LayoutTransformProperty, "UiTransform");
        // Owned items go to the entries in file order, worn ones first; an entry without dye would take the dye off an item the
        // player has, so that one is asked for. An entry left without a free item starts unchecked.
        foreach (var (row, target) in rows.Zip(model.PlanDyes(entries, true)).Where(p => p.First.Possible))
        {
            row.Choice = target.Problem is null ? row.Options.First(o => o.Item == target.Item) : Free(row);
            row.IsChecked = target.Problem is null && (row.Entry.Parts.Length > 0 || target.Item is null);
        }
        Rows.ItemsSource = rows; ready = true;
        Plan();
    }

    private Row? Holder(InventoryItem item, Row except) => rows.FirstOrDefault(r => r != except && r.IsChecked && r.Choice?.Item == item);
    // An owned item no other checked entry has, else a new one, else what the row has.
    private Option? Free(Row row) => row.Options.FirstOrDefault(o => o.Item is { } item && Holder(item, row) is null) ?? row.Options.FirstOrDefault(o => o.Item is null) ?? row.Choice;

    // Picking an item another entry has swaps the two. The target can only be picked on a checked entry.
    private bool swapping;
    private void Chosen(Row row, Option? previous)
    {
        if (!ready || swapping) return;
        swapping = true;
        try
        {
            row.IsChecked = row.Possible;
            if (row.Choice?.Item is { } item && Holder(item, row) is { } other)
                other.Choice = other.Options.FirstOrDefault(o => o.Item == previous?.Item && (o.Item is null || Holder(o.Item, other) is null)) ?? Free(other);
        }
        finally { swapping = false; }
        Plan();
    }
    private void Plan()
    {
        var targets = Targets;
        int created = targets.Count(t => t.Item is null);
        var space = model.InventorySpace();
        bool shared = targets.Where(t => t.Item is not null).GroupBy(t => t.Item).Any(g => g.Count() > 1);
        Accept.IsEnabled = targets.Length > 0 && !shared && space.Used + created <= space.Total;
        Summary.Text = Tr.Format("dye.summary", ("count", (targets.Length - created).ToString("N0")), ("added", created.ToString("N0"))) + (created > 0 ? "\n" + MainViewModel.SpaceText(space, created) : "");
    }
    // A checked entry whose item another one has takes a free one.
    private void Check(Row row)
    {
        swapping = true;
        try { if (row.IsChecked && row.Choice?.Item is { } item && Holder(item, row) is not null) row.Choice = Free(row); }
        finally { swapping = false; }
        Plan();
    }
    private void OnCheck(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is Row row) Check(row); }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is ComboBox || Rows.SelectedItem is not Row { Possible: true } row) return;
        row.IsChecked = !row.IsChecked; Check(row); e.Handled = true;
    }
    private void OnAccept(object sender, RoutedEventArgs e) { if (Accept.IsEnabled) DialogResult = true; }
}
