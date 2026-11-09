// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Color exports reach the targets in order: the Nth export that SPI_SHADER_COL_FORMAT gives a
// format goes to the Nth target that CB_SHADER_MASK enables. Compilers drop MRTs a pixel program
// does not write, so the export number and the target slot can differ. Unreal's base pass, for
// example, exports MRT0 and MRT1 to targets 0 and 4 with CB_SHADER_MASK=0x000F000F.
internal static class PixelExportRouting
{
    private const int TargetCount = 8;

    // The export that feeds the slot, or -1 when no export reaches it. When the registers do not
    // pair up, each export goes to the slot of the same number.
    public static int ExportForSlot(ShaderInterfaceRegisters registers, uint slot)
    {
        if (slot >= TargetCount)
        {
            return -1;
        }

        Span<int> exports = stackalloc int[TargetCount];
        Span<int> slots = stackalloc int[TargetCount];
        var exportCount = 0;
        var slotCount = 0;
        for (var index = 0; index < TargetCount; index++)
        {
            if (index < registers.TargetOutputModes.Length && (registers.TargetOutputModes[index] & 0xF) != 0)
            {
                exports[exportCount++] = index;
            }

            if (((registers.ColorShaderMask >> (index * 4)) & 0xFu) != 0)
            {
                slots[slotCount++] = index;
            }
        }

        if (exportCount == 0 || exportCount != slotCount)
        {
            return (int)slot;
        }

        for (var index = 0; index < slotCount; index++)
        {
            if (slots[index] == slot)
            {
                return exports[index];
            }
        }

        return -1;
    }
}
