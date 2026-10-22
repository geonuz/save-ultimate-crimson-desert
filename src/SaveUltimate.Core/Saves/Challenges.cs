using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveUltimate.Core.Saves;

public sealed record ChallengeGroup(uint Key, string Name, string NameKo, string InternalName, uint[] Quests);
public sealed record ChallengeQuest(uint Key, string Name, string NameKo, string InternalName, uint[]? Missions);
public sealed record ChallengeObjective(uint Target, uint Artifact, uint[]? Goals);
public sealed record ChallengeMission(uint Key, uint Quest, string Name, string NameKo, string InternalName, uint[] Children, ChallengeObjective? ObjectiveEdit);
public sealed record ChallengeShape(bool Branched,
    [property: JsonPropertyName("_completeCount")] uint? Count,
    [property: JsonPropertyName("_uiState")] byte? Ui);
public sealed record ChallengeCompletion(uint[] Roots, Dictionary<uint, ChallengeShape> Missions, Dictionary<uint, byte> Quests, Dictionary<string, string> ClientSources);
public sealed record ChallengeCatalog(ChallengeGroup[] Groups, Dictionary<uint, ChallengeQuest> Quests,
    Dictionary<uint, ChallengeMission> Missions, Dictionary<string, string> SourceSha256)
{
    internal static T Load<T>(string name)
    {
        using var stream = typeof(ChallengeCatalog).Assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    public static ChallengeCatalog Shared { get; } = Load<ChallengeCatalog>("Challenges.json");
    public static ChallengeCompletion Completion { get; } = Load<ChallengeCompletion>("ChallengeCompletion.json");
    public uint[] Descendants(uint root)
    {
        var result = new List<uint>();
        var seen = new HashSet<uint>();
        void Visit(uint key)
        {
            if (!seen.Add(key) || !Missions.TryGetValue(key, out var mission))
                throw new InvalidDataException("Invalid challenge hierarchy.");
            result.Add(key);
            foreach (uint child in mission.Children) Visit(child);
        }
        Visit(root);
        return result.ToArray();
    }
}

public sealed record JournalRecord(byte? State, ulong? Completed, ulong? Branched, uint? Count = null, byte? Ui = null);
public sealed record ChallengeState(IReadOnlyDictionary<uint, JournalRecord> Quest, IReadOnlyDictionary<uint, JournalRecord> Mission)
{
    internal static ChallengeState Read(byte[] raw, SaveObject block)
    {
        Dictionary<uint, JournalRecord> ReadList(bool mission)
        {
            var list = block.Fields.Named(mission ? "_missionStateList" : "_questStateList");
            if (!list.Present) throw new InvalidDataException("Missing journal list.");
            var result = new Dictionary<uint, JournalRecord>();
            foreach (var row in list.Elements(mission ? "MissionStateData" : "QuestStateData"))
            {
                ulong? Value(string name, string type, int size)
                {
                    var field = row.ChildFields.Named(name);
                    return field.Present ? field.Scalar(raw, type, size) : null;
                }
                uint key = checked((uint)row.ChildFields.Named(mission ? "_key" : "_questKey").Scalar(raw, mission ? "MissionKey" : "QuestKey", 4));
                if (!result.TryAdd(key, new((byte?)Value("_state", "QuestStateType", 1),
                    Value("_completedTime", "uint64", 8), Value("_branchedTime", "uint64", 8),
                    mission ? (uint?)Value("_completeCount", "uint32", 4) : null,
                    mission ? (byte?)Value("_uiState", "MissionUIState", 1) : null)))
                    throw new InvalidDataException("Duplicate journal identity.");
            }
            return result;
        }
        return new(ReadList(false), ReadList(true));
    }
}

public sealed partial class SaveDocument
{
    public ChallengeState ReadChallenges() => ChallengeState.Read(blob, SaveRecords.ReadLists(blob, Layout, "QuestSaveData", "_questStateList", "_missionStateList"));

    public (ChallengeState State, int Completed) CompleteChallenges(uint[] roots, uint[]? checkpoints = null)
    {
        var (edits, expected, count) = PlanChallenges(blob, Layout, roots, checkpoints);
        if (count == 0) return (expected, 0);
        byte[] candidate = RecordLists.Rewrite(blob, Layout, EditBuffer, edits.ToArray());
        var snapshots = PrepareSnapshots(blob, candidate);
        ChallengeState after = null!;
        Commit(candidate, _ =>
        {
            after = ChallengeState.Read(candidate, SaveRecords.ReadLists(candidate, Layout.Reparse(candidate, blob), "QuestSaveData", "_questStateList", "_missionStateList"));
            if (!expected.Quest.OrderBy(p => p.Key).SequenceEqual(after.Quest.OrderBy(p => p.Key)) ||
                !expected.Mission.OrderBy(p => p.Key).SequenceEqual(after.Mission.OrderBy(p => p.Key)))
                throw new InvalidDataException("Challenge completion verification failed.");
        }, changed: ["QuestSaveData"]);
        undo.Push((null, snapshots.Before, snapshots.After, ["QuestSaveData"]));
        return (after, count);
    }
    private static (List<ListEdit> Edits, ChallengeState Expected, int Count) PlanChallenges(byte[] raw, ParcLayout layout, uint[] roots, uint[]? checkpoints = null)
    {
        var catalog = ChallengeCatalog.Shared;
        var reference = ChallengeCatalog.Completion;
        if (!reference.ClientSources.OrderBy(p => p.Key).SequenceEqual(catalog.SourceSha256.OrderBy(p => p.Key)) ||
            !reference.Missions.Keys.Order().SequenceEqual(catalog.Missions.Keys.Order()))
            throw new InvalidDataException("Challenge definitions do not match the catalog.");
        if (roots.Length == 0 || roots.Distinct().Count() != roots.Length || roots.Any(k => !reference.Roots.Contains(k)))
            throw new InvalidOperationException("서로 다른 도전과제를 선택하세요.");
        var keys = roots.SelectMany(catalog.Descendants).ToHashSet();
        if (checkpoints is not null)
        {
            if (roots.Length != 1 || checkpoints.Length == 0 || checkpoints.Distinct().Count() != checkpoints.Length ||
                checkpoints.Any(k => !keys.Contains(k) || roots.Contains(k) || catalog.Missions[k].Children.Length != 0))
                throw new InvalidOperationException("이 도전과제에 속하는 서로 다른 체크포인트를 선택하세요.");
            keys = checkpoints.ToHashSet();
        }
        var block = SaveRecords.ReadLists(raw, layout, "QuestSaveData", "_questStateList", "_missionStateList");
        var before = ChallengeState.Read(raw, block);
        uint[] pending = keys.Where(k => before.Mission.GetValueOrDefault(k)?.State != 5).ToArray();
        if (pending.Length == 0) return ([], before, 0);
        ulong timestamp = before.Quest.Values.Concat(before.Mission.Values).SelectMany(r => new[] { r.Completed ?? 0, r.Branched ?? 0 }).DefaultIfEmpty().Max();
        if (timestamp == 0 || timestamp >= 1UL << 63) throw new InvalidDataException("사용할 수 있는 퀘스트 시각 기록이 없습니다.");
        var missions = new Dictionary<uint, JournalRecord>(before.Mission);
        var quests = new Dictionary<uint, JournalRecord>(before.Quest);
        var changes = new Dictionary<uint, Dictionary<string, ScalarValue>>();
        foreach (uint key in pending)
        {
            var row = before.Mission.GetValueOrDefault(key);
            if (row?.State is > 4) throw new InvalidDataException("Unsupported challenge state.");
            var shape = reference.Missions[key];
            var fields = new Dictionary<string, ScalarValue>
            {
                ["_key"] = new("MissionKey", 4, key), ["_state"] = new("QuestStateType", 1, 5),
                ["_completedTime"] = new("uint64", 8, timestamp),
                ["_delayedTime"] = ScalarValue.Remove, ["_delayedFromMissionKey"] = ScalarValue.Remove,
                ["_completeCount"] = shape.Count is uint count ? new("uint32", 4, count) : ScalarValue.Remove,
                ["_uiState"] = shape.Ui is byte ui ? new("MissionUIState", 1, ui) : ScalarValue.Remove
            };
            ulong? branched = row?.Branched;
            if (branched is null && shape.Branched) { branched = timestamp; fields["_branchedTime"] = new("uint64", 8, timestamp); }
            changes.Add(key, fields);
            missions[key] = new(5, timestamp, branched, shape.Count, shape.Ui);
        }
        var questChanges = new Dictionary<uint, Dictionary<string, ScalarValue>>();
        foreach (uint key in checkpoints is null ? roots.Select(r => catalog.Missions[r].Quest).Distinct() : [])
        {
            bool complete = catalog.Quests[key].Missions!.All(k => missions.GetValueOrDefault(k)?.State == 5);
            byte desired = complete ? reference.Quests[key] : (byte)2;
            var row = before.Quest.GetValueOrDefault(key);
            if (row is not null && (desired != 5 || row.State == 5)) continue;
            var fields = new Dictionary<string, ScalarValue> { ["_questKey"] = new("QuestKey", 4, key), ["_state"] = new("QuestStateType", 1, desired) };
            if (desired == 5) fields["_completedTime"] = new("uint64", 8, timestamp);
            questChanges.Add(key, fields);
            quests[key] = new(desired, desired == 5 ? timestamp : null, row?.Branched);
        }
        var edits = new List<ListEdit> { new(block.Fields.Named("_missionStateList"), "MissionStateData", "_key", changes) };
        if (questChanges.Count > 0) edits.Add(new(block.Fields.Named("_questStateList"), "QuestStateData", "_questKey", questChanges));
        return (edits, new ChallengeState(quests, missions), pending.Length);
    }
}

