// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// pthread_getspecific is answered by an emitted native stub reading
/// GuestFastPath's native per-thread tables, so the managed exports must keep
/// writing and reading exactly that storage. These tests drive the exports and
/// then assert through GuestFastPath (what the stub sees) as well as through the
/// export itself.
/// </summary>
[Collection("GuestFastPath")]
public sealed class PthreadSpecificSemanticsTests
{
    private const ulong MemoryBase = 0x6_0010_0000;

    private static (CpuContext Context, FakeCpuMemory Memory) NewContext(ulong memoryBase)
    {
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        return (new CpuContext(memory, Generation.Gen5), memory);
    }

    private static int CreateKey(CpuContext context, ulong outAddress, ulong destructor = 0)
    {
        context[CpuRegister.Rdi] = outAddress;
        context[CpuRegister.Rsi] = destructor;
        var result = KernelPthreadExtendedCompatExports.PosixPthreadKeyCreate(context);
        Assert.Equal(0, result);
        Span<byte> raw = stackalloc byte[4];
        Assert.True(context.Memory.TryRead(outAddress, raw));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw);
    }

    private static ulong Getspecific(CpuContext context, int key)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)(long)key);
        context[CpuRegister.Rax] = 0xDEAD_DEAD_DEAD_DEADUL;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadGetspecific(context));
        return context[CpuRegister.Rax];
    }

    private static int Setspecific(CpuContext context, int key, ulong value)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)(long)key);
        context[CpuRegister.Rsi] = value;
        return KernelPthreadExtendedCompatExports.PosixPthreadSetspecific(context);
    }

    [Fact]
    public void SetspecificValueIsVisibleToTheExportAndToTheNativeTable()
    {
        var (context, _) = NewContext(MemoryBase);
        var key = CreateKey(context, MemoryBase + 0x100);

        Assert.Equal(0UL, Getspecific(context, key));
        Assert.Equal(0, Setspecific(context, key, 0xABCD_1234UL));

        Assert.Equal(0xABCD_1234UL, Getspecific(context, key));
        Assert.Equal(
            0xABCD_1234UL,
            GuestFastPath.GetSpecific(KernelPthreadState.GetCurrentThreadHandle(), key));
    }

    [Fact]
    public void SetspecificRejectsAKeyThatWasNeverCreated()
    {
        var (context, _) = NewContext(MemoryBase + 0x1_0000);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            Setspecific(context, 0x4000, 1));
        Assert.Equal(0UL, Getspecific(context, 0x4000));
    }

    [Fact]
    public void KeyDeleteMakesTheValueReadBackUnset()
    {
        var (context, _) = NewContext(MemoryBase + 0x2_0000);
        var key = CreateKey(context, MemoryBase + 0x2_0100);
        Assert.Equal(0, Setspecific(context, key, 0x55UL));
        Assert.Equal(0x55UL, Getspecific(context, key));

        context[CpuRegister.Rdi] = unchecked((ulong)(long)key);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PosixPthreadKeyDelete(context));

        // Both the export and the native table the stub reads must report unset,
        // otherwise the stub would keep handing out the deleted key's value.
        Assert.Equal(0UL, Getspecific(context, key));
        Assert.Equal(
            0UL,
            GuestFastPath.GetSpecific(KernelPthreadState.GetCurrentThreadHandle(), key));
    }

    [Fact]
    public void NegativeAndOutOfRangeKeysReadZero()
    {
        var (context, _) = NewContext(MemoryBase + 0x3_0000);

        Assert.Equal(0UL, Getspecific(context, -1));
        Assert.Equal(0UL, Getspecific(context, GuestFastPath.TlsSlotCount + 1));
    }

    [Fact]
    public void ValuesDoNotLeakBetweenThreads()
    {
        var (context, memory) = NewContext(MemoryBase + 0x4_0000);
        var key = CreateKey(context, MemoryBase + 0x4_0100);
        Assert.Equal(0, Setspecific(context, key, 0x1111UL));

        ulong seen = 0;
        ulong mine = 0;
        var setResult = -1;
        Exception? failure = null;
        var other = new Thread(() =>
        {
            try
            {
                var otherContext = new CpuContext(memory, Generation.Gen5);
                seen = Getspecific(otherContext, key);
                setResult = Setspecific(otherContext, key, 0x2222UL);
                mine = Getspecific(otherContext, key);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        other.Start();
        other.Join();

        Assert.Null(failure);
        Assert.Equal(0UL, seen);
        Assert.Equal(0, setResult);
        Assert.Equal(0x2222UL, mine);
        Assert.Equal(0x1111UL, Getspecific(context, key));
    }
}
