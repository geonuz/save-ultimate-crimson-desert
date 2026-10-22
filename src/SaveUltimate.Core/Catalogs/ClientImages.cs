using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;
using SaveUltimate.Core.Archives;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Core.Catalogs;

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
    public static ImageManifest Extract(string game, GameArchive archive, Func<string, string, byte[]> read, Dictionary<uint, uint[]> icons,
        string stage, SyncManifest previous, IReadOnlySet<string> valid, Dictionary<string, CacheFile> files,
        IProgress<SyncProgress>? report, CancellationToken cancel)
    {
        (byte[], byte[]) Table(string name) => (read("0008", $"gamedata/binarystaticinfo__/bin/{name}.staticinfoheader"),
            read("0008", $"gamedata/binarystaticinfo__/bin/{name}.staticinfobody"));
        Dictionary<uint, ClientRecord> Records(string name) { var (h, b) = Table(name); return ClientTables.Records(h, b); }
        var (header, body) = Table("stringinfo"); var index = new ClientReader(header);
        var symbols = new Dictionary<uint, string>(); int count = index.U16();
        if (index.Remaining != count * 8) throw new InvalidDataException("Invalid symbol index.");
        for (int i = 0; i < count; i++)
        {
            uint key = index.U32(); int offset = checked((int)index.U32()); var r = new ClientReader(body, offset);
            if (r.U32() != key || !r.Take(5).SequenceEqual(new byte[5]) || !symbols.TryAdd(key, r.Text().ToLowerInvariant()))
                throw new InvalidDataException("Invalid image symbol.");
        }
        var paths = archive.Index.Entries.Keys.ToDictionary(p => p.ToLowerInvariant());
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
            (Path.GetFileName(p).StartsWith("cd_icon_skill_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("cd_image_knowledgeimage_character_", StringComparison.OrdinalIgnoreCase))))
        {
            string xml = Encoding.UTF8.GetString(read("0012", path)).TrimStart('\uFEFF');
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
        foreach (var fish in FishingCatalog.Shared.Fish)
        {
            var record = knowledge[fish.Key]; var r = new ClientReader(record.Bytes, record.Prefix);
            entries.TryGetValue(symbols.GetValueOrDefault(r.U32(), ""), out var entry);
            Add($"knowledge/{fish.Key}.png", entry is null ? null : Resolve(entry.Attribute("Filename")!.Value), null, 320, 240);
        }
        using var aliasStream = typeof(ClientImages).Assembly.GetManifestResourceStream("ClientImageAliases.json")!;
        var aliases = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(aliasStream)!;
        var skills = Records("skill");
        foreach (var (key, skill) in SkillCatalog.Shared.Skills)
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
                crop = entry.Attribute("GetRect")!.Value.Split(',').Select(int.Parse).ToArray();
                if (crop.Length != 4) throw new InvalidDataException("Invalid skill crop.");
            }
            Add($"skills/{key}.png", path, crop, 96, 96);
        }
        int done = 0;
        foreach (var group in result.Records.GroupBy(p => p.Value.Source))
        {
            cancel.ThrowIfCancellationRequested();
            var entry = archive.Index.Entries[group.Key]; var chunk = new FileInfo(Path.Combine(game, "0012", $"{entry.ChunkId}.paz"));
            string identity = $"{chunk.Length}:{chunk.LastWriteTimeUtc.Ticks}:{entry.Offset}:{entry.CompressedSize}:{entry.UncompressedSize}:{entry.Flags}";
            previous.Images.Sources.TryGetValue(group.Key, out var oldSource);
            byte[]? raw = null;
            if (oldSource?.Identity != identity) { raw = archive.Extract(group.Key); result.TextureReads++; }
            string digest = raw is null ? oldSource!.Sha256 : GameDataSync.Hash(raw);
            result.Sources[group.Key] = new(identity, digest);
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
                        files[relative] = previous.Files[relative];
                    else
                    {
                        string variant = $"{string.Join(",", task.Crop ?? [])}:{task.Width}:{task.Height}:{Path.GetExtension(name)}";
                        if (!variants.TryGetValue(variant, out var encoded))
                        {
                            if (picture is null)
                            {
                                if (raw is null) { raw = archive.Extract(group.Key); result.TextureReads++; }
                                picture = new MagickImage(raw); result.Decoded++;
                            }
                            using var image = picture.Clone();
                            if (task.Crop is { } rect)
                            {
                                if (rect[0] < 0 || rect[1] < 0 || rect[2] <= 0 || rect[3] <= 0 || (long)rect[0] + rect[2] > image.Width || (long)rect[1] + rect[3] > image.Height)
                                    throw new InvalidDataException("Image crop exceeds texture.");
                                image.Crop(new MagickGeometry(rect[0], rect[1], (uint)rect[2], (uint)rect[3])); image.ResetPage();
                            }
                            if (image.Width > task.Width || image.Height > task.Height) image.Thumbnail(task.Width, task.Height);
                            image.Settings.SetDefine(MagickFormat.WebP, "lossless", true); image.Settings.SetDefine(MagickFormat.WebP, "method", 0);
                            byte[] bytes = image.ToByteArray(name.EndsWith(".webp") ? MagickFormat.WebP : MagickFormat.Png);
                            encoded = (bytes, GameDataSync.Hash(bytes)); variants[variant] = encoded;
                        }
                        string target = Path.Combine(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, encoded.Bytes);
                        files[relative] = CacheFile.For(target, encoded.Hash); result.Written++;
                    }
                    done++;
                }
            }
            finally { picture?.Dispose(); }
            report?.Report(new("sync.images", done, result.Records.Count));
        }
        return result;
    }
}
