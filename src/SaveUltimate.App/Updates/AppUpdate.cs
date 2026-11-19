using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaveUltimate.App;

public sealed record AppRelease(Version Version, string Notes, string Package, string Sha256);

public static class AppUpdate
{
    private const string LatestRelease = "https://api.github.com/repos/geonuz/save-ultimate-crimson-desert/releases/latest";
    private const string Manifest = "release-manifest.json", Executable = "SaveUltimate.exe", Work = ".update", StartedSignal = "SAVEULTIMATE_UPDATE_STARTED";
    public static Version Current { get; } = new(typeof(AppUpdate).Assembly.GetName().Version!.ToString(3));
    // Only a published folder has a manifest; a build output folder must not replace itself.
    public static bool CanInstall => File.Exists(Path.Combine(AppContext.BaseDirectory, Manifest));
    private static readonly HttpClient http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SaveUltimate/{Current}"); // GitHub rejects requests without one.
        return client;
    }

    public static async Task<AppRelease?> CheckAsync() => ReadRelease(await http.GetStringAsync(LatestRelease), Current);

    public static AppRelease? ReadRelease(string json, Version current)
    {
        using var release = JsonDocument.Parse(json);
        var tag = Regex.Match(release.RootElement.GetProperty("tag_name").GetString() ?? "", @"^v(\d+\.\d+\.\d+)-");
        if (!tag.Success) throw new InvalidDataException("The latest release has an unknown version.");
        var version = new Version(tag.Groups[1].Value);
        if (version <= current) return null;
        foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == $"SaveUltimate-{version}-windows-x64.zip" && asset.TryGetProperty("digest", out var digest) &&
                Regex.Match(digest.GetString() ?? "", "^sha256:([0-9a-f]{64})$") is { Success: true } hash)
                return new(version, Regex.Replace(release.RootElement.GetProperty("body").GetString() ?? "", @"\*\*|`|\s*Source: \S+\s*$", ""), asset.GetProperty("browser_download_url").GetString()!, hash.Groups[1].Value);
        throw new InvalidDataException("The latest release has no verifiable package.");
    }

    public static async Task<string> DownloadAsync(AppRelease release, string folder, IProgress<int> progress)
    {
        string work = Path.Combine(folder, Work), package = Path.Combine(work, "package.zip");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        using var response = await http.GetAsync(release.Package, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0, received = 0;
        await using var source = await response.Content.ReadAsStreamAsync();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stalled = new CancellationTokenSource();
        byte[] buffer = new byte[1 << 20];
        await using (var target = File.Create(package))
            while (true)
            {
                // HttpClient.Timeout stops at the headers; without this a stalled download never ends.
                stalled.CancelAfter(TimeSpan.FromSeconds(60));
                int read = await source.ReadAsync(buffer, stalled.Token);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read));
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0) progress.Report((int)(received * 100 / total));
            }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256) throw new IOException(Tr.Text("ui.update_damaged"));
        return package;
    }

    // Windows lets a running program and its loaded libraries be moved but not overwritten or deleted,
    // so the release's files go aside and the next start removes them. Anything else in the folder is user data.
    // Returns what puts the previous release back.
    public static Action Install(string package, string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        string fresh = Path.Combine(folder, Work, "new"), old = Path.Combine(folder, Work, "old"), failed = Path.Combine(folder, Work, "failed");
        foreach (string leftover in new[] { fresh, old, failed }) if (Directory.Exists(leftover)) Directory.Delete(leftover, true);
        ZipFile.ExtractToDirectory(package, fresh);
        if (!File.Exists(Path.Combine(fresh, Executable)) || !File.Exists(Path.Combine(fresh, Manifest))) throw new IOException(Tr.Text("ui.update_damaged"));
        string[] incoming = Directory.GetFiles(fresh, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(fresh, p)).ToArray();
        // Reading the previous manifest also removes files a newer release no longer ships.
        string[] outgoing = ReleaseFiles(folder).Concat(incoming).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => File.Exists(Path.Combine(folder, p))).ToArray();
        List<string> movedAside = [], placed = [];
        void Undo()
        {
            // Moved rather than deleted: a virus scanner may still hold a file the new version just used.
            foreach (string file in placed) Move(Path.Combine(folder, file), Path.Combine(failed, file));
            foreach (string file in movedAside) Move(Path.Combine(old, file), Path.Combine(folder, file));
        }
        try
        {
            foreach (string file in outgoing) { Move(Path.Combine(folder, file), Path.Combine(old, file)); movedAside.Add(file); }
            foreach (string file in incoming) { Move(Path.Combine(fresh, file), Path.Combine(folder, file)); placed.Add(file); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Undo();
            throw;
        }
        return Undo;
    }
    private static void Move(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target);
    }
    private static string[] ReleaseFiles(string folder)
    {
        try
        {
            // ReadAllText because Build.ps1 may write a byte order mark, which JsonDocument refuses.
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, Manifest)));
            return manifest.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)
                .Where(p => Path.GetFullPath(Path.Combine(folder, p)).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    // True once the new version says its window is up. False lets this version, still running, put itself back.
    public static async Task<bool> StartNewVersionAsync(string folder)
    {
        string name = "SaveUltimate-update-" + Guid.NewGuid().ToString("N");
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        // Not Environment.ProcessPath: that would follow the running program into the folder it was moved aside to.
        var program = new ProcessStartInfo(Path.Combine(folder, Executable)) { WorkingDirectory = folder };
        program.Environment[StartedSignal] = name;
        Process process;
        // Windows or a virus scanner can refuse to run a new program.
        try { process = Process.Start(program)!; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        using (process) return await Task.Run(() => WaitForStart(process, started, TimeSpan.FromMinutes(2)));
    }

    public static bool WaitForStart(Process process, WaitHandle started, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (started.WaitOne(200)) return true;
            if (process.HasExited) return started.WaitOne(0);
        }
        try { process.Kill(true); process.WaitForExit(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    // Tells the version that started this one, if any, that it does not need to roll back.
    public static void ReportStarted()
    {
        if (Environment.GetEnvironmentVariable(StartedSignal) is { } name && EventWaitHandle.TryOpenExisting(name, out var started))
            using (started) started.Set();
    }

    // The previous version may still be exiting and holding its program file.
    public static async Task CleanUpAsync(string folder)
    {
        string work = Path.Combine(folder, Work);
        for (int attempt = 0; attempt < 20 && Directory.Exists(work); attempt++)
        {
            try { Directory.Delete(work, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }
}
