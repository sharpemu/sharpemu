// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

// Translated guest draws. Submission mirrors the Vulkan presenter (offscreen and
// depth-only draws are ordered guest work publishing into guest images; onscreen
// draws ride the presentation), while execution is idiomatic Metal: render passes
// express load/clear intent directly, the driver's hazard tracking replaces the
// explicit barrier choreography, and the binding layout follows the translation
// contract documented on Gen5MslTranslator (global buffers at their flat slot,
// SharpEmuUniforms after them, textures and samplers at the image slots, vertex
// streams at a high base that never collides with global buffers).
internal static partial class MetalVideoPresenter
{
    private const nuint VertexBufferSlotBase = 26;
    private const nuint UsageShaderRead = 1;
    private const nuint UsageShaderWrite = 2;
    private const nuint UsageRenderTarget = 4;
    private static bool _tracedTriangleFan;

    private sealed record TranslatedGuestDraw(
        MetalCompiledGuestShader? VertexShader,
        MetalCompiledGuestShader PixelShader,
        GuestDrawTexture[] Textures,
        GuestMemoryBuffer[] GlobalMemoryBuffers,
        GuestVertexBuffer[] VertexBuffers,
        uint AttributeCount,
        uint VertexCount,
        uint InstanceCount,
        uint PrimitiveType,
        GuestIndexBuffer? IndexBuffer,
        GuestRenderState RenderState);

    private sealed record OffscreenGuestDraw(
        TranslatedGuestDraw Draw,
        GuestRenderTarget[] Targets,
        GuestDepthTarget? DepthTarget,
        bool PublishTarget,
        ulong ShaderAddress);

    private sealed record PipelineKey(
        MetalCompiledGuestShader? VertexShader,
        MetalCompiledGuestShader PixelShader,
        ulong StateHash);

    private static long _perfDrawCount;
    private static long _perfDrawTicks;
    private static long _perfPipelineCreations;

    public static (long Draws, double DrawMs, long Pipelines) ReadAndResetDrawPerfCounters()
    {
        var draws = Interlocked.Exchange(ref _perfDrawCount, 0);
        var ticks = Interlocked.Exchange(ref _perfDrawTicks, 0);
        var pipelines = Interlocked.Exchange(ref _perfPipelineCreations, 0);
        return (draws, ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, pipelines);
    }

    private static readonly Dictionary<PipelineKey, nint> _pipelineCache = new();
    private static readonly Dictionary<GuestSampler, nint> _samplerCache = new();
    private static readonly Dictionary<ulong, GuestImage> _guestDepthImages = new();
    private static readonly Dictionary<(MtlPixelFormat Format, uint Width, uint Height), nint>
        _transientTargets = new();

