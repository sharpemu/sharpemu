// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// A draw whose registers repeat the previous draw's takes that draw's record with only its own arguments; the result must be
// the record a full resolve of the same draw makes.
[Collection(SchedulingStateCollection.Name)]
public sealed class RepeatDrawTests : IDisposable
{
    private sealed class CapturingSink : IResolvedCommandSink
    {
        private long _sequence;

        public List<ResolvedDraw> Draws { get; } = [];

        public long NextSequence() => ++_sequence;

        public void Record(ResolvedDraw record) => Draws.Add(record);

        public void Record(ResolvedDispatch record) => throw new InvalidOperationException("no dispatches here");
    }

    private const ulong IndirectArguments = RecordingRenderHost.MemoryBase + 0x60_0000;

    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly CapturingSink _sink = new();
    private readonly PendingGpuWrites _pending = new();
    private readonly RecordPool _pool = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RepeatDrawTests() => _executor = new RenderExecutor(_host, _pipelines, _sink, _pending, _pool);

    public void Dispose() => _fatal.Dispose();

    private static DrawIndexedArguments IndirectIndexed(uint count, uint indexType = 0, int baseVertex = 0, ulong indexOffset = 0, ulong argumentsOffset = 0) =>
        Indexed(count, indexType: indexType, baseVertex: baseVertex, source: DrawOffsetSource.IndirectArguments, indexAddress: IndexBase + indexOffset) with
        {
            IndirectArgumentsAddress = IndirectArguments + argumentsOffset,
        };

    // An unbounded index buffer is too large for the GPU to read the arguments against it, so the CPU reads them.
    private static DrawIndexedArguments CpuArguments(ulong argumentsOffset) =>
        IndirectIndexed(0x7FFF_FFFF, argumentsOffset: argumentsOffset) with { UnboundedIndexBuffer = true };

