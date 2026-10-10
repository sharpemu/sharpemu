// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // One guest wave is one 64-lane host subgroup (ballot.x = lanes 0..31, ballot.y = lanes
        // 32..63). It has no workgroup exchange, LDS reservation, half-mask plan or
        // cross-subgroup barrier state.
        private sealed class NativeWave64Lowering : IWaveLowering
        {
            private readonly CompilationContext _context;

            public NativeWave64Lowering(CompilationContext context)
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
                }
                else
                {
                    context.StoreS64(106, context._module.Constant64(context._ulongType, 0));
                    context.StoreS64(126, context._module.Constant64(context._ulongType, 1));
                }
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

                return BallotToMask(condition);
            }

            // The host subgroup is at least 64 wide: lanes 0..31 are ballot.x and lanes 32..63 ballot.y.
            private uint BallotToMask(uint condition)
            {
                var context = _context;
                var ballot = context._module.AddInstruction(
                    SpirvOp.GroupNonUniformBallot,
                    context._uvec4Type,
                    context.UInt(3),
                    condition);
                var low = context._module.AddInstruction(SpirvOp.CompositeExtract, context._uintType, ballot, 0);
                var high = context._module.AddInstruction(SpirvOp.CompositeExtract, context._uintType, ballot, 1);
                return context.BitwiseOr64(
                    context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, low),
                    context.ShiftLeftLogical64(
                        context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, high),
                        context._module.Constant64(context._ulongType, 32)));
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
                return shifted;
            }

            public uint BroadcastFirstActive(uint value)
            {
                var context = _context;
                if (context._subgroupInvocationIdInput == 0)
                {
                    return value;
                }

                var activeLanes = BallotToMask(context.Load(context._boolType, context._exec));
                var activeLow = context._module.AddInstruction(SpirvOp.UConvert, context._uintType, activeLanes);
                var activeHigh = context._module.AddInstruction(
                    SpirvOp.UConvert,
                    context._uintType,
                    context.ShiftRightLogical64(activeLanes, context._module.Constant64(context._ulongType, 32)));
                var firstActiveLane = context._module.AddInstruction(
                    SpirvOp.Select,
                    context._uintType,
                    context._module.AddInstruction(SpirvOp.IEqual, context._boolType, activeLow, context.UInt(0)),
                    context.IAdd(context.Ext(73, context._uintType, activeHigh), context.UInt(32)),
                    context.Ext(73, context._uintType, activeLow));
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

            public uint NormalizeExecMask(uint mask) => mask;

            public void StoreWaveMask(uint register, uint condition) =>
                _context.StoreS64(register, BooleanToWaveMask(condition));
        }
    }
}
