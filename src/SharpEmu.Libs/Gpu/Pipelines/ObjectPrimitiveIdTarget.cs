// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The object/primitive ID buffer. With PA_CL_OBJPRIM_ID_CNTL bit 1 set, the hardware writes
// VGT_OBJECT_ID plus the primitive ID of the covering triangle (counted per instance) to a bound
// 32-bit integer colour slot that CB_SHADER_MASK gives no shader components. No public register reference covers
// this; the rule follows the values titles program: the object ID keeps its low 17 bits clear for
// the primitive ID. Several slots may name one surface, and only the lowest of them is written.
internal static class ObjectPrimitiveIdTarget
{
    private const uint PrimitiveIdEnable = 1u << 1;
    private static int _supported;

    // The host writes the target only when its fragment stage can read the primitive ID.
    public static bool Supported
    {
        get => Volatile.Read(ref _supported) != 0;
        set => Volatile.Write(ref _supported, value ? 1 : 0);
    }

    public static bool IsTarget(ContextRegisters context, uint slot)
    {
        if (!Supported || (context.ObjectPrimitiveIdControl & PrimitiveIdEnable) == 0 || !Qualifies(context, slot))
        {
            return false;
        }

        var baseAddress = context.ColorTargets[slot].BaseAddress;
        for (var lower = 0u; lower < slot; lower++)
        {
            if (context.ColorTargets[lower].BaseAddress == baseAddress && Qualifies(context, lower))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Qualifies(ContextRegisters context, uint slot)
    {
        var words = context.ColorTargets[slot];
        return words.BaseAddress != 0 && context.RenderTargetMaskForSlot(slot) != 0 &&
            words.Layout == ChannelLayout.Bits32 && words.NumberType == ChannelType.UInt &&
            ((context.ShaderInterface.ColorShaderMask >> (int)(slot * 4)) & 0xFu) == 0;
    }
}
