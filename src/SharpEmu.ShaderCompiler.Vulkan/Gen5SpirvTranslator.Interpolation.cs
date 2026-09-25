// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private readonly HashSet<uint> _perVertexAttributes = [];
        private readonly HashSet<uint> _flatParameterAttributes = [];
        private readonly Dictionary<int, uint> _barycentricInputs = [];
        private uint _interpolationSampleId;
        private const uint InterpolateAtCentroid = 76;
        private const uint InterpolateAtSample = 77;
        private const uint InterpolateAtOffset = 78;

        private void DeclareInterpolationParameters()
        {
            foreach (var instruction in _request.Program.Instructions)
            {
                if (instruction.Opcode == "VInterpMovF32" &&
                    instruction.Control is Gen5InterpolationControl interpolation)
                {
                    _perVertexAttributes.Add(interpolation.Attribute);
                }
            }

            if (!_request.SupportsPerVertexPixelInputs)
            {
                // Reading P0 alone is the provoking vertex value, which a flat input gives.
                // Other parameters still need the per-vertex values.
                foreach (var attribute in _perVertexAttributes.ToArray())
                {
                    if (_request.Program.Instructions.All(instruction =>
                            instruction.Control is not Gen5InterpolationControl control ||
                            control.Attribute != attribute ||
                            (instruction.Opcode == "VInterpMovF32" && (instruction.Words[0] & 0xFFu) == 2)))
                    {
                        _perVertexAttributes.Remove(attribute);
                        _flatParameterAttributes.Add(attribute);
                    }
                }
            }

            if (_perVertexAttributes.Count == 0)
            {
                return;
            }

            _module.AddCapability(SpirvCapability.FragmentBarycentricKhr);
            _module.AddExtension("SPV_KHR_fragment_shader_barycentric");
            _module.AddCapability(SpirvCapability.InterpolationFunction);
            var enabledInputs = _pixelInputAddress & _pixelInputEnable;
            if ((enabledInputs & (1u << 3)) != 0)
            {
                throw new NotSupportedException("Pull-model interpolation parameters are not supported.");
            }

            var variables = new Dictionary<bool, uint>();
            foreach (var bit in new[] { 0, 1, 2, 4, 5, 6 })
            {
                if ((enabledInputs & (1u << bit)) == 0)
                {
                    continue;
                }

                if (!variables.TryGetValue(bit < 4, out var variable))
                {
                    variable = _module.AddGlobalVariable(
                        _module.TypePointer(SpirvStorageClass.Input, _vec3Type), SpirvStorageClass.Input);
                    _module.AddDecoration(variable, SpirvDecoration.BuiltIn,
                        (uint)(bit < 4 ? SpirvBuiltIn.BaryCoordKhr : SpirvBuiltIn.BaryCoordNoPerspKhr));
                    variables.Add(bit < 4, variable);
                    _interfaces.Add(variable);
                }
                _barycentricInputs.Add(bit, variable);
            }

            if ((enabledInputs & 0x11u) != 0)
            {
                _module.AddCapability(SpirvCapability.SampleRateShading);
                _interpolationSampleId = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Input, _intType), SpirvStorageClass.Input);
                _module.AddDecoration(_interpolationSampleId, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SampleId);
                _module.AddDecoration(_interpolationSampleId, SpirvDecoration.Flat);
                _interfaces.Add(_interpolationSampleId);
            }
        }

        private uint LoadBarycentricCoordinates(int bit, uint variable) => bit switch
        {
            0 or 4 => _module.AddInstruction(SpirvOp.ExtInst, _vec3Type, _glsl, InterpolateAtSample,
                variable, Load(_intType, _interpolationSampleId)),
            2 or 6 => _module.AddInstruction(SpirvOp.ExtInst, _vec3Type, _glsl, InterpolateAtCentroid, variable),
            _ => _module.AddInstruction(SpirvOp.ExtInst, _vec3Type, _glsl, InterpolateAtOffset,
                variable, _module.ConstantNull(_vec2Type)),
        };

        private bool TryEmitInterpolationParameter(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            uint input,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var custom = interpolation.Attribute < 32 &&
                (_request.PixelCustomInterpolationMask & (1u << (int)interpolation.Attribute)) != 0;

            uint LoadVertex(uint vertex)
            {
                var pointer = _module.AddInstruction(SpirvOp.AccessChain,
                    _module.TypePointer(SpirvStorageClass.Input, _floatType),
                    input, UInt(vertex), UInt(interpolation.Channel));
                return Load(_floatType, pointer);
            }

            uint LoadParameter(uint mode)
            {
                var value = LoadVertex((mode + 1) % 3);
                return !custom && mode < 2
                    ? _module.AddInstruction(SpirvOp.FSub, _floatType, value, LoadVertex(0))
                    : value;
            }

            uint result;
            if (instruction.Opcode == "VInterpMovF32")
            {
                var mode = instruction.Words[0] & 0xFFu;
                if (mode >= 3)
                {
                    error = "reserved interpolation parameter selector";
                    return false;
                }
                result = LoadParameter(mode);
            }
            else if (instruction.Opcode is "VInterpP1F32" or "VInterpP2F32")
            {
                var firstPhase = instruction.Opcode == "VInterpP1F32";
                var source = Bitcast(_floatType, GetRawSource(instruction, 0));
                var product = _module.AddInstruction(SpirvOp.FMul, _floatType,
                    LoadParameter(firstPhase ? 0u : 1u), source);
                result = _module.AddInstruction(SpirvOp.FAdd, _floatType, product,
                    firstPhase ? LoadParameter(2) : Bitcast(_floatType, LoadV(destination)));
            }
            else
            {
                error = $"unsupported interpolation opcode {instruction.Opcode}";
                return false;
            }

            StoreV(destination, Bitcast(_uintType, result));
            return true;
        }
    }
}
