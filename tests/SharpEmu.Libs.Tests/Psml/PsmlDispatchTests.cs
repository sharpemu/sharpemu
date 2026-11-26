// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.Psml;
using Xunit;

namespace SharpEmu.Libs.Tests.Psml;

public sealed class PsmlDispatchTests
{
    [Theory]
    [InlineData(900, true)]
    [InlineData(1000, false)]
    [InlineData(1100, false)]
    public void DispatchPacketPreservesTheMotionVectorConvention(int variant, bool jittered)
    {
        const ulong span = 0x1100;
        const ulong parameters = 0x1200;
        const ulong packetAddress = 0x1400;
        var cpu = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        cpu[CpuRegister.Rsi] = span;
        cpu[CpuRegister.Rdx] = parameters;
        Assert.True(cpu.TryWriteUInt64(span, packetAddress));
        Assert.True(cpu.TryWriteUInt32(span + 8, SuperResolutionPacket.Dwords));

        var result = variant switch
        {
            900 => PsmlExports.PsmlMfsrGetDispatchMfsrPacket900(cpu),
            1000 => PsmlExports.PsmlMfsrGetDispatchMfsrPacket1000(cpu),
            _ => PsmlExports.PsmlMfsrGetDispatchMfsrPacket1100(cpu),
        };

        Assert.Equal(0, result);
        Span<uint> packet = stackalloc uint[SuperResolutionPacket.Dwords];
        Assert.True(cpu.Memory.TryRead(packetAddress, MemoryMarshal.AsBytes(packet)));
        Assert.True(SuperResolutionPacket.TryRead(packet[1..], out var dispatch));
        Assert.Equal(jittered, dispatch.MotionVectorsJittered);
    }
}
