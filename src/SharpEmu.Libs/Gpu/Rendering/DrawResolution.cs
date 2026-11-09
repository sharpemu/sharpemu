// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed class DrawResolution
{
    public ulong TargetContextStamp;
    public ulong TargetProgramsStamp;
    public uint TargetVersion;
    public int ColorCount;
    public readonly ColorTargetResolution[] Colors = new ColorTargetResolution[RenderingState.ColorAttachmentCapacity];
    public readonly uint[] ColorSlots = new uint[RenderingState.ColorAttachmentCapacity];
    public DepthTargetState? Depth;
    public bool PixelActive;

    public ulong ValidatedContextStamp;
    public ulong ValidatedUserConfigStamp;

    public DynamicStateKey DynamicKey;
    public DynamicDrawState Dynamic;
}

internal readonly record struct DynamicStateKey(
    ulong ContextStamp,
    ulong DynamicStamp,
    uint TargetVersion,
    uint ColorCount,
    uint BoundSlots,
    bool FirstColorFound,
    bool DepthFound);

internal sealed class KeptPipeline
{
    public bool Valid;
    public PipelineKey Key;
    public RenderingState Rendering;
    public PipelineHandle Handle;
}

internal readonly record struct PipelineKey(
    ulong ContextStamp,
    ulong DynamicStamp,
    uint TargetVersion,
    uint ColorCount,
    uint BoundSlots,
    bool DepthBound,
    VertexAttributeResource[] VertexAttributes,
    VertexInputBuffer[] VertexBuffers,
    uint[]? PixelInterpolators,
    ulong VertexProgram,
    ulong PixelProgram,
    int Topology,
    bool PrimitiveRestart,
    bool DisableBlending);
