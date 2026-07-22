// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.LibcStdio;
using SharpEmu.Libs.Messenger;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class MessengerCompatExportsTests
{
    [Fact]
    public void Cosf_UsesScalarXmmArgumentAndReturn()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        var input = 0.5f;
        var inputBits = unchecked((uint)BitConverter.SingleToInt32Bits(input));
        context[CpuRegister.Rdi] = 0xDEAD_BEEF; // Must not be used as the argument.
        context.SetXmmRegister(0, 0xAABB_CCDD_0000_0000UL | inputBits, 0x1122_3344_5566_7788UL);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, MessengerCompatExports.Cosf(context));

        context.GetXmmRegister(0, out var low, out var high);
        var expectedBits = unchecked((uint)BitConverter.SingleToInt32Bits(MathF.Cos(input)));
        Assert.Equal(expectedBits, unchecked((uint)low));
        Assert.Equal(0xAABB_CCDDUL, low >> 32);
        Assert.Equal(0x1122_3344_5566_7788UL, high);
    }

    [Fact]
    public void CtypeCaseTables_MapAsciiCharacters()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.GetPtolower(context));
        var lower = unchecked((nint)(long)context[CpuRegister.Rax]);
        Assert.Equal((short)'a', Marshal.ReadInt16(lower + ('A' * sizeof(short))));
        Assert.Equal((short)'z', Marshal.ReadInt16(lower + ('z' * sizeof(short))));
        Assert.Equal((short)-1, Marshal.ReadInt16(lower - sizeof(short)));

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.GetPtoupper(context));
        var upper = unchecked((nint)(long)context[CpuRegister.Rax]);
        Assert.Equal((short)'A', Marshal.ReadInt16(upper + ('a' * sizeof(short))));
        Assert.Equal((short)'Z', Marshal.ReadInt16(upper + ('Z' * sizeof(short))));
        Assert.Equal((short)-1, Marshal.ReadInt16(upper - sizeof(short)));
    }

    [Fact]
    public void Il2CppLookup_ReturnsPointerInRaxWithoutWritingRsi()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        const ulong outputAddress = memoryBase + 0x200;
        const ulong resolvedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        memory.WriteCString(nameAddress, "il2cpp_test");
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = nameAddress;
        context[CpuRegister.Rsi] = outputAddress; // Live caller state, not an output pointer.
        Assert.True(context.TryWriteUInt64(outputAddress, 0xCAFE_BABE));

        var result = Il2CppApiLookupAbi.SetResult(context, resolved: true, resolvedAddress);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(resolvedAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(outputAddress, out var output));
        Assert.Equal(0xCAFE_BABEUL, output);
    }

    [Fact]
    public void Il2CppLookup_MissingApiReturnsNullWithoutWritingRsi()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        const ulong outputAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        memory.WriteCString(nameAddress, "il2cpp_missing");
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = nameAddress;
        context[CpuRegister.Rsi] = outputAddress;
        Assert.True(context.TryWriteUInt64(outputAddress, 0xCAFE_BABE));

        var result = Il2CppApiLookupAbi.SetResult(context, resolved: false, address: 0);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(outputAddress, out var output));
        Assert.Equal(0xCAFE_BABEUL, output);
    }

    [Fact]
    public void TrackedLibcHeapFallback_ReadsAndWritesHostAllocation()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 16;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Malloc(context));
        var address = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);

        try
        {
            Assert.True(KernelMemoryCompatExports.TryWriteUInt64Compat(context, address, 0x1234_5678_9ABC_DEF0UL));
            Span<byte> bytes = stackalloc byte[8];
            Assert.True(KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, bytes));
            Assert.True(KernelMemoryCompatExports.TryReadUInt64Compat(context, address, out var value));
            Assert.Equal(0x1234_5678_9ABC_DEF0UL, value);
        }
        finally
        {
            context[CpuRegister.Rdi] = address;
            _ = KernelMemoryCompatExports.Free(context);
        }
    }
}
