using System.Collections.ObjectModel;

namespace SaveUltimate.Core.Saves;

/// <summary>A knowledge entry of the skill trees. <see cref="Grants"/> holds, per level, the skills that level grants.
/// <see cref="Observe"/> is the record watching it writes, for the skills learned by watching; they also carry their level 1
/// description (en, ko).</summary>
public sealed record SkillDefinition(uint Key, uint Skill, int Default, int Owner, uint[][][] Grants, uint? Observe, string[]? Description);
public sealed record SkillCatalog(Dictionary<uint, SkillDefinition> Skills, Dictionary<uint, string[]> Names)
{
    public static SkillCatalog Shared { get; } = Embedded.Load<SkillCatalog>("Skills.json");
}

// The game's KnowledgeLearnFromType in its own order (name table in CrimsonDesert.exe); saves keep one per knowledge as
// _learnFromTypeRaw. Investment writes none.
public enum LearnFrom : uint
{
    Dialog, DialogGlossary, Sequencer, Threat, Region, Cooking, Eavesdrop, FollowComplete, Login, ForceLockOn, Read, Tool, Zoom, Gimmick,
    Following, FollowManualLockOn, Battle, BattleComplete, Meditation, SkillPoint, LearnedPriorKnowledge, Shop, ResetKnowledgeLevel,
    RemoveKnowledge, MiniGame, None, Friendly
}

/// <summary>Skill levels as the game counts them (default skills and what learned levels grant included) and the learn-from
/// records.</summary>
public sealed record SkillState(IReadOnlyDictionary<uint, uint> Levels, IReadOnlyDictionary<uint, uint> Follow)
{
    public uint Level(uint key) => Levels.GetValueOrDefault(key);
    // Observed level 1 survives a skill reset, so these count too: Sequencer (Snap Kick and Escape learned in scripted fights)
    // and ResetKnowledgeLevel (a reset of an observed skill above level 1). LearnedPriorKnowledge or no record is lost on reset.
    public bool Observed(uint key) => SkillCatalog.Shared.Skills[key].Observe is not null && Level(key) > 0 &&
        (LearnFrom)Follow.GetValueOrDefault(key, (uint)LearnFrom.None) is LearnFrom.Sequencer or LearnFrom.FollowComplete or LearnFrom.FollowManualLockOn or LearnFrom.ResetKnowledgeLevel;

    internal static SkillState Read(byte[] raw, ParcLayout layout) => Read(raw, SaveRecords.Read(raw, layout, "KnowledgeSaveData"));

    internal static SkillState Read(byte[] raw, SaveObject block)
    {
        var follow = new Dictionary<uint, uint>();
        foreach (var element in block.Fields.Named("_learnedFollowLearnKnowledge").Elements("FollowLearnElementSaveData"))
        {
            ulong Value(string name, string type, int size)
            {
                var field = element.ChildFields.Named(name);
                if (field.TypeName != type || field.MetaSize != size || field.MetaKind is not 0 and not 2) throw new InvalidDataException("Unsupported skill record format.");
                return field.Present ? field.Scalar(raw, type, size) : 0;
            }
            uint key = checked((uint)Value("_knowledgeKey", "KnowledgeKey", 4));
            if (key == 0 || !follow.TryAdd(key, (uint)Value("_learnFromTypeRaw", "uint8", 1))) throw new InvalidDataException("Duplicate or invalid skill record.");
        }
        var levels = SaveDocument.KnowledgeLevels(raw, block);
        foreach (var (key, definition) in SkillCatalog.Shared.Skills)
        {
            if (levels.GetValueOrDefault(key) > definition.Grants.Length) throw new InvalidDataException("Invalid skill level.");
            if (definition.Default != 0) levels[key] = Math.Max(1, levels.GetValueOrDefault(key));
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var (key, definition) in SkillCatalog.Shared.Skills)
                foreach (var grant in definition.Grants.Take((int)levels.GetValueOrDefault(key)).SelectMany(g => g))
                    if (levels.GetValueOrDefault(grant[0]) < grant[1]) { levels[grant[0]] = grant[1]; changed = true; }
        } while (changed);
        return new(new ReadOnlyDictionary<uint, uint>(levels), new ReadOnlyDictionary<uint, uint>(follow));
    }

    /// <summary>What watching <paramref name="keys"/> writes: level 1 of the ones not learned yet with what it grants, and
    /// the records. A skill learned another way only gets the record (saves hold invested skills observed later); a granted
    /// skill with none gets LearnedPriorKnowledge, as natural saves hold it.</summary>
    internal (Dictionary<uint, uint> Levels, Dictionary<uint, uint> Follow) Observe(IEnumerable<uint> keys)
    {
        var levels = new Dictionary<uint, uint>(Levels);
        var changes = new Dictionary<uint, uint>();
        var follow = new Dictionary<uint, uint>();
        foreach (uint key in keys.Distinct())
        {
            if (SkillCatalog.Shared.Skills.GetValueOrDefault(key)?.Observe is not uint record) throw new InvalidOperationException("This skill cannot be learned by observation.");
            if (Observed(key)) continue;
            follow[key] = record;
            if (levels.GetValueOrDefault(key) > 0) continue;
            levels[key] = changes[key] = 1;
            foreach (var grant in SkillCatalog.Shared.Skills[key].Grants[0])
                if (levels.GetValueOrDefault(grant[0]) < grant[1])
                {
                    levels[grant[0]] = changes[grant[0]] = grant[1];
                    if (!Follow.ContainsKey(grant[0])) follow.TryAdd(grant[0], (uint)LearnFrom.LearnedPriorKnowledge);
                }
        }
        if (follow.Count == 0) throw new InvalidOperationException("This skill is already learned by observation.");
        return (changes, follow);
    }
}
