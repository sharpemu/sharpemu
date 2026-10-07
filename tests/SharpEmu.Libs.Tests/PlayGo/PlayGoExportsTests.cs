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
    private const int BadHandle = unchecked((int)0x80B20009);
    private const int NotSupportPlayGo = unchecked((int)0x80B2000E);
    private const byte LocusNotDownloaded = 0;
    private const byte LocusLocalFast = 3;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong InitParamsAddress = MemoryBase + 0x100;
    private const ulong InitBufferAddress = MemoryBase + 0x1000;
    private const ulong HandleAddress = MemoryBase + 0x200;
    private const ulong ChunkIdsAddress = MemoryBase + 0x300;
    private const ulong LociAddress = MemoryBase + 0x400;

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
    [InlineData(UnusableMetadataKind.ScenarioOnly)]
    [InlineData(UnusableMetadataKind.DatOnly)]
    [InlineData(UnusableMetadataKind.MalformedChunkDefinitions)]
    [InlineData(UnusableMetadataKind.UnrecognizedChunkDefinitions)]
    public void Open_UnusableChunkMap_ReportsUnsupportedWithoutPublishingHandle(UnusableMetadataKind metadataKind)
    {
        switch (metadataKind)
        {
            case UnusableMetadataKind.ScenarioOnly:
                var scenarioDirectory = Directory.CreateDirectory(Path.Combine(_app0Root, "sce_sys"));
                File.WriteAllText(
                    Path.Combine(scenarioDirectory.FullName, "playgo-scenario.json"),
                    "{\"scenarioCount\":1,\"scenarios\":[{\"id\":0,\"type\":\"playmode\"}]}");
                break;
            case UnusableMetadataKind.DatOnly:
                var sceSys = Directory.CreateDirectory(Path.Combine(_app0Root, "sce_sys"));
                File.WriteAllBytes(Path.Combine(sceSys.FullName, "playgo-chunk.dat"), [0x70, 0x6C, 0x67, 0x6F]);
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

        Initialize();
        Assert.True(_ctx.TryWriteUInt32(HandleAddress, 0xDEADBEEF));
        _ctx[CpuRegister.Rdi] = HandleAddress;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(NotSupportPlayGo, PlayGoExports.PlayGoOpen(_ctx));
        Assert.True(_ctx.TryReadUInt32(HandleAddress, out var unchangedHandle));
        Assert.Equal(0xDEADBEEFu, unchangedHandle);
        Assert.Equal(BadHandle, GetLocus(1, [0]));
    }

    [Fact]
    public void GetChunkId_ParsedChunkMap_EnumeratesOnlyDefinedIds()
    {
        File.WriteAllText(
            Path.Combine(_app0Root, "playgo-chunkdefs.xml"),
            "<playgo default_chunk=\"2\"><chunk id=\"2\"/><chunk id=\"7\"/></playgo>");
        var handle = InitializeAndOpen();
        const ulong outCount = MemoryBase + 0x500;
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = ChunkIdsAddress;
        _ctx[CpuRegister.Rdx] = 8;
        _ctx[CpuRegister.Rcx] = outCount;

        Assert.Equal(0, PlayGoExports.PlayGoGetChunkId(_ctx));
        Assert.True(_ctx.TryReadUInt32(outCount, out var count));
        Assert.Equal(2u, count);
        Assert.True(_ctx.TryReadUInt16(ChunkIdsAddress, out var first));
        Assert.True(_ctx.TryReadUInt16(ChunkIdsAddress + 2, out var second));
        Assert.Equal((ushort)2, first);
        Assert.Equal((ushort)7, second);
        Assert.Equal(0, GetLocus(handle, [first, second]));
        Assert.Equal(new byte[] { LocusLocalFast, LocusLocalFast }, ReadLoci(2));
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

    private void Initialize()
    {
        Span<byte> initParams = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(initParams, InitBufferAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(initParams[8..], 0x200000);
        Assert.True(_memory.TryWrite(InitParamsAddress, initParams));

        _ctx[CpuRegister.Rdi] = InitParamsAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            PlayGoExports.PlayGoInitialize(_ctx));
    }

    private uint InitializeAndOpen()
    {
        Initialize();
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

    private void SetGetLocusArguments(uint handle, ulong chunkIds, uint count, ulong outLoci)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = chunkIds;
        _ctx[CpuRegister.Rdx] = count;
        _ctx[CpuRegister.Rcx] = outLoci;
    }

    public enum UnusableMetadataKind
    {
        ScenarioOnly,
        DatOnly,
        MalformedChunkDefinitions,
        UnrecognizedChunkDefinitions,
    }
}
