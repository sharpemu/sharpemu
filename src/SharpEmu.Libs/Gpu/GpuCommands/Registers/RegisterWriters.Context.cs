// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The context bank writers: direct packet writers first, then one indirect writer per register.
internal static partial class RegisterWriters
{
    private const uint ColorSlotStride = 15;
    private const uint ViewportFieldCount = 6;
    private const uint ClipRectangleRegisterCount = 8;

    public static void FillContext(RegisterPacketWriter?[] direct, RegisterWriter?[] indirect)
    {
        direct[DbRenderControl] = RenderControlPacket;
        foreach (var offset in new[]
                 {
                     DbCountControl, DbRenderOverride, DbRenderOverride2, DbDfsmControl, DbRmiL2CacheControl, CbRmiGl2CacheControl,
                     TaBcBaseAddr, TaBcBaseAddrHi, CbDccControl, PaSuPointSize, PaSuPointMinMax, DbAlphaToMask, VgtDrawPayloadCntl,
                     VgtPrimitiveIdReset, PaClObjPrimIdCntl, PaSuVtxCntl, PaScFovWindowLr, PaScFovWindowTb, PaScModeCntl1,
                     PaScAaMaskX0Y0X1Y0, PaScAaMaskX0Y1X1Y1, PsShaderSampleExclusionMask, PaScBinnerCntl0, PaScBinnerCntl1,
                     PaScConservativeRasterizationCntl,
                 })
        {
            direct[offset] = IgnoreValues;
        }

        direct[DbStencilClear] = StencilClearPacket;
        direct[DbDepthClear] = DepthClearPacket;
        direct[PaScScreenScissorTl] = ScreenScissorPacket;
        direct[DbStencilInfo] = StencilInfoPacket;
        direct[PaSuHardwareScreenOffset] = HardwareScreenOffsetPacket;
        direct[PaScWindowOffset] = WindowOffsetPacket;
        direct[PaScWindowScissorTl] = WindowScissorPacket;
        for (var offset = PaScClipRectRule; offset < PaScClipRect0Tl + ClipRectangleRegisterCount; offset++)
        {
            direct[offset] = ClipRectanglePacket;
        }

        direct[CbTargetMask] = RenderTargetMaskPacket;
        direct[PaScGenericScissorTl] = GenericScissorPacket;
        direct[DbStencilControl] = StencilControlPacket;
        direct[DbStencilRefMask] = StencilMaskPacket;
        direct[SpiPsInputCntl0] = PixelInterpolatorsPacket;
        direct[SpiTmpringSize] = IgnoreOneValue;
        direct[DbDepthControl] = DepthControlPacket;
        direct[DbEqaa] = EnhancedQualityAntialiasingPacket;
        direct[CbColorControl] = ColorControlPacket;
        direct[DbDepthBoundsMin] = DepthBoundsPacket;
        direct[DbDepthBoundsMax] = DepthBoundsPacket;
        direct[PaClClipCntl] = ClipControlPacket;
        direct[PaSuScModeCntl] = ModeControlPacket;
        for (var offset = PaSuPolyOffsetDbFmtCntl; offset <= PaSuPolyOffsetBackOffset; offset++)
        {
            direct[offset] = PolygonOffsetPacket;
        }

        direct[PaClVteCntl] = ViewportTransformControlPacket;
        direct[PaSuLineCntl] = LineControlPacket;
        direct[PaScModeCntl0] = ScanModeControlPacket;
        direct[PaScAaConfig] = AntialiasingConfigPacket;
        for (var offset = PaScAaSampleLocations0; offset < PaScAaSampleLocations0 + SampleLocationRegisters.LocationCount; offset++)
        {
            direct[offset] = SampleLocationsPacket;
        }

        direct[PaScCentroidPriority0] = CentroidPriorityPacket;
        direct[PaScCentroidPriority1] = CentroidPriorityPacket;
        direct[VgtShaderStagesEn] = ShaderStagesPacket;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            direct[CbColor0Info + (slot * ColorSlotStride)] = ColorInfoPacket;
            direct[CbBlend0Control + slot] = BlendControlPacket;
        }

        for (var viewport = 0u; viewport < ScreenViewportRegisters.ViewportCount; viewport++)
        {
            direct[PaScViewportScissor0Tl + (viewport * 2)] = ViewportScissorPacket;
            direct[PaScViewportScissor0Br + (viewport * 2)] = ViewportScissorPacket;
            direct[PaScViewportZMin0 + (viewport * 2)] = ViewportZPacket;
            direct[PaScViewportZMax0 + (viewport * 2)] = ViewportZPacket;
            for (var field = 0u; field < ViewportFieldCount; field++)
            {
                direct[PaClViewportXScale + (viewport * ViewportFieldCount) + field] = ViewportScaleOffsetPacket;
            }
        }

