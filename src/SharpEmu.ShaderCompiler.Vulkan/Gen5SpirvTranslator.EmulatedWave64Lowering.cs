// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private sealed class EmulatedWave64Lowering : IWaveLowering
        {
            private const uint ExchangeSlotCount = 4;
            private const uint ExchangeDwordCount = ExchangeSlotCount * 2;
            private const uint GuestWaveLaneCount = 64;
            private static readonly bool HalfWaveMasks = !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_HALF_MASKS"),
                "0",
                StringComparison.Ordinal);

            private readonly CompilationContext _context;
            private uint _exchange;
            private uint _exchangeElementPointer;
            private uint _exchangeOffset;
            private uint _exchangeParity;
            // Several guest waves share one workgroup. Each gets its own exchange area plus an
            // arrival counter, and its two halves meet at that counter instead of at a workgroup
            // barrier, which other waves (possibly in other blocks) would never reach.
            private readonly uint _waveCount;
            private readonly bool _multiWave;
            private readonly uint _waveStride;
            private uint _rendezvousEpoch;
            private Ir.Gen5Wave64HalfMaskPlan? _halfMaskPlan;
            private bool _halfMaskPlanBuilt;
            private bool _synchronizeSharedMemory;
            private SharedMemoryPhase _sharedMemoryPhase;
            private uint? _emittingPc;

            private enum SharedMemoryPhase { None, Read, Write }

            public EmulatedWave64Lowering(CompilationContext context)
            {
                _context = context;
                var threads = (ulong)context._localSizeX * context._localSizeY * context._localSizeZ;
                _waveCount = (uint)Math.Max((threads + GuestWaveLaneCount - 1) / GuestWaveLaneCount, 1);
                _multiWave = _waveCount > 1;
                _waveStride = _multiWave ? ExchangeDwordCount + 1 : ExchangeDwordCount;
            }

            private uint ExchangeTotalDwords => _waveCount * _waveStride;

            public bool RequiresCrossSubgroupSynchronization => true;

            private bool UsesExchange => _context.UsesSubgroupOperations();

            public uint ReserveLdsDwords(uint guestDwordCount) =>
                UsesExchange && guestDwordCount < LdsDwordCount
                    ? guestDwordCount + ExchangeTotalDwords
                    : guestDwordCount;

            public void DeclareResources()
            {
                var context = _context;
                if (!UsesExchange)
                {
                    return;
                }

                if (context._lds != 0)
                {
                    // Metal exposes 32 KiB of threadgroup memory on the Apple
                    // GPUs we target. Reuse the tail of guest LDS so the
                    // cross-subgroup bridge stays within that limit.
                    _exchange = context._lds;
                    _exchangeElementPointer = context._ldsElementPointer;
                    _exchangeOffset = context._ldsDwordMask + 1 < LdsDwordCount
                        ? context._ldsDwordMask + 1
                        : LdsDwordCount - ExchangeTotalDwords;
                    return;
                }

                var exchangeArrayType = context._module.TypeArray(context._uintType, ExchangeTotalDwords);
                _exchangeElementPointer = context._module.TypePointer(SpirvStorageClass.Workgroup, context._uintType);
                _exchange = context._module.AddGlobalVariable(
                    context._module.TypePointer(SpirvStorageClass.Workgroup, exchangeArrayType),
                    SpirvStorageClass.Workgroup);
                _exchangeOffset = 0;
                context._module.AddName(_exchange, "wave64Exchange");
                context._interfaces.Add(_exchange);
            }

            public void DeclareFunctionResources()
            {
                if (!UsesExchange)
                {
                    return;
                }

                _exchangeParity = _context.DeclareFunctionUint(
                    _context._module.Constant(_context._uintType, 0));
                _context._module.AddName(_exchangeParity, "wave64ExchangeParity");
                if (_multiWave)
                {
                    _rendezvousEpoch = _context.DeclareFunctionUint(
                        _context._module.Constant(_context._uintType, 0));
                    _context._module.AddName(_rendezvousEpoch, "wave64RendezvousEpoch");
                }
            }

            public void InitializeMasks()
            {
                var context = _context;
                if (context._subgroupInvocationIdInput != 0)
                {
                    context.StoreS64(106, BothHalvesOfOwnBallot(context._module.ConstantBool(false)));
                    context.StoreS64(126, BothHalvesOfOwnBallot(context._module.ConstantBool(true)));
                    InitializeRendezvous();
                    return;
                }

                context.StoreS64(106, context._module.Constant64(context._ulongType, 0));
                context.StoreS64(126, context._module.Constant64(context._ulongType, 1));
            }

            public void BeginBlock()
            {
                _synchronizeSharedMemory = HalfMaskPlan() is null;
                _sharedMemoryPhase = SharedMemoryPhase.None;
            }

            public void BeforeInstruction(Gen5ShaderInstruction instruction)
            {
                var context = _context;
                _emittingPc = instruction.Pc;
                if (HalfMaskPlan() is { } plan)
                {
                    if (plan.ExactPairsBefore.TryGetValue(instruction.Pc, out var exactPairs))
                    {
                        EmitExactWaveMasks(exactPairs);
                    }

                    if (plan.SharedMemoryBarriersBefore.Contains(instruction.Pc))
                    {
                        Barrier();
                    }
                }

                if (!_synchronizeSharedMemory)
                {
                    return;
                }

                var nextPhase = instruction.Control is Gen5DataShareControl { Gds: false }
                    ? instruction.Opcode.StartsWith("DsRead", StringComparison.Ordinal) ? SharedMemoryPhase.Read
                    : instruction.Opcode.StartsWith("DsWrite", StringComparison.Ordinal) ? SharedMemoryPhase.Write
                    : SharedMemoryPhase.None
                    : SharedMemoryPhase.None;
                if (instruction.Opcode == "SBarrier")
                {
                    _sharedMemoryPhase = SharedMemoryPhase.None;
                }
                if (nextPhase != SharedMemoryPhase.None)
                {
                    if (_sharedMemoryPhase != SharedMemoryPhase.None && _sharedMemoryPhase != nextPhase)
                    {
                        Barrier();
                    }
                    _sharedMemoryPhase = nextPhase;
                }
            }

            public void EndBlock()
            {
                if (_synchronizeSharedMemory && _sharedMemoryPhase != SharedMemoryPhase.None)
                {
                    Barrier();
                }
            }

            public uint SubgroupAny(uint condition) =>
                _context._subgroupInvocationIdInput == 0
                    ? condition
                    : _context.IsNotZero64(BooleanToWaveMask(condition));

            public uint CurrentLaneBit()
            {
                var context = _context;
                if (context._subgroupInvocationIdInput == 0)
                {
                    return context._module.Constant64(context._ulongType, 1);
                }

                return context.ShiftLeftLogical64(
                    context._module.Constant64(context._ulongType, 1),
                    context._module.AddInstruction(
                        SpirvOp.UConvert,
                        context._ulongType,
                        context.GuestWaveLane()));
            }

            public uint BooleanToWaveMask(uint condition)
            {
                var context = _context;
                if (context._subgroupInvocationIdInput == 0)
                {
                    return context.BooleanToLaneMask(condition);
                }

                var lane = context.GuestWaveLane();
                var owned = OwnHalfBallot(condition);
                var exchange = BeginExchange();
                context.EmitConditional(IsHalfWaveLeader(lane), () =>
                    context.Store(ExchangePointer(exchange, context.ShiftRightLogical(lane, context.UInt(5))), owned));
                Barrier();
                return context.Pair64(
                    context.Load(context._uintType, ExchangePointer(exchange, context.UInt(0))),
                    context.Load(context._uintType, ExchangePointer(exchange, context.UInt(1))));
            }

            public uint BroadcastFirstActive(uint value)
            {
                var context = _context;
                if (context._subgroupInvocationIdInput == 0)
                {
                    return value;
                }

                var lane = context.GuestWaveLane();
                var upperHalf = context.ShiftRightLogical(lane, context.UInt(5));
                var activeInHalf = OwnHalfBallot(context.Load(context._boolType, context._exec));
                var halfHasActive = context.IsNotZero(activeInHalf);
                var firstInHalf = context._module.AddInstruction(
                    SpirvOp.Select,
                    context._uintType,
                    halfHasActive,
                    context.Ext(73, context._uintType, activeInHalf),
                    context.UInt(0));
                var halfBase = context.BitwiseAnd(
                    context.Load(context._uintType, context._subgroupInvocationIdInput),
                    context.UInt(~31u));
                var halfValue = context._module.AddInstruction(
                    SpirvOp.GroupNonUniformBroadcast,
                    context._uintType,
                    context.UInt(3),
                    value,
                    context.IAdd(halfBase, firstInHalf));
                var exchange = BeginExchange();
                context.EmitConditional(IsHalfWaveLeader(lane), () =>
                {
                    context.Store(ExchangePointer(exchange, upperHalf), halfValue);
                    context.Store(
                        ExchangePointer(exchange, context.IAdd(context.UInt(2), upperHalf)),
                        context._module.AddInstruction(
                            SpirvOp.Select,
                            context._uintType,
                            halfHasActive,
                            context.UInt(1),
                            context.UInt(0)));
                });
                Barrier();
                var lowerValue = context.Load(context._uintType, ExchangePointer(exchange, context.UInt(0)));
                var upperValue = context.Load(context._uintType, ExchangePointer(exchange, context.UInt(1)));
                var lowerActive = context.IsNotZero(context.Load(context._uintType, ExchangePointer(exchange, context.UInt(2))));
                var upperActive = context.IsNotZero(context.Load(context._uintType, ExchangePointer(exchange, context.UInt(3))));
                return context._module.AddInstruction(
                    SpirvOp.Select,
                    context._uintType,
                    context._module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        context._boolType,
                        context.LogicalNot(lowerActive),
                        upperActive),
                    upperValue,
                    lowerValue);
            }

            public uint BroadcastLane(uint value, uint lane) =>
                ExchangeValue(
                    _context._module.AddInstruction(
                        SpirvOp.IEqual,
                        _context._boolType,
                        _context.GuestWaveLane(),
                        lane),
                    value);

            public uint BroadcastValue(uint writer, uint value, uint lane) => ExchangeValue(writer, value);

            public uint WaveMaskAny(uint register, uint laneActive) =>
                _context._subgroupInvocationIdInput != 0
                    ? _context.IsNotZero64(_context.LoadS64(register))
                    : SubgroupAny(_context.Load(_context._boolType, laneActive));

            public uint CurrentExecMask() =>
                _context._subgroupInvocationIdInput != 0
                    ? _context.LoadS64(126)
                    : BooleanToWaveMask(_context.Load(_context._boolType, _context._exec));

            public uint NormalizeExecMask(uint mask) => mask;

            public void StoreWaveMask(uint register, uint condition)
            {
                var context = _context;
                if (context._waveLaneCount == 32)
                {
                    context.StoreS(register, context.Narrow(BooleanToWaveMask(condition)));
                    return;
                }

                if (context._subgroupInvocationIdInput != 0 &&
                    _emittingPc is { } pc &&
                    HalfMaskPlan() is { } plan &&
                    plan.HalfMaskWrites.TryGetValue(pc, out var halfRegister) &&
                    halfRegister == register)
                {
                    context.StoreS64(register, BooleanToHalfWaveMask(condition));
                    return;
                }

                context.StoreS64(register, BooleanToWaveMask(condition));
            }

            private uint OwnHalfBallot(uint condition)
            {
                var context = _context;
                var ballot = context._module.AddInstruction(
                    SpirvOp.GroupNonUniformBallot,
                    context._uvec4Type,
                    context.UInt(3),
                    condition);
                return context._module.AddInstruction(
                    SpirvOp.VectorExtractDynamic,
                    context._uintType,
                    ballot,
                    context.ShiftRightLogical(
                        context.Load(context._uintType, context._subgroupInvocationIdInput),
                        context.UInt(5)));
            }

            private uint BooleanToHalfWaveMask(uint condition)
            {
                var context = _context;
                var owned = context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, OwnHalfBallot(condition));
                return context._module.AddInstruction(
                    SpirvOp.Select,
                    context._ulongType,
                    context._module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        context._boolType,
                        context.GuestWaveLane(),
                        context.UInt(32)),
                    context.ShiftLeftLogical64(owned, context._module.Constant64(context._ulongType, 32)),
                    owned);
            }

            private uint BothHalvesOfOwnBallot(uint condition)
            {
                var context = _context;
                var owned = context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, OwnHalfBallot(condition));
                return context.BitwiseOr64(
                    owned,
                    context.ShiftLeftLogical64(owned, context._module.Constant64(context._ulongType, 32)));
            }

            private uint IsHalfWaveLeader(uint lane) =>
                _context._module.AddInstruction(
                    SpirvOp.IEqual,
                    _context._boolType,
                    _context.BitwiseAnd(lane, _context.UInt(31)),
                    _context.UInt(0));

            private uint BeginExchange()
            {
                var context = _context;
                var parity = context.Load(context._uintType, _exchangeParity);
                context.Store(_exchangeParity, context.BitwiseXor(parity, context.UInt(1)));
                return context.IAdd(
                    WaveExchangeBase(),
                    context._module.AddInstruction(
                        SpirvOp.IMul,
                        context._uintType,
                        parity,
                        context.UInt(ExchangeSlotCount)));
            }

            private uint WaveExchangeBase()
            {
                var context = _context;
                if (!_multiWave)
                {
                    return context.UInt(_exchangeOffset);
                }

                var wave = context.ShiftRightLogical(
                    context.Load(context._uintType, context._localInvocationIndexInput),
                    context.UInt(6));
                return context.IAdd(
                    context.UInt(_exchangeOffset),
                    context._module.AddInstruction(SpirvOp.IMul, context._uintType, wave, context.UInt(_waveStride)));
            }

            // Zeroes each wave's arrival counter. Runs once at entry, where the whole workgroup is
            // converged, so the workgroup barrier is safe here.
            private void InitializeRendezvous()
            {
                if (!_multiWave || !UsesExchange)
                {
                    return;
                }

                var context = _context;
                context.EmitConditional(
                    context._module.AddInstruction(
                        SpirvOp.IEqual,
                        context._boolType,
                        context.GuestWaveLane(),
                        context.UInt(0)),
                    () => context.Store(
                        ExchangePointer(WaveExchangeBase(), context.UInt(ExchangeDwordCount)),
                        context.UInt(0)));
                WorkgroupBarrier();
            }

            // Both halves of the wave arrive, then wait until the counter shows two arrivals per
            // rendezvous so far. The halves of one wave are co-resident in the workgroup, so the
            // wait always ends.
            private void WaveRendezvous()
            {
                var context = _context;
                var counter = ExchangePointer(WaveExchangeBase(), context.UInt(ExchangeDwordCount));
                context.EmitConditional(
                    IsHalfWaveLeader(context.GuestWaveLane()),
                    () => context._module.AddInstruction(
                        SpirvOp.AtomicIAdd,
                        context._uintType,
                        counter,
                        context.UInt(2),
                        context.UInt(0x108),
                        context.UInt(1)));
                var epoch = context.IAdd(context.Load(context._uintType, _rendezvousEpoch), context.UInt(1));
                context.Store(_rendezvousEpoch, epoch);
                var target = context._module.AddInstruction(SpirvOp.IMul, context._uintType, epoch, context.UInt(2));

                var header = context._module.AllocateId();
                var continueLabel = context._module.AllocateId();
                var mergeLabel = context._module.AllocateId();
                context._module.AddStatement(SpirvOp.Branch, header);
                context._module.AddLabel(header);
                var arrived = context._module.AddInstruction(
                    SpirvOp.AtomicLoad,
                    context._uintType,
                    counter,
                    context.UInt(2),
                    context.UInt(0x102));
                var done = context._module.AddInstruction(
                    SpirvOp.UGreaterThanEqual,
                    context._boolType,
                    arrived,
                    target);
                context._module.AddStatement(SpirvOp.LoopMerge, mergeLabel, continueLabel, 0);
                context._module.AddStatement(SpirvOp.BranchConditional, done, mergeLabel, continueLabel);
                context._module.AddLabel(continueLabel);
                context._module.AddStatement(SpirvOp.Branch, header);
                context._module.AddLabel(mergeLabel);
            }

            private uint ExchangePointer(uint exchange, uint slot) =>
                _context._module.AddInstruction(
                    SpirvOp.AccessChain,
                    _exchangeElementPointer,
                    _exchange,
                    _context.IAdd(exchange, slot));

            private uint ExchangeValue(uint writer, uint value)
            {
                var context = _context;
                var exchange = BeginExchange();
                context.EmitConditional(writer, () => context.Store(ExchangePointer(exchange, context.UInt(0)), value));
                Barrier();
                return context.Load(context._uintType, ExchangePointer(exchange, context.UInt(0)));
            }

            private void EmitExactWaveMasks(IReadOnlyList<uint> pairs)
            {
                var context = _context;
                var lane = context.GuestWaveLane();
                var upperHalf = context.ShiftRightLogical(lane, context.UInt(5));
                var lowerHalf = context._module.AddInstruction(SpirvOp.IEqual, context._boolType, upperHalf, context.UInt(0));
                const int pairsPerExchange = (int)ExchangeSlotCount / 2;
                for (var start = 0; start < pairs.Count; start += pairsPerExchange)
                {
                    var count = Math.Min(pairsPerExchange, pairs.Count - start);
                    var owned = new uint[count];
                    for (var index = 0; index < count; index++)
                    {
                        var pair = pairs[start + index];
                        owned[index] = context._module.AddInstruction(
                            SpirvOp.Select,
                            context._uintType,
                            lowerHalf,
                            context.LoadS(pair),
                            context.LoadS(pair + 1));
                    }

                    var exchange = BeginExchange();
                    context.EmitConditional(IsHalfWaveLeader(lane), () =>
                    {
                        for (var index = 0; index < count; index++)
                        {
                            context.Store(
                                ExchangePointer(exchange, context.IAdd(context.UInt((uint)index * 2), upperHalf)),
                                owned[index]);
                        }
                    });
                    Barrier();
                    for (var index = 0; index < count; index++)
                    {
                        var pair = pairs[start + index];
                        context.StoreS(pair, context.Load(context._uintType, ExchangePointer(exchange, context.UInt((uint)index * 2))));
                        context.StoreS(pair + 1, context.Load(context._uintType, ExchangePointer(exchange, context.UInt((uint)index * 2 + 1))));
                    }
                }
            }

            private Ir.Gen5Wave64HalfMaskPlan? HalfMaskPlan()
            {
                if (_halfMaskPlanBuilt)
                {
                    return _halfMaskPlan;
                }

                _halfMaskPlanBuilt = true;
                var context = _context;
                if (HalfWaveMasks && UsesExchange && context._subgroupInvocationIdInput != 0)
                {
                    var sharedFlatPcs = context._request.Memory.Entries
                        .Where(static memory => memory.AddressSpace is FlatAddressSpace.Shared or FlatAddressSpace.SharedOrPrivate)
                        .Select(static memory => memory.Pc)
                        .ToHashSet();
                    _halfMaskPlan = Ir.Gen5Wave64HalfMaskAnalysis.Analyze(context._request.Program, sharedFlatPcs);
                }

                return _halfMaskPlan;
            }

            private void Barrier()
            {
                if (_multiWave)
                {
                    // The rendezvous needs the exchange area, declared only with subgroup operations.
                    if (UsesExchange)
                    {
                        WaveRendezvous();
                    }

                    return;
                }

                WorkgroupBarrier();
            }

            private void WorkgroupBarrier()
            {
                var workgroup = _context.UInt(2);
                _context._module.AddStatement(
                    SpirvOp.ControlBarrier,
                    workgroup,
                    workgroup,
                    _context.UInt(0x108));
            }
        }
    }
}





