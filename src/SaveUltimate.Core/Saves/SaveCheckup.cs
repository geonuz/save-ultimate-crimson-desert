using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Core.Saves;

public enum CheckStatus { Ok, Suspect, Error, NotRun }
public enum SaveFix { None, Integrity, MissionRows, Enchants }

// Id names the check for the screen; Detail is the reason in the words the failing code used.
public sealed record SaveCheck(string Id, CheckStatus Status, string Detail = "", int Count = 0, SaveFix Fix = SaveFix.None);

// Runs every check this editor knows on one file, without changing it. Ok means the save follows the known rules, not that the game
// will load it. A check that needs an earlier one to pass reports NotRun rather than failing on bytes nothing could read.
public static class SaveCheckup
{
    public static readonly IReadOnlyList<(string Group, string[] Ids)> Plan =
    [
        ("file", ["container", "integrity", "compression"]),
        ("structure", ["schema", "blocks", "pointers", "mission_rows"]),
        ("data", ["game_version", "items", "skills", "knowledge", "challenges", "fishing", "map", "companions", "friendship"]),
        ("values", ["enchants", "sockets", "unknown_items"])
    ];
    private static readonly string[] order = Plan.SelectMany(g => g.Ids).ToArray();

    public static SaveCheck[] Run(string path, string testedGameVersion, Action<SaveCheck>? report = null)
    {
        var results = new Dictionary<string, SaveCheck>();
        void Add(SaveCheck check) { results[check.Id] = check; report?.Invoke(check); }
        // False when the checks after this one cannot run.
        bool Pass(string id, Func<SaveCheck?> run, CheckStatus thrown = CheckStatus.Error)
        {
            try
            {
                var finding = run() ?? new(id, CheckStatus.Ok);
                Add(finding);
                return finding.Status != CheckStatus.Error;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Add(new(id, thrown, error.Message));
                return thrown != CheckStatus.Error;
            }
        }

        byte[] file = [], raw = [];
        SaveContainer.Opened opened = null!;
        ParcLayout layout = null!;
        int damaged = 0;
        SaveDocument document = null!;
        bool readable = Pass("container", () => { file = File.ReadAllBytes(path); opened = SaveContainer.Open(file); return null; });
        bool signed = false;
        if (readable)
        {
            signed = opened.SignedBy(file);
            // Later checks still run: they decide whether the contents can be trusted enough to sign again.
            Add(signed ? new("integrity", CheckStatus.Ok) : new("integrity", CheckStatus.Error, SaveContainer.BadSignature));
        }
        readable = readable
            && Pass("compression", () => { raw = opened.Expand(); return null; })
            && Pass("schema", () => { layout = ParcLayout.Parse(raw); return null; })
            && Pass("blocks", () => ParcReader.BlockErrors(raw, layout) is { Length: > 0 } errors ? new("blocks", CheckStatus.Error, string.Join("\n", errors), errors.Length) : null)
            && Pass("pointers", () => { damaged = ParcReader.Validate(raw, layout).DamagedRows; return null; });
        if (readable)
        {
            Add(damaged == 0 ? new("mission_rows", CheckStatus.Ok) : new("mission_rows", CheckStatus.Error, Count: damaged, Fix: SaveFix.MissionRows));
            // Only a file whose every record reads can be signed again; anything less would seal unknown damage in.
            if (!signed && opened.Version == 2) Add(new("integrity", CheckStatus.Error, SaveContainer.BadSignature, Fix: SaveFix.Integrity));
        }

        if (readable && Pass("game_version", () => GameVersionFinding(SaveDocument.SavedGameVersion(raw, layout), testedGameVersion))
            && Pass("items", () => { document = new SaveDocument(path, file, raw); return null; }))
        {
            ChallengeState? challenges = null;
            void Page(string id, Action read) => Pass(id, () => { read(); return null; }, CheckStatus.Suspect);
            Page("skills", () => document.ReadSkills());
            Page("knowledge", () => document.ReadKnowledge());
            Page("challenges", () => challenges = document.ReadChallenges());
            Page("fishing", () => document.ReadFishing());
            Page("map", () => document.ReadMap(challenges));
            Page("companions", () => document.ReadCompanions());
            Page("friendship", () => document.ReadFriendship());
            var items = document.Items;
            Add(Values("enchants", items.Where(OverEnchant).ToArray(), items.Any(i => OverEnchant(i) && i.MaxEnchant > 0) ? SaveFix.Enchants : SaveFix.None));
            Add(Values("sockets", items.Where(OverSockets).ToArray(), SaveFix.None));
            Add(Values("unknown_items", items.Where(i => ItemRules.Stack(i.Key) == 0).ToArray(), SaveFix.None));
        }
        foreach (string id in order.Where(id => !results.ContainsKey(id))) Add(new(id, CheckStatus.NotRun));
        return order.Select(id => results[id]).ToArray();
    }

    private static SaveCheck GameVersionFinding(uint version, string tested)
    {
        if (version == 0) return new("game_version", CheckStatus.Ok);
        string text = $"{version >> 16}.{(version >> 8) & 255:00}.{version & 255:00}";
        uint[] parts = tested.Split('.').Select(uint.Parse).ToArray();
        return !SaveDocument.SupportedGameVersion(version) ? new("game_version", CheckStatus.Error, text)
            : version > (parts[0] << 16 | parts[1] << 8 | parts[2]) ? new("game_version", CheckStatus.Suspect, text)
            : new("game_version", CheckStatus.Ok, text);
    }

    private static SaveCheck Values(string id, InventoryItem[] found, SaveFix fix) => found.Length == 0 ? new(id, CheckStatus.Ok)
        : new(id, CheckStatus.Suspect, string.Join(", ", found.Select(i => i.Key).Distinct().Take(8)), found.Length, fix);

    // Stack size is deliberately not a rule: the catalog's MaxStack is exceeded by 280 items, silver among them, in every player's saves.
    internal static bool OverEnchant(InventoryItem item) => item.EnchantLevel is ushort level && item.MaxEnchant >= 0 && level > item.MaxEnchant;
    internal static bool OverSockets(InventoryItem item) => item.Sockets.Error is not null ||
        ItemRules.Stack(item.Key) > 0 && (item.Sockets.Unlocked > ItemRules.Sockets(item.Key) || item.Sockets.Slots.Count(s => s.HasGem) > ItemRules.Sockets(item.Key));
}
