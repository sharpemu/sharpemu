// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private Dictionary<uint, (uint Output, uint Value)> _meshParameters = [];
        private uint _meshPositionOutput;
        private uint _meshPositionValue;
        private uint _meshLayerOutput;
        private uint _meshLayerValues;
        private uint _meshPrimitiveIndicesOutput;
        private uint _meshCullOutput;
        private uint _meshPrimitiveValue;
        private uint _meshAllocation;
        private uint _meshBarrierWaiting;
        private uint _meshAnyActive;
        private bool _directMeshBlocks;
        private readonly List<(int Start, int End)> _meshLoopRegions = [];
        private bool _convergedMeshDispatch;
        private uint _meshBlockActive;
        private uint _meshBlockMerge;
        private (uint Header, uint Continue, uint Merge) _meshBarrierLoop;

        private void DeclareMeshOutputs()
        {
            var mesh = _request.Mesh ?? throw new InvalidOperationException("The mesh stage has no output configuration.");
            if (mesh.OutputVertexCapacity == 0 || mesh.OutputPrimitiveCapacity == 0 || mesh.ProvokingVertex > 2)
            {
                throw new InvalidOperationException("The mesh output configuration is invalid.");
            }
            if (mesh.DeviceSubgroupLaneCount != 0 &&
                (mesh.DeviceSubgroupLaneCount is not (32 or 64) || mesh.DeviceSubgroupLaneCount > _waveLaneCount ||
                 _localSizeX % _waveLaneCount != 0 || _localSizeY != 1 || _localSizeZ != 1))
            {
                throw new InvalidOperationException("The mesh workgroup must contain complete supported waves.");
            }

            if (_localInvocationIndexInput == 0)
            {
                _localInvocationIndexInput = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Input, _uintType),
                    SpirvStorageClass.Input);
                _module.AddDecoration(_localInvocationIndexInput, SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.LocalInvocationIndex);
                _interfaces.Add(_localInvocationIndexInput);
            }

            _meshAllocation = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Workgroup, _module.TypeArrayDistinct(_uintType, 2)),
                SpirvStorageClass.Workgroup);
            _meshLayerValues = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Workgroup, _module.TypeArrayDistinct(_uintType, mesh.OutputVertexCapacity)),
                SpirvStorageClass.Workgroup);
            _interfaces.Add(_meshAllocation);
            _interfaces.Add(_meshLayerValues);
            if (!_directMeshBlocks || _meshLoopRegions.Count != 0)
            {
                _meshBarrierWaiting = DeclarePrivateValue(_boolType);
                _meshAnyActive = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Workgroup, _uintType), SpirvStorageClass.Workgroup);
                _interfaces.Add(_meshAnyActive);
            }
            _meshPositionValue = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Private, _vec4Type),
                SpirvStorageClass.Private, _module.ConstantNull(_vec4Type));
            _meshPrimitiveValue = _module.AddGlobalVariable(
                _privateUintPointer, SpirvStorageClass.Private, UInt(0));
            _interfaces.Add(_meshPositionValue);
            _interfaces.Add(_meshPrimitiveValue);

            _meshPositionOutput = DeclareMeshOutput(_vec4Type, mesh.OutputVertexCapacity,
                SpirvBuiltIn.Position);
            _meshLayerOutput = DeclareMeshOutput(_uintType, mesh.OutputPrimitiveCapacity,
                SpirvBuiltIn.Layer, perPrimitive: true);
            _meshPrimitiveIndicesOutput = DeclareMeshOutput(_uvec3Type, mesh.OutputPrimitiveCapacity,
                SpirvBuiltIn.PrimitiveTriangleIndicesExt, perPrimitive: true);
            _meshCullOutput = DeclareMeshOutput(_boolType, mesh.OutputPrimitiveCapacity,
                SpirvBuiltIn.CullPrimitiveExt, perPrimitive: true);

            var locations = MeshShaderConfiguration.ParameterLocations(_request.Program, _requiredVertexOutputCount);
            foreach (var location in locations)
            {
                var output = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Output,
                        _module.TypeArrayDistinct(_vec4Type, mesh.OutputVertexCapacity)),
                    SpirvStorageClass.Output);
                _module.AddDecoration(output, SpirvDecoration.Location, location);
                _interfaces.Add(output);
                var value = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Private, _vec4Type),
                    SpirvStorageClass.Private, _module.ConstantNull(_vec4Type));
                _interfaces.Add(value);
                _meshParameters.Add(location, (output, value));
            }
        }

        private uint DeclareMeshOutput(uint elementType, uint length, SpirvBuiltIn builtIn,
            bool perPrimitive = false)
        {
            var output = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Output, _module.TypeArrayDistinct(elementType, length)),
                SpirvStorageClass.Output);
            _module.AddDecoration(output, SpirvDecoration.BuiltIn, (uint)builtIn);
            if (perPrimitive)
            {
                _module.AddDecoration(output, SpirvDecoration.PerPrimitiveExt);
            }
            _interfaces.Add(output);
            return output;
        }

        private void AddMeshExecutionModes(uint entryPoint)
        {
            var mesh = _request.Mesh!.Value;
            _module.AddExecutionMode(entryPoint, SpirvExecutionMode.OutputVertices, mesh.OutputVertexCapacity);
            _module.AddExecutionMode(entryPoint, SpirvExecutionMode.OutputPrimitivesExt, mesh.OutputPrimitiveCapacity);
            _module.AddExecutionMode(entryPoint, SpirvExecutionMode.OutputTrianglesExt);
        }

        private bool CanEmitDirectMeshBlocks(IReadOnlyList<ShaderBlock> blocks)
        {
            if (_stage != Gen5SpirvStage.Mesh || _hasIndirectControlFlow ||
                (_maxDispatcherSteps > 0 && blocks.Count > _maxDispatcherSteps)) return false;

            var instructions = _request.Program.Instructions;
            for (var index = 1; index < instructions.Count; index++)
                if (instructions[index].Pc <= instructions[index - 1].Pc) return false;

            var barrierPhases = new int[blocks.Count];
            var loopRegions = new List<(int Start, int End)>();
            var phase = 0;
            for (var index = 0; index < blocks.Count; index++)
            {
                barrierPhases[index] = phase;
                if (instructions[blocks[index].EndIndex - 1].Opcode == "SBarrier") phase++;
            }

            for (var index = 0; index < blocks.Count; index++)
            {
                var terminator = instructions[blocks[index].EndIndex - 1];
                if (!IsBranch(terminator.Opcode)) continue;
                if (!TryGetBranchTargetPc(terminator, out var target)) return false;
                if (IsExitBranchTarget(instructions, target)) continue;
                if (!TryFindBlock(blocks, target, out var destination) ||
                    barrierPhases[destination] != barrierPhases[index]) return false;
                if (destination <= index)
                {
                    for (var block = destination; block <= index; block++)
                        if (instructions[blocks[block].EndIndex - 1].Opcode == "SBarrier") return false;
                    loopRegions.Add((destination, index + 1));
                }
            }
            loopRegions.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            foreach (var region in loopRegions)
            {
                if (_meshLoopRegions.Count != 0 && region.Start < _meshLoopRegions[^1].End)
                {
                    var previous = _meshLoopRegions[^1];
                    _meshLoopRegions[^1] = (previous.Start, Math.Max(previous.End, region.End));
                }
                else _meshLoopRegions.Add(region);
            }
            return true;
        }

        private bool TryEmitDirectMeshBlocks(IReadOnlyList<ShaderBlock> blocks, out string error)
        {
            error = string.Empty;
            var cursor = 0;
            foreach (var region in _meshLoopRegions)
            {
                if (!TryEmitDirectMeshRange(blocks, cursor, region.Start, out error)) return false;
                BeginMeshBarrierLoop();
                if (!TryEmitDirectMeshRange(blocks, region.Start, region.End, out error)) return false;
                EndMeshBarrierLoop(region.Start, region.End);
                cursor = region.End;
            }
            if (!TryEmitDirectMeshRange(blocks, cursor, blocks.Count, out error)) return false;
            Store(_programActive, _module.ConstantBool(false));
            return true;
        }

        private bool TryEmitDirectMeshRange(IReadOnlyList<ShaderBlock> blocks, int start, int end, out string error)
        {
            error = string.Empty;
            for (var index = start; index < end; index++)
            {
                var selected = _module.AddInstruction(SpirvOp.IEqual, _boolType,
                    Load(_uintType, _programCounter), UInt((uint)index));
                _meshBlockActive = LogicalAnd(Load(_boolType, _programActive), selected);
                BeginMeshBlockSelection();
                if (!TryEmitBlock(blocks, index, out error))
                {
                    error = $"block=0x{blocks[index].StartPc:X}: {error}";
                    return false;
                }
                if (_meshLoopRegions.Count != 0) AdvanceMeshBlockBudget();
                _module.AddStatement(SpirvOp.Branch, _meshBlockMerge);
                _module.AddLabel(_meshBlockMerge);

                // All surviving paths cross each barrier. Ended waves remain at the host barrier.
                if (_request.Program.Instructions[blocks[index].EndIndex - 1].Opcode == "SBarrier")
                    EmitWave64Barrier();
            }
            return true;
        }

        private void BeginMeshBlockSelection()
        {
            var body = _module.AllocateId();
            _meshBlockMerge = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, _meshBlockMerge, 0);
            _module.AddStatement(SpirvOp.BranchConditional, _meshBlockActive, body, _meshBlockMerge);
            _module.AddLabel(body);
        }

        private void SynchronizePairedMeshMemory()
        {
            // Rejoin skipped waves before the barrier. Keep their register state unchanged.
            if (_directMeshBlocks || _convergedMeshDispatch)
            {
                _module.AddStatement(SpirvOp.Branch, _meshBlockMerge);
                _module.AddLabel(_meshBlockMerge);
            }
            _module.AddStatement(SpirvOp.ControlBarrier, UInt(3), UInt(2), UInt(0x108));
            if (_directMeshBlocks || _convergedMeshDispatch) BeginMeshBlockSelection();
        }

        private bool TryEmitConvergedMeshDispatch(IReadOnlyList<ShaderBlock> blocks, out string error)
        {
            error = string.Empty;
            _convergedMeshDispatch = true;
            var loopStart = FindMeshRepeatedBlockStart(blocks);
            if (!TryEmitMeshDispatchRange(blocks, 0, loopStart, out error)) return false;
            BeginMeshBarrierLoop();
            var validCounter = _module.AddInstruction(SpirvOp.ULessThan, _boolType,
                Load(_uintType, _programCounter), UInt((uint)blocks.Count));
            Store(_programActive, LogicalAnd(Load(_boolType, _programActive), validCounter));
            if (!TryEmitMeshDispatchRange(blocks, loopStart, blocks.Count, out error)) return false;
            EndMeshBarrierLoop();
            _convergedMeshDispatch = false;
            return true;
        }

        private int FindMeshRepeatedBlockStart(IReadOnlyList<ShaderBlock> blocks)
        {
            // No backward edge may enter the prefix. A guest barrier must stay in the dispatcher.
            if (_hasIndirectControlFlow) return 0;
            var firstRepeatedBlock = blocks.Count;
            for (var index = 0; index < blocks.Count; index++)
            {
                var terminator = _request.Program.Instructions[blocks[index].EndIndex - 1];
                if (!IsBranch(terminator.Opcode)) continue;
                if (!TryGetBranchTargetPc(terminator, out var target)) return 0;
                if (IsExitBranchTarget(_request.Program.Instructions, target)) continue;
                if (!TryFindBlock(blocks, target, out var destination)) return 0;
                if (destination <= index) firstRepeatedBlock = Math.Min(firstRepeatedBlock, destination);
            }
            if (firstRepeatedBlock == blocks.Count) return 0;
            for (var index = 0; index < firstRepeatedBlock; index++)
                for (var instruction = blocks[index].StartIndex; instruction < blocks[index].EndIndex; instruction++)
                    if (_request.Program.Instructions[instruction].Opcode == "SBarrier") return 0;
            return firstRepeatedBlock;
        }

        private bool TryEmitMeshDispatchRange(IReadOnlyList<ShaderBlock> blocks, int start, int end, out string error)
        {
            error = string.Empty;
            for (var index = start; index < end; index++)
            {
                var selected = _module.AddInstruction(SpirvOp.IEqual, _boolType,
                    Load(_uintType, _programCounter), UInt((uint)index));
                _meshBlockActive = LogicalAnd(selected, DispatcherIsActive(Load(_boolType, _programActive)));
                BeginMeshBlockSelection();
                if (!TryEmitBlock(blocks, index, out error))
                {
                    error = $"block=0x{blocks[index].StartPc:X}: {error}";
                    return false;
                }
                AdvanceMeshBlockBudget();
                _module.AddStatement(SpirvOp.Branch, _meshBlockMerge);
                _module.AddLabel(_meshBlockMerge);
            }
            return true;
        }

        private void AdvanceMeshBlockBudget()
        {
            if (_maxDispatcherSteps <= 0) return;
            var steps = IAdd(Load(_uintType, _iterationGuard), UInt(1));
            Store(_iterationGuard, steps);
            var withinLimit = _module.AddInstruction(SpirvOp.ULessThan, _boolType,
                steps, UInt((uint)_maxDispatcherSteps));
            Store(_programActive, LogicalAnd(Load(_boolType, _programActive), withinLimit));
        }

        private void BeginMeshBarrierLoop()
        {
            _meshBarrierLoop = (_module.AllocateId(), _module.AllocateId(), _module.AllocateId());
            var body = _module.AllocateId();
            Store(_meshBarrierWaiting, _module.ConstantBool(false));
            _module.AddStatement(SpirvOp.Branch, _meshBarrierLoop.Header);
            _module.AddLabel(_meshBarrierLoop.Header);
            _module.AddStatement(SpirvOp.LoopMerge, _meshBarrierLoop.Merge, _meshBarrierLoop.Continue, 0);
            _module.AddStatement(SpirvOp.Branch, body);
            _module.AddLabel(body);
        }

        private void EndMeshBarrierLoop(int regionStart = 0, int regionEnd = int.MaxValue)
        {
            // Waiting waves resume only after all live waves reach a guest barrier.
            // Every wave participates in the host synchronization, including ended waves.
            EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType,
                Load(_uintType, _localInvocationIndexInput), UInt(0)), () => Store(_meshAnyActive, UInt(0)));
            EmitWave64Barrier();
            var activeCondition = Load(_boolType, _programActive);
            if (_directMeshBlocks)
            {
                // Waves that leave this region wait at its merge, not in the following phase.
                var counter = Load(_uintType, _programCounter);
                var inRegion = LogicalAnd(
                    _module.AddInstruction(SpirvOp.UGreaterThanEqual, _boolType, counter, UInt((uint)regionStart)),
                    _module.AddInstruction(SpirvOp.ULessThan, _boolType, counter, UInt((uint)regionEnd)));
                activeCondition = LogicalAnd(activeCondition, inRegion);
            }
            var runningCondition = DispatcherIsActive(activeCondition);
            var reduceStatus = UsesSubgroupOperations();
            if (reduceStatus)
            {
                activeCondition = _module.AddInstruction(SpirvOp.GroupNonUniformAny, _boolType, UInt(3), activeCondition);
                runningCondition = _module.AddInstruction(SpirvOp.GroupNonUniformAny, _boolType, UInt(3), runningCondition);
            }
            var active = _module.AddInstruction(SpirvOp.Select, _uintType, activeCondition, UInt(1), UInt(0));
            var running = _module.AddInstruction(SpirvOp.Select, _uintType, runningCondition, UInt(2), UInt(0));
            var contribution = BitwiseOr(active, running);
            void PublishStatus() => _module.AddInstruction(SpirvOp.AtomicOr, _uintType,
                _meshAnyActive, UInt(2), UInt(0), contribution);
            if (reduceStatus)
                EmitConditional(_module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3)), PublishStatus);
            else
                PublishStatus();
            EmitWave64Barrier();
            var status = Load(_uintType, _meshAnyActive);
            var anyActive = IsNotZero(status);
            var anyRunning = IsNotZero(BitwiseAnd(status, UInt(2)));
            EmitWave64Barrier();
            Store(_meshBarrierWaiting, LogicalAnd(Load(_boolType, _meshBarrierWaiting), anyRunning));
            _module.AddStatement(SpirvOp.Branch, _meshBarrierLoop.Continue);
            _module.AddLabel(_meshBarrierLoop.Continue);
            _module.AddStatement(SpirvOp.BranchConditional, anyActive, _meshBarrierLoop.Header, _meshBarrierLoop.Merge);
            _module.AddLabel(_meshBarrierLoop.Merge);
        }

        private uint DispatcherIsActive(uint active) => _stage == Gen5SpirvStage.Mesh
            ? LogicalAnd(active, LogicalNot(Load(_boolType, _meshBarrierWaiting))) : active;

        private void InitializeMeshOutputState()
        {
            var halves = _pairedMeshLanes ? 2 : 1;
            for (var half = 0; half < halves; half++)
            {
                if (_pairedMeshLanes) SelectMeshHalf(half);
                InitializeMeshLaneState();
            }
            if (_pairedMeshLanes) SelectMeshHalf(0);
            EmitWave64Barrier();
        }

        private void InitializeMeshLaneState()
        {
            InitializeMeshInputs();
            Store(_meshPositionValue, _module.ConstantNull(_vec4Type));
            Store(_meshPrimitiveValue, UInt(0));
            foreach (var (_, value) in _meshParameters.Values)
            {
                Store(value, _module.ConstantNull(_vec4Type));
            }

            var lane = MeshInvocationIndex();
            EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, lane, UInt(0)), () =>
            {
                Store(MeshArrayElement(_meshAllocation, _uintType, UInt(0), SpirvStorageClass.Workgroup), UInt(0));
                Store(MeshArrayElement(_meshAllocation, _uintType, UInt(1), SpirvStorageClass.Workgroup), UInt(0));
            });
            EmitConditional(_module.AddInstruction(SpirvOp.ULessThan, _boolType,
                lane, UInt(_request.Mesh!.Value.OutputVertexCapacity)), () =>
                Store(MeshArrayElement(_meshLayerValues, _uintType, lane, SpirvStorageClass.Workgroup), UInt(0)));
        }

        private void InitializeMeshInputs()
        {
            var mesh = _request.Mesh!.Value;
            var parameterBase = _request.Bindings.MeshDrawParametersDword;
            var drawCount = LoadShaderDataDword(UInt(parameterBase));
            var firstVertex = LoadShaderDataDword(UInt(parameterBase + 1));
            var firstInstance = LoadShaderDataDword(UInt(parameterBase + 2));
            var lane = MeshInvocationIndex();
            var group = Load(_uvec3Type, _workGroupIdInput);
            var groupX = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, group, 0);
            var groupY = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, group, 1);
            var primitiveStart = _module.AddInstruction(SpirvOp.IMul, _uintType,
                groupX, UInt(mesh.InputPrimitiveCountPerWorkgroup));
            var vertexStart = _module.AddInstruction(SpirvOp.IMul, _uintType,
                primitiveStart, UInt(mesh.InputTriangleStrip ? 1u : 3u));
            var remaining = SaturatingSubtract(drawCount, vertexStart);
            var vertices = UnsignedMinimum(remaining, UInt(mesh.InputVertexCountPerWorkgroup));
            var primitives = mesh.InputTriangleStrip ? SaturatingSubtract(vertices, UInt(2))
                : _module.AddInstruction(SpirvOp.UDiv, _uintType, vertices, UInt(3));
            var wave = _module.AddInstruction(SpirvOp.UDiv, _uintType,
                lane, UInt(_waveLaneCount));
            var waveBase = _module.AddInstruction(SpirvOp.IMul, _uintType,
                wave, UInt(_waveLaneCount));
            var waveVertices = UnsignedMinimum(SaturatingSubtract(vertices, waveBase),
                UInt(_waveLaneCount));
            var wavePrimitives = UnsignedMinimum(SaturatingSubtract(primitives, waveBase),
                UInt(_waveLaneCount));
            var waveCount = (_localSizeX + _waveLaneCount - 1) / _waveLaneCount;
            var waveInfo = BitwiseOr(
                ShiftLeftLogical(wave, UInt(24)),
                ShiftLeftLogical(UInt(waveCount), UInt(28)));
            StoreS(3, BitwiseOr(waveInfo, BitwiseOr(waveVertices,
                ShiftLeftLogical(wavePrimitives, UInt(8)))));

            var first = _module.AddInstruction(SpirvOp.IMul, _uintType, lane,
                UInt(mesh.InputTriangleStrip ? 1u : 3u));
            var second = IAdd(first, UInt(1));
            var third = IAdd(first, UInt(2));
            if (mesh.InputTriangleStrip)
            {
                var odd = IsNotZero(BitwiseAnd(IAdd(primitiveStart, lane), UInt(1)));
                var originalFirst = first;
                first = _module.AddInstruction(SpirvOp.Select, _uintType, odd, second, first);
                second = _module.AddInstruction(SpirvOp.Select, _uintType, odd, originalFirst, second);
            }
            StoreV(0, BitwiseOr(ShiftLeftLogical(first, UInt(2)),
                ShiftLeftLogical(second, UInt(18))), guardWithExec: false);
            StoreV(1, ShiftLeftLogical(third, UInt(2)), guardWithExec: false);
            var vertexOrdinal = IAdd(vertexStart, lane);
            var vertexIndex = DeclarePrivateValue(_uintType);
            Store(vertexIndex, IAdd(firstVertex, vertexOrdinal));
            var indexed = IsNotZero(LoadShaderDataDword(UInt(parameterBase + 3)));
            var inputActive = _module.AddInstruction(SpirvOp.ULessThan, _boolType, lane, vertices);
            EmitConditional(LogicalAnd(indexed, inputActive), () =>
            {
                var address = Pair64(LoadShaderDataDword(UInt(parameterBase + 4)),
                    LoadShaderDataDword(UInt(parameterBase + 5)));
                var offset = _module.AddInstruction(SpirvOp.IMul, _ulongType, Widen(vertexOrdinal), ULong(4));
                Store(vertexIndex, _module.AddInstruction(SpirvOp.Load, _uintType,
                    DeviceWordPointer(IAdd64(address, offset)), 2u, 4u));
            });
            StoreV(5, Load(_uintType, vertexIndex), guardWithExec: false);
            StoreV(8, IAdd(firstInstance, groupY), guardWithExec: false);
        }

        private uint SaturatingSubtract(uint value, uint decrement) =>
            _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, value, decrement),
                UInt(0), _module.AddInstruction(SpirvOp.ISub, _uintType, value, decrement));

        private uint UnsignedMinimum(uint first, uint second) =>
            _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, first, second),
                first, second);

        private bool TryEmitMeshAllocation(Gen5ShaderInstruction instruction, out string error)
        {
            error = string.Empty;
            var message = instruction.Words[0] & 0xFu;
            if (message != 9)
            {
                error = $"The mesh shader uses an unsupported send message: message={message}.";
                return false;
            }

            var allocation = LoadS(M0ScalarRegister);
            var vertices = _module.AddInstruction(SpirvOp.BitFieldUExtract, _uintType,
                allocation, UInt(0), UInt(10));
            var primitives = _module.AddInstruction(SpirvOp.BitFieldUExtract, _uintType,
                allocation, UInt(12), UInt(11));
            EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType,
                MeshInvocationIndex(), UInt(0)), () =>
            {
                Store(MeshArrayElement(_meshAllocation, _uintType, UInt(0), SpirvStorageClass.Workgroup), vertices);
                Store(MeshArrayElement(_meshAllocation, _uintType, UInt(1), SpirvStorageClass.Workgroup), primitives);
            });
            return true;
        }

        private bool TryEmitMeshExport(Gen5ShaderInstruction instruction, Gen5ExportControl export,
            out string error)
        {
            error = string.Empty;
            if (export.Target == 20)
            {
                StoreConditional(_meshPrimitiveValue, LoadV(instruction.Sources[0].Value), _uintType);
                return true;
            }

            if (export.Target is >= 13 and < 16)
            {
                var lane = MeshInvocationIndex();
                for (var component = 0u; component < 4; component++)
                {
                    if ((export.EnableMask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    var output = DecodePositionExportComponent(
                        _request.PositionExportControl, export.Target - 12, component);
                    if (!output.Layer && !output.PointSize && !output.Viewport &&
                        output.ClipDistance == uint.MaxValue && output.CullDistance == uint.MaxValue)
                    {
                        continue;
                    }

                    if (output.PointSize || output.Viewport ||
                        output.ClipDistance != uint.MaxValue || output.CullDistance != uint.MaxValue)
                    {
                        error = $"The mesh shader has an unsupported auxiliary position export: target={export.Target} component={component}.";
                        return false;
                    }

                    var layer = BitwiseAnd(LoadV(instruction.Sources[(int)component].Value), UInt(0x7ff));
                    var valid = _module.AddInstruction(SpirvOp.ULessThan, _boolType,
                        lane, UInt(_request.Mesh!.Value.OutputVertexCapacity));
                    EmitConditional(LogicalAnd(valid, Load(_boolType, _exec)), () =>
                        Store(MeshArrayElement(_meshLayerValues, _uintType, lane,
                            SpirvStorageClass.Workgroup), layer));
                }
                return true;
            }

            uint destination;
            if (export.Target == 12)
            {
                destination = _meshPositionValue;
            }
            else if (export.Target is >= 32 and < 64 &&
                     _meshParameters.TryGetValue(export.Target - 32, out var parameter))
            {
                destination = parameter.Value;
            }
            else
            {
                error = $"The mesh shader has an unsupported export: target={export.Target}.";
                return false;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                components[component] = (export.EnableMask & (1u << component)) != 0
                    ? export.Compressed
                        ? LoadCompressedExportComponent(instruction, component)
                        : Bitcast(_floatType, LoadV(instruction.Sources[component].Value))
                    : Float(component == 3 ? 1f : 0f);
            }
            var value = _module.AddInstruction(SpirvOp.CompositeConstruct, _vec4Type, components);
            if (export.Target == 12 && _request.ClipSpace.Enabled)
            {
                value = ConvertPositionToClipSpace(value);
            }
            StoreConditional(destination, value, _vec4Type);
            return true;
        }

        private void CommitMeshOutputs()
        {
            EmitWave64Barrier();
            var mesh = _request.Mesh!.Value;
            var vertices = Load(_uintType, MeshArrayElement(_meshAllocation, _uintType,
                UInt(0), SpirvStorageClass.Workgroup));
            var primitives = Load(_uintType, MeshArrayElement(_meshAllocation, _uintType,
                UInt(1), SpirvStorageClass.Workgroup));
            vertices = _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType,
                    vertices, UInt(mesh.OutputVertexCapacity)), vertices, UInt(mesh.OutputVertexCapacity));
            primitives = _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType,
                    primitives, UInt(mesh.OutputPrimitiveCapacity)), primitives, UInt(mesh.OutputPrimitiveCapacity));
            _module.AddStatement(SpirvOp.SetMeshOutputsExt, vertices, primitives);

            var halves = _pairedMeshLanes ? 2 : 1;
            for (var half = 0; half < halves; half++)
            {
                if (_pairedMeshLanes) SelectMeshHalf(half);
                CommitMeshLaneOutputs(vertices, primitives);
            }
            if (_pairedMeshLanes) SelectMeshHalf(0);
        }

        private void CommitMeshLaneOutputs(uint vertices, uint primitives)
        {
            var mesh = _request.Mesh!.Value;
            var lane = MeshInvocationIndex();

            EmitConditional(_module.AddInstruction(SpirvOp.ULessThan, _boolType, lane, vertices), () =>
            {
                Store(MeshArrayElement(_meshPositionOutput, _vec4Type, lane, SpirvStorageClass.Output),
                    Load(_vec4Type, _meshPositionValue));
                foreach (var (output, value) in _meshParameters.Values)
                {
                    Store(MeshArrayElement(output, _vec4Type, lane, SpirvStorageClass.Output),
                        Load(_vec4Type, value));
                }
            });

            EmitConditional(_module.AddInstruction(SpirvOp.ULessThan, _boolType, lane, primitives), () =>
            {
                var packed = Load(_uintType, _meshPrimitiveValue);
                uint Vertex(uint bit) => _module.AddInstruction(SpirvOp.BitFieldUExtract,
                    _uintType, packed, UInt(bit), UInt(10));
                var indices = _module.AddInstruction(SpirvOp.CompositeConstruct, _uvec3Type,
                    Vertex(0), Vertex(10), Vertex(20));
                Store(MeshArrayElement(_meshPrimitiveIndicesOutput, _uvec3Type, lane,
                    SpirvStorageClass.Output), indices);
                var isCulled = _module.AddInstruction(SpirvOp.INotEqual, _boolType,
                    BitwiseAnd(packed, UInt(0x80000000)), UInt(0));
                var provoking = _module.AddInstruction(SpirvOp.CompositeExtract,
                    _uintType, indices, mesh.ProvokingVertex);
                var validProvoking = _module.AddInstruction(SpirvOp.ULessThan, _boolType,
                    provoking, vertices);
                var validIndices = validProvoking;
                for (var component = 0u; component < 3; component++)
                {
                    if (component == mesh.ProvokingVertex) continue;
                    var index = _module.AddInstruction(SpirvOp.CompositeExtract,
                        _uintType, indices, component);
                    validIndices = LogicalAnd(validIndices,
                        _module.AddInstruction(SpirvOp.ULessThan, _boolType, index, vertices));
                }
                Store(MeshArrayElement(_meshCullOutput, _boolType, lane,
                    SpirvStorageClass.Output), _module.AddInstruction(SpirvOp.LogicalOr,
                        _boolType, isCulled,
                        _module.AddInstruction(SpirvOp.LogicalNot, _boolType, validIndices)));
                Store(MeshArrayElement(_meshLayerOutput, _uintType, lane,
                    SpirvStorageClass.Output), UInt(0));
                EmitConditional(validProvoking, () => Store(
                    MeshArrayElement(_meshLayerOutput, _uintType, lane, SpirvStorageClass.Output),
                    Load(_uintType, MeshArrayElement(_meshLayerValues, _uintType, provoking,
                        SpirvStorageClass.Workgroup))));
            });
        }

        private uint MeshArrayElement(uint array, uint elementType, uint index,
            SpirvStorageClass storage) =>
            _module.AddInstruction(SpirvOp.AccessChain,
                _module.TypePointer(storage, elementType), array, index);
    }
}
