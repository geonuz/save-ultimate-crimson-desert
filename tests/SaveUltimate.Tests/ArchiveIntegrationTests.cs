using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Archives;
using SaveUltimate.Core.Images;

namespace SaveUltimate.Tests;

[TestClass]
[TestCategory("PrivateFixtures")]
public sealed class ArchiveIntegrationTests
{
    [TestMethod]
    public void InstalledGameMatchesPinnedCrimsonRs()
    {
        string? game = Environment.GetEnvironmentVariable("SAVEULTIMATE_GAME");
        string? reference = Environment.GetEnvironmentVariable("SAVEULTIMATE_ARCHIVE_REFERENCE");
        if (game is null || reference is null) Assert.Inconclusive("Set SAVEULTIMATE_GAME and SAVEULTIMATE_ARCHIVE_REFERENCE for archive parity.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(reference));
        foreach (var group in json.RootElement.GetProperty("groups").EnumerateArray())
        {
            string name = group.GetProperty("group").GetString()!;
            var archive = new GameArchive(Path.Combine(game, name));
            Assert.AreEqual(group.GetProperty("count").GetInt32(), archive.Index.Entries.Count, name);
            foreach (var expected in json.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("group").GetString() == name))
            {
                string path = expected.GetProperty("path").GetString()!.TrimStart('/');
                byte[] bytes = archive.Extract(path);
                Assert.AreEqual(expected.GetProperty("length").GetInt32(), bytes.Length, name + "/" + path);
                Assert.AreEqual(expected.GetProperty("sha256").GetString()!.ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(bytes)), name + "/" + path);
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    Assert.IsGreaterThan(0, TextureImage.Convert(bytes, 96, 96, false).Length);
            }
        }
    }
}
