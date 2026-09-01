// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial records translated-draw diagnostics, including compiled-shader dumps.
public static partial class AgcExports
{
    private static readonly HashSet<(ulong Es, ulong Ps, GuestDrawKind Kind)> _tracedShaderTranslations = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedShaderDecodePairs = new();
    private static readonly HashSet<(ulong Ps, string Error)> _tracedShaderFailures = new();

    private static bool _tracedMissingPixelShaderBindings;
    private static long _shaderTranslationMissTraceCount;
    private static long _translatedDrawTraceCount;

    private static readonly HashSet<ulong> _renderTargetAddresses = new();
    private static readonly HashSet<ulong> _sampledRenderTargets = new();
    private static readonly object _renderTargetProbeGate = new();
    private static long _renderTargetSampleTraceCount;
    private static long _indirectDrawProbeCount;
    private static long _indirectMultiProbeCount;
    private static readonly ulong? _traceDrawDetailEs = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAW_DETAIL_ES"));
    private static readonly uint? _traceDrawDetailCount = uint.TryParse(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAW_DETAIL_COUNT"),
        out var traceDrawDetailCount)
        ? traceDrawDetailCount
        : null;
    private static int _drawDetailTraceCount;
    private static readonly object _videoDrawChainGate = new();
    private static readonly Dictionary<ulong, (int Depth, long Frame)> _videoDrawChainTargets = new();
    private static long _videoDrawChainFrame;

    private static void NoteRenderTargetAddress(ulong address)
    {
        if (address == 0)
        {
            return;
        }

        lock (_renderTargetProbeGate)
        {
            if (_renderTargetAddresses.Count < 512)
            {
                _renderTargetAddresses.Add(address);
            }
        }
    }

