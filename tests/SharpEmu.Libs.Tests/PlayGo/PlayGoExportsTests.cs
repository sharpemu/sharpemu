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
    private const string DisableMetadataFallbackVariable = "SHARPEMU_PLAYGO_DISABLE_METADATA_FALLBACK";
    private const byte LocusNotDownloaded = 0;
    private const byte LocusLocalFast = 3;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong InitParamsAddress = MemoryBase + 0x100;
    private const ulong InitBufferAddress = MemoryBase + 0x1000;
    private const ulong HandleAddress = MemoryBase + 0x200;
    private const ulong ChunkIdsAddress = MemoryBase + 0x300;
    private const ulong LociAddress = MemoryBase + 0x400;
    private const ulong ProgressAddress = MemoryBase + 0x500;

    private readonly string? _originalApp0Root;
    private readonly string? _originalDisableMetadataFallback;
    private readonly string _app0Root;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10000);
    private readonly CpuContext _ctx;

    public PlayGoExportsTests()
    {
        _originalApp0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        _originalDisableMetadataFallback = Environment.GetEnvironmentVariable(DisableMetadataFallbackVariable);
        Environment.SetEnvironmentVariable(DisableMetadataFallbackVariable, null);
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
    [InlineData(false)]
    [InlineData(true)]
    public void Open_MetadataFallbackDisabledWithoutMetadata_ReturnsUnsupportedWithoutWritingHandle(bool app0Unset)
    {
        Environment.SetEnvironmentVariable(DisableMetadataFallbackVariable, "1");
        if (app0Unset)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", null);
        }
        else
        {
            File.WriteAllBytes(Path.Combine(_app0Root, "pakchunk7-ps5.pak"), []);
        }

        Initialize();
        byte[] originalHandle = [0xA5, 0xA5, 0xA5, 0xA5];
        Assert.True(_memory.TryWrite(HandleAddress, originalHandle));
        _ctx[CpuRegister.Rdi] = HandleAddress;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(unchecked((int)0x80B2000E), PlayGoExports.PlayGoOpen(_ctx));
        Span<byte> handleBytes = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(HandleAddress, handleBytes));
        Assert.Equal(originalHandle, handleBytes.ToArray());
        Assert.Equal(unchecked((int)0x80B20009), GetLocus(1, [0]));
    }

    [Fact]
    public void Open_MetadataFallbackDisabledWithChunkDefinitions_PreservesMetadata()
    {
        Environment.SetEnvironmentVariable(DisableMetadataFallbackVariable, "1");
        File.WriteAllText(
            Path.Combine(_app0Root, "playgo-chunkdefs.xml"),
            "<playgo><chunk id=\"7\"/></playgo>");
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [7]));
        Assert.Equal(new byte[] { LocusLocalFast }, ReadLoci(1));
        Assert.Equal(BadChunkId, GetLocus(handle, [8]));
    }

    [Theory]
    [InlineData(UnusableMetadataKind.DatOnly)]
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

    // A title that ships playgo-chunk.dat without playgo-chunkdefs.xml lists the chunks its header counts.
    [Theory]
    [InlineData(0x6F676C70u)]
    [InlineData(0x78676C70u)]
    public void GetChunkId_ChunkDatWithoutDefinitions_ListsTheHeaderChunks(uint magic)
    {
        var dat = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(dat, magic);
        BinaryPrimitives.WriteUInt16LittleEndian(dat.AsSpan(0x08), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dat.AsSpan(0x0A), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(dat.AsSpan(0x10), (uint)dat.Length);
        var sceSys = Directory.CreateDirectory(Path.Combine(_app0Root, "sce_sys"));
        File.WriteAllBytes(Path.Combine(sceSys.FullName, "playgo-chunk.dat"), dat);
        var handle = InitializeAndOpen();

        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = ChunkIdsAddress;
        _ctx[CpuRegister.Rdx] = 16;
        _ctx[CpuRegister.Rcx] = HandleAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, PlayGoExports.PlayGoGetChunkId(_ctx));
        Span<byte> written = stackalloc byte[sizeof(uint) + 3 * sizeof(ushort)];
        Assert.True(_memory.TryRead(HandleAddress, written[..sizeof(uint)]));
        Assert.True(_memory.TryRead(ChunkIdsAddress, written[sizeof(uint)..]));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(written));
        Assert.Equal(new ushort[] { 0, 1, 2 }, new[]
        {
            BinaryPrimitives.ReadUInt16LittleEndian(written[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(written[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(written[8..]),
        });

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0, 1, 2]));
        Assert.Equal(new byte[] { LocusLocalFast, LocusLocalFast, LocusLocalFast }, ReadLoci(3));
        Assert.Equal(BadChunkId, GetLocus(handle, [3]));
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

    [Fact]
    public void GetLocus_PgmChunkCount_ReportsAllChunksAndStaysPermissivePastThem()
    {
        WritePgm(3);
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0, 1, 2]));
        Assert.Equal(new byte[] { LocusLocalFast, LocusLocalFast, LocusLocalFast }, ReadLoci(3));

        // Assumed ids are not authoritative: an id past the count is not rejected.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [9]));
    }

    [Fact]
    public void GetLocus_ChunkDefinitionsTakePrecedenceOverPgm()
    {
        WritePgm(2);
        File.WriteAllText(
            Path.Combine(_app0Root, "playgo-chunkdefs.xml"),
            "<playgo default_chunk=\"5\"><chunk id=\"5\"/></playgo>");
        var handle = InitializeAndOpen();

        // The title's own chunk list stays exact: the pgm count does not add chunk 1.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [5]));
        Assert.Equal(BadChunkId, GetLocus(handle, [1]));
        Assert.Equal((0ul, 0ul), GetProgress(handle, [5]));
    }

    [Fact]
    public void GetLocus_PakChunksTakePrecedenceOverPgm()
    {
        WritePgm(2);
        File.WriteAllBytes(Path.Combine(_app0Root, "pakchunk7-PS5.pak"), []);
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0, 7]));
        Assert.Equal(BadChunkId, GetLocus(handle, [1]));
    }

    [Fact]
    public void GetProgress_PgmChunks_ReportsAFullyInstalledTitle()
    {
        WritePgm(3);
        var handle = InitializeAndOpen();

        Assert.Equal((1ul, 1ul), GetProgress(handle, [0, 1, 2]));
    }

    [Fact]
    public void GetProgress_WithoutPgm_ReportsZeroOfZero()
    {
        var handle = InitializeAndOpen();

        Assert.Equal((0ul, 0ul), GetProgress(handle, [0]));
    }

    [Theory]
    [InlineData("BAD!", 4u)]
    [InlineData("DMGP", 0u)]
    public void GetLocus_InvalidPgm_FallsBackToAuthoritativeDefault(string magic, uint count)
    {
        WritePgm(count, magic);
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0]));
        Assert.Equal(BadChunkId, GetLocus(handle, [1]));
    }

    [Fact]
    public void GetLocus_TruncatedPgm_FallsBackToAuthoritativeDefault()
    {
        var cache = Directory.CreateDirectory(Path.Combine(_app0Root, "cache_ps5"));
        File.WriteAllBytes(Path.Combine(cache.FullName, "playgo.pgm"), "DMGP"u8.ToArray());
        var handle = InitializeAndOpen();

        Assert.Equal(BadChunkId, GetLocus(handle, [1]));
    }

    [Fact]
    public void GetLocus_PakDiscoveryStillAppliesWithoutPgmOrSidecars()
    {
        File.WriteAllBytes(Path.Combine(_app0Root, "pakchunk4-PS5.pak"), []);
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0, 4]));
        Assert.Equal(BadChunkId, GetLocus(handle, [1]));
    }

    [Fact]
    public void GetLocus_ScenarioWithoutChunkList_UsesPgmChunks()
    {
        WritePgm(3);
        WriteScenarioJson();
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0, 1, 2]));
        Assert.Equal(new byte[] { LocusLocalFast, LocusLocalFast, LocusLocalFast }, ReadLoci(3));
        Assert.Equal((1ul, 1ul), GetProgress(handle, [0, 1, 2]));
    }

    [Fact]
    public void GetLocus_ScenarioWithoutChunkList_PakChunksTakePrecedenceOverPgm()
    {
        WritePgm(2);
        WriteScenarioJson();
        File.WriteAllBytes(Path.Combine(_app0Root, "pakchunk7-PS5.pak"), []);
        var handle = InitializeAndOpen();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, GetLocus(handle, [0, 7]));
        Assert.Equal(BadChunkId, GetLocus(handle, [1]));
    }

    private void WriteScenarioJson()
    {
        var sceSys = Directory.CreateDirectory(Path.Combine(_app0Root, "sce_sys"));
        File.WriteAllText(
            Path.Combine(sceSys.FullName, "playgo-scenario.json"),
            "{\"languages\":[\"en\"],\"scenarios\":[]}");
    }

    private void WritePgm(uint chunkCount, string magic = "DMGP")
    {
        var header = new byte[0x14];
        System.Text.Encoding.ASCII.GetBytes(magic, header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), chunkCount);
        var cache = Directory.CreateDirectory(Path.Combine(_app0Root, "cache_ps5"));
        File.WriteAllBytes(Path.Combine(cache.FullName, "playgo.pgm"), header);
    }

    public void Dispose()
    {
        PlayGoExports.ResetForTests();
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _originalApp0Root);
        Environment.SetEnvironmentVariable(DisableMetadataFallbackVariable, _originalDisableMetadataFallback);
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

    private (ulong Installed, ulong Total) GetProgress(uint handle, ushort[] chunkIds)
    {
        WriteChunkIds(chunkIds);
        SetGetLocusArguments(handle, ChunkIdsAddress, (uint)chunkIds.Length, ProgressAddress);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, PlayGoExports.PlayGoGetProgress(_ctx));
        var progress = new byte[sizeof(ulong) * 2];
        Assert.True(_memory.TryRead(ProgressAddress, progress));
        return (BinaryPrimitives.ReadUInt64LittleEndian(progress), BinaryPrimitives.ReadUInt64LittleEndian(progress.AsSpan(sizeof(ulong))));
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
        DatOnly,
        MalformedChunkDefinitions,
        UnrecognizedChunkDefinitions,
    }
}
