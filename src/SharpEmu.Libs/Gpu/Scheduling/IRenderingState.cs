// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Scheduling;

public interface IRenderingState
{
    bool IsRendering { get; }

    void EndRendering();

    // Takes image barriers that must separate the active rendering scope from whatever uses the
    // images after it, and records them as the scope ends. False when no scope is active.
    bool TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers) => false;
}
