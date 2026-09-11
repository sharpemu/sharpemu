// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
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
                storageBufferOffsetAlignment,
                VulkanVideoPresenter.GraphicsSubgroupOperationsEnabled))
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
        IReadOnlyList<uint>? pixelInputCntl = null,
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
                pixelInputCntl,
                storageBufferOffsetAlignment,
                VulkanVideoPresenter.GraphicsSubgroupOperationsEnabled))
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

    public bool TrySubmitGuestImage(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel,
        ulong flipRequestId) =>
        VulkanVideoPresenter.TrySubmitGuestImage(videoOutHandle, displayBufferIndex, address, width, height, pitchInPixel, flipRequestId);

    public void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots) =>
        VulkanVideoPresenter.SubmitCommandStream(memory, queue, address, dwordCount, submissionId, geometrySnapshots);

    public IdleOutcome SubmitDone(ICpuMemory memory) =>
        VulkanVideoPresenter.SubmitDone(memory);

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) =>
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(address, guestFormat);

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) =>
        VulkanVideoPresenter.IsGpuGuestImageAvailable(address, format, numberType);

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
