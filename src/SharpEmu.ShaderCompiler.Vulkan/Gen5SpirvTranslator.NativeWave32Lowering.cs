// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // A guest Wave32 maps directly to one 32-lane host subgroup. This is the
        // former implicit/default lowering, made explicit so every guest wave size
        // has a concrete lowering strategy.
        private sealed class NativeWave32Lowering : IWaveLowering
        {
            private readonly CompilationContext _context;

            public NativeWave32Lowering(CompilationContext context)
            {
                _context = context;
            }

            public bool RequiresCrossSubgroupSynchronization => false;

            public uint ReserveLdsDwords(uint guestDwordCount) => guestDwordCount;

            public void DeclareResources() { }
            public void DeclareFunctionResources() { }
            public void BeginBlock() { }
            public void BeforeInstruction(Gen5ShaderInstruction instruction) { }
            public void EndBlock() { }

            public void InitializeMasks()
            {
                var context = _context;
                if (context._subgroupInvocationIdInput != 0)
                {
                    StoreWaveMask(106, context._module.ConstantBool(false));
                    StoreWaveMask(126, context._module.ConstantBool(true));
                    return;
                }

                context.StoreS64(106, context._module.Constant64(context._ulongType, 0));
                context.StoreS64(126, context._module.Constant64(context._ulongType, 1));
            }

            public uint BooleanToWaveMask(uint condition)
            {
                var context = _context;
                if (context._subgroupInvocationIdInput == 0)
                {
                    return context._module.AddInstruction(
                        SpirvOp.Select,
                        context._ulongType,
                        condition,
                        CurrentLaneBit(),
                        context._module.Constant64(context._ulongType, 0));
                }

                var ballot = context._module.AddInstruction(
                    SpirvOp.GroupNonUniformBallot,
                    context._uvec4Type,
                    context.UInt(3),
                    condition);
                var low = context._module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    context._uintType,
                    ballot,
                    0);
                return context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, low);
            }

            public uint SubgroupAny(uint condition) =>
                _context._subgroupInvocationIdInput == 0
                    ? condition
                    : _context._module.AddInstruction(
                        SpirvOp.GroupNonUniformAny,
                        _context._boolType,
                        _context.UInt(3),
                        condition);

            public uint CurrentLaneBit()
            {
                var context = _context;
                if (context._subgroupInvocationIdInput == 0)
                {
                    return context._module.Constant64(context._ulongType, 1);
                }

                var shifted = context.ShiftLeftLogical64(
                    context._module.Constant64(context._ulongType, 1),
                    context._module.AddInstruction(
                        SpirvOp.UConvert,
                        context._ulongType,
                        context.GuestWaveLane()));
                return context._module.AddInstruction(
                    SpirvOp.Select,
                    context._ulongType,
                    context.IsCurrentLaneInRdnaWave(),
                    shifted,
                    context._module.Constant64(context._ulongType, 0));
            }

            public uint BroadcastFirstActive(uint value)
            {
                var context = _context;
                if (context._subgroupInvocationIdInput == 0)
                {
                    return value;
                }

                var activeLanes = context._module.AddInstruction(
                    SpirvOp.GroupNonUniformBallot,
                    context._uvec4Type,
                    context.UInt(3),
                    context.Load(context._boolType, context._exec));
                var activeLow = context._module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    context._uintType,
                    activeLanes,
                    0);
                var firstActiveLane = context.Ext(73, context._uintType, activeLow);
                return context._module.AddInstruction(
                    SpirvOp.GroupNonUniformBroadcast,
                    context._uintType,
                    context.UInt(3),
                    value,
                    firstActiveLane);
            }

            public uint BroadcastLane(uint value, uint lane) =>
                _context._subgroupInvocationIdInput == 0
                    ? value
                    : _context._module.AddInstruction(
                        SpirvOp.GroupNonUniformBroadcast,
                        _context._uintType,
                        _context.UInt(3),
                        value,
                        lane);

            public uint BroadcastValue(uint writer, uint value, uint lane) =>
                _context.ShuffleLane(value, lane);

            public uint WaveMaskAny(uint register, uint laneActive) =>
                SubgroupAny(_context.Load(_context._boolType, laneActive));

            public uint CurrentExecMask() =>
                BooleanToWaveMask(_context.Load(_context._boolType, _context._exec));

            public uint NormalizeExecMask(uint mask) =>
                _context.BitwiseAnd64(mask, _context._module.Constant64(_context._ulongType, 0xFFFF_FFFFUL));

            public void StoreWaveMask(uint register, uint condition) =>
                _context.StoreS(register, _context.Narrow(BooleanToWaveMask(condition)));
        }
    }
}
