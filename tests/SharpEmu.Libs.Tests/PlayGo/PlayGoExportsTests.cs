// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.PlayGo;
using Xunit;

namespace SharpEmu.Libs.Tests.PlayGo;

[CollectionDefinition("PlayGoState", DisableParallelization = true)]
public sealed class PlayGoStateCollection
{
    public const string Name = "PlayGoState";
}

[Collection(PlayGoStateCollection.Name)]
public sealed class PlayGoExportsTests : IDisposable
{
    private const int BadChunkId = unchecked((int)0x80B2000C);
    private const byte LocusNotDownloaded = 0;
    private const byte LocusLocalFast = 3;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong InitParamsAddress = MemoryBase + 0x100;
    private const ulong InitBufferAddress = MemoryBase + 0x1000;
    private const ulong HandleAddress = MemoryBase + 0x200;
    private const ulong ChunkIdsAddress = MemoryBase + 0x300;
    private const ulong LociAddress = MemoryBase + 0x400;
    private const ulong OutEntriesAddress = MemoryBase + 0x500;
    private const ulong ProgressAddress = MemoryBase + 0x600;
    private const ulong NextChunkAddress = MemoryBase + 0x700;

    private readonly string? _originalApp0Root;
    private readonly string _app0Root;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10000);
    private readonly CpuContext _ctx;

    public PlayGoExportsTests()
    {
        _originalApp0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        _app0Root = Path.Combine(Path.GetTempPath(), $"sharpemu-playgo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_app0Root);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _app0Root);
        PlayGoExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void GetLocus_MetadataFreeApp0_RejectsPastAuthoritativeDefaultChunk()
    {
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0], 0x7F));
        Assert.Equal(new byte[] { LocusLocalFast }, ReadLoci(1));

        Assert.Equal(BadChunkId, GetLocus(handle, [1]));
        Assert.Equal(new byte[] { LocusNotDownloaded }, ReadLoci(1));
    }

    [Fact]
    public void GetLocus_MetadataFreeApp0_ReportsAllInstalledPakChunks()
    {
        for (var chunk = 0; chunk <= 8; chunk++)
        {
            File.WriteAllBytes(Path.Combine(_app0Root, $"pakchunk{chunk}-Windows.pak"), []);
        }

        var handle = InitializeAndOpen();

        Assert.Equal(BadChunkId, GetLocus(handle, [0, 8, 9]));
        Assert.Equal(
            new byte[] { LocusLocalFast, LocusLocalFast, LocusNotDownloaded },
            ReadLoci(3));
    }

    [Fact]
    public void PlayGoPgm_ReportsEveryChunkAndProgress()
    {
        var cacheDirectory = Directory.CreateDirectory(Path.Combine(_app0Root, "cache_ps5"));
        var header = new byte[0x14];
        "DMGP"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), 35);
        File.WriteAllBytes(Path.Combine(cacheDirectory.FullName, "playgo.pgm"), header);

        var handle = InitializeAndOpen();

        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = OutEntriesAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, PlayGoExports.PlayGoGetChunkId(_ctx));
        Assert.Equal(35u, ReadUInt32(OutEntriesAddress));

        var chunkIds = Enumerable.Range(0, 35).Select(static value => (ushort)value).ToArray();
        WriteChunkIds(chunkIds);
        _ctx[CpuRegister.Rsi] = ChunkIdsAddress;
        _ctx[CpuRegister.Rdx] = (ulong)chunkIds.Length;
        _ctx[CpuRegister.Rcx] = OutEntriesAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, PlayGoExports.PlayGoGetChunkId(_ctx));
        Assert.Equal(35u, ReadUInt32(OutEntriesAddress));
        Assert.Equal(chunkIds, ReadChunkIds(chunkIds.Length));

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [34]));
        Assert.Equal(new byte[] { LocusLocalFast }, ReadLoci(1));

        SetGetProgressArguments(handle, ChunkIdsAddress, (uint)chunkIds.Length, ProgressAddress);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, PlayGoExports.PlayGoGetProgress(_ctx));
        Assert.Equal(1ul, ReadUInt64(ProgressAddress));
        Assert.Equal(1ul, ReadUInt64(ProgressAddress + sizeof(ulong)));

        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = NextChunkAddress;
        Assert.Equal(unchecked((int)0x80020002), PlayGoExports.PlayGoRequestNextChunk(_ctx));
        Assert.Equal(0u, ReadUInt32(NextChunkAddress));
    }

    [Fact]
    public void GetLocus_ParsedChunkDefinitions_WritesPrefixAndRejectsFirstUnknownChunk()
    {
        File.WriteAllText(
            Path.Combine(_app0Root, "playgo-chunkdefs.xml"),
            "<playgo default_chunk=\"2\"><chunk id=\"2\"/><chunk id=\"7\"/></playgo>");
        var handle = InitializeAndOpen();

        Assert.Equal(BadChunkId, GetLocus(handle, [2, 3], 0xCC));
        Assert.Equal(new byte[] { LocusLocalFast, LocusNotDownloaded }, ReadLoci(2));
    }

    [Theory]
    [InlineData(UnusableMetadataKind.DatOnly)]
    [InlineData(UnusableMetadataKind.ScenarioOnly)]
    [InlineData(UnusableMetadataKind.MalformedChunkDefinitions)]
    [InlineData(UnusableMetadataKind.UnrecognizedChunkDefinitions)]
    public void GetLocus_UnparseableMetadata_RemainsPermissive(UnusableMetadataKind metadataKind)
    {
        switch (metadataKind)
        {
            case UnusableMetadataKind.DatOnly:
                var sceSys = Directory.CreateDirectory(Path.Combine(_app0Root, "sce_sys"));
                File.WriteAllBytes(Path.Combine(sceSys.FullName, "playgo-chunk.dat"), [0x70, 0x6C, 0x67, 0x6F]);
                break;
            case UnusableMetadataKind.ScenarioOnly:
                var scenarioDirectory = Directory.CreateDirectory(Path.Combine(_app0Root, "sce_sys"));
                File.WriteAllText(Path.Combine(scenarioDirectory.FullName, "playgo-scenario.json"), "{}");
                break;
            case UnusableMetadataKind.MalformedChunkDefinitions:
                File.WriteAllText(
                    Path.Combine(_app0Root, "playgo-chunkdefs.xml"),
                    "<playgo><chunk id=\"2\"></playgo>");
                break;
            case UnusableMetadataKind.UnrecognizedChunkDefinitions:
                File.WriteAllText(Path.Combine(_app0Root, "playgo-chunkdefs.xml"), "<not-playgo/>");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(metadataKind), metadataKind, null);
        }

        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [42]));
        Assert.Equal(new byte[] { LocusLocalFast }, ReadLoci(1));
    }

    [Fact]
    public void GetLocus_FaultingChunkAndOutputPointers_ReturnMemoryFault()
    {
        var handle = InitializeAndOpen();
        const ulong faultingAddress = 0xDEAD_0000_0000;
        Assert.True(_memory.TryWrite(LociAddress, new byte[] { 0xA5 }));

        SetGetLocusArguments(handle, faultingAddress, 1, LociAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            PlayGoExports.PlayGoGetLocus(_ctx));
        Assert.Equal(new byte[] { 0xA5 }, ReadLoci(1));

        WriteChunkIds([1]);
        SetGetLocusArguments(handle, ChunkIdsAddress, 1, faultingAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            PlayGoExports.PlayGoGetLocus(_ctx));
    }

    [Fact]
    public void GetLocus_AllEntriesSentinel_DoesNotReadChunksOrWriteLoci()
    {
        var handle = InitializeAndOpen();
        Assert.True(_memory.TryWrite(LociAddress, [0xA5]));

        SetGetLocusArguments(handle, 0xDEAD_0000_0000, uint.MaxValue, LociAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            PlayGoExports.PlayGoGetLocus(_ctx));
        Assert.Equal(new byte[] { 0xA5 }, ReadLoci(1));
    }

    public void Dispose()
    {
        PlayGoExports.ResetForTests();
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _originalApp0Root);
        Directory.Delete(_app0Root, recursive: true);
    }

    private uint InitializeAndOpen()
    {
        Span<byte> initParams = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(initParams, InitBufferAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(initParams[8..], 0x200000);
        Assert.True(_memory.TryWrite(InitParamsAddress, initParams));

        _ctx[CpuRegister.Rdi] = InitParamsAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            PlayGoExports.PlayGoInitialize(_ctx));

        _ctx[CpuRegister.Rdi] = HandleAddress;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            PlayGoExports.PlayGoOpen(_ctx));

        Span<byte> handleBytes = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(HandleAddress, handleBytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(handleBytes);
    }

    private int GetLocus(uint handle, ushort[] chunkIds, byte? fill = null)
    {
        WriteChunkIds(chunkIds);
        if (fill is { } fillValue)
        {
            var initialLoci = new byte[chunkIds.Length];
            Array.Fill(initialLoci, fillValue);
            Assert.True(_memory.TryWrite(LociAddress, initialLoci));
        }

        SetGetLocusArguments(handle, ChunkIdsAddress, (uint)chunkIds.Length, LociAddress);
        return PlayGoExports.PlayGoGetLocus(_ctx);
    }

    private void WriteChunkIds(ushort[] chunkIds)
    {
        var bytes = new byte[chunkIds.Length * sizeof(ushort)];
        for (var i = 0; i < chunkIds.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), chunkIds[i]);
        }

        Assert.True(_memory.TryWrite(ChunkIdsAddress, bytes));
    }

    private byte[] ReadLoci(int count)
    {
        var loci = new byte[count];
        Assert.True(_memory.TryRead(LociAddress, loci));
        return loci;
    }

    private ushort[] ReadChunkIds(int count)
    {
        var bytes = new byte[count * sizeof(ushort)];
        Assert.True(_memory.TryRead(ChunkIdsAddress, bytes));
        var chunkIds = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            chunkIds[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)));
        }

        return chunkIds;
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private void SetGetLocusArguments(uint handle, ulong chunkIds, uint count, ulong outLoci)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = chunkIds;
        _ctx[CpuRegister.Rdx] = count;
        _ctx[CpuRegister.Rcx] = outLoci;
    }

    private void SetGetProgressArguments(uint handle, ulong chunkIds, uint count, ulong outProgress)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = chunkIds;
        _ctx[CpuRegister.Rdx] = count;
        _ctx[CpuRegister.Rcx] = outProgress;
    }

    public enum UnusableMetadataKind
    {
        DatOnly,
        ScenarioOnly,
        MalformedChunkDefinitions,
        UnrecognizedChunkDefinitions,
    }
}