    public static void SubmitTranslatedDraw(
        MetalCompiledGuestShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        MetalCompiledGuestShader? vertexShader,
        uint vertexCount,
        uint instanceCount,
        uint primitiveType,
        GuestIndexBuffer? indexBuffer,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers,
        GuestRenderState? renderState)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                IsSplash: false,
                RequiredGuestWorkSequence: CurrentSubmittingQueueTailLocked(),
                TranslatedDraw: new TranslatedGuestDraw(
                    vertexShader,
                    pixelShader,
                    ToArray(textures),
                    ToArray(globalMemoryBuffers),
                    vertexBuffers is null ? [] : ToArray(vertexBuffers),
                    attributeCount,
                    vertexCount,
                    instanceCount,
                    primitiveType,
                    indexBuffer,
                    renderState ?? GuestRenderState.Default));
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        if (drawKind == GuestDrawKind.None || width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed ||
                _latestPresentation is { Pixels: null } latest &&
                latest.DrawKind == drawKind &&
                latest.Width == width &&
                latest.Height == height)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                IsSplash: false,
                RequiredGuestWorkSequence: CurrentSubmittingQueueTailLocked(),
                DrawKind: drawKind);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitOffscreenTranslatedDraw(
        MetalCompiledGuestShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<GuestRenderTarget> targets,
        MetalCompiledGuestShader? vertexShader,
        uint vertexCount,
        uint instanceCount,
        uint primitiveType,
        GuestIndexBuffer? indexBuffer,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers,
        GuestRenderState? renderState,
        GuestDepthTarget? depthTarget,
        ulong shaderAddress)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var effectiveRenderState = renderState ?? GuestRenderState.Default;
        if (effectiveRenderState.Blends.Count == 1 && targets.Count > 1)
        {
            var blends = new GuestBlendState[targets.Count];
            for (var index = 0; index < blends.Length; index++)
            {
                blends[index] = effectiveRenderState.Blends[0];
            }

            effectiveRenderState = effectiveRenderState with { Blends = blends };
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            foreach (var target in targets)
            {
                var guestTextureFormat = GetGuestTextureFormat(target.Format, target.NumberType);
                if (target.Address != 0 && guestTextureFormat != 0)
                {
                    _availableGuestImages[target.Address] = guestTextureFormat;
                }
            }

            var workSequence = EnqueueGuestWorkLocked(
                new OffscreenGuestDraw(
                    new TranslatedGuestDraw(
                        vertexShader,
                        pixelShader,
                        ToArray(textures),
                        ToArray(globalMemoryBuffers),
                        vertexBuffers is null ? [] : ToArray(vertexBuffers),
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        effectiveRenderState),
                    ToArray(targets),
                    depthTarget,
                    PublishTarget: true,
                    shaderAddress));
            foreach (var target in targets)
            {
                if (target.Address != 0)
                {
                    _guestImageWorkSequences[target.Address] = workSequence;
                }
            }
        }
    }

    public static void SubmitDepthOnlyTranslatedDraw(
        MetalCompiledGuestShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        GuestDepthTarget depthTarget,
        MetalCompiledGuestShader? vertexShader,
        uint vertexCount,
        uint instanceCount,
        uint primitiveType,
        GuestIndexBuffer? indexBuffer,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers,
        GuestRenderState? renderState,
        ulong shaderAddress)
    {
        if (depthTarget.Address == 0 || depthTarget.Width == 0 || depthTarget.Height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new OffscreenGuestDraw(
                    new TranslatedGuestDraw(
                        vertexShader,
                        pixelShader,
                        ToArray(textures),
                        ToArray(globalMemoryBuffers),
                        vertexBuffers is null ? [] : ToArray(vertexBuffers),
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        renderState ?? GuestRenderState.Default),
                    [new GuestRenderTarget(Address: 0, depthTarget.Width, depthTarget.Height, Format: 10, NumberType: 0)],
                    depthTarget,
                    PublishTarget: false,
                    shaderAddress));
        }
    }

    public static void SubmitStorageTranslatedDraw(
        MetalCompiledGuestShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height,
        ulong shaderAddress)
    {
        var hasStorage = false;
        foreach (var texture in textures)
        {
            hasStorage |= texture.IsStorage;
        }

        if (width == 0 || height == 0 || !hasStorage)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new OffscreenGuestDraw(
                    new TranslatedGuestDraw(
                        null,
                        pixelShader,
                        ToArray(textures),
                        ToArray(globalMemoryBuffers),
                        [],
                        attributeCount,
                        3,
                        1,
                        4,
                        null,
                        GuestRenderState.Default),
                    [new GuestRenderTarget(Address: 0, width, height, Format: 12, NumberType: 7)],
                    DepthTarget: null,
                    PublishTarget: false,
                    shaderAddress));
        }
    }

    private static long CurrentSubmittingQueueTailLocked()
    {
        var queue = _submittingGuestQueue;
        return queue is { } identity &&
            _lastEnqueuedGuestWorkByQueue.TryGetValue(identity.Name, out var tail)
                ? tail
                : 0;
    }

    private static T[] ToArray<T>(IReadOnlyList<T> source)
    {
        var result = new T[source.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = source[index];
        }

        return result;
    }

    private static void ExecuteOffscreenDraw(nint device, nint queue, OffscreenGuestDraw work)
    {
        var perfStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _perfDrawCount);
        VideoOut.PerfOverlay.RecordDraw();
        try
        {
            ExecuteOffscreenDrawCore(device, queue, work);
        }
        finally
        {
            Interlocked.Add(
                ref _perfDrawTicks,
                System.Diagnostics.Stopwatch.GetTimestamp() - perfStart);
        }
    }

    private static void ExecuteOffscreenDrawCore(nint device, nint queue, OffscreenGuestDraw work)
    {
        var draw = work.Draw;
        var targetFormats = new MetalRenderTargetFormat[work.Targets.Length];
        for (var index = 0; index < targetFormats.Length; index++)
        {
            var target = work.Targets[index];
            if (!MetalGuestFormats.TryDecodeRenderTargetFormat(
                    target.Format,
                    target.NumberType,
                    out targetFormats[index]))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Metal skipped draw with unsupported color target " +
                    $"format={target.Format} number_type={target.NumberType}.");
                ReturnPooledGuestData(draw);
                return;
            }
        }

        if (draw.RenderState.Blends.Count != targetFormats.Length)
        {
            ReturnPooledGuestData(draw);
            return;
        }

        // Resolve color targets: published guest images by address, transient
        // pooled textures for address-0 targets (depth-only and storage draws).
        var targetTextures = new nint[work.Targets.Length];
        var targetLoadActions = new nuint[work.Targets.Length];
        var publishedTargets = new GuestImage?[work.Targets.Length];
        var firstWidth = work.Targets[0].Width;
        var firstHeight = work.Targets[0].Height;
        for (var index = 0; index < work.Targets.Length; index++)
        {
            var target = work.Targets[index];
            if (target.Address == 0)
            {
                var extentWidth = target.Width == 0 ? firstWidth : target.Width;
                var extentHeight = target.Height == 0 ? firstHeight : target.Height;
                targetTextures[index] = GetTransientTarget(
                    device, targetFormats[index].Format, extentWidth, extentHeight);
                targetLoadActions[index] = LoadActionClear;
                continue;
            }

            var image = EnsureGuestRenderTarget(device, target, targetFormats[index].Format);
            if (image is null)
            {
                ReturnPooledGuestData(draw);
                return;
            }

            publishedTargets[index] = image;
            targetTextures[index] = image.Texture;
            targetLoadActions[index] = image.Initialized ? LoadActionLoad : LoadActionClear;
        }

        if (targetTextures[0] == 0)
        {
            ReturnPooledGuestData(draw);
            return;
        }

        // Depth attachment, keyed by guest DB address; read-only depth drops write.
        GuestImage? depth = null;
        var depthState = draw.RenderState.Depth;
        if (work.DepthTarget is { } depthTarget && (depthState.TestEnable || depthState.WriteEnable))
        {
            if (depthTarget.ReadOnly && depthState.WriteEnable)
            {
                depthState = depthState with { WriteEnable = false };
            }

            var depthWidth = Math.Max(depthTarget.Width, firstWidth);
            var depthHeight = Math.Max(depthTarget.Height, firstHeight);
            depth = EnsureGuestDepthImage(device, depthTarget.Address, depthWidth, depthHeight);
        }

        if (!TryGetDrawPipeline(device, draw, targetFormats, depth is not null, out var pipeline))
        {
            ReturnPooledGuestData(draw);
            return;
        }

        var commandBuffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
        var pass = MetalNative.Send(
            MetalNative.Class("MTLRenderPassDescriptor"),
            MetalNative.Selector("renderPassDescriptor"));
        var colorAttachments = MetalNative.Send(pass, MetalNative.Selector("colorAttachments"));
        for (var index = 0; index < targetTextures.Length; index++)
        {
            var attachment = MetalNative.SendAtIndex(
                colorAttachments, MetalNative.Selector("objectAtIndexedSubscript:"), (nuint)index);
            MetalNative.SendVoid(attachment, MetalNative.Selector("setTexture:"), targetTextures[index]);
            MetalNative.Send(attachment, MetalNative.Selector("setLoadAction:"), (nint)targetLoadActions[index]);
            MetalNative.Send(attachment, MetalNative.Selector("setStoreAction:"), (nint)StoreActionStore);
        }

        if (depth is not null && work.DepthTarget is { } depthDescriptor)
        {
            var depthAttachment = MetalNative.Send(pass, MetalNative.Selector("depthAttachment"));
            MetalNative.SendVoid(depthAttachment, MetalNative.Selector("setTexture:"), depth.Texture);
            MetalNative.Send(
                depthAttachment,
                MetalNative.Selector("setLoadAction:"),
                (nint)(depth.Initialized ? LoadActionLoad : LoadActionClear));
            MetalNative.Send(depthAttachment, MetalNative.Selector("setStoreAction:"), (nint)StoreActionStore);
            MetalNative.SendVoidDouble(
                depthAttachment, MetalNative.Selector("setClearDepth:"), depthDescriptor.ClearDepth);
        }

        var encoder = MetalNative.Send(
            commandBuffer, MetalNative.Selector("renderCommandEncoderWithDescriptor:"), pass);
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);

        EncodeRenderState(device, encoder, draw.RenderState, depthState, depth is not null, firstWidth, firstHeight);
        var buffers = EncodeDrawBindings(device, encoder, work, out var writeBackBuffers);
        EncodeDrawCall(encoder, draw);

        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
        MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));

        // CPU-visible GPU writes are ordering points in the guest command
        // stream: completing this work item is the signal WaitForGuestWork
        // relies on, so the write-back must land before completion.
        if (writeBackBuffers.Count > 0)
        {
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("waitUntilCompleted"));
            WriteBuffersBackToGuest(writeBackBuffers);
        }

        ReleaseBuffers(buffers);

        for (var index = 0; index < publishedTargets.Length; index++)
        {
            if (publishedTargets[index] is { } published)
            {
                published.Initialized = true;
                published.GpuWritten = true;
            }
        }

        depth?.Initialized = true;
        ReturnPooledGuestData(draw);
    }

    /// <summary>Renders a presentation-carried draw (onscreen translated draw or a
    /// recognized fixed-function draw) into the reusable onscreen target.</summary>
    private static nint ExecutePresentationDraw(nint device, nint queue, Presentation presentation)
    {
        var target = GetTransientTarget(
            device, MtlPixelFormat.Bgra8Unorm, presentation.Width, presentation.Height);
        if (target == 0)
        {
            return 0;
        }

        if (presentation.TranslatedDraw is { } translatedDraw)
        {
            ExecuteOffscreenDrawToTexture(device, queue, translatedDraw, target);
        }
        else if (presentation.DrawKind == GuestDrawKind.FullscreenBarycentric)
        {
            if (!TryGetFixedDrawPipeline(device, out var pipeline))
            {
                return 0;
            }

            var commandBuffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
            var encoder = MetalNative.Send(
                commandBuffer,
                MetalNative.Selector("renderCommandEncoderWithDescriptor:"),
                CreateClearPass(target, new MtlClearColor { Alpha = 1 }));
            MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);
            MetalNative.SendDrawPrimitives(
                encoder,
                MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"),
                PrimitiveTypeTriangle,
                0,
                3);
            MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
        }

        return target;
    }

    private static void ExecuteOffscreenDrawToTexture(
        nint device,
        nint queue,
        TranslatedGuestDraw draw,
        nint target)
    {
        var formats = new[] { new MetalRenderTargetFormat(MtlPixelFormat.Bgra8Unorm, Gen5PixelOutputKind.Float) };
        if (!TryGetDrawPipeline(device, draw, formats, hasDepth: false, out var pipeline))
        {
            ReturnPooledGuestData(draw);
            return;
        }

        var commandBuffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
        var encoder = MetalNative.Send(
            commandBuffer,
            MetalNative.Selector("renderCommandEncoderWithDescriptor:"),
            CreateClearPass(target, new MtlClearColor { Alpha = 1 }));
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);
        var work = new OffscreenGuestDraw(draw, [], null, PublishTarget: false, ShaderAddress: 0);
        var buffers = EncodeDrawBindings(device, encoder, work, out var writeBackBuffers);
        EncodeDrawCall(encoder, draw);
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
        MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
        if (writeBackBuffers.Count > 0)
        {
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("waitUntilCompleted"));
            WriteBuffersBackToGuest(writeBackBuffers);
        }

        ReleaseBuffers(buffers);
        ReturnPooledGuestData(draw);
    }

    private static void EncodeRenderState(
        nint device,
        nint encoder,
        GuestRenderState renderState,
        GuestDepthState depthState,
        bool hasDepth,
        uint targetWidth,
        uint targetHeight)
    {
        if (renderState.Viewport is { } viewport)
        {
            // Guests program Vulkan-style negative-height viewports to get y-up
            // rendering out of Vulkan's y-down NDC. Metal's NDC is already
            // y-up and rejects negative heights (the draw rasterizes nothing),
            // so the equivalent is the normalized rect with the same on-screen
            // mapping.
            double originY = viewport.Y;
            double height = viewport.Height;
            if (height < 0)
            {
                originY += height;
                height = -height;
            }

            MetalNative.SendVoidViewport(
                encoder,
                MetalNative.Selector("setViewport:"),
                new MtlViewport
                {
                    OriginX = viewport.X,
                    OriginY = originY,
                    Width = viewport.Width,
                    Height = height,
                    ZNear = viewport.MinDepth,
                    ZFar = viewport.MaxDepth,
                });
        }

        if (renderState.Scissor is { } scissor)
        {
            var x = (nuint)Math.Clamp(scissor.X, 0, (int)targetWidth);
            var y = (nuint)Math.Clamp(scissor.Y, 0, (int)targetHeight);
            var width = Math.Min(scissor.Width, targetWidth - (uint)x);
            var height = Math.Min(scissor.Height, targetHeight - (uint)y);
            if (width > 0 && height > 0)
            {
                MetalNative.SendVoidScissor(
                    encoder,
                    MetalNative.Selector("setScissorRect:"),
                    new MtlScissorRect { X = x, Y = y, Width = width, Height = height });
            }
        }

        var raster = renderState.Raster;
        // MTLCullMode: None=0, Front=1, Back=2.
        var cullMode = raster switch
        {
            { CullFront: true, CullBack: true } => 3,
            { CullFront: true } => 1,
            { CullBack: true } => 2,
            _ => 0,
        };
        if (cullMode == 3)
        {
            // Culling both faces draws nothing; Metal has no such mode, so an
            // empty scissor is the cheapest equivalent.
            MetalNative.SendVoidScissor(
                encoder,
                MetalNative.Selector("setScissorRect:"),
                new MtlScissorRect { X = 0, Y = 0, Width = 1, Height = 1 });
        }
        else if (cullMode != 0)
        {
            MetalNative.Send(encoder, MetalNative.Selector("setCullMode:"), (nint)cullMode);
        }

        // MTLWinding: Clockwise=0, CounterClockwise=1.
        MetalNative.Send(
            encoder,
            MetalNative.Selector("setFrontFacingWinding:"),
            raster.FrontFaceClockwise ? 0 : 1);
        if (raster.Wireframe)
        {
            // MTLTriangleFillMode.Lines = 1.
            MetalNative.Send(encoder, MetalNative.Selector("setTriangleFillMode:"), 1);
        }

        if (hasDepth)
        {
            var descriptor = MetalNative.Send(
                MetalNative.Send(MetalNative.Class("MTLDepthStencilDescriptor"), MetalNative.Selector("alloc")),
                MetalNative.Selector("init"));
            // The guest ZFUNC encoding matches MTLCompareFunction ordering.
            MetalNative.Send(
                descriptor,
                MetalNative.Selector("setDepthCompareFunction:"),
                (nint)(depthState.TestEnable ? depthState.CompareOp & 0x7 : 7));
            MetalNative.SendVoidBool(
                descriptor, MetalNative.Selector("setDepthWriteEnabled:"), depthState.WriteEnable);
            var depthStencilState = MetalNative.Send(
                device, MetalNative.Selector("newDepthStencilStateWithDescriptor:"), descriptor);
            MetalNative.SendVoid(encoder, MetalNative.Selector("setDepthStencilState:"), depthStencilState);
        }
    }

    /// <summary>Uploads and binds everything the translation contract names: global
    /// buffers and SharpEmuUniforms to both stages, textures/samplers to both
    /// stages, vertex streams at the high slots. Returns the transient MTLBuffers
    /// to release and collects the writable ones for guest write-back.</summary>
    private static List<nint> EncodeDrawBindings(
        nint device,
        nint encoder,
        OffscreenGuestDraw work,
        out List<(nint Buffer, GuestMemoryBuffer Guest, uint Bias)> writeBackBuffers)
    {
        var draw = work.Draw;
        var buffers = new List<nint>();
        writeBackBuffers = [];

        var selSetVertexBuffer = MetalNative.Selector("setVertexBuffer:offset:atIndex:");
        var selSetFragmentBuffer = MetalNative.Selector("setFragmentBuffer:offset:atIndex:");
        var bufferCount = draw.GlobalMemoryBuffers.Length;
        var boundBytes = new uint[Math.Max(bufferCount, 1)];
        for (var index = 0; index < bufferCount; index++)
        {
            var guest = draw.GlobalMemoryBuffers[index];
            var buffer = CreateGlobalBuffer(device, guest, out var bias, out boundBytes[index]);
            buffers.Add(buffer);
            MetalNative.SendSetBuffer(encoder, selSetVertexBuffer, buffer, 0, (nuint)index);
            MetalNative.SendSetBuffer(encoder, selSetFragmentBuffer, buffer, 0, (nuint)index);
            if (guest.Writable && guest.WriteBackToGuest)
            {
                writeBackBuffers.Add((buffer, guest, bias));
            }
        }

        // SharpEmuUniforms per the translation contract: dispatch limit (unused by
        // graphics stages), reserved, then each bound buffer's byte length
        // (including the alignment-bias prefix the shader indexes past).
        var uniforms = new byte[16 + (Math.Max(bufferCount, 1) * sizeof(uint))];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(0), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(8), 1);
        for (var index = 0; index < bufferCount; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                uniforms.AsSpan(16 + (index * sizeof(uint))),
                boundBytes[index]);
        }

        // Each stage declares SharpEmuUniforms at its own translation-time index
        // (globalBufferBase + totalGlobalBufferCount). A draw whose vertex-stage
        // guest buffers sit after the pixel stage's gives the two stages
        // different indices, so bind the buffer at each stage's declared slot —
        // one shared index leaves the other stage's uniforms unbound, which
        // zeroes its bounds-checked loads (caught by Metal API validation as
        // "missing Buffer binding ... for sharpemu_uniforms").
        var uniformsBuffer = CreateBuffer(device, uniforms, uniforms.Length);
        buffers.Add(uniformsBuffer);
        var vertexUniformsIndex = draw.VertexShader?.Shader.UniformsBufferIndex ?? -1;
        MetalNative.SendSetBuffer(
            encoder,
            selSetVertexBuffer,
            uniformsBuffer,
            0,
            (nuint)(vertexUniformsIndex >= 0 ? vertexUniformsIndex : bufferCount));
        var fragmentUniformsIndex = draw.PixelShader.Shader.UniformsBufferIndex;
        MetalNative.SendSetBuffer(
            encoder,
            selSetFragmentBuffer,
            uniformsBuffer,
            0,
            (nuint)(fragmentUniformsIndex >= 0 ? fragmentUniformsIndex : bufferCount));

        var selSetVertexTexture = MetalNative.Selector("setVertexTexture:atIndex:");
        var selSetFragmentTexture = MetalNative.Selector("setFragmentTexture:atIndex:");
        var selSetVertexSampler = MetalNative.Selector("setVertexSamplerState:atIndex:");
        var selSetFragmentSampler = MetalNative.Selector("setFragmentSamplerState:atIndex:");
        for (var index = 0; index < draw.Textures.Length; index++)
        {
            var texture = CreateDrawTexture(device, draw.Textures[index]);
            if (texture != 0)
            {
                MetalNative.SendSetAtIndex(encoder, selSetVertexTexture, texture, (nuint)index);
                MetalNative.SendSetAtIndex(encoder, selSetFragmentTexture, texture, (nuint)index);
                MetalNative.SendVoid(texture, MetalNative.Selector("release"));
            }

            var sampler = GetOrCreateSampler(device, draw.Textures[index].Sampler);
            MetalNative.SendSetAtIndex(encoder, selSetVertexSampler, sampler, (nuint)index);
            MetalNative.SendSetAtIndex(encoder, selSetFragmentSampler, sampler, (nuint)index);
        }

        for (var index = 0; index < draw.VertexBuffers.Length; index++)
        {
            var vertexBuffer = draw.VertexBuffers[index];
            var buffer = CreateBuffer(device, vertexBuffer.Data, vertexBuffer.Length);
            buffers.Add(buffer);
            // Bind at zero; the attribute's byte offset (set in the vertex
            // descriptor) selects the field inside the interleaved vertex.
            MetalNative.SendSetBuffer(
                encoder, selSetVertexBuffer, buffer, 0, VertexBufferSlotBase + (nuint)index);
        }

        return buffers;
    }

    private static void EncodeDrawCall(nint encoder, TranslatedGuestDraw draw)
    {
        var primitive = GetPrimitiveType(draw.PrimitiveType);
        var vertexCount = draw.PrimitiveType == 0x11 && draw.IndexBuffer is null
            ? 4u
            : draw.VertexCount;
        if (draw.IndexBuffer is { } indexBuffer)
        {
            var device = MetalNative.Send(encoder, MetalNative.Selector("device"));
            var buffer = CreateBuffer(device, indexBuffer.Data, indexBuffer.Length);
            MetalNative.SendDrawIndexedPrimitives(
                encoder,
                MetalNative.Selector("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:"),
                primitive,
                vertexCount,
                indexBuffer.Is32Bit ? 1u : 0u,
                buffer,
                0,
                Math.Max(draw.InstanceCount, 1));
            MetalNative.SendVoid(buffer, MetalNative.Selector("release"));
            if (indexBuffer.Pooled)
            {
                GuestDataPool.Shared.Return(indexBuffer.Data);
            }
        }
        else
        {
            MetalNative.SendDrawPrimitivesInstanced(
                encoder,
                MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:instanceCount:"),
                primitive,
                0,
                vertexCount,
                Math.Max(draw.InstanceCount, 1));
        }
    }

    private static bool TryGetDrawPipeline(
        nint device,
        TranslatedGuestDraw draw,
        MetalRenderTargetFormat[] targetFormats,
        bool hasDepth,
        out nint pipeline)
    {
        var stateHash = 14695981039346656037UL;
        void Mix(ulong value)
        {
            stateHash = (stateHash ^ value) * 1099511628211UL;
        }

        for (var index = 0; index < targetFormats.Length; index++)
        {
            Mix((ulong)targetFormats[index].Format);
            var blend = draw.RenderState.Blends[index];
            Mix(blend.Enable ? 1UL : 0UL);
            Mix(blend.ColorSrcFactor | ((ulong)blend.ColorDstFactor << 8) | ((ulong)blend.ColorFunc << 16));
            Mix(blend.AlphaSrcFactor | ((ulong)blend.AlphaDstFactor << 8) | ((ulong)blend.AlphaFunc << 16));
            Mix(blend.SeparateAlphaBlend ? 1UL : 0UL);
            Mix(blend.WriteMask);
        }

        Mix(hasDepth ? 2UL : 1UL);
        for (var index = 0; index < draw.VertexBuffers.Length; index++)
        {
            var vertexBuffer = draw.VertexBuffers[index];
            Mix(vertexBuffer.Location |
                ((ulong)vertexBuffer.ComponentCount << 8) |
                ((ulong)vertexBuffer.DataFormat << 16) |
                ((ulong)vertexBuffer.NumberFormat << 26) |
                ((ulong)vertexBuffer.Stride << 34));
            // The attribute byte offset is baked into the pipeline's vertex
            // descriptor, so it must key the cache too.
            Mix(vertexBuffer.OffsetBytes);
        }

        var key = new PipelineKey(draw.VertexShader, draw.PixelShader, stateHash);
        lock (_pipelineCache)
        {
            if (_pipelineCache.TryGetValue(key, out pipeline))
            {
                return pipeline != 0;
            }
        }

        pipeline = CreateDrawPipeline(device, draw, targetFormats, hasDepth);
        lock (_pipelineCache)
        {
            _pipelineCache[key] = pipeline;
        }

        return pipeline != 0;
    }

    private static nint CreateDrawPipeline(
        nint device,
        TranslatedGuestDraw draw,
        MetalRenderTargetFormat[] targetFormats,
        bool hasDepth)
    {
        var vertexFunction = draw.VertexShader is { } vertexShader
            ? GetShaderFunction(device, vertexShader)
            : GetFixedFullscreenVertexFunction(device, draw.AttributeCount);
        var fragmentFunction = GetShaderFunction(device, draw.PixelShader);
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            return 0;
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertexFunction);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragmentFunction);

        var colorAttachments = MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments"));
        for (var index = 0; index < targetFormats.Length; index++)
        {
            var attachment = MetalNative.SendAtIndex(
                colorAttachments, MetalNative.Selector("objectAtIndexedSubscript:"), (nuint)index);
            MetalNative.Send(
                attachment, MetalNative.Selector("setPixelFormat:"), (nint)targetFormats[index].Format);
            var blend = draw.RenderState.Blends[index];
            MetalNative.Send(
                attachment,
                MetalNative.Selector("setWriteMask:"),
                (nint)ToMetalWriteMask(blend.WriteMask));
            if (blend.Enable && !IsIntegerFormat(targetFormats[index].OutputKind))
            {
                MetalNative.SendVoidBool(attachment, MetalNative.Selector("setBlendingEnabled:"), true);
                MetalNative.Send(
                    attachment,
                    MetalNative.Selector("setSourceRGBBlendFactor:"),
                    (nint)ToMetalBlendFactor(blend.ColorSrcFactor));
                MetalNative.Send(
                    attachment,
                    MetalNative.Selector("setDestinationRGBBlendFactor:"),
                    (nint)ToMetalBlendFactor(blend.ColorDstFactor));
                MetalNative.Send(
                    attachment,
                    MetalNative.Selector("setRgbBlendOperation:"),
                    (nint)ToMetalBlendOperation(blend.ColorFunc));
                var alphaSrc = blend.SeparateAlphaBlend ? blend.AlphaSrcFactor : blend.ColorSrcFactor;
                var alphaDst = blend.SeparateAlphaBlend ? blend.AlphaDstFactor : blend.ColorDstFactor;
                var alphaFunc = blend.SeparateAlphaBlend ? blend.AlphaFunc : blend.ColorFunc;
                MetalNative.Send(
                    attachment,
                    MetalNative.Selector("setSourceAlphaBlendFactor:"),
                    (nint)ToMetalBlendFactor(alphaSrc));
                MetalNative.Send(
                    attachment,
                    MetalNative.Selector("setDestinationAlphaBlendFactor:"),
                    (nint)ToMetalBlendFactor(alphaDst));
                MetalNative.Send(
                    attachment,
                    MetalNative.Selector("setAlphaBlendOperation:"),
                    (nint)ToMetalBlendOperation(alphaFunc));
            }
        }

        if (hasDepth)
        {
            MetalNative.Send(
                descriptor,
                MetalNative.Selector("setDepthAttachmentPixelFormat:"),
                (nint)MtlPixelFormat.Depth32Float);
        }

        if (draw.VertexShader is not null && draw.VertexBuffers.Length > 0)
        {
            MetalNative.SendVoid(
                descriptor,
                MetalNative.Selector("setVertexDescriptor:"),
                CreateVertexDescriptor(draw.VertexBuffers));
        }

        nint error = 0;
        var pipeline = MetalNative.Send(
            device,
            MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"),
            descriptor,
            ref error);
        if (pipeline == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Metal draw pipeline creation failed: {MetalNative.DescribeError(error)}");
        }

        Interlocked.Increment(ref _perfPipelineCreations);
        return pipeline;
    }

    private static nint CreateVertexDescriptor(GuestVertexBuffer[] vertexBuffers)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Class("MTLVertexDescriptor"), MetalNative.Selector("vertexDescriptor"));
        var attributes = MetalNative.Send(descriptor, MetalNative.Selector("attributes"));
        var layouts = MetalNative.Send(descriptor, MetalNative.Selector("layouts"));
        var selAt = MetalNative.Selector("objectAtIndexedSubscript:");
        for (var index = 0; index < vertexBuffers.Length; index++)
        {
            var vertexBuffer = vertexBuffers[index];
            var slot = VertexBufferSlotBase + (nuint)index;
            var attribute = MetalNative.SendAtIndex(attributes, selAt, vertexBuffer.Location);
            MetalNative.Send(
                attribute,
                MetalNative.Selector("setFormat:"),
                (nint)ToMetalVertexFormat(
                    vertexBuffer.DataFormat, vertexBuffer.NumberFormat, vertexBuffer.ComponentCount));
            // The guest byte offset is the attribute's position inside the
            // interleaved vertex; carry it on the attribute (buffer bound at 0)
            // rather than the buffer bind offset. Metal fetches a fixed
            // (bind-offset + attribute-offset + index*stride), so the two are
            // arithmetically equal, but a non-zero per-buffer bind offset here
            // fetched zero on this path — keeping the attribute offset is the
            // layout Metal's vertex-descriptor path expects.
            var attributeOffset = vertexBuffer.OffsetBytes < (uint)vertexBuffer.Length
                ? vertexBuffer.OffsetBytes
                : 0;
            MetalNative.Send(attribute, MetalNative.Selector("setOffset:"), (nint)attributeOffset);
            MetalNative.Send(attribute, MetalNative.Selector("setBufferIndex:"), (nint)slot);

            var layout = MetalNative.SendAtIndex(layouts, selAt, slot);
            var stride = vertexBuffer.Stride != 0
                ? vertexBuffer.Stride
                : Math.Max(vertexBuffer.ComponentCount, 1) * 4;
            MetalNative.Send(layout, MetalNative.Selector("setStride:"), (nint)stride);
            // MTLVertexStepFunction.PerVertex = 1.
            MetalNative.Send(layout, MetalNative.Selector("setStepFunction:"), 1);
        }

        return descriptor;
    }

    private static nint GetShaderFunction(nint device, MetalCompiledGuestShader shader)
    {
        if (shader.CachedLibrary == 0)
        {
            if (!TryCompileLibrary(device, shader.Shader.Source, out var library, out var error))
            {
                Console.Error.WriteLine($"[LOADER][WARN] Metal shader compile failed: {error}");
                return 0;
            }

            shader.CachedLibrary = library;
        }

        return MetalNative.Send(
            shader.CachedLibrary,
            MetalNative.Selector("newFunctionWithName:"),
            MetalNative.NsString(shader.Shader.EntryPoint));
    }

    private static readonly Dictionary<uint, nint> _fixedVertexLibraries = new();
    private static nint _fixedDrawPipeline;

    private static nint GetFixedFullscreenVertexFunction(nint device, uint attributeCount)
    {
        nint library;
        lock (_fixedVertexLibraries)
        {
            _fixedVertexLibraries.TryGetValue(attributeCount, out library);
        }

        if (library == 0)
        {
            if (!TryCompileLibrary(
                    device, MslFixedShaders.CreateFullscreenVertex(attributeCount), out library, out var error))
            {
                Console.Error.WriteLine($"[LOADER][WARN] Metal fullscreen vertex compile failed: {error}");
                return 0;
            }

            lock (_fixedVertexLibraries)
            {
                _fixedVertexLibraries[attributeCount] = library;
            }
        }

        return MetalNative.Send(
            library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString("fullscreen_vs"));
    }

    private static bool TryGetFixedDrawPipeline(nint device, out nint pipeline)
    {
        if (_fixedDrawPipeline != 0)
        {
            pipeline = _fixedDrawPipeline;
            return true;
        }

        pipeline = 0;
        var vertexFunction = GetFixedFullscreenVertexFunction(device, 1);
        if (vertexFunction == 0)
        {
            return false;
        }

        if (!TryCompileLibrary(device, MslFixedShaders.CreateAttributeFragment(0), out var library, out var error))
        {
            Console.Error.WriteLine($"[LOADER][WARN] Metal fixed draw pipeline unavailable: {error}");
            return false;
        }

        var fragmentFunction = MetalNative.Send(
            library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString("attribute_fs"));
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertexFunction);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragmentFunction);
        var attachment = MetalNative.SendAtIndex(
            MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments")),
            MetalNative.Selector("objectAtIndexedSubscript:"),
            0);
        MetalNative.Send(attachment, MetalNative.Selector("setPixelFormat:"), (nint)MtlPixelFormat.Bgra8Unorm);
        nint pipelineError = 0;
        pipeline = MetalNative.Send(
            device,
            MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"),
            descriptor,
            ref pipelineError);
        _fixedDrawPipeline = pipeline;
        _ = error;
        return pipeline != 0;
    }

    private static GuestImage? EnsureGuestRenderTarget(
        nint device,
        GuestRenderTarget target,
        MtlPixelFormat format)
    {
        lock (_gate)
        {
            if (_guestImages.TryGetValue(target.Address, out var existing) &&
                existing.Width == target.Width &&
                existing.Height == target.Height)
            {
                return existing;
            }
        }

        if (target.Width == 0 || target.Height == 0 || target.Width > 16384 || target.Height > 16384)
        {
            return null;
        }

        var image = new GuestImage
        {
            Texture = CreateGuestTexture(device, format, target.Width, target.Height),
            Width = target.Width,
            Height = target.Height,
            Format = format,
        };
        if (image.Texture == 0)
        {
            return null;
        }

        byte[]? initialData;
        lock (_gate)
        {
            _pendingGuestImageInitialData.Remove(target.Address, out initialData);
            if (_guestImages.TryGetValue(target.Address, out var replaced))
            {
                MetalNative.SendVoid(replaced.Texture, MetalNative.Selector("release"));
            }

            _guestImages[target.Address] = image;
            _guestImageExtents[target.Address] = (target.Width, target.Height, (ulong)target.Width * target.Height * 4);
        }

        // Pending initial data is RGBA8; only 4-byte-texel targets take it verbatim.
        if (initialData is not null &&
            MetalRenderTargetFormat.GetBytesPerPixel(format) == 4 &&
            (ulong)initialData.Length >= (ulong)target.Width * target.Height * 4)
        {
            ReplaceTextureContents(
                image.Texture, target.Width, target.Height, initialData, target.Width, bytesPerPixel: 4);
            image.Initialized = true;
        }

        return image;
    }

    private static GuestImage EnsureGuestDepthImage(nint device, ulong address, uint width, uint height)
    {
        lock (_gate)
        {
            if (_guestDepthImages.TryGetValue(address, out var existing) &&
                existing.Width == width &&
                existing.Height == height)
            {
                return existing;
            }
        }

        var descriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)MtlPixelFormat.Depth32Float,
            width,
            height,
            mipmapped: false);
        MetalNative.Send(
            descriptor, MetalNative.Selector("setUsage:"), (nint)(UsageRenderTarget | UsageShaderRead));
        // MTLStorageMode.Private = 2: depth never round-trips to the CPU.
        MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), 2);
        var image = new GuestImage
        {
            Texture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor),
            Width = width,
            Height = height,
            Format = MtlPixelFormat.Depth32Float,
        };
        lock (_gate)
        {
            if (_guestDepthImages.Remove(address, out var replaced))
            {
                MetalNative.SendVoid(replaced.Texture, MetalNative.Selector("release"));
            }

            _guestDepthImages[address] = image;
        }

        return image;
    }

    private static nint GetTransientTarget(nint device, MtlPixelFormat format, uint width, uint height)
    {
        var key = (format, width, height);
        lock (_transientTargets)
        {
            if (_transientTargets.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }

        var texture = CreateGuestTexture(device, format, width, height);
        lock (_transientTargets)
        {
            _transientTargets[key] = texture;
        }

        return texture;
    }

    private static int _missingTextureTraces;

    private static nint CreateDrawTexture(nint device, GuestDrawTexture texture)
    {
        if (texture.Width == 0 || texture.Height == 0)
        {
            return 0;
        }

        // Feedback reads of a live guest render target sample an ordered snapshot.
        if (texture.RgbaPixels.Length == 0 && texture.Address != 0)
        {
            GuestImage? live;
            lock (_gate)
            {
                _guestImages.TryGetValue(texture.Address, out live);
            }

            if (live is { Initialized: true })
            {
                var snapshot = CreateGuestTexture(device, live.Format, live.Width, live.Height);
                if (snapshot != 0)
                {
                    var queue = _drawSnapshotQueue;
                    if (queue != 0)
                    {
                        CopyTexture(queue, live.Texture, snapshot);
                        return snapshot;
                    }

                    MetalNative.SendVoid(snapshot, MetalNative.Selector("release"));
                }
            }

            if (_missingTextureTraces < 16)
            {
                _missingTextureTraces++;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Metal draw texture unresolved: live 0x{texture.Address:X} " +
                    $"{texture.Width}x{texture.Height} found={live is not null} " +
                    $"init={live?.Initialized ?? false}");
            }

            return 0;
        }

        if ((ulong)texture.RgbaPixels.Length < (ulong)texture.Width * texture.Height * 4)
        {
            if (_missingTextureTraces < 16)
            {
                _missingTextureTraces++;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Metal draw texture undersized: 0x{texture.Address:X} " +
                    $"{texture.Width}x{texture.Height} pitch={texture.Pitch} " +
                    $"bytes={texture.RgbaPixels.Length}");
            }

            return 0;
        }

        var descriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)MtlPixelFormat.Rgba8Unorm,
            texture.Width,
            texture.Height,
            mipmapped: false);
        if (texture.IsStorage)
        {
            MetalNative.Send(
                descriptor,
                MetalNative.Selector("setUsage:"),
                (nint)(UsageShaderRead | UsageShaderWrite));
        }

        var handle = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        if (handle != 0)
        {
            var pitch = texture.Pitch != 0 ? Math.Max(texture.Pitch, texture.Width) : texture.Width;
            // AGC always supplies RGBA8 texel copies, matching the Rgba8Unorm
            // texture created above; row clamping happens in the helper.
            ReplaceTextureContents(
                handle, texture.Width, texture.Height, texture.RgbaPixels, pitch, bytesPerPixel: 4);
        }

        return handle;
    }

    [ThreadStatic]
    private static nint _drawSnapshotQueue;

    private static nint GetOrCreateSampler(nint device, GuestSampler sampler)
    {
        lock (_samplerCache)
        {
            if (_samplerCache.TryGetValue(sampler, out var cached))
            {
                return cached;
            }
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLSamplerDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.Send(
            descriptor,
            MetalNative.Selector("setSAddressMode:"),
            (nint)ToMetalAddressMode(sampler.Word0 & 0x7));
        MetalNative.Send(
            descriptor,
            MetalNative.Selector("setTAddressMode:"),
            (nint)ToMetalAddressMode((sampler.Word0 >> 3) & 0x7));
        MetalNative.Send(
            descriptor,
            MetalNative.Selector("setRAddressMode:"),
            (nint)ToMetalAddressMode((sampler.Word0 >> 6) & 0x7));
        var magFilter = (sampler.Word2 >> 20) & 0x3;
        var minFilter = (sampler.Word2 >> 22) & 0x3;
        MetalNative.Send(
            descriptor,
            MetalNative.Selector("setMagFilter:"),
            magFilter is 1 or 3 ? 1 : 0);
        MetalNative.Send(
            descriptor,
            MetalNative.Selector("setMinFilter:"),
            minFilter is 1 or 3 ? 1 : 0);

        var handle = MetalNative.Send(
            device, MetalNative.Selector("newSamplerStateWithDescriptor:"), descriptor);
        lock (_samplerCache)
        {
            _samplerCache[sampler] = handle;
        }

        return handle;
    }

    // A guest global buffer is bound so the shader's alignment bias (the guest
    // base address's low bits below the storage-buffer offset alignment) lands
    // on the real data: the buffer is allocated bias + length bytes with the
    // data at offset bias, matching how the Vulkan backend binds into a larger
    // allocation at an aligned-down descriptor offset. boundBytes is what
    // SharpEmuUniforms must carry so the shader's bounds check passes.
    private const ulong StorageBufferOffsetAlignment = 256;

    private static nint CreateGlobalBuffer(
        nint device, GuestMemoryBuffer guest, out uint bias, out uint boundBytes)
    {
        bias = (uint)((ulong)guest.BaseAddress & (StorageBufferOffsetAlignment - 1));
        var length = Math.Clamp(guest.Length, 0, guest.Data.Length);
        boundBytes = bias + (uint)length;
        if (bias == 0)
        {
            return CreateBuffer(device, guest.Data, length);
        }

        var padded = new byte[bias + (uint)Math.Max(length, 1)];
        Array.Copy(guest.Data, 0, padded, bias, length);
        return CreateBuffer(device, padded, padded.Length);
    }

    private static nint CreateBuffer(nint device, byte[] data, int length)
    {
        var bounded = Math.Clamp(length, 0, data.Length);
        if (bounded == 0)
        {
            bounded = 4;
        }

        unsafe
        {
            fixed (byte* bytes = data)
            {
                // options 0 = shared storage: CPU-visible for write-back.
                return MetalNative.SendBuffer(
                    device,
                    MetalNative.Selector("newBufferWithBytes:length:options:"),
                    (nint)bytes,
                    (nuint)bounded,
                    0);
            }
        }
    }

    private static void WriteBuffersBackToGuest(
        List<(nint Buffer, GuestMemoryBuffer Guest, uint Bias)> writeBackBuffers)
    {
        var memory = _guestMemory;
        foreach (var (buffer, guest, bias) in writeBackBuffers)
        {
            var contents = MetalNative.Send(buffer, MetalNative.Selector("contents"));
            if (contents == 0 || memory is null)
            {
                continue;
            }

            unsafe
            {
                // The data sits at offset bias inside the bound buffer.
                _ = memory.TryWrite(
                    guest.BaseAddress,
                    new ReadOnlySpan<byte>((void*)(contents + bias), guest.Length));
            }
        }
    }

    private static void ReleaseBuffers(List<nint> buffers)
    {
        var selRelease = MetalNative.Selector("release");
        foreach (var buffer in buffers)
        {
            if (buffer != 0)
            {
                MetalNative.SendVoid(buffer, selRelease);
            }
        }
    }

    private static void ReturnPooledGuestData(TranslatedGuestDraw draw)
    {
        foreach (var buffer in draw.GlobalMemoryBuffers)
        {
            if (buffer.Pooled)
            {
                GuestDataPool.Shared.Return(buffer.Data);
            }
        }

        foreach (var vertexBuffer in draw.VertexBuffers)
        {
            if (vertexBuffer.Pooled)
            {
                GuestDataPool.Shared.Return(vertexBuffer.Data);
            }
        }
    }

    // MTLPrimitiveType: Point=0, Line=1, LineStrip=2, Triangle=3, TriangleStrip=4.
    private static nuint GetPrimitiveType(uint guestPrimitiveType)
    {
        switch (guestPrimitiveType)
        {
            case 1:
                return 0;
            case 2:
                return 1;
            case 3:
                return 2;
            case 5:
                // Metal has no triangle fans; a list is the closest safe shape.
                if (!_tracedTriangleFan)
                {
                    _tracedTriangleFan = true;
                    Console.Error.WriteLine(
                        "[LOADER][WARN] Metal has no triangle-fan primitive; drawing as a list.");
                }

                return 3;
            case 6:
            case 0x11:
                return 4;
            default:
                return 3;
        }
    }

    private static bool IsIntegerFormat(Gen5PixelOutputKind kind) =>
        kind is Gen5PixelOutputKind.Uint or Gen5PixelOutputKind.Sint;

    // Guest CB write-mask bits are R=1,G=2,B=4,A=8; MTLColorWriteMask reverses them.
    private static nuint ToMetalWriteMask(uint guestMask) =>
        ((guestMask & 1) != 0 ? 8u : 0u) |
        ((guestMask & 2) != 0 ? 4u : 0u) |
        ((guestMask & 4) != 0 ? 2u : 0u) |
        ((guestMask & 8) != 0 ? 1u : 0u);

    // Guest CB_BLEND factor codes to MTLBlendFactor, matching the Vulkan mapping.
    private static nuint ToMetalBlendFactor(uint factor) =>
        factor switch
        {
            0 => 0,   // Zero
            1 => 1,   // One
            2 => 2,   // SourceColor
            3 => 3,   // OneMinusSourceColor
            4 => 4,   // SourceAlpha
            5 => 5,   // OneMinusSourceAlpha
            6 => 8,   // DestinationAlpha
            7 => 9,   // OneMinusDestinationAlpha
            8 => 6,   // DestinationColor
            9 => 7,   // OneMinusDestinationColor
            10 => 10, // SourceAlphaSaturated
            13 => 11, // BlendColor
            14 => 12, // OneMinusBlendColor
            15 => 15, // Source1Color
            16 => 16, // OneMinusSource1Color
            17 => 17, // Source1Alpha
            18 => 18, // OneMinusSource1Alpha
            19 => 13, // BlendAlpha
            20 => 14, // OneMinusBlendAlpha
            _ => 1,
        };

    // Guest COMB_FCN codes to MTLBlendOperation (Add=0, Sub=1, RevSub=2, Min=3, Max=4).
    private static nuint ToMetalBlendOperation(uint function) =>
        function switch
        {
            0 => 0,
            1 => 1,
            2 => 3,
            3 => 4,
            4 => 2,
            _ => 0,
        };

    // Guest sampler clamp codes to MTLSamplerAddressMode, matching the Vulkan mapping.
    private static nuint ToMetalAddressMode(uint mode) =>
        mode switch
        {
            0 => 2,          // Repeat
            1 => 3,          // MirrorRepeat
            2 => 0,          // ClampToEdge
            3 or 5 or 7 => 1, // MirrorClampToEdge
            4 or 6 => 5,     // ClampToBorderColor
            _ => 0,
        };

    // Guest vertex (dataFormat, numberFormat) codes to MTLVertexFormat raw values,
    // mirroring the Vulkan attribute table; unmapped codes fall back to float{n}.
    private static nuint ToMetalVertexFormat(uint dataFormat, uint numberFormat, uint componentCount)
    {
        var format = (dataFormat, numberFormat) switch
        {
            (1, 0) => 47u,  // ucharNormalized
            (1, 1) => 48u,  // charNormalized
            (1, 4) => 45u,  // uchar
            (1, 5) => 46u,  // char
            (2, 0) => 51u,  // ushortNormalized
            (2, 1) => 52u,  // shortNormalized
            (2, 4) => 49u,  // ushort
            (2, 5) => 50u,  // short
            (2, 7) => 53u,  // half
            (3, 0) => 7u,   // uchar2Normalized
            (3, 1) => 10u,  // char2Normalized
            (3, 4) => 1u,   // uchar2
            (3, 5) => 4u,   // char2
            (4, 4) => 36u,  // uint
            (4, 5) => 32u,  // int
            (4, 7) => 28u,  // float
            (5, 0) => 19u,  // ushort2Normalized
            (5, 1) => 22u,  // short2Normalized
            (5, 4) => 13u,  // ushort2
            (5, 5) => 16u,  // short2
            (5, 7) => 25u,  // half2
            (6, 7) or (7, 7) => 54u, // floatRG11B10
            (8, 0) or (9, 0) => 41u, // uint1010102Normalized (R in bits 0..9)
            (8, 1) or (9, 1) => 40u, // int1010102Normalized
            (10, 0) => 9u,  // uchar4Normalized
            (10, 1) => 12u, // char4Normalized
            (10, 4) => 3u,  // uchar4
            (10, 5) => 6u,  // char4
            (11, 4) => 37u, // uint2
            (11, 5) => 33u, // int2
            (11, 7) => 29u, // float2
            (12, 0) => 21u, // ushort4Normalized
            (12, 1) or (12, 6) => 24u, // short4Normalized
            (12, 4) => 15u, // ushort4
            (12, 5) => 18u, // short4
            (12, 7) => 27u, // half4
            (13, 4) => 38u, // uint3
            (13, 5) => 34u, // int3
            (13, 7) => 30u, // float3
            (14, 4) => 39u, // uint4
            (14, 5) => 35u, // int4
            (14, 7) => 31u, // float4
            (34, 7) => 55u, // floatRGB9E5
            _ => 0u,
        };
        if (format != 0)
        {
            return format;
        }

        return componentCount switch
        {
            1 => 28,
            2 => 29,
            3 => 30,
            _ => 31,
        };
    }
}
