// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Gnm;
using SharpEmu.GnmMode.Libraries.VideoOut;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmFlipSubmissionTests
{
    [Theory]
    [InlineData(false, 1080u)]
    [InlineData(false, 1088u)]
    [InlineData(true, 1080u)]
    [InlineData(true, 1088u)]
    public void NativeDisplayImagesResolveBeforeAnyColorAttachmentHasBeenBound(bool shadow, uint allocatedHeight)
    {
        const ulong source = 0x1127_008000;
        var destination = shadow ? source + 0x8000 : source;
        var exports = typeof(GnmVideoOutExports);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var targets = (System.Collections.IDictionary)exports.GetField("_displayTargets", flags)!.GetValue(null)!;
        var previous = targets.Contains(source) ? targets[source] : null;
        var targetType = exports.GetNestedType("DisplayTarget", BindingFlags.NonPublic)!;
        try
        {
            targets[source] = Activator.CreateInstance(targetType, [source, destination, 1920u, 1080u]);
            uint[] gcn = [(uint)(source >> 8), 10u << 20, 1919u | ((allocatedHeight - 1) << 14),
                (9u << 28) | (10u << 20) | 0xFAC, 1919u << 13, 1u << 31, 0, 0];
            var frontend = new GcnShaderFrontend();
            var normalized = frontend.NormalizeImageDescriptor(gcn, ShaderStage.Compute, 0xD533, "test-display-store");
            Assert.NotEqual(new uint[8], normalized);
            var image = frontend.ResolveTexture(normalized,
                new ShaderImageShape(false, false, false, false, false, TextureNumericClass.Float), 0xD533, 0);
            Assert.Equal(destination, image.Request.Description.Data.Address);
            Assert.Equal(GuestTileMode.RenderTarget, image.Request.Description.TileMode);
            Assert.Equal(1920u, image.Request.Description.Extent.Width);
            Assert.Equal(1080u, image.Request.Description.Extent.Height);
            gcn[0] += 0x10000;
            Assert.Equal(new uint[8], frontend.NormalizeImageDescriptor(gcn, ShaderStage.Compute, 0xD533, "test-unknown-store"));
        }
        finally
        {
            if (previous is not null) targets[source] = previous;
            else targets.Remove(source);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GnmTargetlessDrawsAreNotRetainedForPresentation(bool gnm)
    {
        var host = new RecordingCommandStreamHost();
        if (gnm)
            AgcExports.SetShaderFrontend(host.Memory, new GcnShaderFrontend());
        var translation = AgcExports.CreateCommandStreamTranslation(host.Memory, host);
        var interpreter = new GpuCommandInterpreter(host, 0, 0);
        translation.BeginSubmission(0, 1, null, interpreter);
        var draw = new TargetlessDrawArguments(1, false, default,
            new DrawAutoArguments(0, PacketOpcode.DrawIndexAuto, 3, 1, 0, 0, DrawOffsetSource.Packet));

        Assert.Equal(!gnm, translation.TryRetainTargetlessDraw(interpreter.TypedRegisters, in draw));
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 0u)]
    [InlineData(false, 0x777u)]
    [InlineData(true, 0x777u)]
    [InlineData(false, 0x778u)]
    [InlineData(true, 0x778u)]
    [InlineData(false, 0x781u)]
    [InlineData(true, 0x781u)]
    public void CombinedFlipKeepsCpuPresentationAndRetiresPrepareLabels(bool chained, uint marker)
    {
        using var test = new BackedKernelMemory();
        var context = new CpuContext(test.Memory, Generation.Gen4);
        var packets = typeof(GnmRuntime).Assembly.GetType("SharpEmu.GnmMode.Gpu.GnmCommandPackets")!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var reset = packets.GetMethod("Reset", flags)!;
        var previousMemory = packets.GetField("_memory", flags)!.GetValue(null);
        var previousFactory = VulkanVideoPresenter.TestCommandStreamFactory;
        var stream = AgcExports.GetHeadlessCommandStreamForTests(test.Memory);
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);
        try
        {
            reset.Invoke(null, [test.Memory]);
            VulkanVideoPresenter.TestCommandStreamFactory = _ => stream;
            var ports = (System.Collections.IDictionary)typeof(VideoOutExports).GetField("_ports", flags)!.GetValue(null)!;
            var port = ports[handle]!;
            var groups = (Array)port.GetType().GetProperty("Groups")!.GetValue(port)!;
            groups.SetValue(Activator.CreateInstance(groups.GetType().GetElementType()!, nonPublic: true), 0);
            var slots = (Array)port.GetType().GetProperty("BufferSlots")!.GetValue(port)!;
            var slot = slots.GetValue(0)!;
            slot.GetType().GetProperty("GroupIndex")!.SetValue(slot, 0);
            slot.GetType().GetProperty("AddressLeft")!.SetValue(slot, test.Output + 0x1000);

            var commandsAddress = test.Output + 0x500;
            var result = test.Output + 0x204;
            var flipLabel = test.Output + 0x208;
            var secondResult = test.Output + 0x210;
            var constantResult = test.Output + 0x214;
            Assert.True(context.TryWriteUInt64(flipLabel, 0xDEADBEEF00000000));
            uint[] commands =
            [
                PacketHeader.Make(7, PacketOpcode.Nop, PacketCustomCode.WaitFlipDone),
                (uint)handle, 0, 0, 0, 0, 0,
                PacketHeader.Make(2, PacketOpcode.WaitOnCeCounter), 0,
                PacketHeader.Make(5, PacketOpcode.WriteData), 0x100,
                (uint)result, (uint)(result >> 32), 123,
            ];
            if (marker != 0)
            {
                var tail = new uint[64];
                tail[0] = PacketHeader.Make((uint)tail.Length, PacketOpcode.Nop);
                tail[1] = 0x68750000 | marker;
                tail[2] = (uint)flipLabel;
                tail[3] = (uint)(flipLabel >> 32);
                tail[4] = 42;
                tail[5] = 0x04;
                tail[6] = 0x38;
                commands = [.. commands, .. tail];
            }
            Assert.True(test.Memory.TryWrite(commandsAddress, MemoryMarshal.AsBytes(commands.AsSpan())));
            var address = commandsAddress;
            var words = (uint)commands.Length;
            if (chained)
            {
                address = test.Output + 0x300;
                uint[] root =
                [
                    PacketHeader.Make(4, PacketOpcode.IndirectBuffer),
                    (uint)commandsAddress, (uint)(commandsAddress >> 32), words | 0x00100000,
                ];
                Assert.True(test.Memory.TryWrite(address, MemoryMarshal.AsBytes(root.AsSpan())));
                words = (uint)root.Length;
            }
            Assert.True(context.TryWriteUInt64(test.Output + 0x10, address));
            Assert.True(context.TryWriteUInt32(test.Output + 0x20, words * sizeof(uint)));
            uint[] secondCommands =
            [
                PacketHeader.Make(5, PacketOpcode.WriteData), 0x100,
                (uint)secondResult, (uint)(secondResult >> 32), 456,
            ];
            Assert.True(test.Memory.TryWrite(test.Output + 0x900, MemoryMarshal.AsBytes(secondCommands.AsSpan())));
            Assert.True(context.TryWriteUInt64(test.Output + 0x18, test.Output + 0x900));
            Assert.True(context.TryWriteUInt32(test.Output + 0x24, (uint)secondCommands.Length * sizeof(uint)));
            uint[] constantCommands =
            [
                PacketHeader.Make(3, PacketOpcode.WriteConstantRam), 0, 789,
                PacketHeader.Make(5, PacketOpcode.DumpConstantRam), 0, 1,
                (uint)constantResult, (uint)(constantResult >> 32),
            ];
            Assert.True(test.Memory.TryWrite(test.Output + 0x800, MemoryMarshal.AsBytes(constantCommands.AsSpan())));
            Assert.True(context.TryWriteUInt64(test.Output + 0x30, test.Output + 0x800));
            Assert.True(context.TryWriteUInt64(test.Output + 0x38, 0));
            Assert.True(context.TryWriteUInt32(test.Output + 0x50, (uint)constantCommands.Length * sizeof(uint)));
            Assert.True(context.TryWriteUInt32(test.Output + 0x54, 0));
            context[CpuRegister.Rsp] = test.Output + 0x80;
            Assert.True(context.TryWriteUInt32(context[CpuRegister.Rsp] + 8, 0));
            Assert.True(context.TryWriteUInt32(context[CpuRegister.Rsp] + 16, 1));
            Assert.True(context.TryWriteUInt64(context[CpuRegister.Rsp] + 24, 0x123456789ABCDEF0));

            int Submit()
            {
                context[CpuRegister.Rdi] = 2;
                context[CpuRegister.Rsi] = test.Output + 0x10;
                context[CpuRegister.Rdx] = test.Output + 0x20;
                context[CpuRegister.Rcx] = test.Output + 0x30;
                context[CpuRegister.R8] = test.Output + 0x50;
                context[CpuRegister.R9] = (uint)handle;
                return GnmDriverExports.SubmitAndFlipCommandBuffers(context);
            }

            ulong FlipCount()
            {
                context[CpuRegister.Rdi] = (uint)handle;
                context[CpuRegister.Rsi] = test.Output + 0x400;
                Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(context));
                Span<byte> status = stackalloc byte[8];
                Assert.True(test.Memory.TryRead(test.Output + 0x400, status));
                return BinaryPrimitives.ReadUInt64LittleEndian(status);
            }

            Assert.Equal(0, Submit());
            Assert.True(VideoOutExports.IsFlipDone(handle, 0));
            Assert.False(stream.Queue.HasPending);
            Assert.True(context.TryReadUInt32(result, out var after));
            Assert.Equal(123u, after);
            Assert.True(context.TryReadUInt32(secondResult, out var secondAfter));
            Assert.Equal(456u, secondAfter);
            Assert.True(context.TryReadUInt32(constantResult, out var constantAfter));
            Assert.Equal(789u, constantAfter);
            var submissions = marker is 0x778 or 0x781 ? 3UL : 2UL;
            Assert.Equal(submissions, stream.Queue.SubmissionsStarted);
            Assert.True(context.TryReadUInt64(flipLabel, out var completed));
            Assert.Equal(0xDEADBEEF00000000UL | (marker is 0x778 or 0x781 ? 42UL : 0UL), completed);
            Assert.Equal(1UL, FlipCount());
            Assert.True(context.TryReadUInt64(test.Output + 0x418, out var argument));
            Assert.Equal(0x123456789ABCDEF0UL, argument);

            Assert.Equal(0, Submit());
            Assert.False(stream.Queue.HasPending);
            Assert.Equal(submissions * 2, stream.Queue.SubmissionsStarted);
            Assert.Equal(2UL, FlipCount());
        }
        finally
        {
            context[CpuRegister.Rdi] = (uint)handle;
            _ = VideoOutExports.VideoOutClose(context);
            VulkanVideoPresenter.TestCommandStreamFactory = previousFactory;
            reset.Invoke(null, [previousMemory]);
        }
    }

    [Theory]
    [InlineData(0x778u, false)]
    [InlineData(0x781u, true)]
    public void CombinedPrepareFlipCapturesItsLabelWithoutPublishingItEarly(uint marker, bool interrupt)
    {
        var runner = new StreamRunner();
        var labels = new List<(ulong Address, uint Value, bool Interrupt)>();
        Action<ulong, uint, bool> capture = (address, value, signal) => labels.Add((address, value, signal));
        var packets = typeof(GnmRuntime).Assembly.GetType("SharpEmu.GnmMode.Gpu.GnmCommandPackets")!;
        var handlers = (PacketHandler?[])packets.GetMethod("WithFlipLabels", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [capture])!;
        runner.Interpreter.SetPacketHandlers(handlers);

        Assert.Equal(SubmissionProgress.Complete, runner.Run(StreamRunner.Packet(PacketOpcode.Nop,
            0x68750000 | marker, (uint)StreamRunner.LabelAddress, (uint)(StreamRunner.LabelAddress >> 32), 42, 0x04, 0x38)));

        Assert.Equal((StreamRunner.LabelAddress, 42u, interrupt), Assert.Single(labels));
        Assert.Equal(0u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Empty(runner.Host.EndOfPipeWrites);
        Assert.DoesNotContain(runner.Host.Calls, call => call.StartsWith("prepare_flip ", StringComparison.Ordinal));
    }
}
