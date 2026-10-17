// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// Selects the color target slot of a draw and builds its image request from the context bank.
public static class ColorTargetResolver
{
    public const uint FirstBoundSlot = uint.MaxValue;

    // The first slot with a target mask nibble and a base address; slot 0 when none is bound.
    public static uint FirstBound(ContextRegisters context)
    {
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (context.RenderTargetMaskForSlot(slot) != 0 && context.ColorTargets[slot].BaseAddress != 0)
            {
                return slot;
            }
        }

        return 0;
    }

    // Null means the slot carries no color output.
    public static ColorTargetResolution? Resolve(ContextRegisters context, uint slot, uint drawLayerOffset, bool ignoreTargetMask, out uint resolvedSlot)
    {
        resolvedSlot = slot == FirstBoundSlot ? FirstBound(context) : slot;
        return ImageRequestBuilders.ColorTarget(in context.ColorTargets[resolvedSlot], context.RenderTargetMaskForSlot(resolvedSlot), drawLayerOffset, ignoreTargetMask);
    }
}
