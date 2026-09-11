// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Rendering;

// The draw paths kept beside the ported executor: they act on the resolved programs before the draw records.
public sealed partial class RenderExecutor
{
    private const uint DccFastClearVertexCount = 4;
    private const byte BlendFactorOne = 1;
    private const byte BlendFactorOneMinusSourceAlpha = 5;
    private const byte BlendFunctionAdd = 0;
    private const float ClipSpaceTolerance = 0.001f;

    // False when the draw does not record: no programs, a solid clear, a metadata-only quad or a retained draw.
    private bool ApplyProgramAdaptations(RegisterBanks banks, in DrawCall draw, ref DrawState state, in TargetlessDrawArguments arguments)
    {
        var programs = state.Programs;
        if (!programs.Available)
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"Skipping a draw without programs: name={draw.Name} export=0x{banks.Shader.Vertex.ExportAddress:X16} pixel=0x{banks.Shader.Pixel.Address:X16}");
            }

            return false;
        }

        if (programs.SolidClear is { } clear && state.ColorCount != 0)
        {
            _host.ClearColorTargets(BoundColors(ref state), clear);
            return false;
        }

        if (IsMetadataClearQuad(banks, in draw, ref state))
        {
            return false;
        }

        if (state.ColorCount == 0 && !state.Depth.HasTarget && state.PixelActive &&
            programs.PixelInput.Stage.Program is { Images.Length: > 0 } pixelProgram && !WritesStorageImage(pixelProgram) &&
            _host.TryRetainTargetlessDraw(banks, programs, in arguments))
        {
            return false;
        }

        return true;
    }

    private static bool WritesStorageImage(ShaderProgramInfo program)
    {
        foreach (var image in program.Images)
        {
            if (image.Class == ImageResourceClass.Storage && image.Written)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPremultipliedFillBlend(in BlendRegisters blend) =>
        blend.Enable && blend.ColorSourceFactor == BlendFactorOne && blend.ColorDestinationFactor == BlendFactorOneMinusSourceAlpha && blend.ColorFunction == BlendFunctionAdd;

    // A covering quad that only programs the DCC clear codes; its shaded output is discarded.
    private bool IsMetadataClearQuad(RegisterBanks banks, in DrawCall draw, ref DrawState state)
    {
        var programs = state.Programs;
        var context = banks.Context;
        if (draw.Count != DccFastClearVertexCount || (GuestPrimitiveType)banks.UserConfig.PrimitiveType != GuestPrimitiveType.TriangleStrip ||
            state.ColorCount == 0 || programs.PixelInput.Stage.Program is not { Images.Length: 0 } || programs.VertexInput.Stage.Program is not { Images.Length: 0 })
        {
            return false;
        }

        foreach (ref readonly var target in BoundColors(ref state))
        {
            if (!IsPremultipliedFillBlend(in context.BlendControls[target.Slot]))
            {
                return false;
            }
        }

        var slot = state.Colors[0].Slot;
        var words = context.ColorTargets[slot];
        return words.DccEnabled && words.ClearWord0 == 0 && context.ColorClearWord1[slot] == 0 && CoversClipSpace(programs.PositionStream, draw.Count);
    }

    // The quad covers the clip volume when its positions span [-1, 1] on both axes.
    private bool CoversClipSpace(VertexPositionStream? stream, uint vertexCount)
    {
        if (stream is not { } positions)
        {
            return false;
        }

        var stride = positions.Stride == 0 ? 12u : positions.Stride;
        Span<byte> vertex = stackalloc byte[8];
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (var index = 0u; index < vertexCount; index++)
        {
            var address = positions.Address + positions.OffsetBytes + (ulong)index * stride;
            if (!_host.TryReadGuest(address, vertex))
            {
                return false;
            }

            var x = BinaryPrimitives.ReadSingleLittleEndian(vertex);
            var y = BinaryPrimitives.ReadSingleLittleEndian(vertex[4..]);
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                return false;
            }

            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        return minX <= -1f + ClipSpaceTolerance && maxX >= 1f - ClipSpaceTolerance && minY <= -1f + ClipSpaceTolerance && maxY >= 1f - ClipSpaceTolerance;
    }
}
