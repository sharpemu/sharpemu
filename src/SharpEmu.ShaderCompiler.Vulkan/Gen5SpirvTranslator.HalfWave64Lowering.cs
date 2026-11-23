// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // A lane-local wave64 program runs as two independent halves, one guest lane per invocation: its module is about
        // half the size of paired lanes and compiles in about half the time, while each half repeats the scalar work.
        // SHARPEMU_WAVE64_LANE_LOCAL=0 keeps paired lanes.
        private static readonly bool LaneLocalWave64Enabled = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_LANE_LOCAL"), "0", StringComparison.Ordinal);

        private static bool CanRunHalfWaves(ShaderCompileRequest request, int[] physicalAxisOfLogical)
        {
            if (!LaneLocalWave64Enabled ||
                physicalAxisOfLogical is not [0, 1, 2])
            {
                return false;
            }

            var sharedFlatPcs = request.Memory.Entries
                .Where(static memory => memory.AddressSpace is FlatAddressSpace.Shared or FlatAddressSpace.SharedOrPrivate)
                .Select(static memory => memory.Pc)
                .ToHashSet();
            return Ir.Gen5WaveLaneLocalAnalysis.IsLaneLocal(request.Program, sharedFlatPcs, out _);
        }

        private bool UsesHalfWaveSubgroups => _waveLowering is HalfWave64Lowering && UsesSubgroupOperations();

        private uint HalfWaveThreadCount => checked(_localSizeX * _localSizeY * _localSizeZ);

        private uint HalfWaveLocalIndex() =>
            IAdd(ShiftLeftLogical(Load(_uintType, _subgroupIdInput), UInt(5)), Load(_uintType, _subgroupInvocationIdInput));

        private uint HalfWaveLocalCoordinates(uint index)
        {
            if (_localSizeY == 1 && _localSizeZ == 1)
            {
                return _module.AddInstruction(SpirvOp.CompositeConstruct, _uvec3Type, index, UInt(0), UInt(0));
            }

            var (x, row) = SplitHalfWaveIndex(index, _localSizeX);
            var (y, z) = _localSizeZ == 1 ? (row, UInt(0)) : SplitHalfWaveIndex(row, _localSizeY);
            return _module.AddInstruction(SpirvOp.CompositeConstruct, _uvec3Type, x, y, z);
        }

        private (uint Component, uint Remaining) SplitHalfWaveIndex(uint index, uint size)
        {
            if (size == 1)
            {
                return (UInt(0), index);
            }

            return BitOperations.IsPow2(size)
                ? (BitwiseAnd(index, UInt(size - 1)), ShiftRightLogical(index, UInt((uint)BitOperations.Log2(size))))
                : (_module.AddInstruction(SpirvOp.UMod, _uintType, index, UInt(size)),
                    _module.AddInstruction(SpirvOp.UDiv, _uintType, index, UInt(size)));
        }

        private sealed class HalfWave64Lowering : IWaveLowering
        {
            private readonly CompilationContext _context;

            public HalfWave64Lowering(CompilationContext context)
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
                var low = context._module.AddInstruction(SpirvOp.CompositeExtract, context._uintType, ballot, 0);
                var widened = context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, low);
                return context._module.AddInstruction(
                    SpirvOp.Select,
                    context._ulongType,
                    context._module.AddInstruction(
                        SpirvOp.INotEqual,
                        context._boolType,
                        context.BitwiseAnd(context.Load(context._uintType, context._subgroupIdInput), context.UInt(1)),
                        context.UInt(0)),
                    context.ShiftLeftLogical64(widened, context._module.Constant64(context._ulongType, 32)),
                    widened);
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

                return context.ShiftLeftLogical64(
                    context._module.Constant64(context._ulongType, 1),
                    context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, context.GuestWaveLane()));
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
                var activeLow = context._module.AddInstruction(SpirvOp.CompositeExtract, context._uintType, activeLanes, 0);
                return context._module.AddInstruction(
                    SpirvOp.GroupNonUniformBroadcast,
                    context._uintType,
                    context.UInt(3),
                    value,
                    context.Ext(73, context._uintType, activeLow));
            }

            public uint BroadcastLane(uint value, uint lane) =>
                throw new InvalidOperationException("a half-wave program reads no other lane");

            public uint BroadcastValue(uint writer, uint value, uint lane) =>
                throw new InvalidOperationException("a half-wave program writes no other lane");

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
