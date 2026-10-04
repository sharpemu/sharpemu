// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // Reserve one clip-distance plane for vertices whose homogeneous
        // position is exactly (0, 0, 0, 0). Such a position would otherwise
        // reach rasterization and trigger an undefined 0/0 perspective divide
        // on some Vulkan drivers (notably NVIDIA).
        private uint _invalidPositionClipDistance = uint.MaxValue;
        private uint _clipDistanceOutput;
        private uint _clipDistanceElementPointerType;

        private void DeclareZeroPositionClipGuard()
        {
            if (_stage != Gen5SpirvStage.Vertex)
            {
                return;
            }

            // Sharp currently has no guest clip/cull-distance exports. Reserve
            // the first plane for the invalid-position guard and expose it as
            // the Vulkan gl_ClipDistance built-in output.
            _invalidPositionClipDistance = 0;
            var clipDistanceArrayType = _module.TypeArray(_floatType, 1);
            var clipDistancePointerType =
                _module.TypePointer(SpirvStorageClass.Output, clipDistanceArrayType);
            _clipDistanceElementPointerType =
                _module.TypePointer(SpirvStorageClass.Output, _floatType);
            _clipDistanceOutput = _module.AddGlobalVariable(
                clipDistancePointerType,
                SpirvStorageClass.Output);
            _module.AddDecoration(
                _clipDistanceOutput,
                SpirvDecoration.BuiltIn,
                (uint)SpirvBuiltIn.ClipDistance);
            _interfaces.Add(_clipDistanceOutput);
        }

        private uint GetInvalidPositionClipDistancePointer()
        {
            if (_clipDistanceOutput == 0 || _invalidPositionClipDistance == uint.MaxValue)
            {
                throw new InvalidOperationException(
                    "zero-position clip guard was used before its output was declared");
            }

            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _clipDistanceElementPointerType,
                _clipDistanceOutput,
                UInt(_invalidPositionClipDistance));
        }

        private void InitializeZeroPositionClipGuard()
        {
            if (_clipDistanceOutput == 0)
            {
                return;
            }

            Store(GetInvalidPositionClipDistancePointer(), Float(0f));
        }

        private void EmitZeroPositionClipGuard(uint position)
        {
            if (_clipDistanceOutput == 0)
            {
                return;
            }

            var zero = _module.ConstantNull(_vec4Type);
            var equal = _module.AddInstruction(
                SpirvOp.FOrdEqual,
                _module.TypeVector(_boolType, 4),
                position,
                zero);
            var invalid = _module.AddInstruction(
                SpirvOp.All,
                _boolType,
                equal);
            // A negative clip distance removes the invalid vertex from every
            // primitive; zero leaves valid vertices untouched.
            var distance = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                invalid,
                Float(-1f),
                Float(0f));

            // TryEmitExport emits a masked store (the export is conditional on
            // EXEC). Preserve the prior clip value for inactive lanes so a
            // predicated Position export cannot accidentally re-enable or cull
            // a vertex written by an earlier active export.
            var active = Load(_boolType, _exec);
            var pointer = GetInvalidPositionClipDistancePointer();
            distance = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                active,
                distance,
                Load(_floatType, pointer));
            Store(pointer, distance);
        }
    }
}
