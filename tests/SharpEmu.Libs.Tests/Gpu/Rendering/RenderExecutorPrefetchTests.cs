// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// A draw takes the programs a prefetch resolved from its banks, or resolves them itself.
[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorPrefetchTests : IDisposable
{
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorPrefetchTests() => _executor = new RenderExecutor(_host, _pipelines);

    public void Dispose() => _fatal.Dispose();

    private sealed class ConcurrentHost(bool current) : IConcurrentResolveHost
    {
        public void BeginConcurrentResolve() { }
        public object EndConcurrentResolve() => this;
        public bool IsStillCurrent(object assumptions) => ReferenceEquals(this, assumptions) && current;
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    public void MeshPrefetchPreservesDrawModeAndOwnership(bool indexed, bool requestedIndexed, bool current, bool accepted)
    {
        _pipelines.MeshShadersSupported = true;
        var banks = Banks(PrimitiveTriangleStrip);
        banks.Context.ShaderStages = 0x20;
        var snapshot = banks.Snapshot();
        using var prefetch = new ParallelProgramPrefetch(_executor, _host, new ConcurrentHost(current));
        prefetch.Current = prefetch.Submit(snapshot, indexed: indexed);
        prefetch.WaitIdle();
        var inputs = RenderExecutor.ProgramInputsOf(snapshot, _host.FormatSupport, _host.Fatal, requestedIndexed);

        Assert.Equal(accepted, prefetch.TryTakeGraphics(snapshot, in inputs, out var programs));
        if (accepted) Assert.Same(_pipelines.Graphics, programs);
        Assert.Single(_pipelines.Calls, call => call.StartsWith("get_mesh_programs", StringComparison.Ordinal));
        Assert.DoesNotContain(_pipelines.Calls, call => call.StartsWith("get_graphics_programs", StringComparison.Ordinal));
        Assert.Equal(indexed ? 4u : PrimitiveTriangleStrip, _pipelines.MeshPrimitiveType);
        Assert.Equal(PrimitiveTriangleStrip, snapshot.UserConfig.PrimitiveType);
        Assert.Equal(PrimitiveTriangleStrip, banks.UserConfig.PrimitiveType);
    }

    // Resolves ahead with the executor, as a worker would, and hands the result back once.
    private sealed class AheadPrefetch(RenderExecutor executor, IRenderHost host) : IProgramPrefetch
    {
        private RegisterBanks? _banks;
        private GraphicsProgramInputs _inputs;
        private GraphicsPrograms? _programs;

        public int Taken { get; private set; }

        public int Declined { get; private set; }

        public void Resolve(RegisterBanks banks, bool indexed = false)
        {
            _banks = banks;
            _inputs = RenderExecutor.ProgramInputsOf(banks, host.FormatSupport, host.Fatal, indexed);
            _programs = executor.ResolveGraphicsPrograms(banks, in _inputs);
        }

        public bool TryTakeGraphics(RegisterBanks banks, in GraphicsProgramInputs inputs, out GraphicsPrograms programs)
        {
            if (_programs is not null && ReferenceEquals(banks, _banks) && inputs.Equals(_inputs))
            {
                programs = _programs;
                _programs = null;
                Taken++;
                return true;
            }

            programs = null!;
            Declined++;
            return false;
        }
    }

    [Fact]
    public void ADrawTakesTheProgramsResolvedAheadFromItsBanks()
    {
        var prefetch = new AheadPrefetch(_executor, _host);
        _executor.Prefetch = prefetch;
        var banks = Banks();
        prefetch.Resolve(banks);
        var resolutions = _pipelines.Calls.Count(call => call.StartsWith("get_graphics_programs", StringComparison.Ordinal));

        _executor.DrawAuto(1, banks, Auto(3));

        Assert.Equal(1, prefetch.Taken);
        Assert.Equal(resolutions, _pipelines.Calls.Count(call => call.StartsWith("get_graphics_programs", StringComparison.Ordinal)));
        Assert.Contains(_host.Calls, call => call.StartsWith("draw ", StringComparison.Ordinal));
    }

    [Fact]
    public void ADrawWithOtherBanksResolvesItsOwnPrograms()
    {
        var prefetch = new AheadPrefetch(_executor, _host);
        _executor.Prefetch = prefetch;
        prefetch.Resolve(Banks());

        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.Equal((0, 1), (prefetch.Taken, prefetch.Declined));
        Assert.Equal(2, _pipelines.Calls.Count(call => call.StartsWith("get_graphics_programs", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedDrawAcceptsOnlyIndexedPrefetch(bool indexed)
    {
        var prefetch = new AheadPrefetch(_executor, _host);
        _executor.Prefetch = prefetch;
        var banks = Banks();
        prefetch.Resolve(banks, indexed);

        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Equal(indexed ? (1, 0) : (0, 1), (prefetch.Taken, prefetch.Declined));
    }
}
