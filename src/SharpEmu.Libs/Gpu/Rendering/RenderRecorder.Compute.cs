// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed partial class RenderRecorder
{
    private ComputeProgram? _keptComputeProgram;
    private PipelineHandle _keptComputePipeline;
    private DispatchChain? _dispatchChain;

    public void Record(ResolvedDispatch record)
    {
        try
        {
            RecordDispatch(record);
        }
        finally
        {
            _pendingWrites?.Release(record.Sequence);
            if (_returnsRecords)
            {
                _pool.Return(record);
            }
        }
    }

    private void RecordDispatch(ResolvedDispatch record)
    {
        if (!_host.IsRecording)
        {
            throw _host.Fatal("A dispatch has no recording command buffer.");
        }

        _host.RunPendingOperations();
        SetDebugInformation(in record.Debug);
        if (record.Skipped)
        {
            return;
        }

        var computeProgram = record.Program;
        if (computeProgram.ImageCopy is { } copy)
        {
            if (_host.TryCopyImage(copy.Source, copy.Destination))
            {
                _host.ResetBindings();
                return;
            }
            computeProgram = copy.Fallback();
            record.Program = computeProgram;
            if (!computeProgram.Available || computeProgram.Consumed)
                return;
        }
        var input = computeProgram.Input;
        var program = input.Stage.Program!;
        if (record.IndirectArgumentsAddress == 0)
        {
            bool consumed;
            using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchConsume))
            {
                consumed = TryConsume(record, program);
            }

            var kinds = (record.MetadataClear is not null ? 1 : 0) | (record.ConstantFill is not null ? 2 : 0) | (record.BoundedFill is not null ? 4 : 0) |
                (record.BoundedCopy is not null ? 8 : 0) | (record.ImageClear is not null ? 16 : 0);
            if (kinds != 0)
            {
                Diagnostics.DispatchRunTally.NoteConsume(program.Hash, kinds, consumed);
            }

            if (consumed)
            {
                _host.ResetBindings();
                return;
            }

            if (record.GroupsX == 0 || record.GroupsY == 0 || record.GroupsZ == 0)
            {
                return;
            }
        }

        _host.EndRendering();
        using (_host.BeginPreparation())
        {
            PipelineHandle pipeline;
            if (ReferenceEquals(computeProgram, _keptComputeProgram))
            {
                pipeline = _keptComputePipeline;
            }
            else
            {
                using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchPipeline))
                {
                    while (!_pipelines.TryCreateComputePipeline(input, computeProgram.Program, out pipeline))
                    {
                        using var waitScope = VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.ComputePipelineWait);
                        if (_pipelines.GetComputePipelineCompletion(computeProgram.Program) is { } completion)
                        {
                            completion.GetAwaiter().GetResult();
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                }

                _keptComputeProgram = computeProgram;
                _keptComputePipeline = pipeline;
            }

            var bindings = PrepareBindings(input.Stage);
            if (program.UsesDeviceAddresses)
            {
                _host.PrepareDeviceAddresses();
            }

            _host.BindResources(bindings);
            Span<IPreparedBindings> stages = [bindings];
            _host.CommitBindings(PipelineBindPoint.Compute, in pipeline, stages);
            var chain = _dispatchChain ??= new DispatchChain();
            var stage = input.Stage;
            if (chain.NeedsHazardBarrier(record, in stage, _host.DeferredBarrierPromotions))
            {
                _host.ShaderWriteHazardBarrier();
            }

            var imageSnapshot = _host.BeginComputeImageSnapshot(bindings);
            _host.BeginCheckedDispatch();
            _host.BindPipeline(PipelineBindPoint.Compute, in pipeline);
            if (record.IndirectArgumentsAddress == 0 || !_host.TryDispatchIndirect(record.IndirectArgumentsAddress, GroupAxisOrder(input)))
            {
                var (groupsX, groupsY, groupsZ) = PhysicalGroups(input, record);
                _host.Dispatch(groupsX, groupsY, groupsZ);
            }

            _host.EndCheckedDispatch();
            chain.Commit(_host.TryDeferShaderAccessBarrier(), _host.DeferredBarrierPromotions);
            _host.EndComputeImageSnapshot(imageSnapshot, bindings);
            if (record.IndirectArgumentsAddress == 0 && computeProgram.KnownWrite is { } known)
            {
                _host.NoteKnownGpuContents(known.Address, known.Words);
                known.Release();
            }
        }

        _host.ResetBindings();
    }

    private bool TryConsume(ResolvedDispatch record, ShaderProgramInfo program)
    {
        if (record.MetadataClear is { } probes &&
            TryClearMetadata(probes, record.MetadataClearHasExclusiveOr, program.Frontend is not null && program.StoresUniformValues))
        {
            return true;
        }

        if (record.ConstantFill is { } fill)
        {
            var consumed = _host.TryClearImageFromBuffer(fill.Address, fill.Size, fill.Value) ||
                           _host.TryAbsorbDccFill(fill.Address, fill.Size, fill.Value);
            if (RenderTrace.Enabled && RenderTrace.MetadataClear())
            {
                RenderTrace.Write($"Constant fill: shader=0x{program.Hash:X16} address=0x{fill.Address:X16} size=0x{fill.Size:X} value=0x{fill.Value:X8} consumed={consumed}");
            }

            if (consumed)
            {
                return true;
            }
        }

        if (record.BoundedFill is { } bounded)
        {
            var consumed = _host.TryFillDccMetadata(bounded.Address, bounded.Size, bounded.Value);
            if (RenderTrace.Enabled && RenderTrace.MetadataClear())
            {
                RenderTrace.Write($"Bounded fill: shader=0x{program.Hash:X16} address=0x{bounded.Address:X16} size=0x{bounded.Size:X} value=0x{bounded.Value:X8} consumed={consumed}");
            }

            if (consumed)
            {
                return true;
            }
        }

        if (record.BoundedCopy is { } copy)
        {
            var consumed = _host.TryCopyWordsOnHost(copy.Destination, copy.Source, copy.Modulus, copy.Words);
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write(
                    $"Bounded copy: shader=0x{program.Hash:X16} src=0x{copy.Source:X16} dst=0x{copy.Destination:X16} words={copy.Words} modulus={copy.Modulus} consumed={consumed}");
            }

            if (consumed)
            {
                record.Program.KnownWrite?.Release();
                return true;
            }

            Diagnostics.DccWriterTrace.Refuse(program.Hash, $"copy refused by the host dst=0x{copy.Destination:X} words={copy.Words} modulus={copy.Modulus}");
        }

        if (record.ImageClear is { } clear)
        {
            var address = clear.Descriptor.Address;
            if (!_host.TryClearImageFromBuffer(address, clear.Size, clear.PackedClear))
            {
                var registered = _host.TryAbsorbDccFill(address, clear.Size, clear.PackedClear);
                var filledOnHost = !registered && _host.TryFillDccMetadata(address, clear.Size, clear.PackedClear);
                if (RenderTrace.Enabled && RenderTrace.MetadataClear())
                {
                    RenderTrace.Write(
                        $"{(registered ? "Tracked" : filledOnHost ? "Filled on the host" : "Deferred")} a metadata clear: shader=0x{program.Hash:X16} address=0x{address:X16} size=0x{clear.Size:X16} value=0x{clear.PackedClear:X8}");
                }

                return registered || filledOnHost;
            }

            if (RenderTrace.Enabled && RenderTrace.ImageClear())
            {
                RenderTrace.Write($"Consumed a compute image clear: shader=0x{program.Hash:X16} address=0x{address:X16} size=0x{clear.Size:X16} value=0x{clear.PackedClear:X8}");
            }

            return true;
        }

        return false;
    }

    private bool TryClearMetadata(MetadataBufferProbe[] probes, bool hasExclusiveOr, bool readsOnlyGuardStores)
    {
        foreach (var probe in probes)
        {
            if (_host.IsMetadata(probe.Address) && (!probe.Written || (probe.Read && !readsOnlyGuardStores)))
            {
                return false;
            }
        }

        if (hasExclusiveOr)
        {
            return false;
        }

        foreach (var probe in probes)
        {
            if (probe.Written && _host.ClearMetadata(probe.Address))
            {
                return true;
            }
        }

        return false;
    }

    private static int[] GroupAxisOrder(ComputeInputInfo input) =>
        Gen5SpirvTranslator.ComputeGroupAxisOrder(input.ThreadsX, input.ThreadsY, input.ThreadsZ, input.GroupIdX, input.GroupIdY, input.GroupIdZ);

    private static (uint X, uint Y, uint Z) PhysicalGroups(ComputeInputInfo input, ResolvedDispatch record)
    {
        var axisOrder = GroupAxisOrder(input);
        Span<uint> logical = [record.GroupsX, record.GroupsY, record.GroupsZ];
        Span<uint> physical = stackalloc uint[3];
        for (var axis = 0; axis < 3; axis++)
        {
            physical[axisOrder[axis]] = logical[axis];
        }

        return (physical[0], physical[1], physical[2]);
    }
}
