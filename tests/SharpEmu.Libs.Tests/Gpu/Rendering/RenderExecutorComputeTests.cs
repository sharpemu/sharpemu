// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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
    private const ulong IndirectArgumentsAddress = RecordingRenderHost.MemoryBase + 0x70_0000;
    private const uint ClearInitiator = 0x61;
    private const uint ClearValue = 0x8080_8080;
    private static readonly ComputeWorkgroupAxisMapping VulkanTallZAxisMapping = new(1, 2, 0);

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

    private void WriteDispatchArguments(uint groupsX, uint groupsY, uint groupsZ)
    {
        var arguments = new byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments, groupsX);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(sizeof(uint)), groupsY);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(2 * sizeof(uint)), groupsZ);
        _host.WriteGuest(IndirectArgumentsAddress, arguments);
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
    public void IndirectDispatch_DefaultsToCpuResolvedCounts()
    {
        WriteDispatchArguments(2, 3, 4);

        // Zero XYZ is the interpreter's deferred-count representation when the
        // command-stream host can otherwise leave the arguments on the GPU.
        _executor.Dispatch(1, Banks(), 0, 0, 0, 0x41, IndirectArgumentsAddress);

        Assert.Contains("dispatch 2 3 4", _host.Calls);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("dispatch_indirect ", StringComparison.Ordinal));
        Assert.Equal(1, _host.GuestReads);
    }

    [Fact]
    public void IndirectDispatch_NativePathRequiresExplicitOptIn()
    {
        var nativeExecutor = new RenderExecutor(_host, _pipelines, strictDrawResources: true, nativeIndirectDispatch: true);

        nativeExecutor.Dispatch(1, Banks(), 0, 0, 0, 0x41, IndirectArgumentsAddress);

        Assert.Contains($"dispatch_indirect {IndirectArgumentsAddress:X}", _host.Calls);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("dispatch ", StringComparison.Ordinal));
        Assert.Equal(0, _host.GuestReads);
    }

    [Fact]
    public void NativeIndirectDispatch_ResolvesCountsForADirectHostFallback()
    {
        WriteDispatchArguments(2, 3, 4);
        _host.AcceptIndirectDispatch = false;
        var nativeExecutor = new RenderExecutor(_host, _pipelines, strictDrawResources: true, nativeIndirectDispatch: true);

        nativeExecutor.Dispatch(1, Banks(), 0, 0, 0, 0x41, IndirectArgumentsAddress);

        Assert.Contains($"dispatch_indirect {IndirectArgumentsAddress:X}", _host.Calls);
        Assert.Contains("dispatch 2 3 4", _host.Calls);
        Assert.Equal(1, _host.GuestReads);
    }

    [Fact]
    public void TallZWorkgroup_PermutesLogicalDispatchCounts()
    {
        _pipelines.Compute = ComputeProgram(
            threadsX: 1,
            threadsY: 1,
            threadsZ: 256);
        _pipelines.Compute.Input.WorkgroupAxisMapping = VulkanTallZAxisMapping;

        _executor.Dispatch(1, Banks(), 2, 3, 4, 0x41);

        AssertDispatched(4, 2, 3);
    }

    [Fact]
    public void TallZWorkgroup_IdentityBackendPreservesLogicalDispatchCounts()
    {
        _pipelines.Compute = ComputeProgram(
            threadsX: 1,
            threadsY: 1,
            threadsZ: 256);

        _executor.Dispatch(1, Banks(), 2, 3, 4, 0x41);

        AssertDispatched(2, 3, 4);
    }

    [Fact]
    public void TallZWorkgroup_UsesCpuResolvedIndirectCounts()
    {
        var nativeExecutor = new RenderExecutor(
            _host,
            _pipelines,
            strictDrawResources: true,
            nativeIndirectDispatch: true);
        _pipelines.Compute = ComputeProgram(
            threadsX: 1,
            threadsY: 1,
            threadsZ: 256);
        _pipelines.Compute.Input.WorkgroupAxisMapping = VulkanTallZAxisMapping;
        WriteDispatchArguments(2, 3, 4);

        nativeExecutor.Dispatch(
            1,
            Banks(),
            0,
            0,
            0,
            0x41,
            IndirectArgumentsAddress);

        AssertDispatched(4, 2, 3);
        Assert.Equal(1, _host.GuestReads);
        Assert.DoesNotContain(
            _host.Calls,
            call => call.StartsWith("dispatch_indirect ", StringComparison.Ordinal));
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

    // AGC's fill kernel: s_buffer_load_dword the value from a one-record buffer, store it per thread.
    private void ConfigureLoadedValueFill(uint records, ulong valueAddress, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_host.GuestMemory.TryWrite(valueAddress, bytes));
        var target = BufferDescriptor(MetadataAddress, 4, records, format: BufferDescriptorWords.Format32UInt);
        var source = BufferDescriptor(valueAddress, 0, 1);
        var program = Program(ShaderStageKind.Compute, buffers:
        [
            new BufferResourceInfo(Read: true, Written: false, Atomic: false, Formatted: false, Scalar: true, MaxByteExtent: 4, PackedStride: 0),
            ClearResource(maxByteExtent: 4, packedStride: 4),
        ]);
        _pipelines.Compute = ComputeProgram(Stage(program, buffers: [source, target], userData: [.. target, .. source]));
    }

    [Fact]
    public void LoadedValueFill_DecodesWithTheValueReadFromItsSourceBuffer()
    {
        const ulong valueAddress = RecordingRenderHost.MemoryBase + 0x70_0000;
        ConfigureLoadedValueFill(128 * 64, valueAddress, 0x4040_4040);

        var clear = _executor.TryDecodeImageClear(_pipelines.Compute.Input, 128, 1, 1, 0x41);
        Assert.NotNull(clear);
        Assert.Equal((MetadataAddress, 0x4040_4040u, 0x8000ul), (clear.Value.Descriptor.Address, clear.Value.PackedClear, clear.Value.Size));

        // The dispatch must cover exactly one record per thread.
        Assert.Null(_executor.TryDecodeImageClear(_pipelines.Compute.Input, 127, 1, 1, 0x41));
        Assert.Null(_executor.TryDecodeImageClear(_pipelines.Compute.Input, 128, 2, 1, 0x41));
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

    private const ulong FillParameters = RecordingRenderHost.MemoryBase + 0x70_0000;
    private const uint Format32UInt = 20;

    private void ConfigureBoundedFill(uint start, uint count, uint records)
    {
        _host.WriteGuest(FillParameters, [.. BitConverter.GetBytes(start), .. BitConverter.GetBytes(count)]);
        var fill = new SharpEmu.Libs.Gpu.Pipelines.BoundedFill(
            8,
            new SharpEmu.Libs.Gpu.Pipelines.FillWord(SharpEmu.Libs.Gpu.Pipelines.FillWordSource.BufferResource, 4, 0, 4),
            new SharpEmu.Libs.Gpu.Pipelines.FillWord(SharpEmu.Libs.Gpu.Pipelines.FillWordSource.BufferResource, 4, 0, 0),
            0,
            null,
            [.. Enumerable.Range(0, 4).Select(register =>
                new SharpEmu.Libs.Gpu.Pipelines.FillWord(SharpEmu.Libs.Gpu.Pipelines.FillWordSource.UserData, (uint)register, 0, 0))],
            true);
        var program = new ShaderProgramInfo { Stage = ShaderStageKind.Compute, Hash = 0xF111, BoundedFill = fill };
        var destination = BufferDescriptor(MetadataAddress, 4, records, format: Format32UInt);
        var parameters = BufferDescriptor(FillParameters, 16, 1, format: 0);
        var input = ComputeProgram(Stage(program, userData: [.. destination, .. parameters])).Input;
        _pipelines.Compute = new ComputeProgram
        {
            Program = new ShaderProgram(0x33),
            Input = new ComputeInputInfo
            {
                ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, GroupIdX = true, ThreadIdCount = 1, WaveSize = 64,
                WorkgroupRegister = 8, Stage = input.Stage,
            },
        };
    }

    [Fact]
    public void BoundedFillOverDccMetadata_BecomesAFillAndRecordsNothing()
    {
        _host.RegisteredDcc.Add(MetadataAddress + 0x40);
        ConfigureBoundedFill(start: 0x10, count: 0x300, records: 0x400);
        _executor.Dispatch(1, Banks(), 16, 1, 1, 0x41);

        Assert.Contains($"fill_dcc {MetadataAddress + 0x40:X} C00 00000000", _host.Calls);
        AssertNotDispatched();
        Assert.Contains("reset_bindings", _host.Calls);
    }

    [Fact]
    public void BoundedFill_IsClampedToTheThreadsAndTheRecords()
    {
        _host.RegisteredDcc.Add(MetadataAddress + 0x40);
        ConfigureBoundedFill(start: 0x10, count: 0x1000, records: 0x200);
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41);

        Assert.Contains($"fill_dcc {MetadataAddress + 0x40:X} 400 00000000", _host.Calls);
        AssertNotDispatched();
    }

    [Fact]
    public void BoundedFillWithThreadDimensions_CoversExactlyTheDispatchedThreads()
    {
        _host.RegisteredDcc.Add(MetadataAddress + 0x40);
        ConfigureBoundedFill(start: 0x10, count: 0x1000, records: 0x400);
        _executor.Dispatch(1, Banks(), 0x123, 1, 1, 0x61);

        Assert.Contains($"fill_dcc {MetadataAddress + 0x40:X} 48C 00000000", _host.Calls);
        AssertNotDispatched();
    }

    private const ulong CopySource = RecordingRenderHost.MemoryBase + 0x71_0000;

    private void ConfigureBoundedCopy(uint count, uint modulus, uint sourceRecords, uint destinationRecords, uint sourceFormat = Format32UInt)
    {
        static SharpEmu.Libs.Gpu.Pipelines.FillWord User(uint register) =>
            new(SharpEmu.Libs.Gpu.Pipelines.FillWordSource.UserData, register, 0, 0);
        _host.WriteGuest(FillParameters, [.. BitConverter.GetBytes(count), .. BitConverter.GetBytes(modulus)]);
        var copy = new SharpEmu.Libs.Gpu.Pipelines.BoundedCopy(
            12,
            new SharpEmu.Libs.Gpu.Pipelines.FillWord(SharpEmu.Libs.Gpu.Pipelines.FillWordSource.BufferResource, 8, 0, 0),
            new SharpEmu.Libs.Gpu.Pipelines.FillWord(SharpEmu.Libs.Gpu.Pipelines.FillWordSource.BufferResource, 8, 0, 4),
            [User(0), User(1), User(2), User(3)],
            [User(4), User(5), User(6), User(7)]);
        var program = new ShaderProgramInfo { Stage = ShaderStageKind.Compute, Hash = 0xF333, BoundedCopy = copy };
        var source = BufferDescriptor(CopySource, 4, sourceRecords, format: sourceFormat);
        var destination = BufferDescriptor(MetadataAddress, 4, destinationRecords, format: Format32UInt);
        var parameters = BufferDescriptor(FillParameters, 16, 1, format: 0);
        var input = ComputeProgram(Stage(program, userData: [.. source, .. destination, .. parameters])).Input;
        _pipelines.Compute = new ComputeProgram
        {
            Program = new ShaderProgram(0x33),
            Input = new ComputeInputInfo
            {
                ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, GroupIdX = true, ThreadIdCount = 1, WaveSize = 64,
                WorkgroupRegister = 12, Stage = input.Stage,
            },
        };
    }

    [Fact]
    public void BoundedCopy_BecomesAHostCopyAndRecordsNothing()
    {
        ConfigureBoundedCopy(count: 0x98, modulus: 0x98, sourceRecords: 0x98, destinationRecords: 0x98);
        _executor.Dispatch(1, Banks(), 3, 1, 1, 0x41);

        Assert.Contains($"host_copy {MetadataAddress:X} {CopySource:X} 152 152", _host.Calls);
        AssertNotDispatched();
        Assert.Contains("reset_bindings", _host.Calls);
    }

    [Fact]
    public void BoundedCopy_IsClampedToTheThreadsAndTheDestinationRecords()
    {
        ConfigureBoundedCopy(count: 0x1000, modulus: 4, sourceRecords: 4, destinationRecords: 0x400);
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41);
        Assert.Contains($"host_copy {MetadataAddress:X} {CopySource:X} 4 256", _host.Calls);

        ConfigureBoundedCopy(count: 0x1000, modulus: 4, sourceRecords: 4, destinationRecords: 0x20);
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x41);
        Assert.Contains($"host_copy {MetadataAddress:X} {CopySource:X} 4 32", _host.Calls);
        AssertNotDispatched();
    }

    [Theory]
    [InlineData(0u, 4u, Format32UInt)]
    [InlineData(8u, 4u, Format32UInt)]
    [InlineData(4u, 4u, 22u)]
    public void BoundedCopyThatIsNotAPlainWordCopy_IsDispatched(uint modulus, uint sourceRecords, uint sourceFormat)
    {
        ConfigureBoundedCopy(count: 0x40, modulus: modulus, sourceRecords: sourceRecords, destinationRecords: 0x40, sourceFormat: sourceFormat);
        _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("host_copy", StringComparison.Ordinal));
        AssertDispatched(1, 1, 1);
    }

    [Fact]
    public void BoundedCopyTheHostRefuses_IsDispatched()
    {
        _host.HostCopyAccepted = false;
        ConfigureBoundedCopy(count: 0x40, modulus: 0x40, sourceRecords: 0x40, destinationRecords: 0x40);
        _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);

        Assert.Contains($"host_copy {MetadataAddress:X} {CopySource:X} 64 64", _host.Calls);
        AssertDispatched(1, 1, 1);
    }

    private void ConfigurePatternFill(uint count, uint length, uint[] pattern)
    {
        static SharpEmu.Libs.Gpu.Pipelines.FillWord User(uint register) =>
            new(SharpEmu.Libs.Gpu.Pipelines.FillWordSource.UserData, register, 0, 0);
        var fill = new SharpEmu.Libs.Gpu.Pipelines.BoundedFill(
            10, User(8), null, null, User(4), [User(0), User(1), User(2), User(3)], true, [User(4), User(5), User(6), User(7)], User(9));
        var program = new ShaderProgramInfo { Stage = ShaderStageKind.Compute, Hash = 0xF222, BoundedFill = fill };
        var destination = BufferDescriptor(MetadataAddress, 4, 0x400, format: Format32UInt);
        _pipelines.Compute = new ComputeProgram
        {
            Program = new ShaderProgram(0x33),
            Input = new ComputeInputInfo
            {
                ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, GroupIdX = true, ThreadIdCount = 1, WaveSize = 64,
                WorkgroupRegister = 10, Stage = Stage(program, userData: [.. destination, .. pattern, count, length]),
            },
        };
    }

    [Fact]
    public void SingleValuePatternFillOverDccMetadata_BecomesAFill()
    {
        _host.RegisteredDcc.Add(MetadataAddress);
        ConfigurePatternFill(count: 0x400, length: 1, pattern: [0xFFFFFFFF, 0, 0, 0]);
        _executor.Dispatch(1, Banks(), 0x400, 1, 1, 0x61);

        Assert.Contains($"fill_dcc {MetadataAddress:X} 1000 FFFFFFFF", _host.Calls);
        AssertNotDispatched();
    }

    [Fact]
    public void AVaryingPatternFill_RunsTheDispatch()
    {
        _host.RegisteredDcc.Add(MetadataAddress);
        ConfigurePatternFill(count: 0x400, length: 2, pattern: [0xFFFFFFFF, 0, 0, 0]);
        _executor.Dispatch(1, Banks(), 0x400, 1, 1, 0x61);

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("fill_dcc", StringComparison.Ordinal));
        AssertDispatched(16, 1, 1);
    }

    [Fact]
    public void BoundedFillOutsideDccMetadata_RunsTheDispatch()
    {
        ConfigureBoundedFill(start: 0x10, count: 0x300, records: 0x400);
        _executor.Dispatch(1, Banks(), 16, 1, 1, 0x41);

        Assert.Contains($"fill_dcc {MetadataAddress + 0x40:X} C00 00000000", _host.Calls);
        AssertDispatched(16, 1, 1);
    }

    [Fact]
    public void BoundedFillWithAnotherGroupRegister_RunsTheDispatch()
    {
        _host.RegisteredDcc.Add(MetadataAddress + 0x40);
        ConfigureBoundedFill(start: 0x10, count: 0x300, records: 0x400);
        _pipelines.Compute = new ComputeProgram
        {
            Program = _pipelines.Compute.Program,
            Input = new ComputeInputInfo
            {
                ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, GroupIdX = true, ThreadIdCount = 1, WaveSize = 64,
                WorkgroupRegister = 9, Stage = _pipelines.Compute.Input.Stage,
            },
        };
        _executor.Dispatch(1, Banks(), 16, 1, 1, 0x41);

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("fill_dcc", StringComparison.Ordinal));
        AssertDispatched(16, 1, 1);
    }
}
