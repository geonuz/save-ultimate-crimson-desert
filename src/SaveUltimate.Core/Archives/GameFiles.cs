namespace SaveUltimate.Core.Archives;

// The installed game: base groups plus DLC overlays in dlc/<name>/<group> whose files carry a ".<name>" infix,
// for example iteminfo.dlc_01.staticinfobody beside the base iteminfo.staticinfobody.
public sealed class GameFiles(string game, CancellationToken cancel = default)
{
    private readonly Dictionary<string, GameArchive?> archives = new(StringComparer.Ordinal);
    public string Game { get; } = Path.GetFullPath(game);
    public string[] Dlcs { get; } = Directory.Exists(Path.Combine(game, "dlc"))
        ? Directory.GetDirectories(Path.Combine(game, "dlc")).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal).ToArray()
        : [];

    public static string GroupDirectory(string game, string? dlc, string group) => dlc is null ? Path.Combine(game, group) : Path.Combine(game, "dlc", dlc, group);
    public GameArchive Archive(string group) => Layer(null, group) ?? throw new FileNotFoundException("Missing game archive group.", group);
    public GameArchive? Layer(string? dlc, string group)
    {
        string directory = GroupDirectory(Game, dlc, group);
        if (!archives.TryGetValue(directory, out var archive))
            archives[directory] = archive = dlc is null || File.Exists(Path.Combine(directory, "0.pamt")) ? new GameArchive(directory) : null;
        return archive;
    }
    // Base first, then each installed DLC that has the group.
    public IEnumerable<GameArchive> Layers(string group)
        => Dlcs.Select(d => Layer(d, group)).Prepend(Archive(group)).OfType<GameArchive>();

    public byte[] Read(string group, string path)
    {
        cancel.ThrowIfCancellationRequested();
        return Archive(group).Extract(path);
    }
    // The base file followed by every DLC variant of it that exists, tagged "" and ".<dlc>" as in the file names.
    public IEnumerable<(string Tag, byte[] Data)> ReadLayers(string group, string path)
    {
        yield return ("", Read(group, path));
        int dot = path.IndexOf('.', path.LastIndexOf('/') + 1);
        foreach (string dlc in Dlcs)
        {
            string variant = dot < 0 ? path + "." + dlc : path[..dot] + "." + dlc + path[dot..];
            if (Layer(dlc, group) is { } archive && archive.Index.Entries.ContainsKey(variant))
            {
                cancel.ThrowIfCancellationRequested();
                yield return ("." + dlc, archive.Extract(variant));
            }
        }
    }
    // Misc static-info files: <name>.base.staticinfomisc, then each DLC's <name>.<dlc>.staticinfomisc.
    public IEnumerable<(string Tag, byte[] Data)> Misc(string name)
    {
        string root = $"gamedata/binarystaticinfo__/bin/misc/{name}";
        yield return ("", Read("0008", root + ".base.staticinfomisc"));
        foreach (string dlc in Dlcs)
            if (Layer(dlc, "0008") is { } archive && archive.Index.Entries.ContainsKey($"{root}.{dlc}.staticinfomisc"))
            {
                cancel.ThrowIfCancellationRequested();
                yield return ("." + dlc, archive.Extract($"{root}.{dlc}.staticinfomisc"));
            }
    }
    // Static-info header/body pairs, tagged like ReadLayers; DLC header-only placeholders are skipped.
    public IEnumerable<(string Tag, byte[] Header, byte[] Body)> Tables(string name)
    {
        string root = $"gamedata/binarystaticinfo__/bin/{name}";
        yield return ("", Read("0008", root + ".staticinfoheader"), Read("0008", root + ".staticinfobody"));
        foreach (string dlc in Dlcs)
            if (Layer(dlc, "0008") is { } archive && archive.Index.Entries.ContainsKey($"{root}.{dlc}.staticinfoheader") &&
                archive.Index.Entries.ContainsKey($"{root}.{dlc}.staticinfobody"))
            {
                cancel.ThrowIfCancellationRequested();
                yield return ("." + dlc, archive.Extract($"{root}.{dlc}.staticinfoheader"), archive.Extract($"{root}.{dlc}.staticinfobody"));
            }
    }
}
