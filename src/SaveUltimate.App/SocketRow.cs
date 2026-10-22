using CommunityToolkit.Mvvm.ComponentModel;

namespace SaveUltimate.App;

public sealed record GemChoice(uint Key, string Name, string Category = "", string CanonicalName = "", string CanonicalCategory = "");
public sealed record UnlockChoice(int Count, bool Enabled);

public partial class SocketRow : ObservableObject
{
    private GemChoice[] all;
    private readonly int slot;
    private readonly bool locked;
    public string Label => Tr.Format("port.socket", ("number", slot + 1)) + (locked ? Tr.Text(" (locked)") : !IsEditable ? Tr.Text(" (empty)") : "");
    public bool IsEditable { get; }
    public string[] Categories => ItemCatalog.Shared.GemCategories;
    [ObservableProperty] private uint selectedKey;
    [ObservableProperty] private string category = Tr.Text("map.all");
    [ObservableProperty] private string search = "";
    [ObservableProperty] private GemChoice[] choices = [];

    public SocketRow(int slot, uint key, bool editable, bool locked, SocketRow? previous = null)
    {
        this.slot = slot; this.locked = locked;
        IsEditable = editable;
        selectedKey = key;
        all = ItemCatalog.Shared.Gems;
        if (!all.Any(g => g.Key == key)) all = [.. all, new(key, ItemCatalog.Shared.Name(key))];
        category = previous?.Category ?? Tr.Text("map.all");
        search = previous?.Search ?? "";
        Filter();
    }

    public void RefreshLanguage()
    {
        string canonical = all.FirstOrDefault(g => g.Category == Category)?.CanonicalCategory ?? "";
        all = ItemCatalog.Shared.Gems;
        if (!all.Any(g => g.Key == SelectedKey)) all = [.. all, new(SelectedKey, ItemCatalog.Shared.Name(SelectedKey))];
        Category = canonical.Length == 0 ? Tr.Text("map.all") : Tr.Text(canonical);
        OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(Categories)); OnPropertyChanged(nameof(Category)); Filter();
    }

    partial void OnCategoryChanged(string value) => Filter();
    partial void OnSearchChanged(string value) => Filter();
    private void Filter()
    {
        uint selected = SelectedKey;
        string query = Search.Trim();
        var filtered = all.Where(g => g.Key == 0 || (Category == Tr.Text("map.all") || g.Category == Category) &&
            (g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || g.CanonicalName.Contains(query, StringComparison.OrdinalIgnoreCase) || g.Key.ToString().Contains(query) || Category == Tr.Text("map.all") &&
                (g.Category.Contains(query, StringComparison.OrdinalIgnoreCase) || g.CanonicalCategory.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .Select(g => Category == Tr.Text("map.all") && g.Category.Length > 0 ? g with { Name = $"{g.Name}  [{g.Category}]" } : g).ToList();
        if (!filtered.Any(g => g.Key == selected) && all.FirstOrDefault(g => g.Key == selected) is { } current)
            filtered.Add(current with { Name = current.Name + Tr.Text(" (selected)") });
        Choices = filtered.ToArray();
        // ComboBox can clear SelectedValue while replacing its items.
        SelectedKey = selected;
    }
}
