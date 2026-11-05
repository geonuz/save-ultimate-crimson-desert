using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Archives;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ArchiveTests
{
    private static byte[] Index(uint offset = 0, byte extension = 0, byte distance = 3, bool table = true)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write(0u); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(0u); writer.Write(0u); writer.Write(3u);
        writer.Write(0u);
        // Names: "a" at 0 (root) and "b" at 3, whose parent lies `distance` bytes back.
        writer.Write(6u); writer.Write([0, 1, (byte)'a', distance, 1, (byte)'b']);
        writer.Write(1u); writer.Write(GameChecksum.Calculate([])); writer.Write(-1); writer.Write(0u); writer.Write(1u);
        writer.Write(1u); writer.Write(3u); writer.Write(offset); writer.Write(3u); writer.Write(3u);
        writer.Write((ushort)0); writer.Write((byte)0); writer.Write(extension);
        if (table) { writer.Write(6u); writer.Write("\0.txt\0"u8); }
        var bytes = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, GameChecksum.Calculate(bytes.AsSpan(12)));
        return bytes;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PamtChecksBoundsChecksumAndNames()
    {
        byte[] bytes = Index();
        Assert.AreEqual(3u, PamtIndex.Parse(bytes).Entries["ab"].CompressedSize);
        bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index(offset: 2)));
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index().AsSpan(0, 10)));
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index(distance: 4)));
        Assert.AreEqual(3u, PamtIndex.Parse(Index(extension: 1)).Entries["ab.txt"].CompressedSize);
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index(extension: 2)));
        // An index from before 2.04 has no extension table.
        Assert.Throws<InvalidDataException>(() => PamtIndex.Parse(Index(table: false)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ExtractReadsOnlyTheSpecifiedEntry()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SaveUltimate-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "0.pamt"), Index());
            File.WriteAllBytes(Path.Combine(directory, "0.paz"), "abc"u8.ToArray());
            var archive = new GameArchive(directory);
            Assert.AreEqual("abc", Encoding.UTF8.GetString(archive.Extract("ab")));
            Assert.Throws<FileNotFoundException>(() => archive.Extract("../other"));
            File.WriteAllBytes(Path.Combine(directory, "0.paz"), [1]);
            Assert.Throws<InvalidDataException>(() => archive.Extract("ab"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
