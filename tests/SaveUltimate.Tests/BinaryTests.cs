using System.IO;
using System.IO.Compression;
using ImageMagick;
using K4os.Compression.LZ4;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Binary;
using SaveUltimate.Core.Images;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class BinaryTests
{
    [TestMethod]
    public void SocketLimitReaderSkipsUnneededCatalogFieldsAndRejectsInvalidLimits()
    {
        static Dictionary<uint, int> Read(string json)
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            return SaveUltimate.Core.Catalogs.ItemRules.ReadSocketLimits(stream);
        }
        var limits = Read("""{"items":[{"itemKey":123,"socketLimit":5,"name":"unused","translations":{"ko":"이름"}},{"itemKey":456,"socketLimit":0}]}""");
        Assert.AreEqual(5, limits[123]);
        Assert.AreEqual(0, limits[456]);
        Assert.Throws<InvalidDataException>(() => Read("""{"items":[{"itemKey":0,"socketLimit":1}]}"""));
        Assert.Throws<InvalidDataException>(() => Read("""{"items":[{"itemKey":1,"socketLimit":6}]}"""));
        Assert.Throws<InvalidDataException>(() => Read("{}"));
        Assert.Throws<ArgumentException>(() => Read("""{"items":[{"itemKey":1,"socketLimit":1},{"itemKey":1,"socketLimit":2}]}"""));
    }

    [TestMethod]
    public void BinaryRecordReaderPreservesFieldsAndRejectsMalformedPackets()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Text(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        writer.Write("SVR1"u8); writer.Write(1);
        Text("Type"); writer.Write(2);
        int flagOffset = 0;
        for (int i = 0; i < 2; i++)
        {
            Text("_value"); Text("uint32");
            writer.Write(0); writer.Write(4); writer.Write(i); writer.Write(0);
            flagOffset = (int)stream.Position; writer.Write((byte)1);
            writer.Write(10); writer.Write(14); Text("");
            for (int n = 0; n < 4; n++) writer.Write(0);
            Text("");
            for (int n = 0; n < 3; n++) writer.Write(0);
        }
        writer.Write(0);
        byte[] packet = stream.ToArray();
        var fields = RecordReader.Read(packet).Single().Fields;
        Assert.AreEqual(2, fields.Length);
        Assert.AreSame(fields[0].Name, fields[1].Name);
        Assert.AreEqual((true, 4, 10, 14, 1), (fields[1].Present, fields[1].MetaSize, fields[1].StartOffset, fields[1].EndOffset, fields[1].FieldIndex));
        Assert.IsNull(fields[0].Note);
        for (int length = 0; length < packet.Length; length++)
        {
            int end = length;
            Assert.Throws<InvalidDataException>(() => RecordReader.Read(packet.AsSpan(0, end)));
        }
        Assert.Throws<InvalidDataException>(() => RecordReader.Read([.. packet, 0]));
        packet[flagOffset] = 2;
        Assert.Throws<InvalidDataException>(() => RecordReader.Read(packet));
        packet[flagOffset] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => RecordReader.Read(packet));
        packet[0] = 0;
        Assert.Throws<InvalidDataException>(() => RecordReader.Read(packet));
    }

    [TestMethod]
    public void FieldLookupRejectsMissingAndDuplicateNames()
    {
        SaveField[] fields = [new() { Name = "key" }, new() { Name = "value" }];
        Assert.AreSame(fields[0], fields.Named("key"));
        Assert.Throws<InvalidDataException>(() => fields.Named("absent"));
        Assert.Throws<InvalidDataException>(() => fields.Concat([new SaveField { Name = "key" }]).Named("key"));
    }

    [TestMethod]
    public void ChaChaMatchesRfc8439BlockVector()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] nonce = Convert.FromHexString("01000000000000090000004A00000000");
        byte[] expected = Convert.FromHexString(
            "10F1E7E4D13B5915500FDD1FA32071C4C7D1F4C733C068030422AA9AC3D46C4E" +
            "D2826446079FAA0914C2D705D98B02A2B5129CD1DE164EB9CBD083E8A2503C4E");
        CollectionAssert.AreEqual(expected, GameChaCha.Transform(new byte[64], key, nonce));
    }

    [TestMethod]
    public void CompressionRequiresExactExpandedLength()
    {
        byte[] source = Enumerable.Range(0, 2000).Select(i => (byte)(i % 11)).ToArray();
        byte[] packed = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
        int length = LZ4Codec.Encode(source, packed);
        CollectionAssert.AreEqual(source, Compression.Decode(packed.AsSpan(0, length), 2, source.Length));
        Assert.Throws<InvalidDataException>(() => Compression.Decode(packed.AsSpan(0, length), 2, source.Length - 1));
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, true)) zlib.Write(source);
        CollectionAssert.AreEqual(source, Compression.Decode(buffer.ToArray(), 3, source.Length));
        Assert.Throws<InvalidDataException>(() => Compression.Decode(buffer.ToArray(), 3, source.Length - 1));
        Assert.Throws<InvalidDataException>(() => Compression.Decode(source, 0, source.Length + 1));
        byte[] target = Enumerable.Repeat((byte)0xCD, source.Length + 8).ToArray();
        foreach (var (data, format) in new[] { (source, 0), (packed[..length], 2), (buffer.ToArray(), 3) })
        {
            Compression.Decode(data, format, target.AsSpan(4, source.Length));
            CollectionAssert.AreEqual(source, target[4..^4]);
            Assert.IsTrue(target[..4].Concat(target[^4..]).All(b => b == 0xCD));
            Assert.Throws<InvalidDataException>(() => Compression.Decode(data, format, target.AsSpan(4, source.Length - 1)));
        }
    }

    [TestMethod]
    public void DdsConversionPreservesBoundsAndTransparency()
    {
        using var original = new MagickImage(MagickColors.Transparent, 16, 8);
        byte[] dds = original.ToByteArray(MagickFormat.Dds);
        foreach (bool webp in new[] { false, true })
        {
            using var converted = new MagickImage(TextureImage.Convert(dds, 4, 4, webp));
            Assert.AreEqual(4u, converted.Width);
            Assert.AreEqual(2u, converted.Height);
            Assert.IsTrue(converted.HasAlpha);
        }
        Assert.Throws<InvalidDataException>(() => TextureImage.Convert(dds, 4, 4, false, new(15, 0, 2, 1)));
    }
}
