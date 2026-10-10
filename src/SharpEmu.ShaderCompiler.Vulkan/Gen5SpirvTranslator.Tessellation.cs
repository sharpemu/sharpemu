// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private uint _tessellationCoordinateInput;
        private uint _tessellationPatchIdInput;
        private uint _tessellationVertexIndexScratch;

        private uint TessellationWord(uint index) => LoadShaderDataDword(UInt(_request.Bindings.TessellationDataDword + index));
        private uint TessellationPointer(uint index) => Pair64(TessellationWord(index), TessellationWord(index + 1));

        // Patches of one instance as a divisor; zero (a single instance) divides by all ones.
        private uint PatchesPerInstanceDivisor()
        {
            var patches = TessellationWord(Gen5TessellationData.PatchesPerInstance);
            return _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, patches, UInt(0)), UInt(uint.MaxValue), patches);
        }

        private void EmitHullInputState()
        {
            var info = _request.TessellationHull!.Value;
            var local = Load(_uintType, _localInvocationIndexInput);
            var waveBase = BitwiseAnd(local, UInt(~63u));
            // Workgroup k runs hull group k of the dispatch: its patches, its off-chip slot
            // (s2) and its run of the factor ring (s4).
            var group = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, Load(_uvec3Type, _workGroupIdInput), 0);
            var groupFirst = _module.AddInstruction(SpirvOp.IMul, _uintType, group, UInt(info.PatchesPerGroup));
            var firstPatch = IAdd(TessellationWord(Gen5TessellationData.FirstPatch), groupFirst);
            var remainingPatches = _module.AddInstruction(SpirvOp.ISub, _uintType,
                TessellationWord(Gen5TessellationData.PatchCount), groupFirst);
            var patchCount = _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, remainingPatches, UInt(info.PatchesPerGroup)),
                remainingPatches, UInt(info.PatchesPerGroup));
            uint WaveCount(uint controlPoints)
            {
                var threads = _module.AddInstruction(SpirvOp.IMul, _uintType, patchCount, UInt(controlPoints));
                var remaining = _module.AddInstruction(SpirvOp.ISub, _uintType, threads, waveBase);
                remaining = _module.AddInstruction(SpirvOp.Select, _uintType,
                    _module.AddInstruction(SpirvOp.ULessThan, _boolType, waveBase, threads), remaining, UInt(0));
                return _module.AddInstruction(SpirvOp.Select, _uintType,
                    _module.AddInstruction(SpirvOp.ULessThan, _boolType, remaining, UInt(64)), remaining, UInt(64));
            }
            StoreS(2, IAdd(TessellationWord(Gen5TessellationData.OffchipOffset), _module.AddInstruction(SpirvOp.IMul, _uintType,
                group, TessellationWord(Gen5TessellationData.OffchipSlotBytes))));
            StoreS(3, BitwiseOr(WaveCount(info.InputControlPoints), ShiftLeftLogical(WaveCount(info.OutputControlPoints), UInt(8))));
            StoreS(4, IAdd(TessellationWord(Gen5TessellationData.FactorOffset), _module.AddInstruction(SpirvOp.IMul, _uintType,
                group, TessellationWord(Gen5TessellationData.FactorGroupBytes))));
            var relativePatch = _module.AddInstruction(SpirvOp.UDiv, _uintType, local, UInt(info.OutputControlPoints));
            var controlPoint = _module.AddInstruction(SpirvOp.UMod, _uintType, local, UInt(info.OutputControlPoints));
            // Patch numbers run across the instances of the draw; the guest sees the patch
            // within its instance and the instance of each input control point.
            var perInstance = PatchesPerInstanceDivisor();
            StoreV(0, _module.AddInstruction(SpirvOp.UMod, _uintType, IAdd(relativePatch, firstPatch), perInstance), false);
            StoreV(1, BitwiseOr(relativePatch, ShiftLeftLogical(controlPoint, UInt(8))), false);
            var inputPatch = IAdd(firstPatch, _module.AddInstruction(SpirvOp.UDiv, _uintType, local, UInt(info.InputControlPoints)));
            var inputIndex = IAdd(
                _module.AddInstruction(SpirvOp.IMul, _uintType,
                    _module.AddInstruction(SpirvOp.UMod, _uintType, inputPatch, perInstance), UInt(info.InputControlPoints)),
                _module.AddInstruction(SpirvOp.UMod, _uintType, local, UInt(info.InputControlPoints)));
            var indexSize = TessellationWord(Gen5TessellationData.IndexSize);
            var vertex = _tessellationVertexIndexScratch;
            Store(vertex, inputIndex);
            var inputThreads = _module.AddInstruction(SpirvOp.IMul, _uintType, patchCount, UInt(info.InputControlPoints));
            var fetch = LogicalAnd(_module.AddInstruction(SpirvOp.INotEqual, _boolType, indexSize, UInt(0)),
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, local, inputThreads));
            EmitConditional(fetch, () =>
            {
                var byteOffset = IAdd(_module.AddInstruction(SpirvOp.IMul, _uintType, inputIndex, indexSize),
                    TessellationWord(Gen5TessellationData.IndexByteOffset));
                var address = IAdd64(TessellationPointer(Gen5TessellationData.IndexAddress), Widen(BitwiseAnd(byteOffset, UInt(~3u))));
                var word = _module.AddInstruction(SpirvOp.Load, _uintType, DeviceWordPointer(address), 2, 4);
                var shift = ShiftLeftLogical(BitwiseAnd(byteOffset, UInt(3)), UInt(3));
                var bits = ShiftLeftLogical(indexSize, UInt(3));
                var value = _module.AddInstruction(SpirvOp.BitFieldUExtract, _uintType, word, shift, bits);
                Store(vertex, value);
            });
            StoreV(2, IAdd(Load(_uintType, vertex), TessellationWord(Gen5TessellationData.VertexOffset)), false);
            StoreV(3, local, false);
            StoreV(5, IAdd(TessellationWord(Gen5TessellationData.InstanceId),
                _module.AddInstruction(SpirvOp.UDiv, _uintType, inputPatch, perInstance)), false);
        }

        private void DeclareTessellationInputs()
        {
            _module.AddCapability(SpirvCapability.Tessellation);
            _tessellationCoordinateInput = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Input, _vec3Type), SpirvStorageClass.Input);
            _module.AddDecoration(_tessellationCoordinateInput, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.TessCoord);
            _interfaces.Add(_tessellationCoordinateInput);
            _tessellationPatchIdInput = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Input, _uintType), SpirvStorageClass.Input);
            _module.AddDecoration(_tessellationPatchIdInput, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.PrimitiveId);
            _interfaces.Add(_tessellationPatchIdInput);
        }

        private void EmitTessellationInputState()
        {
            // In the merged ES/GS ABI, five GS inputs precede the domain
            // shader's U, V and relative patch ID. Coordinate values remain
            // float bit patterns in the guest VGPR file.
            var coordinates = Load(_vec3Type, _tessellationCoordinateInput);
            StoreV(5, Bitcast(_uintType, _module.AddInstruction(SpirvOp.CompositeExtract,
                _floatType, coordinates, 0)), guardWithExec: false);
            StoreV(6, Bitcast(_uintType, _module.AddInstruction(SpirvOp.CompositeExtract,
                _floatType, coordinates, 1)), guardWithExec: false);
            // v7 is the patch within its hull group and v8 the patch ID. A native draw covers
            // whole hull groups in order, so the native primitive ID gives the group (and its
            // off-chip slot in s4) and the patch within it.
            var drawPatch = Load(_uintType, _tessellationPatchIdInput);
            if (!_request.Bindings.UsesTessellationData)
            {
                StoreV(7, drawPatch, guardWithExec: false);
                StoreV(8, drawPatch, guardWithExec: false);
                return;
            }

            var groupPatches = TessellationWord(Gen5TessellationData.GroupPatches);
            var divisor = _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, groupPatches, UInt(0)), UInt(uint.MaxValue), groupPatches);
            var group = _module.AddInstruction(SpirvOp.UDiv, _uintType, drawPatch, divisor);
            StoreV(7, _module.AddInstruction(SpirvOp.UMod, _uintType, drawPatch, divisor), guardWithExec: false);
            StoreV(8, _module.AddInstruction(SpirvOp.UMod, _uintType,
                IAdd(drawPatch, TessellationWord(Gen5TessellationData.FirstPatch)), PatchesPerInstanceDivisor()), guardWithExec: false);
            StoreS(4, IAdd(TessellationWord(Gen5TessellationData.OffchipOffset), _module.AddInstruction(SpirvOp.IMul, _uintType,
                group, TessellationWord(Gen5TessellationData.OffchipSlotBytes))));
        }

        private void EmitTessellationExecutionModes(uint entryPoint)
        {
            var info = _request.Tessellation!.Value;
            _module.AddExecutionMode(entryPoint, info.Domain switch
            {
                Gen5TessellationDomain.Triangles => SpirvExecutionMode.Triangles,
                Gen5TessellationDomain.Quads => SpirvExecutionMode.Quads,
                _ => SpirvExecutionMode.Isolines,
            });
            _module.AddExecutionMode(entryPoint, info.Spacing switch
            {
                Gen5TessellationSpacing.FractionalOdd => SpirvExecutionMode.SpacingFractionalOdd,
                Gen5TessellationSpacing.FractionalEven => SpirvExecutionMode.SpacingFractionalEven,
                _ => SpirvExecutionMode.SpacingEqual,
            });
            _module.AddExecutionMode(entryPoint, info.Clockwise
                ? SpirvExecutionMode.VertexOrderCw : SpirvExecutionMode.VertexOrderCcw);
            if (info.PointMode) _module.AddExecutionMode(entryPoint, SpirvExecutionMode.PointMode);
        }
    }
}
