using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Archives;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ArchiveTests
{
    private static byte[] Index(bool cycle = false, uint offset = 0)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write(0u); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(0u); writer.Write(0u); writer.Write(3u);
        writer.Write(0u);
        writer.Write(6u); writer.Write(cycle ? 0 : -1); writer.Write((byte)1); writer.Write((byte)'a');
        writer.Write(1u); writer.Write(GameChecksum.Calculate([])); writer.Write(-1); writer.Write(0u); writer.Write(1u);
        writer.Write(1u); writer.Write(0u); writer.Write(offset); writer.Write(3u); writer.Write(3u);
        writer.Write((ushort)0); writer.Write((byte)0); writer.Write((byte)0);
        var bytes = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, GameChecksum.Calculate(bytes.AsSpan(12)));
        return bytes;
    }

    [TestMethod]
    public void PamtChecksBoundsChecksumAndCyclicNames()
    {
        byte[] bytes = Index();
        Assert.AreEqual(3u, PamtIndex.Parse(bytes).Entries["a"].CompressedSize);
        bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index(cycle: true)));
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index(offset: 2)));
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index().AsSpan(0, 10)));
    }

    [TestMethod]
    public void ExtractReadsOnlyTheSpecifiedEntry()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SaveUltimate-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "0.pamt"), Index());
            File.WriteAllBytes(Path.Combine(directory, "0.paz"), "abc"u8.ToArray());
            var archive = new GameArchive(directory);
            Assert.AreEqual("abc", Encoding.UTF8.GetString(archive.Extract("a")));
            Assert.Throws<FileNotFoundException>(() => archive.Extract("../other"));
            File.WriteAllBytes(Path.Combine(directory, "0.paz"), [1]);
            Assert.Throws<InvalidDataException>(() => archive.Extract("a"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
