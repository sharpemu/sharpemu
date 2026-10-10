// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private uint BroadcastFirstWaveActive(uint value) =>
            _waveLowering.BroadcastFirstActive(value);

        private uint BroadcastWaveLane(uint value, uint lane) =>
            _waveLowering.BroadcastLane(value, lane);

        private uint BroadcastWaveValue(uint writer, uint value, uint lane) =>
            _waveLowering.BroadcastValue(writer, value, lane);

        private uint CurrentExecMask() =>
            _waveLowering.CurrentExecMask();

        private uint NormalizeExecMask(uint mask) =>
            _waveLowering.NormalizeExecMask(mask);
    }
}


