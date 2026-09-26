// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

[CollectionDefinition("Ngs2State", DisableParallelization = true)]
public sealed class Ngs2StateCollection;

[Collection("Ngs2State")]
public sealed class Ngs2ExportsTests : IDisposable
{
    private const int InvalidRackHandle = unchecked((int)0x804A0261);
    private const int TryAgain = unchecked((int)0x80020023);

    public Ngs2ExportsTests() => Ngs2Exports.ResetStateForTests();

    public void Dispose() => Ngs2Exports.ResetStateForTests();

    [Fact]
    public void FirstRackUsesZeroHandleAndDoesNotReuseDestroyedHandles()
    {
        using var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var outputPage = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.NotEqual(0UL, outputPage);
        var systemOut = outputPage + 0x100;
        var rackOut = outputPage + 0x108;
        var voiceOut = outputPage + 0x110;
        var replacementRackOut = outputPage + 0x118;
        var systemHandle = 0UL;

        try
        {
            Assert.Equal(0, Ngs2Exports.Ngs2SystemCreateWithAllocator(Reg(context, rdx: systemOut)));
            Assert.True(context.TryReadUInt64(systemOut, out systemHandle));
            Assert.NotEqual(0UL, systemHandle);

            Assert.Equal(
                0,
                Ngs2Exports.Ngs2RackCreateWithAllocator(
                    Reg(context, rdi: systemHandle, rsi: 1, r8: rackOut)));
            Assert.True(context.TryReadUInt64(rackOut, out var rackHandle));
            Assert.Equal(0UL, rackHandle);

            Assert.Equal(
                0,
                Ngs2Exports.Ngs2RackGetVoiceHandle(Reg(context, rdi: 0, rsi: 0, rdx: voiceOut)));
            Assert.True(context.TryReadUInt64(voiceOut, out var voiceHandle));
            Assert.NotEqual(0UL, voiceHandle);

            Assert.Equal(0, Ngs2Exports.Ngs2RackDestroy(Reg(context, rdi: rackHandle)));
            Assert.Equal(
                0,
                Ngs2Exports.Ngs2RackCreateWithAllocator(
                    Reg(context, rdi: systemHandle, rsi: 2, r8: replacementRackOut)));
            Assert.True(context.TryReadUInt64(replacementRackOut, out var replacementRackHandle));
            Assert.NotEqual(rackHandle, replacementRackHandle);

            Assert.Equal(
                InvalidRackHandle,
                Ngs2Exports.Ngs2RackGetVoiceHandle(
                    Reg(context, rdi: rackHandle, rsi: 0, rdx: voiceOut)));
        }
        finally
        {
            if (systemHandle != 0)
            {
                Ngs2Exports.Ngs2SystemDestroy(Reg(context, rdi: systemHandle));
            }
        }
    }

    [Fact]
    public void VoiceRequestForZeroRackBeforeExplicitRackCreationIsAccepted()
    {
        using var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var outputPage = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.NotEqual(0UL, outputPage);
        var voiceOut = outputPage + 0x110;

        Assert.Equal(
            0,
            Ngs2Exports.Ngs2RackGetVoiceHandle(
                Reg(context, rdi: 0, rsi: 0, rdx: voiceOut)));
        Assert.True(context.TryReadUInt64(voiceOut, out var voiceHandle));
        Assert.NotEqual(0UL, voiceHandle);
    }

    [Fact]
    public void RackCreation_WhenHandleIdsAreExhausted_PreservesOutputAndRax()
    {
        using var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var outputPage = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.NotEqual(0UL, outputPage);
        var systemOut = outputPage + 0x100;
        var rackOut = outputPage + 0x108;
        var systemHandle = 0UL;
        const ulong sentinel = 0xDEADBEEFDEADBEEFUL;

        try
        {
            Assert.Equal(0, Ngs2Exports.Ngs2SystemCreateWithAllocator(Reg(context, rdx: systemOut)));
            Assert.True(context.TryReadUInt64(systemOut, out systemHandle));
            Assert.True(context.TryWriteUInt64(rackOut, sentinel));

            var nextRackHandle = typeof(Ngs2Exports).GetField(
                "_nextRackHandle",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(nextRackHandle);
            nextRackHandle.SetValue(null, ulong.MaxValue);

            Assert.Equal(
                TryAgain,
                Ngs2Exports.Ngs2RackCreateWithAllocator(
                    Reg(context, rdi: systemHandle, rsi: 1, r8: rackOut)));
            Assert.True(context.TryReadUInt64(rackOut, out var output));
            Assert.Equal(sentinel, output);
            Assert.Equal(unchecked((ulong)TryAgain), context[CpuRegister.Rax]);
        }
        finally
        {
            if (systemHandle != 0)
            {
                Ngs2Exports.Ngs2SystemDestroy(Reg(context, rdi: systemHandle));
            }
        }
    }

    private static CpuContext Reg(
        CpuContext context,
        ulong rdi = 0,
        ulong rsi = 0,
        ulong rdx = 0,
        ulong r8 = 0)
    {
        context[CpuRegister.Rdi] = rdi;
        context[CpuRegister.Rsi] = rsi;
        context[CpuRegister.Rdx] = rdx;
        context[CpuRegister.R8] = r8;
        return context;
    }
}
