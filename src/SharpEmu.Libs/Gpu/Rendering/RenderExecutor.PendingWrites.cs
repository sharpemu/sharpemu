// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    public PendingGpuWrites PendingWrites { get; }

    private void Record(ResolvedDraw record)
    {
        record.Sequence = _recorder.NextSequence();
        if (record.Sequence != 0)
        {
            MarkPendingWrites(record);
        }

        _recorder.Record(record);
    }

    private void Record(ResolvedDispatch record)
    {
        record.Sequence = _recorder.NextSequence();
        if (record.Sequence != 0)
        {
            MarkPendingWrites(record);
        }

        _recorder.Record(record);
    }

    private void MarkPendingWrites(ResolvedDraw record)
    {
        var sequence = record.Sequence;
        switch (record.Kind)
        {
            case ResolvedDrawKind.Draw:
            case ResolvedDrawKind.Tessellation:
                MarkTargets(record, sequence);
                MarkStageWrites(record.State.Programs.VertexInput.Stage, sequence);
                if (record.State.Programs.Tessellation is { } tessellation)
                {
                    MarkStageWrites(tessellation.Hull.Input.Stage, sequence);
                    if (tessellation.Hull.Input.Stage.Program?.Frontend?.TessellationLocalDataRing is { } ring)
                        PendingWrites.Mark(ring.Address, ring.Bytes, sequence, PendingWriteKind.Buffer);
                }
                if (record.State.PixelActive)
                {
                    MarkStageWrites(record.State.Programs.PixelInput.Stage, sequence);
                }

                break;
            case ResolvedDrawKind.ClearColorTargets:
                MarkTargets(record, sequence);
                break;
            case ResolvedDrawKind.MultisampleResolve:
                Mark(record.ResolveDestination.Resolution.Request.Description.Data, sequence, PendingWriteKind.Target);
                break;
            case ResolvedDrawKind.DepthStencilCopy:
                Mark(record.CopyWrite.Description.Data, sequence, PendingWriteKind.Target);
                break;
        }
    }

    private void MarkPendingWrites(ResolvedDispatch record) => MarkDispatchWrites(record, record.Sequence, null);

    // The marks a dispatch record would make, collected for a deferred dispatch's finalization.
    internal void CollectDispatchWrites(ResolvedDispatch record, List<PendingMark> into) => MarkDispatchWrites(record, 0, into);

    private void MarkDispatchWrites(ResolvedDispatch record, long sequence, List<PendingMark>? into)
    {
        if (record.Skipped)
        {
            return;
        }

        if (record.Program.ImageCopy is { } imageCopy)
        {
            MarkRange(imageCopy.Destination.Description.Data.Address, imageCopy.Destination.Description.Data.Size, sequence, PendingWriteKind.Target, into);
            return;
        }
        MarkStageWrites(record.Program.Input.Stage, sequence, into);
        if (record.ConstantFill is { } fill)
        {
            MarkRange(fill.Address, fill.Size, sequence, PendingWriteKind.Fill, into);
        }

        if (record.BoundedFill is { } bounded)
        {
            MarkRange(bounded.Address, bounded.Size, sequence, PendingWriteKind.Fill, into);
        }

        if (record.BoundedCopy is { } copy)
        {
            MarkRange(copy.Destination, copy.Words * sizeof(uint), sequence, PendingWriteKind.Copy, into);
        }

        if (record.ImageClear is { } clear)
        {
            MarkRange(clear.Descriptor.Address, clear.Size, sequence, PendingWriteKind.Fill, into);
        }
    }

    private void MarkRange(ulong address, ulong size, long sequence, PendingWriteKind kind, List<PendingMark>? into)
    {
        if (into is null)
        {
            PendingWrites.Mark(address, size, sequence, kind);
        }
        else if (size != 0)
        {
            into.Add(new PendingMark(address, size, kind));
        }
    }

    private void MarkTargets(ResolvedDraw record, long sequence)
    {
        ref var state = ref record.State;
        for (var index = 0; index < state.ColorCount; index++)
        {
            Mark(state.Colors[index].Resolution.Request.Description.Data, sequence, PendingWriteKind.Target);
        }

        if (state.Depth.HasTarget)
        {
            Mark(state.Depth.Target.Target.Request.Description.Data, sequence, PendingWriteKind.Target);
        }
    }

    private void Mark(in GuestSpan data, long sequence, PendingWriteKind kind) => PendingWrites.Mark(data.Address, data.Size, sequence, kind);

    private void MarkStageWrites(in ShaderStageResources stage, long sequence, List<PendingMark>? into = null)
    {
        if (stage.Program is not { } program)
        {
            return;
        }

        var resources = stage.Resources;
        var buffers = resources.Buffers;
        for (var index = 0; index < program.Buffers.Length && index < buffers.Length; index++)
        {
            if (!program.Buffers[index].Written || buffers[index].Length < 4)
            {
                continue;
            }

            var descriptor = BufferDescriptorWords.From(buffers[index]);
            if (descriptor.Address != 0 && descriptor.Footprint() is { } footprint)
            {
                MarkRange(descriptor.Address, footprint, sequence, PendingWriteKind.Buffer, into);
            }
        }

        var images = resources.Images;
        var shapes = program.Resources?.Info.Images;
        for (var index = 0; index < program.Images.Length && index < images.Length; index++)
        {
            var image = program.Images[index];
            if (!image.Written || image.Class != ImageResourceClass.Storage || images[index].Length < 4)
            {
                continue;
            }

            if (shapes is not null && index < shapes.Count && TryGetImageData(program, shapes[index], images[index], index, out var data))
            {
                MarkRange(data.Address, data.Size, sequence, PendingWriteKind.Image, into);
            }
        }

        foreach (var range in resources.DeviceAddressRanges)
        {
            if (range.Written)
            {
                MarkRange(range.Base, range.Size, sequence, PendingWriteKind.Range, into);
            }
        }

        // The buffers the stage's call records name for their callees (ShaderStageResources.CallRecordRanges).
        foreach (var range in stage.CallRecordRanges ?? [])
        {
            if (range.Written)
            {
                MarkRange(range.Base, range.Size, sequence, PendingWriteKind.Range, into);
            }
        }
    }

    private static bool TryGetImageData(ShaderProgramInfo program, ImageResource image, uint[] words, int imageIndex, out GuestSpan data)
    {
        data = default;
        if (new TextureDescriptorWords(words).BaseAddress == 0)
        {
            return false;
        }

        var shape = ImageRequestBuilders.ShapeOf(image);
        var resolution = program.Frontend is { } frontend
            ? frontend.ResolveTexture(words, shape, program.Hash, imageIndex)
            : ImageRequestBuilders.Texture(words, shape, program.Hash, imageIndex);
        data = resolution.Request.Description.Data;
        return data.Size != 0;
    }
}

// One write mark a deferred dispatch publishes when it is finalized.
public readonly record struct PendingMark(ulong Address, ulong Size, PendingWriteKind Kind);
