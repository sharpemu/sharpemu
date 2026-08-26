// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial decodes AGC render state for translated guest draws.

    private static readonly bool _traceDepthMetadata = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DEPTH_METADATA"),
        "1",
        StringComparison.Ordinal);
    private static readonly ConcurrentDictionary<
        (ulong Depth, ulong Htile, uint ZInfo, uint Surface, uint Control, uint RenderControl), byte>
        _tracedDepthMetadataStates = new();
    private static readonly ConcurrentDictionary<(ulong Htile, string Source), byte>
        _tracedHtileMetadataMarks = new();
    private static readonly ConcurrentDictionary<
        (ulong Depth, ulong Htile, uint Layer, uint ClearBits), byte>
        _tracedHtileMetadataConsumes = new();
    private static int _depthMetadataTraceCount;

    private readonly record struct RenderTargetDescriptor(
        uint Slot,
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint ComponentSwap,
        uint TileMode);

#if DEBUG
    private static void ValidateDepthTargetDecoder()
    {
        var registers = new Dictionary<uint, uint>
        {
            [DbDepthControl] = 0x2u | 0x4u | (1u << 4),
            [DbDepthSizeXy] = 1919u | (1079u << 16),
            [DbDepthClear] = BitConverter.SingleToUInt32Bits(1f),
            [DbZInfo] = 3u | (24u << 4),
            [DbZReadBase] = 0x0123_4567u,
            [DbZWriteBase] = 0x0123_4567u,
            [DbZReadBaseHi] = 2u,
            [DbZWriteBaseHi] = 2u,
        };
        var depth = DecodeDepthTarget(registers);
        System.Diagnostics.Debug.Assert(depth is not null);
        System.Diagnostics.Debug.Assert(depth.Width == 1920 && depth.Height == 1080);
        System.Diagnostics.Debug.Assert(depth.GuestFormat == 3u);
        System.Diagnostics.Debug.Assert(depth.SwizzleMode == 24u);
        System.Diagnostics.Debug.Assert(depth.Address == 0x0000_0201_2345_6700UL);
        System.Diagnostics.Debug.Assert(depth.ClearDepth == 1f);
    }
