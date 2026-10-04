// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The compute stage's static inputs from the compute registers and the dispatch mode.
public static class ComputeStageInputResolver
{
    private const uint LocalDataShareGranuleDwords = 128;
    private const uint UseThreadDimensionsBit = 1u << 5;
    private const uint Wave32Bit = 1u << 15;

    public static ComputeInputInfo Resolve(
        ComputeStageRegisters compute,
        RegisteredShader shader,
        uint dispatchInitiator,
        uint hostSubgroupSize,
        uint dispatchX,
        uint dispatchY,
        uint dispatchZ)
    {
        var threadDimensions = (dispatchInitiator & UseThreadDimensionsBit) != 0;
        var waveSize = (dispatchInitiator & Wave32Bit) != 0 ? 32u : 64u;
        return new ComputeInputInfo
        {
            ThreadsX = compute.ThreadsX & 0xFFFFu,
            ThreadsY = compute.ThreadsY & 0xFFFFu,
            ThreadsZ = compute.ThreadsZ & 0xFFFFu,
            DispatchThreadDimensions = threadDimensions,
            DispatchThreadsX = threadDimensions ? dispatchX : 0,
            DispatchThreadsY = threadDimensions ? dispatchY : 0,
            DispatchThreadsZ = threadDimensions ? dispatchZ : 0,
            LocalDataShareDwords = (uint)compute.LocalDataShareSize * LocalDataShareGranuleDwords,
            ScratchDwords = shader.ScratchDwords,
            GroupIdX = compute.ThreadGroupIdXEnable,
            GroupIdY = compute.ThreadGroupIdYEnable,
            GroupIdZ = compute.ThreadGroupIdZEnable,
            WaveSize = waveSize,
            ThreadIdCount = compute.ThreadIdComponentCount + 1,
            ThreadGroupSizeEnabled = compute.ThreadGroupSizeEnable,
            NeedsLocalDataShareBarriers = waveSize == 64 && hostSubgroupSize < 64,
            HostSubgroupSize = hostSubgroupSize,
            WorkgroupRegister = compute.UserScalarCount,
        };
    }
}
