// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint ImageDescriptorDwords = 8;
        private const uint SamplerDescriptorDwords = 4;

        // ---- image resources ----

        /// <summary>
        /// Classifies every image binding (storage vs sampled, component kind
        /// from the descriptor's unified format) and seeds the PC lookup,
        /// mirroring DeclareImages on the SPIR-V side. MSL needs no format on
        /// the texture type — only the component type and access.
        /// </summary>
        private void DeclareImageKinds()
        {
            for (var index = 0; index < _evaluation.ImageBindings.Count; index++)
            {
                var binding = _evaluation.ImageBindings[index];
                _imageBindingByPc.TryAdd(binding.Pc, index);
                var isStorage = Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
                _imageKinds.Add((isStorage, DecodeImageComponentKind(binding.ResourceDescriptor)));
            }
        }

        /// <summary>"float", "int", or "uint" from the descriptor's unified format.</summary>
        private static string DecodeImageComponentKind(IReadOnlyList<uint> descriptor)
        {
            if (descriptor.Count < 2)
            {
                return "float";
            }

            var unifiedFormat = (descriptor[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(unifiedFormat, out _, out var numberType))
            {
                return "float";
            }

            return numberType switch
            {
                4 => "uint",
                5 => "int",
                _ => "float",
            };
        }

        private void EmitImageArguments(StringBuilder source)
        {
            for (var index = 0; index < _imageKinds.Count; index++)
            {
                var (isStorage, kind) = _imageKinds[index];
                var slot = _imageBindingBase + index;
                source.AppendLine(isStorage
                    ? $"    texture2d<{kind}, access::read_write> tex{index} [[texture({slot})]],"
                    : $"    texture2d<{kind}> tex{index} [[texture({slot})]],");
                if (!isStorage)
                {
                    source.AppendLine($"    sampler smp{index} [[sampler({slot})]],");
                }
            }
        }

        private bool TryResolveDominatingImageBinding(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl control,
            out int bindingIndex)
        {
            if (_imageBindingByPc.TryGetValue(instruction.Pc, out bindingIndex) &&
                bindingIndex < _imageKinds.Count)
            {
                return true;
            }

            var storage = Gen5ShaderTranslator.IsStorageImageOperation(instruction.Opcode);
            for (var index = 0; index < _evaluation.ImageBindings.Count; index++)
            {
                var candidate = _evaluation.ImageBindings[index];
                if (candidate.Control.ScalarResource != control.ScalarResource ||
                    candidate.Control.ScalarSampler != control.ScalarSampler ||
                    Gen5ShaderTranslator.IsStorageImageOperation(candidate.Opcode) != storage ||
                    !HasSameScalarDefinitions(
                        candidate.Pc,
                        instruction.Pc,
                        control.ScalarResource,
                        ImageDescriptorDwords) ||
                    (UsesSampler(instruction.Opcode) &&
                     !HasSameScalarDefinitions(
                         candidate.Pc,
                         instruction.Pc,
                         control.ScalarSampler,
                         SamplerDescriptorDwords)))
                {
                    continue;
                }

                bindingIndex = index;
                _imageBindingByPc.Add(instruction.Pc, index);
                return true;
            }

            bindingIndex = -1;
            return false;
        }

        private bool HasSameScalarDefinitions(
            uint candidatePc,
            uint targetPc,
            uint firstRegister,
            uint registerCount)
        {
            if (firstRegister + registerCount > ScalarRegisterFileCount ||
                !_scalarDefinitionsBeforePc.TryGetValue(candidatePc, out var candidate) ||
                !_scalarDefinitionsBeforePc.TryGetValue(targetPc, out var target))
            {
                return false;
            }

            for (var register = firstRegister;
                 register < firstRegister + registerCount;
                 register++)
            {
                var definition = candidate[register];
                if (definition is ConflictingScalarDefinition or UnreachableScalarDefinition ||
                    target[register] != definition)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool UsesSampler(string opcode) =>
            opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
            opcode.StartsWith("ImageGather", StringComparison.Ordinal);

        // ---- image instruction emission ----

        private bool TryEmitImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string error)
        {
            error = string.Empty;
            if (!TryResolveDominatingImageBinding(instruction, image, out var bindingIndex))
            {
                error = $"unresolved image binding t=s{image.ScalarResource} s=s{image.ScalarSampler}";
                return false;
            }

            var (isStorage, kind) = _imageKinds[bindingIndex];
            var texture = $"tex{bindingIndex}";

            if (instruction.Opcode == "ImageGetResinfo")
            {
                var width = Temp("uint", isStorage
                    ? $"{texture}.get_width()"
                    : $"{texture}.get_width(0)");
                var height = Temp("uint", isStorage
                    ? $"{texture}.get_height()"
                    : $"{texture}.get_height(0)");
                uint outputIndex = 0;
                for (var component = 0; component < 4; component++)
                {
                    if ((image.Dmask & (1u << component)) == 0)
                    {
                        continue;
                    }

                    StoreVector(
                        image.VectorData + outputIndex++,
                        component switch
                        {
                            0 => width,
                            1 => height,
                            _ => "1u",
                        });
                }

                return true;
            }

            if (instruction.Opcode is "ImageStore" or "ImageStoreMip")
            {
                if (!isStorage)
                {
                    error = "image store is not bound as storage";
                    return false;
                }

                var x = Temp("int", $"as_type<int>({ImageIntegerAddress(image, 0)})");
                var y = Temp("int", $"as_type<int>({ImageIntegerAddress(image, 1)})");
                var components = new string[4];
                uint sourceIndex = 0;
                for (var component = 0; component < 4; component++)
                {
                    components[component] = (image.Dmask & (1u << component)) != 0
                        ? ImageTexelComponent(kind, ImageStoreComponent(image, kind, sourceIndex++))
                        : kind == "float" ? "0.0f" : "0";
                }

                // Bounds-checked, EXEC-guarded write.
                Line($"if (exec && {x} >= 0 && {y} >= 0 && {x} < (int){texture}.get_width() && {y} < (int){texture}.get_height())");
                Line("{");
                _indent++;
                Line($"{texture}.write({VectorLiteral(kind)}({components[0]}, {components[1]}, {components[2]}, {components[3]}), uint2((uint){x}, (uint){y}));");
                _indent--;
                Line("}");
                return true;
            }

            if (isStorage && instruction.Opcode is not ("ImageLoad" or "ImageLoadMip"))
            {
                error = $"unsupported storage image opcode {instruction.Opcode}";
                return false;
            }

            string sampled;
            var writeAllComponents = false;
            if (instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                var mip = _evaluation.ImageBindings[bindingIndex].MipLevel ?? 0;
                var widthQuery = isStorage ? $"{texture}.get_width()" : $"{texture}.get_width({mip}u)";
                var heightQuery = isStorage ? $"{texture}.get_height()" : $"{texture}.get_height({mip}u)";
                var x = Temp(
                    "uint",
                    $"(uint)clamp(as_type<int>({ImageIntegerAddress(image, 0)}), 0, (int){widthQuery} - 1)");
                var y = Temp(
                    "uint",
                    $"(uint)clamp(as_type<int>({ImageIntegerAddress(image, 1)}), 0, (int){heightQuery} - 1)");
                sampled = Temp(
                    $"vec<{kind}, 4>",
                    isStorage
                        ? $"{texture}.read(uint2({x}, {y}))"
                        : $"{texture}.read(uint2({x}, {y}), {mip}u)");
            }
            else if (instruction.Opcode.StartsWith("ImageSample", StringComparison.Ordinal))
            {
                if (!TryEmitImageSample(instruction, image, bindingIndex, kind, out sampled, out error))
                {
                    return false;
                }
            }
            else if (instruction.Opcode.StartsWith("ImageGather4", StringComparison.Ordinal))
            {
                if (!TryEmitImageGather(instruction, image, bindingIndex, kind, out sampled, out error))
                {
                    return false;
                }

                writeAllComponents = true;
            }
            else
            {
                error = $"unsupported image opcode {instruction.Opcode}";
                return false;
            }

            var outputValues = new List<string>(4);
            for (var component = 0; component < 4; component++)
            {
                if (!writeAllComponents && (image.Dmask & (1u << component)) == 0)
                {
                    continue;
                }

                var value = $"{sampled}[{component}]";
                outputValues.Add(kind == "uint" ? value : AsUInt(value));
            }

            if (image.D16)
            {
                for (var index = 0; index < outputValues.Count; index += 2)
                {
                    var low = outputValues[index];
                    var high = index + 1 < outputValues.Count ? outputValues[index + 1] : "0u";
                    StoreVector(
                        image.VectorData + (uint)(index / 2),
                        PackImageD16(kind, low, high));
                }
            }
            else
            {
                for (var index = 0; index < outputValues.Count; index++)
                {
                    StoreVector(image.VectorData + (uint)index, outputValues[index]);
                }
            }

            return true;
        }

        private bool TryEmitImageSample(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            int bindingIndex,
            string kind,
            out string sampled,
            out string error)
        {
            sampled = string.Empty;
            error = string.Empty;
            var opcode = instruction.Opcode;
            var texture = $"tex{bindingIndex}";
            var samplerName = $"smp{bindingIndex}";
            var hasOffset = opcode.EndsWith("O", StringComparison.Ordinal);
            var hasCompare = opcode.Contains("SampleC", StringComparison.Ordinal);
            var hasGradients = opcode.Contains("SampleD", StringComparison.Ordinal);
            var hasZeroLod = opcode.Contains("Lz", StringComparison.Ordinal);
            var hasLod = !hasZeroLod && opcode.Contains("SampleL", StringComparison.Ordinal);
            var hasBias = opcode.Contains("SampleB", StringComparison.Ordinal);

            // RDNA MIMG address operands are ordered
            // {offset}{bias}{z-compare}{derivatives}{body}; SAMPLE_L carries LOD
            // as the final body component instead.
            var addressCursor = 0;
            var offsetX = "0";
            var offsetY = "0";
            if (hasOffset)
            {
                addressCursor = AlignFullImageAddress(image, addressCursor);
                var packed = Temp(
                    "int",
                    $"as_type<int>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");
                offsetX = Temp("int", $"extract_bits({packed}, 0u, 6u)");
                offsetY = Temp("int", $"extract_bits({packed}, 8u, 6u)");
                addressCursor += ImageFullAddressSlots(image);
            }

            var bias = hasBias ? Temp("float", ImageFloatAddress(image, addressCursor++)) : "0.0f";
            var reference = "0.0f";
            if (hasCompare)
            {
                addressCursor = AlignFullImageAddress(image, addressCursor);
                reference = Temp(
                    "float",
                    $"as_type<float>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");
                addressCursor += ImageFullAddressSlots(image);
            }

            var gradientX = "float2(0.0f)";
            var gradientY = "float2(0.0f)";
            if (hasGradients)
            {
                gradientX = Temp(
                    "float2",
                    $"float2({ImageFloatAddress(image, addressCursor)}, {ImageFloatAddress(image, addressCursor + 1)})");
                gradientY = Temp(
                    "float2",
                    $"float2({ImageFloatAddress(image, addressCursor + 2)}, {ImageFloatAddress(image, addressCursor + 3)})");
                addressCursor += 4;
            }

            var coordinates = Temp(
                "float2",
                $"float2({ImageFloatAddress(image, addressCursor)}, {ImageFloatAddress(image, addressCursor + 1)})");
            var lod = hasZeroLod
                ? "0.0f"
                : hasLod
                    ? Temp("float", ImageFloatAddress(image, addressCursor + 2))
                    : bias;
            if (hasOffset)
            {
                // Per-lane texel offsets fold into normalized coordinates using
                // the selected mip extent, mirroring the SPIR-V translator
                // (Metal sample offsets must be compile-time constants).
                var explicitLod = hasGradients || hasZeroLod || hasLod;
                var offsetLod = explicitLod && !hasGradients ? lod : "0.0f";
                var mipLevel = Temp("uint", $"(uint)max((int)({offsetLod}), 0)");
                coordinates = Temp(
                    "float2",
                    $"{coordinates} + float2((float){offsetX} / (float){texture}.get_width({mipLevel}), " +
                    $"(float){offsetY} / (float){texture}.get_height({mipLevel}))");
            }

            var samplerArguments = hasGradients
                ? $", gradient2d({gradientX}, {gradientY})"
                : hasZeroLod || hasLod
                    ? $", level({lod})"
                    : hasBias
                        ? $", bias({bias})"
                        : string.Empty;
            sampled = Temp(
                $"vec<{kind}, 4>",
                $"{texture}.sample({samplerName}, {coordinates}{samplerArguments})");
            if (hasCompare)
            {
                // Manual PCF: reference passes when <= texel, broadcast (r,r,r,1).
                var passes = Temp("bool", $"{reference} <= (float){sampled}[0]");
                var one = kind == "float" ? "1.0f" : "1";
                var zero = kind == "float" ? "0.0f" : "0";
                sampled = Temp(
                    $"vec<{kind}, 4>",
                    $"{VectorLiteral(kind)}({passes} ? {one} : {zero}, {passes} ? {one} : {zero}, {passes} ? {one} : {zero}, {one})");
            }

            return true;
        }

        private bool TryEmitImageGather(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            int bindingIndex,
            string kind,
            out string sampled,
            out string error)
        {
            sampled = string.Empty;
            error = string.Empty;
            var opcode = instruction.Opcode;
            var texture = $"tex{bindingIndex}";
            var samplerName = $"smp{bindingIndex}";
            var hasOffset = opcode.EndsWith("O", StringComparison.Ordinal);
            var hasCompare = opcode.Contains("Gather4C", StringComparison.Ordinal);
            var addressCursor = 0;
            var offset = "int2(0)";
            if (hasOffset)
            {
                var packed = Temp(
                    "int",
                    $"as_type<int>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");
                offset = Temp(
                    "int2",
                    $"int2(extract_bits({packed}, 0u, 6u), extract_bits({packed}, 8u, 6u))");
                addressCursor += ImageFullAddressSlots(image);
            }

            var reference = "0.0f";
            if (hasCompare)
            {
                addressCursor = AlignFullImageAddress(image, addressCursor);
                reference = Temp(
                    "float",
                    $"as_type<float>(v[{image.GetAddressRegister(ImageAddressRegister(image, addressCursor))}])");
                addressCursor += ImageFullAddressSlots(image);
            }

            var coordinates = Temp(
                "float2",
                $"float2({ImageFloatAddress(image, addressCursor)}, {ImageFloatAddress(image, addressCursor + 1)})");

            // The gathered component is selected from the first dmask bit.
            uint component = 0;
            while (component < 3 && (image.Dmask & (1u << (int)component)) == 0)
            {
                component++;
            }

            var componentName = hasCompare ? "x" : component switch
            {
                0 => "x",
                1 => "y",
                2 => "z",
                _ => "w",
            };
            sampled = Temp(
                $"vec<{kind}, 4>",
                $"{texture}.gather({samplerName}, {coordinates}, {offset}, component::{componentName})");
            if (hasCompare)
            {
                var one = kind == "float" ? "1.0f" : "1";
                var zero = kind == "float" ? "0.0f" : "0";
                var compared = Temp(
                    $"vec<{kind}, 4>",
                    $"{VectorLiteral(kind)}(" +
                    $"{reference} <= (float){sampled}[0] ? {one} : {zero}, " +
                    $"{reference} <= (float){sampled}[1] ? {one} : {zero}, " +
                    $"{reference} <= (float){sampled}[2] ? {one} : {zero}, " +
                    $"{reference} <= (float){sampled}[3] ? {one} : {zero})");
                sampled = compared;
            }

            return true;
        }

        private static string VectorLiteral(string kind) => $"vec<{kind}, 4>";

        private static int ImageAddressRegister(Gen5ImageControl image, int component) =>
            image.A16 ? component / 2 : component;

        private static int ImageFullAddressSlots(Gen5ImageControl image) =>
            image.A16 ? 2 : 1;

        private static int AlignFullImageAddress(Gen5ImageControl image, int component) =>
            image.A16 ? (component + 1) & ~1 : component;

        /// <summary>Float address component, unpacking A16 half pairs.</summary>
        private string ImageFloatAddress(Gen5ImageControl image, int component)
        {
            var register = image.GetAddressRegister(ImageAddressRegister(image, component));
            return image.A16
                ? $"(float)as_type<half2>(v[{register}])[{component & 1}]"
                : $"as_type<float>(v[{register}])";
        }

        /// <summary>Integer address component, unpacking A16 16-bit pairs.</summary>
        private string ImageIntegerAddress(Gen5ImageControl image, int component)
        {
            var register = image.GetAddressRegister(ImageAddressRegister(image, component));
            return image.A16
                ? $"((v[{register}] >> {(component & 1) * 16}) & 0xFFFFu)"
                : $"v[{register}]";
        }

        /// <summary>One store-source component, unpacking D16 halves.</summary>
        private string ImageStoreComponent(Gen5ImageControl image, string kind, uint component)
        {
            if (!image.D16)
            {
                return $"v[{image.VectorData + component}]";
            }

            var packed = $"v[{image.VectorData + (component / 2)}]";
            if (kind == "float")
            {
                return AsUInt($"(float)as_type<half2>({packed})[{component & 1}]");
            }

            var low = $"(({packed} >> {(component & 1) * 16}) & 0xFFFFu)";
            return kind == "int"
                ? $"(uint)extract_bits(as_type<int>({low}), 0u, 16u)"
                : low;
        }

        private static string ImageTexelComponent(string kind, string raw) => kind switch
        {
            "int" => $"as_type<int>({raw})",
            "uint" => raw,
            _ => $"as_type<float>({raw})",
        };

        private string PackImageD16(string kind, string low, string high)
        {
            if (kind == "float")
            {
                return $"(((uint)as_type<ushort>(half(as_type<float>({low})))) | (((uint)as_type<ushort>(half(as_type<float>({high})))) << 16))";
            }

            return $"((({low}) & 0xFFFFu) | ((({high}) & 0xFFFFu) << 16))";
        }

        // ---- exports ----

        private bool TryEmitExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 4)
            {
                error = "missing export sources";
                return false;
            }

            if (_stage != Gen5MslStage.Pixel)
            {
                // Vertex export targets arrive with the vertex stage; compute
                // programs have no export interface.
                return true;
            }

            Gen5PixelOutputBinding? binding = null;
            foreach (var candidate in _pixelOutputBindings)
            {
                if (candidate.GuestSlot == export.Target)
                {
                    binding = candidate;
                    break;
                }
            }

            if (binding is null)
            {
                return true;
            }

            var field = $"sharpemu_out.mrt{binding.Value.GuestSlot}";
            var componentType = binding.Value.Kind switch
            {
                Gen5PixelOutputKind.Uint => "uint",
                Gen5PixelOutputKind.Sint => "int",
                _ => "float",
            };
            var values = new string[4];
            for (var component = 0; component < 4; component++)
            {
                if ((export.EnableMask & (1u << component)) == 0)
                {
                    values[component] = $"{field}[{component}]";
                    continue;
                }

                if (export.Compressed)
                {
                    var packed = $"v[{instruction.Sources[component >> 1].Value}]";
                    var half = $"(float)as_type<half2>({packed})[{component & 1}]";
                    values[component] = binding.Value.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => $"(uint)({half})",
                        Gen5PixelOutputKind.Sint => $"(int)({half})",
                        _ => half,
                    };
                    continue;
                }

                var raw = $"v[{instruction.Sources[component].Value}]";
                values[component] = binding.Value.Kind switch
                {
                    Gen5PixelOutputKind.Uint => raw,
                    Gen5PixelOutputKind.Sint => $"as_type<int>({raw})",
                    _ => $"as_type<float>({raw})",
                };
            }

            // A lane removed from EXEC keeps the previous output value; killed
            // fragments are discarded in the epilogue.
            Line($"{field} = exec ? vec<{componentType}, 4>({values[0]}, {values[1]}, {values[2]}, {values[3]}) : {field};");
            return true;
        }

        // ---- interpolation / pixel inputs ----

        private bool TryEmitInterpolation(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5MslStage.Pixel ||
                !_pixelAttributes.Contains(interpolation.Attribute) ||
                instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister)
            {
                error = "invalid interpolated attribute";
                return false;
            }

            StoreVector(
                instruction.Destinations[0].Value,
                AsUInt($"sharpemu_in.attr{interpolation.Attribute}[{interpolation.Channel}]"));
            return true;
        }

        /// <summary>
        /// Seeds pixel input VGPRs in SPI_PS_INPUT_ADDR compact order: the
        /// interpolation slots reserve registers even though V_INTERP reads MSL
        /// varyings directly, and the position inputs land in the
        /// hardware-selected VGPRs from the fragment coordinate.
        /// </summary>
        private void EmitPixelInputState(StringBuilder source)
        {
            uint vgpr = 0;

            void Advance(int bit, uint dwordCount)
            {
                if ((_pixelInputAddress & (1u << bit)) != 0)
                {
                    vgpr += dwordCount;
                }
            }

            void Position(int bit, string component)
            {
                var mask = 1u << bit;
                if ((_pixelInputAddress & mask) == 0)
                {
                    return;
                }

                if ((_pixelInputEnable & mask) != 0)
                {
                    source.AppendLine(
                        $"    v[{vgpr}] = as_type<uint>(sharpemu_in.sharpemu_frag_coord.{component});");
                }

                vgpr++;
            }

            Advance(0, 2);  // PERSP_SAMPLE
            Advance(1, 2);  // PERSP_CENTER
            Advance(2, 2);  // PERSP_CENTROID
            Advance(3, 3);  // PERSP_PULL_MODEL
            Advance(4, 2);  // LINEAR_SAMPLE
            Advance(5, 2);  // LINEAR_CENTER
            Advance(6, 2);  // LINEAR_CENTROID
            Advance(7, 1);  // LINE_STIPPLE
            Position(8, "x");
            Position(9, "y");
            Position(10, "z");
            Position(11, "w");
        }
    }
}
