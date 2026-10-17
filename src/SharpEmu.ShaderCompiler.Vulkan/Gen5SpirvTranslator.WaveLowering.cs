// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private interface IWaveLowering
        {
            bool RequiresCrossSubgroupSynchronization { get; }

            uint ReserveLdsDwords(uint guestDwordCount);
            void DeclareResources();
            void DeclareFunctionResources();
            void InitializeMasks();
            void BeginBlock();
            void BeforeInstruction(Gen5ShaderInstruction instruction);
            void EndBlock();
            uint BooleanToWaveMask(uint condition);
            uint SubgroupAny(uint condition);
            uint CurrentLaneBit();
            uint BroadcastFirstActive(uint value);
            uint BroadcastLane(uint value, uint lane);
            uint BroadcastValue(uint writer, uint value, uint lane);
            uint WaveMaskAny(uint register, uint laneActive);
            uint CurrentExecMask();
            uint NormalizeExecMask(uint mask);
            void StoreWaveMask(uint register, uint condition);
        }
    }
}

