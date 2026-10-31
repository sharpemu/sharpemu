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
            instruction.Opcode is not ("VReadlaneB32" or "VReadfirstlaneB32" or "DsAppend" or "DsConsume" or "DsOrderedCount") &&
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
                    (uint)_physicalGroupAxisOfLogical[component]);
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
                StoreComputeSystemRegister(registers.WorkGroupXRegister, workGroupId, (uint)_physicalGroupAxisOfLogical[0]);
                StoreComputeSystemRegister(registers.WorkGroupYRegister, workGroupId, (uint)_physicalGroupAxisOfLogical[1]);
                StoreComputeSystemRegister(registers.WorkGroupZRegister, workGroupId, (uint)_physicalGroupAxisOfLogical[2]);
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

        // A pair of lanes past the group's thread count has no running invocation to read, but a scalar that V_WRITELANE
        // spilled to either lane is in every invocation's spill slot.
        private uint PairedReadLaneOrSpillSlot(Gen5ShaderInstruction instruction, uint selectedLane)
        {
            var value = PairedReadLane(instruction, selectedLane);
            var spilled = ReadLaneSpillSlot(instruction, selectedLane, value);
            if (spilled == value)
            {
                return value;
            }

            var pairBit = ShiftLeftLogical(UInt(1), BitwiseAnd(selectedLane, UInt(PairedHalfLaneCount - 1)));
            var pairRuns = IsNotZero(BitwiseAnd(HalfBallot(_module.ConstantBool(true)), pairBit));
            return _module.AddInstruction(SpirvOp.Select, _uintType, pairRuns, value, spilled);
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

        private readonly bool _pairedMeshLanes;
        private int _logicalHalf;
        private readonly List<MeshLaneRegisters> _meshLaneRegisters = [];
        private readonly Dictionary<uint, uint[]> _pendingWaveMasks = [];
        private bool _collectWaveMasks;
        private uint _meshSubgroupInput;
        private readonly uint[] _pairedDppSources = new uint[2];
        private readonly uint[] _pairedActiveWords = new uint[2];

        private sealed record MeshLaneRegisters(uint Vectors, uint PackedHalves, uint Scratch, uint Execution, uint Comparison,
            uint Position, uint Primitive, Dictionary<uint, (uint Output, uint Value)> Parameters);

        private uint DeclarePrivateValue(uint type)
        {
            var variable = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.Private, type),
                SpirvStorageClass.Private, _module.ConstantNull(type));
            _interfaces.Add(variable);
            return variable;
        }

        private void DeclarePairedMeshRegisters()
        {
            if (_stage == Gen5SpirvStage.Mesh)
                _vectorRegisters = DeclarePrivateValue(_module.TypeArray(_uintType, VectorRegisterCount));
            if (_stage != Gen5SpirvStage.Mesh || _request.Mesh?.DeviceSubgroupLaneCount == 0) return;
            _meshSubgroupInput = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Input, _uintType), SpirvStorageClass.Input);
            _module.AddDecoration(_meshSubgroupInput, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SubgroupId);
            _interfaces.Add(_meshSubgroupInput);
            if (!_pairedMeshLanes) return;
            _meshLaneRegisters.Add(new(_vectorRegisters, _packedHalfRegisters, _scratch, _exec, _vcc,
                _meshPositionValue, _meshPrimitiveValue, _meshParameters));
            var parameters = new Dictionary<uint, (uint Output, uint Value)>();
            foreach (var (location, parameter) in _meshParameters)
                parameters.Add(location, (parameter.Output, DeclarePrivateValue(_vec4Type)));
            _meshLaneRegisters.Add(new(
                DeclarePrivateValue(_module.TypeArray(_uintType, VectorRegisterCount)),
                0,
                _scratch == 0 ? 0 : DeclarePrivateValue(_module.TypeArray(_uintType, _scratchDwordCount)),
                DeclarePrivateValue(_boolType), DeclarePrivateValue(_boolType),
                DeclarePrivateValue(_vec4Type), DeclarePrivateValue(_uintType), parameters));
        }

        private void SelectMeshHalf(int half)
        {
            _logicalHalf = half;
            var registers = _meshLaneRegisters[half];
            _vectorRegisters = registers.Vectors;
            _packedHalfRegisters = registers.PackedHalves;
            _scratch = registers.Scratch;
            _meshPositionValue = registers.Position;
            _meshPrimitiveValue = registers.Primitive;
            _meshParameters = registers.Parameters;
            _exec = registers.Execution;
            _vcc = registers.Comparison;
        }

        private void UpdatePairedMeshPredicate(uint register, uint value)
        {
            var registers = _meshLaneRegisters[(int)(register & 1)];
            var predicate = register >= 126 ? registers.Execution : registers.Comparison;
            var laneBit = ShiftLeftLogical(UInt(1), Load(_uintType, _subgroupInvocationIdInput));
            Store(predicate, IsNotZero(BitwiseAnd(value, laneBit)));
        }

        private uint MeshInvocationIndex()
        {
            if (_meshSubgroupInput == 0) return Load(_uintType, _localInvocationIndexInput);
            return IAdd(_module.AddInstruction(SpirvOp.IMul, _uintType,
                Load(_uintType, _meshSubgroupInput), UInt(_waveLaneCount)),
                IAdd(Load(_uintType, _subgroupInvocationIdInput), UInt((uint)_logicalHalf * 32)));
        }

        private uint ExecutionWaveMask() => _pairedMeshLanes ? LoadS64(126) : _waveLowering.CurrentExecMask();

        private uint WaveMaskHasLanes(uint register) => _pairedMeshLanes
            ? IsNotZero64(LoadS64(register))
            : WaveMaskAny(register, register == 126 ? _exec : _vcc);

        private uint ShufflePairedValue(uint low, uint high, uint lane)
        {
            var index = BitwiseAnd(lane, UInt(31));
            var lowValue = _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType, UInt(3), low, index);
            var highValue = _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType, UInt(3), high, index);
            return _module.AddInstruction(SpirvOp.Select, _uintType,
                IsNotZero(BitwiseAnd(lane, UInt(32))), highValue, lowValue);
        }

        private static bool IsMeshSharedMemoryRead(Gen5ShaderInstruction instruction) =>
            instruction.Control is Gen5DataShareControl { Gds: false } &&
            instruction.Opcode.StartsWith("DsRead", StringComparison.Ordinal);

        private bool TryEmitPairedInstruction(Gen5ShaderInstruction instruction, out string error,
            bool continuesSharedMemoryReads)
        {
            SelectMeshHalf(0);
            if (instruction.Opcode is "VReadlaneB32" or "VReadfirstlaneB32")
            {
                error = string.Empty;
                if (instruction.Sources.Count == 0 || instruction.Destinations.Count != 1 ||
                    instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                    (instruction.Opcode == "VReadlaneB32" && instruction.Sources.Count < 2))
                {
                    error = "The wave read has invalid operands.";
                    return false;
                }
                var low = GetRawSource(instruction, 0);
                var lane = instruction.Opcode == "VReadlaneB32"
                    ? BitwiseAnd(GetRawSource(instruction, 1), UInt(63))
                    : FirstExecutionLane();
                SelectMeshHalf(1);
                var high = GetRawSource(instruction, 0);
                var value = ShufflePairedValue(low, high, lane);
                StoreS(instruction.Destinations[0].Value, value);
                SelectMeshHalf(0);
                return true;
            }

            if (instruction.Encoding is Gen5ShaderEncoding.Sop1 or Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or Gen5ShaderEncoding.Sopk or Gen5ShaderEncoding.Sopp or
                Gen5ShaderEncoding.Smem or Gen5ShaderEncoding.Smrd)
                return TryEmitInstruction(instruction, out error);

            if (instruction.Opcode is "DsAppend" or "DsConsume" or "DsOrderedCount" or "DsSwizzleB32")
            {
                error = "This cross-lane operation has no paired mesh implementation.";
                return false;
            }

            _pendingWaveMasks.Clear();
            if (instruction.Control is Gen5DppControl)
            {
                for (var half = 0; half < 2; half++)
                {
                    if (half != 0) SelectMeshHalf(half);
                    _pairedDppSources[half] = GetRawSource(instruction, 0, applyLaneSelection: false);
                    _pairedActiveWords[half] = _module.AddInstruction(SpirvOp.Select, _uintType,
                        Load(_boolType, _exec), UInt(1), UInt(0));
                }
            }
            _collectWaveMasks = true;
            for (var half = 0; half < 2; half++)
            {
                // DPP source collection leaves the second half selected.
                if (half != 0 || instruction.Control is Gen5DppControl) SelectMeshHalf(half);
                if (!TryEmitInstruction(instruction, out error)) return false;
            }
            _collectWaveMasks = false;
            SelectMeshHalf(0);
            foreach (var (register, conditions) in _pendingWaveMasks)
            {
                uint Ballot(uint condition) => _module.AddInstruction(SpirvOp.CompositeExtract, _uintType,
                    _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type, UInt(3), condition), 0);
                StoreS64(register, Pair64(Ballot(conditions[0]), Ballot(conditions[1])));
            }
            // Consecutive reads do not change LDS. Join once after the final read.
            if (instruction.Control is Gen5DataShareControl { Gds: false } && !continuesSharedMemoryReads)
                SynchronizePairedMeshMemory();
            error = string.Empty;
            return true;
        }

        private uint FirstExecutionLane()
        {
            var mask = LoadS64(126);
            var low = Narrow(mask);
            var high = Narrow(ShiftRightLogical64(mask, ULong(32)));
            var first = _module.AddInstruction(SpirvOp.Select, _uintType, IsNotZero(low),
                Ext(73, _uintType, low), IAdd(Ext(73, _uintType, high), UInt(32)));
            return _module.AddInstruction(SpirvOp.Select, _uintType, IsNotZero64(mask), first, UInt(0));
        }
    }
}
