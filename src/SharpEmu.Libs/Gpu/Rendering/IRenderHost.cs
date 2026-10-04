// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// A host buffer, the byte offset a guest address maps to inside it and the accessible
// byte range beginning at that offset. A zero size intentionally describes an empty range.
public readonly record struct BufferBinding(ulong Handle, ulong Offset, ulong Size);

// The packet arguments of a draw the host keeps until the next flip.
public readonly record struct TargetlessDrawArguments(ulong SubmitId, bool Indexed, GpuCommands.DrawIndexedArguments Indexed_, GpuCommands.DrawAutoArguments Auto);

public readonly record struct RenderHostLimits(
    uint MaxFramebufferWidth,
    uint MaxFramebufferHeight,
    uint MaxViewportWidth,
    uint MaxViewportHeight,
    uint MaxViewports = 1);

// Optional VK_EXT_mesh_shader-style host capabilities. Keeping this separate
// from IRenderHost lets non-mesh backends remain unchanged while the shader
// pipeline and executor gain mesh support incrementally.
public readonly record struct MeshShaderHostCapabilities(
    bool Supported,
    uint MaxWorkGroupTotalCount,
    uint MaxWorkGroupCountX,
    uint MaxWorkGroupCountY,
    uint MaxWorkGroupCountZ,
    uint MaxWorkGroupInvocations,
    uint MaxWorkGroupSizeX,
    uint MaxWorkGroupSizeY,
    uint MaxWorkGroupSizeZ,
    uint MaxSharedMemorySize,
    uint MaxOutputVertices,
    uint MaxOutputPrimitives);

public interface IMeshRenderHost
{
    MeshShaderHostCapabilities MeshShaderCapabilities { get; }

    void PushMeshDrawData(in PipelineHandle pipeline, ReadOnlySpan<uint> drawData);

    void DrawMeshTasks(uint groupCountX, uint groupCountY, uint groupCountZ);
}

public readonly record struct ColorAttachmentAcquisition(
    ResourceSlotIdentifier Image,
    ImageView View,
    ImageLayout Layout,
    uint Samples,
    bool MetadataClear,
    ClearColorValue MetadataClearValue);

public readonly record struct DepthAttachmentAcquisition(ImageView View, uint Samples, bool MetadataClear);

public readonly record struct ScissorRectangle(int Left, int Top, int Right, int Bottom)
{
    public bool IsSet => Left != 0 || Top != 0 || Right != 0 || Bottom != 0;

    public bool IsValid => Right > Left && Bottom > Top;

    public ScissorRectangle Offset(int x, int y) => new(Left + x, Top + y, Right + x, Bottom + y);

    public ScissorRectangle Intersect(in ScissorRectangle other) =>
        new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}

// One indexed viewport and its matching indexed scissor. The executor passes a
// one-element span for ordinary shaders and every guest slot when the final
// pre-raster stage can export ViewportIndex.
public readonly record struct DynamicViewportState(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth,
    float MaxDepth,
    ScissorRectangle Scissor);

// The state the host records before every draw.
public readonly record struct DynamicDrawState(
    float ViewportX,
    float ViewportY,
    float ViewportWidth,
    float ViewportHeight,
    float ViewportMinDepth,
    float ViewportMaxDepth,
    ScissorRectangle Scissor,
    float LineWidth,
    float BlendRed,
    float BlendGreen,
    float BlendBlue,
    float BlendAlpha,
    bool DepthTestEnabled,
    bool DepthWriteEnabled,
    CompareOp DepthCompare,
    bool DepthBiasEnabled,
    float DepthBiasConstantFactor,
    float DepthBiasClamp,
    float DepthBiasSlopeFactor,
    bool StencilTestEnabled,
    StencilMasks FrontStencil,
    StencilMasks BackStencil,
    uint ColorWriteCount,
    byte ColorWriteEnableMask);

// The host-side descriptors of one shader stage, prepared before the draw or dispatch records.
public interface IPreparedBindings
{
    ShaderStageResources Stage { get; }
}

// Keeps every prepared binding and the stream ring contents intact until the draw or dispatch is recorded.
public interface IResourcePreparation : IDisposable
{
}

// Everything the executor needs from the renderer, the caches and the pipeline objects.
public interface IRenderHost
{
    RenderHostLimits Limits { get; }

    // The guest can select the final vertex for flat interpolation. Hosts that
    // do not expose an equivalent rasterization mode keep the safe default.
    bool ProvokingVertexLastSupported => false;

