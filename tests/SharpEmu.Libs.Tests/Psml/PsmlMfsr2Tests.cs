// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Psml;
using Xunit;

namespace SharpEmu.Libs.Tests.Psml;

[Collection("PSML capture")]
public sealed class PsmlMfsr2Tests
{
    [Fact]
    public void ProContextEmitsADispatchAndKeepsItsSharedResourceAlive()
    {
        const ulong output = 0x1100;
        const ulong sharedParameters = 0x1200;
        const ulong contextParameters = 0x1240;
        const ulong blocks = 0x1300;
        const ulong contextBlocks = 0x1310;
        const ulong dispatchParameters = 0x1400;
        const ulong commandSpan = 0x1500;
        const ulong texture = 0x1600;
        const ulong packets = 0x4000;
        const ulong shared = 0x200000;
        const ulong context = 0x400000;

        var cpu = new CpuContext(new FakeCpuMemory(0x1000, 0x600000), Generation.Gen5);
        var previousModel = ConsoleHardware.IsPro;
        ulong sharedHandle = 0;
        ulong contextHandle = 0;
        try
        {
            ConsoleHardware.Configure(pro: false);
            Assert.NotEqual(0, PsmlExports.PsmlMfsr2Init(cpu));
            Assert.NotEqual(0, Call(PsmlExports.PsmlMfsr2GetSharedResourcesInitRequirement, output, sharedParameters));
            ConsoleHardware.Configure(pro: true);
            Assert.Equal(0, PsmlExports.PsmlMfsr2Init(cpu));
            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2GetSharedResourcesInitRequirement, output, sharedParameters));
            Assert.True(cpu.TryReadUInt64(output, out var blockSize));
            Assert.True(cpu.TryReadUInt64(output + 8, out var blockAlignment));
            Assert.True(cpu.TryReadUInt64(output + 16, out var blockCount));
            Assert.Equal(0x200000UL, blockSize);
            Assert.Equal(0x200000UL, blockAlignment);
            Assert.Equal(1UL, blockCount);

            Assert.True(cpu.TryWriteUInt64(sharedParameters + 8, blocks));
            Assert.True(cpu.TryWriteUInt64(sharedParameters + 16, blockCount));
            Assert.True(cpu.TryWriteUInt64(blocks, shared));
            Assert.True(cpu.TryWriteUInt64(blocks + 8, blockSize));
            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2CreateSharedResources, output, sharedParameters));
            Assert.True(cpu.TryReadUInt64(output, out sharedHandle));
            Assert.NotEqual(0UL, sharedHandle);
            Assert.NotEqual(shared, sharedHandle);

            Assert.True(cpu.TryWriteUInt64(contextParameters + 8, sharedHandle));
            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2GetContextInitRequirement, output, contextParameters));
            Assert.True(cpu.TryWriteUInt64(contextParameters + 16, contextBlocks));
            Assert.True(cpu.TryWriteUInt64(contextParameters + 24, blockCount));
            Assert.True(cpu.TryWriteUInt16(contextParameters + 40, 3840));
            Assert.True(cpu.TryWriteUInt16(contextParameters + 42, 2160));
            Assert.True(cpu.TryWriteUInt32(contextParameters + 44, 1));
            Assert.True(cpu.TryWriteUInt64(contextBlocks, context));
            Assert.True(cpu.TryWriteUInt64(contextBlocks + 8, blockSize));
            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2CreateContext, output, contextParameters));
            Assert.True(cpu.TryReadUInt64(output, out contextHandle));
            Assert.NotEqual(0UL, contextHandle);
            Assert.NotEqual(context, contextHandle);
            Assert.NotEqual(0, Call(PsmlExports.PsmlMfsr2ReleaseSharedResources, sharedHandle));

            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2GetDispatchPacketsSizeInDwords, contextHandle, output));
            Assert.True(cpu.TryReadUInt32(output, out var dwords));
            Assert.Equal(0x800U, dwords);
            foreach (var offset in new ulong[] { 0, 8, 16, 24, 32 })
            {
                Assert.True(cpu.TryWriteUInt64(dispatchParameters + offset, texture));
            }
            Assert.True(cpu.TryWriteUInt32(texture, 0x1234));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 48, BitConverter.SingleToUInt32Bits(1)));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 52, 8));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 56, 16));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 60, 1920));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 64, 1080));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 68, BitConverter.SingleToUInt32Bits(-0.25f)));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 100, 1));
            Assert.True(cpu.TryWriteUInt32(dispatchParameters + 104, (uint)SuperResolutionFlags.ReverseDepth));
            Assert.True(cpu.TryWriteUInt64(commandSpan, packets));
            cpu[CpuRegister.Rdx] = dispatchParameters;
            Assert.True(cpu.TryWriteUInt32(commandSpan + 8, dwords - 1));
            Assert.NotEqual(0, Call(PsmlExports.PsmlMfsr2GetDispatchPackets, contextHandle, commandSpan));
            Assert.True(cpu.TryWriteUInt32(commandSpan + 8, dwords));
            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2GetDispatchPackets, contextHandle, commandSpan));

            var packet = new uint[dwords];
            Assert.True(cpu.Memory.TryRead(packets, MemoryMarshal.AsBytes(packet.AsSpan())));
            Assert.True(SuperResolutionPacket.TryRead(packet.AsSpan(1, SuperResolutionPacket.Dwords - 1), out var dispatch));
            Assert.Equal(dwords - SuperResolutionPacket.Dwords, PacketHeader.Length(packet[SuperResolutionPacket.Dwords]));
            Assert.Equal((3840U, 2160U), (dispatch.OutputWidth, dispatch.OutputHeight));
            Assert.Equal((1920U, 1080U, 8U, 16U), (dispatch.RenderWidth, dispatch.RenderHeight, dispatch.RenderOffsetX, dispatch.RenderOffsetY));
            Assert.Equal(1U, dispatch.InputColorSpace);
            Assert.Equal(-0.25f, dispatch.JitterX);
            Assert.Equal(SuperResolutionFlags.ReverseDepth, dispatch.Flags);
            Assert.True(dispatch.Reset);
            Assert.True(dispatch.Has(SuperResolutionInput.Depth));
            Assert.True(dispatch.Has(SuperResolutionInput.Exposure));
            Assert.False(dispatch.Has(SuperResolutionInput.PreviousDepth));
            Assert.Equal(0x1234U, dispatch.Texture(SuperResolutionInput.Depth)[0]);

            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2ReleaseContext, contextHandle));
            Assert.NotEqual(0, Call(PsmlExports.PsmlMfsr2GetDispatchPacketsSizeInDwords, contextHandle, output));
            Assert.Equal(0, Call(PsmlExports.PsmlMfsr2ReleaseSharedResources, sharedHandle));
        }
        finally
        {
            Call(PsmlExports.PsmlMfsr2ReleaseContext, contextHandle);
            Call(PsmlExports.PsmlMfsr2ReleaseSharedResources, sharedHandle);
            ConsoleHardware.Configure(previousModel);
        }

        int Call(Func<CpuContext, int> export, ulong rdi, ulong rsi = 0)
        {
            cpu[CpuRegister.Rdi] = rdi;
            cpu[CpuRegister.Rsi] = rsi;
            return export(cpu);
        }
    }
}
