// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Shared Prospero/AGC primitive-type helpers (renderDraw / gpu_defs).
/// </summary>
internal static class AgcPrimitiveHelpers
{
    internal const uint PrimitiveRectList = 7;
    internal const uint PrimitiveRectListLegacy = 0x11;

    internal enum GsOutputPrimitiveType : uint
    {
        Points = 0,
        Lines = 1,
        Triangles = 2,
        Rectangle2D = 3,
        RectList = 4,
    }

    internal static bool IsRectListPrimitive(uint primitiveType) =>
        primitiveType is PrimitiveRectList or PrimitiveRectListLegacy;

    /// <summary>
    /// Maps draw prim type to VGT_GS_OUT_PRIM_TYPE when NGG is not enabled
    /// on the GS (GraphicsPrimitiveTypeToGsOut).
    /// </summary>
    internal static uint PrimitiveTypeToGsOut(uint primitiveType) =>
        primitiveType switch
        {
            1 => (uint)GsOutputPrimitiveType.Points, // PointList
            2 or 3 or 10 or 11 or 18 => (uint)GsOutputPrimitiveType.Lines,
            PrimitiveRectList => (uint)GsOutputPrimitiveType.Rectangle2D,
            PrimitiveRectListLegacy => (uint)GsOutputPrimitiveType.RectList,
            _ => (uint)GsOutputPrimitiveType.Triangles,
        };

    /// <summary>
    /// Rect-list auto-draw topology selection.
    /// NGG single-rect UI quads (DualSense prompts) submit count 1/3/4 and
    /// must become a 4-vert triangle strip — even when the VS has embedded
    /// vertex-buffer fetches (those still show up as host VBs). Indexed and
    /// larger auto counts stay triangle list so the loading video is safe.
    /// </summary>
    internal static bool ShouldDrawRectListAsTriangleStrip(
        uint primitiveType,
        bool indexed,
        uint vertexCount,
        bool hasVertexBuffers = false)
    {
        _ = hasVertexBuffers;
        if (indexed || !IsRectListPrimitive(primitiveType))
        {
            return false;
        }

        if (primitiveType == PrimitiveRectListLegacy)
        {
            return true;
        }

        // NGG kRectList: strip for auto + ngg_rectlist_draw.
        // Restrict to the single-rect counts GTA UI actually submits.
        return vertexCount is 1 or 3 or 4;
    }

    /// <summary>
    /// Host vertex count for auto rect-list draws that expand to a strip.
    /// NGG single-rect: always 4. Legacy 0x11: 3 -> 4.
    /// </summary>
    internal static uint GetRectListDrawVertexCount(
        uint primitiveType,
        uint vertexCount,
        bool indexed,
        bool hasVertexBuffers = false)
    {
        if (!ShouldDrawRectListAsTriangleStrip(
                primitiveType,
                indexed,
                vertexCount,
                hasVertexBuffers))
        {
            return vertexCount;
        }

        if (primitiveType == PrimitiveRectList)
        {
            return 4;
        }

        if (primitiveType == PrimitiveRectListLegacy && vertexCount == 3)
        {
            return 4;
        }

        return vertexCount;
    }

    /// <summary>
    /// Legacy helper — prefer
    /// <see cref="ShouldDrawRectListAsTriangleStrip"/>.
    /// </summary>
    internal static bool IsRectListTriangleStrip(uint primitiveType) =>
        IsRectListPrimitive(primitiveType);
}
