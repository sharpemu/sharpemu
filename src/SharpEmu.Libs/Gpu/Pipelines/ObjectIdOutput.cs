// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The ObjPrimID write of the PS5 hardware: a draw with VGT_DRAW_PAYLOAD_CNTL.OBJPRIM_ID_EN writes
// VGT_OBJECT_ID (plus the primitive index with PA_CL_OBJPRIM_ID_CNTL.ADD_PIPED_PRIM_ID) into a
// 32-bit color target its pixel program does not export to. Ghost of Yotei's visibility pass
// exports one MRT and reads the triangle ids back from slot 7 this way.
internal static class ObjectIdOutput
{
    public const int NoSlot = -1;
    private const uint PayloadObjectPrimitiveIdEnable = 1u << 0;
    private const uint AddPipedPrimitiveId = 1u << 1;

    // Slot 7 when it qualifies, else the first slot that does. A slot qualifies when CB_TARGET_MASK
    // enables it, its target has an address and the single-channel 32-bit layout, and no export reaches it.
    public static int SlotFor(ContextRegisters context)
    {
        var shaderInterface = context.ShaderInterface;
        if ((shaderInterface.DrawPayloadControl & PayloadObjectPrimitiveIdEnable) == 0)
        {
            return NoSlot;
        }

        if (Qualifies(context, 7))
        {
            return 7;
        }

        for (var slot = 0u; slot < 7; slot++)
        {
            if (Qualifies(context, slot))
            {
                return (int)slot;
            }
        }

        return NoSlot;
    }

    public static bool AddsPrimitiveId(ContextRegisters context) =>
        (context.ShaderInterface.ObjectPrimitiveIdControl & AddPipedPrimitiveId) != 0;

    private static bool Qualifies(ContextRegisters context, uint slot)
    {
        var target = context.ColorTargets[slot];
        if (context.RenderTargetMaskForSlot(slot) == 0 || target.BaseAddress == 0 || target.Layout != ChannelLayout.Bits32)
        {
            return false;
        }

        return !IsExported(context.ShaderInterface, slot);
    }

    // The export that feeds the slot has a format; ExportForSlot alone answers the slot itself when nothing pairs up.
    private static bool IsExported(ShaderInterfaceRegisters registers, uint slot)
    {
        var export = PixelExportRouting.ExportForSlot(registers, slot);
        return export >= 0 && export < registers.TargetOutputModes.Length && (registers.TargetOutputModes[export] & 0xF) != 0;
    }
}
