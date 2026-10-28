// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorIndirectAxisTests : IDisposable
{
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly FatalScope _fatal = new();
    private const ulong Address = RecordingRenderHost.MemoryBase + 0x400;

    public void Dispose() => _fatal.Dispose();

    private void Arguments(uint x, uint y, uint z)
    {
        Span<byte> bytes = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, x);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], y);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], z);
        _host.WriteGuest(Address, bytes);
    }

    private RenderExecutor Executor(uint localX = 1, uint localY = 2, uint localZ = 128)
    {
        _pipelines.Compute = ComputeProgram(threadsX: localX, threadsY: localY, threadsZ: localZ);
        _host.NativeIndirectDispatch = address => { _host.Calls.Add($"native_indirect {address:X}"); return true; };
        return new RenderExecutor(_host, _pipelines);
    }

    [Theory]
    [InlineData(1u, 2u, 5u, 3u, 2u)]
    [InlineData(4u, 2u, 5u, 2u, 3u)]
    public void RemappedIndirectGroupsUseTheShaderPhysicalAxes(uint localX, uint localY, uint x, uint y, uint z)
    {
        var executor = Executor(localX, localY);
        Arguments(2, 3, 5);

        executor.Dispatch(1, Banks(), 1, 1, 1, 0x41, Address);

        Assert.Contains($"dispatch {x} {y} {z}", _host.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("native_indirect"));
        Assert.Equal(1, _host.GuestReads);
    }

    [Fact]
    public void RemappedIndirectArgumentsCompleteTheirProducerBeforePreparation()
    {
        var executor = Executor();
        Arguments(2, 3, 5);
        _host.BeforeGuestRead = () =>
        {
            Assert.Equal(0, _host.PreparationDepth);
            Arguments(7, 11, 13);
            _host.Calls.Add("producer_complete");
        };

        executor.Dispatch(1, Banks(), 1, 1, 1, 0x41, Address);

        Assert.Contains("dispatch 13 11 7", _host.Calls);
        Assert.True(_host.Calls.IndexOf("producer_complete") < _host.Calls.IndexOf("preparation_begin"));
    }

    [Fact]
    public void IdentityAxesRetainNativeIndirectWithoutReadingTheProducerOnCpu()
    {
        var executor = Executor(64, 2, 1);
        Arguments(2, 3, 5);
        _host.BeforeGuestRead = () => throw new InvalidOperationException("Unexpected CPU read");

        executor.Dispatch(1, Banks(), 1, 1, 1, 0x41, Address);

        Assert.Contains($"native_indirect {Address:X}", _host.Calls);
        Assert.Equal(0, _host.GuestReads);
    }

    [Fact]
    public void ThreadDimensionModePermutesItsAlreadyResolvedCounts()
    {
        var executor = Executor();
        _pipelines.Compute = ComputeProgram(threadDimensions: true, threadsX: 1, threadsY: 2, threadsZ: 128);
        var banks = Banks();
        banks.Shader.Compute.ThreadsX = 1;
        banks.Shader.Compute.ThreadsY = 2;
        banks.Shader.Compute.ThreadsZ = 128;
        _host.BeforeGuestRead = () => throw new InvalidOperationException("Thread counts are already resolved");

        executor.Dispatch(1, banks, 2, 3, 640, 0x61, Address);

        Assert.Contains("dispatch 5 2 2", _host.Calls);
        Assert.Equal(0, _host.GuestReads);
    }

    [Fact]
    public void RemappedGpuProducedZeroDimensionDoesNotRecordWork()
    {
        var executor = Executor();
        Arguments(2, 3, 5);
        _host.BeforeGuestRead = () => Arguments(2, 0, 5);

        executor.Dispatch(1, Banks(), 1, 1, 1, 0x41, Address);

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("dispatch ") || c.StartsWith("native_indirect"));
        Assert.DoesNotContain("create_compute_pipeline", _pipelines.Calls);
    }

    [Fact]
    public void UnreadableRemappedArgumentsFailBeforeNativeSetup()
    {
        var executor = Executor();

        Assert.Throws<RenderExecutorFatalException>(() => executor.Dispatch(1, Banks(), 1, 1, 1, 0x41, ulong.MaxValue - 16));

        Assert.DoesNotContain("create_compute_pipeline", _pipelines.Calls);
        Assert.DoesNotContain("preparation_begin", _host.Calls);
    }
}
