// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests.LibcStdio;

public sealed class LibcStdioExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong PathAddress = BaseAddress + 0x100;
    private const ulong ModeAddress = BaseAddress + 0x200;
    private const ulong TlsAddress = BaseAddress + 0x800;
    private const ulong ErrnoAddress = TlsAddress + 0x40;

    [Fact]
    public void Fopen_RejectsEmptyPath()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, string.Empty);
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(2, ReadErrno(memory));
    }

    [Fact]
    public void Fopen_RejectsInvalidMode()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, "/app0/file.bin");
        memory.WriteCString(ModeAddress, "invalid");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(22, ReadErrno(memory));
    }

    [Fact]
    public void Fopen_RejectsUnreadablePathPointer()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = BaseAddress + 0x2000;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(14, ReadErrno(memory));
    }

    [Fact]
    public void Fopen_UnresolvedGuestPathSetsEnoent()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, "/sharpemu-unregistered/file.bin");
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(2, ReadErrno(memory));
    }

    [Fact]
    public void Freopen_RejectsEmptyPath()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, string.Empty);
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;
        context[CpuRegister.Rdx] = 0x1234;

        var result = LibcStdioExports.Freopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(2, ReadErrno(memory));
    }

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x1000);
        return new CpuContext(memory, Generation.Gen5)
        {
            FsBase = TlsAddress,
        };
    }

    private static int ReadErrno(FakeCpuMemory memory)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(ErrnoAddress, value));
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }
}
