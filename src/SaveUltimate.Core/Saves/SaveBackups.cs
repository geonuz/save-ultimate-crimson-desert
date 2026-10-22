using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SaveUltimate.Core.Saves;

public static class SaveBackups
{
    public static string DirectoryFor(string source) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "backups");
    public static string PristinePath(string source) => Path.Combine(DirectoryFor(source), Path.GetFileName(source) + ".PRISTINE.bak");
    public static string MirrorDirectory(string source, string appDirectory) => Path.Combine(appDirectory, "backups",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToLowerInvariant())))[..12].ToLowerInvariant());
    public static string[] List(string source) => Directory.Exists(DirectoryFor(source))
        ? Directory.EnumerateFiles(DirectoryFor(source)).Where(p => Path.GetFileName(p).StartsWith(Path.GetFileName(source) + ".", StringComparison.Ordinal)
            && p.EndsWith(".bak", StringComparison.Ordinal)).OrderDescending(StringComparer.Ordinal).ToArray() : [];
    public static byte[] Hash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }

    public static string? Create(string source, string? directory = null)
    {
        if (!File.Exists(source)) return null;
        directory ??= DirectoryFor(source);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Path.GetFileName(source)}.{DateTime.Now:yyyyMMdd_HHmmss_ffffff}_{Guid.NewGuid().ToString("N")[..8]}.bak");
        CopyVerified(source, path);
        Prune(directory, Path.GetFileName(source));
        return path;
    }

    public static void Mirror(string source, string backup, string appDirectory)
    {
        string directory = MirrorDirectory(source, appDirectory);
        Directory.CreateDirectory(directory);
        CopyVerified(backup, Path.Combine(directory, Path.GetFileName(backup)));
        Prune(directory, Path.GetFileName(source));
    }

    private static void Prune(string directory, string sourceName)
    {
        var pattern = new Regex("^" + Regex.Escape(sourceName) + @"\.\d{8}_\d{6}(?:_\d{6}_[0-9a-f]{8})?\.bak$");
        foreach (string old in Directory.EnumerateFiles(directory).Where(p => pattern.IsMatch(Path.GetFileName(p))).OrderDescending(StringComparer.Ordinal).Skip(10))
            File.Delete(old);
    }

    public static void Pristine(string source, bool replace = false)
    {
        string target = PristinePath(source);
        if (!replace && File.Exists(target)) return;
        Directory.CreateDirectory(DirectoryFor(source));
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CopyVerified(source, temporary);
            NativeSave.ValidateFile(temporary);
            if (replace && File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string? Restore(string source, string backup, byte[] expectedHash)
    {
        if (!List(source).Contains(Path.GetFullPath(backup), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Select a backup belonging to this save.");
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, $".restore_{Guid.NewGuid():N}.save");
        try
        {
            CopyVerified(backup, temporary);
            NativeSave.ValidateFile(temporary);
            return ReplacePrepared(temporary, source, expectedHash, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string? ReplacePrepared(string temporary, string destination, byte[]? expectedHash, bool backup)
    {
        if (expectedHash is null) { File.Move(temporary, destination); return null; }
        // Keep ordinary writers out while checking, backing up and replacing the file.
        using var current = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!SHA256.HashData(current).AsSpan().SequenceEqual(expectedHash))
            throw new IOException("The destination changed on disk. Reload it before saving or restoring.");
        string? recovery = backup ? Create(destination) : null;
        if (!Hash(destination).AsSpan().SequenceEqual(expectedHash)) throw new IOException("The destination changed during backup.");
        File.Replace(temporary, destination, null);
        return recovery;
    }

    private static void CopyVerified(string source, string destination)
    {
        bool created = false;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; input.CopyTo(output); output.Flush(true); }
            input.Position = 0;
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(Hash(destination))) throw new IOException("Backup verification failed.");
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        }
        catch { if (created && File.Exists(destination)) File.Delete(destination); throw; }
    }
}
