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

    /// <summary>Metal's vertex stage exposes buffer indices 0..30; setting a
    /// vertex-descriptor attribute to 31 is a framework assertion that aborts
    /// the process.</summary>
    private const nuint MaxVertexStageBufferIndex = 30;

    private static int _vertexSlotOverflowTraces;

    /// <summary>Assigns each vertex stream a Metal buffer slot, sharing one
    /// slot between attributes that read the same guest buffer — interleaved
    /// vertices arrive from AGC as one <see cref="GuestVertexBuffer"/> per
    /// attribute, so without sharing a handful of attributes exhausts the
    /// vertex-stage buffer range. Deterministic over the draw's buffer array;
    /// the pipeline descriptor and the bind path both derive from it. Returns
    /// false when the unique streams still overflow Metal's last slot.</summary>
    private static bool TryAssignVertexBufferSlots(
        GuestVertexBuffer[] vertexBuffers,
        Span<nuint> slots)
    {
        var uniqueCount = 0;
        var overflowed = false;
        for (var index = 0; index < vertexBuffers.Length; index++)
        {
            var buffer = vertexBuffers[index];
            var shared = false;
            if (buffer.BaseAddress != 0)
            {
                for (var prior = 0; prior < index; prior++)
                {
                    var candidate = vertexBuffers[prior];
                    if (candidate.BaseAddress == buffer.BaseAddress &&
                        candidate.Stride == buffer.Stride &&
                        candidate.Length == buffer.Length)
                    {
                        slots[index] = slots[prior];
                        shared = true;
                        break;
                    }
                }
            }

            if (shared)
            {
                continue;
            }

            slots[index] = VertexBufferSlotBase + (nuint)uniqueCount;
            uniqueCount++;
            overflowed |= slots[index] > MaxVertexStageBufferIndex;
        }

        return !overflowed;
    }
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

    // A depth target sampled later through its read address must resolve to
    // the same image the write address produced (the Vulkan presenter matches
    // either address on its depth resources).
    private static readonly Dictionary<ulong, ulong> _guestDepthReadAliases = new();

    // Retired same-address render targets: a game that recreates a target with
    // a new extent at the same address may still sample the old content later,
    // so replacement retires the image here instead of releasing it, and the
    // draw-texture path scores candidates like the Vulkan presenter's
    // guest-image variants. Bounded FIFO so stale variants cannot accumulate.
    private const int MaxGuestImageVariants = 32;
    private static readonly Dictionary<(ulong Address, uint Width, uint Height, MtlPixelFormat Format), GuestImage>
        _guestImageVariants = new();
    private static readonly Queue<(ulong Address, uint Width, uint Height, MtlPixelFormat Format)>
        _guestImageVariantOrder = new();
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
            depth = EnsureGuestDepthImage(device, depthTarget, depthWidth, depthHeight);
        }

        if (!TryGetDrawPipeline(device, draw, targetFormats, depth is not null, out var pipeline))
        {
            ReturnPooledGuestData(draw);
            return;
        }

        var commandBuffer = BeginBatchedGuestCommands(queue);
        Span<nint> textureHandles = stackalloc nint[draw.Textures.Length];
        Span<bool> textureOwned = stackalloc bool[draw.Textures.Length];
        ResolveDrawTextures(device, commandBuffer, draw.Textures, textureHandles, textureOwned);

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
        EncodeDrawBindings(device, encoder, work, textureHandles, textureOwned, out var writeBackBuffers);
        EncodeDrawCall(encoder, draw);

        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));

        // CPU-visible GPU writes are ordering points in the guest command
        // stream: completing this work item is the signal WaitForGuestWork
        // relies on, so the batch must land and the write-back must complete
        // before this item does. Pure-GPU draws stay in the open batch.
        if (writeBackBuffers.Count > 0)
        {
            var committed = FlushBatchedGuestCommands();
            MetalNative.SendVoid(committed, MetalNative.Selector("waitUntilCompleted"));
            WriteBuffersBackToGuest(writeBackBuffers);
        }

        for (var index = 0; index < publishedTargets.Length; index++)
        {
            if (publishedTargets[index] is { } published)
            {
                published.MarkContentChanged();
            }
        }

        depth?.MarkContentChanged();
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
        Span<nint> textureHandles = stackalloc nint[draw.Textures.Length];
        Span<bool> textureOwned = stackalloc bool[draw.Textures.Length];
        ResolveDrawTextures(device, commandBuffer, draw.Textures, textureHandles, textureOwned);
        var encoder = MetalNative.Send(
            commandBuffer,
            MetalNative.Selector("renderCommandEncoderWithDescriptor:"),
            CreateClearPass(target, new MtlClearColor { Alpha = 1 }));
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);
        var work = new OffscreenGuestDraw(draw, [], null, PublishTarget: false, ShaderAddress: 0);
        EncodeDrawBindings(device, encoder, work, textureHandles, textureOwned, out var writeBackBuffers);
        EncodeDrawCall(encoder, draw);
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
        MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
        TagUploadPages(commandBuffer);
        TagSnapshotResources(commandBuffer);
        if (writeBackBuffers.Count > 0)
        {
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("waitUntilCompleted"));
            WriteBuffersBackToGuest(writeBackBuffers);
        }

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
        // CB_BLEND_RED..ALPHA feed the CONSTANT_COLOR / CONSTANT_ALPHA blend
        // factors; encoder state, so set unconditionally like the Vulkan
        // presenter's dynamic blend constants.
        var blendConstant = renderState.BlendConstant;
        MetalNative.SendVoidBlendColor(
            encoder,
            MetalNative.Selector("setBlendColorRed:green:blue:alpha:"),
            blendConstant.Red,
            blendConstant.Green,
            blendConstant.Blue,
            blendConstant.Alpha);

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

    /// <summary>Builds and binds a stage's sampler argument buffer: one 8-byte
    /// Tier 2 resource ID per sampled image, written into an arena slice and
    /// bound at the shader's SamplerArgBufferIndex. The stage's images are
    /// draw.Textures[ImageBindingBase + j] for its j-th image, matching how the
    /// translator numbered SamplerSlots. No-op for stages that sample nothing.</summary>
    private static void BindSamplerArgumentBuffer(
        nint device,
        nint encoder,
        nint selSetBuffer,
        MetalCompiledGuestShader shader,
        GuestDrawTexture[] textures)
    {
        var slots = shader.Shader.SamplerSlots;
        var count = shader.Shader.SamplerCount;
        var argIndex = shader.Shader.SamplerArgBufferIndex;
        if (slots is null || count == 0 || argIndex < 0)
        {
            return;
        }

        var imageBase = shader.Shader.ImageBindingBase;
        var slice = AllocateUpload(device, count * sizeof(ulong), out var buffer, out var offset);
        slice.Clear();
        for (var j = 0; j < slots.Count; j++)
        {
            var slot = slots[j];
            if (slot < 0)
            {
                continue;
            }

            var sampler = GetOrCreateSampler(device, textures[imageBase + j].Sampler);
            var resourceId = MetalNative.SendGpuResourceId(sampler, MetalNative.Selector("gpuResourceID"));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                slice[(slot * sizeof(ulong))..], resourceId);
        }

        MetalNative.SendSetBuffer(encoder, selSetBuffer, buffer, (nuint)offset, (nuint)argIndex);
    }

    /// <summary>Resolves every texture a draw samples, encoding any snapshot
    /// blits into <paramref name="blitCommandBuffer"/>; must run before the
    /// consuming encoder opens on that command buffer.</summary>
    private static void ResolveDrawTextures(
        nint device,
        nint blitCommandBuffer,
        GuestDrawTexture[] textures,
        Span<nint> handles,
        Span<bool> owned)
    {
        for (var index = 0; index < textures.Length; index++)
        {
            handles[index] = CreateDrawTexture(
                device, blitCommandBuffer, textures[index], out var ownedTexture);
            owned[index] = ownedTexture;
        }
    }

    /// <summary>Binds everything the translation contract names: global buffers
    /// and SharpEmuUniforms to both stages, the pre-resolved textures/samplers
    /// to both stages, vertex streams at the high slots. Collects the writable
    /// buffers for guest write-back.</summary>
    private static void EncodeDrawBindings(
        nint device,
        nint encoder,
        OffscreenGuestDraw work,
        ReadOnlySpan<nint> textureHandles,
        ReadOnlySpan<bool> textureOwned,
        out List<(nint Pointer, GuestMemoryBuffer Guest)> writeBackBuffers)
    {
        var draw = work.Draw;
        writeBackBuffers = [];

        var selSetVertexBuffer = MetalNative.Selector("setVertexBuffer:offset:atIndex:");
        var selSetFragmentBuffer = MetalNative.Selector("setFragmentBuffer:offset:atIndex:");
        var bufferCount = draw.GlobalMemoryBuffers.Length;
        Span<uint> boundBytes = stackalloc uint[Math.Max(bufferCount, 1)];
        for (var index = 0; index < bufferCount; index++)
        {
            var guest = draw.GlobalMemoryBuffers[index];
            var pointer = UploadGlobalBuffer(
                device, guest, out var buffer, out var offset, out boundBytes[index]);
            MetalNative.SendSetBuffer(encoder, selSetVertexBuffer, buffer, (nuint)offset, (nuint)index);
            MetalNative.SendSetBuffer(encoder, selSetFragmentBuffer, buffer, (nuint)offset, (nuint)index);
            if (guest.Writable && guest.WriteBackToGuest)
            {
                writeBackBuffers.Add((pointer, guest));
            }
        }

        // SharpEmuUniforms per the translation contract: dispatch limit (unused by
        // graphics stages), reserved, then each bound buffer's byte length
        // (including the alignment-bias prefix the shader indexes past).
        var uniforms = AllocateUpload(
            device,
            16 + (Math.Max(bufferCount, 1) * sizeof(uint)),
            out var uniformsBuffer,
            out var uniformsOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms[4..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms[8..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms[12..], 0);
        for (var index = 0; index < bufferCount; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                uniforms[(16 + (index * sizeof(uint)))..],
                boundBytes[index]);
        }

        // Each stage declares SharpEmuUniforms at its own translation-time index
        // (globalBufferBase + totalGlobalBufferCount). A draw whose vertex-stage
        // guest buffers sit after the pixel stage's gives the two stages
        // different indices, so bind the buffer at each stage's declared slot —
        // one shared index leaves the other stage's uniforms unbound, which
        // zeroes its bounds-checked loads (caught by Metal API validation as
        // "missing Buffer binding ... for sharpemu_uniforms").
        var vertexUniformsIndex = draw.VertexShader?.Shader.UniformsBufferIndex ?? -1;
        MetalNative.SendSetBuffer(
            encoder,
            selSetVertexBuffer,
            uniformsBuffer,
            (nuint)uniformsOffset,
            (nuint)(vertexUniformsIndex >= 0 ? vertexUniformsIndex : bufferCount));
        var fragmentUniformsIndex = draw.PixelShader.Shader.UniformsBufferIndex;
        MetalNative.SendSetBuffer(
            encoder,
            selSetFragmentBuffer,
            uniformsBuffer,
            (nuint)uniformsOffset,
            (nuint)(fragmentUniformsIndex >= 0 ? fragmentUniformsIndex : bufferCount));

        var selSetVertexTexture = MetalNative.Selector("setVertexTexture:atIndex:");
        var selSetFragmentTexture = MetalNative.Selector("setFragmentTexture:atIndex:");
        // Texture slots are global across the draw's stages ([0, vertexImageBase)
        // is the pixel stage's block), so textures bind to both stage tables at
        // their global index.
        for (var index = 0; index < draw.Textures.Length; index++)
        {
            var texture = textureHandles[index];
            if (texture != 0)
            {
                MetalNative.SendSetAtIndex(encoder, selSetVertexTexture, texture, (nuint)index);
                MetalNative.SendSetAtIndex(encoder, selSetFragmentTexture, texture, (nuint)index);
                if (textureOwned[index])
                {
                    MetalNative.SendVoid(texture, MetalNative.Selector("release"));
                }
            }
        }

        // Samplers travel in a per-stage argument buffer (Metal caps direct
        // sampler slots at 16 per stage, but shaders sample more), one entry
        // per sampled image.
        BindSamplerArgumentBuffer(device, encoder, selSetFragmentBuffer, draw.PixelShader, draw.Textures);
        if (draw.VertexShader is { } vertexShader)
        {
            BindSamplerArgumentBuffer(device, encoder, selSetVertexBuffer, vertexShader, draw.Textures);
        }

        Span<nuint> vertexSlots = stackalloc nuint[draw.VertexBuffers.Length];
        _ = TryAssignVertexBufferSlots(draw.VertexBuffers, vertexSlots);
        for (var index = 0; index < draw.VertexBuffers.Length; index++)
        {
            // Streams sharing a slot read the same guest bytes; the first
            // occurrence uploads and binds them once.
            var duplicate = false;
            for (var prior = 0; prior < index; prior++)
            {
                if (vertexSlots[prior] == vertexSlots[index])
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
            {
                continue;
            }

            var vertexBuffer = draw.VertexBuffers[index];
            var length = Math.Max(vertexBuffer.Length, 1);
            var slice = AllocateUpload(device, length, out var buffer, out var offset);
            vertexBuffer.Data.AsSpan(0, Math.Min(vertexBuffer.Length, vertexBuffer.Data.Length))
                .CopyTo(slice);
            // The attribute's byte offset (set in the vertex descriptor)
            // selects the field inside the interleaved vertex; the bind
            // offset is just the arena slice.
            MetalNative.SendSetBuffer(
                encoder, selSetVertexBuffer, buffer, (nuint)offset, vertexSlots[index]);
        }
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
            var slice = AllocateUpload(
                device, Math.Max(indexBuffer.Length, 1), out var buffer, out var offset);
            indexBuffer.Data.AsSpan(0, Math.Min(indexBuffer.Length, indexBuffer.Data.Length))
                .CopyTo(slice);
            MetalNative.SendDrawIndexedPrimitives(
                encoder,
                MetalNative.Selector("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:"),
                primitive,
                vertexCount,
                indexBuffer.Is32Bit ? 1u : 0u,
                buffer,
                (nuint)offset,
                Math.Max(draw.InstanceCount, 1));
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
        Span<nuint> vertexSlots = stackalloc nuint[draw.VertexBuffers.Length];
        if (!TryAssignVertexBufferSlots(draw.VertexBuffers, vertexSlots))
        {
            if (_vertexSlotOverflowTraces < 16)
            {
                _vertexSlotOverflowTraces++;
                Console.Error.WriteLine(
                    "[LOADER][WARN] Metal skipped draw: " +
                    $"{draw.VertexBuffers.Length} vertex streams need more than " +
                    $"{MaxVertexStageBufferIndex - VertexBufferSlotBase + 1} unique buffer slots.");
            }

            pipeline = 0;
            return false;
        }

        for (var index = 0; index < draw.VertexBuffers.Length; index++)
        {
            var vertexBuffer = draw.VertexBuffers[index];
            Mix(vertexBuffer.Location |
                ((ulong)vertexBuffer.ComponentCount << 8) |
                ((ulong)vertexBuffer.DataFormat << 16) |
                ((ulong)vertexBuffer.NumberFormat << 26) |
                ((ulong)vertexBuffer.Stride << 34));
            // The attribute byte offset and the assigned slot are baked into
            // the pipeline's vertex descriptor, so both must key the cache
            // (the slot captures which streams alias one guest buffer).
            Mix(vertexBuffer.OffsetBytes | ((ulong)vertexSlots[index] << 32));
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
        Span<nuint> slots = stackalloc nuint[vertexBuffers.Length];
        _ = TryAssignVertexBufferSlots(vertexBuffers, slots);
        for (var index = 0; index < vertexBuffers.Length; index++)
        {
            var vertexBuffer = vertexBuffers[index];
            var slot = slots[index];
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
                RetireGuestImageVariantLocked(target.Address, replaced);
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
            // A guest-memory seed initializes content without marking it
            // GPU-produced; the version still moves so snapshots refresh.
            image.Initialized = true;
            image.ContentVersion++;
        }

        return image;
    }

    private static void RetireGuestImageVariantLocked(ulong address, GuestImage retired)
    {
        var key = (address, retired.Width, retired.Height, retired.Format);
        if (_guestImageVariants.Remove(key, out var previous))
        {
            previous.ReleaseSnapshot();
            MetalNative.SendVoid(previous.Texture, MetalNative.Selector("release"));
        }
        else
        {
            while (_guestImageVariantOrder.Count >= MaxGuestImageVariants)
            {
                var evicted = _guestImageVariantOrder.Dequeue();
                if (_guestImageVariants.Remove(evicted, out var old))
                {
                    old.ReleaseSnapshot();
                    MetalNative.SendVoid(old.Texture, MetalNative.Selector("release"));
                }
            }

            _guestImageVariantOrder.Enqueue(key);
        }

        _guestImageVariants[key] = retired;
    }

    private static GuestImage EnsureGuestDepthImage(
        nint device,
        GuestDepthTarget target,
        uint width,
        uint height)
    {
        var address = target.Address;
        lock (_gate)
        {
            if (target.ReadAddress != 0 && target.ReadAddress != address)
            {
                _guestDepthReadAliases[target.ReadAddress] = address;
            }

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
                replaced.ReleaseSnapshot();
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

    /// <summary>Resolves one draw texture. Snapshot copies for feedback reads
    /// are encoded into <paramref name="blitCommandBuffer"/>, so this must be
    /// called before the consuming render or compute encoder opens on that
    /// same command buffer — encoder order is what keeps the snapshot after
    /// earlier batched passes that render to the source image.</summary>
    private static nint CreateDrawTexture(
        nint device,
        nint blitCommandBuffer,
        GuestDrawTexture texture,
        out bool ownedByCaller)
    {
        ownedByCaller = true;
        if (texture.Width == 0 || texture.Height == 0)
        {
            return 0;
        }

        var cacheable = IsCacheableDrawTexture(texture);

        // Feedback reads of a live guest render target sample an ordered
        // snapshot, resolved like the Vulkan presenter: a guest depth image
        // first (shadow-style depth sampling), then the current image or a
        // retired variant at the same address, scored by descriptor match.
        if (texture.RgbaPixels.Length == 0 && texture.Address != 0)
        {
            if (TryCreateDepthSampleTexture(device, blitCommandBuffer, texture, out var depthSample))
            {
                ownedByCaller = false;
                return depthSample;
            }

            var live = ResolveGuestImageAlias(texture);
            if (live is { Initialized: true } && blitCommandBuffer != 0)
            {
                // One snapshot per content version: draws sampling the same
                // unchanged image share it, so the blit happens per content
                // change instead of per draw — compositing games otherwise
                // copy a full render target for every draw. The image holds
                // the retain; consuming command buffers keep replaced
                // snapshots alive until they complete.
                if (live.SnapshotTexture != 0 && live.SnapshotVersion == live.ContentVersion)
                {
                    ownedByCaller = false;
                    return live.SnapshotTexture;
                }

                var snapshot = CreateGuestTexture(device, live.Format, live.Width, live.Height);
                if (snapshot != 0)
                {
                    EncodeCopyTexture(blitCommandBuffer, live.Texture, snapshot);
                    live.ReleaseSnapshot();
                    live.SnapshotTexture = snapshot;
                    live.SnapshotVersion = live.ContentVersion;
                    ownedByCaller = false;
                    return snapshot;
                }
            }

            // Empty texels can also mean the submit thread skipped the
            // guest-memory copy because this identity is cached here.
            if (cacheable && TryGetCachedDrawTexture(texture, out var cachedSkip))
            {
                ownedByCaller = false;
                return cachedSkip;
            }

            // A miss on skipped texels is an invalidation race (the entry
            // was evicted after the submit thread checked). Self-heal by
            // reading the texels directly rather than rendering a fallback.
            var refreshed = TryReadGuestDrawTexturePixels(texture);
            if (refreshed is null)
            {
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

            texture = texture with { RgbaPixels = refreshed };
        }
        else if (cacheable && TryGetCachedDrawTexture(texture, out var cached))
        {
            // Fresh texels for an identity already cached: the content is
            // unchanged (a guest write would have evicted the entry at drain
            // start), so skip the redundant texture creation and upload.
            ownedByCaller = false;
            return cached;
        }

        // AGC ships the raw (detiled) source texels; create the texture in the
        // guest's native format — Mac-family GPUs sample BC blocks directly —
        // and size expectations with the same block-aware math AGC used.
        var textureFormat = MetalGuestFormats.DecodeTextureFormat(texture.Format, texture.NumberType);
        var pitch = texture.Pitch != 0 ? Math.Max(texture.Pitch, texture.Width) : texture.Width;
        var expectedBytes = MetalGuestFormats.GetTextureByteCount(textureFormat, pitch, texture.Height);
        if ((ulong)texture.RgbaPixels.Length < expectedBytes)
        {
            if (_missingTextureTraces < 16)
            {
                _missingTextureTraces++;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Metal draw texture undersized: 0x{texture.Address:X} " +
                    $"{texture.Width}x{texture.Height} pitch={texture.Pitch} " +
                    $"fmt={texture.Format}/{texture.NumberType} " +
                    $"bytes={texture.RgbaPixels.Length} expected={expectedBytes}");
            }

            return 0;
        }

        var descriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)textureFormat.Format,
            texture.Width,
            texture.Height,
            mipmapped: false);
        // MTLStorageModeShared (0): CPU-uploaded (replaceRegion) + GPU-sampled;
        // the Managed default reads stale on unified memory (see CreateGuestTexture).
        MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), (nint)0);
        if (texture.IsStorage)
        {
            MetalNative.Send(
                descriptor,
                MetalNative.Selector("setUsage:"),
                (nint)(UsageShaderRead | UsageShaderWrite));
        }
        else if (texture.DstSelect != 0xFAC && texture.DstSelect != 0)
        {
            // Channel select from the guest descriptor, like the Vulkan view's
            // component mapping. Shader-writable textures reject swizzles, so
            // storage stays identity (matching Vulkan, which never swizzles
            // storage views either).
            MetalNative.SendVoidSwizzle(
                descriptor,
                MetalNative.Selector("setSwizzle:"),
                ToMetalSwizzle(texture.DstSelect));
        }

        var handle = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        if (handle != 0)
        {
            ReplaceDrawTextureContents(handle, texture, pitch, textureFormat);
            if (cacheable)
            {
                CacheDrawTexture(texture, handle);
            }
        }

        return handle;
    }

    /// <summary>Uploads a draw texture's source texels: linear formats reuse the
    /// row-clamping helper; block-compressed formats upload whole 4x4 block rows
    /// with the block-row stride replaceRegion expects.</summary>
    private static unsafe void ReplaceDrawTextureContents(
        nint handle,
        GuestDrawTexture texture,
        uint pitch,
        in MetalTextureFormat format)
    {
        if (!format.IsBlockCompressed)
        {
            ReplaceTextureContents(
                handle, texture.Width, texture.Height, texture.RgbaPixels, pitch, format.BytesPerPixel);
            return;
        }

        var blocksWide = ((ulong)Math.Max(pitch, texture.Width) + 3) / 4;
        var bytesPerBlockRow = blocksWide * format.BlockBytes;
        if (bytesPerBlockRow == 0)
        {
            return;
        }

        var blockRows = Math.Min(
            ((ulong)texture.Height + 3) / 4,
            (ulong)texture.RgbaPixels.Length / bytesPerBlockRow);
        if (blockRows == 0)
        {
            return;
        }

        var texelRows = Math.Min(texture.Height, (uint)(blockRows * 4));
        fixed (byte* source = texture.RgbaPixels)
        {
            MetalNative.SendReplaceRegion(
                handle,
                MetalNative.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                new MtlRegion { X = 0, Y = 0, Z = 0, Width = texture.Width, Height = texelRows, Depth = 1 },
                0,
                (nint)source,
                (nuint)bytesPerBlockRow);
        }
    }

    /// <summary>Guest DST_SEL (3 bits per channel: 0=zero, 1=one, 4..7=RGBA) to
    /// Metal swizzle bytes, mirroring the Vulkan view's component mapping.</summary>
    private static MtlTextureSwizzleChannels ToMetalSwizzle(uint dstSelect) => new()
    {
        Red = ToMetalSwizzleChannel(dstSelect & 0x7, identity: 2),
        Green = ToMetalSwizzleChannel((dstSelect >> 3) & 0x7, identity: 3),
        Blue = ToMetalSwizzleChannel((dstSelect >> 6) & 0x7, identity: 4),
        Alpha = ToMetalSwizzleChannel((dstSelect >> 9) & 0x7, identity: 5),
    };

    private static byte ToMetalSwizzleChannel(uint selector, byte identity) =>
        selector switch
        {
            0 => 0,
            1 => 1,
            4 => 2,
            5 => 3,
            6 => 4,
            7 => 5,
            _ => identity,
        };

    /// <summary>Resolves a live guest texture to the current image or a retired
    /// same-address variant, scored by descriptor match like the Vulkan
    /// presenter's guest-image variants: exact extent outranks format, format
    /// outranks initialization, and the active image breaks ties.</summary>
    private static GuestImage? ResolveGuestImageAlias(GuestDrawTexture texture)
    {
        var hasViewFormat = MetalGuestFormats.TryDecodeRenderTargetFormat(
            texture.Format, texture.NumberType, out var viewFormat);
        GuestImage? best = null;
        var bestScore = int.MinValue;

        void Consider(GuestImage candidate, bool isActive)
        {
            // Exact extent always qualifies; a larger image qualifies only for
            // tiled descriptors, mirroring IsCompatibleGuestImageAlias.
            var sizeMatch = candidate.Width == texture.Width &&
                candidate.Height == texture.Height;
            if (!sizeMatch &&
                (texture.TileMode == 0 ||
                 texture.Width == 0 ||
                 texture.Height == 0 ||
                 texture.Width > candidate.Width ||
                 texture.Height > candidate.Height))
            {
                return;
            }

            var score = 0;
            if (sizeMatch)
            {
                score += 32;
            }

            if (hasViewFormat && candidate.Format == viewFormat.Format)
            {
                score += 16;
            }

            if (candidate.Initialized)
            {
                score += 4;
            }

            if (isActive)
            {
                score += 1;
            }

            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        lock (_gate)
        {
            if (_guestImages.TryGetValue(texture.Address, out var active))
            {
                Consider(active, isActive: true);
            }

            foreach (var (key, candidate) in _guestImageVariants)
            {
                if (key.Address == texture.Address)
                {
                    Consider(candidate, isActive: false);
                }
            }
        }

        return best;
    }

    /// <summary>Snapshots a guest depth image for sampling when the texture
    /// descriptor names a depth target's write or read address. Depth32Float
    /// cannot blit to a color format, so the copy round-trips through a
    /// buffer into an R32Float texture the translated shader can sample.</summary>
    private static bool TryCreateDepthSampleTexture(
        nint device,
        nint blitCommandBuffer,
        GuestDrawTexture texture,
        out nint sample)
    {
        sample = 0;
        // Identity channel select only; swizzled depth reads keep the
        // unresolved-texture warning until a title needs them.
        if (texture.DstSelect != 0xFAC || blitCommandBuffer == 0)
        {
            return false;
        }

        GuestImage? depth;
        lock (_gate)
        {
            if (!_guestDepthImages.TryGetValue(texture.Address, out depth) &&
                _guestDepthReadAliases.TryGetValue(texture.Address, out var primary))
            {
                _guestDepthImages.TryGetValue(primary, out depth);
            }
        }

        if (depth is null ||
            !depth.Initialized ||
            texture.Width > depth.Width ||
            texture.Height > depth.Height)
        {
            return false;
        }

        var bytesPerRow = (nuint)depth.Width * 4;
        var bytesPerImage = bytesPerRow * depth.Height;
        var staging = AcquireSnapshotBuffer(device, bytesPerImage);
        if (staging == 0)
        {
            return false;
        }

        sample = AcquireSnapshotTexture(
            device,
            MtlPixelFormat.R32Float,
            depth.Width,
            depth.Height,
            (nint)UsageShaderRead);
        if (sample == 0)
        {
            return false;
        }

        var blit = MetalNative.Send(blitCommandBuffer, MetalNative.Selector("blitCommandEncoder"));
        var size = new MtlSize { Width = depth.Width, Height = depth.Height, Depth = 1 };
        MetalNative.SendCopyTextureToBuffer(
            blit,
            MetalNative.Selector(
                "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:" +
                "toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
            depth.Texture,
            0,
            0,
            default,
            size,
            staging,
            0,
            bytesPerRow,
            bytesPerImage);
        MetalNative.SendCopyBufferToTexture(
            blit,
            MetalNative.Selector(
                "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:" +
                "toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
            staging,
            0,
            bytesPerRow,
            bytesPerImage,
            size,
            sample,
            0,
            0,
            default);
        MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding"));
        return true;
    }

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
    // on the real data: the slice holds bias + length bytes with the data at
    // offset bias, matching how the Vulkan backend binds into a larger
    // allocation at an aligned-down descriptor offset. boundBytes is what
    // SharpEmuUniforms must carry so the shader's bounds check passes. The
    // returned pointer addresses the data (past the bias) for write-backs.
    private const ulong StorageBufferOffsetAlignment = 256;

    private static unsafe nint UploadGlobalBuffer(
        nint device,
        GuestMemoryBuffer guest,
        out nint buffer,
        out int offset,
        out uint boundBytes)
    {
        var bias = (int)((ulong)guest.BaseAddress & (StorageBufferOffsetAlignment - 1));
        var length = Math.Clamp(guest.Length, 0, guest.Data.Length);
        boundBytes = (uint)(bias + length);
        var slice = AllocateUpload(device, bias + Math.Max(length, 1), out buffer, out offset);
        if (bias != 0)
        {
            // Deterministic zeros below the bias, like the padded copy had.
            slice[..bias].Clear();
        }

        guest.Data.AsSpan(0, length).CopyTo(slice[bias..]);
        fixed (byte* data = slice)
        {
            return (nint)(data + bias);
        }
    }

    private static void WriteBuffersBackToGuest(
        List<(nint Pointer, GuestMemoryBuffer Guest)> writeBackBuffers)
    {
        var memory = _guestMemory;
        if (memory is null)
        {
            return;
        }

        foreach (var (pointer, guest) in writeBackBuffers)
        {
            unsafe
            {
                // The pointer addresses the slice's data (past the alignment
                // bias) inside its shared-storage arena page, which stays
                // alive until the command buffer completes — and the caller
                // waited on that before reading.
                _ = memory.TryWrite(
                    guest.BaseAddress,
                    new ReadOnlySpan<byte>((void*)pointer, guest.Length));
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
