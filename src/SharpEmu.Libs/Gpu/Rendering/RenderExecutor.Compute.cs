// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
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
    private const uint ImageClearStride = 16;
    private const uint ImageClearUserDataCount = 8;

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
        var program = input.Stage.Program ?? throw _host.Fatal($"The compute program is missing: shader=0x{compute.Address:X16}.");
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

        if (indirectArgumentsAddress == 0 && TryConsumeImageClear(input, groupsX, groupsY, groupsZ, dispatchInitiator))
        {
            _host.ResetBindings();
            return;
        }

        if (useThreadDimensions)
        {
            // The indirect buffer carries thread counts in this mode, while Vulkan indirect
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

        _host.EndRendering();
        using (_host.BeginPreparation())
        {
            var pipeline = _pipelines.CreateComputePipeline(input, computeProgram.Program);
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
            // The shader's local workgroup axes may have been remapped at compile time
            // (see Gen5SpirvTranslator.ComputeWorkgroupAxisOrder) so the largest NUM_THREAD
            // axis lands on a physical axis Vulkan actually allows it on. The dispatch group
            // counts must be permuted the same way, or vkCmdDispatch would hand group counts
            // for the wrong physical axis to the pipeline it built with the remapped sizes.
            var axisOrder = Gen5SpirvTranslator.ComputeWorkgroupAxisOrder(
                input.ThreadsX,
                input.ThreadsY,
                input.ThreadsZ);
            var logicalGroups = new[] { groupsX, groupsY, groupsZ };
            var physicalGroups = new uint[3];
            for (var logical = 0; logical < 3; logical++)
            {
                physicalGroups[axisOrder[logical]] = logicalGroups[logical];
            }

            if (indirectArgumentsAddress == 0 || !_host.TryDispatchIndirect(indirectArgumentsAddress))
            {
                _host.Dispatch(physicalGroups[0], physicalGroups[1], physicalGroups[2]);
            }
            _host.ShaderAccessBarrier();
        }

        _host.ResetBindings();
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
    public ComputeImageClear? TryDecodeImageClear(ComputeInputInfo input, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
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
            // A metadata fill may run before its target is bound; the store keeps it pending and the dispatch runs.
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
