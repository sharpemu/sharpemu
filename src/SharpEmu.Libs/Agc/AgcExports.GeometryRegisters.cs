// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{

    private static void ApplySubmittedRegisters(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint operation,
        uint register)
    {
        if (operation is ItSetShReg or ItSetContextReg or ItSetUconfigReg or ItSetUconfigRegIndex)
        {
            if (packetLength < 3 ||
                !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var startRegister))
            {
                return;
            }

            if (operation == ItSetUconfigRegIndex)
            {
                startRegister &= 0x0FFF_FFFFu;
            }

            var directDestination = operation switch
            {
                ItSetShReg => state.ShRegisters,
                ItSetContextReg => state.CxRegisters,
                _ => state.UcRegisters,
            };
            for (uint index = 0; index < packetLength - 2; index++)
            {
                if (!TryReadUInt32(
                        ctx,
                        packetAddress + 8 + ((ulong)index * sizeof(uint)),
                        out var value))
                {
                    return;
                }

                directDestination[startRegister + index] = value;
                if (operation == ItSetContextReg &&
                    startRegister + index is DbZInfo or DbDepthSizeXy)
                {
                    // A standalone attachment or extent write supersedes the
                    // extent carried by an earlier composite binding packet.
                    state.CompositeDepthSizeXy = null;
                }
                if (operation is ItSetUconfigReg or ItSetUconfigRegIndex)
                {
                    ApplyUcIndexTypeIfNeeded(state, startRegister + index, value);
                }

                if (operation == ItSetContextReg)
                {
                }
            }

            return;
        }

        Dictionary<uint, uint> destination;
        uint registerCount;
        ulong registersAddress;
        uint indirectRegister;
        if (operation is ItSetContextRegIndirect or ItSetShRegIndirect or ItSetUconfigRegIndirect)
        {
            if (packetLength < 5 ||
                !TryReadUInt64(ctx, packetAddress + 4, out registersAddress) ||
                !TryReadUInt32(ctx, packetAddress + 16, out registerCount))
            {
                return;
            }

            registersAddress &= ~0x3UL;
            registerCount &= 0x3FFFu;
            indirectRegister = operation switch
            {
                ItSetContextRegIndirect => RCxRegsIndirect,
                ItSetShRegIndirect => RShRegsIndirect,
                _ => RUcRegsIndirect,
            };
        }
        else if (operation == ItNop &&
                 register is RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect &&
                 packetLength >= 4 &&
                 TryReadUInt32(ctx, packetAddress + sizeof(uint), out registerCount) &&
                 TryReadUInt64(ctx, packetAddress + 8, out registersAddress))
        {
            // Accept command buffers produced by older builds. New packets use
            // the native five-dword SET_*_REG_INDIRECT encoding above.
            indirectRegister = register;
        }
        else
        {
            return;
        }

        destination = indirectRegister switch
        {
            RCxRegsIndirect => state.CxRegisters,
            RShRegsIndirect => state.ShRegisters,
            _ => state.UcRegisters,
        };
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            uint registerOffset;
            uint value;
            if (!TryReadUInt32(ctx, entryAddress, out registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out value))
            {
                return;
            }

            // Offset zero is a valid context-register index, not a terminator.
            // Preserve it to prevent stale depth and render-control state.
            registerOffset &= ~0x7000_0000u;
            if (indirectRegister == RUcRegsIndirect && registerOffset == DbDepthSizeXy)
            {
                // Apply recognized depth extents to the draw state.
                // Do not retain an old context-register value.
                state.CxRegisters[registerOffset] = value;
                state.CompositeDepthSizeXy = null;
                continue;
            }

            destination[registerOffset] = value;
            if (indirectRegister == RCxRegsIndirect)
            {
            }
            if (indirectRegister == RUcRegsIndirect)
            {
                ApplyUcIndexTypeIfNeeded(state, registerOffset, value);
            }
        }
    }

    private static void ApplySubmittedCompositeDepthExtent(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint remainingDwords,
        uint packetLength,
        uint operation)
    {
        const uint DbDepthInfo = 0x00Fu;
        const uint compositeDwordCount = 24;
        if (operation != ItSetContextReg ||
            packetLength != 10 ||
            remainingDwords < compositeDwordCount ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister) ||
            startRegister != DbZInfo ||
            !TryReadUInt32(ctx, packetAddress + 40, out var depthInfoHeader) ||
            depthInfoHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 44, out var depthInfoRegister) ||
            depthInfoRegister != DbDepthInfo ||
            !TryReadUInt32(ctx, packetAddress + 52, out var depthViewHeader) ||
            depthViewHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 56, out var depthViewRegister) ||
            depthViewRegister != DbDepthView ||
            !TryReadUInt32(ctx, packetAddress + 64, out var htileBaseHeader) ||
            htileBaseHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 68, out var htileBaseRegister) ||
            htileBaseRegister != DbHtileDataBase ||
            !TryReadUInt32(ctx, packetAddress + 76, out var htileSurfaceHeader) ||
            htileSurfaceHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 80, out var htileSurfaceRegister) ||
            htileSurfaceRegister != DbHtileSurface ||
            !TryReadUInt32(ctx, packetAddress + 88, out var extentHeader) ||
            extentHeader != Pm4(2, ItNop, 0) ||
            !TryReadUInt32(ctx, packetAddress + 92, out var sizeXy) ||
            sizeXy == 0)
        {
            return;
        }

        // The final NOP payload carries the composite depth extent.
        // Keep it separate so a later standalone write can replace it.
        state.CompositeDepthSizeXy = sizeXy;
    }

    internal static Gpu.GpuCommands.Registers.ContextRegisters? GetGraphicsContextForTests(CpuContext context)
    {
        if (!_submittedGpuStates.TryGetValue(context.Memory, out var gpuState))
        {
            return null;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.TypedRegisters?.Context.Copy();
        }
    }

    internal static bool TryGetGraphicsCompositeDepthSizeForTests(
        CpuContext ctx,
        out uint sizeXy)
    {
        sizeXy = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            if (gpuState.Graphics.CompositeDepthSizeXy is not { } value)
            {
                return false;
            }

            sizeXy = value;
            return true;
        }
    }

    // Test access to the shader words used by compilation.
    internal static bool TryGetGraphicsShRegisterForTests(
        CpuContext ctx,
        uint registerOffset,
        out uint value)
    {
        value = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.ShRegisters.TryGetValue(registerOffset, out value);
        }
    }

    /// <summary>
    /// GraphicsDcbSetIndexSize writes VGT_INDEX_TYPE via SET_UCONFIG_REG.
    /// Mirror that into <see cref="SubmittedDcbState.IndexSize"/>.
    /// </summary>
    private static void ApplyUcIndexTypeIfNeeded(
        SubmittedDcbState state,
        uint registerOffset,
        uint value)
    {
        if (registerOffset == VgtIndexType)
        {
            state.IndexSize = value & 0x3;
        }
    }
}
