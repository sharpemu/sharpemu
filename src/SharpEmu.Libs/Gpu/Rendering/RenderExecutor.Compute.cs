// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Gpu.Rendering;

public readonly record struct ComputeImageClear(BufferDescriptorWords Descriptor, uint PackedClear, ulong Size);

public sealed partial class RenderExecutor
{
    private const uint DispatchInitiatorUseThreadDimensions = 1u << 5;
    private const uint DispatchInitiatorBaseBits = 0x41;
    private const uint DispatchInitiatorModifierBits = 0xA038;
    private const uint DispatchInitiatorKnownMask = DispatchInitiatorBaseBits | DispatchInitiatorModifierBits;
    private const uint ImageClearDispatchInitiator = 0x61;
    private const uint ImageClearWaveSize = 64;
    private const uint Format32UInt = 20;
    private const uint ImageClearStride = 16;
    private const uint ImageClearUserDataCount = 8;

    private static bool SynchronizeShaderForDiagnostic(ulong hash)
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_SYNC_SHADER_HASH");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        var value = configured.AsSpan().Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return ulong.TryParse(
            value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var configuredHash) && configuredHash == hash;
    }

    private void SynchronizeShaderProbe(
        string phase,
        ulong submitId,
        ulong address,
        ulong hash,
        uint groupsX,
        uint groupsY,
        uint groupsZ)
    {
        Console.Error.WriteLine(
            $"[GPU][SYNC-PROBE] phase={phase} state=waiting submit={submitId} " +
            $"shader=0x{address:X16} hash=0x{hash:X16} groups={groupsX}x{groupsY}x{groupsZ}");
        _host.SynchronizeForDiagnostic();
        Console.Error.WriteLine(
            $"[GPU][SYNC-PROBE] phase={phase} state=complete submit={submitId} " +
            $"shader=0x{address:X16} hash=0x{hash:X16} groups={groupsX}x{groupsY}x{groupsZ}");
    }

    public void Dispatch(ulong submitId, RegisterBanks banks, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0)
    {
        if (!_host.IsRecording)
        {
            throw _host.Fatal("A dispatch has no recording command buffer.");
        }

        _host.RunPendingOperations();
        var compute = banks.Shader.Compute;
        _host.SetDebugInformation(RecordedOperation.DispatchDirect, submitId, groupsX, groupsY, groupsZ, dispatchInitiator, compute.Address);
        if (compute.Address == 0)
        {
            if (RenderTrace.Enabled && RenderTrace.NullComputeShader())
            {
                RenderTrace.Write($"Ignoring a dispatch with no compute shader: groups={groupsX}x{groupsY}x{groupsZ} initiator=0x{dispatchInitiator:X8}");
            }

            return;
        }

        var unknownBits = dispatchInitiator & ~DispatchInitiatorKnownMask;
        if (unknownBits != 0 && RenderTrace.Enabled && RenderTrace.UnknownInitiator())
        {
            RenderTrace.Write(
                $"The dispatch initiator has unknown bits: initiator=0x{dispatchInitiator:X8} unknown=0x{unknownBits:X8} shader=0x{compute.Address:X16} groups={groupsX}x{groupsY}x{groupsZ}");
        }

        var useThreadDimensions = (dispatchInitiator & DispatchInitiatorUseThreadDimensions) != 0;
        if (!_nativeIndirectDispatch)
        {
            // The guest-visible semantics resolve the three counts at packet
            // execution time. Keep that path as the safe default; a backend
            // may use native indirect dispatch only when explicitly enabled.
            ResolveDeferredIndirectDispatchCounts(
                indirectArgumentsAddress,
                ref groupsX,
                ref groupsY,
                ref groupsZ);
            indirectArgumentsAddress = 0;
        }

        var computeProgram = _pipelines.GetComputeProgram(compute, banks.Context.ShaderInterface, dispatchInitiator, groupsX, groupsY, groupsZ);
        if (computeProgram.Consumed)
        {
            return;
        }

        if (!computeProgram.Available)
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"Skipping a dispatch without a program: shader=0x{compute.Address:X16} groups={groupsX}x{groupsY}x{groupsZ}");
            }

            return;
        }

        var input = computeProgram.Input;
        var workgroupAxisMapping = input.WorkgroupAxisMapping;
        if (!workgroupAxisMapping.IsValid)
        {
            throw _host.Fatal(
                $"The compute program has an invalid workgroup-axis mapping: " +
                $"logicalX={workgroupAxisMapping.LogicalX} logicalY={workgroupAxisMapping.LogicalY} logicalZ={workgroupAxisMapping.LogicalZ}.");
        }

        var remapWorkgroupAxes = !workgroupAxisMapping.IsIdentity;
        if (remapWorkgroupAxes)
        {
            // A native indirect buffer still stores logical X/Y/Z counts. The
            // backend-specific shader mapping needs those counts on the CPU so
            // the direct dispatch can apply the same permutation.
            ResolveDeferredIndirectDispatchCounts(
                indirectArgumentsAddress,
                ref groupsX,
                ref groupsY,
                ref groupsZ);
            indirectArgumentsAddress = 0;
        }

        var program = input.Stage.Program ?? throw _host.Fatal($"The compute program is missing: shader=0x{compute.Address:X16}.");
        if (Diagnostics.DccWriterTrace.Enabled)
        {
            Diagnostics.DccWriterTrace.CurrentProgram = program.Hash;
        }

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(
                $"Dispatch seq={RenderTrace.NextSequence()} submit={submitId} shader=0x{compute.Address:X16} hash=0x{program.Hash:X16} " +
                $"groups={groupsX}x{groupsY}x{groupsZ} local={input.ThreadsX}x{input.ThreadsY}x{input.ThreadsZ} wave={input.WaveSize} " +
                $"localDataShareDwords={input.LocalDataShareDwords} barriers={input.NeedsLocalDataShareBarriers} initiator=0x{dispatchInitiator:X8} " +
                $"buffers={program.Buffers.Length} images={program.Images.Length} samplers={program.SamplerCount}");
        }

        if (indirectArgumentsAddress == 0 && TryConsumeMetadataClear(input))
        {
            _host.ResetBindings();
            return;
        }

        if (indirectArgumentsAddress == 0 && TryConsumeConstantFill(input, groupsX, groupsY, groupsZ))
        {
            _host.ResetBindings();
            return;
        }

        if (indirectArgumentsAddress == 0 && TryConsumeBoundedFill(input, groupsX, groupsY, groupsZ, dispatchInitiator))
        {
            _host.ResetBindings();
            return;
        }

        if (indirectArgumentsAddress == 0 && TryConsumeBoundedCopy(input, groupsX, groupsY, groupsZ, dispatchInitiator))
        {
            _host.ResetBindings();
            return;
        }

        if (indirectArgumentsAddress == 0 && TryConsumeImageClear(input, groupsX, groupsY, groupsZ, dispatchInitiator))
        {
            _host.ResetBindings();
            return;
        }

        if (useThreadDimensions)
        {
            // Indirect arguments carry thread counts in this mode, while Vulkan indirect
            // dispatch consumes workgroup counts. Use the CPU-resolved counts after conversion.
            indirectArgumentsAddress = 0;
            var threadsX = groupsX;
            var threadsY = groupsY;
            var threadsZ = groupsZ;
            groupsX = GroupsFromThreads(threadsX, compute.ThreadsX);
            groupsY = GroupsFromThreads(threadsY, compute.ThreadsY);
            groupsZ = GroupsFromThreads(threadsZ, compute.ThreadsZ);
            if (RenderTrace.Enabled && RenderTrace.ThreadDimensionConversion())
            {
                RenderTrace.Write(
                    $"Converted thread dimensions to groups: threads={threadsX}x{threadsY}x{threadsZ} " +
                    $"local={Math.Max(compute.ThreadsX, 1)}x{Math.Max(compute.ThreadsY, 1)}x{Math.Max(compute.ThreadsZ, 1)} groups={groupsX}x{groupsY}x{groupsZ}");
            }
        }

        if (indirectArgumentsAddress == 0 && (groupsX == 0 || groupsY == 0 || groupsZ == 0))
        {
            if (RenderTrace.Enabled && RenderTrace.ZeroDispatch())
            {
                RenderTrace.Write($"Skipping a zero-sized dispatch: groups={groupsX}x{groupsY}x{groupsZ} initiator=0x{dispatchInitiator:X8} shader=0x{compute.Address:X16}");
            }

            return;
        }

        var synchronizeProbe = SynchronizeShaderForDiagnostic(program.Hash);
        if (synchronizeProbe)
        {
            SynchronizeShaderProbe("before", submitId, compute.Address, program.Hash, groupsX, groupsY, groupsZ);
        }

        _host.EndRendering();
        using (_host.BeginPreparation())
        {
            // The host may compile this program off the command-stream thread and report that
            // the pipeline is not ready yet. The dispatch waits for it rather than being
            // dropped: a guest that does not replay a one-shot dispatch deadlocks, because
            // the dispatch can feed a label a later packet waits on. Asking again keeps the
            // wait outside the pipeline cache's lock, so other queues can create their own
            // pipelines while this program compiles.
            PipelineHandle pipeline;
            while (!_pipelines.TryCreateComputePipeline(input, computeProgram.Program, out pipeline))
            {
                Thread.Sleep(1);
            }

            var bindings = _host.PrepareBindings(input.Stage);
            if (program.UsesDeviceAddresses)
            {
                _host.PrepareDeviceAddresses();
            }

            _host.BindResources(bindings);
            Span<IPreparedBindings> stages = [bindings];
            _host.CommitBindings(PipelineBindPoint.Compute, in pipeline, stages);
            var hasStorageWrites = HasBufferWrites(input.Stage);
            foreach (var image in program.Images)
            {
                hasStorageWrites |= image.Written && image.Class == ImageResourceClass.Storage;
            }

            if (hasStorageWrites)
            {
                // Every earlier read of the written resources completes before this dispatch writes.
                _host.ShaderWriteHazardBarrier();
            }

            _host.BindPipeline(PipelineBindPoint.Compute, in pipeline);
            var dispatchedIndirect =
                indirectArgumentsAddress != 0 &&
                _host.TryDispatchIndirect(indirectArgumentsAddress);
            if (!dispatchedIndirect)
            {
                // A backend may decline native indirect dispatch after the
                // interpreter deferred the counts. Recover them before using
                // the direct fallback instead of recording a zero dispatch.
                ResolveDeferredIndirectDispatchCounts(
                    indirectArgumentsAddress,
                    ref groupsX,
                    ref groupsY,
                    ref groupsZ);
                if (groupsX != 0 && groupsY != 0 && groupsZ != 0 && remapWorkgroupAxes)
                {
                    Span<uint> logicalGroups = [groupsX, groupsY, groupsZ];
                    Span<uint> physicalGroups = stackalloc uint[3];
                    for (var logical = 0; logical < 3; logical++)
                    {
                        physicalGroups[workgroupAxisMapping.PhysicalAxisOfLogical(logical)] = logicalGroups[logical];
                    }

                    _host.Dispatch(
                        physicalGroups[0],
                        physicalGroups[1],
                        physicalGroups[2]);
                }
                else if (groupsX != 0 && groupsY != 0 && groupsZ != 0)
                {
                    _host.Dispatch(groupsX, groupsY, groupsZ);
                }
            }
            _host.ShaderAccessBarrier();
        }

        _host.ResetBindings();
        if (synchronizeProbe)
        {
            SynchronizeShaderProbe("after", submitId, compute.Address, program.Hash, groupsX, groupsY, groupsZ);
        }
    }

    // The interpreter uses zero XYZ plus a nonzero address when the backend said
    // it could leave workgroup counts on the GPU. A real all-zero indirect dispatch
    // is indistinguishable but harmless: reading it again yields the same no-op.
    private void ResolveDeferredIndirectDispatchCounts(
        ulong argumentsAddress,
        ref uint groupsX,
        ref uint groupsY,
        ref uint groupsZ)
    {
        if (argumentsAddress == 0 || groupsX != 0 || groupsY != 0 || groupsZ != 0)
        {
            return;
        }

        Span<byte> arguments = stackalloc byte[3 * sizeof(uint)];
        if (!_host.TryReadGuest(argumentsAddress, arguments))
        {
            throw _host.Fatal($"The indirect dispatch arguments are unreadable: address=0x{argumentsAddress:X16}.");
        }

        groupsX = BinaryPrimitives.ReadUInt32LittleEndian(arguments);
        groupsY = BinaryPrimitives.ReadUInt32LittleEndian(arguments[sizeof(uint)..]);
        groupsZ = BinaryPrimitives.ReadUInt32LittleEndian(arguments[(2 * sizeof(uint))..]);
    }

    // The dispatch counts threads; the host counts groups of the shader's thread size.
    public static uint GroupsFromThreads(uint threads, uint groupSize)
    {
        if (threads == 0)
        {
            return 0;
        }

        var size = Math.Max(groupSize, 1u);
        return (threads + size - 1) / size;
    }

    private BufferDescriptorWords DecodeBufferDescriptor(ResourceSnapshot resources, int index)
    {
        var words = resources.Buffers[index];
        if (words.Length < 4)
        {
            throw _host.Fatal($"A buffer descriptor is too short: index={index} words={words.Length}.");
        }

        return BufferDescriptorWords.From(words);
    }

    // A full overwrite of registered metadata by a compute shader becomes a tracked clear.
    private bool TryConsumeMetadataClear(ComputeInputInfo input)
    {
        var program = input.Stage.Program!;
        var resources = input.Stage.Resources;
        if (resources.Buffers.Length != program.Buffers.Length)
        {
            throw _host.Fatal($"The compute buffer count does not match the program: descriptors={resources.Buffers.Length} program={program.Buffers.Length}.");
        }

        for (var i = 0; i < program.Buffers.Length; i++)
        {
            var resource = program.Buffers[i];
            var descriptor = DecodeBufferDescriptor(resources, i);
            // Metadata that is also read is not a proven overwrite; the dispatch runs as written.
            if (_host.IsMetadata(descriptor.Address) && (!resource.Written || resource.Read))
            {
                return false;
            }
        }

        if (program.HasBitwiseExclusiveOr)
        {
            return false;
        }

        for (var i = 0; i < program.Buffers.Length; i++)
        {
            if (!program.Buffers[i].Written)
            {
                continue;
            }

            var descriptor = DecodeBufferDescriptor(resources, i);
            if (_host.ClearMetadata(descriptor.Address))
            {
                return true;
            }
        }

        return false;
    }

    // Recognizes a dispatch that fills one formatted buffer with a single value over every record.
    public ComputeImageClear? TryDecodeImageClear(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator) =>
        TryDecodeUserDataFill(input, groupsX, groupsY, groupsZ, dispatchInitiator) ??
        TryDecodeLoadedValueFill(input, groupsX, groupsY, groupsZ) ??
        TryDecodeConstantFill(input, groupsX, groupsY, groupsZ);

    // AGC's constant fill kernel: one formatted dword buffer written at every thread index with a
    // value the program moves from a constant (a DCC or CMask clear code).
    private ComputeImageClear? TryDecodeConstantFill(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ)
    {
        var program = input.Stage.Program ?? throw _host.Fatal("The compute stage has no program.");
        var resources = input.Stage.Resources;
        if (program.ConstantStoreValue is not { } value ||
            program.Buffers.Length != 1 || resources.Buffers.Length != 1 || resources.Buffers[0].Length != 4 ||
            program.Images.Length != 0 || program.SamplerCount != 0 || program.UsesDeviceAddresses)
        {
            return null;
        }

        var info = program.Buffers[0];
        var descriptor = BufferDescriptorWords.From(resources.Buffers[0]);
        var threads = (ulong)groupsX * input.ThreadsX;
        if (!info.Formatted || !info.Written || info.Read || info.Atomic || info.Scalar || info.MaxByteExtent != sizeof(uint) ||
            descriptor.Stride != sizeof(uint) || descriptor.Format != BufferDescriptorWords.Format32UInt || descriptor.SwizzleEnabled ||
            descriptor.IndexStride != 0 || descriptor.AddThreadId || descriptor.RecordCount == 0 ||
            input.ThreadsX != ImageClearWaveSize || input.ThreadsY != 1 || input.ThreadsZ != 1 || input.WaveSize != ImageClearWaveSize ||
            groupsX == 0 || groupsY != 1 || groupsZ != 1 || threads != descriptor.RecordCount ||
            !input.GroupIdX || input.GroupIdY || input.GroupIdZ || input.ThreadIdCount != 1 ||
            (input.DispatchThreadDimensions && input.DispatchThreadsX != threads))
        {
            return null;
        }

        var size = descriptor.Footprint() ?? throw _host.Fatal($"The compute buffer footprint overflows: stride={descriptor.Stride} records={descriptor.RecordCount}.");
        return new ComputeImageClear(descriptor, value, size);
    }

    // The fill shader AGC titles use for DCC and CMask clears: each thread stores one dword it loaded
    // with s_buffer_load_dword from a one-record constant buffer, at its global thread index.
    private ComputeImageClear? TryDecodeLoadedValueFill(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ)
    {
        var program = input.Stage.Program ?? throw _host.Fatal("The compute stage has no program.");
        var resources = input.Stage.Resources;
        if (program.Buffers.Length != 2 || resources.Buffers.Length != 2 || program.Images.Length != 0 || program.SamplerCount != 0 ||
            program.UsesDeviceAddresses || resources.Images.Length != 0 || resources.Samplers.Length != 0 ||
            resources.Buffers[0].Length != 4 || resources.Buffers[1].Length != 4)
        {
            return null;
        }

        int source = -1, target = -1;
        for (var index = 0; index < 2; index++)
        {
            var info = program.Buffers[index];
            if (info.Scalar && info.Read && !info.Written && !info.Atomic && info.MaxByteExtent == sizeof(uint))
            {
                source = index;
            }
            else if (info.Formatted && info.Written && !info.Read && !info.Atomic && !info.Scalar && info.MaxByteExtent == sizeof(uint))
            {
                target = index;
            }
        }

        if (source < 0 || target < 0)
        {
            return null;
        }

        var descriptor = BufferDescriptorWords.From(resources.Buffers[target]);
        var valueDescriptor = BufferDescriptorWords.From(resources.Buffers[source]);
        var threads = (ulong)groupsX * input.ThreadsX;
        if (descriptor.Stride != sizeof(uint) || descriptor.Format != BufferDescriptorWords.Format32UInt || descriptor.SwizzleEnabled ||
            descriptor.IndexStride != 0 || descriptor.AddThreadId || descriptor.RecordCount == 0 ||
            input.ThreadsX != ImageClearWaveSize || input.ThreadsY != 1 || input.ThreadsZ != 1 || input.WaveSize != ImageClearWaveSize ||
            groupsX == 0 || groupsY != 1 || groupsZ != 1 || threads != descriptor.RecordCount ||
            !input.GroupIdX || input.GroupIdY || input.GroupIdZ || input.ThreadIdCount != 1 ||
            (input.DispatchThreadDimensions && input.DispatchThreadsX != threads) ||
            valueDescriptor.Address == 0)
        {
            return null;
        }

        Span<byte> value = stackalloc byte[sizeof(uint)];
        if (!_host.TryReadGuest(valueDescriptor.Address, value))
        {
            return null;
        }

        var size = descriptor.Footprint() ?? throw _host.Fatal($"The compute buffer footprint overflows: stride={descriptor.Stride} records={descriptor.RecordCount}.");
        return new ComputeImageClear(descriptor, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value), size);
    }

    private ComputeImageClear? TryDecodeUserDataFill(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
    {
        var program = input.Stage.Program ?? throw _host.Fatal("The compute stage has no program.");
        var resources = input.Stage.Resources;
        if (program.Buffers.Length != 1 || resources.Buffers.Length != 1 || program.Images.Length != 0 || program.SamplerCount != 0 ||
            program.UsesDeviceAddresses || resources.Images.Length != 0 || resources.Samplers.Length != 0)
        {
            return null;
        }

        var resource = program.Buffers[0];
        var words = resources.Buffers[0];
        if (words.Length != 4)
        {
            return null;
        }

        var descriptor = BufferDescriptorWords.From(words);
        if (!resource.Formatted || !resource.Written || resource.Read || resource.Atomic || resource.Scalar || resource.MaxByteExtent != ImageClearStride ||
            descriptor.Stride != ImageClearStride || descriptor.Format != BufferDescriptorWords.Format32x4UInt || descriptor.SwizzleEnabled ||
            descriptor.IndexStride != 0 || descriptor.AddThreadId || resource.PackedStride != descriptor.PackedStride ||
            program.UserDataBase != 0 || resources.UserData.Length != ImageClearUserDataCount)
        {
            return null;
        }

        for (var i = 0; i < words.Length; i++)
        {
            if (words[i] != resources.UserData[i])
            {
                return null;
            }
        }

        var clear = resources.UserData[4];
        if (resources.UserData[5] != clear || resources.UserData[6] != clear || resources.UserData[7] != clear)
        {
            return null;
        }

        var fullDispatch =
            input.DispatchThreadDimensions && input.ThreadsX == ImageClearWaveSize && input.ThreadsY == 1 && input.ThreadsZ == 1 &&
            groupsX != 0 && groupsY == 1 && groupsZ == 1 &&
            input.DispatchThreadsX == groupsX && input.DispatchThreadsY == 1 && input.DispatchThreadsZ == 1 &&
            input.GroupIdX && !input.GroupIdY && !input.GroupIdZ &&
            input.ThreadIdCount == 1 && input.WaveSize == ImageClearWaveSize && !input.ThreadGroupSizeEnabled &&
            dispatchInitiator == ImageClearDispatchInitiator && groupsX % input.ThreadsX == 0 && descriptor.RecordCount == groupsX;
        var size = descriptor.Footprint() ?? throw _host.Fatal($"The compute buffer footprint overflows: stride={descriptor.Stride} records={descriptor.RecordCount}.");
        if (!fullDispatch || size == 0)
        {
            return null;
        }

        return new ComputeImageClear(descriptor, clear, size);
    }

    // A constant fill that covers a whole image or DCC metadata becomes a clear. Anything
    // else, including a fill of plain buffer memory, runs as the guest wrote it.
    private bool TryConsumeConstantFill(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ)
    {
        var program = input.Stage.Program!;
        if (program.ConstantFill is not { } fill || program.UserDataBase != 0)
        {
            return false;
        }

        var userData = input.Stage.Resources.UserData;
        if (fill.GroupScalarRegister != (uint)input.WorkgroupRegister || !input.GroupIdX || input.GroupIdY || input.GroupIdZ ||
            input.ThreadsX != ImageClearWaveSize || input.ThreadsY != 1 || input.ThreadsZ != 1 || groupsY != 1 || groupsZ != 1 ||
            fill.DestinationScalarResource + 4 > userData.Length || fill.SourceScalarResource + 4 > userData.Length)
        {
            return false;
        }

        var destination = BufferDescriptorWords.From(userData.AsSpan((int)fill.DestinationScalarResource, 4).ToArray());
        var source = BufferDescriptorWords.From(userData.AsSpan((int)fill.SourceScalarResource, 4).ToArray());
        Span<byte> valueBytes = stackalloc byte[sizeof(uint)];
        if (destination.Format != Format32UInt || destination.Stride != sizeof(uint) || destination.SwizzleEnabled || destination.AddThreadId ||
            (ulong)groupsX * input.ThreadsX != destination.RecordCount || source.RecordCount == 0 ||
            !_host.TryReadGuest(source.Address, valueBytes))
        {
            return false;
        }

        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(valueBytes);
        var size = (ulong)destination.RecordCount * sizeof(uint);
        var consumed = _host.TryClearImageFromBuffer(destination.Address, size, value) ||
                       _host.TryAbsorbDccFill(destination.Address, size, value);
        if (RenderTrace.Enabled && RenderTrace.MetadataClear())
        {
            RenderTrace.Write($"Constant fill: shader=0x{program.Hash:X16} address=0x{destination.Address:X16} size=0x{size:X} value=0x{value:X8} consumed={consumed}");
        }

        return consumed;
    }

    private const uint Format32SInt = 21;
    private const uint Format32Float = 22;
    private const ulong DescriptorAddressMask = 0x0000_FFFF_FFFF_FFFFul;

    private bool TryConsumeBoundedFill(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
    {
        var program = input.Stage.Program!;
        if (program.BoundedFill is not { } fill)
        {
            return false;
        }

        if (program.UserDataBase != 0 || fill.GroupScalarRegister != (uint)input.WorkgroupRegister || !input.GroupIdX || input.GroupIdY || input.GroupIdZ ||
            input.ThreadIdCount < 1 || input.ThreadsX != ImageClearWaveSize || input.ThreadsY != 1 || input.ThreadsZ != 1 ||
            groupsX == 0 || groupsY != 1 || groupsZ != 1)
        {
            return RefuseBoundedFill(program,
                $"shape group=s{fill.GroupScalarRegister}/s{input.WorkgroupRegister} ids={input.GroupIdX}{input.GroupIdY}{input.GroupIdZ} tid={input.ThreadIdCount} " +
                $"local={input.ThreadsX}x{input.ThreadsY}x{input.ThreadsZ} dispatch={groupsX}x{groupsY}x{groupsZ} base={program.UserDataBase}");
        }

        var userData = input.Stage.Resources.UserData;
        Span<uint> destinationWords = stackalloc uint[4];
        for (var word = 0; word < destinationWords.Length; word++)
        {
            if (!TryResolveFillWord(fill.Destination[word], userData, out destinationWords[word]))
            {
                return RefuseBoundedFill(program, $"destination word {word} unreadable");
            }
        }

        var destination = BufferDescriptorWords.From(destinationWords);
        uint start = 0;
        uint value;
        if (!TryResolveFillWord(fill.Count, userData, out var count) ||
            (fill.Start is { } startWord && !TryResolveFillWord(startWord, userData, out start)))
        {
            return RefuseBoundedFill(program, "range unreadable");
        }

        if (fill.ConstantValue is { } constant)
        {
            value = constant;
        }
        else if (fill.Value is not { } valueWord || !TryResolveFillWord(valueWord, userData, out value))
        {
            return RefuseBoundedFill(program, "value unreadable");
        }

        if (fill.PatternLength is { } lengthWord)
        {
            if (fill.Pattern is not { } pattern || !TryResolveFillWord(lengthWord, userData, out var length) || length == 0 || length > pattern.Length)
            {
                return RefuseBoundedFill(program, "pattern length");
            }

            for (var word = 1; word < length; word++)
            {
                if (!TryResolveFillWord(pattern[word], userData, out var repeated) || repeated != value)
                {
                    return RefuseBoundedFill(program, $"pattern length={length} varies");
                }
            }
        }

        if (destination.Stride != sizeof(uint) || destination.SwizzleEnabled || destination.AddThreadId || destination.OutOfBounds != 0 ||
            destination.Type != 0 || (destination.Address & 3) != 0 ||
            (fill.Formatted && destination.Format is not (Format32UInt or Format32SInt or Format32Float)))
        {
            return RefuseBoundedFill(program,
                $"descriptor stride={destination.Stride} swizzle={destination.SwizzleEnabled} tid={destination.AddThreadId} oob={destination.OutOfBounds} " +
                $"type={destination.Type} format={destination.Format} address=0x{destination.Address:X}");
        }

        var threads = (dispatchInitiator & DispatchInitiatorUseThreadDimensions) != 0 ? groupsX : (ulong)groupsX * input.ThreadsX;
        var written = Math.Min(count, threads);
        if (threads > uint.MaxValue || (ulong)start + written > uint.MaxValue)
        {
            return RefuseBoundedFill(program, $"overflow threads={threads} start={start} count={count}");
        }

        var end = Math.Min((ulong)start + written, destination.RecordCount);
        if (end <= start)
        {
            return false;
        }

        var address = destination.Address + (ulong)start * sizeof(uint);
        var size = (end - start) * sizeof(uint);
        var consumed = _host.TryFillDccMetadata(address, size, value);
        if (RenderTrace.Enabled && RenderTrace.MetadataClear())
        {
            RenderTrace.Write($"Bounded fill: shader=0x{program.Hash:X16} address=0x{address:X16} size=0x{size:X} value=0x{value:X8} consumed={consumed}");
        }

        return consumed;
    }

    private bool TryConsumeBoundedCopy(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
    {
        var program = input.Stage.Program!;
        if (program.BoundedCopy is not { } copy || !BoundedCopyEnabled)
        {
            return false;
        }

        if (program.UserDataBase != 0 || copy.GroupScalarRegister != (uint)input.WorkgroupRegister || !input.GroupIdX || input.GroupIdY || input.GroupIdZ ||
            input.ThreadIdCount < 1 || input.ThreadsX != ImageClearWaveSize || input.ThreadsY != 1 || input.ThreadsZ != 1 ||
            groupsX == 0 || groupsY != 1 || groupsZ != 1)
        {
            return RefuseBoundedFill(program, $"copy shape dispatch={groupsX}x{groupsY}x{groupsZ} local={input.ThreadsX}x{input.ThreadsY}x{input.ThreadsZ}");
        }

        var userData = input.Stage.Resources.UserData;
        Span<uint> sourceWords = stackalloc uint[4];
        Span<uint> destinationWords = stackalloc uint[4];
        for (var word = 0; word < 4; word++)
        {
            if (!TryResolveFillWord(copy.Source[word], userData, out sourceWords[word]) ||
                !TryResolveFillWord(copy.Destination[word], userData, out destinationWords[word]))
            {
                return RefuseBoundedFill(program, $"copy descriptor word {word} unreadable");
            }
        }

        if (!TryResolveFillWord(copy.Count, userData, out var count) || !TryResolveFillWord(copy.Modulus, userData, out var modulus))
        {
            return RefuseBoundedFill(program, "copy range unreadable");
        }

        var source = BufferDescriptorWords.From(sourceWords);
        var destination = BufferDescriptorWords.From(destinationWords);
        if (!IsPlainWordBuffer(source) || !IsPlainWordBuffer(destination) || source.Format != destination.Format ||
            modulus == 0 || modulus > source.RecordCount)
        {
            return RefuseBoundedFill(program,
                $"copy src=0x{source.Address:X}/{source.Format}/{source.Stride}/{source.OutOfBounds} dst=0x{destination.Address:X}/{destination.Format}/{destination.Stride}/{destination.OutOfBounds} " +
                $"modulus={modulus} records={source.RecordCount}");
        }

        var threads = (dispatchInitiator & DispatchInitiatorUseThreadDimensions) != 0 ? groupsX : (ulong)groupsX * input.ThreadsX;
        var words = Math.Min(Math.Min((ulong)count, threads), destination.RecordCount);
        if (words == 0)
        {
            return false;
        }

        var consumed = _host.TryCopyWordsOnHost(destination.Address, source.Address, modulus, words);
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(
                $"Bounded copy: shader=0x{program.Hash:X16} src=0x{source.Address:X16} dst=0x{destination.Address:X16} words={words} modulus={modulus} consumed={consumed}");
        }

        return consumed || RefuseBoundedFill(program, $"copy refused by the host dst=0x{destination.Address:X} words={words} modulus={modulus}");
    }

    private static readonly bool BoundedCopyEnabled = Environment.GetEnvironmentVariable("SHARPEMU_HOST_BOUNDED_COPY") != "0";

    private static bool IsPlainWordBuffer(BufferDescriptorWords descriptor) =>
        descriptor.Stride == sizeof(uint) && !descriptor.SwizzleEnabled && !descriptor.AddThreadId && descriptor.OutOfBounds == 0 &&
        descriptor.Type == 0 && (descriptor.Address & 3) == 0 && descriptor.Format is Format32UInt or Format32SInt or Format32Float;

    private static bool RefuseBoundedFill(ShaderProgramInfo program, string reason)
    {
        Diagnostics.DccWriterTrace.Refuse(program.Hash, reason);
        return false;
    }

    private bool TryResolveFillWord(Pipelines.FillWord word, ReadOnlySpan<uint> userData, out uint value)
    {
        value = 0;
        switch (word.Source)
        {
            case Pipelines.FillWordSource.UserData:
                if (word.Register >= userData.Length)
                {
                    return false;
                }

                value = userData[(int)word.Register];
                return true;
            case Pipelines.FillWordSource.BufferResource:
            {
                if (word.Register + 4 > userData.Length)
                {
                    return false;
                }

                var resource = BufferDescriptorWords.From(userData.Slice((int)word.Register, 4));
                var size = resource.Stride == 0 ? resource.RecordCount : (ulong)resource.Stride * resource.RecordCount;
                var offset = (ulong)word.Offset & ~3ul;
                if (offset > size || size - offset < sizeof(uint))
                {
                    return true;
                }

                return TryReadFillWord((resource.Address & ~3ul) + offset, out value);
            }
            case Pipelines.FillWordSource.Pointer:
                return TryReadPointer(word.Register, userData, out var pointer) && TryReadFillWord(pointer + (ulong)word.Offset, out value);
            case Pipelines.FillWordSource.IndirectPointer:
            {
                if (!TryReadPointer(word.Register, userData, out var outer) ||
                    !TryReadFillWord(outer + (ulong)word.PointerOffset, out var low) ||
                    !TryReadFillWord(outer + (ulong)word.PointerOffset + sizeof(uint), out var high))
                {
                    return false;
                }

                var inner = ((low | ((ulong)high << 32)) & DescriptorAddressMask) & ~3ul;
                return TryReadFillWord(inner + (ulong)word.Offset, out value);
            }
            default:
                return false;
        }
    }

    private static bool TryReadPointer(uint register, ReadOnlySpan<uint> userData, out ulong pointer)
    {
        pointer = 0;
        if (register + 2 > userData.Length)
        {
            return false;
        }

        pointer = ((userData[(int)register] | ((ulong)userData[(int)register + 1] << 32)) & DescriptorAddressMask) & ~3ul;
        return pointer != 0;
    }

    private bool TryReadFillWord(ulong address, out uint value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (address == 0 || !_host.TryReadGuest(address, bytes))
        {
            return false;
        }

        value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private bool TryConsumeImageClear(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
    {
        if (TryDecodeImageClear(input, groupsX, groupsY, groupsZ, dispatchInitiator) is not { } clear)
        {
            return false;
        }

        var address = clear.Descriptor.Address;
        var hash = input.Stage.Program!.Hash;
        if (!_host.TryClearImageFromBuffer(address, clear.Size, clear.PackedClear))
        {
            // DCC backing is authoritative. Unless the host performed an equivalent tracked
            // write, keep the guest dispatch so later materialization can inspect its bytes.
            var registered = _host.TryAbsorbDccFill(address, clear.Size, clear.PackedClear);
            if (RenderTrace.Enabled && RenderTrace.MetadataClear())
            {
                RenderTrace.Write(
                    $"{(registered ? "Tracked" : "Deferred")} a metadata clear: shader=0x{hash:X16} address=0x{address:X16} size=0x{clear.Size:X16} value=0x{clear.PackedClear:X8}");
            }

            return registered;
        }

        if (RenderTrace.Enabled && RenderTrace.ImageClear())
        {
            RenderTrace.Write($"Consumed a compute image clear: shader=0x{hash:X16} address=0x{address:X16} size=0x{clear.Size:X16} value=0x{clear.PackedClear:X8}");
        }

        return true;
    }
}
