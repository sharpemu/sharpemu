// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

/// <summary>
/// Vulkan backend for the guest-GPU seam: SPIR-V codegen via
/// SharpEmu.ShaderCompiler.Vulkan, rendering via a thin adapter over the existing
/// VulkanVideoPresenter statics (folding the presenter into an instance type is
/// follow-up work, not a seam concern).
/// </summary>
internal sealed class VulkanGuestGpuBackend : IGuestGpuBackend
{
    public string BackendName => "Vulkan";

    private static readonly IGuestCompiledShader DepthOnlyFragmentShader =
        new VulkanCompiledGuestShader(SpirvFixedShaders.CreateDepthOnlyFragment());

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
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
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

        shader = new VulkanCompiledGuestShader(compiled.Spirv);
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
        if (!Gen5SpirvTranslator.TryCompilePixelShader(
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

        shader = new VulkanCompiledGuestShader(compiled.Spirv);
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
        if (!Gen5SpirvTranslator.TryCompileComputeShader(
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

        shader = new VulkanCompiledGuestShader(compiled.Spirv);
        return true;
    }

    public IGuestCompiledShader GetDepthOnlyFragmentShader() =>
        DepthOnlyFragmentShader;

    public void EnsureStarted(uint width, uint height) =>
        VulkanVideoPresenter.EnsureStarted(width, height);

    public void HideSplashScreen() =>
        VulkanVideoPresenter.HideSplashScreen();

    public void Submit(byte[] bgraFrame, uint width, uint height) =>
        VulkanVideoPresenter.Submit(bgraFrame, width, height);

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) =>
        VulkanVideoPresenter.SubmitGuestDraw(drawKind, width, height);

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
        VulkanVideoPresenter.SubmitTranslatedDraw(
            Spirv(pixelShader),
            textures,
            globalMemoryBuffers,
            width,
            height,
            attributeCount,
            vertexShader is null ? null : Spirv(vertexShader),
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
        VulkanVideoPresenter.SubmitDepthOnlyTranslatedDraw(
            Spirv(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            depthTarget,
            vertexShader is null ? null : Spirv(vertexShader),
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
        VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
            Spirv(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            targets,
            vertexShader is null ? null : Spirv(vertexShader),
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
        VulkanVideoPresenter.SubmitStorageTranslatedDraw(
            Spirv(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            width,
            height,
            shaderAddress);

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
        uint threadCountZ = uint.MaxValue) =>
        VulkanVideoPresenter.SubmitComputeDispatch(
            shaderAddress,
            Spirv(computeShader),
            textures,
            globalMemoryBuffers,
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            localSizeX,
            localSizeY,
            localSizeZ,
            isIndirect,
            writesGlobalMemory,
            threadCountX,
            threadCountY,
            threadCountZ);

    public bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel) =>
        VulkanVideoPresenter.TrySubmitGuestImage(address, width, height, pitchInPixel);

    public bool TrySubmitOrderedGuestImageFlip(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel) =>
        VulkanVideoPresenter.TrySubmitOrderedGuestImageFlip(
            videoOutHandle,
            displayBufferIndex,
            address,
            width,
            height,
            pitchInPixel);

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) =>
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(address, guestFormat);

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) =>
        VulkanVideoPresenter.IsGpuGuestImageAvailable(address, format, numberType);

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
        VulkanVideoPresenter.TrySubmitGuestImageBlit(
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

    public bool TryGetRenderTargetOutputKind(uint dataFormat, uint numberType, out Gen5PixelOutputKind outputKind)
    {
        if (VulkanVideoPresenter.TryDecodeRenderTargetFormat(dataFormat, numberType, out var format))
        {
            outputKind = format.OutputKind;
            return true;
        }

        outputKind = default;
        return false;
    }

    public IDisposable EnterGuestQueue(string queueName, ulong submissionId) =>
        VulkanVideoPresenter.EnterGuestQueue(queueName, submissionId);

    public long SubmitOrderedGuestAction(Action action, string debugName) =>
        VulkanVideoPresenter.SubmitOrderedGuestAction(action, debugName);

    public long SubmitOrderedGuestFlipWait(int videoOutHandle, int displayBufferIndex) =>
        VulkanVideoPresenter.SubmitOrderedGuestFlipWait(videoOutHandle, displayBufferIndex);

    public bool WaitForGuestWork(long workSequence, int timeoutMilliseconds = Timeout.Infinite) =>
        VulkanVideoPresenter.WaitForGuestWork(workSequence, timeoutMilliseconds);

    public long CurrentGuestWorkSequenceForDiagnostics =>
        VulkanVideoPresenter.CurrentGuestWorkSequenceForDiagnostics;

    public bool IsGuestImageUploadKnown(ulong address, uint format, uint numberType) =>
        VulkanVideoPresenter.IsGuestImageUploadKnown(address, format, numberType);

    public bool GuestImageWantsInitialData(ulong address) =>
        VulkanVideoPresenter.GuestImageWantsInitialData(address);

    public void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels) =>
        VulkanVideoPresenter.ProvideGuestImageInitialData(address, rgbaPixels);

    public void SubmitGuestImageFill(ulong address, uint fillValue) =>
        VulkanVideoPresenter.SubmitGuestImageFill(address, fillValue);

    public void SubmitGuestImageWrite(ulong address, byte[] pixels) =>
        VulkanVideoPresenter.SubmitGuestImageWrite(address, pixels);

    public bool TryGetGuestImageExtent(ulong address, out uint width, out uint height, out ulong byteCount) =>
        VulkanVideoPresenter.TryGetGuestImageExtent(address, out width, out height, out byteCount);

    public IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents() =>
        VulkanVideoPresenter.GetGuestImageExtents();

    public bool IsTextureContentCached(in TextureContentIdentity identity) =>
        VulkanVideoPresenter.IsTextureContentCached(identity);

    public void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) =>
        VulkanVideoPresenter.AttachGuestMemory(memory);

    public ulong GuestStorageBufferOffsetAlignment =>
        VulkanVideoPresenter.GuestStorageBufferOffsetAlignment;

    public void CountShaderCompilation() =>
        VulkanVideoPresenter.CountSpirvCompilation();

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() =>
        VulkanVideoPresenter.ReadAndResetPerfCounters();

    public void RequestClose() =>
        VulkanVideoPresenter.RequestClose();

    private static byte[] Spirv(IGuestCompiledShader shader) =>
        shader is VulkanCompiledGuestShader vulkanShader
            ? vulkanShader.Spirv
            : throw new InvalidOperationException(
                $"shader handle of type {shader.GetType().Name} was not compiled by the Vulkan backend");
}