    private static void NoteSampledAddress(ulong address, uint format = 0, uint numberType = 0)
    {
        if (address == 0)
        {
            return;
        }

        bool firstTime;
        int distinctTargets;
        lock (_renderTargetProbeGate)
        {
            if (!_renderTargetAddresses.Contains(address))
            {
                return;
            }

            firstTime = _sampledRenderTargets.Add(address);
            distinctTargets = _renderTargetAddresses.Count;
        }

        var count = Interlocked.Increment(ref _renderTargetSampleTraceCount);
        if (firstTime || count % 2000 == 0)
        {
            var gpuResident = GuestGpu.Current.IsGpuGuestImageAvailable(address, format, numberType);
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.rt_sampled#{count} addr=0x{address:X} first={firstTime} " +
                $"gpu_resident={gpuResident} fmt={format}/{numberType} known_targets={distinctTargets}");
        }
    }

    private static void TraceTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        TranslatedGuestDraw draw,
        uint psInputEna,
        uint psInputAddr)
    {
        var targets = draw.RenderTargets.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.RenderTargets.Select(target =>
                    $"{target.Slot}:0x{target.Address:X16}:{target.Width}x{target.Height}:" +
                    $"fmt{target.Format}/num{target.NumberType}/tile{target.TileMode}"));
        var depthTarget = draw.DepthTarget is { } depth
            ? $"0x{depth.Address:X16}:{depth.Width}x{depth.Height}:" +
              $"fmt{depth.GuestFormat}/sw{depth.SwizzleMode}:" +
              $"read=0x{depth.ReadAddress:X16}/write=0x{depth.WriteAddress:X16}:" +
              $"clear={depth.ClearDepth:0.######}/ro={(depth.ReadOnly ? 1 : 0)}"
            : "none";
        var probes = new Dictionary<ulong, string>();
        var textures = string.Join(
            ',',
            draw.Textures.Select(binding =>
            {
                var texture = binding.Descriptor;
                var targetSlot = draw.RenderTargets
                    .FirstOrDefault(target => target.Address == texture.Address)
                    .Slot;
                var target = draw.RenderTargets.Any(candidate => candidate.Address == texture.Address)
                    ? $"/rt{targetSlot}"
                    : string.Empty;
                if (!probes.TryGetValue(texture.Address, out var probe))
                {
                    probe = ProbeTexture(ctx, texture);
                    probes.Add(texture.Address, probe);
                }

                state.RenderTargetWriters.TryGetValue(texture.Address, out var sourceWriter);
                gpuState.ComputeImageWriters.TryGetValue(texture.Address, out var computeWriter);
                var writer = sourceWriter.Sequence >= computeWriter.Sequence && sourceWriter.Sequence != 0
                    ? $"/writer={sourceWriter.Sequence}:" +
                      $"es0x{sourceWriter.ExportShaderAddress:X}:" +
                      $"ps0x{sourceWriter.PixelShaderAddress:X}:" +
                      $"v{sourceWriter.VertexCount}:prim0x{sourceWriter.PrimitiveType:X}"
                    : computeWriter.Sequence != 0
                        ? $"/compute={computeWriter.Sequence}:" +
                          $"cs0x{computeWriter.ShaderAddress:X}:{computeWriter.Opcode}"
                        : "/writer=none";
                return
                    $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                    $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                    $"/storage={binding.IsStorage}{target}/{probe}{writer}";
            }));
        var buffers = string.Join(
            ',',
            draw.GlobalMemoryBindings.Select((binding, index) =>
                $"{index}:0x{binding.BaseAddress:X16}:{binding.DataLength}:" +
                Convert.ToHexString(binding.Data.AsSpan(0, Math.Min(binding.DataLength, 256)))));
        var indices = draw.IndexBuffer is { } indexBuffer
            ? $"{(indexBuffer.Is32Bit ? 32 : 16)}:" +
              Convert.ToHexString(indexBuffer.Data.AsSpan(0, Math.Min(indexBuffer.Length, 32)))
            : "none";
        var vertexInputs = draw.VertexInputs.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.VertexInputs.Select(input =>
                    $"{input.Location}:pc=0x{input.Pc:X}:0x{input.BaseAddress:X16}" +
                    $":stride{input.Stride}:off{input.OffsetBytes}:c{input.ComponentCount}" +
                    $":fmt{input.DataFormat}/num{input.NumberFormat}"));
        var scissor = draw.RenderState.Scissor is { } drawScissor
            ? $"{drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height}"
            : "full";
        var viewport = draw.RenderState.Viewport is { } drawViewport
            ? $"{drawViewport.X:0.###},{drawViewport.Y:0.###}," +
              $"{drawViewport.Width:0.###}x{drawViewport.Height:0.###}:" +
              $"{drawViewport.MinDepth:0.###}-{drawViewport.MaxDepth:0.###}"
            : "full";
        var rasterRegisters = new (string Name, uint Offset)[]
        {
            ("screen_tl", PaScScreenScissorTl),
            ("screen_br", PaScScreenScissorBr),
            ("window_off", PaScWindowOffset),
            ("window_tl", PaScWindowScissorTl),
            ("window_br", PaScWindowScissorBr),
            ("generic_tl", PaScGenericScissorTl),
            ("generic_br", PaScGenericScissorBr),
            ("vport_tl", PaScVportScissor0Tl),
            ("vport_br", PaScVportScissor0Br),
            ("mode", PaScModeCntl0),
            ("xscale", PaClVportXScale),
            ("xoffset", PaClVportXOffset),
            ("yscale", PaClVportYScale),
            ("yoffset", PaClVportYOffset),
        };
        var raster = string.Join(
            ',',
            rasterRegisters.Select(entry =>
                state.CxRegisters.TryGetValue(entry.Offset, out var value)
                    ? $"{entry.Name}=0x{value:X8}"
                    : $"{entry.Name}=missing"));
        var blend = draw.RenderState.Blend;
        var rectExpanded = AgcPrimitiveHelpers.GetRectListDrawVertexCount(
            draw.PrimitiveType,
            draw.VertexCount,
            indexed: draw.IndexBuffer is not null,
            hasVertexBuffers: draw.VertexInputs.Count > 0);
        TraceAgcShader(
            $"agc.shader_draw es=0x{draw.ExportShaderAddress:X16} " +
            $"ps=0x{draw.PixelShaderAddress:X16} spirv={draw.PixelShader.Payload.Length} " +
            $"primitive=0x{draw.PrimitiveType:X} verts={draw.VertexCount}->{rectExpanded} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc} " +
            $"write_mask=0x{blend.WriteMask:X} scissor={scissor} viewport={viewport} " +
            $"raster=[{raster}] " +
            $"ps_ena=0x{psInputEna:X8} ps_addr=0x{psInputAddr:X8} " +
            $"targets=[{targets}] depth=[{depthTarget}] textures=[{textures}] " +
            $"buffers=[{buffers}] vertex=[{vertexInputs}] indices=[{indices}]");
    }

    private static void TraceDrawCompact(
        ulong sequence,
        SubmittedDcbState state,
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        TraceVideoDrawChain(sequence, draw, textures);
        TraceDrawOracle(sequence, state, draw);
        if (!_traceDraws)
        {
            return;
        }

        var target = draw.RenderTargets.FirstOrDefault();
        var blend = draw.RenderState.Blend;
        var viewport = draw.RenderState.Viewport is { } vp
            ? $"{vp.X:0.#},{vp.Y:0.#},{vp.Width:0.#}x{vp.Height:0.#}"
            : "none";
        var textureList = string.Join(
            '|',
            textures.Select(texture =>
                $"0x{texture.Address:X}:{texture.Width}x{texture.Height}" +
                $":f{texture.Format}/n{texture.NumberType}/d{texture.DstSelect:X3}" +
                (texture.IsFallback ? ":FALLBACK" : string.Empty)));
        var positions = string.Empty;
        var positionBuffer = vertexBuffers.FirstOrDefault(buffer => buffer.Location == 0);
        if (positionBuffer is { Length: >= 8 })
        {
            var stride = Math.Max(positionBuffer.Stride, 4u);
            var vertexTotal = (int)((positionBuffer.Length - positionBuffer.OffsetBytes) / stride);
            var sampled = new List<string>();
            foreach (var vertex in new[] { 0, 1, vertexTotal - 1 })
            {
                var baseOffset = (int)(positionBuffer.OffsetBytes + vertex * stride);
                if (vertex < 0 || baseOffset + 8 > positionBuffer.Length)
                {
                    continue;
                }

                sampled.Add(
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset):0.##}," +
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset + 4):0.##}");
            }

            positions = string.Join(';', sampled);
        }

        Console.Error.WriteLine(
            $"[DRAW] seq={sequence} es=0x{draw.ExportShaderAddress:X} ps=0x{draw.PixelShaderAddress:X} " +
            $"target=0x{target.Address:X}:{target.Width}x{target.Height}:f{target.Format}/n{target.NumberType} " +
            $"prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} indexed={draw.IndexBuffer is not null} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc}" +
            $":a{blend.AlphaSrcFactor}/{blend.AlphaDstFactor}/{blend.AlphaFunc}/s{(blend.SeparateAlphaBlend ? 1 : 0)} " +
            $"mask=0x{blend.WriteMask:X} viewport={viewport} textures={textureList} pos={positions} " +
            $"ps_s0..3={string.Join(',', draw.PixelUserData.Take(4).Select(value => BitConverter.UInt32BitsToSingle(value).ToString("0.###")))} " +
            $"rawblend=0x{draw.RawBlendControl:X8} info=0x{draw.RawColorInfo:X8}");
    }

    private static void TraceDrawOracle(
        ulong sequence,
        SubmittedDcbState state,
        TranslatedGuestDraw draw)
    {
        if (!_traceDrawOracle)
        {
            return;
        }

        const ulong fnvOffset = 14695981039346656037UL;
        var targetHash = fnvOffset;
        foreach (var target in draw.RenderTargets)
        {
            targetHash = HashDrawOracleWord(targetHash, target.Slot);
            targetHash = HashDrawOracleWord(targetHash, target.Address);
            targetHash = HashDrawOracleWord(targetHash, target.Width);
            targetHash = HashDrawOracleWord(targetHash, target.Height);
            targetHash = HashDrawOracleWord(targetHash, target.Format);
        }

        var textureHash = fnvOffset;
        foreach (var binding in draw.Textures)
        {
            var descriptor = binding.Descriptor;
            textureHash = HashDrawOracleWord(textureHash, descriptor.Address);
            textureHash = HashDrawOracleWord(textureHash, descriptor.Width);
            textureHash = HashDrawOracleWord(textureHash, descriptor.Height);
            textureHash = HashDrawOracleWord(textureHash, descriptor.Format);
            textureHash = HashDrawOracleWord(textureHash, descriptor.NumberType);
            textureHash = HashDrawOracleWord(textureHash, descriptor.TileMode);
            textureHash = HashDrawOracleWord(textureHash, binding.IsStorage ? 1UL : 0UL);
        }

        var bufferHash = fnvOffset;
        foreach (var binding in draw.GlobalMemoryBindings)
        {
            bufferHash = HashDrawOracleWord(bufferHash, binding.BaseAddress);
            bufferHash = HashDrawOracleWord(bufferHash, (ulong)binding.DataLength);
        }

        var vertexHash = fnvOffset;
        foreach (var input in draw.VertexInputs)
        {
            vertexHash = HashDrawOracleWord(vertexHash, input.Location);
            vertexHash = HashDrawOracleWord(vertexHash, input.BaseAddress);
            vertexHash = HashDrawOracleWord(vertexHash, input.Stride);
            vertexHash = HashDrawOracleWord(vertexHash, input.OffsetBytes);
            vertexHash = HashDrawOracleWord(vertexHash, input.ComponentCount);
            vertexHash = HashDrawOracleWord(vertexHash, input.DataFormat);
            vertexHash = HashDrawOracleWord(vertexHash, input.NumberFormat);
        }

        var targetMask = 0u;
        var shaderMask = 0u;
        state.CxRegisters.TryGetValue(CbTargetMask, out targetMask);
        state.CxRegisters.TryGetValue(0x8Fu, out shaderMask);

        var depth = draw.DepthTarget;
        var depthState = draw.RenderState.Depth;
        var blend = draw.RenderState.Blend;
        var viewport = draw.RenderState.Viewport;
        var indexType = draw.IndexBuffer?.Is32Bit == true ? 1u : 0u;
        var depthFormat = depth is null
            ? 0u
            : depth.HasStencil
                ? 130u
                : depth.GuestFormat switch
                {
                    1u => 124u,
                    3u => 126u,
                    _ => depth.GuestFormat,
                };

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.draw_oracle seq={sequence} " +
            $"name={(draw.IndexBuffer is null ? "DrawIndexAuto" : "DrawIndex")} " +
            $"es=0x{draw.ExportShaderAddress:X16} ps=0x{draw.PixelShaderAddress:X16} " +
            $"ps_active={(draw.PixelShaderAddress != 0 ? 1 : 0)} prim={draw.PrimitiveType} " +
            $"index_type={indexType} count={draw.VertexCount} instances={draw.InstanceCount} " +
            $"rt_count={draw.RenderTargets.Count} rt_hash={targetHash:X16} " +
            $"target_mask=0x{targetMask:X8} shader_mask=0x{shaderMask:X8} " +
            $"depth=0x{depth?.Address ?? 0:X10}:{depth?.Width ?? 0}x{depth?.Height ?? 0}:{depthFormat} " +
            $"depth_state={(depthState.TestEnable ? 1 : 0)}/{(depthState.WriteEnable ? 1 : 0)}/{depthState.CompareOp} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc} " +
            $"write_mask=0x{blend.WriteMask:X} " +
            $"viewport={viewport?.X ?? 0:0.000},{viewport?.Y ?? 0:0.000}," +
            $"{viewport?.Width ?? 0:0.000},{viewport?.Height ?? 0:0.000} " +
            $"vs_user={HashDrawOracleWords(draw.VertexInitialScalars):X16} " +
            $"ps_user={HashDrawOracleWords(draw.PixelInitialScalars):X16} " +
            $"textures={textureHash:X16} buffers={bufferHash:X16} vertex={vertexHash:X16}");

        TraceDrawOracleDetail(sequence, draw);
    }

    private static void TraceDrawOracleDetail(ulong sequence, TranslatedGuestDraw draw)
    {
        if (_traceDrawDetailEs != draw.ExportShaderAddress ||
            (_traceDrawDetailCount.HasValue && _traceDrawDetailCount != draw.VertexCount) ||
            Interlocked.Increment(ref _drawDetailTraceCount) > 8)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.draw_detail seq={sequence} es=0x{draw.ExportShaderAddress:X16} " +
            $"ps=0x{draw.PixelShaderAddress:X16} count={draw.VertexCount} " +
            $"vs_user=[{FormatShaderDwords(draw.VertexInitialScalars)}] " +
            $"ps_user=[{FormatShaderDwords(draw.PixelInitialScalars)}]");

        for (var index = 0; index < draw.Textures.Count; index++)
        {
            var binding = draw.Textures[index];
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.draw_detail_image seq={sequence} index={index} " +
                $"storage={(binding.IsStorage ? 1 : 0)} array={(binding.IsArrayed ? 1 : 0)} " +
                $"mip={binding.MipLevel} decoded={FormatTextureDescriptor(binding.Descriptor)} " +
                $"resource=[{FormatShaderDwords(binding.ResourceDescriptor ?? [])}] " +
                $"sampler=[{FormatShaderDwords(binding.SamplerDescriptor)}]");
        }

        for (var index = 0; index < draw.GlobalMemoryBindings.Count; index++)
        {
            var binding = draw.GlobalMemoryBindings[index];
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.draw_detail_buffer seq={sequence} index={index} " +
                $"scalar={binding.ScalarAddress} base=0x{binding.BaseAddress:X16} " +
                $"bytes={binding.DataLength} writable={(binding.Writable ? 1 : 0)} " +
                $"pcs=[{string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}]");
        }

        for (var index = 0; index < draw.VertexInputs.Count; index++)
        {
            var input = draw.VertexInputs[index];
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.draw_detail_vertex seq={sequence} index={index} " +
                $"pc=0x{input.Pc:X} location={input.Location} base=0x{input.BaseAddress:X16} " +
                $"stride={input.Stride} offset={input.OffsetBytes} components={input.ComponentCount} " +
                $"format={input.DataFormat}/{input.NumberFormat}");
        }
    }

    private static ulong HashDrawOracleWords(IReadOnlyList<uint> words)
    {
        var hash = 14695981039346656037UL;
        foreach (var word in words)
        {
            hash = HashDrawOracleWord(hash, word);
        }

        return hash;
    }

    private static ulong HashDrawOracleWord(ulong hash, ulong value)
    {
        const ulong prime = 1099511628211UL;
        for (var index = 0; index < sizeof(ulong); index++)
        {
            hash ^= (byte)(value >> (index * 8));
            hash *= prime;
        }

        return hash;
    }

    private static void TraceVideoDrawChain(
        ulong sequence,
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures)
    {
        if (!_traceVideoDrawChain)
        {
            return;
        }

        var sourceDepth = int.MaxValue;
        var targetDepth = int.MaxValue;
        var hasSourcePlane = false;
        var frame = Volatile.Read(ref _videoDrawChainFrame);
        var inputChain = new Dictionary<ulong, (int Depth, long Frame)>();
        lock (_videoDrawChainGate)
        {
            foreach (var texture in textures)
            {
                if (IsVideoPlaneCandidate(texture))
                {
                    hasSourcePlane = true;
                    sourceDepth = 0;
                    continue;
                }

                if (_videoDrawChainTargets.TryGetValue(texture.Address, out var inputState) &&
                    frame - inputState.Frame <= 3)
                {
                    sourceDepth = Math.Min(sourceDepth, inputState.Depth);
                    inputChain[texture.Address] = inputState;
                }
            }

            foreach (var target in draw.RenderTargets)
            {
                if (_videoDrawChainTargets.TryGetValue(target.Address, out var targetState) &&
                    frame - targetState.Frame <= 3)
                {
                    targetDepth = Math.Min(targetDepth, targetState.Depth);
                }
            }

            if (sourceDepth == int.MaxValue && targetDepth == int.MaxValue)
            {
                return;
            }

            if (sourceDepth != int.MaxValue)
            {
                var outputDepth = sourceDepth + 1;
                foreach (var target in draw.RenderTargets)
                {
                    if (target.Address == 0)
                    {
                        continue;
                    }

                    if (!_videoDrawChainTargets.TryGetValue(target.Address, out var priorState) ||
                        outputDepth < priorState.Depth ||
                        frame > priorState.Frame)
                    {
                        _videoDrawChainTargets[target.Address] = (outputDepth, frame);
                    }
                }
            }
        }

        var targets = draw.RenderTargets.Count == 0
            ? "none"
            : string.Join(
                '|',
                draw.RenderTargets
                    .Where(target => target.Address != 0)
                    .Select(target =>
                        $"0x{target.Address:X}:{target.Width}x{target.Height}:" +
                        $"f{target.Format}/n{target.NumberType}"));
        var inputs = string.Join(
            '|',
            textures.Select(texture =>
                $"0x{texture.Address:X}:{texture.Width}x{texture.Height}:" +
                $"f{texture.Format}/n{texture.NumberType}:g{texture.WriteGeneration}:" +
                $"stable={(texture.CpuSnapshotStable ? 1 : 0)}:" +
                $"payload={DescribeVideoTexturePayload(texture.RgbaPixels)}" +
                (inputChain.TryGetValue(texture.Address, out var chain)
                    ? $":chain={chain.Depth}@{chain.Frame}"
                    : string.Empty) +
                (texture.IsFallback ? ":fallback" : string.Empty)));
        var blend = draw.RenderState.Blend;
        var viewport = draw.RenderState.Viewport is { } vp
            ? $"{vp.X:0.#},{vp.Y:0.#},{vp.Width:0.#}x{vp.Height:0.#}"
            : "none";
        var depth = sourceDepth != int.MaxValue
            ? sourceDepth + 1
            : targetDepth;
        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.video_draw_chain frame={frame} seq={sequence} depth={depth} " +
            $"source_plane={(hasSourcePlane ? 1 : 0)} " +
            $"overwrite={(sourceDepth == int.MaxValue ? 1 : 0)} " +
            $"es=0x{draw.ExportShaderAddress:X} ps=0x{draw.PixelShaderAddress:X} " +
            $"targets=[{targets}] inputs=[{inputs}] " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc}" +
            $":a{blend.AlphaSrcFactor}/{blend.AlphaDstFactor}/{blend.AlphaFunc} " +
            $"mask=0x{blend.WriteMask:X} viewport={viewport}");
    }

    private static string DescribeVideoTexturePayload(byte[] pixels)
    {
        if (pixels.Length == 0)
        {
            return "cached";
        }

        const int SampleCount = 256;
        ulong hash = 14695981039346656037UL;
        var minimum = byte.MaxValue;
        var maximum = byte.MinValue;
        var zeroes = 0;
        var samples = Math.Min(SampleCount, pixels.Length);
        for (var index = 0; index < samples; index++)
        {
            var offset = samples == 1
                ? 0
                : (int)((long)index * (pixels.Length - 1) / (samples - 1));
            var value = pixels[offset];
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            if (value == 0)
            {
                zeroes++;
            }

            hash ^= value;
            hash *= 1099511628211UL;
        }

        return $"{pixels.Length}:h{hash:X16}:min{minimum}:max{maximum}:z{zeroes}/{samples}";
    }

    private static bool IsVideoPlaneCandidate(GuestDrawTexture texture) =>
        !texture.IsStorage &&
        !texture.IsFallback &&
        texture.ArrayLayers == 1 &&
        texture.Depth == 1 &&
        (texture.Format == 1 && texture.Height >= 1_000 && texture.Width is >= 1_900 and <= 2_048 ||
         texture.Format == 2 && texture.Height is >= 500 and <= 1_080 && texture.Width is >= 900 and <= 2_048);

    private static void ResetVideoDrawChainAtFlip()
    {
        if (!_traceVideoDrawChain)
        {
            return;
        }

        lock (_videoDrawChainGate)
        {
            _videoDrawChainFrame++;
            var oldestFrame = _videoDrawChainFrame - 3;
            foreach (var address in _videoDrawChainTargets
                         .Where(entry => entry.Value.Frame < oldestFrame)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _videoDrawChainTargets.Remove(address);
            }
        }
    }

    private static void TraceDrawCompactMiss(ulong sequence, uint vertexCount, string error)
    {
        if (!_traceDraws)
        {
            return;
        }

        Console.Error.WriteLine($"[DRAW] seq={sequence} MISS verts={vertexCount} error={error}");
    }

    private static int _grassTraceCount;

    private static void TraceGrassDrawVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (_grassTraceCount >= 6 ||
            !textures.Any(texture => texture.Width == 288 && texture.Height == 160) ||
            vertexBuffers.Count == 0 ||
            Interlocked.Increment(ref _grassTraceCount) > 6)
        {
            return;
        }

        var text = new System.Text.StringBuilder();
        text.Append($"agc.grassdraw prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} ");
        text.Append($"indexed={draw.IndexBuffer is not null} buffers={vertexBuffers.Count}");
        foreach (var buffer in vertexBuffers)
        {
            text.Append(
                $"\n  loc={buffer.Location} fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount} " +
                $"stride={buffer.Stride} offset={buffer.OffsetBytes} bytes={buffer.Length}");
            var stride = Math.Max(buffer.Stride, 4u);
            var maxVerts = Math.Min(6, (int)((buffer.Length - buffer.OffsetBytes) / stride));
            for (var vertex = 0; vertex < maxVerts; vertex++)
            {
                var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
                var components = Math.Min(4, (int)((buffer.Length - baseOffset) / 4));
                text.Append($"\n    v{vertex}:");
                for (var c = 0; c < components; c++)
                {
                    text.Append($" {BitConverter.ToSingle(buffer.Data, baseOffset + c * 4):0.#####}");
                }
            }
        }

        TraceAgcShader(text.ToString());
    }

    private static int _rectListTraceCount;

    private static void TraceRectListVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (!AgcPrimitiveHelpers.IsRectListPrimitive(draw.PrimitiveType) ||
            _rectListTraceCount >= 16 ||
            Interlocked.Increment(ref _rectListTraceCount) > 16)
        {
            return;
        }

        var expanded = AgcPrimitiveHelpers.GetRectListDrawVertexCount(
            draw.PrimitiveType,
            draw.VertexCount,
            indexed: draw.IndexBuffer is not null,
            hasVertexBuffers: vertexBuffers.Count > 0);
        var text = new System.Text.StringBuilder();
        text.Append(
            $"agc.rectlist prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount}->{expanded} " +
            $"indexed={(draw.IndexBuffer is not null ? 1 : 0)} vb={vertexBuffers.Count}");

        if (vertexBuffers.Count > 0)
        {
            var buffer = vertexBuffers[0];
            var stride = Math.Max(buffer.Stride, 4u);
            text.Append(
                $" stride={buffer.Stride} " +
                $"fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount}");
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
                if (baseOffset + 16 > buffer.Length)
                {
                    break;
                }

                var x = BitConverter.ToSingle(buffer.Data, baseOffset);
                var y = BitConverter.ToSingle(buffer.Data, baseOffset + 4);
                var z = BitConverter.ToSingle(buffer.Data, baseOffset + 8);
                var w = BitConverter.ToSingle(buffer.Data, baseOffset + 12);
                text.Append($" v{vertex}=({x:0.###},{y:0.###},{z:0.###},{w:0.###})");
            }
        }
        else
        {
            text.Append(" procedural=1");
        }

        TraceAgcShader(text.ToString());
    }

    private static void TraceShaderTranslationMiss(
        CpuContext ctx,
        SubmittedDcbState state,
        uint vertexCount,
        bool hasExportShader,
        ulong exportShaderAddress,
        bool hasPixelShader,
        ulong pixelShaderAddress,
        bool hasPsInputEna,
        uint psInputEna,
        bool hasPsInputAddr,
        uint psInputAddr,
        string? translationError = null)
    {
        var firstFailure = false;
        if (!string.IsNullOrEmpty(translationError))
        {
            lock (_submitTraceGate)
            {
                firstFailure = _tracedShaderFailures.Add(
                    (pixelShaderAddress, translationError));
            }
        }

        if (!firstFailure &&
            !ShouldTraceHotPath(ref _shaderTranslationMissTraceCount))
        {
            return;
        }

        // Translation failures are compatibility issues, not merely verbose
        // shader diagnostics. Report each distinct failure once even when AGC
        // tracing is disabled so normal runs preserve the missing opcode or
        // unsupported translation reason needed to fix the game.
        if (firstFailure)
        {
            Console.Error.WriteLine(
                $"[COMPAT][SHADER] ps=0x{pixelShaderAddress:X16} " +
                $"es=0x{exportShaderAddress:X16} error={translationError}");
        }

        if ((!hasPixelShader || !hasPsInputEna || !hasPsInputAddr) &&
            TryMarkMissingPixelShaderBindingsTrace())
        {
            TraceAgcShader(
                $"agc.shader_register_candidates " +
                DescribeShaderRegisterCandidates(ctx, state.ShRegisters));
        }

        if (!hasPixelShader)
        {
            state.CxRegisters.TryGetValue(DbDepthControl, out var rawDepthControl);
            state.CxRegisters.TryGetValue(DbZInfo, out var rawZInfo);
            state.CxRegisters.TryGetValue(DbDepthSizeXy, out var rawDepthSize);
            state.CxRegisters.TryGetValue(DbDepthView, out var rawDepthView);
            var depthState = DecodeDepthState(state.CxRegisters);
            var depthTarget = DecodeDepthTarget(
                state.CxRegisters,
                state.CompositeDepthSizeXy);
            TraceAgcShader(
                $"agc.shader_depth_state control=0x{rawDepthControl:X8} " +
                $"zinfo=0x{rawZInfo:X8} size=0x{rawDepthSize:X8} " +
                $"view=0x{rawDepthView:X8} " +
                $"test={(depthState.TestEnable ? 1 : 0)} " +
                $"write={(depthState.WriteEnable ? 1 : 0)} " +
                $"func={depthState.CompareOp} " +
                (depthTarget is null
                    ? "target=none"
                    : $"target=0x{depthTarget.Address:X16}:" +
                      $"{depthTarget.Width}x{depthTarget.Height}:" +
                      $"fmt{depthTarget.GuestFormat}/sw{depthTarget.SwizzleMode}:" +
                      $"ro={(depthTarget.ReadOnly ? 1 : 0)}"));
        }

        var shaderDecode = string.Empty;
        if (hasExportShader && hasPixelShader)
        {
            var shouldDescribe = false;
            ulong exportShaderHeader;
            ulong pixelShaderHeader;
            lock (_submitTraceGate)
            {
                shouldDescribe = _tracedShaderDecodePairs.Add((exportShaderAddress, pixelShaderAddress));
                _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
                _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
            }

            if (shouldDescribe)
            {
                shaderDecode = $" decode={Gen5ShaderTranslator.Describe(ctx, exportShaderAddress, pixelShaderAddress)}";
                TraceAgcShader(
                    $"agc.shader_words es=0x{exportShaderAddress:X16} " +
                    Gen5ShaderTranslator.DescribeWords(ctx, exportShaderAddress));
                if (Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        exportShaderAddress,
                        exportShaderHeader,
                        state.ShRegisters,
                        SelectExportUserDataRegister(state.ShRegisters),
                        out var exportState,
                        out _,
                        userDataScalarRegisterBase: NggUserDataScalarRegisterBase) &&
                    Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        pixelShaderAddress,
                        pixelShaderHeader,
                        state.ShRegisters,
                        PsTextureUserDataRegister,
                        out var pixelState,
                        out _))
                {
                    TraceAgcShader(
                        $"agc.shader_state es=0x{exportShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(exportState));
                    TraceAgcShader(
                        $"agc.shader_state ps=0x{pixelShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(pixelState));
                    if (Gen5ShaderScalarEvaluator.TryEvaluate(
                            ctx,
                            pixelState,
                            out var evaluation,
                            out var bindingError,
                            profileStage: Gen5ShaderEvaluationStage.Pixel))
                    {
                        foreach (var binding in evaluation.ImageBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_binding ps=0x{pixelShaderAddress:X16} " +
                                $"pc=0x{binding.Pc:X} op={binding.Opcode} " +
                                $"resource={FormatShaderDwords(binding.ResourceDescriptor)} " +
                                $"sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
                        }

                        foreach (var binding in evaluation.GlobalMemoryBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_global_binding ps=0x{pixelShaderAddress:X16} " +
                                $"saddr=s{binding.ScalarAddress} " +
                                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
                        }

                        if (GuestGpu.Current.TryCompilePixelShader(
                                 pixelState,
                                 evaluation,
                                 [new(0, 0, Gen5PixelOutputKind.Float)],
                                 out var compiledPixel,
                                 out var compileError,
                                 pixelInputEnable: psInputEna,
                                 pixelInputAddress: psInputAddr,
                                 pixelInputCntl: ReadPsInputCntlRegisters(state.CxRegisters),
                                 storageBufferOffsetAlignment:
                                     _storageBufferOffsetAlignment))
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv ps=0x{pixelShaderAddress:X16} " +
                                $"bytes={compiledPixel!.Payload.Length} bindings={evaluation.ImageBindings.Count} " +
                                $"global_buffers={evaluation.GlobalMemoryBindings.Count}");
                        }
                        else
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv_error ps=0x{pixelShaderAddress:X16} " +
                                compileError.ReplaceLineEndings(" "));
                        }
                    }
                    else
                    {
                        TraceAgcShader(
                            $"agc.shader_binding_error ps=0x{pixelShaderAddress:X16} " +
                            bindingError);
                    }
                }
            }
        }

        TraceAgcShader(
            $"agc.shader_translate_miss vertices={vertexCount} " +
            $"es={(hasExportShader ? $"0x{exportShaderAddress:X16}" : "missing")} " +
            $"ps={(hasPixelShader ? $"0x{pixelShaderAddress:X16}" : "missing")} " +
            $"ps_ena={(hasPsInputEna ? $"0x{psInputEna:X8}" : "missing")} " +
            $"ps_addr={(hasPsInputAddr ? $"0x{psInputAddr:X8}" : "missing")}" +
            (string.IsNullOrEmpty(translationError) ? string.Empty : $" error={translationError}") +
            shaderDecode);
    }

    private static bool TryMarkMissingPixelShaderBindingsTrace()
    {
        lock (_submitTraceGate)
        {
            if (_tracedMissingPixelShaderBindings)
            {
                return false;
            }

            _tracedMissingPixelShaderBindings = true;
            return true;
        }
    }

    private static string DescribeShaderRegisterCandidates(
        CpuContext ctx,
        IReadOnlyDictionary<uint, uint> registers)
    {
        var candidates = new List<(uint Register, ulong Address, ulong Header)>();
        lock (_submitTraceGate)
        {
            foreach (var (register, lo) in registers)
            {
                if (!registers.TryGetValue(register + 1, out var hi))
                {
                    continue;
                }

                var address = ((ulong)hi << 40) | ((ulong)lo << 8);
                if (address != 0 &&
                    _shaderHeadersByCode.TryGetValue(address, out var header))
                {
                    candidates.Add((register, address, header));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ',',
            candidates
                .OrderBy(candidate => candidate.Register)
                .Take(16)
                .Select(candidate =>
                {
                    var type = TryReadByte(
                        ctx,
                        candidate.Header + ShaderTypeOffset,
                        out var shaderType)
                        ? shaderType.ToString()
                        : "?";
                    return
                        $"sh[0x{candidate.Register:X}/0x{candidate.Register + 1:X}]=" +
                        $"0x{candidate.Address:X16}:type{type}";
                }));
    }

    private static string FormatShaderDwords(IReadOnlyList<uint> values) =>
        values.Count == 0
            ? "none"
            : string.Join(',', values.Select(static value => $"{value:X8}"));

    private static string FormatTextureDescriptor(TextureDescriptor descriptor) =>
        $"addr=0x{descriptor.Address:X16} {descriptor.Width}x{descriptor.Height} " +
        $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
        $"type={descriptor.Type} depth={descriptor.Depth} base_array={descriptor.BaseArray} " +
        $"levels={descriptor.BaseLevel}-{descriptor.LastLevel}/max{descriptor.MaxMip} " +
        $"pitch={descriptor.Pitch} array_pitch={descriptor.ArrayPitch} " +
        $"lod={descriptor.MinLod:X3}/{descriptor.MinLodWarn:X3} " +
        $"bc={descriptor.BcSwizzle} meta=0x{descriptor.MetadataAddress:X16} " +
        $"flags=0x{descriptor.DescriptorFlags:X6} dst=0x{descriptor.DstSelect:X3}";

    private static void DumpCompiledShader(
        string stage,
        ulong shaderAddress,
        ulong stateFingerprint,
        IGuestCompiledShader shader,
        Gen5ShaderProgram program)
    {
        if (shader.Payload.Length == 0 ||
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var addressFilter = Environment.GetEnvironmentVariable(
            "SHARPEMU_DUMP_SPIRV_ADDRESS");
        if (!string.IsNullOrWhiteSpace(addressFilter))
        {
            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(
                    span,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var filteredAddress) ||
                shaderAddress != filteredAddress)
            {
                return;
            }
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
        Directory.CreateDirectory(directory);
        var name = $"{shaderAddress:X16}-{stateFingerprint:X16}.{stage}";
        File.WriteAllBytes(
            Path.Combine(directory, $"{name}.{shader.PayloadFileExtension}"),
            shader.Payload);

        var lines = new List<string>(program.Instructions.Count + 2)
        {
            $"address=0x{program.Address:X16}",
            "pc words opcode destinations <- sources control",
        };
        foreach (var instruction in program.Instructions)
        {
            lines.Add(
                $"0x{instruction.Pc:X4} " +
                $"{string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} " +
                $"{instruction.Opcode} " +
                $"{string.Join(',', instruction.Destinations)} <- " +
                $"{string.Join(',', instruction.Sources)} " +
                $"{instruction.Control}");
        }

        File.WriteAllLines(Path.Combine(directory, $"{name}.ir.txt"), lines);
    }
}