    private ResolvedDraw Template(RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)]);
        _executor.DrawIndexed(7, banks, arguments);
        var template = Assert.Single(_sink.Draws);
        Assert.Equal(ResolvedDrawKind.Draw, template.Kind);
        Assert.True(template.RepeatableArguments);
        return template;
    }

    private void WriteArguments(ulong address, params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var index = 0; index < words.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), words[index]);
        }

        _host.WriteGuest(address, bytes);
    }

    // The repeat, then a full resolve of the same draw.
    private (ResolvedDraw Repeat, ResolvedDraw Full) RepeatAndResolve(ResolvedDraw template, RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        Assert.True(_executor.TryRepeatIndexed(template, 8, banks, arguments));
        var repeat = _sink.Draws[^1];
        _executor.DrawIndexed(8, banks, arguments);
        var full = _sink.Draws[^1];
        Assert.NotSame(repeat, full);
        Assert.Equal(full.Sequence - 1, repeat.Sequence);
        return (repeat, full);
    }

    [Fact]
    public void ARepeatRecordsWhatAFullResolveOfItsArgumentsRecords()
    {
        var banks = Banks().Snapshot();
        var template = Template(banks, IndirectIndexed(40));

        var (repeat, full) = RepeatAndResolve(template, banks, IndirectIndexed(24, baseVertex: 5, indexOffset: 0x40, argumentsOffset: 0x20));

        Assert.Null(FirstDifference(repeat, full));
        Assert.Equal(IndexBase + 0x40, repeat.Index.Address);
        Assert.Equal(48ul, repeat.Index.Size);
        Assert.Equal(IndirectArguments + 0x20, repeat.Emission.IndirectArgumentsAddress);
        Assert.Equal(8ul, repeat.SubmitId);
    }

    [Fact]
    public void ARepeatMarksItsTargetAtItsOwnSequence()
    {
        var banks = Banks().Snapshot();
        var template = Template(banks, IndirectIndexed(40));
        _pending.Release(template.Sequence);
        Assert.False(_pending.TryGetPending(ColorBase, 4, out _));

        Assert.True(_executor.TryRepeatIndexed(template, 8, banks, IndirectIndexed(24, argumentsOffset: 0x20)));

        Assert.True(_pending.TryGetPending(ColorBase, 4, out var sequence));
        Assert.Equal(_sink.Draws[^1].Sequence, sequence);
    }

    [Fact]
    public void ARepeatOfACpuArgumentsDrawReadsItsOwnArguments()
    {
        var banks = Banks().Snapshot();
        WriteArguments(IndirectArguments, 12, 1, 0, 0, 0);
        WriteArguments(IndirectArguments + 0x20, 9, 2, 3, 4, 0);
        var template = Template(banks, CpuArguments(0));
        Assert.True(template.RepeatsCpuArguments);

        var (repeat, full) = RepeatAndResolve(template, banks, CpuArguments(0x20));

        Assert.Null(FirstDifference(repeat, full));
        Assert.Equal(9u, repeat.Draw.Count);
        Assert.Equal(IndexBase + 6, repeat.Index.Address);
        Assert.Equal(0ul, repeat.Emission.IndirectArgumentsAddress);
    }

    [Fact]
    public void ARepeatWhoseArgumentsDrawNothingIsASkippedRecord()
    {
        var banks = Banks().Snapshot();
        WriteArguments(IndirectArguments, 12, 1, 0, 0, 0);
        WriteArguments(IndirectArguments + 0x20, 0, 1, 0, 0, 0);
        var template = Template(banks, CpuArguments(0));

        var (repeat, full) = RepeatAndResolve(template, banks, CpuArguments(0x20));

        Assert.Equal(ResolvedDrawKind.Skipped, full.Kind);
        Assert.Null(FirstDifference(repeat, full));
    }

    [Fact]
    public void ARepeatOnTheOtherArgumentsPathResolvesInFull()
    {
        var banks = Banks().Snapshot();
        WriteArguments(IndirectArguments, 12, 1, 0, 0, 0);
        var template = Template(banks, CpuArguments(0));

        Assert.False(_executor.TryRepeatIndexed(template, 8, banks, IndirectIndexed(24, argumentsOffset: 0x20)));
        Assert.Single(_sink.Draws);
    }

    [Fact]
    public void ARepeatWithAnotherIndexTypeResolvesInFull()
    {
        var banks = Banks().Snapshot();
        var template = Template(banks, IndirectIndexed(40, indexType: 0));

        Assert.False(_executor.TryRepeatIndexed(template, 8, banks, IndirectIndexed(24, indexType: 1)));
        Assert.Single(_sink.Draws);
    }

    [Fact]
    public void ARepeatWithoutCountsResolvesInFull()
    {
        var banks = Banks().Snapshot();
        var template = Template(banks, IndirectIndexed(40));

        Assert.False(_executor.TryRepeatIndexed(template, 8, banks, IndirectIndexed(0)));
        Assert.Single(_sink.Draws);
    }

    [Fact]
    public void APacketDrawIsNotRepeatable()
    {
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)]);
        _executor.DrawIndexed(7, Banks(), Indexed(40));
        var record = Assert.Single(_sink.Draws);

        Assert.False(record.RepeatableArguments);
        Assert.False(_executor.TryRepeatIndexed(record, 8, Banks(), IndirectIndexed(24)));
        Assert.Single(_sink.Draws);
    }

    [Fact]
    public void ARecordCopyEqualsItsSource()
    {
        var banks = Banks().Snapshot();
        var template = Template(banks, IndirectIndexed(40));
        var copy = _pool.RentDraw();

        copy.CopyFrom(template);

        Assert.Equal(template.Sequence, copy.Sequence);
        Assert.Null(FirstDifference(copy, template));
        copy.Index = copy.Index with { Size = copy.Index.Size + 4 };
        Assert.Equal("index", FirstDifference(copy, template));
    }

    [Fact]
    public void AReadLogCopyKeepsEveryRead()
    {
        var source = new ResolveReadLog { Version = 3, Publications = 4 };
        source.Add(0x1000, 0xAABBCCDDu);
        source.Add(0x1004, 0x11223344u);
        source.Add(0x2000, [1, 2, 3], clean: true);
        for (var index = 0; index < 100; index++)
        {
            source.Add(0x10_0000 + ((ulong)index * 0x100), new byte[32]);
        }

        var copy = new ResolveReadLog();
        copy.Add(0x9000, 7u);

        copy.CopyFrom(source);

        Assert.Equal(source.Count, copy.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var expected = source.Bytes(index, out var address).ToArray();
            Assert.Equal(expected, copy.Bytes(index, out var copied).ToArray());
            Assert.Equal(address, copied);
            Assert.Equal(source.IsClean(index), copy.IsClean(index));
        }

        Assert.Equal(3, copy.Version);
        Assert.Equal(4, copy.Publications);
        copy.Add(0x3000, 9u);
        Assert.Equal(source.Count + 1, copy.Count);
    }

    // The first field in which two draw records differ (the sequence aside), or null.
    private static string? FirstDifference(ResolvedDraw a, ResolvedDraw b)
    {
        if (a.Kind != b.Kind) return "kind";
        if (a.Announce != b.Announce) return "announce";
        if (a.Debug != b.Debug) return "debug";
        if (a.ResetBindings != b.ResetBindings) return "reset_bindings";
        if (a.TargetsResolved != b.TargetsResolved) return "targets_resolved";
        if (a.SubmitId != b.SubmitId) return "submit";
        if (a.Draw != b.Draw) return "draw";
        if (a.Topology != b.Topology) return "topology";
        if (a.PrimitiveRestart != b.PrimitiveRestart) return "primitive_restart";
        if (a.PrimitiveType != b.PrimitiveType) return "primitive_type";
        if (a.Emission != b.Emission) return "emission";
        if (a.Index != b.Index) return "index";
        if (a.TessellationIndexAddress != b.TessellationIndexAddress || a.TessellationIndexSize != b.TessellationIndexSize ||
            a.TessellationVertexOffset != b.TessellationVertexOffset) return "tessellation";
        if (!ReferenceEquals(a.Context, b.Context)) return "context";
        if (a.ContextStamp != b.ContextStamp) return "context_stamp";
        if (a.DynamicStamp != b.DynamicStamp) return "dynamic_stamp";
        if (a.TargetVersion != b.TargetVersion) return "target_version";
        if (a.Dynamic != b.Dynamic) return "dynamic";
        if (a.WritesMemory != b.WritesMemory) return "writes_memory";
        if (a.WriteStages != b.WriteStages) return "write_stages";
        if (a.SetBindDebug != b.SetBindDebug || a.SetAutoDebug != b.SetAutoDebug) return "debug_flags";
        if (!ReferenceEquals(a.IndexedTrace, b.IndexedTrace)) return "indexed_trace";
        if (a.PacketAddress != b.PacketAddress) return "packet";
        if (a.RepeatableArguments != b.RepeatableArguments || a.RepeatsCpuArguments != b.RepeatsCpuArguments) return "repeatable";
        ref var stateA = ref a.State;
        ref var stateB = ref b.State;
        if (!ReferenceEquals(stateA.Programs, stateB.Programs)) return "programs";
        if (stateA.ColorCount != stateB.ColorCount) return "color_count";
        for (var index = 0; index < (int)stateA.ColorCount; index++)
        {
            if (!SameValue(stateA.Colors[index], stateB.Colors[index])) return "color_target";
        }

        if (!SameValue(stateA.Depth, stateB.Depth)) return "depth";
        if (stateA.DepthResolved != stateB.DepthResolved) return "depth_resolved";
        if (stateA.PixelActive != stateB.PixelActive) return "pixel_active";
        if (!stateA.Rendering.Equals(stateB.Rendering)) return "rendering";
        if (stateA.StartInstanceUserData != stateB.StartInstanceUserData || stateA.BaseVertexUserData != stateB.BaseVertexUserData)
        {
            return "draw_user_data";
        }

        return null;
    }

    // Raw bytes, references included: default equality throws on the inline arrays these states hold.
    private static bool SameValue<T>(T a, T b) where T : struct =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref a), Unsafe.SizeOf<T>())
            .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref b), Unsafe.SizeOf<T>()));
}
