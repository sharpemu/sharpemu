// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// A host buffer and the byte offset a guest address maps to inside it.
public readonly record struct BufferBinding(ulong Handle, ulong Offset);

// The packet arguments of a draw the host keeps until the next flip.
public readonly record struct TargetlessDrawArguments(ulong SubmitId, bool Indexed, GpuCommands.DrawIndexedArguments Indexed_, GpuCommands.DrawAutoArguments Auto);

public readonly record struct RenderHostLimits(uint MaxFramebufferWidth, uint MaxFramebufferHeight, uint MaxViewportWidth, uint MaxViewportHeight);

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

    IImageFormatSupport FormatSupport { get; }

    // True while a command buffer is recording.
    bool IsRecording { get; }

    void RunPendingOperations();

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

    void SetDynamicState(in DynamicDrawState state);

    void BeginRendering(in RenderingState state);

    void EndRendering();

    void BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline);

    void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

    void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance);

    void Dispatch(uint groupsX, uint groupsY, uint groupsZ);

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

    bool IsMetadata(ulong address);

    bool ClearMetadata(ulong address);

    bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear);

    bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue);

    Exception Fatal(string message);
}
