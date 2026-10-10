// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private uint _cooperativeProgramCounters;
        // Nonzero only while emitting a block selected by the workgroup dispatcher.
        private uint _executingWave;
        private uint CooperativeWaveCount => checked(_localSizeX * _localSizeY * _localSizeZ / 64);
        private uint CooperativeWaveId() => ShiftRightLogical(Load(_uintType, _localInvocationIndexInput), UInt(6));

        private void DeclareCooperativeWaveScratch()
        {
            var array = _module.TypeArray(_uintType, CooperativeWaveCount);
            _cooperativeProgramCounters = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Workgroup, array), SpirvStorageClass.Workgroup);
            _module.AddName(_cooperativeProgramCounters, "waveProgramCounters");
            _interfaces.Add(_cooperativeProgramCounters);
        }

        private uint CooperativeCounterPointer(uint index) => _module.AddInstruction(
            SpirvOp.AccessChain, _wave64ExchangeElementPointer, _cooperativeProgramCounters, index);

        private uint SelectCooperativeBlock(IReadOnlyList<ShaderBlock> blocks)
        {
            var pc = _module.AddInstruction(SpirvOp.Select, _uintType,
                Load(_boolType, _programActive), Load(_uintType, _programCounter), UInt(uint.MaxValue));
            // Waiting guest waves must not pass a guest barrier until all still
            // running waves arrive. Host rendezvous used for ballots are distinct
            // from these guest barriers.
            for (var block = 0; block < blocks.Count; block++)
            {
                if (_request.Program.Instructions[blocks[block].StartIndex].Opcode != "SBarrier") continue;
                pc = _module.AddInstruction(SpirvOp.Select, _uintType,
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, pc, UInt((uint)block)),
                    UInt((uint)block | 0x80000000u), pc);
            }
            EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, GuestWaveLane(), UInt(0)),
                () => Store(CooperativeCounterPointer(CooperativeWaveId()), pc));
            EmitWave64Barrier();
            var selected = Load(_uintType, CooperativeCounterPointer(UInt(0)));
            for (uint wave = 1; wave < CooperativeWaveCount; wave++)
            {
                var candidate = Load(_uintType, CooperativeCounterPointer(UInt(wave)));
                selected = _module.AddInstruction(SpirvOp.Select, _uintType,
                    _module.AddInstruction(SpirvOp.ULessThan, _boolType, candidate, selected), candidate, selected);
            }
            // Every invocation finishes reading before a later iteration overwrites the counters.
            EmitWave64Barrier();
            return _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, selected, UInt(uint.MaxValue)),
                selected, BitwiseAnd(selected, UInt(0x7FFFFFFF)));
        }

        private bool TryEmitCooperativeDispatcher(IReadOnlyList<ShaderBlock> blocks, out string error)
        {
            error = string.Empty;
            var header = _module.AllocateId();
            var condition = _module.AllocateId();
            var body = _module.AllocateId();
            var continuation = _module.AllocateId();
            var exit = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(header);
            _module.AddStatement(SpirvOp.LoopMerge, exit, continuation, 0);
            _module.AddStatement(SpirvOp.Branch, condition);
            _module.AddLabel(condition);
            var selector = SelectCooperativeBlock(blocks);
            var anyActive = _module.AddInstruction(SpirvOp.INotEqual, _boolType, selector, UInt(uint.MaxValue));
            _module.AddStatement(SpirvOp.BranchConditional, anyActive, body, exit);
            _module.AddLabel(body);
            var merge = _module.AllocateId();
            var invalid = _module.AllocateId();
            var labels = blocks.Select(_ => _module.AllocateId()).ToArray();
            var operands = new List<uint> { selector, invalid };
            for (var block = 0; block < blocks.Count; block++)
            {
                operands.Add((uint)block); operands.Add(labels[block]);
            }
            _module.AddStatement(SpirvOp.SelectionMerge, merge, 0);
            _module.AddStatement(SpirvOp.Switch, operands.ToArray());
            for (var block = 0; block < blocks.Count; block++)
            {
                _module.AddLabel(labels[block]);
                _executingWave = LogicalAnd(Load(_boolType, _programActive),
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _programCounter), UInt((uint)block)));
                Store(_exec, Load(_boolType, _exec));
                // All host subgroups execute the same block, including its rendezvous;
                // only the guest waves at this PC may change architectural state.
                if (!TryEmitBlock(blocks, block, out error)) return false;
                _executingWave = 0;
                Store(_exec, IsLaneSetInMaskRegisters(126));
                _module.AddStatement(SpirvOp.Branch, merge);
            }
            _module.AddLabel(invalid);
            Store(_programActive, _module.ConstantBool(false));
            _module.AddStatement(SpirvOp.Branch, merge);
            _module.AddLabel(merge);
            _module.AddStatement(SpirvOp.Branch, continuation);
            _module.AddLabel(continuation);
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(exit);
            return true;
        }

        private uint GuardCooperativeUint(uint value, uint previous) => _executingWave == 0 ? value :
            _module.AddInstruction(SpirvOp.Select, _uintType, _executingWave, value, previous);
    }
}
