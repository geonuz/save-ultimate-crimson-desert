using System.IO;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class AppUpdateTests
{
    private static readonly string Digest = new('a', 64);
    private static string Release(string tag, string asset, string digest) =>
        $$"""{"tag_name":"{{tag}}","body":"**notes**\r\n\r\nSource: https://example.test/commit/1","assets":[{"name":"{{asset}}","digest":"{{digest}}","browser_download_url":"https://example.test/p.zip"}]}""";

    [TestMethod]
    [TestCategory("Unit")]
    public void OnlyANewerReleaseWithAVerifiablePackageIsOffered()
    {
        var current = new Version(1, 4, 0);
        var release = AppUpdate.ReadRelease(Release("v1.5.0-f27c809fd557", "SaveUltimate-1.5.0-windows-x64.zip", "sha256:" + Digest), current)!;
        Assert.AreEqual(new Version(1, 5, 0), release.Version);
        Assert.AreEqual(Digest, release.Sha256);
        Assert.AreEqual("notes", release.Notes);
        Assert.IsNull(AppUpdate.ReadRelease(Release("v1.4.0-f27c809fd557", "SaveUltimate-1.4.0-windows-x64.zip", "sha256:" + Digest), current));
        Assert.IsNull(AppUpdate.ReadRelease(Release("v1.3.9-f27c809fd557", "SaveUltimate-1.3.9-windows-x64.zip", "sha256:" + Digest), current));
        Assert.Throws<InvalidDataException>(() => AppUpdate.ReadRelease(Release("v1.5.0-f27c809fd557", "other.zip", "sha256:" + Digest), current));
        Assert.Throws<InvalidDataException>(() => AppUpdate.ReadRelease(Release("v1.5.0-f27c809fd557", "SaveUltimate-1.5.0-windows-x64.zip", "md5:abc"), current));
        Assert.Throws<InvalidDataException>(() => AppUpdate.ReadRelease(Release("latest", "SaveUltimate-1.5.0-windows-x64.zip", "sha256:" + Digest), current));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallReplacesReleaseFilesAndKeepsUserData() => InFolders((app, package) =>
    {
        InstalledRelease(app);
        AppUpdate.Install(package, app);

        Assert.AreEqual("new", File.ReadAllText(Path.Combine(app, "SaveUltimate.exe")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(app, "resources", "data.json")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(app, "added.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(app, "removed.dll")), "A file the new release no longer ships goes away.");
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(app, "editor_config.json")));
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(app, "backups", "a.save")));
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(app, "game_data", "manifest.json")));

        AppUpdate.CleanUpAsync(app).GetAwaiter().GetResult();
        Assert.IsFalse(Directory.Exists(Path.Combine(app, ".update")));
    });

    [TestMethod]
    [TestCategory("Unit")]
    public void FailedInstallRestoresEveryFile() => InFolders((app, package) =>
    {
        InstalledRelease(app);
        using (File.Open(Path.Combine(app, "resources", "data.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => AppUpdate.Install(package, app));

        Assert.AreEqual("old", File.ReadAllText(Path.Combine(app, "SaveUltimate.exe")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(app, "resources", "data.json")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(app, "removed.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(app, "added.dll")));
    });

    [TestMethod]
    [TestCategory("Unit")]
    public void RollingBackRestoresThePreviousReleaseAndKeepsUserData() => InFolders((app, package) =>
    {
        InstalledRelease(app);
        var rollBack = AppUpdate.Install(package, app);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(app, "SaveUltimate.exe")));

        rollBack();
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(app, "SaveUltimate.exe")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(app, "resources", "data.json")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(app, "removed.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(app, "added.dll")));
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(app, "editor_config.json")));
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(app, "game_data", "manifest.json")));
    });

    [TestMethod]
    [TestCategory("Unit")]
    public void OnlyASignalFromTheNewVersionCountsAsStarted()
    {
        static System.Diagnostics.Process Run(string arguments) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", arguments) { CreateNoWindow = true, UseShellExecute = false })!;
        string name = "SaveUltimate-test-" + Guid.NewGuid().ToString("N");
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using (var crashed = Run("/c exit 1")) Assert.IsFalse(AppUpdate.WaitForStart(crashed, started, TimeSpan.FromSeconds(30)), "It exited without a window.");
        using (var hung = Run("/c ping -n 60 127.0.0.1 >nul"))
        {
            Assert.IsFalse(AppUpdate.WaitForStart(hung, started, TimeSpan.FromMilliseconds(300)), "It never reported.");
            Assert.IsTrue(hung.HasExited, "A version that never reported is stopped.");
        }
        Environment.SetEnvironmentVariable("SAVEULTIMATE_UPDATE_STARTED", name);
        try { AppUpdate.ReportStarted(); }
        finally { Environment.SetEnvironmentVariable("SAVEULTIMATE_UPDATE_STARTED", null); }
        using (var running = Run("/c ping -n 2 127.0.0.1 >nul")) Assert.IsTrue(AppUpdate.WaitForStart(running, started, TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PackageWithoutTheProgramChangesNothing() => InFolders((app, _) =>
    {
        InstalledRelease(app);
        string source = Path.Combine(Path.GetDirectoryName(app)!, "wrong"), wrong = source + ".zip";
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "new");
        ZipFile.CreateFromDirectory(source, wrong);

        Assert.Throws<IOException>(() => AppUpdate.Install(wrong, app));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(app, "SaveUltimate.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(app, "readme.txt")));
    });

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OnlyAPackageWithTheExpectedHashIsKept()
    {
        using var temp = new TempFolder();
        byte[] package = "package"u8.ToArray();
        int port;
        using (var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { free.Start(); port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port; free.Stop(); }
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        var serving = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var context = await server.GetContextAsync();
                await context.Response.OutputStream.WriteAsync(package);
                context.Response.Close();
            }
        });
        string url = $"http://localhost:{port}/package.zip", hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(package));

        string saved = await AppUpdate.DownloadAsync(new(new(9, 9, 9), "", url, hash), temp.Root, new Progress<int>());
        CollectionAssert.AreEqual(package, File.ReadAllBytes(saved));
        await Assert.ThrowsAsync<IOException>(() => AppUpdate.DownloadAsync(new(new(9, 9, 9), "", url, Digest), temp.Root, new Progress<int>()));
        await serving;
    }

    private static void InstalledRelease(string app)
    {
        foreach (string file in new[] { "SaveUltimate.exe", "removed.dll", @"resources\data.json" }) Write(app, file, "old");
        foreach (string file in new[] { "editor_config.json", @"backups\a.save", @"game_data\manifest.json" }) Write(app, file, "mine");
        // The byte order mark and shape match what Build.ps1 writes.
        File.WriteAllText(Path.Combine(app, "release-manifest.json"), """{"files":[{"path":"SaveUltimate.exe"},{"path":"removed.dll"},{"path":"resources\\data.json"},{"path":"..\\outside.txt"}]}""", new System.Text.UTF8Encoding(true));
    }
    private static void Write(string folder, string file, string text)
    {
        string path = Path.Combine(folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private static void InFolders(Action<string, string> test)
    {
        using var temp = new TempFolder();
        string app = temp.Named("app"), source = temp.Named("source"), package = temp.Named("package.zip");
        Directory.CreateDirectory(app);
        foreach (string file in new[] { "SaveUltimate.exe", "added.dll", @"resources\data.json" }) Write(source, file, "new");
        Write(source, "release-manifest.json", """{"files":[]}""");
        ZipFile.CreateFromDirectory(source, package);
        Write(temp.Root, "outside.txt", "mine");
        test(app, package);
        Assert.AreEqual("mine", File.ReadAllText(temp.Named("outside.txt")), "A manifest path cannot leave the folder.");
    }
}
