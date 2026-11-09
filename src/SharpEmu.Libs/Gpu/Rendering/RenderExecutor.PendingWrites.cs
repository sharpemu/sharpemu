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

    private void MarkPendingWrites(ResolvedDispatch record)
    {
        if (record.Skipped)
        {
            return;
        }

        var sequence = record.Sequence;
        if (record.Program.ImageCopy is { } imageCopy)
        {
            Mark(imageCopy.Destination.Description.Data, sequence, PendingWriteKind.Target);
            return;
        }
        MarkStageWrites(record.Program.Input.Stage, sequence);
        if (record.ConstantFill is { } fill)
        {
            PendingWrites.Mark(fill.Address, fill.Size, sequence, PendingWriteKind.Fill);
        }

        if (record.BoundedFill is { } bounded)
        {
            PendingWrites.Mark(bounded.Address, bounded.Size, sequence, PendingWriteKind.Fill);
        }

        if (record.BoundedCopy is { } copy)
        {
            PendingWrites.Mark(copy.Destination, copy.Words * sizeof(uint), sequence, PendingWriteKind.Copy);
        }

        if (record.ImageClear is { } clear)
        {
            PendingWrites.Mark(clear.Descriptor.Address, clear.Size, sequence, PendingWriteKind.Fill);
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

    private void MarkStageWrites(in ShaderStageResources stage, long sequence)
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
                PendingWrites.Mark(descriptor.Address, footprint, sequence, PendingWriteKind.Buffer);
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
                Mark(data, sequence, PendingWriteKind.Image);
            }
        }

        foreach (var range in resources.DeviceAddressRanges)
        {
            if (range.Written)
            {
                PendingWrites.Mark(range.Base, range.Size, sequence, PendingWriteKind.Range);
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
