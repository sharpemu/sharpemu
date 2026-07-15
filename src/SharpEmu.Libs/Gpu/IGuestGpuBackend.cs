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
/// Interim exception, resolved when shader compilation moves behind the backend: the
/// shader parameters are SPIR-V blobs, which presumes the Vulkan codegen.
/// </summary>
internal interface IGuestGpuBackend
{
    /// <summary>Starts the presenter (window + device) once; safe to call repeatedly.</summary>
    void EnsureStarted(uint width, uint height);

    void HideSplashScreen();

    /// <summary>Presents one CPU-produced BGRA frame.</summary>
    void Submit(byte[] bgraFrame, uint width, uint height);

    /// <summary>Presents a recognized fixed-function guest draw (see GuestDrawKind).</summary>
    void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height);

    void SubmitTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null);

    void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<GuestRenderTarget> targets,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null);

    void SubmitStorageTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height);

    void SubmitComputeDispatch(
        ulong shaderAddress,
        byte[] computeSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ);

    bool TrySubmitGuestImage(
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
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat);

    /// <summary>
    /// Whether the backend supports the guest render-target format, and how its pixel
    /// outputs are typed. Deliberately does not expose the backend's native format —
    /// the guest codes cross the seam and each backend maps them internally.
    /// </summary>
    bool TryGetRenderTargetOutputKind(uint dataFormat, uint numberType, out Gen5PixelOutputKind outputKind);
}
