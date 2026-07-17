// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// Metal backend for the guest-GPU seam: MSL codegen via
/// SharpEmu.ShaderCompiler.Metal, rendering via the Metal presenter — the full
/// surface (presentation, guest images, ordered flips, translated draws, and
/// compute) with no Vulkan, MoltenVK, or windowing-library dependency.
/// </summary>
internal sealed class MetalGuestGpuBackend : IGuestGpuBackend
{
    public string BackendName => "Metal";

    private static readonly IGuestCompiledShader DepthOnlyFragmentShader =
        new MetalCompiledGuestShader(new Gen5MslShader(
            MslFixedShaders.CreateDepthOnlyFragment(),
            "depth_only_fs",
            Gen5MslStage.Pixel,
            [],
            [],
            AttributeCount: 0,
            []));

    public bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out IGuestCompiledShader? shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1,
        int requiredVertexOutputCount = 0,
        ulong storageBufferOffsetAlignment = 1)
    {
        shader = null;
        if (!Gen5MslTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var compiled,
                out error,
                globalBufferBase,
                totalGlobalBufferCount,
                imageBindingBase,
                scalarRegisterBufferIndex,
                requiredVertexOutputCount,
                storageBufferOffsetAlignment))
        {
            return false;
        }

        shader = new MetalCompiledGuestShader(compiled);
        return true;
    }

    public bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        out IGuestCompiledShader? shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong storageBufferOffsetAlignment = 1)
    {
        shader = null;
        if (!Gen5MslTranslator.TryCompilePixelShader(
                state,
                evaluation,
                outputs,
                out var compiled,
                out error,
                globalBufferBase,
                totalGlobalBufferCount,
                imageBindingBase,
                scalarRegisterBufferIndex,
                pixelInputEnable,
                pixelInputAddress,
                storageBufferOffsetAlignment))
        {
            return false;
        }

        shader = new MetalCompiledGuestShader(compiled);
        return true;
    }

    public bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out IGuestCompiledShader? shader,
        out string error,
        int totalGlobalBufferCount = -1,
        int initialScalarBufferIndex = -1,
        uint waveLaneCount = 32,
        ulong storageBufferOffsetAlignment = 1)
    {
        shader = null;
        // Wave64 compute is emulated by the translator: cross-lane ops bridge
        // the two 32-wide Apple simdgroups of a guest wave through threadgroup
        // scratch, and wave-agnostic kernels run per-thread unchanged.
        if (!Gen5MslTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var compiled,
                out error,
                totalGlobalBufferCount,
                initialScalarBufferIndex,
                waveLaneCount,
                storageBufferOffsetAlignment))
        {
            return false;
        }

        shader = new MetalCompiledGuestShader(compiled);
        return true;
    }

    public IGuestCompiledShader GetDepthOnlyFragmentShader() =>
        DepthOnlyFragmentShader;

    public bool TryGetRenderTargetOutputKind(uint dataFormat, uint numberType, out Gen5PixelOutputKind outputKind)
    {
        if (MetalGuestFormats.TryDecodeRenderTargetFormat(dataFormat, numberType, out var format))
        {
            outputKind = format.OutputKind;
            return true;
        }

        outputKind = default;
        return false;
    }

    public void EnsureStarted(uint width, uint height) =>
        MetalVideoPresenter.EnsureStarted(width, height);

    public void HideSplashScreen() =>
        MetalVideoPresenter.HideSplashScreen();

    public void Submit(byte[] bgraFrame, uint width, uint height) =>
        MetalVideoPresenter.Submit(bgraFrame, width, height);

    public bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel) =>
        MetalVideoPresenter.TrySubmitGuestImage(address, width, height, pitchInPixel);

    public bool TrySubmitOrderedGuestImageFlip(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel) =>
        MetalVideoPresenter.TrySubmitOrderedGuestImageFlip(
            videoOutHandle,
            displayBufferIndex,
            address,
            width,
            height,
            pitchInPixel);

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) =>
        MetalVideoPresenter.RegisterKnownDisplayBuffer(address, guestFormat);

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) =>
        MetalVideoPresenter.IsGuestImageAvailable(address, format, numberType);

    public bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        uint sourceNumberType,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat,
        uint destinationNumberType) =>
        MetalVideoPresenter.TrySubmitGuestImageBlit(
            sourceAddress,
            sourceWidth,
            sourceHeight,
            sourceFormat,
            sourceNumberType,
            destinationAddress,
            destinationWidth,
            destinationHeight,
            destinationFormat,
            destinationNumberType);

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) =>
        MetalVideoPresenter.SubmitGuestDraw(drawKind, width, height);

    public void SubmitTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null) =>
        MetalVideoPresenter.SubmitTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            width,
            height,
            attributeCount,
            vertexShader is null ? null : Msl(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState);

    public void SubmitDepthOnlyTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        GuestDepthTarget depthTarget,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        ulong shaderAddress = 0) =>
        MetalVideoPresenter.SubmitDepthOnlyTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            depthTarget,
            vertexShader is null ? null : Msl(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            shaderAddress);

    public void SubmitOffscreenTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<GuestRenderTarget> targets,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        GuestDepthTarget? depthTarget = null,
        ulong shaderAddress = 0) =>
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            targets,
            vertexShader is null ? null : Msl(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            depthTarget,
            shaderAddress);

    public void SubmitStorageTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height,
        ulong shaderAddress = 0) =>
        MetalVideoPresenter.SubmitStorageTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            width,
            height,
            shaderAddress);

    private static MetalCompiledGuestShader Msl(IGuestCompiledShader shader) =>
        shader as MetalCompiledGuestShader ??
        throw new InvalidOperationException(
            $"shader handle of type {shader.GetType().Name} was not compiled by the Metal backend");

    public long SubmitComputeDispatch(
        ulong shaderAddress,
        IGuestCompiledShader computeShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint baseGroupX,
        uint baseGroupY,
        uint baseGroupZ,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        bool isIndirect,
        bool writesGlobalMemory,
        uint threadCountX = uint.MaxValue,
        uint threadCountY = uint.MaxValue,
        uint threadCountZ = uint.MaxValue)
    {
        // The translated kernel bakes its threadgroup size; localSize and
        // isIndirect are already folded in by the AGC layer before submission.
        _ = localSizeX;
        _ = localSizeY;
        _ = localSizeZ;
        _ = isIndirect;
        return MetalVideoPresenter.SubmitComputeDispatch(
            shaderAddress,
            Msl(computeShader),
            textures,
            globalMemoryBuffers,
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            writesGlobalMemory,
            threadCountX,
            threadCountY,
            threadCountZ);
    }

    private long _perfShaderCompilations;

    public IDisposable EnterGuestQueue(string queueName, ulong submissionId) =>
        MetalVideoPresenter.EnterGuestQueue(queueName, submissionId);

    public long SubmitOrderedGuestAction(Action action, string debugName) =>
        MetalVideoPresenter.SubmitOrderedGuestAction(action, debugName);

    public long SubmitOrderedGuestFlipWait(int videoOutHandle, int displayBufferIndex) =>
        MetalVideoPresenter.SubmitOrderedGuestFlipWait(videoOutHandle, displayBufferIndex);

    public bool WaitForGuestWork(long workSequence, int timeoutMilliseconds = Timeout.Infinite) =>
        MetalVideoPresenter.WaitForGuestWork(workSequence, timeoutMilliseconds);

    public long CurrentGuestWorkSequenceForDiagnostics =>
        MetalVideoPresenter.CurrentGuestWorkSequenceForDiagnostics;

    public bool IsGuestImageUploadKnown(ulong address, uint format, uint numberType) =>
        MetalVideoPresenter.IsGuestImageUploadKnown(address, format, numberType);

    public bool GuestImageWantsInitialData(ulong address) =>
        MetalVideoPresenter.GuestImageWantsInitialData(address);

    public void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels) =>
        MetalVideoPresenter.ProvideGuestImageInitialData(address, rgbaPixels);

    public void SubmitGuestImageFill(ulong address, uint fillValue) =>
        MetalVideoPresenter.SubmitGuestImageFill(address, fillValue);

    public void SubmitGuestImageWrite(ulong address, byte[] pixels) =>
        MetalVideoPresenter.SubmitGuestImageWrite(address, pixels);

    public bool TryGetGuestImageExtent(ulong address, out uint width, out uint height, out ulong byteCount) =>
        MetalVideoPresenter.TryGetGuestImageExtent(address, out width, out height, out byteCount);

    public IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents() =>
        MetalVideoPresenter.GetGuestImageExtents();

    public bool IsTextureContentCached(in TextureContentIdentity identity) =>
        MetalVideoPresenter.IsTextureContentCached(identity);

    public void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) =>
        MetalVideoPresenter.AttachGuestMemory(memory);

    // Over-alignment is always valid, and 256 covers every Metal buffer-offset
    // requirement (Intel Macs need 256 for constant buffers; Apple GPUs less).
    public ulong GuestStorageBufferOffsetAlignment => 256;

    public void CountShaderCompilation() =>
        Interlocked.Increment(ref _perfShaderCompilations);

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters()
    {
        var (draws, drawMs, pipelines) = MetalVideoPresenter.ReadAndResetDrawPerfCounters();
        return (draws, drawMs, pipelines, Interlocked.Exchange(ref _perfShaderCompilations, 0));
    }

    public void RequestClose() =>
        MetalVideoPresenter.RequestClose();

}
