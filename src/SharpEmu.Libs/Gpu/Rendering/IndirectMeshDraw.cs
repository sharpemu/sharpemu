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
}
