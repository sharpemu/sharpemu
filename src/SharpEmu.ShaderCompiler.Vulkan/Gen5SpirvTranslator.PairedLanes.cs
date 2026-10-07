// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
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

        private uint ExecutionWaveMask() => _pairedMeshLanes || (_emulateWave64 && _subgroupInvocationIdInput != 0)
            ? LoadS64(126) : BooleanToWaveMask(Load(_boolType, _exec));

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

            if (instruction.Opcode is "DsAppend" or "DsConsume" or "DsSwizzleB32")
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
