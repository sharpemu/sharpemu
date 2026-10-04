// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// PA_SU_SC_MODE_CNTL has one enable field and a polygon type for each face.
// Vulkan has one polygon mode, so the mode of a culled face does not constrain
// the host pipeline.
internal static class PolygonModeResolver
{
    internal static bool TryResolve(
        in RasterModeRegisters mode,
        bool cullFront,
        bool cullBack,
        out PolygonMode polygonMode,
        out string error)
    {
        polygonMode = PolygonMode.Fill;
        error = string.Empty;

        // CxPrimitiveSetup::PolygonMode disables both per-face fields at zero.
        if (mode.PolygonMode == 0)
        {
            return true;
        }

        if (mode.PolygonMode != 1)
        {
            error = $"The polygon mode is not supported: mode={mode.PolygonMode}.";
            return false;
        }

        if (cullFront && cullBack)
        {
            return true;
        }

        if (!cullFront && !cullBack && mode.FrontPolygonType != mode.BackPolygonType)
        {
            error =
                $"Different polygon modes for two visible faces are not supported: " +
                $"front={mode.FrontPolygonType} back={mode.BackPolygonType}.";
            return false;
        }

        // When one face is culled, only the other face selects the Vulkan mode.
        var polygonType = cullFront ? mode.BackPolygonType : mode.FrontPolygonType;
        polygonMode = polygonType switch
        {
            0 => PolygonMode.Point,
            1 => PolygonMode.Line,
            2 => PolygonMode.Fill,
            _ => PolygonMode.Fill,
        };

        if (polygonType <= 2)
        {
            return true;
        }

        error = $"The polygon type is invalid: type={polygonType}.";
        return false;
    }
}
