using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CDSE.Core.Catalogs;

namespace CDSE.App;

public sealed record GemChoice(uint Key, string Name, string Category = "", string CanonicalName = "", string CanonicalCategory = "")
{
    // What the gear does, in one line, and the game's fuller text for a tooltip. Read when shown: the knowledge text they
    // come from reads item names from the catalog that lists the gears.
    public string Effect => Key == 0 ? "" : ItemCatalog.Shared.GemEffect(Key);
    public string Detail => Key == 0 ? "" : ItemCatalog.Shared.GemDetail(Key);
}
public sealed record UnlockChoice(int Count, bool Enabled);

/// <summary>One socket of the selected equipment and the gear chosen for it, applied with the others by Apply sockets.</summary>
public partial class SocketRow : ObservableObject
{
    private readonly int slot;
    private readonly bool locked;
    public uint Equipment { get; }
    public string Label => Tr.Format("port.socket", ("number", slot + 1)) + (locked ? Tr.Text(" (locked)") : !IsEditable ? Tr.Text(" (empty)") : "");
    public bool IsEditable { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Name), nameof(Icon), nameof(CanClear), nameof(Effect), nameof(Detail))] private uint selectedKey;
    public string Name => SelectedKey == 0 ? Tr.Text("companion.gear_none") : ItemCatalog.Shared.Name(SelectedKey);
    public ImageSource? Icon => SelectedKey == 0 ? null : ItemImages.Get(SelectedKey, 96);
    public string Effect => SelectedKey == 0 ? "" : ItemCatalog.Shared.GemEffect(SelectedKey);
    public string? Detail => SelectedKey == 0 ? null : ItemCatalog.Shared.GemDetail(SelectedKey) is { Length: > 0 } detail ? detail : null;
    public bool CanClear => IsEditable && SelectedKey != 0;
    public string ChangeLabel => $"{Tr.Text("socket.change")} · {Label}";
    public string ClearLabel => $"{Tr.Text("socket.clear")} · {Label}";

    public SocketRow(uint equipment, int slot, uint key, bool editable, bool locked)
    {
        Equipment = equipment; this.slot = slot; this.locked = locked;
        IsEditable = editable;
        selectedKey = key;
    }

    /// <summary>What the game lets this equipment hold, and the gear already in the socket whatever it is.</summary>
    public ItemSearchWindow.Choice[] Choices()
    {
        var gears = ItemCatalog.Shared.Gems.Where(g => g.Key != 0 && (g.Key == SelectedKey || ItemRules.Fits(Equipment, g.Key))).ToList();
        if (SelectedKey != 0 && !gears.Any(g => g.Key == SelectedKey)) gears.Add(new(SelectedKey, ItemCatalog.Shared.Name(SelectedKey)));
        return gears.Select(g => new ItemSearchWindow.Choice(g.Key, g.Name, g.Category, g.CanonicalName, key => ItemImages.Get(key, 96)) { Effect = g.Effect, Detail = g.Detail.Length > 0 ? g.Detail : null }).ToArray();
    }

    public void RefreshLanguage()
    {
        foreach (string name in new[] { nameof(Label), nameof(Name), nameof(ChangeLabel), nameof(ClearLabel), nameof(Effect), nameof(Detail) }) OnPropertyChanged(name);
    }
}