    IImageFormatSupport FormatSupport { get; }

    // True while a command buffer is recording.
    bool IsRecording { get; }

    void RunPendingOperations();

    // Optional diagnostic boundary. Vulkan overrides this to submit and wait for all
    // commands recorded so far; other hosts may leave the default as a no-op.
    void SynchronizeForDiagnostic()
    {
    }

    void SetDebugInformation(RecordedOperation operation, ulong submitId, uint argument0, uint argument1, uint argument2, uint argument3, ulong argument4);

    bool TryReadGuest(ulong address, Span<byte> destination);

    // The part of the range that is mapped from its start; zero when the start is unmapped.
    ulong ClampMappedSize(ulong address, ulong size);

    ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat);

    void BindRenderTarget(ResourceSlotIdentifier image);

    void ResetBindings();

    // Finds the image again when it changed, makes the view, sets the layout and resolves a metadata clear.
    ColorAttachmentAcquisition AcquireColorAttachment(in ColorTargetState target);

    // Makes the view and consumes the metadata clear state; the layout follows in a second call.
    DepthAttachmentAcquisition AcquireDepthAttachment(in DepthAttachmentState depth);

    void TransitionDepthAttachment(in DepthAttachmentState depth, ImageLayout layout, ImageAspectFlags writeAspects);

    BufferBinding NullBuffer { get; }

    BufferBinding ObtainBuffer(ulong address, ulong size, bool isWritten);

    // Makes a guest range persistent and visible through the shader device-address table.
    // Unlike ObtainBuffer, this must not satisfy the request from a transient upload ring.
    void RegisterDeviceAddressRange(ulong address, ulong size);

    // Copies host bytes into the stream ring for the current recording.
    BufferBinding UploadTransient(ReadOnlySpan<byte> data, uint alignment);

    void BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings, VertexInputInfo input);

    void BindIndexBuffer(BufferBinding binding, IndexType type);

    // Opens the preparation scope; a ring wrap inside it is refused, and disposal releases it on every path.
    IResourcePreparation BeginPreparation();

    IPreparedBindings PrepareBindings(ShaderStageResources stage);

    void PrepareDeviceAddresses();

    void BindResources(IPreparedBindings prepared);

    void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages);

    void SetDynamicState(in DynamicDrawState state, ReadOnlySpan<DynamicViewportState> viewports);

    void BeginRendering(in RenderingState state);

    void EndRendering();

    // Whether a texture bound for the draw being prepared reads the depth attachment's subresources.
    bool SamplesDepthAttachment(in DepthAttachmentState depth) => true;

    // The next draw stores to buffers or storage images; called before its BeginRendering.
    void PrepareMemoryWritingDraw() { }

    void BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline);

    void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

    void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance);

    // One indexed draw whose counts the GPU reads from the buffer (VkDrawIndexedIndirectCommand layout).
    void DrawIndexedIndirect(BufferBinding arguments) =>
        throw new NotSupportedException("The render host does not draw from indirect arguments.");

    void Dispatch(uint groupsX, uint groupsY, uint groupsZ);

    // Executes a dispatch from three uint32 group counts in guest memory when the backend can
    // consume the guest buffer directly. Backends without native indirect dispatch return false.
    bool TryDispatchIndirect(ulong argumentsAddress);

    // Orders the buffer writes of the given shader stages before every later access.
    void ShaderWriteBarrier(PipelineStageFlags sourceStages);

    void ShaderWriteHazardBarrier();

    void ShaderAccessBarrier();

    // Clears the bound color targets to one colour in place of the draw.
    void ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear);

    // Keeps a draw without targets for the next flip; false leaves the draw to run as it is.
    bool TryRetainTargetlessDraw(GpuCommands.Registers.RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments);

    void MarkGpuWritten(ResourceSlotIdentifier image);

    void ResolveImage(ResourceSlotIdentifier source, uint sourceMip, uint sourceLayer, ResourceSlotIdentifier destination, uint destinationMip, uint destinationLayer);

    void CopyDepthStencilImage(ResourceSlotIdentifier source, ResourceSlotIdentifier destination, in SubresourceRange range, in Extent3D extent, ImageAspectFlags aspects);

    bool IsMetadata(ulong address);

    bool ClearMetadata(ulong address);

    bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear);

    bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue);

    bool TryFillDccMetadata(ulong address, ulong size, uint fillValue);

    bool TryCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words);

    Exception Fatal(string message);
}