#endif

    private static IReadOnlyList<RenderTargetDescriptor> GetRenderTargets(
        IReadOnlyDictionary<uint, uint> registers,
        bool includeMaskedTargets = false)
    {
        var hasTargetMask = registers.TryGetValue(CbTargetMask, out var targetMask);
        var targets = new List<RenderTargetDescriptor>(ColorTargetCount);
        for (uint slot = 0; slot < ColorTargetCount; slot++)
        {
            var baseRegister = CbColor0Base + slot * CbColorRegisterStride;
            if (!registers.TryGetValue(baseRegister, out var baseLow) ||
                !registers.TryGetValue(CbColor0BaseExt + slot, out var baseHigh) ||
                !registers.TryGetValue(CbColor0Attrib2 + slot, out var attrib2) ||
                !registers.TryGetValue(CbColor0Attrib3 + slot, out var attrib3) ||
                !registers.TryGetValue(CbColor0Info + slot * CbColorRegisterStride, out var info))
            {
                continue;
            }

            var address = ((ulong)(baseHigh & 0xFFu) << 40) | ((ulong)baseLow << 8);
            var writeMask = (targetMask >> ((int)slot * 4)) & 0xFu;
            if (address == 0 ||
                (!includeMaskedTargets && hasTargetMask && writeMask == 0))
            {
                continue;
            }

            if (targets.Exists(existing => existing.Address == address))
            {
                continue;
            }

            NoteRenderTargetAddress(address);

            targets.Add(new RenderTargetDescriptor(
                slot,
                address,
                ((attrib2 >> 14) & 0x3FFFu) + 1,
                (attrib2 & 0x3FFFu) + 1,
                (info >> 2) & 0x1Fu,
                (info >> 8) & 0x7u,
                ExtractRenderTargetComponentSwap(info),
                ExtractRenderTargetTileMode(attrib3)));
        }

        if (targets.Count > 1 &&
            targets.Select(t => t.Address).Distinct().Count() != targets.Count)
        {
            var dupCount = Interlocked.Increment(ref _duplicateTargetTraceCount);
            if (dupCount <= 12 || dupCount % 500 == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.rt_duplicate#{dupCount} has_mask={hasTargetMask} " +
                    $"mask=0x{targetMask:X8} slots=[" +
                    string.Join(",", targets.Select(t =>
                        $"{t.Slot}:0x{t.Address:X}:m{(targetMask >> ((int)t.Slot * 4)) & 0xFu}")) +
                    "]");
            }
        }

        return targets;
    }

    internal static uint ExtractRenderTargetComponentSwap(uint colorInfo) =>
        (colorInfo >> 11) & 0x3u;

    internal static uint ExtractRenderTargetTileMode(uint colorAttrib3) =>
        (colorAttrib3 >> 14) & 0x1Fu;

    private static GuestRenderTarget CreateGuestRenderTarget(
        RenderTargetDescriptor target) =>
        new(
            target.Address,
            target.Width,
            target.Height,
            target.Format,
            target.NumberType,
            MipLevels: 1,
            ComponentSwap: target.ComponentSwap,
            TileMode: target.TileMode);

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        RenderTargetDescriptor target)
    {
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        return new GuestRenderState(
            [DecodeBlendState(registers, target.Slot)],
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers),
            DecodeBlendConstant(registers));
    }

    internal static GuestRenderState CreateDepthTargetRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        GuestDepthTarget depthTarget)
    {
        var target = new RenderTargetDescriptor(
            Slot: 0,
            Address: 0,
            depthTarget.Width,
            depthTarget.Height,
            Format: 0,
            NumberType: 0,
            ComponentSwap: 0,
            TileMode: 0);
        return CreateRenderState(registers, target) with
        {
            Blends = [GuestBlendState.Default with { WriteMask = 0 }],
        };
    }

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> targets,
        uint pixelColorExportMasks,
        IReadOnlyList<Gen5ColorComponentMapping> outputMappings)
    {
        if (targets.Count == 0)
        {
            return GuestRenderState.Default;
        }

        var target = targets[0];
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        var blends = new GuestBlendState[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            var blend = DecodeBlendState(registers, targets[index].Slot);
            blends[index] = blend with
            {
                WriteMask = outputMappings[index].ApplyMask(
                    blend.WriteMask &
                        GetPixelColorExportMask(
                            pixelColorExportMasks,
                            targets[index].Slot)),
            };
        }

        return new GuestRenderState(
            blends,
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers),
            DecodeBlendConstant(registers));
    }

    // DB_DEPTH_CONTROL (context register 0x200): Z_ENABLE bit1, Z_WRITE_ENABLE
    // bit2, ZFUNC bits[6:4] (GCN compare, matches Vulkan CompareOp ordering).
    // DB_RENDER_CONTROL (context register 0x000): DEPTH_CLEAR_ENABLE bit0.
    private const uint DbDepthControl = 0x200;

    internal static GuestDepthState DecodeDepthState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var hasDepthControl = registers.TryGetValue(DbDepthControl, out var control);
        registers.TryGetValue(DbRenderControl, out var renderControl);
        var testEnable = (control & 0x2u) != 0;
        var writeEnable = (control & 0x4u) != 0;
        var compareOp = hasDepthControl
            ? (control >> 4) & 0x7u
            : GuestDepthState.Default.CompareOp;
        var clearEnable = (renderControl & 0x1u) != 0;
        return new GuestDepthState(testEnable, writeEnable, compareOp, clearEnable);
    }

    internal static bool TryDecodeHtileMetadataBinding(
        IReadOnlyDictionary<uint, uint> registers,
        out ulong address,
        out uint baseLayer)
    {
        address = 0;
        baseLayer = 0;
        if (!registers.TryGetValue(DbZInfo, out var zInfo) ||
            (zInfo & 0x20000000u) == 0 ||
            !registers.TryGetValue(DbHtileDataBase, out var htileBase))
        {
            return false;
        }

        registers.TryGetValue(DbHtileDataBaseHi, out var htileBaseHi);
        registers.TryGetValue(DbDepthView, out var depthView);
        address =
            ((ulong)(htileBaseHi & 0xFFu) << 40) |
            ((ulong)htileBase << 8);
        baseLayer = depthView & 0x1FFFu;
        return address != 0;
    }

    private static void RegisterActiveHtile(
        SubmittedGpuState gpuState,
        IReadOnlyDictionary<uint, uint> registers)
    {
        if (TryDecodeHtileMetadataBinding(registers, out var address, out _))
        {
            gpuState.HtileMetadata.Register(address);
        }
    }

    private static void MarkHtileMetadataClear(
        SubmittedGpuState gpuState,
        ulong address,
        string source)
    {
        if (!gpuState.HtileMetadata.TryMarkAllLayersCleared(address))
        {
            return;
        }

        if (_traceDepthMetadata &&
            _tracedHtileMetadataMarks.TryAdd((address, source), 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.htile_metadata_mark " +
                $"htile=0x{address:X16} source={source}");
        }
    }

    private static TranslatedGuestDraw ApplyHtileMetadataClear(
        SubmittedGpuState gpuState,
        ulong drawSequence,
        TranslatedGuestDraw draw)
    {
        if (draw.DepthTarget is not { HtileAcceleration: true } depthTarget)
        {
            return draw;
        }

        gpuState.HtileMetadata.Register(depthTarget.HtileAddress);
        if (draw.RenderState.Depth.ClearEnable)
        {
            MarkHtileMetadataClear(
                gpuState,
                depthTarget.HtileAddress,
                "direct-depth-clear");
        }

        if (!gpuState.HtileMetadata.TryConsumeClearedLayer(
                depthTarget.HtileAddress,
                depthTarget.HtileBaseLayer))
        {
            return draw;
        }

        if (_traceDepthMetadata &&
            _tracedHtileMetadataConsumes.TryAdd(
                (depthTarget.Address,
                 depthTarget.HtileAddress,
                 depthTarget.HtileBaseLayer,
                 BitConverter.SingleToUInt32Bits(depthTarget.ClearDepth)),
                0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.htile_metadata_consume " +
                $"seq={drawSequence} depth=0x{depthTarget.Address:X16} " +
                $"htile=0x{depthTarget.HtileAddress:X16} " +
                $"layer={depthTarget.HtileBaseLayer} clear={depthTarget.ClearDepth:R}");
        }

        return draw with
        {
            DepthTarget = depthTarget with { MetadataClear = true },
        };
    }

    private static GuestDepthTarget? DecodeDepthTarget(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var depthState = DecodeDepthState(registers);
        if (!depthState.TestEnable &&
            !depthState.WriteEnable &&
            !depthState.ClearEnable)
        {
            return null;
        }

        if (!registers.TryGetValue(DbZInfo, out var zInfo) ||
            !registers.TryGetValue(DbDepthSizeXy, out var sizeXy))
        {
            return null;
        }

        var guestFormat = zInfo & 0x3u;
        if (guestFormat == 0)
        {
            return null;
        }

        registers.TryGetValue(DbZReadBase, out var readBase);
        registers.TryGetValue(DbZWriteBase, out var writeBase);
        registers.TryGetValue(DbZReadBaseHi, out var readBaseHi);
        registers.TryGetValue(DbZWriteBaseHi, out var writeBaseHi);
        var readAddress = ((ulong)(readBaseHi & 0xFFu) << 40) | ((ulong)readBase << 8);
        var writeAddress = ((ulong)(writeBaseHi & 0xFFu) << 40) | ((ulong)writeBase << 8);
        if (readAddress == 0 && writeAddress == 0)
        {
            return null;
        }

        var width = (sizeXy & 0x3FFFu) + 1;
        var height = ((sizeXy >> 16) & 0x3FFFu) + 1;
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        registers.TryGetValue(DbDepthView, out var depthView);
        registers.TryGetValue(DbHtileDataBase, out var htileBase);
        registers.TryGetValue(DbHtileDataBaseHi, out var htileBaseHi);
        var htileAddress =
            ((ulong)(htileBaseHi & 0xFFu) << 40) |
            ((ulong)htileBase << 8);
        var htileAcceleration =
            htileAddress != 0 && (zInfo & 0x20000000u) != 0;
        var htileBaseLayer = depthView & 0x1FFFu;
        var clearDepth = registers.TryGetValue(DbDepthClear, out var clearBits)
            ? BitConverter.UInt32BitsToSingle(clearBits)
            : 1f;
        if (!float.IsFinite(clearDepth) || clearDepth < 0f || clearDepth > 1f)
        {
            clearDepth = 1f;
        }

        if (_traceDepthMetadata)
        {
            registers.TryGetValue(DbHtileSurface, out var htileSurface);
            registers.TryGetValue(DbDepthControl, out var depthControl);
            registers.TryGetValue(DbRenderControl, out var renderControl);
            var depthAddress = writeAddress != 0 ? writeAddress : readAddress;
            if (_tracedDepthMetadataStates.TryAdd(
                    (depthAddress,
                     htileAddress,
                     zInfo,
                     htileSurface,
                     depthControl,
                     renderControl),
                    0) &&
                Interlocked.Increment(ref _depthMetadataTraceCount) <= 128)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.depth_metadata " +
                    $"depth=0x{depthAddress:X16} htile=0x{htileAddress:X16} " +
                    $"size={width}x{height} z_info=0x{zInfo:X8} " +
                    $"htile_accel={((zInfo & 0x20000000u) != 0 ? 1 : 0)} " +
                    $"surface=0x{htileSurface:X8} control=0x{depthControl:X8} " +
                    $"render_control=0x{renderControl:X8} " +
                    $"direct_clear={(depthState.ClearEnable ? 1 : 0)} " +
                    $"clear={clearDepth:R}");
            }
        }

        return new GuestDepthTarget(
            readAddress,
            writeAddress,
            width,
            height,
            guestFormat,
            (zInfo >> 4) & 0x1Fu,
            clearDepth,
            ReadOnly: (depthView & (1u << 24)) != 0 || writeAddress == 0,
            HtileAddress: htileAddress,
            HtileBaseLayer: htileBaseLayer,
            HtileAcceleration: htileAcceleration);
    }

    // PA_SU_SC_MODE_CNTL (context register 0x205) carries face culling, the
    // front-face winding and polygon (wireframe) mode.
    private const uint PaSuScModeCntl = 0x205;
    private const uint PaSuPolyOffsetDbFmtCntl = 0x2DE;
    private const uint PaSuPolyOffsetClamp = 0x2DF;
    private const uint PaSuPolyOffsetFrontScale = 0x2E0;
    private const uint PaSuPolyOffsetFrontOffset = 0x2E1;
    private const uint PaSuPolyOffsetBackScale = 0x2E2;
    private const uint PaSuPolyOffsetBackOffset = 0x2E3;

    internal static GuestRasterState DecodeRasterState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        if (!registers.TryGetValue(PaSuScModeCntl, out var mode))
        {
            return GuestRasterState.Default;
        }

        var cullFront = (mode & 0x1u) != 0;
        var cullBack = (mode & 0x2u) != 0;
        var frontFaceClockwise = (mode & 0x4u) != 0;
        var polyMode = (mode >> 3) & 0x3u;
        var frontPtype = (mode >> 5) & 0x7u;
        // POLY_MODE != 0 with a line front primitive type renders wireframe.
        var wireframe = polyMode != 0 && frontPtype == 1;
        var useFrontBias = (mode & (1u << 11)) != 0 && !cullFront;
        var useBackBias = (mode & (1u << 12)) != 0 && !cullBack;
        var depthBiasEnable = useFrontBias || useBackBias;
        var scaleRegister = useFrontBias
            ? PaSuPolyOffsetFrontScale
            : PaSuPolyOffsetBackScale;
        var offsetRegister = useFrontBias
            ? PaSuPolyOffsetFrontOffset
            : PaSuPolyOffsetBackOffset;
        registers.TryGetValue(scaleRegister, out var rawScale);
        registers.TryGetValue(offsetRegister, out var rawOffset);
        registers.TryGetValue(PaSuPolyOffsetClamp, out var rawClamp);
        var negNumDbBits = (sbyte)-23;
        var isFloatFormat = true;
        if (registers.TryGetValue(PaSuPolyOffsetDbFmtCntl, out var formatControl))
        {
            negNumDbBits = unchecked((sbyte)(formatControl & 0xFFu));
            isFloatFormat = (formatControl & (1u << 8)) != 0;
        }

        return new GuestRasterState(
            cullFront,
            cullBack,
            frontFaceClockwise,
            wireframe,
            depthBiasEnable,
            BitConverter.Int32BitsToSingle(unchecked((int)rawOffset)),
            BitConverter.Int32BitsToSingle(unchecked((int)rawClamp)),
            BitConverter.Int32BitsToSingle(unchecked((int)rawScale)) / 16f,
            negNumDbBits,
            isFloatFormat);
    }

    /// <summary>CB_BLEND_RED..ALPHA carry the constant blend color as raw
    /// float bits; unwritten registers read as the reset value (0.0).</summary>
    private static GuestBlendConstant DecodeBlendConstant(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(CbBlendRed, out var red);
        registers.TryGetValue(CbBlendGreen, out var green);
        registers.TryGetValue(CbBlendBlue, out var blue);
        registers.TryGetValue(CbBlendAlpha, out var alpha);
        return new GuestBlendConstant(
            BitConverter.Int32BitsToSingle(unchecked((int)red)),
            BitConverter.Int32BitsToSingle(unchecked((int)green)),
            BitConverter.Int32BitsToSingle(unchecked((int)blue)),
            BitConverter.Int32BitsToSingle(unchecked((int)alpha)));
    }

    private static GuestBlendState DecodeBlendState(
        IReadOnlyDictionary<uint, uint> registers,
        uint slot)
    {
        var writeMask = 0xFu;
        if (registers.TryGetValue(CbTargetMask, out var targetMask))
        {
            writeMask = (targetMask >> checked((int)(slot * 4))) & 0xFu;
        }

        registers.TryGetValue(CbBlend0Control + slot, out var control);
        return new GuestBlendState(
            ((control >> 30) & 1u) != 0,
            control & 0x1Fu,
            (control >> 8) & 0x1Fu,
            (control >> 5) & 0x7u,
            (control >> 16) & 0x1Fu,
            (control >> 24) & 0x1Fu,
            (control >> 21) & 0x7u,
            ((control >> 29) & 1u) != 0,
            writeMask);
    }

    private static GuestRect? DecodeScissor(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestRect(0, 0, 0, 0);
        }

        var left = 0;
        var top = 0;
        var right = checked((int)Math.Min(targetWidth, int.MaxValue));
        var bottom = checked((int)Math.Min(targetHeight, int.MaxValue));

        var windowOffsetX = 0;
        var windowOffsetY = 0;
        var enableWindowOffset = true;
        if (registers.TryGetValue(PaScWindowScissorTl, out var windowScissorTl))
        {
            enableWindowOffset = (windowScissorTl & 0x80000000u) == 0;
        }

        if (enableWindowOffset &&
            registers.TryGetValue(PaScWindowOffset, out var windowOffset))
        {
            windowOffsetX = (short)(windowOffset & 0xFFFFu);
            windowOffsetY = (short)(windowOffset >> 16);
        }

        // AGC reset-state blocks can carry an all-zero screen-scissor pair as
        // an unpatched placeholder while the generic/viewport scissors hold
        // the active bounds. Treat only that exact reset value as absent. A
        // nonzero empty rectangle remains meaningful and still clips the draw.
        IntersectScissorPair(
            registers,
            PaScScreenScissorTl,
            PaScScreenScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            ignoreAllZeroPair: true);
        IntersectScissorPair(
            registers,
            PaScWindowScissorTl,
            PaScWindowScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        IntersectScissorPair(
            registers,
            PaScGenericScissorTl,
            PaScGenericScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        var vportScissorEnabled =
            !registers.TryGetValue(PaScModeCntl0, out var modeControl) ||
            ((modeControl >> 1) & 1u) != 0;
        if (vportScissorEnabled)
        {
            IntersectScissorPair(registers, PaScVportScissor0Tl, PaScVportScissor0Br, ref left, ref top, ref right, ref bottom);
        }

        left = Math.Clamp(left, 0, checked((int)targetWidth));
        top = Math.Clamp(top, 0, checked((int)targetHeight));
        right = Math.Clamp(right, left, checked((int)targetWidth));
        bottom = Math.Clamp(bottom, top, checked((int)targetHeight));

        if (left == 0 &&
            top == 0 &&
            right == (int)targetWidth &&
            bottom == (int)targetHeight)
        {
            return null;
        }

        return new GuestRect(
            left,
            top,
            checked((uint)(right - left)),
            checked((uint)(bottom - top)));
    }

    private static GuestViewport? DecodeViewport(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight,
        GuestRect? scissor)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestViewport(0, 0, 0, 0, 0, 1);
        }

        var minDepth = 0f;
        var maxDepth = 1f;
        if (registers.TryGetValue(PaScVportZMin0, out var zMinBits) &&
            registers.TryGetValue(PaScVportZMax0, out var zMaxBits))
        {
            var decodedMin = BitConverter.UInt32BitsToSingle(zMinBits);
            var decodedMax = BitConverter.UInt32BitsToSingle(zMaxBits);
            if (float.IsFinite(decodedMin) &&
                float.IsFinite(decodedMax) &&
                decodedMax > decodedMin)
            {
                minDepth = decodedMin;
                maxDepth = decodedMax;
            }
        }

        if (TryDecodeFiniteFloat(registers, PaClVportXScale, out var xScale) &&
            TryDecodeFiniteFloat(registers, PaClVportXOffset, out var xOffset) &&
            TryDecodeFiniteFloat(registers, PaClVportYScale, out var yScale) &&
            TryDecodeFiniteFloat(registers, PaClVportYOffset, out var yOffset) &&
            xScale > 0f &&
            yScale != 0f)
        {
            return new GuestViewport(
                xOffset - xScale,
                yOffset - yScale,
                xScale * 2f,
                yScale * 2f,
                minDepth,
                maxDepth);
        }

        if (scissor is not { } rect)
        {
            return minDepth == 0f && maxDepth == 1f
                ? null
                : new GuestViewport(0, 0, targetWidth, targetHeight, minDepth, maxDepth);
        }

        return new GuestViewport(
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            minDepth,
            maxDepth);
    }

    private static bool TryDecodeFiniteFloat(
        IReadOnlyDictionary<uint, uint> registers,
        uint register,
        out float value)
    {
        value = 0;
        if (!registers.TryGetValue(register, out var bits))
        {
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return float.IsFinite(value);
    }

    private static void IntersectScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        ref int left,
        ref int top,
        ref int right,
        ref int bottom,
        int offsetX = 0,
        int offsetY = 0,
        bool ignoreAllZeroPair = false)
    {
        if (!TryDecodeScissorPair(
                registers,
                tlRegister,
                brRegister,
                out var pairLeft,
                out var pairTop,
                out var pairRight,
                out var pairBottom,
                out var allZero) ||
            (ignoreAllZeroPair && allZero))
        {
            return;
        }

        pairLeft += offsetX;
        pairTop += offsetY;
        pairRight += offsetX;
        pairBottom += offsetY;

        left = Math.Max(left, pairLeft);
        top = Math.Max(top, pairTop);
        right = Math.Min(right, pairRight);
        bottom = Math.Min(bottom, pairBottom);
    }

    private static bool TryDecodeScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        out int left,
        out int top,
        out int right,
        out int bottom,
        out bool allZero)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        allZero = false;
        if (!registers.TryGetValue(tlRegister, out var tl) ||
            !registers.TryGetValue(brRegister, out var br))
        {
            return false;
        }

        allZero = tl == 0 && br == 0;
        left = (int)(tl & 0x7FFFu);
        top = (int)((tl >> 16) & 0x7FFFu);
        right = (int)(br & 0x7FFFu);
        bottom = (int)((br >> 16) & 0x7FFFu);
        return true;
    }

    internal static void UnregisterHtileMetadataRange(
        ICpuMemory memory,
        ulong address,
        ulong length)
    {
        if (!_submittedGpuStates.TryGetValue(
                CanonicalMemory(memory),
                out var gpuState))
        {
            return;
        }

        gpuState.HtileMetadata.UnregisterRange(address, length);
    }
}