        FillContextIndirect(indirect);
    }

    private static void FillContextIndirect(RegisterWriter?[] indirect)
    {
        for (var offset = SpiPsInputCntl0; offset <= SpiPsInputCntl31; offset++)
        {
            indirect[offset] = static (banks, register, value) =>
            {
                banks.Context.ShaderInterface.PixelInterpolatorSettings[register - SpiPsInputCntl0] = value;
                banks.Context.ShaderInterface.PixelInterpolatorWritten |= 1u << (int)(register - SpiPsInputCntl0);
            };
        }

        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            var block = slot * ColorSlotStride;
            indirect[CbColor0Base + block] = ColorBaseLowEntry;
            indirect[CbColor0BaseExt + slot] = ColorBaseHighEntry;
            indirect[CbColor0View + block] = ColorViewEntry;
            indirect[CbColor0Info + block] = ColorInfoEntry;
            indirect[CbColor0Attrib + block] = ColorAttribEntry;
            indirect[CbColor0DccControl + block] = ColorDccControlEntry;
            indirect[CbColor0Cmask + block] = ColorCmaskLowEntry;
            indirect[CbColor0CmaskBaseExt + slot] = ColorCmaskHighEntry;
            indirect[CbColor0Fmask + block] = ColorFmaskLowEntry;
            indirect[CbColor0FmaskBaseExt + slot] = ColorFmaskHighEntry;
            indirect[CbColor0ClearWord0 + block] = ColorClearWord0Entry;
            indirect[CbColor0ClearWord1 + block] = ColorClearWord1Entry;
            indirect[CbColor0DccBase + block] = ColorDccBaseLowEntry;
            indirect[CbColor0DccBaseExt + slot] = ColorDccBaseHighEntry;
            indirect[CbColor0Attrib2 + slot] = ColorAttrib2Entry;
            indirect[CbColor0Attrib3 + slot] = ColorAttrib3Entry;
            indirect[CbBlend0Control + slot] = BlendControlEntry;
        }

        for (var viewport = 0u; viewport < ScreenViewportRegisters.ViewportCount; viewport++)
        {
            for (var field = 0u; field < ViewportFieldCount; field++)
            {
                indirect[PaClViewportXScale + (viewport * ViewportFieldCount) + field] = ViewportScaleOffsetEntry;
            }

            indirect[PaScViewportScissor0Tl + (viewport * 2)] = ViewportScissorTopLeftEntry;
            indirect[PaScViewportScissor0Br + (viewport * 2)] = ViewportScissorBottomRightEntry;
            indirect[PaScViewportZMin0 + (viewport * 2)] = ViewportZMinEntry;
            indirect[PaScViewportZMax0 + (viewport * 2)] = ViewportZMaxEntry;
        }

        for (var offset = PaClUserClipPlane0X; offset <= PaClUserClipPlane5W; offset++)
        {
            indirect[offset] = DisabledUserClipPlaneEntry;
        }

        for (var offset = PaClGbVertClipAdj; offset <= PaClGbHorzDiscAdj; offset++)
        {
            indirect[offset] = GuardBandEntry;
        }

        for (var offset = CbBlendRed; offset <= CbBlendAlpha; offset++)
        {
            indirect[offset] = BlendColorEntry;
        }

        for (var offset = PaScClipRectRule; offset < PaScClipRect0Tl + ClipRectangleRegisterCount; offset++)
        {
            indirect[offset] = ClipRectangleEntry;
        }

        for (var offset = PaScAaSampleLocations0; offset < PaScAaSampleLocations0 + SampleLocationRegisters.LocationCount; offset++)
        {
            indirect[offset] = static (banks, register, value) => banks.Context.SampleLocations.Locations[register - PaScAaSampleLocations0] = value;
        }

        foreach (var offset in new[]
                 {
                     CbDccControl, DbCountControl, DbSResultsCompareState0, DbSResultsCompareState1, DbRenderOverride, DbRenderOverride2,
                     DbDfsmControl, DbRmiL2CacheControl, CbRmiGl2CacheControl, TaBcBaseAddr, TaBcBaseAddrHi, PaSuPointSize, PaSuPointMinMax,
                     SpiTmpringSize, VgtDrawPayloadCntl, VgtPrimitiveIdReset, PaClObjPrimIdCntl, PaScFovWindowLr, PaScFovWindowTb, PaScFsrEnable,
                     FsrRecursions0, FsrRecursions1, PaScModeCntl1, PaScAaMaskX0Y0X1Y0, PaScAaMaskX0Y1X1Y1, PaSuVtxCntl, PsShaderSampleExclusionMask,
                     PaScBinnerCntl0, PaScBinnerCntl1, PaScConservativeRasterizationCntl, DbAlphaToMask,
                 })
        {
            indirect[offset] = IgnoreEntry;
        }

        indirect[SpiVsOutConfig] = static (banks, _, value) => banks.Context.ShaderInterface.VertexOutputConfiguration = value;
        indirect[SpiShaderPosFormat] = static (banks, _, value) => banks.Context.ShaderInterface.PositionExportFormat = value;
        indirect[SpiShaderIdxFormat] = static (banks, _, value) => banks.Context.ShaderInterface.IndexExportFormat = value;
        indirect[PaClVsOutCntl] = static (banks, _, value) => banks.Context.ShaderInterface.VertexOutputControl = value;
        indirect[GeNggSubgroupCntl] = static (banks, _, value) => banks.Context.ShaderInterface.PrimitiveShaderSubgroupControl = value;
        indirect[VgtGsInstanceCnt] = static (banks, _, value) => banks.Context.ShaderInterface.GeometryInstanceCount = value;
        indirect[VgtGsOnchipCntl] = static (banks, _, value) => banks.Context.ShaderInterface.GeometryOnChipControl = value;
        indirect[VgtHosMaxTessLevel] = static (banks, _, value) => banks.Context.ShaderInterface.MaxTessellationLevel = value;
        indirect[VgtHosMinTessLevel] = static (banks, _, value) => banks.Context.ShaderInterface.MinTessellationLevel = value;
        indirect[GeMaxOutputPerSubgroup] = static (banks, _, value) => banks.Context.ShaderInterface.MaxOutputPerSubgroup = value;
        indirect[VgtEsgsRingItemSize] = static (banks, _, value) => banks.Context.ShaderInterface.ExportRingItemSize = value;
        indirect[VgtGsMaxVertOut] = static (banks, _, value) => banks.Context.ShaderInterface.GeometryMaxVerticesOut = value;
        indirect[VgtPrimitiveIdEn] = static (banks, _, value) => banks.Context.ShaderInterface.PrimitiveIdEnable = value;
        indirect[VgtReuseOff] = static (banks, _, value) => banks.Context.ShaderInterface.VertexReuseOff = value;
        indirect[VgtMultiPrimIbResetIndex] = static (banks, _, value) => banks.Context.PrimitiveResetIndex = value;
        indirect[VgtTessDistribution] = static (banks, _, value) => banks.Context.ShaderInterface.TessellationDistribution = value;
        indirect[VgtShaderStagesEn] = static (banks, _, value) => banks.Context.ShaderStages = value;
        indirect[VgtLsHsConfig] = static (banks, _, value) => banks.Context.ShaderInterface.LocalHullConfiguration = value;
        indirect[VgtGsOutPrimType] = static (banks, _, value) => banks.Context.ShaderInterface.GeometryOutputPrimitiveType = value;
        indirect[VgtTfParam] = static (banks, _, value) => banks.Context.ShaderInterface.TessellationFactorParameter = value;
        indirect[SpiShaderZFormat] = static (banks, _, value) => banks.Context.ShaderInterface.DepthExportFormat = value;
        indirect[SpiShaderColFormat] = ShaderColorFormatEntry;
        indirect[SpiPsInputEna] = static (banks, _, value) => banks.Context.ShaderInterface.PixelInputEnable = value;
        indirect[SpiPsInputAddr] = static (banks, _, value) => banks.Context.ShaderInterface.PixelInputAddress = value;
        indirect[SpiPsInControl] = static (banks, _, value) => banks.Context.ShaderInterface.PixelInputControl = value;
        indirect[SpiBarycCntl] = static (banks, _, value) => banks.Context.ShaderInterface.BarycentricControl = value;
        indirect[DbShaderControl] = static (banks, _, value) => banks.Context.ShaderInterface.DepthShaderControl = DepthShaderControlRegisters.Decode(value);
        indirect[CbShaderMask] = static (banks, _, value) => banks.Context.ShaderInterface.ColorShaderMask = value;
        indirect[PaScShaderControl] = static (banks, _, value) => banks.Context.ShaderInterface.ScanShaderControl = value;
        indirect[CbTargetMask] = static (banks, _, value) => banks.Context.RenderTargetMask = value;
        indirect[PaScScreenScissorTl] = ScreenScissorTopLeftEntry;
        indirect[PaScScreenScissorBr] = ScreenScissorBottomRightEntry;
        indirect[PaScGenericScissorTl] = GenericScissorTopLeftEntry;
        indirect[PaScGenericScissorBr] = GenericScissorBottomRightEntry;
        indirect[PaSuHardwareScreenOffset] = HardwareScreenOffsetEntry;
        indirect[PaScWindowOffset] = WindowOffsetEntry;
        indirect[PaScWindowScissorTl] = WindowScissorTopLeftEntry;
        indirect[PaScWindowScissorBr] = WindowScissorBottomRightEntry;
        indirect[PaSuScModeCntl] = static (banks, _, value) => banks.Context.RasterMode = RasterModeRegisters.Decode(value);
        for (var offset = PaSuPolyOffsetDbFmtCntl; offset <= PaSuPolyOffsetBackOffset; offset++)
        {
            indirect[offset] = PolygonOffsetEntry;
        }

        indirect[DbZInfo] = DepthZInfoEntry;
        indirect[DbDepthBoundsMin] = DepthBoundsEntry;
        indirect[DbDepthBoundsMax] = DepthBoundsEntry;
        indirect[DbStencilInfo] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { StencilInfo = value };
        indirect[DbZReadBase] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { ZReadBase = RegisterField.WithLowAddress(banks.Context.DepthTarget.ZReadBase, value) };
        indirect[DbZReadBaseHi] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { ZReadBase = RegisterField.WithHighAddress(banks.Context.DepthTarget.ZReadBase, value) };
        indirect[DbStencilReadBase] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { StencilReadBase = RegisterField.WithLowAddress(banks.Context.DepthTarget.StencilReadBase, value) };
        indirect[DbStencilReadBaseHi] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { StencilReadBase = RegisterField.WithHighAddress(banks.Context.DepthTarget.StencilReadBase, value) };
        indirect[DbZWriteBase] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { ZWriteBase = RegisterField.WithLowAddress(banks.Context.DepthTarget.ZWriteBase, value) };
        indirect[DbZWriteBaseHi] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { ZWriteBase = RegisterField.WithHighAddress(banks.Context.DepthTarget.ZWriteBase, value) };
        indirect[DbStencilWriteBase] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { StencilWriteBase = RegisterField.WithLowAddress(banks.Context.DepthTarget.StencilWriteBase, value) };
        indirect[DbStencilWriteBaseHi] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { StencilWriteBase = RegisterField.WithHighAddress(banks.Context.DepthTarget.StencilWriteBase, value) };
        indirect[DbHtileDataBase] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { HtileBase = RegisterField.WithLowAddress(banks.Context.DepthTarget.HtileBase, value) };
        indirect[DbHtileDataBaseHi] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { HtileBase = RegisterField.WithHighAddress(banks.Context.DepthTarget.HtileBase, value) };
        indirect[DbHtileSurface] = HtileSurfaceEntry;
        indirect[DbDepthView] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { DepthView = value };
        indirect[DbDepthSizeXy] = DepthSizeEntry;
        indirect[DbReservedRegister2] = static (banks, _, value) => banks.Context.ReservedDepthRegister = value;
        indirect[DbReservedRegister1] = static (banks, _, value) => banks.Context.ReservedDepthRegister1 = value;
        indirect[DbReservedRegister3] = static (banks, _, value) => banks.Context.ReservedDepthRegister3 = value;
        indirect[DbDepthClear] = static (banks, _, value) => banks.Context.DepthClearValue = RegisterField.AsFloat(value);
        indirect[DbStencilClear] = static (banks, _, value) => banks.Context.StencilClearValue = (byte)RegisterField.Get(value, 0, 0xFF);
        indirect[DbStencilControl] = static (banks, _, value) => banks.Context.StencilControl = StencilControlRegisters.Decode(value);
        indirect[DbRenderControl] = RenderControlEntry;
        indirect[DbStencilRefMask] = StencilMaskFrontEntry;
        indirect[DbStencilRefMaskBack] = StencilMaskBackEntry;
        indirect[PaClClipCntl] = static (banks, _, value) => banks.Context.Clip = ClipControlRegisters.Decode(value);
        indirect[PaSuLineCntl] = LineControlEntry;
        indirect[PaClVteCntl] = static (banks, _, value) => banks.Context.ScreenViewport.TransformControl = value;
        indirect[PaScModeCntl0] = static (banks, _, value) => banks.Context.ScanMode = ScanModeRegisters.Decode(value);
        indirect[PaScAaConfig] = static (banks, _, value) => banks.Context.AntialiasingConfig = AntialiasingConfigRegisters.Decode(value);
        indirect[PaScCentroidPriority0] = CentroidPriorityEntry;
        indirect[PaScCentroidPriority1] = CentroidPriorityEntry;
        indirect[CbColorControl] = static (banks, _, value) => banks.Context.ColorControl = ColorControlRegisters.Decode(value);
        indirect[DbDepthControl] = static (banks, _, value) => banks.Context.DepthTarget = banks.Context.DepthTarget with { DepthControl = value };
        indirect[DbEqaa] = static (banks, _, value) => banks.Context.EnhancedQualityAntialiasing = EnhancedQualityAntialiasingRegisters.Decode(value);
    }

    private static uint IgnoreValues(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) => (uint)values.Length;

    private static uint IgnoreOneValue(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        RequireOneValue(banks, in packet, offset, values);

    private static void IgnoreEntry(RegisterBanks banks, uint offset, uint value)
    {
    }

    private static uint RequireOneValue(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length != 1)
        {
            throw PacketError(banks, "The register packet must carry one value", in packet, offset, values.Length);
        }

        return 1;
    }

    private static Exception PacketError(RegisterBanks banks, string condition, in PacketContext packet, uint offset, int count) =>
        banks.Fatal($"{condition}: offset=0x{offset:X4} count={count} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");

    // A direct writer that stores its values through the indirect writers of a fixed register range.
    private static uint ForwardRange(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values, uint first, uint last)
    {
        if (values.Length == 0 || offset < first || offset + (uint)values.Length - 1 > last)
        {
            throw banks.Fatal($"The register packet is outside its range: offset=0x{offset:X4} count={values.Length} range=0x{first:X4}-0x{last:X4} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        for (var index = 0u; index < values.Length; index++)
        {
            RegisterWriteTable.ContextIndirect[offset + index]!(banks, offset + index, values[(int)index]);
        }

        return (uint)values.Length;
    }

    private static uint SingleValue(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values, RegisterWriter entry)
    {
        RequireOneValue(banks, in packet, offset, values);
        entry(banks, offset, values[0]);
        return 1;
    }

    private static uint EachValue(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values, RegisterWriter entry)
    {
        for (var index = 0u; index < values.Length; index++)
        {
            entry(banks, offset + index, values[(int)index]);
        }

        return (uint)values.Length;
    }

    private static uint RenderControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RenderControlEntry);

    private static uint StencilClearPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[DbStencilClear]!);

    private static uint DepthClearPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[DbDepthClear]!);

    private static uint ScreenScissorPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        ForwardRange(banks, in packet, offset, values, PaScScreenScissorTl, PaScScreenScissorBr);

    private static uint StencilInfoPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[DbStencilInfo]!);

    private static uint HardwareScreenOffsetPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, HardwareScreenOffsetEntry);

    private static uint WindowOffsetPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, WindowOffsetEntry);

    private static uint WindowScissorPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        ForwardRange(banks, in packet, offset, values, PaScWindowScissorTl, PaScWindowScissorBr);

    private static uint ClipRectanglePacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length == 0 || offset < PaScClipRectRule || offset + (uint)values.Length > PaScClipRect0Tl + ClipRectangleRegisterCount)
        {
            throw PacketError(banks, "The clip rectangle packet is outside its range", in packet, offset, values.Length);
        }

        return EachValue(banks, in packet, offset, values, ClipRectangleEntry);
    }

    private static uint RenderTargetMaskPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[CbTargetMask]!);

    private static uint GenericScissorPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        ForwardRange(banks, in packet, offset, values, PaScGenericScissorTl, PaScGenericScissorBr);

    private static uint StencilControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[DbStencilControl]!);

    private static uint StencilMaskPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        ForwardRange(banks, in packet, offset, values, DbStencilRefMask, DbStencilRefMaskBack);

    private static uint PixelInterpolatorsPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length == 0 || values.Length > ShaderInterfaceRegisters.InterpolatorCount)
        {
            throw PacketError(banks, "The pixel interpolator packet has an invalid count", in packet, offset, values.Length);
        }

        values.CopyTo(banks.Context.ShaderInterface.PixelInterpolatorSettings);
        banks.Context.ShaderInterface.PixelInterpolatorWritten |= (uint)((1ul << values.Length) - 1);
        return (uint)values.Length;
    }

    private static uint DepthControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[DbDepthControl]!);

    private static uint EnhancedQualityAntialiasingPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[DbEqaa]!);

    private static uint ColorControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[CbColorControl]!);

    private static uint DepthBoundsPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        EachValue(banks, in packet, offset, values, DepthBoundsEntry);

    private static uint ClipControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[PaClClipCntl]!);

    private static uint ModeControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[PaSuScModeCntl]!);

    private static uint PolygonOffsetPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        EachValue(banks, in packet, offset, values, PolygonOffsetEntry);

    private static uint ViewportTransformControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[PaClVteCntl]!);

    private static uint LineControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, LineControlEntry);

    private static uint ScanModeControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[PaScModeCntl0]!);

    private static uint AntialiasingConfigPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[PaScAaConfig]!);

    // One location at any slot, or all sixteen from the first slot.
    private static uint SampleLocationsPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        var locations = banks.Context.SampleLocations.Locations;
        if (values.Length == 1)
        {
            locations[offset - PaScAaSampleLocations0] = values[0];
            return 1;
        }

        if (values.Length != SampleLocationRegisters.LocationCount || offset != PaScAaSampleLocations0)
        {
            throw PacketError(banks, "The sample location packet is not supported", in packet, offset, values.Length);
        }

        values.CopyTo(locations);
        return (uint)values.Length;
    }

    private static uint CentroidPriorityPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        EachValue(banks, in packet, offset, values, CentroidPriorityEntry);

    private static uint ShaderStagesPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.ContextIndirect[VgtShaderStagesEn]!);

    private static uint ColorInfoPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, ColorInfoEntry);

    private static uint BlendControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, BlendControlEntry);

    private static uint ViewportScissorPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length == 0 || offset < PaScViewportScissor0Tl || offset + (uint)values.Length - 1 > PaScViewportScissor15Br)
        {
            throw PacketError(banks, "The viewport scissor packet is outside its range", in packet, offset, values.Length);
        }

        for (var index = 0u; index < values.Length; index++)
        {
            var register = offset + index;
            if (((register - PaScViewportScissor0Tl) % 2) == 0)
            {
                ViewportScissorTopLeftEntry(banks, register, values[(int)index]);
            }
            else
            {
                ViewportScissorBottomRightEntry(banks, register, values[(int)index]);
            }
        }

        return (uint)values.Length;
    }

    private static uint ViewportZPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length == 0 || offset < PaScViewportZMin0 || offset + (uint)values.Length - 1 > PaScViewportZMax15)
        {
            throw PacketError(banks, "The viewport depth range packet is outside its range", in packet, offset, values.Length);
        }

        for (var index = 0u; index < values.Length; index++)
        {
            var register = offset + index;
            if (((register - PaScViewportZMin0) % 2) == 0)
            {
                ViewportZMinEntry(banks, register, values[(int)index]);
            }
            else
            {
                ViewportZMaxEntry(banks, register, values[(int)index]);
            }
        }

        return (uint)values.Length;
    }

    private static uint ViewportScaleOffsetPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length == 0 || offset < PaClViewportXScale || offset + (uint)values.Length - 1 > PaClViewportZOffset15)
        {
            throw PacketError(banks, "The viewport scale packet is outside its range", in packet, offset, values.Length);
        }

        return EachValue(banks, in packet, offset, values, ViewportScaleOffsetEntry);
    }

    private static void RenderControlEntry(RegisterBanks banks, uint offset, uint value)
    {
        if (RegisterField.Bit(value, 2) || RegisterField.Bit(value, 3))
        {
            throw banks.Fatal($"The depth or stencil copy to color is not supported: renderControl=0x{value:X8}.");
        }

        banks.Context.DepthTarget = banks.Context.DepthTarget with { RenderControl = value };
    }

    private static void HardwareScreenOffsetEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.ScreenViewport.HardwareOffsetX = RegisterField.Get(value, 0, 0x1FF);
        banks.Context.ScreenViewport.HardwareOffsetY = RegisterField.Get(value, 16, 0x1FF);
    }

    private static void WindowOffsetEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.ScreenViewport.WindowOffsetX = RegisterField.Coordinate16(value);
        banks.Context.ScreenViewport.WindowOffsetY = RegisterField.Coordinate16(value >> 16);
    }

    private static void ClipRectangleEntry(RegisterBanks banks, uint offset, uint value)
    {
        var viewport = banks.Context.ScreenViewport;
        if (offset == PaScClipRectRule)
        {
            viewport.ClipRectangleRule = (ushort)(value & 0xFFFFu);
            return;
        }

        if (offset >= PaScClipRect0Tl && offset < PaScClipRect0Tl + ClipRectangleRegisterCount)
        {
            var rectangle = (offset - PaScClipRect0Tl) / 2;
            if (((offset - PaScClipRect0Tl) & 1) == 0)
            {
                viewport.ClipRectangleLeft[rectangle] = RegisterField.Coordinate15(value);
                viewport.ClipRectangleTop[rectangle] = RegisterField.Coordinate15(value >> 16);
                viewport.ClipRectangleWindowOffsetEnable[rectangle] = !RegisterField.Bit(value, 31);
            }
            else
            {
                viewport.ClipRectangleRight[rectangle] = RegisterField.Coordinate15(value);
                viewport.ClipRectangleBottom[rectangle] = RegisterField.Coordinate15(value >> 16);
            }

            return;
        }

        throw banks.Fatal($"The clip rectangle register is unknown: offset=0x{offset:X4} value=0x{value:X8}.");
    }

    private static void DepthBoundsEntry(RegisterBanks banks, uint offset, uint value)
    {
        switch (offset)
        {
            case DbDepthBoundsMin:
                banks.Context.DepthBoundsMin = RegisterField.AsFloat(value);
                break;
            case DbDepthBoundsMax:
                banks.Context.DepthBoundsMax = RegisterField.AsFloat(value);
                break;
            default:
                throw banks.Fatal($"The depth bounds register is unknown: offset=0x{offset:X4} value=0x{value:X8}.");
        }
    }

    private static void PolygonOffsetEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var polygonOffset = ref banks.Context.PolygonOffset;
        switch (offset)
        {
            case PaSuPolyOffsetDbFmtCntl:
                polygonOffset.NegativeDepthBits = (sbyte)(byte)RegisterField.Get(value, 0, 0xFF);
                polygonOffset.DepthIsFloat = RegisterField.Bit(value, 8);
                break;
            case PaSuPolyOffsetClamp:
                polygonOffset.Clamp = RegisterField.AsFloat(value);
                break;
            case PaSuPolyOffsetFrontScale:
                polygonOffset.FrontScale = RegisterField.AsFloat(value);
                break;
            case PaSuPolyOffsetFrontOffset:
                polygonOffset.FrontOffset = RegisterField.AsFloat(value);
                break;
            case PaSuPolyOffsetBackScale:
                polygonOffset.BackScale = RegisterField.AsFloat(value);
                break;
            case PaSuPolyOffsetBackOffset:
                polygonOffset.BackOffset = RegisterField.AsFloat(value);
                break;
            default:
                throw banks.Fatal($"The polygon offset register is unknown: offset=0x{offset:X4} value=0x{value:X8}.");
        }
    }

    private static void LineControlEntry(RegisterBanks banks, uint offset, uint value)
    {
        var width = RegisterField.Get(value, 0, 0xFFFF);
        banks.Context.LineWidth = width == 8 ? 1f : width / 8f;
    }

    private static void CentroidPriorityEntry(RegisterBanks banks, uint offset, uint value)
    {
        var locations = banks.Context.SampleLocations;
        switch (offset)
        {
            case PaScCentroidPriority0:
                locations.CentroidPriority = (locations.CentroidPriority & 0xFFFF_FFFF_0000_0000ul) | value;
                break;
            case PaScCentroidPriority1:
                locations.CentroidPriority = (locations.CentroidPriority & 0x0000_0000_FFFF_FFFFul) | ((ulong)value << 32);
                break;
        }
    }

    private static void ShaderColorFormatEntry(RegisterBanks banks, uint offset, uint value)
    {
        var modes = banks.Context.ShaderInterface.TargetOutputModes;
        for (var index = 0; index < modes.Length; index++)
        {
            modes[index] = (byte)((value >> (index * 4)) & 0xFu);
        }
    }

    private static void ScreenScissorTopLeftEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.ScreenViewport.ScreenScissorLeft = RegisterField.Coordinate16(value);
        banks.Context.ScreenViewport.ScreenScissorTop = RegisterField.Coordinate16(value >> 16);
    }

    private static void ScreenScissorBottomRightEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.ScreenViewport.ScreenScissorRight = RegisterField.Coordinate16(value);
        banks.Context.ScreenViewport.ScreenScissorBottom = RegisterField.Coordinate16(value >> 16);
    }

    private static void GenericScissorTopLeftEntry(RegisterBanks banks, uint offset, uint value)
    {
        var viewport = banks.Context.ScreenViewport;
        viewport.GenericScissorLeft = RegisterField.Coordinate15(value);
        viewport.GenericScissorTop = RegisterField.Coordinate15(value >> 16);
        viewport.GenericScissorWindowOffsetEnable = !RegisterField.Bit(value, 31);
    }

    private static void GenericScissorBottomRightEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.ScreenViewport.GenericScissorRight = RegisterField.Coordinate15(value);
        banks.Context.ScreenViewport.GenericScissorBottom = RegisterField.Coordinate15(value >> 16);
    }

    private static void WindowScissorTopLeftEntry(RegisterBanks banks, uint offset, uint value)
    {
        var viewport = banks.Context.ScreenViewport;
        viewport.WindowScissorLeft = RegisterField.Coordinate15(value);
        viewport.WindowScissorTop = RegisterField.Coordinate15(value >> 16);
        viewport.WindowScissorWindowOffsetEnable = !RegisterField.Bit(value, 31);
    }

    private static void WindowScissorBottomRightEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.ScreenViewport.WindowScissorRight = RegisterField.Coordinate15(value);
        banks.Context.ScreenViewport.WindowScissorBottom = RegisterField.Coordinate15(value >> 16);
    }

    private static void ViewportScissorTopLeftEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var viewport = ref banks.Context.ScreenViewport.Viewports[(offset - PaScViewportScissor0Tl) / 2];
        viewport.ScissorLeft = RegisterField.Coordinate15(value);
        viewport.ScissorTop = RegisterField.Coordinate15(value >> 16);
        viewport.ScissorWindowOffsetEnable = !RegisterField.Bit(value, 31);
    }

    private static void ViewportScissorBottomRightEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var viewport = ref banks.Context.ScreenViewport.Viewports[(offset - PaScViewportScissor0Br) / 2];
        viewport.ScissorRight = RegisterField.Coordinate15(value);
        viewport.ScissorBottom = RegisterField.Coordinate15(value >> 16);
    }

    private static void ViewportZMinEntry(RegisterBanks banks, uint offset, uint value) =>
        banks.Context.ScreenViewport.Viewports[(offset - PaScViewportZMin0) / 2].MinDepth = RegisterField.AsFloat(value);

    private static void ViewportZMaxEntry(RegisterBanks banks, uint offset, uint value) =>
        banks.Context.ScreenViewport.Viewports[(offset - PaScViewportZMax0) / 2].MaxDepth = RegisterField.AsFloat(value);

    private static void ViewportScaleOffsetEntry(RegisterBanks banks, uint offset, uint value)
    {
        var relative = offset - PaClViewportXScale;
        ref var viewport = ref banks.Context.ScreenViewport.Viewports[relative / ViewportFieldCount];
        var number = RegisterField.AsFloat(value);
        switch (relative % ViewportFieldCount)
        {
            case 0:
                viewport.XScale = number;
                break;
            case 1:
                viewport.XOffset = number;
                break;
            case 2:
                viewport.YScale = number;
                break;
            case 3:
                viewport.YOffset = number;
                break;
            case 4:
                viewport.ZScale = number;
                break;
            default:
                viewport.ZOffset = number;
                break;
        }
    }

    private static void DisabledUserClipPlaneEntry(RegisterBanks banks, uint offset, uint value)
    {
        if (value != 0 || banks.Context.Clip.UserClipPlanes != 0)
        {
            throw banks.Fatal($"User clip planes are not supported: offset=0x{offset:X4} value=0x{value:X8} enabled=0x{banks.Context.Clip.UserClipPlanes:X2}.");
        }
    }

    private static void GuardBandEntry(RegisterBanks banks, uint offset, uint value)
    {
        var viewport = banks.Context.ScreenViewport;
        var number = RegisterField.AsFloat(value);
        switch (offset)
        {
            case PaClGbVertClipAdj:
                viewport.GuardBandVerticalClip = number;
                break;
            case PaClGbVertDiscAdj:
                viewport.GuardBandVerticalDiscard = number;
                break;
            case PaClGbHorzClipAdj:
                viewport.GuardBandHorizontalClip = number;
                break;
            default:
                viewport.GuardBandHorizontalDiscard = number;
                break;
        }
    }

    // Register order follows the hardware map: red, green, blue, alpha.
    private static void BlendColorEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var color = ref banks.Context.BlendColor;
        var number = RegisterField.AsFloat(value);
        switch (offset)
        {
            case CbBlendRed:
                color.Red = number;
                break;
            case CbBlendGreen:
                color.Green = number;
                break;
            case CbBlendBlue:
                color.Blue = number;
                break;
            default:
                color.Alpha = number;
                break;
        }
    }

    private static void BlendControlEntry(RegisterBanks banks, uint offset, uint value) =>
        banks.Context.BlendControls[offset - CbBlend0Control] = BlendRegisters.Decode(value);

    private static void DepthZInfoEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.DepthTarget = banks.Context.DepthTarget with { ZInfo = value };
        banks.CompositeDepthSizeXy = null;
    }

    private static void DepthSizeEntry(RegisterBanks banks, uint offset, uint value)
    {
        banks.Context.DepthTarget = banks.Context.DepthTarget with { DepthSizeXy = value, DepthSizeValid = true };
        banks.CompositeDepthSizeXy = null;
    }

    private static void HtileSurfaceEntry(RegisterBanks banks, uint offset, uint value)
    {
        var encoding = RegisterField.Get(value, 19, 0x3);
        if (encoding > 2)
        {
            throw banks.Fatal($"The shading rate encoding is not supported: value=0x{value:X8} encoding={encoding}.");
        }

        banks.Context.DepthTarget = banks.Context.DepthTarget with { HtileSurface = value };
    }

    private static void StencilMaskFrontEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var mask = ref banks.Context.StencilMask;
        mask.TestValue = (byte)RegisterField.Get(value, 0, 0xFF);
        mask.Mask = (byte)RegisterField.Get(value, 8, 0xFF);
        mask.WriteMask = (byte)RegisterField.Get(value, 16, 0xFF);
        mask.OperationValue = (byte)RegisterField.Get(value, 24, 0xFF);
    }

    private static void StencilMaskBackEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var mask = ref banks.Context.StencilMask;
        mask.TestValueBack = (byte)RegisterField.Get(value, 0, 0xFF);
        mask.MaskBack = (byte)RegisterField.Get(value, 8, 0xFF);
        mask.WriteMaskBack = (byte)RegisterField.Get(value, 16, 0xFF);
        mask.OperationValueBack = (byte)RegisterField.Get(value, 24, 0xFF);
    }

    private static ref ColorTargetWords ColorTarget(RegisterBanks banks, uint offset, uint first, uint stride) =>
        ref banks.Context.ColorTargets[(offset - first) / stride];

    private static void ColorBaseLowEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0Base, ColorSlotStride);
        target = target with { BaseAddress = RegisterField.WithLowAddress(target.BaseAddress, value) };
    }

    private static void ColorBaseHighEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0BaseExt, 1);
        target = target with { BaseAddress = RegisterField.WithHighAddress(target.BaseAddress, value) };
    }

    private static void ColorViewEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0View, ColorSlotStride);
        target = target with { View = value };
    }

    private static void ColorInfoEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0Info, ColorSlotStride);
        target = target with { Info = value };
    }

    private static void ColorAttribEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0Attrib, ColorSlotStride);
        target = target with { Attrib = value };
    }

    private static void ColorDccControlEntry(RegisterBanks banks, uint offset, uint value)
    {
        if (RegisterField.Bit(value, 9) && RegisterField.Bit(value, 20))
        {
            throw banks.Fatal($"The color target sets both independent DCC block sizes: offset=0x{offset:X4} dccControl=0x{value:X8}.");
        }

        ref var target = ref ColorTarget(banks, offset, CbColor0DccControl, ColorSlotStride);
        target = target with { DccControl = value };
    }

    private static void ColorCmaskLowEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0Cmask, ColorSlotStride);
        target = target with { CmaskAddress = RegisterField.WithLowAddress(target.CmaskAddress, value) };
    }

    private static void ColorCmaskHighEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0CmaskBaseExt, 1);
        target = target with { CmaskAddress = RegisterField.WithHighAddress(target.CmaskAddress, value) };
    }

    private static void ColorFmaskLowEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0Fmask, ColorSlotStride);
        target = target with { FmaskAddress = RegisterField.WithLowAddress(target.FmaskAddress, value) };
    }

    private static void ColorFmaskHighEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0FmaskBaseExt, 1);
        target = target with { FmaskAddress = RegisterField.WithHighAddress(target.FmaskAddress, value) };
    }

    private static void ColorClearWord0Entry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0ClearWord0, ColorSlotStride);
        target = target with { ClearWord0 = value };
    }

    private static void ColorClearWord1Entry(RegisterBanks banks, uint offset, uint value) =>
        banks.Context.ColorClearWord1[(offset - CbColor0ClearWord1) / ColorSlotStride] = value;

    private static void ColorDccBaseLowEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0DccBase, ColorSlotStride);
        target = target with { DccAddress = RegisterField.WithLowAddress(target.DccAddress, value) };
    }

    private static void ColorDccBaseHighEntry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0DccBaseExt, 1);
        target = target with { DccAddress = RegisterField.WithHighAddress(target.DccAddress, value) };
    }

    private static void ColorAttrib2Entry(RegisterBanks banks, uint offset, uint value)
    {
        ref var target = ref ColorTarget(banks, offset, CbColor0Attrib2, 1);
        target = target with { Attrib2 = value };
    }

    private static void ColorAttrib3Entry(RegisterBanks banks, uint offset, uint value)
    {
        if (RegisterField.Bit(value, 26) != RegisterField.Bit(value, 30))
        {
            throw banks.Fatal($"The color target metadata alignment differs between CMASK and DCC: offset=0x{offset:X4} attrib3=0x{value:X8}.");
        }

        ref var target = ref ColorTarget(banks, offset, CbColor0Attrib3, 1);
        target = target with { Attrib3 = value };
    }
}
