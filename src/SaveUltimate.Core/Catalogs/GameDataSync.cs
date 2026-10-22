using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SaveUltimate.Core.Archives;

namespace SaveUltimate.Core.Catalogs;

public sealed record SyncProgress(string Stage, int Completed = 0, int Total = 0);
internal sealed record CacheFile(string Sha256, long Length, long Modified)
{
    public static CacheFile For(string path, string hash) { var info = new FileInfo(path); return new(hash, info.Length, info.LastWriteTimeUtc.Ticks); }
}
internal sealed class SyncManifest
{
    public int Format { get; set; } = 3;
    public string Fingerprint { get; set; } = "";
    public string MetadataFingerprint { get; set; } = "";
    public Dictionary<string, CacheFile> Files { get; set; } = [];
    public ImageManifest Images { get; set; } = new();
    public int Items { get; set; }
}
public sealed record SyncResult(int Items, int Added, int Changed, int Images, int Unresolved, int Decoded, int Written, bool Reused, double Seconds);

public static class GameDataSync
{
    private static readonly string[] Metadata = ["data/item_names.json", "data/item_details.json", "data/max_enchant_map.json",
        "locale/names_ko.json", "locale/names_ja.json", "locale/names_ru.json"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    internal static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static SortedDictionary<string, string> Identity(string game, string resources)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string group in new[] { "0008", "0012", "0019", "0020", "0021", "0022" })
        {
            string root = Path.Combine(game, group);
            result[group + "/0.pamt"] = FileHash(Path.Combine(root, "0.pamt"));
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.paz")) result[group + "/" + file.Name] = $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }
        foreach (string path in Metadata.Where(p => p != "data/max_enchant_map.json")) result["resource/" + path] = FileHash(Path.Combine(resources, path));
        result["schema"] = "csharp-sync-3";
        return result;
    }
    private static SyncManifest ReadManifest(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SyncManifest>(File.ReadAllBytes(path), Json);
            if (saved is null || saved.Format != 3 || saved.Files is null || saved.Images?.Records is null || saved.Images.Sources is null || saved.Images.Unresolved is null ||
                saved.Images.Records.Values.Any(v => v is null) ||
                !Metadata.All(saved.Files.ContainsKey) || !saved.Files.Keys.Except(Metadata).Order().SequenceEqual(saved.Images.Records.Keys.Select(n => "images/" + n).Order()) ||
                saved.Files.Any(p => !Metadata.Contains(p.Key) && !Regex.IsMatch(p.Key, @"^images/(items/\d+\.webp|(knowledge|skills)/\d+\.png)$") ||
                    p.Value is null || !Regex.IsMatch(p.Value.Sha256 ?? "", "^[0-9a-f]{64}$"))) return new();
            return saved;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    private static void NoLinks(string path)
    {
        for (var info = new DirectoryInfo(path); info is not null; info = info.Parent)
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cache folders must not be links.");
    }
    public static SyncResult Run(string game, string resources, string destination, IProgress<SyncProgress>? progress = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        game = Path.GetFullPath(game); resources = Path.GetFullPath(resources); destination = Path.GetFullPath(destination);
        if (destination.Equals(game, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(resources, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(resources + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The cache must be separate from game files and bundled resources.");
        NoLinks(destination);
        string parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
        using var syncLock = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var watch = Stopwatch.StartNew(); progress?.Report(new("sync.metadata"));
        var inputs = Identity(game, resources);
        string fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(inputs));
        string metadataFingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(inputs.Where(p => !p.Key.StartsWith("0012/"))));
        var saved = ReadManifest(Path.Combine(destination, "manifest.json")); var valid = new HashSet<string>();
        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, record) in saved.Files)
        {
            cancel.ThrowIfCancellationRequested();
            string path = Path.Combine(destination, name), directory = Path.GetDirectoryName(path)!;
            if (checkedDirectories.Add(directory)) NoLinks(directory);
            var file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            // As in Python, unchanged image size/time avoids rereading thousands of images.
            if ((Metadata.Contains(name) || file.Length != record.Length || file.LastWriteTimeUtc.Ticks != record.Modified) && FileHash(path) != record.Sha256) continue;
            valid.Add(name);
        }
        if (saved.Fingerprint == fingerprint && valid.Count == saved.Files.Count && Metadata.All(valid.Contains))
            return new(saved.Items, 0, 0, saved.Images.Records.Count, saved.Images.Unresolved.Count, 0, 0, true, watch.Elapsed.TotalSeconds);
        var archives = new Dictionary<string, GameArchive>();
        GameArchive Archive(string group)
        {
            if (!archives.TryGetValue(group, out var archive)) archives[group] = archive = new(Path.Combine(game, group));
            return archive;
        }
        byte[] Read(string group, string path)
        {
            cancel.ThrowIfCancellationRequested();
            return Archive(group).Extract(path);
        }
        int items = saved.Items, added = 0, changed = 0;
        Dictionary<string, JsonNode> outputs = saved.MetadataFingerprint == metadataFingerprint && Metadata.All(valid.Contains) ? [] :
            ClientMetadata.Build(Read, resources, destination, valid, out items, out added, out changed);
        string stage = Path.Combine(parent, ".game-sync-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            var files = saved.Files.Where(p => Metadata.Contains(p.Key) && valid.Contains(p.Key)).ToDictionary();
            foreach (var (name, data) in outputs)
            {
                string path = Path.Combine(stage, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = File.Create(path)) JsonSerializer.Serialize(stream, data);
                files[name] = CacheFile.For(path, FileHash(path));
            }
            var details = outputs.GetValueOrDefault("data/item_details.json") ?? JsonNode.Parse(File.ReadAllBytes(Path.Combine(destination, "data/item_details.json")))!;
            var icons = details["items"]!.AsObject().ToDictionary(p => uint.Parse(p.Key), p => p.Value!["icons"]!.AsArray().Select(v => v!.GetValue<uint>()).ToArray());
            details = null; outputs.Clear();
            var images = ClientImages.Extract(game, Archive("0012"), Read, icons, stage, saved, valid, files, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            if (fingerprint != Hash(JsonSerializer.SerializeToUtf8Bytes(Identity(game, resources)))) throw new IOException("Game files changed during synchronization. Retry after the update.");
            var manifest = new SyncManifest { Fingerprint = fingerprint, MetadataFingerprint = metadataFingerprint, Files = files, Images = images, Items = items };
            File.WriteAllBytes(Path.Combine(stage, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            progress?.Report(new("sync.publishing")); Publish(stage, destination);
            return new(items, added, changed, images.Records.Count, images.Unresolved.Count, images.Decoded, images.Written, false, watch.Elapsed.TotalSeconds);
        }
        finally
        {
            if (!Path.GetFullPath(stage).StartsWith(parent + Path.DirectorySeparatorChar + ".game-sync-", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid synchronization staging path.");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
    internal static void Publish(string stage, string destination)
    {
        // A new cache can publish the complete sibling staging directory in one move.
        if (!Directory.Exists(destination)) { Directory.Move(stage, destination); return; }
        string recovery = stage + "-recovery"; Directory.CreateDirectory(recovery);
        var committed = new List<(string Target, string Backup, bool Existed)>(); bool keep = false;
        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string source in Directory.GetFiles(stage, "*", SearchOption.AllDirectories).OrderBy(p => Path.GetFileName(p) == "manifest.json"))
            {
                string relative = Path.GetRelativePath(stage, source), target = Path.Combine(destination, relative), backup = Path.Combine(recovery, relative);
                string directory = Path.GetDirectoryName(target)!;
                if (checkedDirectories.Add(directory)) { Directory.CreateDirectory(directory); NoLinks(directory); }
                bool exists = File.Exists(target);
                if (exists)
                {
                    if (File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cache files must not be links.");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup);
                }
                File.Move(source, target, true); committed.Add((target, backup, exists));
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var (target, backup, existed) in committed.AsEnumerable().Reverse())
                try { if (existed) File.Move(backup, target, true); else File.Delete(target); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(error); }
            if (errors.Count > 0) { keep = true; throw new AggregateException($"Cache publication and rollback failed. Recovery files: {recovery}", new[] { failure }.Concat(errors)); }
            throw;
        }
        finally
        {
            if (!keep && Path.GetFullPath(recovery) == Path.GetFullPath(stage) + "-recovery") Directory.Delete(recovery, true);
        }
    }
}
