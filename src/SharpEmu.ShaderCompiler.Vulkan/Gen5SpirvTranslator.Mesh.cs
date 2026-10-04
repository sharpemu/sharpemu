// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // Mesh Layer is produced per vertex by the fused ES/GS program, then
        // consumed by whichever invocation emits a primitive. Keep that
        // staging in Workgroup storage so the provoking vertex may belong to a
        // different host invocation.
        private uint _meshLayerOutput;
        private uint _meshLayerStaging;
        private uint _meshProbePositions;
        private uint _meshProbePrimitives;

        private void DeclareMeshInterface()
        {
            var mesh = _mesh ?? throw new InvalidOperationException(
                "mesh interface requested without geometry assembly parameters");

            var inputUintPointer =
                _module.TypePointer(SpirvStorageClass.Input, _uintType);
            if (_localInvocationIndexInput == 0)
            {
                _localInvocationIndexInput = _module.AddGlobalVariable(
                    inputUintPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _localInvocationIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.LocalInvocationIndex);
                _interfaces.Add(_localInvocationIndexInput);
            }

            var inputUvec3Pointer =
                _module.TypePointer(SpirvStorageClass.Input, _uvec3Type);
            _workGroupIdInput = _module.AddGlobalVariable(
                inputUvec3Pointer,
                SpirvStorageClass.Input);
            _module.AddDecoration(
                _workGroupIdInput,
                SpirvDecoration.BuiltIn,
                (uint)SpirvBuiltIn.WorkgroupId);
            _interfaces.Add(_workGroupIdInput);

            _meshPositionOutput = DeclareMeshVectorOutput(
                "meshPosition",
                location: null,
                builtIn: SpirvBuiltIn.Position);

            var parameters = _request.Program.Instructions
                .Select(static instruction => instruction.Control)
                .OfType<Gen5ExportControl>()
                .Where(static export => export.Target is >= 32 and < 64)
                .Select(static export => export.Target - 32)
                .Concat(
                    Enumerable
                        .Range(0, Math.Max(_requiredVertexOutputCount, 0))
                        .Select(static location => (uint)location))
                .Distinct()
                .Order()
                .ToArray();
            foreach (var parameter in parameters)
            {
                _meshParameterOutputs.Add(
                    parameter,
                    DeclareMeshVectorOutput(
                        $"meshParam{parameter}",
                        parameter,
                    builtIn: null));
            }

            if (MeshExportsLayer())
            {
                // Layer output from MeshEXT requires Vulkan 1.2's
                // shaderOutputLayer feature. The Vulkan host enables that core
                // feature, so declare the matching SPIR-V 1.5 capability.
                _module.AddCapability(SpirvCapability.ShaderLayer);

                var layerOutputType = _module.TypeArray(
                    _uintType,
                    mesh.MaxPrimitives);
                _meshLayerOutput = _module.AddGlobalVariable(
                    _module.TypePointer(
                        SpirvStorageClass.Output,
                        layerOutputType),
                    SpirvStorageClass.Output);
                _module.AddDecoration(
                    _meshLayerOutput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.Layer);
                _module.AddDecoration(
                    _meshLayerOutput,
                    SpirvDecoration.PerPrimitiveEXT);
                _module.AddName(_meshLayerOutput, "meshLayer");
                _interfaces.Add(_meshLayerOutput);

                var layerStagingType = _module.TypeArray(
                    _uintType,
                    mesh.MaxVertices);
                _meshLayerStaging = _module.AddGlobalVariable(
                    _module.TypePointer(
                        SpirvStorageClass.Workgroup,
                        layerStagingType),
                    SpirvStorageClass.Workgroup);
                _module.AddName(_meshLayerStaging, "meshLayerStaging");
                _interfaces.Add(_meshLayerStaging);
            }

            // The third word retains the unmodified M0 allocation request for
            // the optional GPU-visible mesh probe.
            var allocationType = _module.TypeArray(_uintType, 3);
            _meshAllocation = _module.AddGlobalVariable(
                _module.TypePointer(
                    SpirvStorageClass.Workgroup,
                    allocationType),
                SpirvStorageClass.Workgroup);
            _module.AddName(_meshAllocation, "meshAllocation");
            _interfaces.Add(_meshAllocation);

            var laneCount = _pairWave64 ? 2u : 1u;
            var primitiveDataType = _module.TypeArray(_uintType, laneCount);
            _meshPrimitiveData = _module.AddGlobalVariable(
                _module.TypePointer(
                    SpirvStorageClass.Private,
                    primitiveDataType),
                SpirvStorageClass.Private,
                _module.ConstantNull(primitiveDataType));
            _module.AddName(_meshPrimitiveData, "meshPrimitiveData");
            _interfaces.Add(_meshPrimitiveData);

            var primitiveArrayType =
                _module.TypeArray(_uvec3Type, mesh.MaxPrimitives);
            _meshPrimitiveOutput = _module.AddGlobalVariable(
                _module.TypePointer(
                    SpirvStorageClass.Output,
                    primitiveArrayType),
                SpirvStorageClass.Output);
            _module.AddDecoration(
                _meshPrimitiveOutput,
                SpirvDecoration.BuiltIn,
                (uint)SpirvBuiltIn.PrimitiveTriangleIndicesEXT);
            _module.AddName(_meshPrimitiveOutput, "meshPrimitiveIndices");
            _interfaces.Add(_meshPrimitiveOutput);

            var cullArrayType = _module.TypeArray(_boolType, mesh.MaxPrimitives);
            _meshCullOutput = _module.AddGlobalVariable(
                _module.TypePointer(
                    SpirvStorageClass.Output,
                    cullArrayType),
                SpirvStorageClass.Output);
            _module.AddDecoration(
                _meshCullOutput,
                SpirvDecoration.BuiltIn,
                (uint)SpirvBuiltIn.CullPrimitiveEXT);
            _module.AddDecoration(
                _meshCullOutput,
                SpirvDecoration.PerPrimitiveEXT);
            _module.AddName(_meshCullOutput, "meshCullPrimitive");
            _interfaces.Add(_meshCullOutput);

            _meshIndexScratch = _module.AddGlobalVariable(
                _privateUintPointer,
                SpirvStorageClass.Private,
                UInt(0));
            _module.AddName(_meshIndexScratch, "meshIndexScratch");
            _interfaces.Add(_meshIndexScratch);

            if (_request.TraceMeshOutputs)
            {
                var probePositionType = _module.TypeArray(_vec4Type, 3);
                _meshProbePositions = _module.AddGlobalVariable(
                    _module.TypePointer(
                        SpirvStorageClass.Workgroup,
                        probePositionType),
                    SpirvStorageClass.Workgroup);
                _module.AddName(_meshProbePositions, "meshProbePositions");
                _interfaces.Add(_meshProbePositions);

                var probePrimitiveType = _module.TypeArray(_uintType, 3);
                _meshProbePrimitives = _module.AddGlobalVariable(
                    _module.TypePointer(
                        SpirvStorageClass.Workgroup,
                        probePrimitiveType),
                    SpirvStorageClass.Workgroup);
                _module.AddName(_meshProbePrimitives, "meshProbePrimitives");
                _interfaces.Add(_meshProbePrimitives);
            }
        }

        private bool MeshExportsLayer()
        {
            foreach (var export in _request.Program.Instructions
                         .Select(static instruction => instruction.Control)
                         .OfType<Gen5ExportControl>()
                         .Where(static export => export.Target is >= 13 and < 16))
            {
                var positionIndex = export.Target - 12;
                for (var component = 0u; component < 4; component++)
                {
                    if ((export.EnableMask & (1u << (int)component)) != 0 &&
                        DecodePositionExportComponent(
                            _request.PositionExportControl,
                            positionIndex,
                            component).Layer)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private SpirvMeshOutput DeclareMeshVectorOutput(
            string name,
            uint? location,
            SpirvBuiltIn? builtIn)
        {
            var mesh = _mesh!;
            var outputArrayType =
                _module.TypeArray(_vec4Type, mesh.MaxVertices);
            var output = _module.AddGlobalVariable(
                _module.TypePointer(
                    SpirvStorageClass.Output,
                    outputArrayType),
                SpirvStorageClass.Output);
            if (location is { } outputLocation)
            {
                _module.AddDecoration(
                    output,
                    SpirvDecoration.Location,
                    outputLocation);
            }
            else if (builtIn is { } outputBuiltIn)
            {
                _module.AddDecoration(
                    output,
                    SpirvDecoration.BuiltIn,
                    (uint)outputBuiltIn);
            }

            _module.AddName(output, name);
            _interfaces.Add(output);

            var laneCount = _pairWave64 ? 2u : 1u;
            var stagingArrayType = _module.TypeArray(_vec4Type, laneCount);
            var staging = _module.AddGlobalVariable(
                _module.TypePointer(
                    SpirvStorageClass.Private,
                    stagingArrayType),
                SpirvStorageClass.Private,
                _module.ConstantNull(stagingArrayType));
            _module.AddName(staging, $"{name}Staging");
            _interfaces.Add(staging);
            return new SpirvMeshOutput(output, staging);
        }

        private void EmitMeshInitialState()
        {
            var workGroupId = Load(_uvec3Type, _workGroupIdInput);
            if (_pairWave64)
            {
                SelectLaneHalf(0);
                EmitMeshLaneInitialState(workGroupId);
                SelectLaneHalf(1);
                EmitMeshLaneInitialState(workGroupId);
                SelectLaneHalf(0);
            }
            else
            {
                EmitMeshLaneInitialState(workGroupId);
            }
        }

        private void EmitMeshLaneInitialState(uint workGroupId)
        {
            var mesh = _mesh!;
            var local = MeshLocalInvocationIndex();
            var groupX = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                workGroupId,
                0);
            var groupY = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                workGroupId,
                1);

            var inputCount = LoadMeshDrawParameter(0);
            var primitiveChunk = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                groupX,
                UInt(mesh.PrimitivesPerGroup));
            var chunk = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                primitiveChunk,
                UInt(mesh.InputPrimitiveStep));
            var remainingVertices = SaturatingSubtract(inputCount, chunk);
            var vertices = Ext(
                38,
                _uintType,
                remainingVertices,
                UInt(mesh.VerticesPerGroup));
            var hasPrimitive = _module.AddInstruction(
                SpirvOp.UGreaterThanEqual,
                _boolType,
                vertices,
                UInt(mesh.InputPrimitiveSize));
            var primitiveCount = IAdd(
                _module.AddInstruction(
                    SpirvOp.UDiv,
                    _uintType,
                    SaturatingSubtract(
                        vertices,
                        UInt(mesh.InputPrimitiveSize)),
                    UInt(mesh.InputPrimitiveStep)),
                UInt(1));
            primitiveCount = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                hasPrimitive,
                primitiveCount,
                UInt(0));

            var wave = ShiftRightLogical(local, UInt(6));
            var waveBase = BitwiseAnd(local, UInt(unchecked((uint)~63)));
            var vertexCount = Ext(
                38,
                _uintType,
                SaturatingSubtract(vertices, waveBase),
                UInt(64));
            var wavePrimitiveCount = Ext(
                38,
                _uintType,
                SaturatingSubtract(primitiveCount, waveBase),
                UInt(64));
            var totalThreads = checked(_localSizeX * _localSizeY * _localSizeZ);
            var waveInfo = BitwiseOr(
                ShiftLeftLogical(wave, UInt(24)),
                UInt(checked(((totalThreads + 63) / 64) << 28)));
            StoreS(
                3,
                BitwiseOr(
                    waveInfo,
                    BitwiseOr(
                        ShiftLeftLogical(wavePrimitiveCount, UInt(8)),
                        vertexCount)));

            var parity = mesh.InputPrimitive == 6
                ? BitwiseAnd(IAdd(primitiveChunk, local), UInt(1))
                : UInt(0);
            var vertex = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                local,
                UInt(mesh.InputPrimitiveStep));
            var first = IAdd(vertex, parity);
            var second = mesh.InputPrimitiveSize >= 2
                ? _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    IAdd(vertex, UInt(1)),
                    parity)
                : UInt(0);
            var third = mesh.InputPrimitiveSize == 3
                ? IAdd(vertex, UInt(2))
                : UInt(0);
            StoreV(
                0,
                BitwiseOr(
                    ShiftLeftLogical(first, UInt(2)),
                    ShiftLeftLogical(second, UInt(18))),
                guardWithExec: false);
            StoreV(
                1,
                ShiftLeftLogical(third, UInt(2)),
                guardWithExec: false);

            var inputVertex = IAdd(chunk, local);
            var indexBytes = LoadMeshDrawParameter(3);
            var indexed = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                indexBytes,
                UInt(0));
            var validVertex = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                local,
                vertices);
            var loadIndex = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                indexed,
                validVertex);
            var addressLow = LoadMeshDrawParameter(4);
            var byteOffset = IAdd(
                BitwiseAnd(addressLow, UInt(3)),
                _module.AddInstruction(
                    SpirvOp.IMul,
                    _uintType,
                    inputVertex,
                    indexBytes));
            var indexAddress = Pair64(
                BitwiseAnd(addressLow, UInt(unchecked((uint)~3))),
                LoadMeshDrawParameter(5));
            var alignedAddress = IAdd64(
                indexAddress,
                Widen(BitwiseAnd(byteOffset, UInt(unchecked((uint)~3)))));
            Store(_meshIndexScratch, UInt(0));
            EmitConditional(loadIndex, () =>
                Store(_meshIndexScratch, LoadDeviceDword(alignedAddress)));
            var packedIndex = Load(_uintType, _meshIndexScratch);
            var indexWidth = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                Ext(38, _uintType, indexBytes, UInt(4)),
                UInt(8));
            var index = _module.AddInstruction(
                SpirvOp.BitFieldUExtract,
                _uintType,
                packedIndex,
                _module.AddInstruction(
                    SpirvOp.IMul,
                    _uintType,
                    BitwiseAnd(byteOffset, UInt(3)),
                    UInt(8)),
                indexWidth);
            var vertexIndex = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                indexed,
                index,
                inputVertex);
            StoreV(
                5,
                IAdd(LoadMeshDrawParameter(1), vertexIndex),
                guardWithExec: false);
            StoreV(
                8,
                IAdd(LoadMeshDrawParameter(2), groupY),
                guardWithExec: false);
        }

        private uint LoadMeshDrawParameter(uint index)
        {
            if (index >= ShaderMeshInfo.DrawDwordCount ||
                _pushData == 0 ||
                _pushConstantUintPointer == 0)
            {
                throw new InvalidOperationException(
                    $"invalid mesh draw parameter {index}");
            }

            var pointer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _pushConstantUintPointer,
                _pushData,
                UInt(0),
                UInt(index));
            return Load(_uintType, pointer);
        }

        private uint SaturatingSubtract(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                left,
                Ext(38, _uintType, left, right));

        private uint MeshLocalInvocationIndex() => _pairWave64
            ? GuestLocalInvocationIndex()
            : Load(_uintType, _localInvocationIndexInput);

        private bool TryEmitSendMessage(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5SpirvStage.Mesh)
            {
                return true;
            }

            if (instruction.Words.Count == 0)
            {
                error = "missing S_SENDMSG immediate";
                return false;
            }

            var message = instruction.Words[0] & 0xFFFFu;
            if (message != 9)
            {
                error = $"unsupported mesh S_SENDMSG immediate {message}; expected MSG_GS_ALLOC_REQ (9)";
                return false;
            }

            var firstInvocation = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                MeshLocalInvocationIndex(),
                UInt(0));
            EmitConditional(firstInvocation, () =>
            {
                var allocation = LoadS(M0ScalarRegister);
                var vertices = _module.AddInstruction(
                    SpirvOp.BitFieldUExtract,
                    _uintType,
                    allocation,
                    UInt(0),
                    UInt(10));
                var primitives = _module.AddInstruction(
                    SpirvOp.BitFieldUExtract,
                    _uintType,
                    allocation,
                    UInt(12),
                    UInt(11));
                Store(MeshAllocationPointer(2), allocation);
                Store(
                    MeshAllocationPointer(0),
                    Ext(38, _uintType, vertices, UInt(_mesh!.MaxVertices)));
                Store(
                    MeshAllocationPointer(1),
                    Ext(38, _uintType, primitives, UInt(_mesh.MaxPrimitives)));
            });
            return true;
        }

        private bool TryEmitMeshExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            if (export.Target == 0x14)
            {
                var primitivePointer = MeshPrivateElementPointer(
                    _meshPrimitiveData,
                    _uintType,
                    checked((uint)_laneHalf));
                var value = LoadV(instruction.Sources[0].Value);
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    Load(_boolType, _exec),
                    value,
                    Load(_uintType, primitivePointer));
                Store(primitivePointer, value);
                return true;
            }

            if (export.Target is >= 13 and < 16)
            {
                EmitMeshAuxPositionExport(instruction, export);
                return true;
            }

            SpirvMeshOutput output;
            if (export.Target == 12)
            {
                output = _meshPositionOutput;
            }
            else if (export.Target is >= 32 and < 64 &&
                     _meshParameterOutputs.TryGetValue(
                         export.Target - 32,
                         out var parameter))
            {
                output = parameter;
            }
            else
            {
                return true;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                components[component] =
                    (export.EnableMask & (1u << component)) != 0
                        ? export.Compressed
                            ? LoadCompressedExportComponent(
                                instruction,
                                component)
                            : Bitcast(
                                _floatType,
                                LoadV(instruction.Sources[component].Value))
                        : Float(component == 3 ? 1f : 0f);
            }

            var pointer = MeshPrivateElementPointer(
                output.StagingVariable,
                _vec4Type,
                checked((uint)_laneHalf));
            var outputValue = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec4Type,
                components);
            if (export.Target == 12 && _request.ClipSpace.Enabled)
            {
                outputValue = ConvertPositionToClipSpace(outputValue);
            }
            outputValue = _module.AddInstruction(
                SpirvOp.Select,
                _vec4Type,
                Load(_boolType, _exec),
                outputValue,
                Load(_vec4Type, pointer));
            Store(pointer, outputValue);
            return true;
        }

        private void EmitMeshAuxPositionExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            if (_meshLayerStaging == 0)
            {
                return;
            }

            var positionIndex = export.Target - 12;
            for (var component = 0u; component < 4; component++)
            {
                if ((export.EnableMask & (1u << (int)component)) == 0 ||
                    !DecodePositionExportComponent(
                        _request.PositionExportControl,
                        positionIndex,
                        component).Layer)
                {
                    continue;
                }

                var raw = export.Compressed
                    ? Bitcast(
                        _uintType,
                        LoadCompressedExportComponent(
                            instruction,
                            checked((int)component)))
                    : LoadV(instruction.Sources[checked((int)component)].Value);
                var pointer = MeshWorkgroupElementPointer(
                    _meshLayerStaging,
                    _uintType,
                    MeshLocalInvocationIndex());
                EmitExecConditional(() =>
                    Store(pointer, BitwiseAnd(raw, UInt(0x7ff))));
            }
        }

        private void EmitMeshEpilogue()
        {
            var workgroup = UInt(2);
            _module.AddStatement(
                SpirvOp.ControlBarrier,
                workgroup,
                workgroup,
                UInt(0x108));

            var vertices = Load(
                _uintType,
                MeshAllocationPointer(0));
            var primitives = Load(
                _uintType,
                MeshAllocationPointer(1));
            _module.AddStatement(
                SpirvOp.SetMeshOutputsEXT,
                vertices,
                primitives);

            if (_request.TraceMeshOutputs)
            {
                var firstInvocation = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    Load(_uintType, _localInvocationIndexInput),
                    UInt(0));
                EmitConditional(firstInvocation, () =>
                {
                    var zeroPosition = _module.ConstantNull(_vec4Type);
                    for (var sample = 0u; sample < 3; sample++)
                    {
                        Store(
                            MeshWorkgroupElementPointer(
                                _meshProbePositions,
                                _vec4Type,
                                UInt(sample)),
                            zeroPosition);
                        Store(
                            MeshWorkgroupElementPointer(
                                _meshProbePrimitives,
                                _uintType,
                                UInt(sample)),
                            UInt(0));
                    }
                });
                _module.AddStatement(
                    SpirvOp.ControlBarrier,
                    workgroup,
                    workgroup,
                    UInt(0x108));
            }

            var laneCount = _pairWave64 ? 2 : 1;
            for (var half = 0; half < laneCount; half++)
            {
                SelectLaneHalf(half);
                var index = MeshLocalInvocationIndex();
                var isVertex = _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    index,
                    vertices);
                EmitConditional(isVertex, () =>
                {
                    CopyMeshVectorOutput(
                        _meshPositionOutput,
                        checked((uint)half),
                        index);
                    if (_request.TraceMeshOutputs)
                    {
                        var isProbeVertex = _module.AddInstruction(
                            SpirvOp.ULessThan,
                            _boolType,
                            index,
                            UInt(3));
                        EmitConditional(isProbeVertex, () =>
                            Store(
                                MeshWorkgroupElementPointer(
                                    _meshProbePositions,
                                    _vec4Type,
                                    index),
                                Load(
                                    _vec4Type,
                                    MeshPrivateElementPointer(
                                        _meshPositionOutput.StagingVariable,
                                        _vec4Type,
                                        checked((uint)half)))));
                    }
                    foreach (var output in _meshParameterOutputs.Values)
                    {
                        CopyMeshVectorOutput(
                            output,
                            checked((uint)half),
                            index);
                    }
                });

                var isPrimitive = _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    index,
                    primitives);
                EmitConditional(isPrimitive, () =>
                {
                    var packed = Load(
                        _uintType,
                        MeshPrivateElementPointer(
                            _meshPrimitiveData,
                            _uintType,
                            checked((uint)half)));
                    if (_request.TraceMeshOutputs)
                    {
                        void StagePrimitive(uint sample, uint condition) =>
                            EmitConditional(condition, () =>
                                Store(
                                    MeshWorkgroupElementPointer(
                                        _meshProbePrimitives,
                                        _uintType,
                                        UInt(sample)),
                                    packed));

                        StagePrimitive(
                            0,
                            _module.AddInstruction(
                                SpirvOp.IEqual,
                                _boolType,
                                index,
                                UInt(0)));
                        StagePrimitive(
                            1,
                            _module.AddInstruction(
                                SpirvOp.IEqual,
                                _boolType,
                                index,
                                UInt(1)));
                        StagePrimitive(
                            2,
                            _module.AddInstruction(
                                SpirvOp.IEqual,
                                _boolType,
                                index,
                                _module.AddInstruction(
                                    SpirvOp.ISub,
                                    _uintType,
                                    primitives,
                                    UInt(1))));
                    }
                    var indices = new uint[3];
                    for (var component = 0; component < indices.Length; component++)
                    {
                        indices[component] = _module.AddInstruction(
                            SpirvOp.BitFieldUExtract,
                            _uintType,
                            packed,
                            UInt(checked((uint)component * 10)),
                            UInt(10));
                    }

                    var triangle = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _uvec3Type,
                        indices);
                    Store(
                        MeshOutputElementPointer(
                            _meshPrimitiveOutput,
                            _uvec3Type,
                            index),
                        triangle);
                    var culled = _module.AddInstruction(
                        SpirvOp.INotEqual,
                        _boolType,
                        BitwiseAnd(packed, UInt(0x8000_0000)),
                        UInt(0));
                    Store(
                        MeshOutputElementPointer(
                            _meshCullOutput,
                            _boolType,
                            index),
                        culled);
                    if (_meshLayerOutput != 0)
                    {
                        var layer = Load(
                            _uintType,
                            MeshWorkgroupElementPointer(
                                _meshLayerStaging,
                                _uintType,
                                indices[_mesh!.ProvokingVertex]));
                        Store(
                            MeshOutputElementPointer(
                                _meshLayerOutput,
                                _uintType,
                                index),
                            layer);
                    }
                });
            }

            SelectLaneHalf(0);
            if (_request.TraceMeshOutputs)
            {
                _module.AddStatement(
                    SpirvOp.ControlBarrier,
                    workgroup,
                    workgroup,
                    UInt(0x108));
                EmitMeshOutputProbe(vertices, primitives);
            }
        }

        private void EmitMeshOutputProbe(uint vertices, uint primitives)
        {
            if (!_request.TraceMeshOutputs)
            {
                return;
            }

            const uint recordWords = 48;
            const uint deviceFaultRecordWords = 8;
            const uint magic = 0x4853454D; // "MESH" in little-endian memory.

            var workGroupId = Load(_uvec3Type, _workGroupIdInput);
            var groupX = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, workGroupId, 0);
            var groupY = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, workGroupId, 1);
            var groupZ = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, workGroupId, 2);
            var firstInvocation = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                Load(_uintType, _localInvocationIndexInput),
                UInt(0));
            var firstGroup = LogicalAnd(
                _module.AddInstruction(SpirvOp.IEqual, _boolType, groupX, UInt(0)),
                LogicalAnd(
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, groupY, UInt(0)),
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, groupZ, UInt(0))));
            var length = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _faultBuffer, 0);
            var hasRecord = _module.AddInstruction(
                SpirvOp.UGreaterThanEqual,
                _boolType,
                length,
                UInt(recordWords + deviceFaultRecordWords));

            EmitConditional(LogicalAnd(firstInvocation, LogicalAnd(firstGroup, hasRecord)), () =>
            {
                var recordStart = _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    length,
                    UInt(recordWords + deviceFaultRecordWords));
                var claim = _module.AddInstruction(
                    SpirvOp.AtomicCompareExchange,
                    _uintType,
                    BlockWordPointer(_faultBuffer, recordStart),
                    UInt(1),
                    UInt(0),
                    UInt(0),
                    UInt(magic),
                    UInt(0));
                EmitConditional(
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, claim, UInt(0)),
                    () =>
                    {
                        void Write(uint field, uint value) =>
                            Store(
                                BlockWordPointer(
                                    _faultBuffer,
                                    IAdd(recordStart, UInt(field))),
                                value);

                        var packed = Load(
                            _uintType,
                            MeshWorkgroupElementPointer(
                                _meshProbePrimitives,
                                _uintType,
                                UInt(0)));
                        var indices = new uint[3];
                        for (var component = 0; component < indices.Length; component++)
                        {
                            indices[component] = _module.AddInstruction(
                                SpirvOp.BitFieldUExtract,
                                _uintType,
                                packed,
                                UInt(checked((uint)component * 10)),
                                UInt(10));
                        }

                        var position = Load(
                            _vec4Type,
                            MeshWorkgroupElementPointer(
                                _meshProbePositions,
                                _vec4Type,
                                UInt(0)));
                        var positionBits = new uint[4];
                        for (var component = 0; component < positionBits.Length; component++)
                        {
                            positionBits[component] = Bitcast(
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.CompositeExtract,
                                    _floatType,
                                    position,
                                    checked((uint)component)));
                        }

                        Write(1, UInt((uint)_request.Hash));
                        Write(2, UInt((uint)(_request.Hash >> 32)));
                        Write(3, groupX);
                        Write(4, groupY);
                        Write(5, groupZ);
                        Write(6, Load(_uintType, MeshAllocationPointer(2)));
                        Write(7, vertices);
                        Write(8, primitives);
                        Write(9, packed);
                        Write(10, indices[0]);
                        Write(11, indices[1]);
                        Write(12, indices[2]);
                        Write(
                            13,
                            _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.INotEqual,
                                    _boolType,
                                    BitwiseAnd(packed, UInt(0x8000_0000)),
                                    UInt(0)),
                                UInt(1),
                                UInt(0)));
                        Write(14, UInt(uint.MaxValue));
                        if (_meshLayerStaging != 0)
                        {
                            var hasPrimitive = _module.AddInstruction(
                                SpirvOp.INotEqual,
                                _boolType,
                                primitives,
                                UInt(0));
                            EmitConditional(hasPrimitive, () =>
                            {
                                var layerIndex = Ext(
                                    38,
                                    _uintType,
                                    indices[_mesh!.ProvokingVertex],
                                    UInt(_mesh.MaxVertices - 1));
                                Write(
                                    14,
                                    Load(
                                        _uintType,
                                        MeshWorkgroupElementPointer(
                                            _meshLayerStaging,
                                            _uintType,
                                            layerIndex)));
                            });
                        }

                        for (var component = 0; component < positionBits.Length; component++)
                        {
                            Write(checked((uint)(15 + component)), positionBits[component]);
                        }

                        for (var parameter = 0u; parameter < ShaderMeshInfo.DrawDwordCount; parameter++)
                        {
                            Write(20 + parameter, LoadMeshDrawParameter(parameter));
                        }

                        Write(26, UInt(_mesh!.MaxVertices));
                        Write(27, UInt(_mesh.MaxPrimitives));
                        Write(28, UInt(_mesh.OutputPrimitive));
                        Write(29, UInt(_mesh.ProvokingVertex));

                        for (var sample = 1u; sample <= 2; sample++)
                        {
                            var sampledPosition = Load(
                                _vec4Type,
                                MeshWorkgroupElementPointer(
                                    _meshProbePositions,
                                    _vec4Type,
                                    UInt(sample)));
                            for (var component = 0u; component < 4; component++)
                            {
                                Write(
                                    26 + sample * 4 + component,
                                    Bitcast(
                                        _uintType,
                                        _module.AddInstruction(
                                            SpirvOp.CompositeExtract,
                                            _floatType,
                                            sampledPosition,
                                            component)));
                            }
                        }

                        void WritePrimitiveSample(uint sample, uint field)
                        {
                            var sampledPacked = Load(
                                _uintType,
                                MeshWorkgroupElementPointer(
                                    _meshProbePrimitives,
                                    _uintType,
                                    UInt(sample)));
                            Write(field, sampledPacked);
                            for (var component = 0u; component < 3; component++)
                            {
                                Write(
                                    field + 1 + component,
                                    _module.AddInstruction(
                                        SpirvOp.BitFieldUExtract,
                                        _uintType,
                                        sampledPacked,
                                        UInt(component * 10),
                                        UInt(10)));
                            }
                            Write(
                                field + 4,
                                _module.AddInstruction(
                                    SpirvOp.Select,
                                    _uintType,
                                    _module.AddInstruction(
                                        SpirvOp.INotEqual,
                                        _boolType,
                                        BitwiseAnd(sampledPacked, UInt(0x8000_0000)),
                                        UInt(0)),
                                    UInt(1),
                                    UInt(0)));
                        }

                        WritePrimitiveSample(1, 38);
                        WritePrimitiveSample(2, 43);
                    });
            });
        }

        private void CopyMeshVectorOutput(
            SpirvMeshOutput output,
            uint half,
            uint index)
        {
            var value = Load(
                _vec4Type,
                MeshPrivateElementPointer(
                    output.StagingVariable,
                    _vec4Type,
                    half));
            Store(
                MeshOutputElementPointer(
                    output.Variable,
                    _vec4Type,
                    index),
                value);
        }

        private uint MeshAllocationPointer(uint field) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _module.TypePointer(
                    SpirvStorageClass.Workgroup,
                    _uintType),
                _meshAllocation,
                UInt(field));

        private uint MeshPrivateElementPointer(
            uint variable,
            uint type,
            uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _module.TypePointer(
                    SpirvStorageClass.Private,
                    type),
                variable,
                UInt(index));

        private uint MeshWorkgroupElementPointer(
            uint variable,
            uint type,
            uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _module.TypePointer(
                    SpirvStorageClass.Workgroup,
                    type),
                variable,
                index);

        private uint MeshOutputElementPointer(
            uint variable,
            uint type,
            uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _module.TypePointer(
                    SpirvStorageClass.Output,
                    type),
                variable,
                index);
    }
}
