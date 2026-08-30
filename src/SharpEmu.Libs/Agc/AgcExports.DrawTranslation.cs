// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial builds translated guest draws from submitted AGC graphics state.
public static partial class AgcExports
{
    internal readonly record struct GuestColorMetadataClear(
        byte FillCode,
        uint ClearWord0,
        uint ClearWord1);

    private enum ColorMetadataKind : byte
    {
        None,
        Cmask,
        Dcc,
    }

    private record struct MetaSurfaceInfo(
        ulong MetadataAddress,
        ColorMetadataKind Kind,
        uint ClearWord0,
        uint ClearWord1,
        byte FillCode,
        bool IsCleared);

    private readonly record struct PendingDccFill(ulong ByteCount, byte FillCode);

    private static readonly Dictionary<ulong, MetaSurfaceInfo> _metaSurfaces = new();
    private static readonly Dictionary<ulong, ulong> _dccToColorBuffer = new();
    private static readonly Dictionary<ulong, PendingDccFill> _pendingDccFills = new();
    private static readonly object _metaSurfaceGate = new();
    private static readonly bool _traceMetaSurfaces = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_META_SURFACES"),
        "1",
        StringComparison.Ordinal);
    private static readonly ulong? _traceMetaSurfaceAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_META_SURFACE_ADDRESS"));
    private static readonly HashSet<
        (ulong Surface, ulong Metadata, ColorMetadataKind Kind,
         uint ClearWord0, uint ClearWord1, byte FillCode, bool IsCleared)>
        _tracedMetaRegistrations = new();

    private static readonly HashSet<(ulong Es, ulong Ps, ulong Target, ulong Texture, uint VertexCount)> _tracedShaderDraws = new();
    private static readonly ulong? _traceRenderTargetAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_RENDER_TARGET_ADDRESS"));
    private static long _cbMetadataSkipTraceCount;

    private static readonly HashSet<ulong> _tracedEmptySrtDrawRejects = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedFixedFullscreenClears = new();

    // Concurrent so the per-draw/per-dispatch hit path is lock-free (and no longer
    // shares _submitTraceGate with tracing).
    private static readonly ConcurrentDictionary<
        (ulong Es, uint EsChecksum, ulong EsState,
         ulong Ps, uint PsChecksum, ulong PsState, ulong OutputLayout,
         ulong OutputMappings, uint OutputCount, uint Attributes, uint PsInputEna,
         uint PsInputAddr, ulong PsInputCntl, ulong AliasAlignment),
        (IGuestCompiledShader Vertex, IGuestCompiledShader Pixel)> _graphicsShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Es, uint Checksum, ulong State, ulong AliasAlignment),
        IGuestCompiledShader> _depthOnlyVertexShaderCache = new();

    // Drop a draw on an undecodable texture descriptor instead of substituting
    // a 1x1 fallback binding. Off by default so a garbage descriptor degrades
    // the pass rather than dropping it (Demon's Souls composite feeders).
    private static readonly bool _strictShaderDescriptors = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_STRICT_SHADER_DESCRIPTORS"),
        "1",
        StringComparison.Ordinal);

    private static readonly ulong? _tracePixelShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS"));
    private static readonly ulong? _traceVertexShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_SHADER_ADDRESS"));

    private readonly record struct RenderTargetWriter(
        ulong Sequence,
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint VertexCount,
        uint PrimitiveType);

    private const int IndirectArgsFlushTimeoutMilliseconds = 250;

    private static void FlushGpuWorkForIndirectArgs(SubmittedGpuState gpuState)
    {
        var pending = gpuState.WorkSequence;
        if (pending == 0 || pending > long.MaxValue)
        {
            return;
        }

        GuestGpu.Current.WaitForGuestWork(
            (long)pending,
            IndirectArgsFlushTimeoutMilliseconds);
    }

    private static bool TryReadSubmittedDrawCount(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        out uint drawCount)
    {
        drawCount = 0;
        state.CurrentVertexSnapshot = null;
        if (state.ActiveVertexSnapshots is not null &&
            state.ActiveVertexSnapshots.TryGetValue(packetAddress, out var vertexSnapshot))
        {
            state.CurrentVertexSnapshot = vertexSnapshot;
        }

        switch (op)
        {
            case ItDrawIndexAuto when packetLength >= 3:
                return TryReadUInt32(ctx, packetAddress + 4, out drawCount);
            case ItDrawIndex2 when packetLength >= 6:
                if (!TryReadUInt32(ctx, packetAddress + 4, out var maximumIndexCount) ||
                    !TryReadUInt32(ctx, packetAddress + 8, out var indexBaseLo) ||
                    !TryReadUInt32(ctx, packetAddress + 12, out var indexBaseHi) ||
                    !TryReadUInt32(ctx, packetAddress + 16, out drawCount))
                {
                    return false;
                }

                state.IndexBufferAddress = indexBaseLo | ((ulong)indexBaseHi << 32);
                state.IndexBufferCount = maximumIndexCount;
                state.DrawIndexOffset = 0;
                state.CurrentIndexSnapshot = null;
                if (state.ActiveIndexSnapshots is not null &&
                    state.ActiveIndexSnapshots.TryGetValue(packetAddress, out var snapshot) &&
                    snapshot.SourceAddress == state.IndexBufferAddress &&
                    snapshot.IndexCount == drawCount)
                {
                    state.CurrentIndexSnapshot = snapshot;
                }

                return true;
            case ItDrawIndexOffset2 when packetLength >= 5:
                if (!TryReadUInt32(ctx, packetAddress + 8, out var indexOffset))
                {
                    return false;
                }

                state.DrawIndexOffset = indexOffset;
                return TryReadUInt32(ctx, packetAddress + 12, out drawCount);
            case ItDrawIndexMultiAuto when packetLength >= 4:
                if (!TryReadUInt32(ctx, packetAddress + 12, out var control))
                {
                    return false;
                }

                drawCount = (control >> 21) & 0x7FFu;
                return true;
            case ItDrawIndexIndirectMulti when packetLength >= 8 &&
                state.IndirectArgsAddress != 0:
                if (!TryReadUInt32(ctx, packetAddress + 4, out var multiOffset) ||
                    !TryReadUInt32(ctx, packetAddress + 20, out var multiDraws) ||
                    !TryReadUInt32(ctx, packetAddress + 24, out var multiStride))
                {
                    return false;
                }

                if (multiStride < DrawIndexedIndirectArgsSize)
                {
                    multiStride = DrawIndexedIndirectArgsSize;
                }

                var multiTotal = 0UL;
                var multiCapped = multiDraws == 0
                    ? DrawIndexedIndirectMaxScan
                    : Math.Min(multiDraws, 4096u);
                for (var draw = 0u; draw < multiCapped; draw++)
                {
                    if (!TryReadUInt32(
                            ctx,
                            state.IndirectArgsAddress + multiOffset + ((ulong)draw * multiStride),
                            out var subCount))
                    {
                        break;
                    }

                    if (subCount == 0 && multiDraws == 0)
                    {
                        break;
                    }

                    multiTotal += subCount;
                }

                var multiProbe = Interlocked.Increment(ref _indirectMultiProbeCount);
                if (multiProbe <= 12 || multiProbe % 250 == 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.draw_multi#{multiProbe} args=0x{state.IndirectArgsAddress:X} " +
                        $"off=0x{multiOffset:X} draws={multiDraws} stride={multiStride} " +
                        $"total={multiTotal}");
                }

                drawCount = (uint)Math.Min(multiTotal, uint.MaxValue);
                return drawCount != 0;
            case ItDrawIndirect or ItDrawIndexIndirect:
                var probe = Interlocked.Increment(ref _indirectDrawProbeCount);
                if (!TryReadUInt32(ctx, packetAddress + 4, out var dataOffset))
                {
                    dataOffset = 0xFFFFFFFFu;
                }

                var readable = packetLength >= 5 &&
                    state.IndirectArgsAddress != 0 &&
                    dataOffset != 0xFFFFFFFFu;
                var resolved = readable &&
                    TryReadUInt32(
                        ctx,
                        state.IndirectArgsAddress + dataOffset,
                        out drawCount);
                if (probe <= 12 || probe % 100 == 0)
                {
                    var dump = string.Empty;
                    for (var word = 0; word < 8; word++)
                    {
                        dump += TryReadUInt32(
                            ctx,
                            state.IndirectArgsAddress + dataOffset + ((ulong)word * 4),
                            out var raw)
                            ? $" {raw}"
                            : " ?";
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.draw_indirect#{probe} op=0x{op:X} len={packetLength} " +
                        $"args=0x{state.IndirectArgsAddress:X} off=0x{dataOffset:X} " +
                        $"resolved={resolved} count={drawCount} words:{dump}");
                }

                return resolved;
            default:
                return false;
        }
    }

    private static void TryTranslateGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        uint vertexCount,
        bool indexed)
    {
        var hasExportShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoEs,
            SpiShaderPgmHiEs,
            out var exportShaderAddress);
        var hasPixelShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoPs,
            SpiShaderPgmHiPs,
            out var pixelShaderAddress);
        var hasPsInputEna = state.CxRegisters.TryGetValue(SpiPsInputEna, out var psInputEna);
        var hasPsInputAddr = state.CxRegisters.TryGetValue(SpiPsInputAddr, out var psInputAddr);
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
var renderTargets = GetRenderTargets(state.CxRegisters);
        TrackColorMetadataAddresses(state.CxRegisters, renderTargets);
        var drawSequence = ++gpuState.WorkSequence;
        if (state.PendingTargetlessDraw is { } stalePendingDraw)
        {
            ReturnPooledDrawArrays(
                stalePendingDraw,
                globals: true,
                vertex: true,
                index: true);
            state.PendingTargetlessDraw = null;
        }
        state.TranslatedDraw = null;
        state.GuestDrawKind = GuestDrawKind.None;

        // CB modes EliminateFastClear / FmaskDecompress / DccDecompress run
        // colour-buffer metadata ops. The bound shader is only a vehicle and
        // must not be applied as a normal colour draw.
        if (TryGetCbColorControlMode(state.CxRegisters, out var cbMode) &&
            IsCbMetadataColorMode(cbMode))
        {
            // EliminateFastClear: the game explicitly asks the CB to clear
            // the fast-clear metadata and the colour buffer.
            if (cbMode == (uint)CbColorMode.EliminateFastClear &&
                renderTargets.Count > 0 &&
                renderTargets[0].Address != 0)
            {
                var targetAddr = renderTargets[0].Address;
                bool requestClear;
                lock (_metaSurfaceGate)
                {
                    requestClear =
                        _metaSurfaces.TryGetValue(targetAddr, out var meta) &&
                        meta.IsCleared;
                    if (requestClear)
                    {
                        _metaSurfaces[targetAddr] = meta with { IsCleared = false };
                    }
                }

                if (requestClear)
                {
                    VulkanVideoPresenter.RequestGuestColorClear(targetAddr);
                }
            }

            if (_traceAgcShader || ShouldTraceHotPath(ref _cbMetadataSkipTraceCount))
            {
                TraceAgcShader(
                    $"agc.cb_metadata_skip seq={drawSequence} mode={cbMode} " +
                    $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                    $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16} " +
                    $"vertices={vertexCount}");
            }

            return;
        }

        foreach (var target in renderTargets)
        {
            state.KnownRenderTargets[target.Address] = target;
            // Colour exports originate in the pixel stage.  A depth-only draw
            // can leave old CB registers bound, but it must not become the
            // advertised writer of those surfaces merely because they remain
            // in state.
            if (hasPixelShader)
            {
                state.RenderTargetWriters[target.Address] = new RenderTargetWriter(
                    drawSequence,
                    hasExportShader ? exportShaderAddress : 0,
                    pixelShaderAddress,
                    vertexCount,
                    primitiveType);
            }

            if (_traceAgcShader ||
                _tracePixelShaderAddress == pixelShaderAddress ||
                _traceRenderTargetAddress == target.Address)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"agc.rt_writer seq={drawSequence} target=0x{target.Address:X16} " +
                    $"fmt={target.Format} tile={target.TileMode} " +
                    $"size={target.Width}x{target.Height} vertices={vertexCount} " +
                    $"prim=0x{primitiveType:X} indexed={indexed} " +
                    $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                    $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16} " +
                    $"color_write={(hasPixelShader ? 1 : 0)}");
            }
        }

        if (vertexCount == 0 || vertexCount > 1_048_576)
        {
            return;
        }

        var translationError = string.Empty;
        var depthState = DecodeDepthState(state.CxRegisters);
        var depthTarget = DecodeDepthTarget(
            state.CxRegisters,
            state.CompositeDepthSizeXy);
        var hasDepthOnlyCandidate = hasExportShader &&
            !hasPixelShader &&
            depthTarget is not null &&
            (depthState.TestEnable || depthState.WriteEnable || depthState.ClearEnable);
        if (hasDepthOnlyCandidate &&
            TryCreateTranslatedDepthOnlyGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                vertexCount,
                indexed,
                depthTarget!,
                out var depthOnlyDraw,
                out translationError))
        {
            depthOnlyDraw = ApplyHtileMetadataClear(
                gpuState,
                drawSequence,
                depthOnlyDraw);
            state.TranslatedDraw = depthOnlyDraw;
            var activeDepthTarget = depthOnlyDraw.DepthTarget!;
            var textures = CreateGuestDrawTextures(
                ctx,
                depthOnlyDraw.Textures,
                out _);
            var globalMemoryBuffers =
                CreateTranslatedDrawGlobalBuffers(depthOnlyDraw);
            var vertexBuffers =
                CreateGuestVertexBuffers(depthOnlyDraw.VertexInputs);
            var renderState = depthOnlyDraw.RenderState;
            if (activeDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
            {
                renderState = renderState with
                {
                    Depth = renderState.Depth with { WriteEnable = false },
                };
            }

            TraceDrawCompact(
                drawSequence,
                depthOnlyDraw,
                textures,
                vertexBuffers);
            GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                depthOnlyDraw.PixelShader,
                textures,
                globalMemoryBuffers,
                depthOnlyDraw.AttributeCount,
                activeDepthTarget,
                depthOnlyDraw.VertexShader,
                depthOnlyDraw.VertexCount,
                depthOnlyDraw.InstanceCount,
                depthOnlyDraw.PrimitiveType,
                depthOnlyDraw.IndexBuffer,
                vertexBuffers,
                renderState,
                depthOnlyDraw.PixelShaderAddress,
                depthOnlyDraw.BaseVertex);

            if (_traceAgcShader &&
                (_traceVertexShaderAddress is not { } traceVertexShaderAddress ||
                 exportShaderAddress == traceVertexShaderAddress))
            {
                TraceAgcShader(
                    $"agc.depth_only_draw seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} " +
                    $"depth=0x{activeDepthTarget.Address:X16}:" +
                    $"{activeDepthTarget.Width}x{activeDepthTarget.Height}:" +
                    $"fmt{activeDepthTarget.GuestFormat}/sw{activeDepthTarget.SwizzleMode} " +
                    $"test={(renderState.Depth.TestEnable ? 1 : 0)} " +
                    $"write={(renderState.Depth.WriteEnable ? 1 : 0)} " +
                    $"func={renderState.Depth.CompareOp} ro={(activeDepthTarget.ReadOnly ? 1 : 0)}");
            }

            return;
        }

        var intentionallySkipped = false;
        if (hasExportShader &&
            hasPixelShader &&
            hasPsInputEna &&
            hasPsInputAddr &&
            TryCreateTranslatedGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                pixelShaderAddress,
                psInputEna,
                psInputAddr,
                vertexCount,
                indexed,
                out var translatedDraw,
                out intentionallySkipped,
                out translationError))
        {
            state.TranslatedDraw = translatedDraw;
            if (TryGetHardwareColorResolveTargets(
                    state.CxRegisters,
                    out var resolveSource,
                    out var resolveDestination))
            {
                state.KnownRenderTargets[resolveSource.Address] = resolveSource;
                state.KnownRenderTargets[resolveDestination.Address] = resolveDestination;
                ProvideRenderTargetInitialData(ctx, resolveSource);
                if (GuestGpu.Current.TrySubmitGuestImageBlit(
                        resolveSource.Address,
                        resolveSource.Width,
                        resolveSource.Height,
                        resolveSource.Format,
                        resolveSource.NumberType,
                        resolveDestination.Address,
                        resolveDestination.Width,
                        resolveDestination.Height,
                        resolveDestination.Format,
                        resolveDestination.NumberType))
                {
                    state.RenderTargetWriters[resolveDestination.Address] =
                        new RenderTargetWriter(
                            drawSequence,
                            exportShaderAddress,
                            pixelShaderAddress,
                            vertexCount,
                            primitiveType);
                    TraceAgcShader(
                        $"agc.hardware_color_resolve seq={drawSequence} " +
                        $"src=0x{resolveSource.Address:X16}:" +
                        $"{resolveSource.Width}x{resolveSource.Height}:" +
                        $"fmt{resolveSource.Format}/num{resolveSource.NumberType} " +
                        $"dst=0x{resolveDestination.Address:X16}:" +
                        $"{resolveDestination.Width}x{resolveDestination.Height}:" +
                        $"fmt{resolveDestination.Format}/num{resolveDestination.NumberType}");
                    ReturnPooledDrawArrays(
                        translatedDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.TranslatedDraw = null;
                    return;
                }

                TraceAgcShader(
                    $"agc.hardware_color_resolve_unavailable seq={drawSequence} " +
                    $"src=0x{resolveSource.Address:X16} " +
                    $"dst=0x{resolveDestination.Address:X16}");
            }

            // A DCC fast clear writes metadata only; the colour block discards
            // the quad's shaded output. Reset the attachment and drop the draw,
            // which reproduces the observable effect of a clear to zero without
            // modelling DCC block state.
            if (translatedDraw.IsDccFastClear)
            {
                foreach (var target in translatedDraw.GuestTargets)
                {
                    if (target.Address != 0)
                    {
                        VulkanVideoPresenter.RequestGuestColorClear(target.Address);
                    }
                }

                ReturnPooledDrawArrays(
                    translatedDraw,
                    globals: true,
                    vertex: true,
                    index: true);
                state.TranslatedDraw = null;
                return;
            }

            // DbRenderControl CLEARON (bit0): when set, the CB clears color
            // targets on first draw. Handle color targets (depth is already
            // handled by DecodeDepthState).
            if (state.CxRegisters.TryGetValue(DbRenderControl, out var rc) && (rc & 0x1u) != 0)
            {
                foreach (var rt in translatedDraw.RenderTargets)
                {
                    if (rt.Address != 0)
                    {
                        VulkanVideoPresenter.RequestGuestColorClear(rt.Address);
                    }
                }
            }

            // CMASK fast clear: CB_COLORn_INFO.FAST_CLEAR (bit12) set on
            // one or more targets. The CB clears via CMASK before the draw
            // writes; mark targets for clear-on-first-use.
            if (IsCmaskFastClearDraw(state.CxRegisters, translatedDraw.RenderTargets))
            {
                foreach (var rt in translatedDraw.RenderTargets)
                {
                    if (rt.Address != 0)
                    {
                        VulkanVideoPresenter.RequestGuestColorClear(rt.Address);
                    }
                }
            }

            // Read diagnostic snapshots before submit. Submit gives the pooled
            // arrays to the presenter, which can return them while parsing
            // continues.
            if (_traceAgcShader &&
                (_traceVertexShaderAddress is not { } traceVertexShaderAddress ||
                 exportShaderAddress == traceVertexShaderAddress))
            {
                lock (_submitTraceGate)
                {
                    var firstTraceTarget = translatedDraw.RenderTargets.FirstOrDefault();
                    var firstTextureAddress = translatedDraw.Textures.FirstOrDefault()?.Descriptor.Address ?? 0;
                    if (_tracedShaderDraws.Add(
                            (exportShaderAddress,
                             pixelShaderAddress,
                             firstTraceTarget.Address,
                             firstTextureAddress,
                             vertexCount)))
                    {
                        TraceTranslatedGuestDraw(
                            ctx,
                            gpuState,
                            state,
                            translatedDraw,
                            psInputEna,
                            psInputAddr);
                    }
                }
            }

            var firstTarget = translatedDraw.RenderTargets.FirstOrDefault();
            if (firstTarget.Address != 0)
            {
                if (!translatedDraw.IsFullscreenColorClear)
                {
                    translatedDraw = ApplyHtileMetadataClear(
                        gpuState,
                        drawSequence,
                        translatedDraw);
                    state.TranslatedDraw = translatedDraw;
                }

                // Submit all color targets in one host MRT pass. The depth
                // attachment is therefore loaded or cleared once for this
                // guest draw.
                var drawRenderTargets = translatedDraw.RenderTargets;
                var lastTargetIndex = 0;
                for (var targetIndex = 1; targetIndex < drawRenderTargets.Count; targetIndex++)
                {
                    if (drawRenderTargets[targetIndex].Address != 0)
                    {
                        lastTargetIndex = targetIndex;
                    }
                }

                var sharedTextures = CreateGuestDrawTextures(
                    ctx,
                    translatedDraw.Textures,
                    out _);
                var sharedGlobalMemoryBuffers =
                    CreateTranslatedDrawGlobalBuffers(translatedDraw);
                var sharedVertexBuffers =
                    CreateGuestVertexBuffers(translatedDraw.VertexInputs);
                TraceRectListVertices(translatedDraw, sharedVertexBuffers);
                TraceGrassDrawVertices(translatedDraw, sharedTextures, sharedVertexBuffers);
                TraceDrawCompact(
                    drawSequence,
                    translatedDraw,
                    sharedTextures,
                    sharedVertexBuffers);
                foreach (var renderTarget in drawRenderTargets)
                {
                    if (renderTarget.Address != 0)
                    {
                        ProvideRenderTargetInitialData(ctx, renderTarget);
                    }
                }

                if (translatedDraw.IsFullscreenColorClear)
                {
                    VulkanVideoPresenter.SubmitOffscreenColorClear(
                        translatedDraw.GuestTargets,
                        translatedDraw.ClearRed,
                        translatedDraw.ClearGreen,
                        translatedDraw.ClearBlue,
                        translatedDraw.ClearAlpha,
                        translatedDraw.PixelShaderAddress);
                }
                else
                {
                    GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                        translatedDraw.PixelShader,
                        sharedTextures,
                        sharedGlobalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        translatedDraw.GuestTargets,
                        translatedDraw.VertexShader,
                        translatedDraw.VertexCount,
                        translatedDraw.InstanceCount,
                        translatedDraw.PrimitiveType,
                        translatedDraw.IndexBuffer,
                        sharedVertexBuffers,
                        translatedDraw.RenderState,
                        translatedDraw.DepthTarget,
                        translatedDraw.PixelShaderAddress,
                        translatedDraw.BaseVertex);
                }
            }
            else
            {
                if (translatedDraw.DepthTarget is { } translatedDepthTarget)
                {
                    translatedDraw = ApplyHtileMetadataClear(
                        gpuState,
                        drawSequence,
                        translatedDraw);
                    state.TranslatedDraw = translatedDraw;
                    translatedDepthTarget = translatedDraw.DepthTarget!;
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        translatedDraw.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(translatedDraw);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(translatedDraw.VertexInputs);
                    var renderState = translatedDraw.RenderState;
                    if (translatedDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
                    {
                        renderState = renderState with
                        {
                            Depth = renderState.Depth with { WriteEnable = false },
                        };
                    }

                    TraceDrawCompact(
                        drawSequence,
                        translatedDraw,
                        textures,
                        vertexBuffers);
                    GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        translatedDepthTarget,
                        translatedDraw.VertexShader,
                        translatedDraw.VertexCount,
                        translatedDraw.InstanceCount,
                        translatedDraw.PrimitiveType,
                        translatedDraw.IndexBuffer,
                        vertexBuffers,
                        renderState,
                        translatedDraw.PixelShaderAddress,
                        translatedDraw.BaseVertex);
                }
                else
                {
                    var storageTarget = translatedDraw.Textures
                        .FirstOrDefault(binding => binding.IsStorage);
                    if (storageTarget is not null)
                    {
                        var textures = CreateGuestDrawTextures(
                            ctx,
                            translatedDraw.Textures,
                            out _);
                        var globalMemoryBuffers =
                            CreateTranslatedDrawGlobalBuffers(translatedDraw);
                        TraceDrawCompact(drawSequence, translatedDraw, textures, []);
                        GuestGpu.Current.SubmitStorageTranslatedDraw(
                            translatedDraw.PixelShader,
                            textures,
                            globalMemoryBuffers,
                            translatedDraw.AttributeCount,
                            storageTarget.Descriptor.Width,
                            storageTarget.Descriptor.Height,
                            translatedDraw.PixelShaderAddress);
                        // The storage submit consumes the global buffers (the
                        // presenter returns them) but never the vertex/index
                        // arrays; return those here so they don't leak the pool.
                        ReturnPooledDrawArrays(
                            translatedDraw,
                            globals: false,
                            vertex: true,
                            index: true);
                    }
                    else
                    {
                        if (translatedDraw.Textures.Count != 0)
                        {
                            // Unity's PS5 final blit can omit CB registers and
                            // rely on the following AGC flip to name the scanout
                            // target. Retain that sampled draw until RFlip, then
                            // enqueue it against the known display surface before
                            // the ordered capture.
                            state.PendingTargetlessDraw = translatedDraw;
                        }
                        else
                        {
                            // No render target, storage sink or sampled source:
                            // nothing can consume this draw.
                            ReturnPooledDrawArrays(
                                translatedDraw,
                                globals: true,
                                vertex: true,
                                index: true);
                        }
                    }
                }
            }

            if (ShouldTraceHotPath(ref _translatedDrawTraceCount))
            {
                TraceAgcShader(
                    $"agc.shader_draw_seen seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
                    $"target=0x{firstTarget.Address:X16}:{firstTarget.Width}x{firstTarget.Height}:fmt{firstTarget.Format}/tile{firstTarget.TileMode} " +
                    $"textures={translatedDraw.Textures.Count}");
            }

            return;
        }

        if (intentionallySkipped)
        {
            return;
        }

        TraceDrawCompactMiss(
            drawSequence,
            vertexCount,
            hasExportShader && hasPixelShader
                ? translationError
                : hasDepthOnlyCandidate && !string.IsNullOrEmpty(translationError)
                    ? $"depth-only: {translationError}"
                : $"missing-shaders es={hasExportShader} ps={hasPixelShader} ena={hasPsInputEna} addr={hasPsInputAddr}");
        TraceShaderTranslationMiss(
            ctx,
            state,
            vertexCount,
            hasExportShader,
            exportShaderAddress,
            hasPixelShader,
            pixelShaderAddress,
            hasPsInputEna,
            psInputEna,
            hasPsInputAddr,
            psInputAddr,
            hasExportShader && hasPixelShader || hasDepthOnlyCandidate
                ? translationError
                : null);
    }

    private static bool TryCreateTranslatedDepthOnlyGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        uint vertexCount,
        bool indexed,
        GuestDepthTarget depthTarget,
        out TranslatedGuestDraw draw,
        out string error)
    {
        draw = default!;
        error = string.Empty;
        ulong exportShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
        }

        TryRegisterEmbeddedFusedProgram(ctx, exportShaderAddress, exportShaderHeader);
        state.ShRegisters.TryGetValue(SpiShaderPgmChksumGs, out var exportShaderChecksum);

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out error,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase,
                shaderChecksum: exportShaderChecksum) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    out var depthVertexRecords)
                        ? depthVertexRecords
                        : null))
        {
            return false;
        }

        var exportFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var cacheKey = (
            exportShaderAddress,
            exportState.ShaderChecksum,
            exportFingerprint,
            _storageBufferOffsetAlignment);
        _depthOnlyVertexShaderCache.TryGetValue(cacheKey, out var vertexShader);

        if (vertexShader is null)
        {
            var guestGlobalBufferCount = exportEvaluation.GlobalMemoryBindings.Count;
            // CreateTranslatedDrawGlobalBuffers appends both stage scalar
            // blocks.  The pixel block is unused by the fixed fragment stage;
            // the vertex block remains at guestCount+1, matching this layout.
            var totalGlobalBufferCount = _bakeScalars
                ? guestGlobalBufferCount
                : guestGlobalBufferCount + 2;
            if (!GuestGpu.Current.TryCompileVertexShader(
                    exportState,
                    exportEvaluation,
                    out vertexShader,
                    out error,
                    globalBufferBase: 0,
                    totalGlobalBufferCount: totalGlobalBufferCount,
                    imageBindingBase: 0,
                    scalarRegisterBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount + 1,
                    requiredVertexOutputCount: 0,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                return false;
            }

            DumpCompiledShader(
                "depth-vs",
                exportShaderAddress,
                exportFingerprint,
                vertexShader!,
                exportState.Program);
            GuestGpu.Current.CountShaderCompilation();
            _depthOnlyVertexShaderCache.TryAdd(cacheKey, vertexShader!);
        }

        var textures = new List<TranslatedImageBinding>(
            exportEvaluation.ImageBindings.Count);
        foreach (var binding in exportEvaluation.ImageBindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                if (_strictShaderDescriptors)
                {
                    error = $"invalid export texture descriptor at pc=0x{binding.Pc:X}";
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    return false;
                }

                texture = CreateFallbackTextureDescriptor(
                    binding.ResourceDescriptor,
                    binding.Control.Dimension);
            }

            textures.Add(new TranslatedImageBinding(
                texture,
                Gen5ShaderTranslator.RequiresStorageImage(
                    binding,
                    exportEvaluation.ImageBindings),
                binding.MipLevel ?? 0,
                NormalizeSamplerDescriptorForImageOperation(
                    binding.SamplerDescriptor),
                Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
        }

        IReadOnlyList<Gen5VertexInputBinding> vertexInputs =
            exportEvaluation.VertexInputs ?? [];
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var syntheticTarget = new RenderTargetDescriptor(
            Slot: 0,
            Address: 0,
            depthTarget.Width,
            depthTarget.Height,
            Format: 0,
            NumberType: 0,
            ComponentSwap: 0,
            TileMode: 0);
        var renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
        {
            // A guest pass without a pixel shader has no colour exports.  The
            // presenter uses a private compatibility attachment, so disable
            // all writes to it and expose only the persistent DB result.
            Blends = [GuestBlendState.Default with { WriteMask = 0 }],
        };
        if (depthTarget.Width == 1 &&
            depthTarget.Height == 1 &&
            renderState.Viewport is { } depthViewport)
        {
            var inferredWidth = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Width)),
                1f,
                16384f);
            var inferredHeight = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Height)),
                1f,
                16384f);
            if (inferredWidth > 1 || inferredHeight > 1)
            {
                depthTarget = depthTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                syntheticTarget = syntheticTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
                {
                    Blends = [GuestBlendState.Default with { WriteMask = 0 }],
                };
            }
        }
        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            PixelShaderAddress: 0,
            primitiveType,
            vertexShader!,
            GuestGpu.Current.GetDepthOnlyFragmentShader(),
            AttributeCount: 0,
            vertexCount,
            state.InstanceCount,
            GetBaseVertex(state),
            indexed ? CreateGuestIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            exportEvaluation.GlobalMemoryBindings,
            vertexInputs,
            RenderTargets: [],
            depthTarget,
            GuestTargets: [],
            renderState,
            PixelUserData: [],
            RawBlendControl: 0,
            RawColorInfo: 0,
            PixelInitialScalars: [],
            exportEvaluation.InitialScalarRegisters);
        return true;
    }

    private static bool TryCreateTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        uint psInputEna,
        uint psInputAddr,
        uint vertexCount,
        bool indexed,
        out TranslatedGuestDraw draw,
        out bool intentionallySkipped,
        out string error)
    {
        draw = default!;
        intentionallySkipped = false;
        error = string.Empty;
        ulong exportShaderHeader;
        ulong pixelShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
            _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
        }

        TryRegisterEmbeddedFusedProgram(ctx, exportShaderAddress, exportShaderHeader);
        state.ShRegisters.TryGetValue(SpiShaderPgmChksumGs, out var exportShaderChecksum);
        state.ShRegisters.TryGetValue(SpiShaderPgmChksumPs, out var pixelShaderChecksum);

        // Sequential (not short-circuited into one condition) so a failure
        // after an evaluation succeeded can return that evaluation's pooled
        // buffer arrays to the pool instead of leaking them.
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out error,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase,
                shaderChecksum: exportShaderChecksum))
        {
            return false;
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    out var vertexRecords)
                        ? vertexRecords
                        : null))
        {
            return false;
        }

        ApplySubmittedVertexSnapshot(
            state,
            exportShaderAddress,
            ref exportEvaluation);

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                pixelShaderAddress,
                pixelShaderHeader,
                state.ShRegisters,
                PsTextureUserDataRegister,
                out var pixelState,
                out error,
                shaderChecksum: pixelShaderChecksum))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                pixelState,
                out var pixelEvaluation,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        // Empty SRT/EUD is fine for clears/passthroughs that bind nothing
        // (Astro title PS 0x808E88000 is a procedural fullscreen clear).
        // Reject only when evaluation produced image/global slots that
        // collapsed to Address-0 — that layout mismatches SPIR-V and loses
        // the device on QueueSubmit.
        if (pixelState.Metadata is
            {
                ShaderResourceTableSizeDwords: 0,
                ExtendedUserDataSizeDwords: 0,
            } ||
            Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(
                pixelShaderAddress))
        {
            var hasAnyImageSlot = pixelEvaluation.ImageBindings.Count > 0;
            var hasUsablePixelImage = false;
            foreach (var binding in pixelEvaluation.ImageBindings)
            {
                if (TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture) &&
                    texture.Address != 0)
                {
                    hasUsablePixelImage = true;
                    break;
                }
            }

            var hasUsablePixelGlobal = pixelEvaluation.GlobalMemoryBindings.Any(
                static binding => binding.BaseAddress != 0);
            var hasPoisonImageSlots = hasAnyImageSlot && !hasUsablePixelImage;
            if (hasPoisonImageSlots && !hasUsablePixelGlobal)
            {
                error = Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(
                    pixelShaderAddress)
                    ? "empty-srt-scalar-pointer-fallback"
                    : "empty-srt-no-usable-resources";
                lock (_submitTraceGate)
                {
                    if (_tracedEmptySrtDrawRejects.Add(pixelShaderAddress))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject ps=0x{pixelShaderAddress:X16} " +
                            $"es=0x{exportShaderAddress:X16} reason={error}");
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject_state ps=0x{pixelShaderAddress:X16} " +
                            $"header=0x{pixelShaderHeader:X16} " +
                            Gen5ShaderTranslator.DescribeState(pixelState));
                        var shDump = new List<string>(16);
                        for (uint reg = 0x8; reg <= 0x1C; reg++)
                        {
                            if (state.ShRegisters.TryGetValue(reg, out var value))
                            {
                                shDump.Add($"0x{reg:X}={value:X8}");
                            }
                        }

                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject_sh ps=0x{pixelShaderAddress:X16} " +
                            $"[{string.Join(',', shDump)}]");
                        var bindingIndex = 0;
                        foreach (var binding in pixelEvaluation.ImageBindings)
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] agc.draw_reject_binding ps=0x{pixelShaderAddress:X16} " +
                                $"[{bindingIndex++}] pc=0x{binding.Pc:X} op={binding.Opcode} " +
                                $"resource={FormatShaderDwords(binding.ResourceDescriptor)} " +
                                $"sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
                        }

                        foreach (var binding in pixelEvaluation.GlobalMemoryBindings)
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] agc.draw_reject_global ps=0x{pixelShaderAddress:X16} " +
                                $"s{binding.ScalarAddress} base=0x{binding.BaseAddress:X16} " +
                                $"bytes={binding.DataLength}");
                        }
                    }
                }

                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS") == "1")
        {
            TraceAstroTitlePixelGlobals(pixelEvaluation);
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS_LIVE") == "1")
        {
            TraceAstroTitlePixelGlobalProbe(pixelEvaluation);
        }

        // Patch BufferFormat from the attrib table onto the V# before host
        // vertex input. IR discovery often keeps a stale float format from the
        // unpatched sharp — that turns UI glyphs into gradient triangles.
        // Match by stride+offset (not bare base address) so interleaved streams
        // keep loading-video bindings intact.
        if (exportEvaluation.VertexInputs is { Count: > 0 } discoveredInputs &&
            AgcVertexMetadata.TryGetVertexTableRegisters(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                out var vertexTables))
        {
            var resolvedVertexTables =
                AgcVertexMetadata.AddUserDataScalarRegisterBase(
                    vertexTables,
                    exportState.UserDataScalarRegisterBase);
            var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
                ctx,
                exportEvaluation.InitialScalarRegisters,
                resolvedVertexTables,
                exportState.Program,
                discoveredInputs);
            if (!ReferenceEquals(merged, discoveredInputs))
            {
                TraceAgcShader(
                    $"agc.vertex_metadata_format es=0x{exportShaderAddress:X16} " +
                    $"count={merged.Count}");
                exportEvaluation = exportEvaluation with { VertexInputs = merged };
            }
        }

        var attributeCount = GetInterpolatedAttributeCount(pixelState);
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        if (AgcPrimitiveHelpers.ShouldSkipRectListWithoutParameterExports(
                primitiveType,
                indexed,
                exportEvaluation.VertexInputs?.Count ?? 0,
                exportState.Program.ParameterExportMask,
                attributeCount))
        {
            intentionallySkipped = true;
            if (_traceAgcShader)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.rect_list_skip " +
                    $"es=0x{exportShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
                    $"ps_inputs={attributeCount}");
            }

            ReturnPooledEvaluationArrays(exportEvaluation);
            ReturnPooledEvaluationArrays(pixelEvaluation);
            return false;
        }

        // Every bound color target the shader exports to. Deferred renderers
        // draw a multi-render-target G-buffer (up to eight slots) in one pass.
        // Fall back to slot 0 if we cannot match any export to a bound target.
        var pixelColorExportMasks = pixelState.Program.PixelColorExportMasks;
        var allBoundTargets = GetRenderTargets(state.CxRegisters);
        // At most 8 slots; a manual filter avoids the per-draw LINQ iterator/
        // closure allocations. Slots are distinct, so sorting by slot is stable.
        var selectedTargets = new List<RenderTargetDescriptor>(allBoundTargets.Count);
        foreach (var target in allBoundTargets)
        {
            if (GetPixelColorExportMask(pixelColorExportMasks, target.Slot) != 0)
            {
                selectedTargets.Add(target);
            }
        }

        if (selectedTargets.Count == 0)
        {
            foreach (var target in allBoundTargets)
            {
                if (target.Slot == 0)
                {
                    selectedTargets.Add(target);
                }
            }
        }

        selectedTargets.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        var renderTargets = selectedTargets.ToArray();
        if (_traceAgcShader && allBoundTargets.Count > 1)
        {
            TraceAgcShader(
                $"agc.mrt_filter ps=0x{pixelShaderAddress:X16} " +
                $"bound=[{string.Join(",", allBoundTargets.Select(t => $"s{t.Slot}:0x{t.Address:X}:exp{(GetPixelColorExportMask(pixelColorExportMasks, t.Slot) != 0 ? 1 : 0)}"))}] " +
                 $"kept={renderTargets.Length}");
        }

        var renderTargetOutputKinds = new Gen5PixelOutputKind[renderTargets.Length];
        var renderTargetOutputMappings = new Gen5ColorComponentMapping[renderTargets.Length];
        for (var index = 0; index < renderTargets.Length; index++)
        {
            var target = renderTargets[index];
            if (!GuestGpu.Current.TryGetRenderTargetOutputInfo(
                    target.Format,
                    target.NumberType,
                    target.ComponentSwap,
                    out renderTargetOutputKinds[index],
                    out renderTargetOutputMappings[index]))
            {
                error =
                    $"unsupported color target format={target.Format} " +
                    $"number_type={target.NumberType} component_swap={target.ComponentSwap}";
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }
        }

        // Exact packed encoding of the output layout — guest slot (6 bits, CB targets are
        // 0-7) plus output kind (2 bits) per target, host locations being the sequential
        // byte positions. Replaces a per-draw LINQ + string build that allocated on every
        // draw, cache hit or not; the target count disambiguates trailing zero bytes.
        var outputLayout = 0UL;
        for (var index = 0; index < renderTargets.Length; index++)
        {
            outputLayout |= (ulong)(((renderTargets[index].Slot & 0x3Fu) << 2) |
                (uint)renderTargetOutputKinds[index]) << (index * 8);
        }
        var outputMappings = PackPixelOutputMappings(renderTargetOutputMappings);

        var exportStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var pixelStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(pixelEvaluation)
            : ComputeShaderStructuralFingerprint(pixelEvaluation);
        var psInputCntl = ReadPsInputCntlRegisters(state.CxRegisters);
        var psInputCntlFingerprint = ComputePsInputCntlFingerprint(psInputCntl);
        var shaderKey = (
            exportShaderAddress,
            exportState.ShaderChecksum,
            exportStateFingerprint,
            pixelShaderAddress,
            pixelState.ShaderChecksum,
            pixelStateFingerprint,
            outputLayout,
            outputMappings,
            (uint)renderTargets.Length,
            attributeCount,
            psInputEna,
            psInputAddr,
            psInputCntlFingerprint,
            _storageBufferOffsetAlignment);

        var guestGlobalBuffers =
            pixelEvaluation.GlobalMemoryBindings.Count +
            exportEvaluation.GlobalMemoryBindings.Count;
        // Two per-draw initial-scalar buffers ride after the guest buffers:
        // [pixel guest][vertex guest][pixel sgprs][vertex sgprs].
        var totalGlobalBuffers = _bakeScalars
            ? guestGlobalBuffers
            : guestGlobalBuffers + 2;
        _graphicsShaderCache.TryGetValue(shaderKey, out var compiled);
        var usedFixedFullscreenClear = false;
        (float Red, float Green, float Blue, float Alpha) fullscreenClearColor = default;

        if (compiled.Vertex is null || compiled.Pixel is null)
        {
            if (IsProceduralFullscreenClearPair(
                    exportState,
                    exportEvaluation,
                    pixelState,
                    pixelEvaluation))
            {
                // Title ES/PS clear (0x808E88D00/0x808E88000): empty SRT/EUD.
                // Gen5→SPIR-V and even fixed fragment pipelines have lost the
                // device on the 2432x1368 offscreen submit. Apply the solid
                // clear via CmdClearColorImage so the pass still runs without
                // Address-0 descriptors or a graphics pipeline.
                usedFixedFullscreenClear = true;
                fullscreenClearColor = DecodeSolidClearColor(pixelEvaluation);
                lock (_submitTraceGate)
                {
                    if (_tracedFixedFullscreenClears.Add(
                            (exportShaderAddress, pixelShaderAddress)))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.shader_color_clear " +
                            $"es=0x{exportShaderAddress:X16} " +
                            $"ps=0x{pixelShaderAddress:X16} " +
                            $"rgba=({fullscreenClearColor.Red:0.###}," +
                            $"{fullscreenClearColor.Green:0.###}," +
                            $"{fullscreenClearColor.Blue:0.###}," +
                            $"{fullscreenClearColor.Alpha:0.###})");
                    }
                }

                compiled = (
                    GuestGpu.Current.GetDepthOnlyFragmentShader(),
                    GuestGpu.Current.GetDepthOnlyFragmentShader());
                _graphicsShaderCache.TryAdd(shaderKey, compiled);
            }
            else
            {
                var pixelOutputs = new Gen5PixelOutputBinding[renderTargets.Length];
                for (var location = 0; location < renderTargets.Length; location++)
                {
                    pixelOutputs[location] = new Gen5PixelOutputBinding(
                        renderTargets[location].Slot,
                        (uint)location,
                        renderTargetOutputKinds[location],
                        renderTargetOutputMappings[location]);
                }

                if (!GuestGpu.Current.TryCompilePixelShader(
                        pixelState,
                        pixelEvaluation,
                        pixelOutputs,
                        out var pixelShader,
                        out error,
                        globalBufferBase: 0,
                        totalGlobalBufferCount: totalGlobalBuffers,
                        imageBindingBase: 0,
                        scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers,
                        pixelInputEnable: psInputEna,
                        pixelInputAddress: psInputAddr,
                        pixelInputCntl: psInputCntl,
                        storageBufferOffsetAlignment:
                            _storageBufferOffsetAlignment) ||
                    !GuestGpu.Current.TryCompileVertexShader(
                        exportState,
                        exportEvaluation,
                        out var vertexShader,
                        out error,
                        globalBufferBase: pixelEvaluation.GlobalMemoryBindings.Count,
                        totalGlobalBufferCount: totalGlobalBuffers,
                        imageBindingBase: pixelEvaluation.ImageBindings.Count,
                        scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                        requiredVertexOutputCount: (int)GetInterpolatedAttributeCount(pixelState),
                        storageBufferOffsetAlignment:
                            _storageBufferOffsetAlignment))
                {
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    ReturnPooledEvaluationArrays(pixelEvaluation);
                    return false;
                }

                compiled = (vertexShader!, pixelShader!);
                DumpCompiledShader(
                    "vs",
                    exportShaderAddress,
                    exportStateFingerprint,
                    compiled.Vertex,
                    exportState.Program);
                DumpCompiledShader(
                    "ps",
                    pixelShaderAddress,
                    pixelStateFingerprint,
                    compiled.Pixel,
                    pixelState.Program);
                GuestGpu.Current.CountShaderCompilation();
                _graphicsShaderCache.TryAdd(shaderKey, compiled);
            }
        }
        else if (IsCachedFixedFullscreenClearPair(
                     exportState,
                     exportEvaluation,
                     pixelState,
                     pixelEvaluation))
        {
            usedFixedFullscreenClear = true;
            fullscreenClearColor = DecodeSolidClearColor(pixelEvaluation);
        }

        var useFixedFullscreenClear = usedFixedFullscreenClear;

        List<TranslatedImageBinding> textures;
        Gen5GlobalMemoryBinding[] globalMemoryBindings;
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs;
        if (useFixedFullscreenClear)
        {
            textures = [];
            globalMemoryBindings = [];
            vertexInputs = [];
        }
        else
        {
            var imageBindings = pixelEvaluation.ImageBindings
                .Concat(exportEvaluation.ImageBindings)
                .ToArray();
            textures = new List<TranslatedImageBinding>(
                pixelEvaluation.ImageBindings.Count +
                exportEvaluation.ImageBindings.Count);
            if (!TryAppendTranslatedImageBindings(
                    pixelEvaluation.ImageBindings,
                    imageBindings,
                    textures,
                    pixelShaderAddress,
                    exportShaderAddress,
                    out error) ||
                !TryAppendTranslatedImageBindings(
                    exportEvaluation.ImageBindings,
                    imageBindings,
                    textures,
                    pixelShaderAddress,
                    exportShaderAddress,
                    out error))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }

            globalMemoryBindings = new Gen5GlobalMemoryBinding[
                pixelEvaluation.GlobalMemoryBindings.Count +
                exportEvaluation.GlobalMemoryBindings.Count];
            for (var index = 0; index < pixelEvaluation.GlobalMemoryBindings.Count; index++)
            {
                globalMemoryBindings[index] = pixelEvaluation.GlobalMemoryBindings[index];
            }
            for (var index = 0; index < exportEvaluation.GlobalMemoryBindings.Count; index++)
            {
                globalMemoryBindings[pixelEvaluation.GlobalMemoryBindings.Count + index] =
                    exportEvaluation.GlobalMemoryBindings[index];
            }

            vertexInputs = exportEvaluation.VertexInputs ?? [];
        }

        var guestTargets = new GuestRenderTarget[renderTargets.Length];
        for (var index = 0; index < renderTargets.Length; index++)
        {
            guestTargets[index] = CreateGuestRenderTarget(renderTargets[index]);
        }

        var pixelUserDataCount = Math.Min(pixelEvaluation.InitialScalarRegisters.Count, 8);
        var pixelUserData = new uint[pixelUserDataCount];
        for (var index = 0; index < pixelUserDataCount; index++)
        {
            pixelUserData[index] = pixelEvaluation.InitialScalarRegisters[index];
        }

        var depthTarget = DecodeDepthTarget(
            state.CxRegisters,
            state.CompositeDepthSizeXy);
        var decodedRenderState = renderTargets.Length == 0 && depthTarget is not null
            ? CreateDepthTargetRenderState(state.CxRegisters, depthTarget)
            : CreateRenderState(
                state.CxRegisters,
                renderTargets,
                pixelColorExportMasks,
                renderTargetOutputMappings);
        var renderState = ApplyTransparentPremultipliedFillClear(
            decodedRenderState,
            textures,
            vertexInputs,
            pixelEvaluation.InitialScalarRegisters);

        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            pixelShaderAddress,
            primitiveType,
            compiled.Vertex,
            compiled.Pixel,
            GetInterpolatedAttributeCount(pixelState),
            vertexCount,
            state.InstanceCount,
            GetBaseVertex(state),
            indexed ? CreateGuestIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            globalMemoryBindings,
            vertexInputs,
            renderTargets,
            depthTarget,
            guestTargets,
            renderState,
            pixelUserData,
            state.CxRegisters.TryGetValue(CbBlend0Control, out var rawBlend) ? rawBlend : 0,
            state.CxRegisters.TryGetValue(
                CbColor0Info + renderTargets.FirstOrDefault().Slot * CbColorRegisterStride,
                out var rawInfo)
                ? rawInfo
                : 0,
            pixelEvaluation.InitialScalarRegisters,
            exportEvaluation.InitialScalarRegisters,
            useFixedFullscreenClear,
            fullscreenClearColor.Red,
            fullscreenClearColor.Green,
            fullscreenClearColor.Blue,
            fullscreenClearColor.Alpha,
            IsDccFastClearDraw(
                state.CxRegisters,
                renderTargets,
                textures,
                vertexInputs,
                renderState,
                primitiveType,
                vertexCount));
        return true;
    }

    private static bool TryAppendTranslatedImageBindings(
        IReadOnlyList<Gen5ImageBinding> bindings,
        IReadOnlyList<Gen5ImageBinding> stageBindings,
        List<TranslatedImageBinding> textures,
        ulong pixelShaderAddress,
        ulong exportShaderAddress,
        out string error)
    {
        foreach (var binding in bindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                // A garbage/zeroed texture descriptor (from a per-draw descriptor
                // setup race — the same root as scalar-load-failed) would drop
                // the whole draw, so deferred-lighting/composite passes that
                // produce the composite's feeder targets never run. Keep the
                // existing 1x1 fallback unless strict diagnostics are requested.
                if (_strictShaderDescriptors)
                {
                    error = $"invalid texture descriptor at pc=0x{binding.Pc:X}";
                    return false;
                }

                texture = CreateFallbackTextureDescriptor(
                    binding.ResourceDescriptor,
                    binding.Control.Dimension);
            }

            var isStorage = Gen5ShaderTranslator.RequiresStorageImage(
                binding,
                stageBindings);
            if (_traceAgcShader || _tracePixelShaderAddress == pixelShaderAddress)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"agc.texture_binding ps=0x{pixelShaderAddress:X16} es=0x{exportShaderAddress:X16} " +
                    $"pc=0x{binding.Pc:X} op={binding.Opcode} storage={(isStorage ? 1 : 0)} " +
                    $"decoded={FormatTextureDescriptor(texture)} " +
                    $"raw={FormatShaderDwords(binding.ResourceDescriptor)} sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
            }
            textures.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    binding.MipLevel ?? 0,
                NormalizeSamplerDescriptorForImageOperation(
                    binding.SamplerDescriptor),
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
        }

        error = string.Empty;
        return true;
    }

    private static int _tracedAstroTitlePixelGlobals;
    private static int _tracedAstroTitlePixelGlobalProbe;

    private static void TraceAstroTitlePixelGlobalProbe(Gen5ShaderEvaluation evaluation)
    {
        const int probeOffset = 17216;
        var draw = Interlocked.Increment(ref _tracedAstroTitlePixelGlobalProbe);
        foreach (var (binding, index) in evaluation.GlobalMemoryBindings.Select((value, index) => (value, index)))
        {
            if (probeOffset + 16 > binding.DataLength)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[TITLE-GLOBALS-LIVE] draw={draw} binding={index} " +
                $"base=0x{binding.BaseAddress:X16} offset=0x{probeOffset:X} " +
                $"bytes={Convert.ToHexString(binding.Data.AsSpan(probeOffset, 16))}");
        }
    }

    private static void TraceAstroTitlePixelGlobals(Gen5ShaderEvaluation evaluation)
    {
        if (Interlocked.Exchange(ref _tracedAstroTitlePixelGlobals, 1) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[TITLE-GLOBALS] initial_s0_31=" +
            string.Join(',', evaluation.InitialScalarRegisters
                .Take(32)
                .Select((value, index) => $"s{index}={value:X8}")));

        var probeOffsets = new[]
        {
            0, 16, 24, 32, 48,
            192, 256, 400, 432,
            17100, 17104, 17136, 17168, 17184, 17200, 17216,
        };
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Console.Error.WriteLine(
                $"[TITLE-GLOBALS] binding s{binding.ScalarAddress} " +
                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
            foreach (var offset in probeOffsets)
            {
                if (offset < 0 || offset + 16 > binding.DataLength)
                {
                    continue;
                }

                Console.Error.WriteLine(
                    $"[TITLE-GLOBALS] s{binding.ScalarAddress}+0x{offset:X}=" +
                    Convert.ToHexString(binding.Data.AsSpan(offset, 16)));
            }
        }
    }

    private static bool IsCachedFixedFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation) =>
        IsProceduralFullscreenClearPair(
            exportState,
            exportEvaluation,
            pixelState,
            pixelEvaluation);

    private static bool IsProceduralFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation)
    {
        if ((exportEvaluation.VertexInputs?.Count ?? 0) != 0 ||
            exportEvaluation.ImageBindings.Count != 0 ||
            pixelEvaluation.ImageBindings.Count != 0 ||
            exportEvaluation.GlobalMemoryBindings.Count != 0 ||
            pixelEvaluation.GlobalMemoryBindings.Count != 0)
        {
            return false;
        }

        if (!HasExportTarget(exportState, target: 12) ||
            !HasExportTarget(pixelState, target: 0))
        {
            return false;
        }

        if (pixelState.Program.Instructions.Count is 0 or > 8 ||
            exportState.Program.Instructions.Count is 0 or > 48)
        {
            return false;
        }

        return pixelState.Program.Instructions.All(IsBenignClearPixelInstruction) &&
               exportState.Program.Instructions.All(IsBenignProceduralVertexInstruction);
    }

    private static bool HasExportTarget(Gen5ShaderState state, uint target) =>
        state.Program.Instructions.Any(instruction =>
            instruction.Control is Gen5ExportControl export &&
            export.Target == target);

    private static bool IsBenignClearPixelInstruction(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "VMovB32" ||
        instruction.Control is Gen5ExportControl { Target: 0 };

    private static bool IsBenignProceduralVertexInstruction(Gen5ShaderInstruction instruction)
    {
        if (instruction.Control is Gen5BufferMemoryControl or
            Gen5ImageControl or
            Gen5GlobalMemoryControl or
            Gen5ScalarMemoryControl)
        {
            return false;
        }

        if (instruction.Control is Gen5ExportControl export)
        {
            // Position (12) plus ignored NGG/param exports.
            return export.Target is 12 or (>= 13 and < 32) or 20;
        }

        return instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "SSendmsg" or
            "VMovB32" or
            "VAndB32" or
            "VAddI32" or
            "VLshlrevB32" or
            "VCvtF32I32" or
            "VCvtF32U32" ||
            instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk or
                Gen5ShaderEncoding.Sopp;
    }

    private static (float Red, float Green, float Blue, float Alpha) DecodeSolidClearColor(
        Gen5ShaderEvaluation pixelEvaluation)
    {
        // Default opaque white; guest clear shaders often mov a 1.0 literal into v0.
        float red = 1f, green = 1f, blue = 1f, alpha = 1f;
        if (pixelEvaluation.InitialScalarRegisters.Count > 0)
        {
            var bits = pixelEvaluation.InitialScalarRegisters[0];
            if (bits != 0)
            {
                red = green = blue = alpha = BitConverter.UInt32BitsToSingle(bits);
                if (!float.IsFinite(red) || red < 0f || red > 4f)
                {
                    red = green = blue = alpha = 1f;
                }
            }
        }

        return (red, green, blue, alpha);
    }

    private static readonly bool _fillClearHack = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_FILL_CLEAR"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Treat an untextured fill that outputs pure transparent black through
    /// premultiplied blending as an overwrite. Chowdren issues exactly this
    /// draw once per frame to reset its effect layers (fog smoke, vignette
    /// masks); under the blend factors it sets (One, OneMinusSrcAlpha) a
    /// (0,0,0,0) source is a mathematical no-op, so without this the layers
    /// accumulate until they saturate and the fog composites as a flat veil
    /// over the whole scene. The workaround applies only when every MRT
    /// attachment uses the same blend pattern. Disable with
    /// SHARPEMU_DISABLE_FILL_CLEAR=1.
    /// </summary>
    private static GuestRenderState ApplyTransparentPremultipliedFillClear(
        GuestRenderState renderState,
        IReadOnlyList<TranslatedImageBinding> textures,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        IReadOnlyList<uint> pixelUserData)
    {
        if (!_fillClearHack ||
            textures.Count != 0 ||
            vertexInputs.Count != 0 ||
            pixelUserData.Count < 4 ||
            !renderState.Blends.All(IsTransparentPremultipliedFillBlend))
        {
            return renderState;
        }

        for (var index = 0; index < 4; index++)
        {
            // Positive or negative zero.
            if ((pixelUserData[index] & 0x7FFF_FFFFu) != 0)
            {
                return renderState;
            }
        }

        return renderState with
        {
            Blends = renderState.Blends
                .Select(blend => blend with { Enable = false })
                .ToArray(),
        };
    }

    /// <summary>
    /// Recognises the covering quad a GFX10 driver issues to clear a
    /// DCC-compressed colour target. There is no clear packet: the driver
    /// programs CB_COLORn_CLEAR_WORD0/1 and draws a quad that the colour block
    /// turns into DCC clear codes, discarding whatever the pixel shader
    /// exported. Executing it as an ordinary draw writes the shaded output
    /// instead, and because the blend it uses computes
    /// <c>a &lt;- a_src + a_dst * (1 - a_src)</c> - fixed point 1 - the target's
    /// alpha then climbs every frame and saturates.
    ///
    /// Restricted to clear-to-zero. The reset performed for a match clears the
    /// attachment to zero, so a nonzero CLEAR_WORD would be cleared to the
    /// wrong colour; those fall through and are drawn. Zero is zero under every
    /// encoding the register can carry, so the pair needs no format handling.
    ///
    /// The clip-space test is load-bearing rather than belt-and-braces: fills
    /// sharing the vertex count, topology and blend outnumber the clears by two
    /// orders of magnitude and sit at coordinates well outside the frame.
    /// </summary>
    private const uint TriangleStripPrimitive = 6;

    // A float32x3 vertex position stream (BUF_DATA_FORMAT_32_32_32 / FLOAT).
    private const uint PositionDataFormat = 13;
    private const uint PositionNumberFormat = 7;

    private static bool IsDccFastClearDraw(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> renderTargets,
        IReadOnlyList<TranslatedImageBinding> textures,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        GuestRenderState renderState,
        uint primitiveType,
        uint vertexCount)
    {
        if (textures.Count != 0 ||
            vertexCount != 4 ||
            primitiveType != TriangleStripPrimitive ||
            renderTargets.Count == 0 ||
            renderState.Blends.Count == 0 ||
            !renderState.Blends.All(IsTransparentPremultipliedFillBlend))
        {
            return false;
        }

        var slotStride = renderTargets[0].Slot * CbColorRegisterStride;
        return registers.TryGetValue(CbColor0Info + slotStride, out var info) &&
            (info & CbColorInfoDccEnableMask) != 0 &&
            registers.TryGetValue(CbColor0ClearWord0 + slotStride, out var clearWord0) &&
            registers.TryGetValue(CbColor0ClearWord1 + slotStride, out var clearWord1) &&
            clearWord0 == 0 &&
            clearWord1 == 0 &&
            CoversClipSpace(vertexInputs, vertexCount);
    }

    /// <summary>
    /// GFX10 CMASK fast clear: CB_COLORn_INFO.FAST_CLEAR (bit 12) set on
    /// one or more targets. The CB clears via CMASK before the draw writes;
    /// mark targets for clear-on-first-use. Unlike DCC, the draw content
    /// is written rather than dropped.
    /// </summary>
    private static bool IsCmaskFastClearDraw(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> renderTargets)
    {
        foreach (var rt in renderTargets)
        {
            var stride = rt.Slot * CbColorRegisterStride;
            if (registers.TryGetValue(CbColor0Info + stride, out var info) &&
                (info & CbColorInfoFastClearEnableMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Registers the active colour metadata mapping for each colour buffer.
    /// A metadata fill can arrive before the first draw binds its target, so
    /// pending DCC fills are adopted when the mapping becomes known.
    /// </summary>
    private static void TrackColorMetadataAddresses(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> renderTargets)
    {
        foreach (var rt in renderTargets)
        {
            var stride = rt.Slot * CbColorRegisterStride;

            // CMASK metadata address (legacy GCN path).
            var cmaskRegAddr = CbColor0Cmask + stride;
            registers.TryGetValue(cmaskRegAddr, out var cmaskLow);
            var cmaskExtAddr = CbColor0CmaskBaseExt + rt.Slot;
            registers.TryGetValue(cmaskExtAddr, out var cmaskExt);
            var cmaskAddress = ((ulong)(cmaskExt & 0xFFu) << 40) |
                               ((ulong)(cmaskLow & 0x1FFFFFFFu) << 8);

            // DCC metadata address (GFX10+ primary path).
            var dccRegAddr = CbColor0DccBase + stride;
            registers.TryGetValue(dccRegAddr, out var dccLow);
            var dccExtAddr = CbColor0DccBaseExt + rt.Slot;
            registers.TryGetValue(dccExtAddr, out var dccExt);
            var dccAddress = ((ulong)(dccExt & 0xFFu) << 40) |
                             ((ulong)(dccLow & 0x1FFFFFFFu) << 8);

            registers.TryGetValue(CbColor0Info + stride, out var colorInfo);
            var dccEnabled = (colorInfo & CbColorInfoDccEnableMask) != 0;
            var kind = dccEnabled && dccAddress != 0
                ? ColorMetadataKind.Dcc
                : cmaskAddress != 0
                    ? ColorMetadataKind.Cmask
                    : ColorMetadataKind.None;
            var metaAddress = kind switch
            {
                ColorMetadataKind.Dcc => dccAddress,
                ColorMetadataKind.Cmask => cmaskAddress,
                _ => 0UL,
            };

            var cw0Addr = CbColor0ClearWord0 + stride;
            var cw1Addr = CbColor0ClearWord1 + stride;
            registers.TryGetValue(cw0Addr, out var cw0);
            registers.TryGetValue(cw1Addr, out var cw1);

            lock (_metaSurfaceGate)
            {
                _pendingDccFills.Remove(rt.Address);

                var preserved = _metaSurfaces.TryGetValue(rt.Address, out var previous) &&
                    previous.IsCleared &&
                    previous.MetadataAddress == metaAddress &&
                    previous.Kind == kind;
                var fillCode = preserved ? previous.FillCode : (byte)0;
                var isCleared = preserved;

                if (kind == ColorMetadataKind.Dcc &&
                    _pendingDccFills.Remove(metaAddress, out var pending))
                {
                    fillCode = pending.FillCode;
                    isCleared = true;
                }

                if (_metaSurfaces.TryGetValue(rt.Address, out previous) &&
                    previous.Kind == ColorMetadataKind.Dcc &&
                    previous.MetadataAddress != metaAddress)
                {
                    _dccToColorBuffer.Remove(previous.MetadataAddress);
                }

                _metaSurfaces[rt.Address] = new MetaSurfaceInfo(
                    metaAddress,
                    kind,
                    cw0,
                    cw1,
                    fillCode,
                    isCleared);
                if (kind == ColorMetadataKind.Dcc)
                {
                    _dccToColorBuffer[metaAddress] = rt.Address;
                }

                if (ShouldTraceMetaSurface(rt.Address, metaAddress) &&
                    _tracedMetaRegistrations.Add(
                        (rt.Address, metaAddress, kind, cw0, cw1, fillCode, isCleared)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] agc.meta_register " +
                        $"surface=0x{rt.Address:X16} meta=0x{metaAddress:X16} " +
                        $"kind={kind.ToString().ToLowerInvariant()} " +
                        $"clear=0x{cw1:X8}{cw0:X8} pending={isCleared} " +
                        $"code=0x{fillCode:X2}");
                }
            }
        }
    }

    private static bool IsRecognizedDccFill(uint fillValue, out byte fillCode)
    {
        fillCode = (byte)fillValue;
        if (fillValue != (uint)fillCode * 0x01010101u)
        {
            return false;
        }

        return fillCode is 0x00 or 0x20 or 0x40 or 0x80 or 0xC0;
    }

    /// <summary>
    /// Records a uniform DCC metadata fill. The fill may precede render-target
    /// registration, so an unmatched address remains pending until the target
    /// registers its DCC base.
    /// </summary>
    private static void RecordDccFill(
        ulong writeAddress,
        ulong byteCount,
        uint fillValue)
    {
        if (writeAddress == 0 ||
            byteCount == 0 ||
            !IsRecognizedDccFill(fillValue, out var fillCode))
        {
            return;
        }

        lock (_metaSurfaceGate)
        {
            ulong surfaceAddress = 0;
            MetaSurfaceInfo metadata = default;
            var registered = _dccToColorBuffer.TryGetValue(writeAddress, out surfaceAddress) &&
                _metaSurfaces.TryGetValue(surfaceAddress, out metadata) &&
                metadata.Kind == ColorMetadataKind.Dcc &&
                metadata.MetadataAddress == writeAddress;
            if (registered)
            {
                _metaSurfaces[surfaceAddress] = metadata with
                {
                    FillCode = fillCode,
                    IsCleared = true,
                };
            }
            else
            {
                _pendingDccFills[writeAddress] = new PendingDccFill(byteCount, fillCode);
            }

            if (ShouldTraceMetaSurface(surfaceAddress, writeAddress))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.meta_fill " +
                    $"meta=0x{writeAddress:X16} bytes={byteCount} " +
                    $"code=0x{fillCode:X2} " +
                    $"surface=0x{surfaceAddress:X16} " +
                    $"state={(registered ? "registered" : "pending")}");
            }
        }
    }

    internal static bool TryPeekColorMetadataClear(
        ulong colorBufferAddress,
        out GuestColorMetadataClear clear)
    {
        lock (_metaSurfaceGate)
        {
            if (_metaSurfaces.TryGetValue(colorBufferAddress, out var metadata) &&
                metadata.Kind == ColorMetadataKind.Dcc &&
                metadata.IsCleared)
            {
                clear = new GuestColorMetadataClear(
                    metadata.FillCode,
                    metadata.ClearWord0,
                    metadata.ClearWord1);
                return true;
            }
        }

        clear = default;
        return false;
    }

    internal static bool TryConsumeColorMetadataClear(
        ulong colorBufferAddress,
        GuestColorMetadataClear expected)
    {
        lock (_metaSurfaceGate)
        {
            if (!_metaSurfaces.TryGetValue(colorBufferAddress, out var metadata) ||
                metadata.Kind != ColorMetadataKind.Dcc ||
                !metadata.IsCleared ||
                metadata.FillCode != expected.FillCode ||
                metadata.ClearWord0 != expected.ClearWord0 ||
                metadata.ClearWord1 != expected.ClearWord1)
            {
                return false;
            }

            _metaSurfaces[colorBufferAddress] = metadata with { IsCleared = false };
            if (ShouldTraceMetaSurface(colorBufferAddress, metadata.MetadataAddress))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.meta_consume " +
                    $"surface=0x{colorBufferAddress:X16} " +
                    $"meta=0x{metadata.MetadataAddress:X16} " +
                    $"code=0x{metadata.FillCode:X2}");
            }

            return true;
        }
    }

    private static bool ShouldTraceMetaSurface(
        ulong surfaceAddress,
        ulong metadataAddress) =>
        _traceMetaSurfaces &&
        (!_traceMetaSurfaceAddress.HasValue ||
         _traceMetaSurfaceAddress.Value == surfaceAddress ||
         _traceMetaSurfaceAddress.Value == metadataAddress);

    /// <summary>
    /// True when the draw's float32x3 position stream spans the full clip
    /// rectangle, i.e. x and y both reach -1 and +1.
    /// </summary>
    private static bool CoversClipSpace(
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        uint vertexCount)
    {
        const float Tolerance = 0.001f;
        foreach (var input in vertexInputs)
        {
            if (input.DataFormat != PositionDataFormat ||
                input.NumberFormat != PositionNumberFormat)
            {
                continue;
            }

            var stride = input.Stride == 0 ? 12u : input.Stride;
            var available = Math.Min(input.DataLength, input.Data.Length);
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            var seen = 0;
            for (var vertex = 0u; vertex < vertexCount; vertex++)
            {
                var at = (int)(input.OffsetBytes + (vertex * stride));
                if (at + 12 > available)
                {
                    break;
                }

                var position = input.Data.AsSpan(at);
                var x = BitConverter.ToSingle(position);
                var y = BitConverter.ToSingle(position[4..]);
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    return false;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                seen++;
            }

            return seen >= 3 &&
                minX <= -1f + Tolerance && maxX >= 1f - Tolerance &&
                minY <= -1f + Tolerance && maxY >= 1f - Tolerance;
        }

        return false;
    }

    private static bool IsTransparentPremultipliedFillBlend(GuestBlendState blend) =>
        blend is
        {
            Enable: true,
            ColorSrcFactor: 1,
            ColorDstFactor: 5,
            ColorFunc: 0,
        };

    /// <summary>
    /// Guest storage buffers for a translated draw, followed by the per-draw
    /// initial scalar registers of each stage (pixel then vertex), matching
    /// the binding layout the shaders were compiled against.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffers(
        TranslatedGuestDraw translatedDraw)
    {
        var buffers = CreateGuestMemoryBuffers(translatedDraw.GlobalMemoryBindings);
        if (_bakeScalars)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(buffers.Count + 2);
        combined.AddRange(buffers);
        var runtimeStateLength = GetRuntimeScalarBufferLength(
            translatedDraw.GlobalMemoryBindings.Count);
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                translatedDraw.PixelInitialScalars,
                translatedDraw.GlobalMemoryBindings),
            runtimeStateLength,
            Pooled: true));
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                translatedDraw.VertexInitialScalars,
                translatedDraw.GlobalMemoryBindings),
            runtimeStateLength,
            Pooled: true));
        return combined;
    }

    private static IReadOnlyList<GuestMemoryBuffer>
        CreateGlobalBufferOwnershipView(
            IReadOnlyList<GuestMemoryBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestMemoryBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    /// <summary>
    /// Present-time variant: the flip path can reuse the same translated
    /// draw across several flips and swapchain retries, so it must not wrap
    /// the (pooled, single-consumption) binding arrays. Buffer contents are
    /// re-read from guest memory instead, which also presents current data.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffersForPresent(
        CpuContext ctx,
        TranslatedGuestDraw translatedDraw)
    {
        var bindings = translatedDraw.GlobalMemoryBindings;
        var combined = new List<GuestMemoryBuffer>(bindings.Count + 2);
        foreach (var binding in bindings)
        {
            var data = new byte[Math.Max(binding.DataLength, sizeof(uint))];
            var guestMemoryBacked = binding.BaseAddress != 0 &&
                (ctx.Memory.TryRead(binding.BaseAddress, data) ||
                 KernelMemoryCompatExports.TryReadTrackedLibcHeap(binding.BaseAddress, data));
            if (!guestMemoryBacked)
            {
                // Keep the zero-filled buffer; layout must match the shader.
            }

            combined.Add(new GuestMemoryBuffer(
                binding.BaseAddress,
                data,
                data.Length,
                Pooled: false,
                Writable: binding.Writable,
                WriteBackToGuest: binding.WriteBackToGuest && guestMemoryBacked));
        }

        if (!_bakeScalars)
        {
            var runtimeStateLength = GetRuntimeScalarBufferLength(bindings.Count);
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.PixelInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.VertexInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
        }

        return combined;
    }

    private static ulong ComputePsInputCntlFingerprint(ReadOnlySpan<uint> cntl)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var value in cntl)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }

    private static uint GetInterpolatedAttributeCount(Gen5ShaderState state)
    {
        var maxAttribute = -1;
        foreach (var instruction in state.Program.Instructions)
        {
            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                maxAttribute = Math.Max(maxAttribute, (int)interpolation.Attribute);
            }
        }

        return (uint)(maxAttribute + 1);
    }
}
