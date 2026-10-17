// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint PairedHalfLaneCount = 32;

        private static readonly bool PairedWave64Enabled = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_PAIRED"), "0", StringComparison.Ordinal);

        private readonly bool _pairedLanes;
        private int _laneHalf;
        private bool _inLanePass;
        private readonly Dictionary<uint, uint> _upperVectorRegisterVariables = new();
        private readonly uint[] _packedHalfRegistersByHalf = new uint[2];
        private uint _lowerExec;
        private uint _upperExec;
        private uint _lowerVcc;
        private uint _upperVcc;
        private uint _lowerScratch;
        private uint _upperScratch;
        private uint _subgroupIdInput;
        private uint _pairedCounterResult;
        private Dictionary<uint, uint>? _scalarSnapshot;

        private void SelectLaneHalf(int half)
        {
            if (!_pairedLanes)
            {
                return;
            }

            _packedHalfRegistersByHalf[_laneHalf] = _packedHalfRegisters;
            _laneHalf = half;
            _packedHalfRegisters = _packedHalfRegistersByHalf[half];
            _exec = half == 0 ? _lowerExec : _upperExec;
            _vcc = half == 0 ? _lowerVcc : _upperVcc;
            if (_lowerScratch != 0)
            {
                _scratch = half == 0 ? _lowerScratch : _upperScratch;
            }
        }

        private void DeclarePairedLaneFlags(uint lowerVcc, uint lowerExec, Func<bool, uint> flag)
        {
            _lowerVcc = lowerVcc;
            _lowerExec = lowerExec;
            if (!_pairedLanes)
            {
                return;
            }

            _upperVcc = flag(false);
            _upperExec = flag(true);
            _interfaces.Add(_upperVcc);
            _interfaces.Add(_upperExec);
            _module.AddName(_upperVcc, "vccUpperHalf");
            _module.AddName(_upperExec, "execUpperHalf");
        }

        private void DeclarePairedScratch(uint arrayPointer, uint arrayType)
        {
            _lowerScratch = _scratch;
            if (!_pairedLanes)
            {
                return;
            }

            _upperScratch = _module.AddGlobalVariable(
                arrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(arrayType));
            _interfaces.Add(_upperScratch);
            _module.AddName(_upperScratch, "scratchUpperHalf");
        }

        private void DeclarePairedLaneInputs()
        {
            if (!_pairedLanes)
            {
                return;
            }

            _subgroupIdInput = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Input, _uintType),
                SpirvStorageClass.Input);
            _module.AddDecoration(_subgroupIdInput, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SubgroupId);
            _interfaces.Add(_subgroupIdInput);
        }

        private uint PairedHostThreadCount()
        {
            var guestThreads = checked(_localSizeX * _localSizeY * _localSizeZ);
            return (guestThreads + 63) / 64 * PairedHalfLaneCount;
        }

        private Dictionary<uint, uint> CurrentVectorRegisterVariables() =>
            _pairedLanes && _laneHalf == 1 ? _upperVectorRegisterVariables : _vectorRegisterVariables;

        private string CurrentVectorRegisterPrefix() =>
            _pairedLanes && _laneHalf == 1 ? "vu" : "v";

        private uint PairedSubgroupLane() => Load(_uintType, _subgroupInvocationIdInput);

        private uint PairedGuestLane() =>
            _laneHalf == 0 ? PairedSubgroupLane() : IAdd(PairedSubgroupLane(), UInt(PairedHalfLaneCount));

        private uint HalfBallot(uint condition) =>
            _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type, UInt(3), condition),
                0);

        private uint SubgroupBroadcast(uint value, uint lane) =>
            _module.AddInstruction(SpirvOp.GroupNonUniformBroadcast, _uintType, UInt(3), value, lane);

        private uint ExecHalfBallot(int half)
        {
            var current = _laneHalf;
            SelectLaneHalf(half);
            var ballot = HalfBallot(Load(_boolType, _exec));
            SelectLaneHalf(current);
            return ballot;
        }

        private uint VectorSourceOfHalf(Gen5ShaderInstruction instruction, int sourceIndex, int half)
        {
            var current = _laneHalf;
            SelectLaneHalf(half);
            var value = GetRawSource(instruction, sourceIndex);
            SelectLaneHalf(current);
            return value;
        }

        private static bool IsPairedLaneInstruction(Gen5ShaderInstruction instruction) =>
            instruction.Opcode is not ("VReadlaneB32" or "VReadfirstlaneB32" or "DsAppend" or "DsConsume") &&
            instruction.Encoding is
                Gen5ShaderEncoding.Mubuf or
                Gen5ShaderEncoding.Mtbuf or
                Gen5ShaderEncoding.Vop1 or
                Gen5ShaderEncoding.Vop2 or
                Gen5ShaderEncoding.Vopc or
                Gen5ShaderEncoding.Vop3 or
                Gen5ShaderEncoding.Vop3p or
                Gen5ShaderEncoding.Vintrp or
                Gen5ShaderEncoding.Ds or
                Gen5ShaderEncoding.Flat or
                Gen5ShaderEncoding.Mimg or
                Gen5ShaderEncoding.Exp;

        private bool TryEmitPairedLaneInstruction(Gen5ShaderInstruction instruction, out string error)
        {
            _scalarSnapshot = SnapshotScalarSources(instruction);
            _inLanePass = true;
            try
            {
                for (var half = 0; half < 2; half++)
                {
                    SelectLaneHalf(half);
                    if (!TryEmitInstruction(instruction, out error))
                    {
                        return false;
                    }
                }

                error = string.Empty;
                return true;
            }
            finally
            {
                _inLanePass = false;
                _scalarSnapshot = null;
                SelectLaneHalf(0);
            }
        }

        private Dictionary<uint, uint>? SnapshotScalarSources(Gen5ShaderInstruction instruction)
        {
            Dictionary<uint, uint>? snapshot = null;
            foreach (var operand in instruction.Sources)
            {
                if (operand.Kind != Gen5OperandKind.ScalarRegister)
                {
                    continue;
                }

                for (var register = operand.Value; register <= operand.Value + 1 && register < ScalarRegisterCount; register++)
                {
                    if (register == 125)
                    {
                        continue;
                    }

                    snapshot ??= new Dictionary<uint, uint>();
                    if (!snapshot.ContainsKey(register))
                    {
                        snapshot[register] = Load(_uintType, ScalarPointer(register));
                    }
                }
            }

            return snapshot;
        }

        private void UpdateLaneMaskFlag(uint lowRegister)
        {
            if (!_pairedLanes || _inLanePass)
            {
                Store(lowRegister == 106 ? _vcc : _exec, IsLaneSetInMaskRegisters(lowRegister));
                return;
            }

            var current = _laneHalf;
            for (var half = 0; half < 2; half++)
            {
                SelectLaneHalf(half);
                Store(lowRegister == 106 ? _vcc : _exec, IsLaneSetInMaskRegisters(lowRegister));
            }

            SelectLaneHalf(current);
        }

        private void StorePairedLaneMaskWord(uint register, uint value)
        {
            var word = _laneHalf == 0
                ? Narrow(value)
                : Narrow(ShiftRightLogical64(value, _module.Constant64(_ulongType, 32)));
            StoreS(register + (uint)_laneHalf, word);
        }

        private void EmitPairedComputeInitialState()
        {
            var workGroupId = Load(_uvec3Type, _workGroupIdInput);
            var waveBase = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                Load(_uintType, _subgroupIdInput),
                UInt(2 * PairedHalfLaneCount));
            var guestThreads = checked(_localSizeX * _localSizeY * _localSizeZ);
            var limits = new uint[3];
            var groups = new uint[3];
            for (uint component = 0; component < 3; component++)
            {
                limits[component] = ComputeThreadLimit(component);
                groups[component] = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    workGroupId,
                    (uint)_physicalAxisOfLogical[component]);
            }

            var active = new uint[2];
            for (var half = 0; half < 2; half++)
            {
                SelectLaneHalf(half);
                var index = IAdd(waveBase, PairedGuestLane());
                var inBounds = _module.AddInstruction(SpirvOp.ULessThan, _boolType, index, UInt(guestThreads));
                var row = _module.AddInstruction(SpirvOp.UDiv, _uintType, index, UInt(_localSizeX));
                var local = new[]
                {
                    _module.AddInstruction(SpirvOp.UMod, _uintType, index, UInt(_localSizeX)),
                    _module.AddInstruction(SpirvOp.UMod, _uintType, row, UInt(_localSizeY)),
                    _module.AddInstruction(SpirvOp.UDiv, _uintType, row, UInt(_localSizeY)),
                };
                var sizes = new[] { _localSizeX, _localSizeY, _localSizeZ };
                for (uint component = 0; component < 3; component++)
                {
                    StoreV(component, local[component], guardWithExec: false);
                    var global = IAdd(
                        _module.AddInstruction(SpirvOp.IMul, _uintType, groups[component], UInt(sizes[component])),
                        local[component]);
                    inBounds = LogicalAnd(
                        inBounds,
                        _module.AddInstruction(SpirvOp.ULessThan, _boolType, global, limits[component]));
                }

                active[half] = inBounds;
            }

            SelectLaneHalf(0);
            var lowerActive = HalfBallot(active[0]);
            var upperActive = HalfBallot(active[1]);
            StoreS(126, lowerActive);
            StoreS(127, upperActive);
            Store(_programActive, _module.AddInstruction(SpirvOp.LogicalOr, _boolType, active[0], active[1]));

            if (_request.ComputeSystemRegisters is { } registers)
            {
                StoreComputeSystemRegister(registers.WorkGroupXRegister, workGroupId, (uint)_physicalAxisOfLogical[0]);
                StoreComputeSystemRegister(registers.WorkGroupYRegister, workGroupId, (uint)_physicalAxisOfLogical[1]);
                StoreComputeSystemRegister(registers.WorkGroupZRegister, workGroupId, (uint)_physicalAxisOfLogical[2]);
                if (registers.ThreadGroupSizeRegister is { } sizeRegister)
                {
                    StoreS(sizeRegister, UInt(guestThreads));
                }
            }
        }

        private uint PairedReadFirstLane(Gen5ShaderInstruction instruction)
        {
            var lowerActive = ExecHalfBallot(0);
            var upperActive = ExecHalfBallot(1);
            var lowerValue = VectorSourceOfHalf(instruction, 0, 0);
            var upperValue = VectorSourceOfHalf(instruction, 0, 1);
            var lowerHasActive = IsNotZero(lowerActive);
            var useLower = LogicalOr(lowerHasActive, LogicalNot(IsNotZero(upperActive)));
            var lowerLane = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                lowerHasActive,
                Ext(73, _uintType, lowerActive),
                UInt(0));
            var lane = BitwiseAnd(
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    useLower,
                    lowerLane,
                    Ext(73, _uintType, upperActive)),
                UInt(PairedHalfLaneCount - 1));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                useLower,
                SubgroupBroadcast(lowerValue, lane),
                SubgroupBroadcast(upperValue, lane));
        }

        private uint PairedReadLane(Gen5ShaderInstruction instruction, uint selectedLane)
        {
            var lowerValue = VectorSourceOfHalf(instruction, 0, 0);
            var upperValue = VectorSourceOfHalf(instruction, 0, 1);
            var lane = BitwiseAnd(selectedLane, UInt(PairedHalfLaneCount - 1));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, selectedLane, UInt(PairedHalfLaneCount)),
                SubgroupBroadcast(lowerValue, lane),
                SubgroupBroadcast(upperValue, lane));
        }

        private uint LogicalOr(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.LogicalOr, _boolType, left, right);

        private void EmitPairedWaveCounter(uint destination, uint inBounds, Func<uint, uint> update)
        {
            var lowerActive = ExecHalfBallot(0);
            var upperActive = ExecHalfBallot(1);
            var activeCount = IAdd(
                _module.AddInstruction(SpirvOp.BitCount, _uintType, lowerActive),
                _module.AddInstruction(SpirvOp.BitCount, _uintType, upperActive));
            var anyActive = IsNotZero(BitwiseOr(lowerActive, upperActive));
            var leaderLane = BitwiseAnd(
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    IsNotZero(lowerActive),
                    Ext(73, _uintType, lowerActive),
                    Ext(73, _uintType, upperActive)),
                UInt(PairedHalfLaneCount - 1));
            if (_pairedCounterResult == 0)
            {
                _pairedCounterResult = DeclareFunctionUint(UInt(0));
                _module.AddName(_pairedCounterResult, "pairedCounterResult");
            }

            var valid = LogicalAnd(inBounds, anyActive);
            var leader = LogicalAnd(
                valid,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, PairedSubgroupLane(), leaderLane));
            EmitConditional(leader, () => Store(_pairedCounterResult, update(activeCount)));
            var original = SubgroupBroadcast(Load(_uintType, _pairedCounterResult), leaderLane);
            var result = _module.AddInstruction(SpirvOp.Select, _uintType, valid, original, UInt(0));
            var current = _laneHalf;
            for (var half = 0; half < 2; half++)
            {
                SelectLaneHalf(half);
                StoreV(destination, result);
            }

            SelectLaneHalf(current);
        }

        private sealed class PairedWave64Lowering : IWaveLowering
        {
            private readonly CompilationContext _context;

            public PairedWave64Lowering(CompilationContext context)
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

            public void InitializeMasks() =>
                _context.StoreS64(106, _context._module.Constant64(_context._ulongType, 0));

            public uint BooleanToWaveMask(uint condition)
            {
                var context = RequireLanePass(nameof(BooleanToWaveMask));
                var wide = context._module.AddInstruction(SpirvOp.UConvert, context._ulongType, context.HalfBallot(condition));
                return context._laneHalf == 0
                    ? wide
                    : context.ShiftLeftLogical64(wide, context._module.Constant64(context._ulongType, 32));
            }

            public uint SubgroupAny(uint condition)
            {
                var context = RequireLanePass(nameof(SubgroupAny));
                return context._module.AddInstruction(SpirvOp.GroupNonUniformAny, context._boolType, context.UInt(3), condition);
            }

            public uint CurrentLaneBit() =>
                _context.ShiftLeftLogical64(
                    _context._module.Constant64(_context._ulongType, 1),
                    _context._module.AddInstruction(SpirvOp.UConvert, _context._ulongType, _context.PairedGuestLane()));

            public uint BroadcastFirstActive(uint value) =>
                throw new InvalidOperationException("paired wave64 reads the first active lane from both halves");

            public uint BroadcastLane(uint value, uint lane) =>
                throw new InvalidOperationException("paired wave64 reads a lane from both halves");

            public uint BroadcastValue(uint writer, uint value, uint lane) =>
                throw new InvalidOperationException("paired wave64 broadcasts a wave counter from both halves");

            public uint WaveMaskAny(uint register, uint laneActive) =>
                _context.IsNotZero64(_context.LoadS64(register));

            public uint CurrentExecMask() => _context.LoadS64(126);

            public uint NormalizeExecMask(uint mask) => mask;

            public void StoreWaveMask(uint register, uint condition)
            {
                var context = RequireLanePass(nameof(StoreWaveMask));
                context.StoreS(register + (uint)context._laneHalf, context.HalfBallot(condition));
            }

            private CompilationContext RequireLanePass(string operation)
            {
                if (!_context._inLanePass)
                {
                    throw new InvalidOperationException($"paired wave64 {operation} needs a lane half");
                }

                return _context;
            }
        }
    }
}
