// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// Dispatches over a recording host: initiator decode, thread dimensions, both clear consumers and the tail.
[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorComputeTests : IDisposable
{
    private const ulong MetadataAddress = RecordingRenderHost.MemoryBase + 0x60_0000;
    private const uint ClearInitiator = 0x61;
    private const uint ClearValue = 0x8080_8080;

    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorComputeTests() => _executor = new RenderExecutor(_host, _pipelines);

    public void Dispose() => _fatal.Dispose();

    private static BufferResourceInfo ClearResource(bool read = false, bool formatted = true, bool written = true, uint maxByteExtent = 16, uint packedStride = 16) =>
        new(read, written, false, formatted, false, maxByteExtent, packedStride);

    private static uint[] ClearUserData(uint[] descriptor, uint value, uint last = ClearValue) => [descriptor[0], descriptor[1], descriptor[2], descriptor[3], value, value, value, last];

    // A dispatch shaped as the full-buffer fill kernel: 64 threads per group over every record.
    private void ConfigureClearKernel(uint records, BufferResourceInfo? resource = null, uint[]? descriptor = null, uint[]? userData = null, bool exclusiveOr = false)
    {
        descriptor ??= BufferDescriptor(MetadataAddress, 16, records);
        var program = Program(ShaderStageKind.Compute, buffers: [resource ?? ClearResource()], exclusiveOr: exclusiveOr);
        _pipelines.Compute = ComputeProgram(Stage(program, buffers: [descriptor], userData: userData ?? ClearUserData(descriptor, ClearValue)), threadDimensions: true);
    }

    private void AssertDispatched(uint x, uint y, uint z)
    {
        Assert.Contains($"dispatch {x} {y} {z}", _host.Calls);
        Assert.Contains("create_compute_pipeline", _pipelines.Calls);
    }

    private void AssertNotDispatched()
    {
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("dispatch", StringComparison.Ordinal));
        Assert.DoesNotContain("create_compute_pipeline", _pipelines.Calls);
    }

    [Fact]
    public void Dispatch_RecordsTheTailInOrder()
    {
        _executor.Dispatch(5, Banks(), 4, 2, 1, 0x41);

        var expected = new[]
        {
            "pending", "debug DispatchDirect 5 4 2 1 41 200020000", "end_rendering", "preparation_begin", "prepare_bindings Compute -> 1",
            "bind_resources 1", "commit Compute C1 [1]", "bind_pipeline Compute C1", "dispatch 4 2 1", "access_barrier", "preparation_end", "reset_bindings",
        };
        Assert.Equal(expected, _host.Calls);
        Assert.Equal(["get_compute_program threadDimensions=False", "create_compute_pipeline"], _pipelines.Calls);
    }

    [Fact]
    public void NullComputeShader_IsIgnoredBeforeTheProgramLookup()
    {
        var banks = Banks();
        banks.Shader.Compute.Address = 0;
        _executor.Dispatch(1, banks, 1, 1, 1, 0x41);

        Assert.Empty(_pipelines.Calls);
        Assert.DoesNotContain("end_rendering", _host.Calls);
    }

    [Theory]
    [InlineData(0u, 64u, 0u)]
    [InlineData(1u, 64u, 1u)]
    [InlineData(64u, 64u, 1u)]
    [InlineData(65u, 64u, 2u)]
    [InlineData(7u, 0u, 7u)]
    public void GroupsFromThreads_RoundsUpAndTreatsZeroGroupSizeAsOne(uint threads, uint groupSize, uint expected) =>
        Assert.Equal(expected, RenderExecutor.GroupsFromThreads(threads, groupSize));

    [Fact]
    public void ThreadDimensionInitiator_ConvertsThreadsToGroupsAndRecordsTheThreadCounts()
    {
        _pipelines.Compute = ComputeProgram(threadDimensions: true, threadsX: 8, threadsY: 8);
        var banks = Banks();
        banks.Shader.Compute.ThreadsX = 8;
        banks.Shader.Compute.ThreadsY = 8;
        _executor.Dispatch(1, banks, 100, 17, 1, 0x41 | (1u << 5));

        Assert.True(_pipelines.LastDispatchThreadDimensions);
        Assert.Equal((100u, 17u, 1u), (_pipelines.Compute.Input.DispatchThreadsX, _pipelines.Compute.Input.DispatchThreadsY, _pipelines.Compute.Input.DispatchThreadsZ));
        AssertDispatched(13, 3, 1);
    }

    [Fact]
    public void ZeroSizedDispatch_IsSkippedWithoutEndingRenderingOrResettingBindings()
    {
        _executor.Dispatch(1, Banks(), 4, 0, 1, 0x41);

        AssertNotDispatched();
        Assert.DoesNotContain("end_rendering", _host.Calls);
        Assert.DoesNotContain("reset_bindings", _host.Calls);
    }

    [Fact]
    public void StorageWrites_InsertTheHazardBarrierBeforeThePipelineBind()
    {
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [written]), buffers: [BufferDescriptor(VertexBase, 4, 16)]));
        _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);
        Assert.True(_host.Calls.IndexOf("write_hazard_barrier") < _host.Calls.IndexOf("bind_pipeline Compute C1"));

        _host.Calls.Clear();
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, images: [new ImageResourceInfo(ImageResourceClass.Storage, true)])));
        _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);
        Assert.Contains("write_hazard_barrier", _host.Calls);

        _host.Calls.Clear();
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, images: [new ImageResourceInfo(ImageResourceClass.Sampled, true)])));
        _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);
        Assert.DoesNotContain("write_hazard_barrier", _host.Calls);
    }

    [Fact]
    public void Preparation_IsReleasedWhenTheDispatchFailsInsideTheScope()
    {
        _host.FailPrepareBindings = true;
        Assert.Throws<RenderExecutorFatalException>(() => _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41));

        Assert.Equal("preparation_end", _host.Calls[^1]);
        Assert.Equal((0, 1), (_host.PreparationDepth, _host.PreparationsOpened));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("dispatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Preparation_IsNotOpenedForAConsumedOrSkippedDispatch()
    {
        _host.ClearableImages.Add(MetadataAddress);
        ConfigureClearKernel(256);
        _executor.Dispatch(1, Banks(), 256, 1, 1, ClearInitiator);
        _executor.Dispatch(2, Banks(), 4, 0, 1, 0x41);

        Assert.Equal(0, _host.PreparationsOpened);
    }

    [Fact]
    public void DeviceAddresses_ArePreparedBetweenPreparationAndBinding()
    {
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, usesDeviceAddresses: true)));
        _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);

        var prepare = _host.Calls.IndexOf("prepare_bindings Compute -> 1");
        var addresses = _host.Calls.IndexOf("prepare_device_addresses");
        var bind = _host.Calls.IndexOf("bind_resources 1");
        Assert.True(prepare < addresses && addresses < bind);
    }

    [Fact]
    public void FullUniformClear_ReachesTheImageClearWithTheExactRangeAndValue()
    {
        _host.ClearableImages.Add(MetadataAddress);
        ConfigureClearKernel(256);
        _executor.Dispatch(1, Banks(), 256, 1, 1, ClearInitiator);

        Assert.Contains($"clear_image {MetadataAddress:X} 1000 {ClearValue:X8}", _host.Calls);
        Assert.Contains("reset_bindings", _host.Calls);
        AssertNotDispatched();
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("absorb_dcc", StringComparison.Ordinal));
    }

    [Fact]
    public void ImageClear_DecodesOnlyTheFullDispatchShape()
    {
        ConfigureClearKernel(256);
        var input = _pipelines.Compute.Input;
        input.DispatchThreadsX = 256;
        input.DispatchThreadsY = 1;
        input.DispatchThreadsZ = 1;

        var clear = _executor.TryDecodeImageClear(input, 256, 1, 1, ClearInitiator);
        Assert.NotNull(clear);
        Assert.Equal((MetadataAddress, ClearValue, 0x1000ul), (clear.Value.Descriptor.Address, clear.Value.PackedClear, clear.Value.Size));

        Assert.Null(_executor.TryDecodeImageClear(input, 256, 1, 1, 0x41));
        Assert.Null(_executor.TryDecodeImageClear(input, 256, 2, 1, ClearInitiator));
        Assert.Null(_executor.TryDecodeImageClear(input, 255, 1, 1, ClearInitiator));
        input.DispatchThreadsX = 128;
        Assert.Null(_executor.TryDecodeImageClear(input, 256, 1, 1, ClearInitiator));
    }

    [Fact]
    public void DifferentPatternDwords_RefuseTheClearAndRunTheDispatch()
    {
        _host.ClearableImages.Add(MetadataAddress);
        var descriptor = BufferDescriptor(MetadataAddress, 16, 256);
        ConfigureClearKernel(256, descriptor: descriptor, userData: ClearUserData(descriptor, ClearValue, last: ClearValue + 1));
        _executor.Dispatch(1, Banks(), 256, 1, 1, ClearInitiator);

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("clear_image", StringComparison.Ordinal));
        AssertDispatched(4, 1, 1);
    }

    [Fact]
    public void PartialDispatch_RefusesTheClearAndRunsTheDispatch()
    {
        _host.ClearableImages.Add(MetadataAddress);
        ConfigureClearKernel(256);
        _executor.Dispatch(1, Banks(), 128, 1, 1, ClearInitiator);

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("clear_image", StringComparison.Ordinal));
        AssertDispatched(2, 1, 1);
    }

    public static TheoryData<string, BufferResourceInfo, uint[]> RefusedClearShapes => new()
    {
        { "read-modify-write", ClearResource(read: true), BufferDescriptor(MetadataAddress, 16, 256) },
        { "unformatted", ClearResource(formatted: false), BufferDescriptor(MetadataAddress, 16, 256) },
        { "narrow extent", ClearResource(maxByteExtent: 4), BufferDescriptor(MetadataAddress, 16, 256) },
        { "stride mismatch", ClearResource(packedStride: 8), BufferDescriptor(MetadataAddress, 8, 256) },
        { "other format", ClearResource(), BufferDescriptor(MetadataAddress, 16, 256, format: 10) },
        { "swizzled", ClearResource(packedStride: 16 | (1u << 14)), BufferDescriptor(MetadataAddress, 16, 256, swizzle: true) },
        { "thread id added", ClearResource(packedStride: 16 | (1u << 20)), BufferDescriptor(MetadataAddress, 16, 256, addThreadId: true) },
    };

    [Theory]
    [MemberData(nameof(RefusedClearShapes))]
    public void OtherKernelShapes_RefuseTheClear(string shape, BufferResourceInfo resource, uint[] descriptor)
    {
        _host.ClearableImages.Add(MetadataAddress);
        ConfigureClearKernel(256, resource, descriptor, ClearUserData(descriptor, ClearValue));
        _executor.Dispatch(1, Banks(), 256, 1, 1, ClearInitiator);

        Assert.True(!_host.Calls.Exists(c => c.StartsWith("clear_image", StringComparison.Ordinal)), shape);
        Assert.Contains(_host.Calls, c => c.StartsWith("dispatch ", StringComparison.Ordinal));
    }

    [Fact]
    public void UnregisteredMetadataAddress_TakesThePendingPathAndStillDispatches()
    {
        ConfigureClearKernel(256);
        _executor.Dispatch(1, Banks(), 256, 1, 1, ClearInitiator);

        Assert.Contains($"clear_image {MetadataAddress:X} 1000 {ClearValue:X8}", _host.Calls);
        Assert.Contains($"absorb_dcc {MetadataAddress:X} 1000 {ClearValue:X8}", _host.Calls);
        AssertDispatched(4, 1, 1);
    }

    [Fact]
    public void RegisteredDccAddress_ConsumesTheFillAndRecordsNothing()
    {
        _host.RegisteredDcc.Add(MetadataAddress);
        ConfigureClearKernel(256);
        _executor.Dispatch(1, Banks(), 256, 1, 1, ClearInitiator);

        Assert.Contains($"absorb_dcc {MetadataAddress:X} 1000 {ClearValue:X8}", _host.Calls);
        AssertNotDispatched();
        Assert.DoesNotContain("end_rendering", _host.Calls);
        Assert.Contains("reset_bindings", _host.Calls);
    }

    [Fact]
    public void MetadataClear_ConsumesAWrittenBufferOverClearableMetadata()
    {
        _host.MetadataAddresses.Add(MetadataAddress);
        _host.ClearableMetadata.Add(MetadataAddress);
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [written]), buffers: [BufferDescriptor(MetadataAddress, 4, 16)]));
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41);

        Assert.Contains($"clear_metadata {MetadataAddress:X}", _host.Calls);
        AssertNotDispatched();
        Assert.Contains("reset_bindings", _host.Calls);
    }

    [Fact]
    public void MetadataClear_RefusesAReadModifyWriteKernelOverMetadata()
    {
        _host.MetadataAddresses.Add(MetadataAddress);
        _host.ClearableMetadata.Add(MetadataAddress);
        var readWrite = new BufferResourceInfo(true, true, false, false, false, 4, 0);
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [readWrite]), buffers: [BufferDescriptor(MetadataAddress, 4, 16)]));
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41);

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("clear_metadata", StringComparison.Ordinal));
        AssertDispatched(4, 1, 1);
    }

    [Fact]
    public void MetadataClear_RefusesAProgramWithBitwiseExclusiveOr()
    {
        _host.ClearableMetadata.Add(MetadataAddress);
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [written], exclusiveOr: true), buffers: [BufferDescriptor(MetadataAddress, 4, 16)]));
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41);

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("clear_metadata", StringComparison.Ordinal));
        AssertDispatched(4, 1, 1);
    }

    [Fact]
    public void MetadataClear_WithAMismatchedBufferCountIsFatal()
    {
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [written])));
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41));
        Assert.Contains("descriptors=0 program=1", fatal.Message);
    }

    [Fact]
    public void ShortBufferDescriptor_IsFatal()
    {
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [written]), buffers: [[1, 2]]));
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41));
        Assert.Contains("index=0 words=2", fatal.Message);
    }
}
