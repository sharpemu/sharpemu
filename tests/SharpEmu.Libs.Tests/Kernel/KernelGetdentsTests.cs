// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Locks the current directory-open + getdents/getdirentries contract as implemented
// in KernelMemoryCompatExports. Observed (not FreeBSD-canonical) behavior includes:
// one 512-byte fixed dirent per call, no "."/".." entries, and DT_DIR/DT_REG types
// derived from host Directory.Exists. Prefer tests-only unless a clear Orbis bug appears
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelGetdentsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x2000;
    private const ulong PathAddress = MemoryBase + 0x100;
    private const ulong BufferAddress = MemoryBase + 0x400;
    private const ulong BasePointerAddress = MemoryBase + 0x700;
    private const ulong StatAddress = MemoryBase + 0x800;

    // Matches KernelMemoryCompatExports.O_DIRECTORY / open flags used by guests
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x00020000;

    // Fixed dirent layout produced by KernelGetdirentriesCore
    private const int DirentSize = 512;
    private const int DirentReclenOffset = 4;
    private const int DirentTypeOffset = 6;
    private const int DirentNamlenOffset = 7;
    private const int DirentNameOffset = 8;
    private const byte DtDir = 4;
    private const byte DtReg = 8;
    private const ushort KernelStatModeDirectory = 0x41FF;
    private const int KernelStatStModeOffset = 8;

    private const string GuestMount = "/sharpemu_getdents_mnt";

    private readonly string _tempRoot;
    private readonly string _mountRoot;

    public KernelGetdentsTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-getdents-{Guid.NewGuid():N}");
        _mountRoot = Path.Combine(_tempRoot, "root");
        Directory.CreateDirectory(_mountRoot);
        KernelMemoryCompatExports.RegisterGuestPathMount(GuestMount, _mountRoot);
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.UnregisterGuestPathMount(GuestMount);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void OpenDirectory_Getdents_CollectsNames_ThenEofAndClose()
    {
        // Host entries; enumeration uses OrdinalIgnoreCase name order and skips "."/".."
        File.WriteAllBytes(Path.Combine(_mountRoot, "zeta.bin"), [1]);
        File.WriteAllBytes(Path.Combine(_mountRoot, "alpha.bin"), [2]);
        Directory.CreateDirectory(Path.Combine(_mountRoot, "subdir"));

        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);

        var fd = OpenPath(context, memory, GuestMount, O_RDONLY | O_DIRECTORY);
        Assert.True(fd >= 0, $"directory open failed with 0x{unchecked((uint)fd):X8}");

        try
        {
            var names = new List<string>();
            var types = new Dictionary<string, byte>(StringComparer.Ordinal);
            for (var i = 0; i < 32; i++)
            {
                var result = CallGetdents(context, fd, BufferAddress, DirentSize);
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);

                var bytesReturned = unchecked((int)context[CpuRegister.Rax]);
                if (bytesReturned == 0)
                {
                    break;
                }

                Assert.Equal(DirentSize, bytesReturned);
                var (name, type, reclen, namlen) = ReadDirent(memory, BufferAddress);
                Assert.Equal(DirentSize, reclen);
                Assert.Equal(name.Length, namlen);
                Assert.False(string.IsNullOrEmpty(name));
                Assert.NotEqual(".", name);
                Assert.NotEqual("..", name);
                names.Add(name);
                types[name] = type;
            }

            // Snapshot order is case-insensitive name sort of host children only
            Assert.Equal(["alpha.bin", "subdir", "zeta.bin"], names);
            Assert.Equal(DtReg, types["alpha.bin"]);
            Assert.Equal(DtReg, types["zeta.bin"]);
            Assert.Equal(DtDir, types["subdir"]);

            // EOF is sticky until the fd is reopened: another call still returns 0 bytes
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CallGetdents(context, fd, BufferAddress, DirentSize));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
        finally
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CloseFd(context, fd));
        }

        // Closed directory fd is no longer getdents-capable
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            CallGetdents(context, fd, BufferAddress, DirentSize));
    }

    [Fact]
    public void Getdirentries_WritesBasePointerAndReturnsOneEntryPerCall()
    {
        File.WriteAllBytes(Path.Combine(_mountRoot, "one.txt"), [1]);
        File.WriteAllBytes(Path.Combine(_mountRoot, "two.txt"), [2]);

        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        var fd = OpenPath(context, memory, GuestMount, O_RDONLY | O_DIRECTORY);
        Assert.True(fd >= 0);

        try
        {
            // basep receives the pre-advance cursor (0, then 1, then 2 at EOF)
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                CallGetdirentries(context, fd, BufferAddress, DirentSize, BasePointerAddress));
            Assert.Equal(DirentSize, unchecked((int)context[CpuRegister.Rax]));
            Assert.Equal(0UL, ReadUInt64(memory, BasePointerAddress));
            var first = ReadDirent(memory, BufferAddress).Name;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                CallGetdirentries(context, fd, BufferAddress, DirentSize, BasePointerAddress));
            Assert.Equal(DirentSize, unchecked((int)context[CpuRegister.Rax]));
            Assert.Equal(1UL, ReadUInt64(memory, BasePointerAddress));
            var second = ReadDirent(memory, BufferAddress).Name;

            Assert.Equal(
                new[] { first, second }.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                new[] { first, second });
            Assert.Equal(2, new HashSet<string>([first, second], StringComparer.Ordinal).Count);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                CallGetdirentries(context, fd, BufferAddress, DirentSize, BasePointerAddress));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.Equal(2UL, ReadUInt64(memory, BasePointerAddress));
        }
        finally
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CloseFd(context, fd));
        }
    }

    [Fact]
    public void Getdents_OnRegularFileFd_ReturnsNotFound()
    {
        File.WriteAllBytes(Path.Combine(_mountRoot, "plain.bin"), [9]);

        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        var fd = OpenPath(context, memory, $"{GuestMount}/plain.bin", O_RDONLY);
        Assert.True(fd >= 0);

        try
        {
            // Regular file descriptors live in _openFiles, not _openDirectories
            var result = CallGetdents(context, fd, BufferAddress, DirentSize);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        }
        finally
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CloseFd(context, fd));
        }
    }

    [Fact]
    public void Getdents_RejectsInvalidArguments()
    {
        Directory.CreateDirectory(Path.Combine(_mountRoot, "empty"));

        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        var fd = OpenPath(context, memory, $"{GuestMount}/empty", O_RDONLY | O_DIRECTORY);
        Assert.True(fd >= 0);

        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                CallGetdents(context, -1, BufferAddress, DirentSize));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                CallGetdents(context, fd, bufferAddress: 0, requested: DirentSize));
            // Buffer smaller than the fixed 512-byte dirent is rejected outright
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                CallGetdents(context, fd, BufferAddress, requested: DirentSize - 1));
        }
        finally
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CloseFd(context, fd));
        }
    }

    [Fact]
    public void Fstat_OnDirectoryFd_ReportsDirectoryMode()
    {
        Directory.CreateDirectory(Path.Combine(_mountRoot, "statme"));

        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        // Opening an existing directory without O_DIRECTORY still takes the directory path
        var fd = OpenPath(context, memory, $"{GuestMount}/statme", O_RDONLY);
        Assert.True(fd >= 0);

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)fd);
            context[CpuRegister.Rsi] = StatAddress;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelFstat(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            Span<byte> modeBytes = stackalloc byte[sizeof(ushort)];
            Assert.True(memory.TryRead(StatAddress + KernelStatStModeOffset, modeBytes));
            Assert.Equal(KernelStatModeDirectory, BinaryPrimitives.ReadUInt16LittleEndian(modeBytes));
        }
        finally
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CloseFd(context, fd));
        }
    }

    [Fact]
    public void Open_ODirectoryOnMissingPath_ReturnsNotFound()
    {
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(PathAddress, $"{GuestMount}/no_such_dir");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = unchecked((ulong)(O_RDONLY | O_DIRECTORY));

        var result = KernelMemoryCompatExports.KernelOpenUnderscore(context);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    private static int OpenPath(CpuContext context, FakeCpuMemory memory, string guestPath, int flags)
    {
        memory.WriteCString(PathAddress, guestPath);
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = unchecked((ulong)flags);
        var result = KernelMemoryCompatExports.KernelOpenUnderscore(context);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return result;
        }

        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CallGetdents(CpuContext context, int fd, ulong bufferAddress, int requested)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)fd);
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = unchecked((ulong)requested);
        return KernelMemoryCompatExports.KernelGetdents(context);
    }

    private static int CallGetdirentries(
        CpuContext context,
        int fd,
        ulong bufferAddress,
        int requested,
        ulong basePointerAddress)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)fd);
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = unchecked((ulong)requested);
        context[CpuRegister.Rcx] = basePointerAddress;
        return KernelMemoryCompatExports.KernelGetdirentries(context);
    }

    private static int CloseFd(CpuContext context, int fd)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)fd);
        return KernelMemoryCompatExports.KernelClose(context);
    }

    private static (string Name, byte Type, ushort Reclen, byte Namlen) ReadDirent(
        FakeCpuMemory memory,
        ulong bufferAddress)
    {
        Span<byte> payload = stackalloc byte[DirentSize];
        Assert.True(memory.TryRead(bufferAddress, payload));
        var reclen = BinaryPrimitives.ReadUInt16LittleEndian(payload[DirentReclenOffset..]);
        var type = payload[DirentTypeOffset];
        var namlen = payload[DirentNamlenOffset];
        var name = Encoding.UTF8.GetString(payload.Slice(DirentNameOffset, namlen));
        return (name, type, reclen, namlen);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
