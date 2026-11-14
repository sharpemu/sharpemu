// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

public interface IIndirectMeshDraw : IDisposable
{
    ulong ParametersAddress { get; }
    void Prepare();
    void Draw();
}

public readonly record struct IndirectMeshDrawRequest(
    ulong ArgumentsAddress, ulong IndexAddress, uint IndexCapacity, uint IndexElementSize,
    bool PointList, uint PrimitivesPerGroup, MeshExecutionLimits Limits)
{
    public const uint MaximumIndexCapacity = 1 << 20;

    // An automatic draw reads no index buffer; its arguments are (vertexCount, instanceCount, firstVertex, firstInstance).
    public bool Automatic => IndexElementSize == 0;

    // The draw's mesh program is task-launched (MeshTaskLaunch): the task stage converts the arguments.
    public bool TaskLaunched { get; init; }

    public static IndirectMeshDrawRequest ForAutomaticDraw(ulong argumentsAddress, bool pointList, uint primitivesPerGroup,
        MeshExecutionLimits limits) => new(argumentsAddress, 0, 0, 0, pointList, primitivesPerGroup, limits);
}
