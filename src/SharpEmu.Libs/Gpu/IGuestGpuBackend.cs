// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// The guest-GPU backend seam: everything the AGC/VideoOut export layers need from a
/// host renderer, expressed in guest-domain terms so Vulkan, Metal, and DX12 backends
/// can each translate to their native API. Two rules keep it that way: no host-API
/// value (formats, blend enums, barrier or pass concepts) may cross this interface,
/// and submission is coarse-grained — synchronization is a backend-internal concern.
///
/// Shader compilation also lives behind the seam: the backend owns its codegen and
/// returns opaque <see cref="IGuestCompiledShader"/> handles that only it can submit.
/// </summary>
internal interface IGuestGpuBackend
{
    /// <summary>Starts the presenter (window + device) once; safe to call repeatedly.</summary>
    void EnsureStarted(uint width, uint height);

    // Shader compilation. The optional base/index parameters describe how a multi-stage
    // draw lays both stages' resources into one flat per-role slot space (buffers,
    // images, scalar-spill slots); each backend maps those slots to its own API binding
    // model. -1 keeps the emitter's single-stage defaults.

    bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out IGuestCompiledShader? shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1,
        int requiredVertexOutputCount = 0,
        ulong storageBufferOffsetAlignment = 1);

    bool TryCompilePixelShader(
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
        ulong storageBufferOffsetAlignment = 1);

    bool TryCompileComputeShader(
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
        ulong storageBufferOffsetAlignment = 1);

    /// <summary>Returns the backend's no-color-output fragment shader.</summary>
    IGuestCompiledShader GetDepthOnlyFragmentShader();

    void HideSplashScreen();

    /// <summary>Presents one CPU-produced BGRA frame.</summary>
    void Submit(byte[] bgraFrame, uint width, uint height);

    /// <summary>Presents a recognized fixed-function guest draw (see GuestDrawKind).</summary>
    void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height);

    void SubmitTranslatedDraw(
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
        GuestRenderState? renderState = null);

    void SubmitDepthOnlyTranslatedDraw(
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
        ulong shaderAddress = 0);

    void SubmitOffscreenTranslatedDraw(
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
        ulong shaderAddress = 0);

    void SubmitStorageTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height,
        ulong shaderAddress = 0);

    long SubmitComputeDispatch(
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
        uint threadCountZ = uint.MaxValue);

    bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel);

    bool TrySubmitOrderedGuestImageFlip(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel);

    /// <summary>Registers a display buffer with its guest texture format tag.</summary>
    void RegisterKnownDisplayBuffer(ulong address, uint guestFormat);

    /// <summary>Format/numberType are raw guest texture descriptor codes.</summary>
    bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType);

    bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        uint sourceNumberType,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat,
        uint destinationNumberType);

    /// <summary>
    /// Whether the backend supports the guest render-target format, and how its pixel
    /// outputs are typed. Deliberately does not expose the backend's native format —
    /// the guest codes cross the seam and each backend maps them internally.
    /// </summary>
    bool TryGetRenderTargetOutputKind(uint dataFormat, uint numberType, out Gen5PixelOutputKind outputKind);
}
