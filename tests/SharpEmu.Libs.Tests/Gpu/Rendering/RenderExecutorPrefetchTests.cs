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

    // Resolves ahead with the executor, as a worker would, and hands the result back once.
    private sealed class AheadPrefetch(RenderExecutor executor, IRenderHost host) : IProgramPrefetch
    {
        private RegisterBanks? _banks;
        private GraphicsProgramInputs _inputs;
        private GraphicsPrograms? _programs;

        public int Taken { get; private set; }

        public int Declined { get; private set; }

        public void Resolve(RegisterBanks banks)
        {
            _banks = banks;
            _inputs = RenderExecutor.ProgramInputsOf(banks, host.FormatSupport, host.Fatal);
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
}
