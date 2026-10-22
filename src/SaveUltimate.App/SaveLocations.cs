using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SaveUltimate.App;

public sealed record SaveSlot(string Path, string Category, string Platform, string Account, string Slot, DateTime? Modified)
{
    public string Group => Tr.Text("ui.slots_" + Category);
    public string Name => FriendlyName(Slot);
    public string Folder => Category == "manual" ? Path : System.IO.Path.GetDirectoryName(Path)!;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string SavedAt => Modified?.ToString("yyyy-MM-dd HH:mm") ?? Tr.Text("ui.file_missing");
    public static string FriendlyName(string name) => int.TryParse(name.Replace("slot", ""), out int value)
        ? value < 100 ? $"Auto Save {value + 1}" : $"Save Slot {value - 99}" : name;
}
public static class SaveLocations
{
    public static IEnumerable<(string Platform, string Path)> Roots(EditorSettings settings)
    {
        if (Directory.Exists(settings.SaveRoot)) yield return ("Custom", settings.SaveRoot);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (settings.SaveRoot.Length == 0)
            foreach (var (platform, directory) in new[] { ("Steam", "CD"), ("Epic", "CD_Epic"), ("Game Pass", "CD_GamePass") })
            {
                string path = Path.Combine(local, "Pearl Abyss", directory, "save");
                if (Directory.Exists(path)) yield return (platform, path);
            }
        foreach (string package in Directories(Path.Combine(local, "Packages")).Where(p => Path.GetFileName(p).StartsWith("PearlAbyss.CrimsonDesert", StringComparison.OrdinalIgnoreCase)))
        {
            string path = Path.Combine(package, "SystemAppData/wgs");
            if (Directory.Exists(path)) yield return ("WGS", path);
        }
    }
    private static string[] Directories(string path)
    {
        try { return Directory.GetDirectories(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
    private static string[] Files(string path)
    {
        try { return Directory.GetFiles(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
    public static SaveSlot[] Find(EditorSettings settings)
    {
        var found = new Dictionary<string, SaveSlot>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string category, string platform, string account, string slot)
        {
            try
            {
                var info = new FileInfo(path);
                found.TryAdd((category == "manual" ? "manual:" : "") + info.FullName,
                    new(info.FullName, category, platform, account, slot, info.Exists ? info.LastWriteTime : null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        var roots = Roots(settings).Select(r => (r.Platform, r.Path, Category: "default")).ToList();
        if (Directory.Exists(settings.AdditionalSaveRoot)) roots.Add(("Custom", settings.AdditionalSaveRoot, "additional"));
        foreach (var root in roots)
        {
            if (root.Platform == "WGS")
            {
                foreach (string account in Directories(root.Path).Where(p => Path.GetFileName(p) != "t"))
                    foreach (string container in Directories(account))
                    {
                        var candidates = new List<FileInfo>();
                        foreach (string path in Files(container))
                            try
                            {
                                var file = new FileInfo(path);
                                if (file.Name.StartsWith("container") || file.Length <= 1000) continue;
                                using var stream = File.OpenRead(path); byte[] magic = new byte[4];
                                if (stream.Read(magic) == 4 && magic.AsSpan().SequenceEqual("SAVE"u8)) candidates.Add(file);
                            }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                        if (candidates.OrderByDescending(f => f.Length).FirstOrDefault() is { } candidate)
                            Add(candidate.FullName, "default", "Game Pass", Path.GetFileName(account), Path.GetFileName(container));
                    }
                continue;
            }
            IEnumerable<string> dirs = [root.Path];
            for (int depth = 0; depth <= 2; depth++)
            {
                foreach (string directory in dirs)
                    foreach (string path in Files(directory).Where(p => p.EndsWith(".save", StringComparison.OrdinalIgnoreCase)))
                        if (Path.GetFileName(path).ToLowerInvariant() is not ("lobby.save" or "initialize_input.save"))
                            Add(path, root.Category, root.Platform, Path.GetFileName(root.Path), Path.GetFileName(path) == "save.save" ? Path.GetFileName(directory) : Path.GetFileNameWithoutExtension(path));
                dirs = dirs.SelectMany(Directories).Where(p => !Path.GetFileName(p).Contains("backup", StringComparison.OrdinalIgnoreCase)).ToArray();
            }
        }
        foreach (string path in settings.RecentSaveFiles)
            Add(path, "manual", "Custom", "", Path.GetFileName(path) == "save.save" ? Path.GetFileName(Path.GetDirectoryName(path)) ?? "" : Path.GetFileNameWithoutExtension(path));
        return found.Values.OrderBy(r => Array.IndexOf(new[] { "default", "additional", "manual" }, r.Category)).ThenBy(r => r.Platform).ThenBy(r => r.Account)
            .ThenBy(r => Regex.Replace(r.Slot, @"\d+", m => m.Value.PadLeft(20, '0')), StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path).ToArray();
    }
    public static bool ValidGame(string path) => File.Exists(Path.Combine(path, "0008/0.paz")) || File.Exists(Path.Combine(path, "0008/0.paz.sebak"));
    public static string GameFolder(EditorSettings settings)
    {
        if (!settings.AutoGameFolder) return settings.GameInstallPath;
        var roots = new List<string>();
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam) roots.Add(steam);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (string root in roots.ToArray())
        {
            string file = Path.Combine(root, "steamapps/libraryfolders.vdf");
            if (File.Exists(file))
                try { roots.AddRange(Regex.Matches(File.ReadAllText(file), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))); }
                catch (IOException) { }
        }
        return roots.Select(r => Path.Combine(r, "steamapps/common/Crimson Desert")).Prepend(settings.GameInstallPath).FirstOrDefault(ValidGame) ?? "";
    }
}
