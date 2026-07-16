// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// Metal backend for the guest-GPU seam: MSL codegen via
/// SharpEmu.ShaderCompiler.Metal, rendering via the Metal presenter. Shader
/// compilation and format decoding are complete; the presenter arrives in
/// follow-up phases, so submission methods currently fail loudly rather than
/// silently dropping frames.
/// </summary>
internal sealed class MetalGuestGpuBackend : IGuestGpuBackend
{
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
        if (waveLaneCount != 32)
        {
            // One MSL invocation models one lane of a 32-wide wave (the Apple
            // simdgroup width); wave64 needs a two-pass emulation that has not
            // been built yet.
            error = $"the Metal backend does not support wave{waveLaneCount} compute shaders yet";
            return false;
        }

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

    // Draw and compute submission — implemented by later phases. Failing loudly
    // beats silently swallowing guest work while the backend is opt-in via
    // SHARPEMU_GPU_BACKEND=metal.

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) => throw PresenterNotReady();

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
        GuestRenderState? renderState = null) => throw PresenterNotReady();

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
        ulong shaderAddress = 0) => throw PresenterNotReady();

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
        ulong shaderAddress = 0) => throw PresenterNotReady();

    public void SubmitStorageTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height,
        ulong shaderAddress = 0) => throw PresenterNotReady();

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
        uint threadCountZ = uint.MaxValue) => throw PresenterNotReady();

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

    // No backend texture cache until the translated-draw phase.
    public bool IsTextureContentCached(in TextureContentIdentity identity) => false;

    public void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) =>
        MetalVideoPresenter.AttachGuestMemory(memory);

    // Over-alignment is always valid, and 256 covers every Metal buffer-offset
    // requirement (Intel Macs need 256 for constant buffers; Apple GPUs less).
    public ulong GuestStorageBufferOffsetAlignment => 256;

    public void CountShaderCompilation() =>
        Interlocked.Increment(ref _perfShaderCompilations);

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() =>
        (0, 0, 0, Interlocked.Exchange(ref _perfShaderCompilations, 0));

    public void RequestClose() =>
        MetalVideoPresenter.RequestClose();

    private static NotSupportedException PresenterNotReady() => new(
        "the Metal backend does not submit guest draws or compute yet; " +
        "unset SHARPEMU_GPU_BACKEND to use the Vulkan backend");
}
