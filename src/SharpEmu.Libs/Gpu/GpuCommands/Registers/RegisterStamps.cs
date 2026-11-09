// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

public enum RegisterStamp
{
    ColorTargets,
    DepthTargets,
    ShaderInterface,
    PipelineState,
    ContextDynamic,
    ShaderPrograms,
    VertexUserData,
    PixelUserData,
    ComputeUserData,
    UserConfig,
}

public static class RegisterStamps
{
    public const int Count = 10;
    public const uint ContextBits =
        (1u << (int)RegisterStamp.ColorTargets) | (1u << (int)RegisterStamp.DepthTargets) | (1u << (int)RegisterStamp.ShaderInterface) |
        (1u << (int)RegisterStamp.PipelineState) | (1u << (int)RegisterStamp.ContextDynamic);
    public const uint UserConfigBits = 1u << (int)RegisterStamp.UserConfig;
    public const uint AllBits = (1u << Count) - 1;

    private const uint ClipRectangleRegisterCount = 8;
    private const uint GraphicsUserDataCount = 32;
    private const uint ComputeUserDataCount = 16;
    private const uint ColorTargetRegisterEnd = CbColor0Attrib3 + ContextRegisters.ColorTargetCount - 1;

    private static readonly RegisterStamp[] _contextStampOf = BuildContextStamps();

    public static RegisterStamp ContextStampOf(uint offset) => offset < _contextStampOf.Length ? _contextStampOf[offset] : RegisterStamp.PipelineState;

    public static bool IsDynamicContext(uint offset) => ContextStampOf(offset) == RegisterStamp.ContextDynamic;

    public static uint ContextBitsFor(uint offset, uint count)
    {
        var bits = 0u;
        for (var index = 0u; index < count; index++)
        {
            bits |= 1u << (int)ContextStampOf(offset + index);
        }

        return bits;
    }

    public static uint ShaderBitsFor(uint offset, uint count)
    {
        var bits = 0u;
        for (var index = 0u; index < count; index++)
        {
            bits |= 1u << (int)ShaderStampOf(offset + index);
        }

        return bits;
    }

    public static RegisterStamp ShaderStampOf(uint offset)
    {
        if (offset - SpiShaderUserDataPs0 < GraphicsUserDataCount)
        {
            return RegisterStamp.PixelUserData;
        }

        if (offset - SpiShaderUserDataGs0 < GraphicsUserDataCount)
        {
            return RegisterStamp.VertexUserData;
        }

        return offset - ComputeUserData0 < ComputeUserDataCount ? RegisterStamp.ComputeUserData : RegisterStamp.ShaderPrograms;
    }

    private static RegisterStamp[] BuildContextStamps()
    {
        var stamps = new RegisterStamp[RegisterBankLayout.ContextRegisterCount];
        Array.Fill(stamps, RegisterStamp.PipelineState);
        void Range(uint first, uint last, RegisterStamp stamp)
        {
            for (var offset = first; offset <= last; offset++)
            {
                stamps[offset] = stamp;
            }
        }

        void One(RegisterStamp stamp, params ReadOnlySpan<uint> offsets)
        {
            foreach (var offset in offsets)
            {
                stamps[offset] = stamp;
            }
        }

        Range(CbColor0Base, ColorTargetRegisterEnd, RegisterStamp.ColorTargets);
        One(RegisterStamp.ColorTargets, CbTargetMask, CbColorControl);

        Range(DbRenderControl, DbRmiL2CacheControl, RegisterStamp.DepthTargets);
        Range(DbStencilControl, DbStencilRefMaskBack, RegisterStamp.DepthTargets);
        One(RegisterStamp.DepthTargets, DbDepthControl, DbHtileSurface);

        Range(SpiPsInputCntl0, SpiShaderColFormat, RegisterStamp.ShaderInterface);
        One(RegisterStamp.ShaderInterface, CbShaderMask, GeMaxOutputPerSubgroup, DbShaderControl, PaClClipCntl, PaClVsOutCntl,
            VgtHosMaxTessLevel, VgtHosMinTessLevel, VgtGsOnchipCntl, VgtGsOutPrimType, VgtPrimitiveIdEn, VgtEsgsRingItemSize, VgtReuseOff,
            VgtGsMaxVertOut, GeNggSubgroupCntl, VgtTessDistribution, VgtShaderStagesEn, VgtLsHsConfig, VgtTfParam, VgtGsInstanceCnt,
            PaScShaderControl);

        Range(PaScScreenScissorTl, PaScScreenScissorBr, RegisterStamp.ContextDynamic);
        Range(PaScWindowOffset, PaScWindowScissorBr, RegisterStamp.ContextDynamic);
        Range(PaScClipRectRule, PaScClipRect0Tl + ClipRectangleRegisterCount - 1, RegisterStamp.ContextDynamic);
        Range(PaScGenericScissorTl, PaScGenericScissorBr, RegisterStamp.ContextDynamic);
        Range(PaScViewportScissor0Tl, PaScViewportScissor15Br, RegisterStamp.ContextDynamic);
        Range(PaScViewportZMin0, PaScViewportZMax15, RegisterStamp.ContextDynamic);
        Range(PaClViewportXScale, PaClViewportZOffset15, RegisterStamp.ContextDynamic);
        Range(CbBlendRed, CbBlendAlpha, RegisterStamp.ContextDynamic);
        Range(PaSuLineCntl, PaSuLineCntl, RegisterStamp.ContextDynamic);
        Range(PaSuPolyOffsetDbFmtCntl, PaSuPolyOffsetBackOffset, RegisterStamp.ContextDynamic);
        return stamps;
    }
}

[System.Runtime.CompilerServices.InlineArray(RegisterStamps.Count)]
public struct RegisterStampSet
{
    private ulong _first;
}
