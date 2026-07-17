// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Metal;

// Guest compute dispatches: ordered guest work like draws, with two contracts to
// honor. Storage images are shared live through the guest-image registry so a
// dispatch's writes are visible to later draws, blits, and flips of the same
// address; and CPU-visible buffer writes land back in guest memory before the
// work item completes, which is the ordering point WaitForGuestWork promises.
internal static partial class MetalVideoPresenter
{
    private static readonly bool _skipAllCompute =
        Environment.GetEnvironmentVariable("SHARPEMU_SKIP_ALL_COMPUTE") == "1";
    private static bool _tracedDispatchBase;

    private sealed record ComputeGuestDispatch(
        ulong ShaderAddress,
        MetalCompiledGuestShader Shader,
        GuestDrawTexture[] Textures,
        GuestMemoryBuffer[] GlobalMemoryBuffers,
        uint GroupCountX,
        uint GroupCountY,
        uint GroupCountZ,
        uint BaseGroupX,
        uint BaseGroupY,
        uint BaseGroupZ,
        uint ThreadCountX,
        uint ThreadCountY,
        uint ThreadCountZ);

    private static readonly Dictionary<MetalCompiledGuestShader, nint> _computePipelineCache = new();

    public static long SubmitComputeDispatch(
        ulong shaderAddress,
        MetalCompiledGuestShader computeShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint baseGroupX,
        uint baseGroupY,
        uint baseGroupZ,
        bool writesGlobalMemory,
        uint threadCountX,
        uint threadCountY,
        uint threadCountZ)
    {
        var hasStorage = false;
        foreach (var texture in textures)
        {
            hasStorage |= texture.IsStorage;
        }

        if (groupCountX == 0 ||
            groupCountY == 0 ||
            groupCountZ == 0 ||
            (!hasStorage && !writesGlobalMemory))
        {
            return 0;
        }

        lock (_gate)
        {
            if (_closed || _thread is null)
            {
                return 0;
            }

            // Storage images a dispatch writes become flip sources and sampled
            // inputs for later work, exactly like published render targets.
            foreach (var texture in textures)
            {
                if (!texture.IsStorage || texture.Address == 0)
                {
                    continue;
                }

                var guestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType);
                if (guestFormat != 0)
                {
                    _availableGuestImages[texture.Address] = guestFormat;
                }
            }

            var sequence = EnqueueGuestWorkLocked(
                new ComputeGuestDispatch(
                    shaderAddress,
                    computeShader,
                    ToArray(textures),
                    ToArray(globalMemoryBuffers),
                    groupCountX,
                    groupCountY,
                    groupCountZ,
                    baseGroupX,
                    baseGroupY,
                    baseGroupZ,
                    threadCountX,
                    threadCountY,
                    threadCountZ));
            foreach (var texture in textures)
            {
                if (texture.IsStorage && texture.Address != 0)
                {
                    _guestImageWorkSequences[texture.Address] = sequence;
                }
            }

            return sequence;
        }
    }

    private static void ExecuteComputeDispatch(nint device, nint queue, ComputeGuestDispatch dispatch)
    {
        if (_skipAllCompute)
        {
            ReturnPooledComputeData(dispatch);
            return;
        }

        VideoOut.PerfOverlay.RecordDraw();

        if ((dispatch.BaseGroupX | dispatch.BaseGroupY | dispatch.BaseGroupZ) != 0 &&
            !_tracedDispatchBase)
        {
            // Metal has no dispatch-base; the translated kernel derives its ids
            // from the raw grid position, so a nonzero base computes offset-zero
            // work until base support lands in the emitted kernel.
            _tracedDispatchBase = true;
            Console.Error.WriteLine(
                "[LOADER][WARN] Metal compute dispatch with nonzero base group " +
                $"({dispatch.BaseGroupX},{dispatch.BaseGroupY},{dispatch.BaseGroupZ}); " +
                "executing without the base offset.");
        }

        if (!TryGetComputePipeline(device, dispatch.Shader, out var pipeline))
        {
            ReturnPooledComputeData(dispatch);
            return;
        }

        var commandBuffer = BeginBatchedGuestCommands(queue);

        // Pre-resolve textures before the compute encoder opens: snapshot
        // blits for feedback reads encode into the batch and encoder order
        // must place them ahead of this dispatch.
        Span<nint> textureHandles = stackalloc nint[dispatch.Textures.Length];
        Span<bool> textureOwned = stackalloc bool[dispatch.Textures.Length];
        for (var index = 0; index < dispatch.Textures.Length; index++)
        {
            var descriptor = dispatch.Textures[index];
            if (descriptor.IsStorage && descriptor.Address != 0)
            {
                textureHandles[index] = EnsureStorageImage(device, descriptor)?.Texture ?? 0;
                textureOwned[index] = false;
            }
            else
            {
                textureHandles[index] = CreateDrawTexture(
                    device, commandBuffer, descriptor, out var ownedTexture);
                textureOwned[index] = ownedTexture;
            }
        }

        var encoder = MetalNative.Send(commandBuffer, MetalNative.Selector("computeCommandEncoder"));
        MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), pipeline);

        var writeBackBuffers = new List<(nint Pointer, GuestMemoryBuffer Guest)>();
        var selSetBuffer = MetalNative.Selector("setBuffer:offset:atIndex:");
        var bufferCount = dispatch.GlobalMemoryBuffers.Length;
        Span<uint> boundBytes = stackalloc uint[Math.Max(bufferCount, 1)];
        for (var index = 0; index < bufferCount; index++)
        {
            var guest = dispatch.GlobalMemoryBuffers[index];
            var pointer = UploadGlobalBuffer(
                device, guest, out var buffer, out var offset, out boundBytes[index]);
            MetalNative.SendSetBuffer(encoder, selSetBuffer, buffer, (nuint)offset, (nuint)index);
            if (guest.Writable && guest.WriteBackToGuest)
            {
                writeBackBuffers.Add((pointer, guest));
            }
        }

        // SharpEmuUniforms: the dispatch limit clamps the overshoot threads of the
        // last threadgroup row, then each bound buffer's byte length follows
        // (including the alignment-bias prefix the shader indexes past).
        var shader = dispatch.Shader.Shader;
        var uniforms = AllocateUpload(
            device,
            16 + (Math.Max(bufferCount, 1) * sizeof(uint)),
            out var uniformsBuffer,
            out var uniformsOffset);
        WriteDispatchLimit(uniforms, 0, dispatch.ThreadCountX, dispatch.GroupCountX, shader.ThreadgroupSizeX);
        WriteDispatchLimit(uniforms, 4, dispatch.ThreadCountY, dispatch.GroupCountY, shader.ThreadgroupSizeY);
        WriteDispatchLimit(uniforms, 8, dispatch.ThreadCountZ, dispatch.GroupCountZ, shader.ThreadgroupSizeZ);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(uniforms[12..], 0);
        for (var index = 0; index < bufferCount; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                uniforms[(16 + (index * sizeof(uint)))..],
                boundBytes[index]);
        }

        // Bind at the stage's declared SharpEmuUniforms slot (see the draw path:
        // stages compute their own index from globalBufferBase + total count).
        var uniformsIndex = shader.UniformsBufferIndex;
        MetalNative.SendSetBuffer(
            encoder,
            selSetBuffer,
            uniformsBuffer,
            (nuint)uniformsOffset,
            (nuint)(uniformsIndex >= 0 ? uniformsIndex : bufferCount));

        var selSetTexture = MetalNative.Selector("setTexture:atIndex:");
        for (var index = 0; index < dispatch.Textures.Length; index++)
        {
            var texture = textureHandles[index];
            if (texture != 0)
            {
                MetalNative.SendSetAtIndex(encoder, selSetTexture, texture, (nuint)index);
                if (textureOwned[index])
                {
                    MetalNative.SendVoid(texture, MetalNative.Selector("release"));
                }
            }
        }

        // Samplers travel in an argument buffer bound at setBuffer (see the draw
        // path), sidestepping Metal's 16-sampler-per-stage cap.
        BindSamplerArgumentBuffer(device, encoder, selSetBuffer, dispatch.Shader, dispatch.Textures);

        MetalNative.SendDispatch(
            encoder,
            MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:"),
            new MtlSize
            {
                Width = dispatch.GroupCountX,
                Height = dispatch.GroupCountY,
                Depth = dispatch.GroupCountZ,
            },
            new MtlSize
            {
                Width = Math.Max(shader.ThreadgroupSizeX, 1),
                Height = Math.Max(shader.ThreadgroupSizeY, 1),
                Depth = Math.Max(shader.ThreadgroupSizeZ, 1),
            });
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));

        // CPU-visible writes are ordering points (see the draw path): flush
        // the batch and wait so the write-back lands before this work item
        // completes. Pure-GPU dispatches stay in the open batch.
        if (writeBackBuffers.Count > 0)
        {
            var committed = FlushBatchedGuestCommands();
            MetalNative.SendVoid(committed, MetalNative.Selector("waitUntilCompleted"));
            WriteBuffersBackToGuest(writeBackBuffers);
        }

        foreach (var descriptor in dispatch.Textures)
        {
            if (!descriptor.IsStorage || descriptor.Address == 0)
            {
                continue;
            }

            GuestImage? image;
            lock (_gate)
            {
                _guestImages.TryGetValue(descriptor.Address, out image);
            }

            if (image is not null)
            {
                image.MarkContentChanged();
            }
        }

        ReturnPooledComputeData(dispatch);
    }

    /// <summary>The live, shared storage image for a guest address: dispatches,
    /// draws, blits, and flips of the same address all see one texture.</summary>
    private static GuestImage? EnsureStorageImage(nint device, GuestDrawTexture descriptor)
    {
        lock (_gate)
        {
            if (_guestImages.TryGetValue(descriptor.Address, out var existing))
            {
                return existing;
            }
        }

        if (descriptor.Width == 0 || descriptor.Height == 0 ||
            descriptor.Width > 16384 || descriptor.Height > 16384)
        {
            return null;
        }

        var format = MetalGuestFormats.TryDecodeRenderTargetFormat(
            descriptor.Format, descriptor.NumberType, out var decoded)
            ? decoded.Format
            : MtlPixelFormat.Rgba8Unorm;
        var textureDescriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format,
            descriptor.Width,
            descriptor.Height,
            mipmapped: false);
        MetalNative.Send(
            textureDescriptor,
            MetalNative.Selector("setUsage:"),
            (nint)(UsageShaderRead | UsageShaderWrite | UsageRenderTarget));
        var image = new GuestImage
        {
            Texture = MetalNative.Send(
                device, MetalNative.Selector("newTextureWithDescriptor:"), textureDescriptor),
            Width = descriptor.Width,
            Height = descriptor.Height,
            Format = format,
        };
        if (image.Texture == 0)
        {
            return null;
        }

        var bytesPerPixel = MetalRenderTargetFormat.GetBytesPerPixel(format);
        // Snapshot copies arrive in the image's native texel layout; only
        // 4-byte texels can be RGBA8 verbatim, wider ones carry native bytes.
        if ((ulong)descriptor.RgbaPixels.Length >= (ulong)descriptor.Width * bytesPerPixel)
        {
            var pitch = descriptor.Pitch != 0
                ? Math.Max(descriptor.Pitch, descriptor.Width)
                : descriptor.Width;
            ReplaceTextureContents(
                image.Texture, descriptor.Width, descriptor.Height, descriptor.RgbaPixels, pitch, bytesPerPixel);
            image.MarkContentChanged();
        }

        lock (_gate)
        {
            if (_guestImages.TryGetValue(descriptor.Address, out var raced))
            {
                MetalNative.SendVoid(image.Texture, MetalNative.Selector("release"));
                return raced;
            }

            _guestImages[descriptor.Address] = image;
            _guestImageExtents[descriptor.Address] =
                (descriptor.Width, descriptor.Height, (ulong)descriptor.Width * descriptor.Height * bytesPerPixel);
        }

        return image;
    }

    private static bool TryGetComputePipeline(nint device, MetalCompiledGuestShader shader, out nint pipeline)
    {
        lock (_computePipelineCache)
        {
            if (_computePipelineCache.TryGetValue(shader, out pipeline))
            {
                return pipeline != 0;
            }
        }

        var function = GetShaderFunction(device, shader);
        if (function != 0)
        {
            nint error = 0;
            pipeline = MetalNative.Send(
                device,
                MetalNative.Selector("newComputePipelineStateWithFunction:error:"),
                function,
                ref error);
            if (pipeline == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Metal compute pipeline creation failed: {MetalNative.DescribeError(error)}");
            }
            else
            {
                Interlocked.Increment(ref _perfPipelineCreations);
            }
        }
        else
        {
            pipeline = 0;
        }

        lock (_computePipelineCache)
        {
            _computePipelineCache[shader] = pipeline;
        }

        return pipeline != 0;
    }

    private static void WriteDispatchLimit(
        Span<byte> uniforms,
        int offset,
        uint threadCount,
        uint groupCount,
        uint threadgroupSize)
    {
        var limit = threadCount != uint.MaxValue
            ? threadCount
            : groupCount * Math.Max(threadgroupSize, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            uniforms[offset..],
            limit);
    }

    private static void ReturnPooledComputeData(ComputeGuestDispatch dispatch)
    {
        foreach (var buffer in dispatch.GlobalMemoryBuffers)
        {
            if (buffer.Pooled)
            {
                GuestDataPool.Shared.Return(buffer.Data);
            }
        }
    }
}
