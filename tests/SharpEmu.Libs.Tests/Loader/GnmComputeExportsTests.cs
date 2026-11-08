// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Gnm;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Agc;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class GnmComputeExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong Ring = Base + 0x1000;
    private const ulong ReadPointer = Base + 0x100;
    private const ulong Result = Base + 0x200;

    [Fact]
    public void RingCommandsExecuteOnTheSharedAgcComputeQueue()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(5, PacketOpcode.WriteData), 0x100, unchecked((uint)Result), (uint)(Result >> 32), 123);
        Assert.Equal(0, Ding(context, id, 5));
        Assert.True(context.TryReadUInt32(Result, out var result));
        Assert.Equal(123u, result);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(5u, read);
        Assert.Equal(1UL, AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.SubmissionsStarted);
        Assert.Equal(0, Ding(context, id, 5));
        Assert.Equal(1UL, AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.SubmissionsStarted);
    }

    [Fact]
    public void ReadPointerDoesNotAdvanceUntilBlockedCommandsAreConsumed()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(7, PacketOpcode.WaitRegisterMemory), 0x13,
            unchecked((uint)Result), (uint)(Result >> 32), 123, uint.MaxValue, 10);
        Assert.Equal(0, Ding(context, id, 7));
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(0u, read);
        var stream = AgcExports.GetHeadlessCommandStreamForTests(context.Memory);
        Assert.Equal(1, stream.Queue.BlockedQueueCount);
        Assert.Equal(0, Ding(context, id, 7));
        Assert.True(context.TryWriteUInt32(Result, 123));
        stream.RetryBlocked();
        Assert.True(context.TryReadUInt32(ReadPointer, out read));
        Assert.Equal(7u, read);
        Assert.False(stream.Queue.HasPending);
        Assert.Equal(1UL, stream.Queue.SubmissionsStarted);
    }

    [Fact]
    public void RingWrapSubmitsTailThenHead()
    {
        var context = Context();
        var id = Map(context, dwords: 16);
        Write(context, Ring, PacketHeader.Make(12, PacketOpcode.Nop));
        Assert.Equal(0, Ding(context, id, 12));
        Write(context, Ring + 12 * 4, PacketHeader.Make(4, PacketOpcode.Nop));
        Write(context, Ring, PacketHeader.Make(5, PacketOpcode.WriteData), 0x100, unchecked((uint)Result), (uint)(Result >> 32), 456,
            PacketHeader.Make(3, PacketOpcode.Nop), 0, 0);
        Assert.Equal(0, Ding(context, id, 8));
        Assert.True(context.TryReadUInt32(Result, out var result));
        Assert.Equal(456u, result);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(8u, read);
        Assert.Equal(3UL, AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.SubmissionsStarted);
    }

    [Fact]
    public void GcnReleaseMemoryTranslatesToAgcWithoutChangingPs5Packets()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(7, PacketOpcode.ReleaseMemory) | 2,
            0x528, 1u << 29, unchecked((uint)Result), (uint)(Result >> 32), 789, 0);
        Assert.Equal(0, Ding(context, id, 7));
        Assert.True(context.TryReadUInt32(Result, out var result));
        Assert.Equal(789u, result);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(7u, read);
    }

    [Theory]
    [InlineData(0u, 0u, 0u)]
    [InlineData(6u, 0u, 3u)]
    [InlineData(6u, 1u, 2u)]
    [InlineData(6u, 2u, 0u)]
    public void GcnComputeDoneReleaseSamplesTheClockAndKeepsTheFollowingPacket(uint eventIndex, uint cachePolicy, uint interrupt)
    {
        var context = Context();
        var id = Map(context);
        var control = (3u << 29) | (interrupt << 24);
        var eventControl = 0x2Fu | (eventIndex << 8) | (cachePolicy << 25);
        Write(context, Ring, PacketHeader.Make(7, PacketOpcode.ReleaseMemory) | 2,
            eventControl, control, unchecked((uint)Result), (uint)(Result >> 32), uint.MaxValue, uint.MaxValue,
            PacketHeader.Make(5, PacketOpcode.WriteData), 0x100, unchecked((uint)(Result + 8)), (uint)(Result >> 32), 123);
        var before = EndOfPipe.ReadReferenceClock();
        Assert.Equal(0, Ding(context, id, 12));
        var after = EndOfPipe.ReadReferenceClock();
        Assert.True(context.TryReadUInt64(Result, out var clock));
        Assert.InRange(clock, before, after);
        Assert.True(context.TryReadUInt32(Result + 8, out var following));
        Assert.Equal(123u, following);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(12u, read);
        Assert.True(context.TryReadUInt32(Ring + 4, out var unchanged));
        Assert.Equal(eventControl, unchanged);
    }

    [Fact]
    public void GcnComputeDoneClockKeepsItsGpuCompletionDependency()
    {
        var runner = new StreamRunner(queueId: 3);
        var packets = typeof(GnmRuntime).Assembly.GetType("SharpEmu.GnmMode.Gpu.GnmCommandPackets")!;
        var handlers = (PacketHandler?[])packets.GetField("Handlers",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        runner.Interpreter.SetPacketHandlers(handlers);
        var deferred = new List<EndOfPipeWrite>();
        runner.Host.DeferredEndOfPipe = (queue, write) =>
        {
            Assert.Equal(3, queue);
            deferred.Add(write);
            return true;
        };
        var label = StreamRunner.LabelAddress;
        runner.Host.WriteQword(label, ulong.MaxValue);
        var before = EndOfPipe.ReadReferenceClock();
        Assert.Equal(SubmissionProgress.Complete, runner.Run(StreamRunner.Packet(PacketOpcode.ReleaseMemory,
            0x62F, (3u << 29) | (3u << 24), (uint)label, (uint)(label >> 32), uint.MaxValue, uint.MaxValue)));
        var after = EndOfPipe.ReadReferenceClock();
        var completion = Assert.Single(deferred);
        Assert.True(completion.WaitForGpu);
        Assert.Equal(EndOfPipeWriteKind.ClockWrite, completion.Kind);
        Assert.Equal(label, completion.Destination);
        Assert.InRange(completion.Value, before, after);
        Assert.Equal(ulong.MaxValue, runner.Host.ReadQword(label));
        Assert.Equal(new[] { "barrier" }, runner.Host.Calls);
        Assert.Empty(runner.Host.EndOfPipeWrites);
    }

    [Theory]
    [InlineData(1u, 0u, 0u, 0u)]
    [InlineData(1u, 0u, 2u, 0u)]
    [InlineData(2u, 0u, 2u, 0u)]
    [InlineData(2u, 1u, 2u, 0u)]
    [InlineData(2u, 0u, 3u, 0u)]
    [InlineData(2u, 0u, 0u, 0x38u)]
    [InlineData(2u, 2u, 2u, 0x38u)]
    public void GcnBottomOfPipeLabelsKeepTheirWidthAndFollowingPacket(uint source, uint cachePolicy, uint interrupt, uint cacheAction)
    {
        var context = Context();
        var id = Map(context);
        const ulong value = 0x1122334455667788;
        Assert.True(context.TryWriteUInt64(Result, 0xAABBCCDDEEFF0011));
        var eventControl = 0x528u | (cachePolicy << 25) | (cacheAction << 12);
        var control = (uint)(Result >> 32) | (source << 29) | (interrupt << 24);
        Write(context, Ring, PacketHeader.Make(6, PacketOpcode.EventWriteEndOfPipe), eventControl,
            unchecked((uint)Result), control, unchecked((uint)value), (uint)(value >> 32),
            PacketHeader.Make(5, PacketOpcode.WriteData), 0x100, unchecked((uint)(Result + 8)), (uint)(Result >> 32), 123);
        Assert.Equal(0, Ding(context, id, 11));
        Assert.True(context.TryReadUInt64(Result, out var label));
        Assert.Equal(source == 1 ? 0xAABBCCDD00000000UL | unchecked((uint)value) : value, label);
        Assert.True(context.TryReadUInt32(Result + 8, out var following));
        Assert.Equal(123u, following);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(11u, read);
        Assert.True(context.TryReadUInt32(Ring + 4, out var unchanged));
        Assert.Equal(eventControl, unchanged);
        Assert.True(context.TryReadUInt32(Ring + 12, out unchanged));
        Assert.Equal(control, unchanged);
    }

    [Theory]
    [InlineData(0, 1u, 0u, EndOfPipeWriteKind.Interrupt32)]
    [InlineData(3, 1u, 0u, EndOfPipeWriteKind.Interrupt32)]
    [InlineData(0, 2u, 0u, EndOfPipeWriteKind.Interrupt64)]
    [InlineData(3, 2u, 0u, EndOfPipeWriteKind.Interrupt64)]
    [InlineData(0, 2u, 0x38u, EndOfPipeWriteKind.InterruptWriteBack64)]
    [InlineData(3, 2u, 0x38u, EndOfPipeWriteKind.InterruptWriteBack64)]
    public void GcnBottomOfPipeInterruptWaitsForTheGpuLabel(int queueId, uint source, uint cacheAction, EndOfPipeWriteKind kind)
    {
        var runner = new StreamRunner(queueId);
        var packets = typeof(GnmRuntime).Assembly.GetType("SharpEmu.GnmMode.Gpu.GnmCommandPackets")!;
        var handlers = (PacketHandler?[])packets.GetField("Handlers",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        runner.Interpreter.SetPacketHandlers(handlers);
        var deferred = new List<EndOfPipeWrite>();
        runner.Host.DeferredEndOfPipe = (queue, write) =>
        {
            Assert.Equal(queueId, queue);
            deferred.Add(write);
            return true;
        };
        const ulong value = 0x1122334455667788;
        var label = StreamRunner.LabelAddress;
        runner.Host.WriteQword(label, ulong.MaxValue);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe,
            0x528u | (cacheAction << 12), (uint)label,
            (uint)(label >> 32) | (source << 29) | (2u << 24), unchecked((uint)value), (uint)(value >> 32))));
        var completion = Assert.Single(deferred);
        Assert.True(completion.WaitForGpu);
        Assert.Equal(kind, completion.Kind);
        Assert.Equal(label, completion.Destination);
        Assert.Equal(source == 1 ? unchecked((uint)value) : value, completion.Value);
        Assert.Equal(runner.Interpreter.InterruptEventId, completion.EventId);
        Assert.Equal(0u, completion.ContextId);
        Assert.Equal(ulong.MaxValue, runner.Host.ReadQword(label));
        Assert.Empty(runner.Host.Calls);
        Assert.Empty(runner.Host.EndOfPipeWrites);
    }

    [Theory]
    [InlineData(4u, 0u, 0u, 0u)]
    [InlineData(4u, 1u, 0u, 0u)]
    [InlineData(4u, 2u, 2u, 0u)]
    [InlineData(0x28u, 0u, 0u, 0u)]
    [InlineData(0x28u, 1u, 2u, 0u)]
    [InlineData(0x28u, 0u, 0u, 0x38u)]
    public void GcnEndOfPipeClockUsesTheSharedTimestampWriter(uint eventType, uint cachePolicy, uint interrupt, uint cacheAction)
    {
        var context = Context();
        var id = Map(context);
        var control = (uint)(Result >> 32) | (interrupt << 24) | (3u << 29);
        Write(context, Ring, PacketHeader.Make(6, PacketOpcode.EventWriteEndOfPipe),
            eventType | (5u << 8) | (cachePolicy << 25) | (cacheAction << 12), unchecked((uint)Result), control, 0x12345678, 0x9ABCDEF0);
        var before = EndOfPipe.ReadReferenceClock();
        Assert.Equal(0, Ding(context, id, 6));
        var after = EndOfPipe.ReadReferenceClock();
        Assert.True(context.TryReadUInt64(Result, out var clock));
        Assert.InRange(clock, before, after);
        Assert.True(context.TryReadUInt32(Ring + 12, out var unchanged));
        Assert.Equal(control, unchanged);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(6u, read);
    }

    [Fact]
    public void ComputeShaderPacketUsesSharedShaderRegisters()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Base + 0x800, 0x1234, 0, 0x5678, 0x9ABC, 64, 1, 1);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 25;
        context[CpuRegister.Rdx] = Base + 0x800;
        Assert.Equal(0, GnmDriverExports.SetCsShader(context));
        Assert.Equal(0, Ding(context, id, 25));
        var registers = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1).Registers.Shader;
        Assert.Equal(0x1234u, registers[ShaderRegisterOffset.ComputePgmLo]);
        Assert.Equal(0x5678u, registers[ShaderRegisterOffset.ComputePgmRsrc1]);
        Assert.Equal(64u, registers[ShaderRegisterOffset.ComputeNumThreadX]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40u)]
    [InlineData(0x3C0u)]
    public void ComputeShaderModifierPreservesOtherResourceBits(uint modifier)
    {
        var context = Context();
        var id = Map(context);
        const uint original = 0xAABBFDFF;
        Write(context, Base + 0x800, 0x1234, 0, original, 0x9ABC, 64, 1, 1);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 25;
        context[CpuRegister.Rdx] = Base + 0x800;
        context[CpuRegister.Rcx] = modifier;
        Assert.Equal(0, GnmDriverExports.SetCsShaderWithModifier(context));
        Assert.Equal(0, Ding(context, id, 25));
        var registers = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1).Registers.Shader;
        Assert.Equal(modifier == 0 ? original : (original & 0xFFFFFC3F) | modifier,
            registers[ShaderRegisterOffset.ComputePgmRsrc1]);
        Assert.True(context.TryReadUInt32(Base + 0x808, out var unchanged));
        Assert.Equal(original, unchanged);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(0x400u)]
    public void InvalidComputeShaderModifierLeavesTheCommandBufferUntouched(uint modifier)
    {
        var context = Context();
        Write(context, Ring, 0x12345678);
        Write(context, Base + 0x800, 0x1234, 0, 0x5678, 0x9ABC, 64, 1, 1);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 25;
        context[CpuRegister.Rdx] = Base + 0x800;
        context[CpuRegister.Rcx] = modifier;
        Assert.Equal(-1, GnmDriverExports.SetCsShaderWithModifier(context));
        Assert.True(context.TryReadUInt32(Ring, out var unchanged));
        Assert.Equal(0x12345678u, unchanged);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x18u)]
    public void DirectDispatchEmitsACompleteComputePacketAndTrailingNop(uint flags)
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 9;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = 5;
        context[CpuRegister.R8] = 7;
        context[CpuRegister.R9] = flags;
        Write(context, Ring + 36, 0x12345678);
        Assert.Equal(0, GnmDriverExports.DispatchDirect(context));
        Span<byte> bytes = stackalloc byte[40];
        Assert.True(context.Memory.TryRead(Ring, bytes));
        Assert.Equal(PacketHeader.Make(5, PacketOpcode.DispatchDirect) | 2 | (flags & 1),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        Assert.Equal((flags & 0x18) | 1, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
        Assert.Equal(PacketHeader.Make(4, PacketOpcode.Nop), BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]));
    }

    [Theory]
    [InlineData(0u, 0u, 0u, 0u)]
    [InlineData(2u, 3u, 5u, 1u)]
    [InlineData(4u, 3u, 5u, 0xE0000001u)]
    [InlineData(6u, 15u, 15u, 0u)]
    public void IndirectDrawEmitsStageRegisterLocationsAndPreservesTheReservation(
        uint stage, uint vertexOffset, uint instanceOffset, uint flags)
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 9;
        context[CpuRegister.Rdx] = 0x80;
        context[CpuRegister.Rcx] = stage;
        context[CpuRegister.R8] = vertexOffset;
        context[CpuRegister.R9] = instanceOffset;
        context[CpuRegister.Rsp] = Base + 0x8000;
        Write(context, Base + 0x8008, flags);
        Write(context, Ring + 36, 0x12345678);
        Assert.Equal(0, GnmDriverExports.DrawIndirect(context));
        var scalarBase = stage switch { 2 => 0x4Cu, 4 => 0xCCu, 6 => 0x14Cu, _ => 0u };
        uint[] expected = [PacketHeader.Make(5, PacketOpcode.DrawIndirect) | (flags & 1), 0x80,
            vertexOffset == 0 ? 0 : scalarBase + vertexOffset,
            instanceOffset == 0 ? 0 : scalarBase + instanceOffset, 2,
            PacketHeader.Make(4, PacketOpcode.Nop), 0, 0, 0, 0x12345678];
        Span<byte> actual = stackalloc byte[40];
        Assert.True(context.Memory.TryRead(Ring, actual));
        Assert.Equal(MemoryMarshal.AsBytes<uint>(expected).ToArray(), actual.ToArray());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x18u)]
    [InlineData(0x19u)]
    public void IndirectDispatchEmitsAComputeOffsetPacketAndPreservesTheReservation(uint flags)
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 7;
        context[CpuRegister.Rdx] = 0x40;
        context[CpuRegister.Rcx] = flags;
        Write(context, Ring + 28, 0x12345678);
        Assert.Equal(0, GnmDriverExports.DispatchIndirect(context));
        uint[] expected = [PacketHeader.Make(3, PacketOpcode.DispatchIndirect) | 2 | (flags & 1), 0x40,
            (flags & 0x18) | 1, PacketHeader.Make(4, PacketOpcode.Nop), 0, 0, 0, 0x12345678];
        Span<byte> actual = stackalloc byte[32];
        Assert.True(context.Memory.TryRead(Ring, actual));
        Assert.Equal(MemoryMarshal.AsBytes<uint>(expected).ToArray(), actual.ToArray());
    }

    [Fact]
    public void GnmIndirectCommandsKeepSeparateBasesAndReachTheSharedInterpreter()
    {
        var context = Context();
        var id = Map(context);
        const ulong arguments = Base + 0x2000;
        Write(context, Ring, PacketHeader.Make(4, PacketOpcode.SetBase), 1, unchecked((uint)arguments), (uint)(arguments >> 32));
        Write(context, arguments + 0x40, 7, 3, 11, 13);
        context[CpuRegister.Rdi] = Ring + 16;
        context[CpuRegister.Rsi] = 9;
        context[CpuRegister.Rdx] = 0x40;
        context[CpuRegister.Rcx] = 2;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0;
        context[CpuRegister.Rsp] = Base + 0x8000;
        Write(context, Base + 0x8008, 1);
        Assert.Equal(0, GnmDriverExports.DrawIndirect(context));
        const ulong dispatchArguments = arguments + 0x80;
        Write(context, Ring + 13 * 4, PacketHeader.Make(4, PacketOpcode.SetBase) | 2, 1,
            unchecked((uint)dispatchArguments), (uint)(dispatchArguments >> 32));
        Write(context, dispatchArguments + 0x20, 3, 5, 7);
        context[CpuRegister.Rdi] = Ring + 17 * 4;
        context[CpuRegister.Rsi] = 7;
        context[CpuRegister.Rdx] = 0x20;
        context[CpuRegister.Rcx] = 1;
        Assert.Equal(0, GnmDriverExports.DispatchIndirect(context));
        Assert.Equal(0, Ding(context, id, 24));
        var interpreter = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1);
        Assert.Equal(arguments, interpreter.DrawIndirectArgumentsBase);
        Assert.Equal(dispatchArguments, interpreter.DispatchIndirectArgumentsBase);
        Assert.Equal(3u, interpreter.InstanceCount);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(24u, read);
    }

    [Theory]
    [InlineData(false, 8u, 2u, 0u, 0u)]
    [InlineData(false, 10u, 2u, 0u, 0u)]
    [InlineData(false, 9u, 7u, 0u, 0u)]
    [InlineData(false, 9u, 2u, 16u, 0u)]
    [InlineData(false, 9u, 2u, 0u, 16u)]
    [InlineData(true, 6u, 0u, 0u, 0u)]
    [InlineData(true, 8u, 0u, 0u, 0u)]
    public void InvalidIndirectCommandsLeaveTheBufferUntouched(bool compute, uint size, uint stage, uint vertex, uint instance)
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = size;
        context[CpuRegister.Rcx] = stage;
        context[CpuRegister.R8] = vertex;
        context[CpuRegister.R9] = instance;
        context[CpuRegister.Rsp] = Base + 0x8000;
        Write(context, Ring, 0x12345678);
        Assert.Equal(-1, compute ? GnmDriverExports.DispatchIndirect(context) : GnmDriverExports.DrawIndirect(context));
        Assert.True(context.TryReadUInt32(Ring, out var unchanged));
        Assert.Equal(0x12345678u, unchanged);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x100u)]
    [InlineData(0x101u)]
    [InlineData(0x11000100u)]
    public void GnmDepthCountControlReachesTheSharedOcclusionState(uint value)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbCountControl, value);
        Assert.Equal(0, Ding(context, id, 3));
        Assert.Equal(value, AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.DepthCountControl);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void GnmPrimitiveRestartTranslatesToGeometryEngineState(uint enabled)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring,
            PacketHeader.Make(4, PacketOpcode.SetContextRegister), 0x102, 7, 0xFFFF,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), 0x2A5, enabled);
        Assert.Equal(0, Ding(context, id, 7));
        var registers = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1).TypedRegisters;
        Assert.Equal(7u, registers.UserConfig.IndexOffset);
        Assert.Equal(0xFFFFu, registers.Context.PrimitiveResetIndex);
        Assert.Equal(enabled, registers.UserConfig.PrimitiveResetControl);
    }

    [Fact]
    public void InsertWaitFlipDoneEmitsABlockingWaitWithoutOverwritingTheNextCommand()
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 7;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = 1;
        Write(context, Ring + 7 * sizeof(uint), 0x12345678);
        Assert.Equal(0, GnmDriverExports.InsertWaitFlipDone(context));
        Assert.True(context.TryReadUInt32(Ring + 7 * sizeof(uint), out var canary));
        Assert.Equal(0x12345678u, canary);
        var commands = new uint[7];
        Assert.True(context.Memory.TryRead(Ring, MemoryMarshal.AsBytes(commands.AsSpan())));
        var runner = new StreamRunner();
        runner.Host.FlipDone = false;
        Assert.Equal(SubmissionProgress.Blocked,
            runner.Run(commands, StreamRunner.Packet(PacketOpcode.NumInstances, 5)));
        Assert.Equal(new[] { "flush", "flip_done? 3 1" }, runner.Host.Calls);
        runner.Host.FlipDone = true;
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(5u, runner.Interpreter.InstanceCount);
    }

    [Theory]
    [InlineData(6u, 0u)]
    [InlineData(8u, 0u)]
    [InlineData(7u, 16u)]
    public void InvalidWaitFlipDoneLeavesTheCommandBufferUntouched(uint size, uint index)
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = size;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = index;
        Write(context, Ring, 0x12345678);
        Assert.Equal(-1, GnmDriverExports.InsertWaitFlipDone(context));
        Assert.True(context.TryReadUInt32(Ring, out var unchanged));
        Assert.Equal(0x12345678u, unchanged);
    }

    [Theory]
    [InlineData(8u, 1u, 1u, 1u)]
    [InlineData(10u, 1u, 1u, 1u)]
    [InlineData(9u, 0x80000000u, 1u, 1u)]
    [InlineData(9u, 1u, 0x80000000u, 1u)]
    [InlineData(9u, 1u, 1u, 0x80000000u)]
    public void InvalidDirectDispatchLeavesTheCommandBufferUntouched(uint size, uint x, uint y, uint z)
    {
        var context = Context();
        Write(context, Ring, 0x12345678);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = size;
        context[CpuRegister.Rdx] = x;
        context[CpuRegister.Rcx] = y;
        context[CpuRegister.R8] = z;
        Assert.Equal(-1, GnmDriverExports.DispatchDirect(context));
        Assert.True(context.TryReadUInt32(Ring, out var unchanged));
        Assert.Equal(0x12345678u, unchanged);
    }

    [Theory]
    [InlineData(0x7Eu, 0u)]
    [InlineData(0x36u, 1u)]
    [InlineData(0x7Fu, 3u)]
    public void InvalidGcnDepthFormatsDisableStaleTestsAndRestoreRequestedControls(uint depthControl, uint renderControl)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(10, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbZInfo,
            3, 1, 0x00400000, 0x00410000, 0x00400000, 0x00410000, 31u | (15u << 11), 511,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbDepthControl, depthControl,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbRenderControl, renderControl);
        Assert.Equal(0, Ding(context, id, 16));
        var interpreter = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1);
        Assert.Equal(depthControl, interpreter.TypedRegisters.Context.DepthTarget.DepthControl);
        Assert.Equal(renderControl, interpreter.TypedRegisters.Context.DepthTarget.RenderControl);

        Write(context, Ring + 16 * 4, PacketHeader.Make(4, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbZInfo, 0, 0);
        Assert.Equal(0, Ding(context, id, 20));
        var depth = interpreter.TypedRegisters.Context.DepthTarget;
        Assert.Equal(depthControl & ~0xFu, depth.DepthControl);
        Assert.Equal(renderControl & ~0xFu, depth.RenderControl);
        Assert.Equal(0x40000000UL, depth.ZReadBase);
        Assert.Equal(0x40000000UL, depth.ZWriteBase);
        Assert.Null(ImageRequestBuilders.DepthTarget(in depth, null!));

        depthControl ^= 0x40u;
        renderControl ^= 0x40u;
        Write(context, Ring + 20 * 4,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbDepthControl, depthControl,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbRenderControl, renderControl);
        Assert.Equal(0, Ding(context, id, 26));
        depth = interpreter.TypedRegisters.Context.DepthTarget;
        Assert.Equal(depthControl & ~0xFu, depth.DepthControl);
        Assert.Equal(renderControl & ~0xFu, depth.RenderControl);
        Assert.Null(ImageRequestBuilders.DepthTarget(in depth, null!));

        interpreter.TypedRegisters.ApplyContextState(ContextStateOperation.Push);
        Write(context, Ring + 26 * 4, PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbDepthControl, 0);
        Assert.Equal(0, Ding(context, id, 29));
        interpreter.TypedRegisters.ApplyContextState(ContextStateOperation.Pop);
        Write(context, Ring + 29 * 4, PacketHeader.Make(4, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbZInfo, 3, 1);
        Assert.Equal(0, Ding(context, id, 33));
        depth = interpreter.TypedRegisters.Context.DepthTarget;
        Assert.Equal(depthControl, depth.DepthControl);
        Assert.Equal(renderControl, depth.RenderControl);
        Assert.Equal(0x40000000UL, depth.ZReadBase);
        Assert.Equal(0x40000000UL, depth.ZWriteBase);
        Assert.Equal(0x41000000UL, depth.StencilReadBase);
        Assert.Equal(0x41000000UL, depth.StencilWriteBase);

        interpreter.TypedRegisters.ApplyContextState(ContextStateOperation.Clear);
        Write(context, Ring + 33 * 4, PacketHeader.Make(4, PacketOpcode.SetContextRegister), ContextRegisterOffset.DbZInfo, 3, 1);
        Assert.Equal(0, Ding(context, id, 37));
        depth = interpreter.TypedRegisters.Context.DepthTarget;
        Assert.Equal(0u, depth.DepthControl);
        Assert.Equal(0u, depth.RenderControl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComputeDepthLoadsResolveTheKnownGpuPlaneInsteadOfAZeroBufferProjection(bool writePlane)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(10, PacketOpcode.SetContextRegister), 0x10,
            3, 0, 0x00400000, 0, 0x00410000, 0, 31u | (15u << 11), 511);
        Assert.Equal(0, Ding(context, id, 10));
        const ulong codeAddress = Base + 0x4000;
        Write(context, codeAddress, 0xF0000100, 0x00020000, 0xBF810000);
        Assert.True(GcnShaderDecoder.TryDecode(context.Memory, codeAddress, 12, out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var lowered, out error), error);
        var frontend = new GcnShaderFrontend();
        var plan = ShaderResourcePlan.Extract(lowered, ShaderStage.Compute, 54321, 0, 16,
            normalizeBufferDescriptor: frontend.NormalizeBufferDescriptor,
            normalizeImageDescriptor: frontend.NormalizeImageDescriptor);
        uint[] descriptor = [writePlane ? 0x00410000u : 0x00400000u, 0x1C400000, 255u | (127u << 14),
            0x90000204, 255u << 13, 0, 0, 0];
        var user = new uint[16];
        descriptor.CopyTo(user, 8);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = user },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var native = new TextureDescriptorWords(Assert.Single(snapshot.Images));
        Assert.Equal(writePlane ? 0x41000000UL : 0x40000000UL, native.BaseAddress);
        Assert.Equal(GuestTileMode.Depth, native.TileMode);
        Assert.All(snapshot.Buffers, words => Assert.Equal(0UL, BufferDescriptorWords.From(words).Address));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(lowered, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
            out _, out error), error);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x10000u)]
    public void FullGcnDepthCopyKeepsAlignedBackingForSamplingAndAttachmentRebinding(uint allocationPadding)
    {
        var context = Context();
        var id = Map(context);
        const ulong source = 0x40000000;
        const ulong destination = 0x1B35D8000;
        const ulong shadow = 0x70000000;
        const ulong bytes = 256 * 128 * 4;
        Write(context, Ring, PacketHeader.Make(10, PacketOpcode.SetContextRegister), 0x10,
            3, 0, (uint)(source >> 8), 0, (uint)(source >> 8), 0, 31u | (15u << 11), 511);
        Assert.Equal(0, Ding(context, id, 10));
        const ulong codeAddress = Base + 0x4000;
        uint[] code = [0xBEEB03FF, 9, 0x936AC008, 0xD2BA0000, 0x0401006A, 0xE0002000, 0x80000100,
            0xD1A80000, 0x00020006, 0xBF8C0F70, 0xE0102000, 0x80010100, 0xBF810000];
        Write(context, codeAddress, code);
        var shader = new RegisteredShader(codeAddress, 0, (uint)code.Length * 4, 0, 0, 0, 0, 0, 0);
        var allocations = 0;
        ulong? allocation = null;
        var frontend = new GcnShaderFrontend(context.Memory, size =>
        {
            Assert.Equal(bytes, size);
            allocations++;
            return allocation;
        });
        var copyBytes = (uint)bytes + allocationPadding;
        uint[] user = [(uint)source, 0x43000, copyBytes / 4, 0x24004,
            unchecked((uint)destination), 0x43000 | (uint)(destination >> 32), copyBytes / 4, 0x24004];
        var input = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1 };
        var system = new Gen5ComputeSystemRegisters(8, null, null, null);
        var groups = copyBytes / 256;
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, groups, 1, 1, out _, out _));
        allocation = shadow;
        Assert.True(frontend.TryResolveImageCopy(context, shader, user, input, system, groups, 1, 1, out var from, out var to));
        Assert.Equal(source, from.Description.Data.Address);
        Assert.Equal(shadow, to.Description.Data.Address);
        Assert.Equal(bytes, from.Description.Data.Size);
        Assert.Equal(bytes, to.Description.Data.Size);
        Assert.True(frontend.TryResolveImageCopy(context, shader, user, input, system, groups, 1, 1, out _, out var repeated));
        Assert.Equal(shadow, repeated.Description.Data.Address);
        Assert.Equal(2, allocations);

        uint[] descriptor = [(uint)(destination >> 8), 0x1C400000, 255u | (127u << 14),
            0x90000204, 255u << 13, 0, 0, 0];
        var normalized = frontend.NormalizeImageDescriptor(descriptor, ShaderStage.Pixel, 54322, "test-depth-copy");
        Assert.Equal(shadow, new TextureDescriptorWords(normalized).BaseAddress);
        var shape = new ShaderImageShape(false, false, false, false, false, TextureNumericClass.Float);
        var sampled = frontend.ResolveTexture(normalized, shape, 54322, 0);
        Assert.Equal(shadow, sampled.Request.Description.Data.Address);
        Assert.Equal(GuestTileMode.Depth, sampled.Request.Description.TileMode);

        user[2] = user[6] = (uint)bytes / 4 - 64;
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, (uint)bytes / 256 - 1, 1, 1, out _, out _));
        Write(context, Ring + 10 * 4, PacketHeader.Make(10, PacketOpcode.SetContextRegister), 0x10,
            3, 0, (uint)(destination >> 8), 0, (uint)(destination >> 8), 0, 31u | (15u << 11), 511);
        Assert.Equal(0, Ding(context, id, 20));
        var depth = AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.DepthTarget;
        Assert.Equal(shadow, depth.ZReadBase);
        Assert.Equal(shadow, depth.ZWriteBase);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(false, 350)]
    [InlineData(true, 0)]
    public void DefaultHardwareStateIsAnExecutableAgcReset(bool compute, int version)
    {
        var context = Context();
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 256;
        Assert.Equal(256, compute ? GnmDriverExports.DispatchInitDefaultHardwareState(context)
            : version == 350 ? GnmDriverExports.DrawInitDefaultHardwareState350(context)
            : GnmDriverExports.DrawInitDefaultHardwareState200(context));
        Assert.Equal(0, AgcExports.SubmitComputeCommands(context, 0x20, Ring, 256));
        Assert.False(AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.HasPending);
    }

    [Fact]
    public void ContextInit400IsExecutableAndPreservesShaderRegisters()
    {
        var context = Context();
        var id = Map(context, 512);
        Write(context, Ring,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.CbColor0Info, 10u << 2,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.CbTargetMask, 0xF,
            PacketHeader.Make(4, PacketOpcode.SetShaderRegister), ShaderRegisterOffset.ComputePgmRsrc1, 0x1234, 0x5678,
            PacketHeader.Make(2, PacketOpcode.NumInstances), 4);
        Assert.Equal(0, Ding(context, id, 12));
        var interpreter = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1);
        var shader = interpreter.TypedRegisters.Shader;
        Assert.Equal(0xFu, interpreter.TypedRegisters.Context.RenderTargetMask);
        Assert.Equal(4u, interpreter.InstanceCount);
        context[CpuRegister.Rdi] = Ring + 12 * sizeof(uint);
        context[CpuRegister.Rsi] = 256;
        Assert.Equal(256, GnmDriverExports.DrawInitToDefaultContextState400(context));
        Assert.Equal(256UL, context[CpuRegister.Rax]);
        Assert.Equal(0, Ding(context, id, 268));
        Assert.Equal(0u, interpreter.TypedRegisters.Context.RenderTargetMask);
        Assert.Equal(1u, interpreter.InstanceCount);
        Assert.Same(shader, interpreter.TypedRegisters.Shader);
        Assert.Equal(0x1234u, interpreter.Registers.Shader[ShaderRegisterOffset.ComputePgmRsrc1]);
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(268u, read);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void PixelShaderPacketsSupportBindingAndUnbinding(bool unbind, bool update, bool legacy)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Base + 0x800, 0x1234, 0, 0, 0, 0, 0xF, 0, 0, 0, 0, 0, 0xF);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 40;
        context[CpuRegister.Rdx] = unbind ? 0 : Base + 0x800;
        Assert.Equal(0, legacy
            ? update ? GnmDriverExports.UpdatePsShader(context) : GnmDriverExports.SetPsShader(context)
            : update ? GnmDriverExports.UpdatePsShader350(context) : GnmDriverExports.SetPsShader350(context));
        Assert.Equal(0, Ding(context, id, 40));
        var interpreter = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1);
        Assert.Equal(unbind ? 0u : 0x1234u, interpreter.Registers.Shader[ShaderRegisterOffset.SpiShaderPgmLoPs]);
        if (unbind && !legacy)
        {
            Assert.True(context.TryReadUInt32(Ring + 9 * sizeof(uint), out var mask));
            Assert.Equal(0xFu, mask);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0xE0000001u)]
    public void AutoIndexDrawReachesTheSharedInterpreter(uint flags)
    {
        var host = new RecordingCommandStreamHost();
        var context = new CpuContext(host.Memory, Generation.Gen4);
        context[CpuRegister.Rdi] = RecordingCommandStreamHost.MemoryBase;
        context[CpuRegister.Rsi] = 7;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = flags;
        Assert.Equal(0, GnmDriverExports.DrawIndexAuto(context));
        var interpreter = new GpuCommandInterpreter(host, 0, 0);
        _ = interpreter.Process(new PacketCursorStack(), RecordingCommandStreamHost.MemoryBase, 7);
        var draw = Assert.Single(host.AutoDraws);
        Assert.Equal(3u, draw.VertexCount);
        Assert.Equal(1u, draw.InstanceCount);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(1u, 4u)]
    public void IndexOffsetDrawUsesTheBoundIndexType(uint indexType, uint elementBytes)
    {
        var host = new RecordingCommandStreamHost();
        var context = new CpuContext(host.Memory, Generation.Gen4);
        var commands = RecordingCommandStreamHost.MemoryBase;
        var indices = commands + 0x1000;
        Write(context, commands, PacketHeader.Make(3, PacketOpcode.IndexBase), (uint)indices, 0,
            PacketHeader.Make(2, PacketOpcode.IndexType), indexType);
        context[CpuRegister.Rdi] = commands + 20;
        context[CpuRegister.Rsi] = 9;
        context[CpuRegister.Rdx] = 0x88;
        context[CpuRegister.Rcx] = 0x3C;
        Assert.Equal(0, GnmDriverExports.DrawIndexOffset(context));
        var interpreter = new GpuCommandInterpreter(host, 0, 0);
        _ = interpreter.Process(new PacketCursorStack(), commands, 14);
        var draw = Assert.Single(host.IndexedDraws);
        Assert.Equal(0x3Cu, draw.IndexCount);
        Assert.Equal(indices + 0x88 * elementBytes, draw.IndexAddress);
        Assert.Equal(indexType, draw.IndexTypeAndSize);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(1u, 0xE0000001u)]
    public void IndexedDrawUsesItsExplicitAddressAndKeepsTheBoundIndexType(uint indexType, uint flags)
    {
        var host = new RecordingCommandStreamHost();
        var context = new CpuContext(host.Memory, Generation.Gen4);
        var commands = RecordingCommandStreamHost.MemoryBase;
        var indices = commands + 0x1000;
        Write(context, commands, PacketHeader.Make(3, PacketOpcode.IndexBase), (uint)(indices + 0x1000), 0,
            PacketHeader.Make(2, PacketOpcode.IndexType), indexType);
        Write(context, commands + 60, 0x12345678);
        context[CpuRegister.Rdi] = commands + 20;
        context[CpuRegister.Rsi] = 10;
        context[CpuRegister.Rdx] = 6;
        context[CpuRegister.Rcx] = indices;
        context[CpuRegister.R8] = flags;
        context[CpuRegister.R9] = 1;
        Assert.Equal(0, GnmDriverExports.DrawIndex(context));
        var interpreter = new GpuCommandInterpreter(host, 0, 0);
        _ = interpreter.Process(new PacketCursorStack(), commands, 15);
        var draw = Assert.Single(host.IndexedDraws);
        Assert.Equal(6u, draw.IndexCount);
        Assert.Equal(indices, draw.IndexAddress);
        Assert.Equal(indexType, draw.IndexTypeAndSize);
        Assert.True(context.TryReadUInt32(commands + 60, out var untouched));
        Assert.Equal(0x12345678u, untouched);
    }

    [Theory]
    [InlineData(0u, 0u, 0u)]
    [InlineData(255u, 1u, 0u)]
    [InlineData(246u, 0u, 1u)]
    public void VgtControlEmitsAnExecutableLegacyContextPacket(uint groupSizeMinusOne, uint partialWave, uint switchOnEop)
    {
        var context = Context();
        var id = Map(context);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 3;
        context[CpuRegister.Rdx] = groupSizeMinusOne;
        context[CpuRegister.Rcx] = partialWave;
        context[CpuRegister.R8] = switchOnEop;
        Assert.Equal(0, GnmDriverExports.SetVgtControl(context));
        Assert.True(context.TryReadUInt32(Ring + 4, out var register));
        Assert.Equal(0x2AAu, register);
        Assert.True(context.TryReadUInt32(Ring + 8, out var value));
        Assert.Equal(groupSizeMinusOne | (partialWave << 16), value);
        Assert.Equal(0, Ding(context, id, 3));
        Assert.True(context.TryReadUInt32(ReadPointer, out var read));
        Assert.Equal(3u, read);
    }

    [Theory]
    [InlineData(256u, 0u, 0u)]
    [InlineData(255u, 2u, 0u)]
    [InlineData(255u, 0u, 2u)]
    public void InvalidVgtControlsLeaveTheCommandBufferUntouched(uint groupSizeMinusOne, uint partialWave, uint switchOnEop)
    {
        var context = Context();
        Write(context, Ring, 0x12345678);
        context[CpuRegister.Rdi] = Ring;
        context[CpuRegister.Rsi] = 3;
        context[CpuRegister.Rdx] = groupSizeMinusOne;
        context[CpuRegister.Rcx] = partialWave;
        context[CpuRegister.R8] = switchOnEop;
        Assert.Equal(-1, GnmDriverExports.SetVgtControl(context));
        Assert.True(context.TryReadUInt32(Ring, out var untouched));
        Assert.Equal(0x12345678u, untouched);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShaderUpdatesKeepContextRegistersUnchanged(bool vertex, bool legacy)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.CbColor0Info, 10u << 2,
            PacketHeader.Make(3, PacketOpcode.SetContextRegister), ContextRegisterOffset.CbTargetMask, 0xF);
        Assert.Equal(0, Ding(context, id, 6));
        var interpreter = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1);
        var previous = interpreter.TypedRegisters.Context;
        Write(context, Base + 0x800, 0x1234, 0, 0xC0000, 4, 0, 4, 0, 0, 0, 0, 0, 0xF);
        context[CpuRegister.Rdi] = Ring + 24;
        context[CpuRegister.Rsi] = vertex ? 29UL : 40UL;
        context[CpuRegister.Rdx] = Base + 0x800;
        context[CpuRegister.Rcx] = 0xC0;
        Assert.Equal(0, vertex ? GnmDriverExports.UpdateVsShader(context)
            : legacy ? GnmDriverExports.UpdatePsShader(context) : GnmDriverExports.UpdatePsShader350(context));
        Assert.Equal(0, Ding(context, id, vertex ? 35u : 46u));
        Assert.Same(previous, interpreter.TypedRegisters.Context);
        Assert.Equal(0xFu, interpreter.TypedRegisters.Context.RenderTargetMask);
        Assert.Equal(0x1234u, interpreter.Registers.Shader[vertex ? 0xC8u : ShaderRegisterOffset.SpiShaderPgmLoPs]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(7u)]
    public void GcnColorPitchAndSliceProduceATwoDimensionalAgcTarget(uint slot)
    {
        var context = Context();
        var id = Map(context);
        var offset = 0x318u + slot * 15;
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), offset,
            0x00400000, 239, 32639, 0, 10u << 2, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        var target = AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.ColorTargets[slot];
        Assert.Equal(0x40000000UL, target.BaseAddress);
        Assert.Equal(1920u, target.Width + 1);
        Assert.Equal(1088u, target.Height + 1);
        Assert.Equal(1u, target.Dimension);
        Assert.Equal(GuestTileMode.Linear, target.TileMode);
        var resolved = ImageRequestBuilders.ColorTarget(in target, 15, 0, false);
        Assert.NotNull(resolved);
        Assert.Equal(1920u, resolved.Value.Extent.Width);
        Assert.Equal(1088u, resolved.Value.Extent.Height);

        Write(context, Ring + 17 * 4, PacketHeader.Make(3, PacketOpcode.SetContextRegister), offset + 2, 16319);
        Assert.Equal(0, Ding(context, id, 20));
        var updated = AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.ColorTargets[slot];
        Assert.Equal(544u, updated.Height + 1);
        Assert.Equal(1088u, target.Height + 1);
    }

    [Theory]
    [InlineData(10u)]
    [InlineData(14u)]
    public void SampledColorTargetKeepsItsGpuBackingAtAGcnOnlyAlignedAddress(uint tile)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), 0x318,
            0x00408080, 239, 32639, 0, 10u << 2, tile, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        var target = AgcExports.GetHeadlessCommandStreamForTests(context.Memory)
            .Queue.GetInterpreter(1).TypedRegisters.Context.ColorTargets[0];
        var frontend = new GcnShaderFrontend();
        uint[] gcn = [0x00408080, 10u << 20, 1919u | (1079u << 14),
            (9u << 28) | (tile << 20) | 0xFAC, 1919u << 13, 0, 0, 0];
        var converted = frontend.NormalizeImageDescriptor(gcn, SharpEmu.ShaderCompiler.Resources.ShaderStage.Pixel, 123, "test-gpu-target");
        var shape = new ShaderImageShape(false, false, false, false, false, TextureNumericClass.Float);
        var sampled = frontend.ResolveTexture(converted, shape, 123, 0);
        var color = ImageRequestBuilders.ColorTarget(in target, 15, 0, false)!.Value;
        Assert.Equal(target.BaseAddress, sampled.Request.Description.Data.Address);
        Assert.Equal(color.Request.Description.TileMode, sampled.Request.Description.TileMode);
        Assert.Equal(color.Request.Description.Pitch, sampled.Request.Description.Pitch);
        var colorMip = color.Request.Description.MipLayout[0];
        var textureMip = sampled.Request.Description.MipLayout[0];
        Assert.True(colorMip.Size == textureMip.Size && colorMip.Pitch == textureMip.Pitch,
            $"color={colorMip.Size:X}/{colorMip.Pitch}/{colorMip.Height} texture={textureMip.Size:X}/{textureMip.Pitch}/{textureMip.Height}");
        Assert.Equal(1920u, sampled.Request.Description.Extent.Width);
        Assert.Equal(target.Height + 1, sampled.Request.Description.Extent.Height);
        Assert.Equal(color.Request.Description.Data, sampled.Request.Description.Data);
        Assert.Equal(0x00408080u, gcn[0]);
    }

    [Theory]
    [InlineData(10u, false)]
    [InlineData(10u, true)]
    [InlineData(14u, false)]
    [InlineData(14u, true)]
    public void NativeColorImageAccessesMaterializeAndCompileWithoutATiledBufferProjection(uint tile, bool store)
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), 0x318,
            0x00409090, 239, 32639, 0, 10u << 2, tile, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        Write(context, Base + 0x800, store ? 0xF0200F00u : 0xF0000F00u, 0x00020400, 0xBF810000);
        Assert.True(GcnShaderDecoder.TryDecode(context.Memory, Base + 0x800, 12, out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var program, out error), error);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0xD532, 0, 16,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        uint[] userData = [0, 0, 0, 0, 0, 0, 0, 0,
            0x00409090, 10u << 20, 1919u | (1079u << 14),
            (9u << 28) | (tile << 20) | 0xFAC, 1919u << 13, 0, 0, 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(0u, Assert.Single(snapshot.Buffers)[2]);
        var image = new TextureDescriptorWords(Assert.Single(snapshot.Images));
        Assert.Equal(0x40909000UL, image.BaseAddress);
        Assert.Equal(GuestTileMode.Linear, image.TileMode);
        Assert.Equal(1920u, image.Width + 1);
        Assert.Equal(1088u, image.Height + 1);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 8, LocalSizeY = 8, WaveSize = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Fact]
    public void LegacyColorExtentWritesInvalidateCachedTargetsAndPreserveSnapshots()
    {
        var context = Context();
        var id = Map(context);
        Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), 0x318,
            0x00400000, 239, 32639, 0, 10u << 2, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, Ding(context, id, 17));
        var banks = AgcExports.GetHeadlessCommandStreamForTests(context.Memory).Queue.GetInterpreter(1).TypedRegisters;
        var snapshot = banks.Snapshot();
        var stamp = banks.Stamp(RegisterStamp.ColorTargets);

        Write(context, Ring + 17 * 4, PacketHeader.Make(3, PacketOpcode.SetContextRegister), 0x31A, 16319);
        Assert.Equal(0, Ding(context, id, 20));

        Assert.True(banks.Stamp(RegisterStamp.ColorTargets) > stamp);
        Assert.Equal(544u, banks.Context.ColorTargets[0].Height + 1);
        Assert.Equal(1088u, snapshot.Context.ColorTargets[0].Height + 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FullGcnSurfaceCopyUsesNativeImagesWithTheirHostLayout(bool threadDimensions, bool eightUserScalars)
    {
        var context = Context();
        var id = Map(context);
        var source = eightUserScalars ? 0x181500000UL : 0x0040808000UL;
        var destination = eightUserScalars ? 0x1853C0000UL : 0x0041800000UL;
        if (eightUserScalars)
        {
            Write(context, Ring, PacketHeader.Make(16, PacketOpcode.SetContextRegister), 0x318,
                (uint)(source >> 8), 239, 32639, 0, 0x28828, 14, 0, 0, 0, 0, 0, 0, 0, 0,
                0xC0001000, 1920u | (1080u << 16));
            Assert.Equal(0, Ding(context, id, 18));
        }
        else
        {
            Write(context, Ring, PacketHeader.Make(17, PacketOpcode.SetContextRegister), 0x318,
                (uint)(source >> 8), 239, 32639, 0, 0x28828, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            Assert.Equal(0, Ding(context, id, 17));
        }
        const ulong codeAddress = Base + 0x4000;
        uint[] code = eightUserScalars
            ? [0xBEEB03FF, 9, 0x936AC008, 0xD2BA0000, 0x0401006A, 0xE0002000, 0x80000100,
                0xD1A80000, 0x00020006, 0xBF8C0F70, 0xE0102000, 0x80010100, 0xBF810000]
            : [0xBEEB03FF, 7, 0x8F00860C, 0x4A000000, 0xE0002000, 0x80010100,
                0x7D88000A, 0xBE80246A, 0xBF8C0F70, 0xE0102000, 0x80020100, 0xBF810000];
        Write(context, codeAddress, code);
        var shader = new RegisteredShader(codeAddress, 0, (uint)code.Length * 4, 0, 0, 0, 0, 0, 0);
        var frontend = new GcnShaderFrontend();
        var sourceRegister = eightUserScalars ? 0 : 4;
        var user = new uint[sourceRegister + 8];
        var descriptorFlags = eightUserScalars ? 0x43000u : 0x40000u;
        uint[] descriptors = [(uint)source, descriptorFlags | (uint)(source >> 32), 2088960, 0x24004,
            (uint)destination, descriptorFlags | (uint)(destination >> 32), 2088960, 0x24004];
        descriptors.CopyTo(user, sourceRegister);
        var input = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1,
            DispatchThreadDimensions = threadDimensions };
        var system = new Gen5ComputeSystemRegisters((uint)sourceRegister + 8, null, null, null);
        var x = threadDimensions ? 2088960u : 32640u;
        Assert.True(frontend.TryResolveImageCopy(context, shader, user, input, system, x, 1, 1, out var from, out var to));
        Assert.Equal(source, from.Description.Data.Address);
        Assert.Equal(destination, to.Description.Data.Address);
        if (eightUserScalars)
        {
            Assert.Equal(1080u, from.Description.Extent.Height);
            Assert.Equal(GuestTileMode.Linear, from.Description.TileMode);
            Assert.Equal(1920UL * 1080 * 4, from.Description.Data.Size);
        }
        else
        {
            Assert.Equal(GuestTileMode.Linear, from.Description.TileMode);
            Assert.Equal(2088960UL * 4, from.Description.Data.Size);
        }
        Assert.Equal(from.Description.Data.Size, to.Description.Data.Size);
        Assert.Equal(from.Description.Extent, to.Description.Extent);

        // Partial copies, modified kernels and mismatched descriptor lengths stay on the normal path.
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, x - 1, 1, 1, out _, out _));
        user[sourceRegister + 6]--;
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, x, 1, 1, out _, out _));
        user[sourceRegister + 6]++;
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system with { WorkGroupXRegister = 9 }, x, 1, 1, out _, out _));
        code[^1] ^= 1;
        Write(context, codeAddress, code);
        Assert.False(frontend.TryResolveImageCopy(context, shader, user, input, system, x, 1, 1, out _, out _));
    }

    [Theory]
    [InlineData(7u, 0u, 0u, 0x80D17000u)]
    [InlineData(0u, 8u, 0u, 0x80D17001u)]
    [InlineData(0u, 0u, 3u, 0x80D17002u)]
    public void InvalidQueueArgumentsReturnGnmErrors(uint pipe, uint queue, uint dwords, uint error)
    {
        var context = Context();
        SetMapArguments(context, dwords);
        context[CpuRegister.Rdi] = pipe;
        context[CpuRegister.Rsi] = queue;
        Assert.Equal(unchecked((int)error), GnmDriverExports.MapComputeQueue(context));
    }

    [Fact]
    public void QueueMappingsAndDeveloperServicesAreGnmOnly()
    {
        foreach (var nid in new[] { "29oKvKXzEZo", "bX5IbRvECXk", "iCO804ZgzdA", "KXltnCwEJHQ",
            "TLV4mswiZ4A", "Kx-h-nWQJ8A", "0BzLGljcwBo", "VOx8NGmHXTs", "hsZPf1lON7E", "yb2cRhagD1I",
            "5uFKckiJYRM", "GGsn7jMTxw4", "im2ZuItabu4", "+AFvOEXrKJk", "4v+otIIdjqg", "Z43vKp5k7r0",
            "oYM+YzfCm2Y", "mLVL7N7BVBg", "V31V01UiScY", "cFCp0NX8wf0", "HlTPoZ-oY7Y", "X9Omw9dwv5M",
            "1qXLHIpROPE", "b08AgtPlHPg" })
        {
            Assert.Single(GnmExportRegistry.CreateExports(), export => export.Nid == nid);
            Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5), export => export.Nid == nid);
        }
        Assert.Equal(unchecked((int)0x80D13FFF), GnmDriverExports.ValidateCommandBuffers(Context()));
        Assert.Equal(0, GnmDriverExports.DriverCaptureInProgress(Context()));
        Assert.Equal(1, GnmDriverExports.AreSubmitsAllowed(Context()));
    }

    private static CpuContext Context()
    {
        GcnShaderFrontend.ResetVertexFetchTargets();
        return new(new FakeCpuMemory(Base, 0x10000), Generation.Gen4);
    }

    private static void SetMapArguments(CpuContext context, uint dwords)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = Ring;
        context[CpuRegister.Rcx] = dwords;
        context[CpuRegister.R8] = ReadPointer;
    }

    private static uint Map(CpuContext context, uint dwords = 256)
    {
        SetMapArguments(context, dwords);
        Assert.Equal(1, GnmDriverExports.MapComputeQueue(context));
        return (uint)context[CpuRegister.Rax];
    }

    private static int Ding(CpuContext context, uint id, uint offset)
    {
        context[CpuRegister.Rdi] = id;
        context[CpuRegister.Rsi] = offset;
        return GnmDriverExports.DingDong(context);
    }

    private static void Write(CpuContext context, ulong address, params uint[] values)
    {
        var bytes = new byte[values.Length * sizeof(uint)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), values[i]);
        Assert.True(context.Memory.TryWrite(address, bytes));
    }
}
