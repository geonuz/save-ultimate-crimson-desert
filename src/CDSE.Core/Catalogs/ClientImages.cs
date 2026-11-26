using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;
using CDSE.Core.Archives;
using CDSE.Core.Saves;

namespace CDSE.Core.Catalogs;

internal sealed record ImageTask(string Source, int[]? Crop, uint Width, uint Height);
internal sealed record ImageSource(string Identity, string Sha256);
internal sealed class ImageManifest
{
    public Dictionary<string, ImageTask> Records { get; set; } = [];
    public Dictionary<string, ImageSource> Sources { get; set; } = [];
    public List<string> Unresolved { get; set; } = [];
    public int TextureReads { get; set; }
    public int Decoded { get; set; }
    public int Written { get; set; }
}

internal static class ClientImages
{
    // CharacterInfo: the symbol of the character's list picture.
    private const int CharacterPortrait = 192;
    // The square of a 512×512 journal picture that every mercenary portrait in cd_image_portrait_00.xml takes (2.05.01).
    private static readonly int[] JournalFace = [127, 37, 256, 256];

    public static ImageManifest Extract(GameFiles game, Dictionary<uint, uint[]> icons,
        string stage, SyncManifest previous, IReadOnlySet<string> valid, Dictionary<string, CacheFile> files,
        IProgress<SyncProgress>? report, CancellationToken cancel)
    {
        Dictionary<uint, ClientRecord> Records(string name) => ClientTables.Records(game.Tables(name));
        var symbols = new Dictionary<uint, string>();
        // StringInfo's records are unnamed: each holds one symbol.
        foreach (var (key, record) in ClientTables.Records(game.Tables("stringinfo"), emptyNames: true))
            symbols[key] = new ClientReader(record.Bytes, record.Prefix).Text().ToLowerInvariant();
        // DLC archives add icons and replace base files with the same path.
        var owners = new Dictionary<string, GameArchive>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>();
        foreach (var layer in game.Layers("0012"))
            foreach (string path in layer.Index.Entries.Keys)
            {
                if (paths.Remove(path.ToLowerInvariant(), out var replaced)) owners.Remove(replaced);
                paths[path.ToLowerInvariant()] = path; owners[path] = layer;
            }
        var byName = paths.Keys.GroupBy(p => p[(p.LastIndexOf('/') + 1)..]).ToDictionary(g => g.Key, g => g.ToArray());
        string? Resolve(string value)
        {
            value = value.Replace('\\', '/').ToLowerInvariant();
            if (paths.TryGetValue(value, out var path)) return path;
            return byName.TryGetValue(value[(value.LastIndexOf('/') + 1)..], out var candidates) && candidates.Length == 1 ? paths[candidates[0]] : null;
        }
        var entries = new Dictionary<string, XElement>();
        foreach (string path in paths.Values.Where(p =>
            p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFileName(p).StartsWith("cd_icon_skill_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("cd_image_knowledgeimage_character", StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(p).StartsWith("cd_image_portrait_", StringComparison.OrdinalIgnoreCase))))
        {
            cancel.ThrowIfCancellationRequested();
            string xml = Encoding.UTF8.GetString(owners[path].Extract(path)).TrimStart('\uFEFF');
            using var reader = XmlReader.Create(new StringReader("<Root>" + xml + "</Root>"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            foreach (var node in XElement.Load(reader).Elements())
                if (node.Attribute("Name") is { } name && node.Attribute("Filename") is not null) entries[name.Value.ToLowerInvariant()] = node;
        }
        var result = new ImageManifest();
        void Add(string output, string? path, int[]? crop, uint width, uint height)
        {
            if (path is null) result.Unresolved.Add(output);
            else result.Records[output] = new(path, crop, width, height);
        }
        foreach (var (key, itemIcons) in icons)
        {
            if (key == 1000975) continue;
            string? path = itemIcons.Select(v => Resolve("ui/texture/icon/" + symbols.GetValueOrDefault(v, "") + ".dds")).FirstOrDefault(p => p is not null);
            Add($"items/{key}.webp", path, null, 256, 256);
        }
        var knowledge = Records("knowledgeinfo");
        // Fish and the profiles of companions and NPCs share the journal picture of their knowledge entry.
        foreach (uint key in FishingCatalog.Shared.Fish.Select(f => f.Key).Concat(KnowledgeCatalog.Shared.Characters.Values).Distinct())
        {
            if (!knowledge.TryGetValue(key, out var record)) continue;
            var r = new ClientReader(record.Bytes, record.Prefix + 38);
            entries.TryGetValue(symbols.GetValueOrDefault(r.U32(), ""), out var entry);
            Add($"knowledge/{key}.webp", entry is null ? null : Resolve(entry.Attribute("Filename")!.Value), null, 320, 240);
        }
        static int[] Rect(XElement entry) => entry.Attribute("GetRect")!.Value.Split(',').Select(int.Parse).ToArray() is { Length: 4 } rect ? rect : throw new InvalidDataException("Invalid image crop.");
        // The picture the game's own lists show a pet, mount or mercenary by: a cut-out per color for animals, and for people a
        // square of their journal picture named in cd_image_portrait_*.xml.
        var characters = Records("characterinfo");
        foreach (uint key in CompanionTypes.Shared.Characters.Keys.Where(k => CompanionTypes.Shared.Group(k) is "pet" or "horse" or "special" or "dedicated" or "wagon" or "ship" or "domestic" or "mercenary"))
        {
            if (!characters.TryGetValue(key, out var record)) continue;
            string symbol = symbols.GetValueOrDefault(new ClientReader(record.Bytes, record.Prefix + CharacterPortrait).U32(), "");
            if (entries.TryGetValue(symbol, out var portrait)) Add($"characters/{key}.webp", Resolve(portrait.Attribute("Filename")!.Value), Rect(portrait), 256, 256);
            else if (symbol.Length > 0) Add($"characters/{key}.webp", Resolve((symbol.StartsWith("itemicon_", StringComparison.Ordinal) ? "ui/texture/icon/" : "") + symbol + ".dds"), null, 256, 256);
            // A mercenary without one gets the same square of its journal picture, so the list shows every face alike. Some
            // wagons and ships have none either; they keep the journal picture.
            else if (CompanionTypes.Shared.Group(key) == "mercenary" && KnowledgeCatalog.Shared.Characters.TryGetValue(key, out uint journal) &&
                result.Records.TryGetValue($"knowledge/{journal}.webp", out var picture))
                Add($"characters/{key}.webp", picture.Source, [.. JournalFace, 512], 256, 256);
        }
        var aliases = Embedded.Load<Dictionary<string, Dictionary<string, string>>>("ClientImageAliases.json");
        var skills = Records("skill");
        foreach (var (key, skill) in SkillCatalog.Shared.Skills.Where(p => p.Value.Observe is not null))
        {
            string name = skills[skill.Skill].Name;
            if (name.StartsWith("Skill_")) name = name[6..];
            string shared = Regex.Replace(name, "^(Damian|Oongka|OongKa|Kliff)_", "");
            var entry = new[] { name, aliases["SKILL_ALIASES"].GetValueOrDefault(name, name), shared, aliases["SKILL_ALIASES"].GetValueOrDefault(shared, shared) }
                .Select(n => entries.GetValueOrDefault(("cd_icon_skill_" + n).ToLowerInvariant())).FirstOrDefault(e => e is not null);
            int[]? crop = null; string? path = null;
            if (aliases["SKILL_SPECIAL"].TryGetValue(shared, out var special)) path = Resolve($"ui/texture/image/skillimage/{special}.dds");
            else if (entry is not null)
            {
                path = Resolve(entry.Attribute("Filename")!.Value);
                crop = Rect(entry);
            }
            Add($"skills/{key}.png", path, crop, 96, 96);
        }
        int done = 0, reads = 0, decoded = 0, written = 0;
        var sources = new System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource>();
        var outputs = new System.Collections.Concurrent.ConcurrentDictionary<string, CacheFile>();
        // Each worker holds one decoded texture, so a few at a time keep the sync's peak memory bounded.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 6), CancellationToken = cancel };
        try { Parallel.ForEach(result.Records.GroupBy(p => p.Value.Source).ToArray(), options, Encode); }
        catch (AggregateException error)
        {
            cancel.ThrowIfCancellationRequested();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerExceptions[0]);
        }
        foreach (var (name, source) in sources) result.Sources[name] = source;
        foreach (var (name, file) in outputs) files[name] = file;
        (result.TextureReads, result.Decoded, result.Written) = (reads, decoded, written);
        return result;

        void Encode(IGrouping<string, KeyValuePair<string, ImageTask>> group)
        {
            var archive = owners[group.Key]; var entry = archive.Index.Entries[group.Key]; var chunk = new FileInfo(Path.Combine(archive.Root, $"{entry.ChunkId}.paz"));
            string identity = $"{chunk.Length}:{chunk.LastWriteTimeUtc.Ticks}:{entry.Offset}:{entry.CompressedSize}:{entry.UncompressedSize}:{entry.Flags}";
            previous.Images.Sources.TryGetValue(group.Key, out var oldSource);
            byte[]? raw = null;
            if (oldSource?.Identity != identity) { raw = archive.Extract(group.Key); Interlocked.Increment(ref reads); }
            string digest = raw is null ? oldSource!.Sha256 : GameDataSync.Hash(raw);
            sources[group.Key] = new(identity, digest);
            MagickImage? picture = null;
            try
            {
                var variants = new Dictionary<string, (byte[] Bytes, string Hash)>();
                foreach (var (name, task) in group)
                {
                    cancel.ThrowIfCancellationRequested();
                    string relative = "images/" + name;
                    if (valid.Contains(relative) && oldSource?.Sha256 == digest && previous.Images.Records.TryGetValue(name, out var oldTask) &&
                        task.Source == oldTask.Source && task.Width == oldTask.Width && task.Height == oldTask.Height &&
                        (task.Crop ?? []).SequenceEqual(oldTask.Crop ?? []))
                        outputs[relative] = previous.Files[relative];
                    else
                    {
                        string variant = $"{string.Join(",", task.Crop ?? [])}:{task.Width}:{task.Height}:{Path.GetExtension(name)}";
                        if (!variants.TryGetValue(variant, out var encoded))
                        {
                            if (picture is null)
                            {
                                if (raw is null) { raw = archive.Extract(group.Key); Interlocked.Increment(ref reads); }
                                picture = new MagickImage(raw); Interlocked.Increment(ref decoded);
                            }
                            using var image = picture.Clone();
                            if (task.Crop is { } rect)
                            {
                                // A fifth value is the size of the square the rect was measured on; the texture may be another size.
                                if (rect.Length == 5) rect = rect[..4].Select((v, i) => (int)((long)v * (i % 2 == 0 ? image.Width : image.Height) / rect[4])).ToArray();
                                if (rect[0] < 0 || rect[1] < 0 || rect[2] <= 0 || rect[3] <= 0 || (long)rect[0] + rect[2] > image.Width || (long)rect[1] + rect[3] > image.Height)
                                    throw new InvalidDataException("Image crop exceeds texture.");
                                image.Crop(new MagickGeometry(rect[0], rect[1], (uint)rect[2], (uint)rect[3])); image.ResetPage();
                            }
                            if (image.Width > task.Width || image.Height > task.Height) image.Thumbnail(task.Width, task.Height);
                            // Lossy q90 is a quarter to a sixth of lossless WebP for pictures shown at 32-180 px; alpha stays lossless.
                            // Skill icons stay PNG (zlib level 1): they are few and are trimmed by their alpha.
                            image.Settings.SetDefine(MagickFormat.WebP, "method", 4);
                            image.Quality = name.EndsWith(".png") ? 10u : 90u;
                            byte[] bytes = image.ToByteArray(name.EndsWith(".webp") ? MagickFormat.WebP : MagickFormat.Png);
                            encoded = (bytes, GameDataSync.Hash(bytes)); variants[variant] = encoded;
                        }
                        string target = Path.Combine(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, encoded.Bytes);
                        outputs[relative] = CacheFile.For(target, encoded.Hash); Interlocked.Increment(ref written);
                    }
                }
            }
            finally { picture?.Dispose(); }
            report?.Report(new("sync.images", Interlocked.Add(ref done, group.Count()), result.Records.Count));
        }
    }
}
