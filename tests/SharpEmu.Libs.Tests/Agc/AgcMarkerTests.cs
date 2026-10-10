// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Restore marker packet reservations without allowing foreign markers to overwrite inline packets.
[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcMarkerTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CursorAddress = BaseAddress + 0x1000;
    private const ulong MarkerStringAddress = BaseAddress + 0x200;
    private const uint PoisonDword = 0xDEAD_BEEFu;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Marker_ReservesReturnedStorageBeforeTheNextPacket(bool push)
    {
        var (memory, ctx) = Setup();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = MarkerStringAddress;
        if (push) AgcExports.DcbPushMarker(ctx);
        else AgcExports.DcbPopMarker(ctx);

        var markerPacket = ctx[CpuRegister.Rax];
        WriteShaderRegister(ctx);
        var registerPacket = ctx[CpuRegister.Rax];

        // Patching a returned marker payload must not alter the following SET_SH_REG.
        WriteUInt32(memory, markerPacket + 4, 0x74736574);
        Assert.Equal(0x228u, ReadUInt32(memory, registerPacket + 4));
        Assert.Equal(markerPacket + (push ? 12UL : 8UL), registerPacket);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Marker_WithoutCapacity_DoesNotReturnUnreservedStorage(bool push)
    {
        var (memory, ctx) = Setup();
        WriteUInt64(memory, CommandBufferAddress + 0x18, CursorAddress);
        var before = Snapshot(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = MarkerStringAddress;
        if (push) AgcExports.DcbPushMarker(ctx);
        else AgcExports.DcbPopMarker(ctx);

        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(before, Snapshot(memory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignMarker_DoesNotOverwriteAnInlinePacketBeforeItsCursorIsPublished(bool push)
    {
        var (memory, _) = Setup();
        // Native guest threads each have a different tracker around the shared memory.
        var writer = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
        WriteShaderRegister(writer); // Establish the writer through an ordinary export.
        var inlinePacket = ReadUInt64(memory, CommandBufferAddress + 0x10);
        // Yotei writes SET_SH_REG in line before publishing its next cursor. The HLE
        // allocation lock cannot protect these stores from another guest thread.
        WriteUInt32(memory, inlinePacket, 0xC001_7600);
        WriteUInt32(memory, inlinePacket + 4, 0x228);
        WriteUInt32(memory, inlinePacket + 8, 0);
        var before = Snapshot(memory);
        var foreign = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
        foreign[CpuRegister.Rdi] = CommandBufferAddress;
        foreign[CpuRegister.Rsi] = MarkerStringAddress;

        await Task.Run(() =>
        {
            if (push) AgcExports.DcbPushMarker(foreign);
            else AgcExports.AcbPopMarker(foreign);
        });

        Assert.Equal(0UL, foreign[CpuRegister.Rax]);
        Assert.Equal(before, Snapshot(memory));
    }

    private static void WriteShaderRegister(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x228;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AgcExports.SetShaderRegisterDirect(ctx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Marker_BufferFullCallback_UsesTheReplacementBufferAndPreservesReservedSpace(bool push)
    {
        var (memory, ctx) = Setup();
        const uint reserved = 7;
        var packetDwords = push ? 3u : 2u;
        WriteUInt64(memory, CommandBufferAddress + 0x18, CursorAddress + reserved * 4);
        WriteUInt64(memory, CommandBufferAddress + 0x20, 0x1234);
        WriteUInt64(memory, CommandBufferAddress + 0x28, 0x5678);
        WriteUInt32(memory, CommandBufferAddress + 0x30, reserved);
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, MarkerScheduler>();
        var proxy = (MarkerScheduler)(object)scheduler;
        proxy.Callback = args =>
        {
            Assert.Equal(0x1234UL, args[1]);
            Assert.Equal(CommandBufferAddress, args[2]);
            Assert.Equal((ulong)(packetDwords + reserved), args[3]);
            Assert.Equal(0x5678UL, args[4]);
            WriteUInt64(memory, CommandBufferAddress + 0x10, CursorAddress + 0x100);
            WriteUInt64(memory, CommandBufferAddress + 0x18, CursorAddress + 0x100 + (packetDwords + reserved) * 4);
        };
        var previous = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            ctx[CpuRegister.Rdi] = CommandBufferAddress;
            ctx[CpuRegister.Rsi] = MarkerStringAddress;
            if (push) AgcExports.DcbPushMarker(ctx);
            else AgcExports.DcbPopMarker(ctx);
            Assert.Equal(1, proxy.CallCount);
            Assert.Equal(CursorAddress + 0x100, ctx[CpuRegister.Rax]);
            Assert.Equal(CursorAddress + 0x100 + packetDwords * 4, ReadUInt64(memory, CommandBufferAddress + 0x10));
            Assert.Equal(PoisonDword, ReadUInt32(memory, CursorAddress));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void OrdinaryExport_TransfersTheBufferToAnotherWriter()
    {
        var (memory, first) = Setup();
        WriteShaderRegister(first);
        var second = new CpuContext(memory, Generation.Gen5);
        WriteShaderRegister(second);
        var cursor = ReadUInt64(memory, CommandBufferAddress + 0x10);
        Assert.Equal(0, AgcExports.DcbPopMarker(second));
        Assert.Equal(cursor, second[CpuRegister.Rax]);
        var before = Snapshot(memory);
        Assert.Equal(0, AgcExports.DcbPopMarker(first));
        Assert.Equal(0UL, first[CpuRegister.Rax]);
        Assert.Equal(before, Snapshot(memory));
    }

    [Fact]
    public void WriterIdentity_UsesGuestHandlesAcrossContextsAndIsScopedToMemory()
    {
        var (memory, ctx) = Setup();
        var previous = GuestThreadExecution.EnterGuestThread(0x123);
        try
        {
            WriteShaderRegister(ctx);
            var resumed = new CpuContext(memory, Generation.Gen5);
            resumed[CpuRegister.Rdi] = CommandBufferAddress;
            var cursor = ReadUInt64(memory, CommandBufferAddress + 0x10);
            AgcExports.AcbPopMarker(resumed);
            Assert.Equal(cursor, resumed[CpuRegister.Rax]);

            GuestThreadExecution.EnterGuestThread(0x456);
            var before = Snapshot(memory);
            AgcExports.DcbPopMarker(ctx);
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
            Assert.Equal(before, Snapshot(memory));

            // Reusing the same virtual address in a different game must not inherit an owner.
            var (_, other) = Setup();
            other[CpuRegister.Rdi] = CommandBufferAddress;
            AgcExports.DcbPopMarker(other);
            Assert.Equal(CursorAddress, other[CpuRegister.Rax]);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previous);
        }
    }

    public class MarkerScheduler : DispatchProxy
    {
        public Action<object?[]>? Callback { get; set; }
        public int CallCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IGuestThreadScheduler.TryCallGuestFunction) || args?.Length != 10)
                throw new NotSupportedException(targetMethod?.Name);
            CallCount++;
            Callback!(args);
            args[8] = 0UL;
            args[9] = null;
            return true;
        }
    }

    [Fact]
    public void Markers_RejectNullCommandBuffer()
    {
        var (_, ctx) = Setup();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = MarkerStringAddress;

        AgcExports.DcbPushMarker(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        AgcExports.DcbPopMarker(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

    }

    [Fact]
    public void PushMarker_RejectsUnreadableString()
    {
        var (_, ctx) = Setup();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x10; // unmapped

        AgcExports.DcbPushMarker(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

    }

    [Fact]
    public void Markers_WriteNopPackets()
    {
        var (memory, ctx) = Setup();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = MarkerStringAddress;

        AgcExports.DcbPushMarker(ctx);
        Assert.Equal(CursorAddress, ctx[CpuRegister.Rax]);
        // "pass" + terminator = 2 payload dwords, plus the header.
        Assert.Equal(0xC001_102Cu, ReadUInt32(memory, CursorAddress));
        Assert.Equal(CursorAddress + 12, ReadUInt64(memory, CommandBufferAddress + 0x10));

        AgcExports.DcbPopMarker(ctx);
        Assert.Equal(CursorAddress + 12, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC000_1030u, ReadUInt32(memory, CursorAddress + 12));
        Assert.Equal(CursorAddress + 20, ReadUInt64(memory, CommandBufferAddress + 0x10));

    }

    private static (FakeCpuMemory Memory, CpuContext Ctx) Setup()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, CursorAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, CursorAddress + 0x400);
        memory.WriteCString(MarkerStringAddress, "pass");
        for (ulong offset = 0; offset < 0x40; offset += 4)
        {
            WriteUInt32(memory, CursorAddress + offset, PoisonDword);
        }

        return (memory, ctx);
    }

    private static byte[] Snapshot(FakeCpuMemory memory)
    {
        var buffer = new byte[MemorySize];
        Assert.True(memory.TryRead(BaseAddress, buffer));
        return buffer;
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
