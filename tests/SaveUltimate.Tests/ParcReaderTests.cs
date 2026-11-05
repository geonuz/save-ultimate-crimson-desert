using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ParcReaderTests
{
    // Writes a one-block stream with real addresses, so a test can state a layout byte by byte.
    private sealed class BlobWriter
    {
        private readonly MemoryStream stream = new();
        private readonly BinaryWriter writer;
        private readonly int tableStart;
        private readonly int blockType;

        public BlobWriter(int blockType, params (string Name, (string Name, string Type, ushort Kind, ushort Size)[] Fields)[] types)
        {
            this.blockType = blockType;
            writer = new(stream);
            writer.Write(new byte[14]);
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)types.Length);
            for (int i = 0; i < types.Length; i++)
            {
                Text(types[i].Name);
                writer.Write((ushort)types[i].Fields.Length);
                foreach (var (name, type, kind, size) in types[i].Fields)
                {
                    Text(name); Text(type);
                    writer.Write(kind); writer.Write(size); writer.Write(0u);
                }
            }
            SchemaEnd = (int)stream.Position;
            writer.Write(0u); writer.Write(1u);
            tableStart = (int)stream.Position;
            writer.Write(new byte[24]);
        }

        public int SchemaEnd { get; }
        public int Position => (int)stream.Position;
        private void Text(string value) { writer.Write(value.Length); writer.Write(Encoding.ASCII.GetBytes(value)); }
        public BlobWriter Bytes(params byte[] value) { writer.Write(value); return this; }
        public BlobWriter U32(uint value) { writer.Write(value); return this; }

        public BlobWriter Block(byte[] mask, Action fields)
        {
            writer.Write((ushort)mask.Length); writer.Write(mask); writer.Write(0u);
            fields();
            return this;
        }

        public BlobWriter Object(ushort type, byte[] mask, Action fields, ulong id = ulong.MaxValue)
        {
            writer.Write((ushort)mask.Length); writer.Write(mask); writer.Write(type); writer.Write((byte)0); writer.Write(id);
            uint payload = (uint)stream.Position + 4;
            writer.Write(payload); writer.Write(0u);
            fields();
            writer.Write((uint)stream.Position - payload);
            return this;
        }

        public BlobWriter List(int count, uint nextId = 0)
        {
            writer.Write((byte)0); writer.Write(count); writer.Write((byte)0); writer.Write(nextId); writer.Write(0ul);
            return this;
        }

        public byte[] Finish()
        {
            byte[] blob = stream.ToArray();
            int end = blob.Length;
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(tableStart), end);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(tableStart + 4), blockType);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(tableStart + 16), tableStart + 24);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(tableStart + 20), end - tableStart - 24);
            return blob;
        }
    }

    private static readonly (string, (string, string, ushort, ushort)[]) RootType = ("Root", new (string, string, ushort, ushort)[]
    {
        ("_fixed", "uint32", 0, 4), ("_enum", "uint8", 2, 1), ("_text", "staticstringA", 1, 1), ("_array", "uint32", 3, 4),
        ("_object", "Child", 4, 0), ("_pointer", "Child", 5, 0), ("_list", "Child", 6, 0), ("_indexed", "Child", 7, 0),
        ("_noFixed", "uint32", 0, 4), ("_noText", "staticstringA", 1, 1), ("_noArray", "uint32", 3, 4), ("_noObject", "Child", 4, 0),
        ("_noPointer", "Child", 5, 0), ("_noList", "Child", 6, 0), ("_noIndexed", "Child", 7, 0), ("_after", "uint16", 0, 2)
    });
    private static readonly (string, (string, string, ushort, ushort)[]) ChildType = ("Child", new (string, string, ushort, ushort)[]
    {
        ("_value", "uint16", 0, 2), ("_tags", "uint32", 3, 4)
    });

    // Bit per field: present 0-7 and 15, absent 8-14.
    private static readonly byte[] RootMask = [0xFF, 0x80];

    private static byte[] Sample()
    {
        var blob = new BlobWriter(0, RootType, ChildType);
        void Child(ushort value) => blob.Object(1, [1], () => blob.Bytes((byte)value, 0).Bytes(1));
        blob.Block(RootMask, () =>
        {
            blob.U32(7).Bytes(9);
            blob.U32(2).Bytes(0x41, 0x42);
            blob.Bytes(0).U32(1).U32(5);
            Child(1);
            blob.Bytes(1); Child(2);
            blob.List(2); Child(3); Child(4);
            blob.List(1, 9); blob.Object(1, [1], () => blob.Bytes(5, 0).Bytes(1), 0);
            blob.Bytes(1, 1, 1);
            blob.Bytes(6, 0);
        });
        return blob.Finish();
    }

    private static SaveField[] Read(byte[] raw)
    {
        var layout = ParcLayout.Parse(raw);
        return ParcReader.Read(raw, layout, layout.Blocks.Single()).Fields;
    }

    private static void Refused(byte[] raw) => Assert.ThrowsExactly<InvalidDataException>(() => ParcReader.Validate(raw, ParcLayout.Parse(raw)));

    private static byte[] Changed(byte[] raw, int offset, byte value)
    {
        byte[] copy = (byte[])raw.Clone();
        copy[offset] = value;
        return copy;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ReadsEveryKindPresentAndAbsent()
    {
        byte[] raw = Sample();
        ParcReader.Validate(raw, ParcLayout.Parse(raw));
        var fields = Read(raw);
        Assert.AreEqual(16, fields.Length);
        Assert.AreEqual(7ul, fields.Named("_fixed").Scalar(raw, "uint32", 4));
        Assert.AreEqual(9ul, fields.Named("_enum").Scalar(raw, "uint8", 1));
        Assert.AreEqual("AB", fields.Named("_text").Text(raw));
        var array = fields.Named("_array");
        Assert.AreEqual(9, array.EndOffset - array.StartOffset);
        Assert.AreEqual(0, raw[array.StartOffset]);

        var inner = fields.Named("_object");
        Assert.AreEqual("Child", inner.ChildTypeName);
        Assert.AreEqual(1ul, inner.ChildFields.Named("_value").Scalar(raw, "uint16", 2));
        Assert.AreEqual((uint)inner.ChildPayloadOffset, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(inner.ChildPayloadOffset - 4)));

        var pointer = fields.Named("_pointer");
        Assert.AreEqual(1, raw[pointer.StartOffset]);
        Assert.AreEqual(2ul, pointer.ChildFields.Named("_value").Scalar(raw, "uint16", 2));

        var list = fields.Named("_list").Elements("Child");
        CollectionAssert.AreEqual(new ulong[] { 3, 4 }, list.Select(e => e.ChildFields.Named("_value").Scalar(raw, "uint16", 2)).ToArray());
        Assert.AreEqual(fields.Named("_list").StartOffset + 18, list[0].StartOffset);
        Assert.AreEqual(list[0].EndOffset, list[1].StartOffset);
        Assert.AreEqual(5ul, fields.Named("_indexed").Elements("Child").Single().ChildFields.Named("_value").Scalar(raw, "uint16", 2));

        // Absent arrays and lists take their one marker byte; other absent fields take no bytes.
        foreach (string name in new[] { "_noArray", "_noList", "_noIndexed" })
        {
            var absent = fields.Named(name);
            Assert.IsFalse(absent.Present);
            Assert.AreEqual(1, absent.EndOffset - absent.StartOffset, name);
            Assert.AreEqual(1, raw[absent.StartOffset], name);
        }
        foreach (string name in new[] { "_noFixed", "_noText", "_noObject", "_noPointer" })
            Assert.AreEqual(fields.Named(name).StartOffset, fields.Named(name).EndOffset, name);

        // The scalar after the markers is where the old parser went wrong.
        Assert.AreEqual(6ul, fields.Named("_after").Scalar(raw, "uint16", 2));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RefusesEveryBrokenRule()
    {
        byte[] raw = Sample();
        var fields = Read(raw);
        int Start(string name) => fields.Named(name).StartOffset;
        var inner = fields.Named("_object");
        var element = fields.Named("_list").ListElements[0];
        var indexed = fields.Named("_indexed");
        int blockStart = ParcLayout.Parse(raw).Blocks.Single().Offset;

        Refused(Changed(raw, blockStart, 3)); // block mask size
        Refused(Changed(raw, blockStart + 2 + RootMask.Length, 1)); // block reserved value
        Refused(Changed(raw, Start("_noArray"), 0)); // absent array marker
        Refused(Changed(raw, Start("_noList"), 0)); // absent list marker
        Refused(Changed(raw, Start("_array"), 1)); // present array marker
        Refused(Changed(raw, Start("_pointer"), 0)); // kind 5 prefix
        Refused(Changed(raw, Start("_list"), 1)); // present list marker
        Refused(Changed(raw, Start("_list") + 10, 1)); // list header padding
        Refused(Changed(raw, indexed.StartOffset + 5, 1)); // indexed list header
        Refused(Changed(raw, indexed.StartOffset + 12, 1)); // indexed list padding
        Refused(Changed(raw, inner.StartOffset, 2)); // object mask size
        Refused(Changed(raw, inner.StartOffset + 3, 200)); // object type
        Refused(Changed(raw, inner.StartOffset + 5, 1)); // object reserved byte
        Refused(Changed(raw, inner.StartOffset + 6, 0)); // object id outside an indexed list
        Refused(Changed(raw, inner.ChildPayloadOffset - 4, 0)); // payload address
        Refused(Changed(raw, inner.ChildPayloadOffset, 1)); // payload prefix
        Refused(Changed(raw, inner.EndOffset - 4, 0)); // payload size
        Refused(Changed(raw, element.ChildFields.Named("_tags").StartOffset, 0)); // absent array inside a list element

        // One byte short, with the table kept consistent, so the reader is what notices.
        byte[] cut = raw[..^1];
        int table = ParcLayout.Parse(raw).SchemaEnd + 8;
        BinaryPrimitives.WriteInt32LittleEndian(cut.AsSpan(table), cut.Length);
        BinaryPrimitives.WriteInt32LittleEndian(cut.AsSpan(table + 20), cut.Length - blockStart);
        var layout = ParcLayout.Parse(cut);
        Assert.AreEqual(cut.Length, layout.Blocks.Single().Offset + layout.Blocks.Single().Size);
        Refused(cut);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RefusesAPointerNoRecordAccountsFor()
    {
        var blob = new BlobWriter(0, ("Root", new (string, string, ushort, ushort)[] { ("_value", "uint12", 0, 12) }));
        int value = blob.SchemaEnd + 32 + 2 + 1 + 4;
        byte[] raw = blob.Block([1], () => blob.Bytes(255, 255, 255, 255, 255, 255, 255, 255).U32((uint)(value + 12))).Finish();
        Refused(raw);
        raw[value] = 0;
        ParcReader.Validate(raw, ParcLayout.Parse(raw));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RefusesOversizedCountsAndDeepNesting()
    {
        var huge = new BlobWriter(0, ("Root", new (string, string, ushort, ushort)[] { ("_list", "Root", 6, 0) }));
        Refused(huge.Block([1], () => huge.List(int.MaxValue)).Finish());
        var array = new BlobWriter(0, ("Root", new (string, string, ushort, ushort)[] { ("_array", "uint32", 3, 4) }));
        Refused(array.Block([1], () => array.Bytes(0).U32(uint.MaxValue)).Finish());
        var text = new BlobWriter(0, ("Root", new (string, string, ushort, ushort)[] { ("_text", "uint32", 1, 4) }));
        Refused(text.Block([1], () => text.U32(uint.MaxValue)).Finish());

        var deep = new BlobWriter(0, ("Root", new (string, string, ushort, ushort)[] { ("_child", "Root", 4, 0) }));
        void Nest(int depth) { if (depth > 0) deep.Object(0, [1], () => Nest(depth - 1)); else deep.Object(0, [0], () => { }); }
        Refused(deep.Block([1], () => Nest(200)).Finish());
    }

    // A save is untrusted input: damage must end in a refusal, never in another exception or a read outside the buffer.
    [TestMethod]
    [TestCategory("Unit")]
    public void DamagedRecordsOnlyEverRaiseARefusal()
    {
        byte[] raw = Sample();
        var layout = ParcLayout.Parse(raw);
        var random = new Random(20261106);
        int start = layout.Blocks.Single().Offset;
        for (int i = 0; i < 20000; i++)
        {
            byte[] copy = (byte[])raw.Clone();
            for (int change = random.Next(1, 4); change > 0; change--)
                copy[random.Next(start, copy.Length)] = (byte)(random.Next(4) switch { 0 => 0, 1 => 1, 2 => 255, _ => random.Next(256) });
            try
            {
                ParcReader.Validate(copy, layout);
                ParcReader.Read(copy, layout, layout.Blocks.Single());
            }
            catch (InvalidDataException) { }
        }
    }

    private static byte[] MissionRows()
    {
        var blob = new BlobWriter(0, ("Root", [("_list", "MissionStateData", 6, 0)]),
            ("MissionStateData", [("_key", "MissionKey", 0, 4), ("_state", "QuestStateType", 2, 1), ("_tags", "StringInfoKey", 3, 4), ("_alarm", "bool", 0, 1)]));
        blob.Block([1], () =>
        {
            blob.List(3);
            blob.Object(1, [0b0011], () => blob.U32(77).Bytes(5));
            blob.Object(1, [0b1011], () => blob.U32(78).Bytes(5, 1, 0));
            blob.Object(1, [0b1011], () => blob.U32(79).Bytes(5));
        });
        return blob.Finish();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ReadKeepsMarkerlessMissionRowsAndValidateCountsThem()
    {
        byte[] raw = MissionRows();
        Assert.AreEqual(2, ParcReader.Validate(raw, ParcLayout.Parse(raw)).DamagedRows);
        var rows = Read(raw).Named("_list").ListElements;
        Assert.AreEqual(3, rows.Length);
        Assert.AreEqual(77ul, rows[0].ChildFields.Named("_key").Scalar(raw, "MissionKey", 4));
        Assert.AreEqual(5ul, rows[0].ChildFields.Named("_state").Scalar(raw, "QuestStateType", 1));
        Assert.IsFalse(rows[0].ChildFields.Named("_tags").Present);
        Assert.AreEqual(1, rows[1].ChildFields.Named("_tags").EndOffset - rows[1].ChildFields.Named("_tags").StartOffset);
        Assert.AreEqual(0ul, rows[1].ChildFields.Named("_alarm").Scalar(raw, "bool", 1));
        // Its flag says _alarm is there, but the row ends first.
        Assert.AreEqual(79ul, rows[2].ChildFields.Named("_key").Scalar(raw, "MissionKey", 4));
        Assert.ThrowsExactly<InvalidDataException>(() => rows[2].ChildFields.Named("_alarm").Scalar(raw, "bool", 1));

        var layout = ParcLayout.Parse(raw);
        int start = layout.Blocks.Single().Offset;
        var random = new Random(20261106);
        for (int i = 0; i < 5000; i++)
        {
            byte[] copy = (byte[])raw.Clone();
            copy[random.Next(start, copy.Length)] = (byte)random.Next(256);
            try
            {
                ParcReader.Validate(copy, layout);
                ParcReader.Read(copy, layout, layout.Blocks.Single());
            }
            catch (InvalidDataException) { }
        }
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void TheNativeReaderTakesOverWhatTheLayoutRefusesAndLeavesATrace()
    {
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(FixtureSaves.Named("editing_baseline"))).Blob;
        var layout = ParcLayout.Parse(raw);
        var rows = SaveRecords.Read(raw, layout, "KnowledgeSaveData").Fields.Named("_list").ListElements;
        // The native reader tolerates a stale payload address.
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(rows[0].ChildPayloadOffset - 4), 1);
        var seen = new ConcurrentBag<Exception>();
        SaveDocument.ReaderFallback = seen.Add;
        try { Assert.AreEqual(rows.Length, SaveRecords.Read(raw, layout, "KnowledgeSaveData").Fields.Named("_list").ListElements.Length); }
        finally { SaveDocument.ReaderFallback = null; }
        Assert.IsFalse(seen.IsEmpty);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RelocationMovesAddressesSizesAndBoundsAndRefusesBrokenRanges()
    {
        byte[] raw = Sample();
        var array = Read(raw).Named("_array");
        int before = Read(raw).Named("_after").StartOffset;
        byte[] longer = [0, 3, 0, 0, 0, 5, 0, 0, 0, 6, 0, 0, 0, 7, 0, 0, 0];
        byte[] Relocated(params (int, int, byte[])[] regions) => RecordLists.Relocate(raw, regions, size => new byte[size]);

        byte[] moved = Relocated((array.StartOffset, array.EndOffset, longer));
        int shift = longer.Length - (array.EndOffset - array.StartOffset);
        var layout = ParcLayout.Parse(moved);
        ParcReader.Validate(moved, layout);
        Assert.AreEqual(raw.Length + shift, moved.Length);
        Assert.AreEqual(ParcLayout.Parse(raw).Blocks.Single().Size + shift, layout.Blocks.Single().Size);
        var fields = Read(moved);
        Assert.AreEqual(before + shift, fields.Named("_after").StartOffset);
        Assert.AreEqual(longer.Length, fields.Named("_array").EndOffset - fields.Named("_array").StartOffset);
        Assert.AreEqual(6ul, fields.Named("_after").Scalar(moved, "uint16", 2));

        var list = Read(raw).Named("_list");
        Assert.ThrowsExactly<InvalidDataException>(() => Relocated((list.ListElements[0].ChildPayloadOffset, list.EndOffset, [])));
        Assert.ThrowsExactly<InvalidDataException>(() => Relocated((array.StartOffset, array.EndOffset, longer), (array.StartOffset, array.EndOffset, longer)));
        Assert.ThrowsExactly<InvalidDataException>(() => Relocated((0, 1, [])));
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void AnEditMayNotAddDamagedRowsButUndoMayBringThemBack()
    {
        string path = FixtureSaves.Named("mission_rows_8");
        byte[] damaged = SaveContainer.Decode(File.ReadAllBytes(path)).Blob;
        var document = SaveDocument.Open(path);
        string original = document.BlobSha256;
        Assert.AreEqual(8, document.RepairMissionRows());
        Assert.ThrowsExactly<InvalidDataException>(() => document.Commit(damaged));
        document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.AreEqual(8, document.RepairMissionRows());
    }

    private static string[] Saves() => Directory.GetFiles(FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES."), "*.save", SearchOption.AllDirectories);
    private static bool Damaged(string path) => Path.GetFileName(Path.GetDirectoryName(path)) == "damaged";

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void EverySaveFollowsTheLayoutAndOnlyDamagedRowsBreakIt()
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Saves(), path =>
        {
            byte[] raw = SaveContainer.Decode(File.ReadAllBytes(path)).Blob;
            var layout = ParcLayout.Parse(raw);
            string name = Path.GetFileName(path);
            try
            {
                int damaged = ParcReader.Validate(raw, layout).DamagedRows;
                if ((damaged != 0) != Damaged(path)) failures.Add($"{name}: {damaged} damaged rows.");
            }
            catch (InvalidDataException e) { failures.Add($"{name}: {e.Message}"); }
            if (!Damaged(path)) return;
            foreach (var block in layout.Blocks)
                try { ParcReader.Read(raw, layout, block); }
                catch (InvalidDataException e) { failures.Add($"{name} {block.Name}: {e.Message}"); }
        });
        Assert.AreEqual("", string.Join(Environment.NewLine, failures.Order()));
    }

    // Where the native parser reports another position than the layout, per field. Kept until the native reader is gone, so a
    // swap changes only these. Markers of absent arrays are what it misses in all of them; see the table in ParcReader.
    private static readonly HashSet<string> nativeMisreads =
    [
        "MissionStateData._newAlarm", "MissionStateData._completeCount", "MissionStateData._overlapTargetList",
        "MercenarySaveData._currentHp", "MercenarySaveData._currentMp", "MercenarySaveData._lastEquipSlotNameKey", "MercenarySaveData._occupationState",
        // A native decode that fails on one field of a row, such as this object, keeps nothing after it.
        "MercenarySaveData._customizationSaveData", "MercenarySaveData._remainTimeBuffSaveDataList", "MercenarySaveData._useItemReserveSlotSaveList",
        "NPCScheduleStageSaveData._stageNameHash", "QuestGaugeStateData._state",
        "FieldGimmickSaveData._fieldGimmickSocketIndex", "FieldGimmickSaveData._fieldGimmickSaveData_TargetedConstraintList", "FieldGimmickSaveData._fieldGimmickSaveData_SaveTriggerGimmickList",
        "InventoryItemContentsSaveData._bankRandomSeedDataList", "InventoryItemContentsSaveData._foldItemGroupList",
        "FactionSaveData._factionManagementRewardTagList", "FactionManagementSaveData._enableItemList",
        // Only on 1.14.00: its item schema has no _averagePrice, which the native parser takes as a sign of the older layout.
        "ItemSaveData._chargedUseableCount", "ItemSaveData._transferredItemKey", "ItemSaveData._maxChargeUseableCount", "ItemSaveData._timeWhenPushItem",
        "ItemSaveData._isNewMark", "ItemSaveData._coolTimePerCharge", "ItemSaveData._isLocked", "ItemSaveData._currentGimmickState",
    ];

    private static void CompareFields(SaveField[] native, SaveField[] rule, string owner, ConcurrentDictionary<string, string> differences)
    {
        void Differ(string id, string what) => differences.TryAdd(id, what);
        if (native.Length != rule.Length) { Differ(owner, $"{native.Length} fields, the layout has {rule.Length}"); return; }
        for (int i = 0; i < native.Length; i++)
        {
            SaveField n = native[i], r = rule[i];
            string id = $"{owner}.{r.Name}";
            if (n.Present != r.Present) { Differ(id, $"present {n.Present}, the layout says {r.Present}"); continue; }
            if (!r.Present) continue;
            if (r.MetaKind is 0 or 1 or 2 or 3)
            {
                if (n.StartOffset != r.StartOffset || n.EndOffset != r.EndOffset) Differ(id, $"{n.StartOffset}-{n.EndOffset}, the layout says {r.StartOffset}-{r.EndOffset}");
                continue;
            }
            if (r.MetaKind is 4 or 5)
            {
                // A native decode that gave up on an object keeps none of its fields.
                if (n.ChildFields.Length == 0) Differ(id, "object not decoded");
                else CompareFields(n.ChildFields, r.ChildFields, r.ChildTypeName, differences);
                continue;
            }
            if (n.ListCount != r.ListCount) { Differ(id, $"{n.ListCount} elements, the layout has {r.ListCount}"); continue; }
            // Ownership reads drop shop stock on purpose.
            if (n.ListElements.Length == 0 && r.Name == "_storeItemList") continue;
            if (n.ListElements.Length != r.ListElements.Length) { Differ(id, $"{n.ListElements.Length} decoded of {r.ListElements.Length}"); continue; }
            for (int e = 0; e < r.ListElements.Length; e++)
            {
                SaveField a = n.ListElements[e], b = r.ListElements[e];
                if (a.StartOffset != b.StartOffset || a.EndOffset != b.EndOffset || a.ChildPayloadOffset != b.ChildPayloadOffset) Differ(id, $"element {e} at {a.StartOffset}-{a.EndOffset}, the layout says {b.StartOffset}-{b.EndOffset}");
                else CompareFields(a.ChildFields, b.ChildFields, b.ChildTypeName, differences);
            }
        }
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void NativeReaderDiffersFromTheLayoutOnlyInKnownFields()
    {
        var differences = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(Saves().Where(path => !Damaged(path)), path =>
        {
            byte[] raw = SaveContainer.Decode(File.ReadAllBytes(path)).Blob;
            var layout = ParcLayout.Parse(raw);
            foreach (string name in layout.Blocks.Select(b => b.Name).Distinct())
            {
                var blocks = layout.Blocks.Where(b => b.Name == name).ToArray();
                var native = SaveRecords.ReadNative(raw, layout, name);
                for (int i = 0; i < blocks.Length; i++)
                    CompareFields(native[i].Fields, ParcReader.Read(raw, layout, blocks[i]).Fields, name, differences);
            }
        });
        var unknown = differences.Where(p => !nativeMisreads.Contains(p.Key)).Select(p => $"{p.Key}: {p.Value}").Order().ToArray();
        Assert.AreEqual("", string.Join(Environment.NewLine, unknown));
    }
}
