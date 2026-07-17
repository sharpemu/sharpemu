// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelAprResolveFilepathsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PrefixAddress = MemoryBase + 0x100;
    private const ulong PathListAddress = MemoryBase + 0x200;
    private const ulong FirstPathAddress = MemoryBase + 0x400;
    private const ulong SecondPathAddress = MemoryBase + 0x800;
    private const ulong IdsAddress = MemoryBase + 0xC00;
    private const ulong SizesAddress = MemoryBase + 0xD00;
    private const ulong ErrorIndexAddress = MemoryBase + 0xE00;
    private const ulong StatAddress = MemoryBase + 0x1000;
    private const ulong FileSizeAddress = MemoryBase + 0x1200;
    private const ulong FaultingAddress = 0xDEAD_0000_0000;
    private const int KernelStatSizeOffset = 72;

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"sharpemu-apr-resolve-{Guid.NewGuid():N}");
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x4000);
    private readonly CpuContext _context;

    public KernelAprResolveFilepathsTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void EmptyPrefixResolvesFileAndRegistersItForStat()
    {
        var filePath = Path.Combine(_tempRoot, "metadata.bin");
        File.WriteAllBytes(filePath, [1, 2, 3, 4, 5]);
        WriteRequest(prefix: string.Empty, [filePath]);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Resolve(count: 1));

        var fileId = ReadUInt32(IdsAddress);
        Assert.NotEqual(uint.MaxValue, fileId);
        Assert.Equal(5UL, ReadUInt64(SizesAddress));

        _context[CpuRegister.Rdi] = fileId;
        _context[CpuRegister.Rsi] = StatAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelAprGetFileStat(_context));
        Assert.Equal(5UL, ReadUInt64(StatAddress + KernelStatSizeOffset));

        _context[CpuRegister.Rdi] = fileId;
        _context[CpuRegister.Rsi] = FileSizeAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelAprCompatExports.KernelAprGetFileSize(_context));
        Assert.Equal(5UL, ReadUInt64(FileSizeAddress));
    }

    [Fact]
    public void NonemptyPrefixJoinsRelativePaths()
    {
        const string fileName = "joined.bin";
        File.WriteAllBytes(Path.Combine(_tempRoot, fileName), new byte[17]);
        WriteRequest(_tempRoot, [fileName]);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Resolve(count: 1));
        Assert.NotEqual(uint.MaxValue, ReadUInt32(IdsAddress));
        Assert.Equal(17UL, ReadUInt64(SizesAddress));
    }

    [Fact]
    public void MissingFileStopsAtFailingEntryAndReportsItsIndex()
    {
        const string presentName = "present.bin";
        File.WriteAllBytes(Path.Combine(_tempRoot, presentName), new byte[23]);
        WriteRequest(_tempRoot, [presentName, "missing.bin"]);
        WriteUInt32(IdsAddress, 0x11111111);
        WriteUInt32(IdsAddress + sizeof(uint), 0x22222222);
        var initialSizes = new byte[sizeof(ulong) * 2];
        Array.Fill(initialSizes, (byte)0xA5);
        Assert.True(_memory.TryWrite(SizesAddress, initialSizes));
        WriteUInt32(ErrorIndexAddress, 0x33333333);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            Resolve(count: 2));
        Assert.NotEqual(0x11111111U, ReadUInt32(IdsAddress));
        Assert.Equal(23UL, ReadUInt64(SizesAddress));
        Assert.Equal(0x22222222U, ReadUInt32(IdsAddress + sizeof(uint)));
        Assert.Equal(0xA5A5A5A5A5A5A5A5UL, ReadUInt64(SizesAddress + sizeof(ulong)));
        Assert.Equal(1U, ReadUInt32(ErrorIndexAddress));
    }

    [Fact]
    public void NullIdsIsInvalidEvenWhenSizesAreWritable()
    {
        var filePath = Path.Combine(_tempRoot, "size-only.bin");
        File.WriteAllBytes(filePath, new byte[31]);
        WriteRequest(prefix: null, [filePath]);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Resolve(count: 1, idsAddress: 0));
    }

    [Fact]
    public void ZeroCountSucceedsWithRequiredPointers()
    {
        _memory.WriteCString(PrefixAddress, string.Empty);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Resolve(count: 0));
    }

    [Fact]
    public void FaultingPrefixPointerReturnsMemoryFaultBeforeOutputs()
    {
        var filePath = Path.Combine(_tempRoot, "unread.bin");
        File.WriteAllBytes(filePath, [1]);
        WriteRequest(prefix: null, [filePath]);
        WriteUInt32(IdsAddress, 0x12345678);
        WriteUInt64(SizesAddress, 0x123456789ABCDEF0);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Resolve(count: 1, prefixAddress: FaultingAddress));
        Assert.Equal(0x12345678U, ReadUInt32(IdsAddress));
        Assert.Equal(0x123456789ABCDEF0UL, ReadUInt64(SizesAddress));
    }

    [Fact]
    public void FaultingPathAndOutputPointersReturnMemoryFault()
    {
        _memory.WriteCString(PrefixAddress, string.Empty);
        WriteUInt64(PathListAddress, FaultingAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Resolve(count: 1));
        Assert.Equal(0U, ReadUInt32(IdsAddress));

        var filePath = Path.Combine(_tempRoot, "fault-output.bin");
        File.WriteAllBytes(filePath, [1]);
        WriteRequest(string.Empty, [filePath]);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Resolve(count: 1, sizesAddress: FaultingAddress));
    }

    [Fact]
    public void InvalidPathPointerIsNotReinterpretedAsInlinePathText()
    {
        const ulong pointerWhoseBytesSpellFoo = 0x0000_0000_006F_6F66;
        File.WriteAllBytes(Path.Combine(_tempRoot, "foo"), [0x42]);
        _memory.WriteCString(PrefixAddress, _tempRoot);
        WriteUInt64(PathListAddress, pointerWhoseBytesSpellFoo);
        WriteUInt32(IdsAddress, 0x11111111);
        WriteUInt64(SizesAddress, 0x2222222222222222);
        WriteUInt32(ErrorIndexAddress, 0x33333333);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Resolve(count: 1));
        Assert.Equal(0x11111111U, ReadUInt32(IdsAddress));
        Assert.Equal(0x2222222222222222UL, ReadUInt64(SizesAddress));
        Assert.Equal(0x33333333U, ReadUInt32(ErrorIndexAddress));
    }

    [Fact]
    public void FaultingErrorIndexTakesMemoryFaultPrecedenceOnMissingFile()
    {
        WriteRequest(_tempRoot, ["missing.bin"]);
        _context[CpuRegister.Rdi] = PrefixAddress;
        _context[CpuRegister.Rsi] = PathListAddress;
        _context[CpuRegister.Rdx] = 1;
        _context[CpuRegister.Rcx] = IdsAddress;
        _context[CpuRegister.R8] = SizesAddress;
        _context[CpuRegister.R9] = FaultingAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.KernelAprResolveFilepathsWithPrefixToIdsAndFileSizes(_context));
    }

    [Fact]
    public void AprGetFileSizeValidatesIdAndOutputPointer()
    {
        _context[CpuRegister.Rdi] = 0xF0000001;
        _context[CpuRegister.Rsi] = FileSizeAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelAprCompatExports.KernelAprGetFileSize(_context));

        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelAprCompatExports.KernelAprGetFileSize(_context));
    }

    public void Dispose()
    {
        Directory.Delete(_tempRoot, recursive: true);
    }

    private int Resolve(
        ulong count,
        ulong prefixAddress = PrefixAddress,
        ulong idsAddress = IdsAddress,
        ulong sizesAddress = SizesAddress)
    {
        _context[CpuRegister.Rdi] = prefixAddress;
        _context[CpuRegister.Rsi] = PathListAddress;
        _context[CpuRegister.Rdx] = count;
        _context[CpuRegister.Rcx] = idsAddress;
        _context[CpuRegister.R8] = sizesAddress;
        _context[CpuRegister.R9] = ErrorIndexAddress;
        return KernelMemoryCompatExports.KernelAprResolveFilepathsWithPrefixToIdsAndFileSizes(_context);
    }

    private void WriteRequest(string? prefix, string[] paths)
    {
        if (prefix is not null)
        {
            _memory.WriteCString(PrefixAddress, prefix);
        }

        var pathAddresses = new[] { FirstPathAddress, SecondPathAddress };
        Assert.True(paths.Length <= pathAddresses.Length);
        for (var i = 0; i < paths.Length; i++)
        {
            _memory.WriteCString(pathAddresses[i], paths[i]);
            WriteUInt64(PathListAddress + ((ulong)i * sizeof(ulong)), pathAddresses[i]);
        }
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
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
}
