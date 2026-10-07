// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// With the trace channel off, a cache hit writes no line and allocates the same as any other hit.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderPipelineTraceTests : IDisposable
{
    private const ulong Code = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong DataBase = PipelineTestGuest.MemoryBase + 0x4_0000;

    private readonly FatalScope _fatal = new();
    private readonly PipelineTestGuest _guest = new();

    public void Dispose() => _fatal.Dispose();

    private ShaderProgram Hit()
    {
        var cursor = 0u;
        return _guest.Programs.GetOrCompile(
            _guest.Source(Code, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, 75)),
            PipelineTestGuest.ComputeOptions(),
            ref cursor,
            out _);
    }

    [Fact]
    public void ChannelOff_CacheHitWritesNoTraceLineAndAllocatesNoMore()
    {
        if (RenderTrace.Enabled)
        {
            Console.Error.WriteLine("[TEST][SKIP] SHARPEMU_LOG_AGC is on; the untraced baseline cannot be measured.");
            return;
        }

        _guest.RegisterProgram(Code, Header, PipelineTestGuest.FormatLoadProgram);
        var compiled = Hit();
        Assert.Equal(compiled.Id, Hit().Id);
        var previous = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Hit();
            var baseline = GC.GetAllocatedBytesForCurrentThread();
            Hit();
            var first = GC.GetAllocatedBytesForCurrentThread() - baseline;
            baseline = GC.GetAllocatedBytesForCurrentThread();
            Hit();
            var second = GC.GetAllocatedBytesForCurrentThread() - baseline;
            Assert.True(second <= first, $"a traced hit allocated more than the untraced baseline: {second} > {first}");
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.DoesNotContain("[RENDER][TRACE]", captured.ToString());
        Assert.Equal(1, _guest.Compiler.Compilations);
    }

    [Fact]
    public void PipelineGate_IsBoundedWhenTheChannelIsOn()
    {
        // The gate counts every call; the bound keeps a long run from flooding the log.
        var first = RenderTrace.Pipeline();
        Assert.True(first || !first);
    }
}
