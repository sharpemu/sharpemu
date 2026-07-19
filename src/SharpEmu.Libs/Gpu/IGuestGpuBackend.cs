// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
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
    /// <summary>Human-readable name of this backend ("Metal", "Vulkan"), shown in
    /// the window title on macOS where either backend can run.</summary>
    string BackendName { get; }

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

    // Guest work ordering. AGC submissions execute on a single backend consumer in
    // logical guest-queue order; sequences returned here are backend work tickets.
    // A backend without a running presenter returns 0 from the Submit* methods and
    // callers fall back to executing inline.

    /// <summary>Scopes subsequent submissions on this thread to a named guest queue.</summary>
    IDisposable EnterGuestQueue(string queueName, ulong submissionId);

    /// <summary>Enqueues an action at its exact position in the current guest queue;
    /// returns its work sequence, or 0 when nothing could be enqueued.</summary>
    long SubmitOrderedGuestAction(Action action, string debugName);

    /// <summary>Preserves sceAgcDcbWaitUntilSafeForRendering in queue order.</summary>
    long SubmitOrderedGuestFlipWait(int videoOutHandle, int displayBufferIndex);

    /// <summary>Blocks until the given work sequence completes; false on timeout,
    /// close, or a non-positive sequence.</summary>
    bool WaitForGuestWork(long workSequence, int timeoutMilliseconds = Timeout.Infinite);

    /// <summary>Sequence currently executing on the guest-work consumer; diagnostics only.</summary>
    long CurrentGuestWorkSequenceForDiagnostics { get; }

    // Guest image lifecycle beyond presentation: CPU-visible seeding, writes, and
    // extent queries the AGC layer uses to keep guest memory and backend images
    // coherent. Addresses and formats are always raw guest values.

    /// <summary>Whether the image exists on the backend or an already-queued upload
    /// owns its initialization (a pending image may skip a duplicate upload but is
    /// not yet a valid flip source).</summary>
    bool IsGuestImageUploadKnown(ulong address, uint format, uint numberType);

    /// <summary>True when the first draw into this address must seed the backend
    /// image from guest memory (PS5 render targets alias guest memory, so
    /// CPU-prefilled pixels are visible before the first draw).</summary>
    bool GuestImageWantsInitialData(ulong address);

    void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels);

    void SubmitGuestImageFill(ulong address, uint fillValue);

    void SubmitGuestImageWrite(ulong address, byte[] pixels);

    bool TryGetGuestImageExtent(ulong address, out uint width, out uint height, out ulong byteCount);

    IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents();

    /// <summary>Whether the backend's texture cache already holds this content; lets
    /// the AGC layer skip copying texels out of guest memory on every draw.</summary>
    bool IsTextureContentCached(in TextureContentIdentity identity);

    /// <summary>Guest memory handle for backend self-healing (cache misses re-read
    /// texels directly instead of showing a fallback pattern).</summary>
    void AttachGuestMemory(ICpuMemory memory);

    /// <summary>Alignment the AGC layer must apply to storage-buffer offsets before
    /// they cross the seam.</summary>
    ulong GuestStorageBufferOffsetAlignment { get; }

    /// <summary>Counts a guest shader translation for the perf overlay.</summary>
    void CountShaderCompilation();

    (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters();

    /// <summary>Asks a running presenter to close its window.</summary>
    void RequestClose();
}
