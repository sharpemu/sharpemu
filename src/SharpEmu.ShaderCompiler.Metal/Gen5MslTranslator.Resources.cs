// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal;

// The resource path over a compile request: every binding comes from the layout through one
// argument buffer, every descriptor index from the memory table, user data from push data.
public static partial class Gen5MslTranslator
{
    // The range table holds at most 64 merged entries; seven halvings cover them.
    public const uint AddressRangeSearchSteps = 7;
    public const uint MaxAddressRangeCount = 64;

    public static bool TryCompileProgram(ShaderCompileRequest request, out Gen5MslShader shader, out string error)
    {
        shader = default!;
        try
        {
            BindingLayoutValidator.Validate(
                request.Bindings,
                request.Resources.Info,
                BindingLayout.CollectUserDataRegisters(request.Program, request.UserDataBase, request.UserDataCount),
                request.UsesGlobalDataShare,
                request.UsesFlattenedTable,
                request.ReadsShaderBase,
                request.Hash,
                request.Stage);
        }
        catch (ResourcePlanException exception)
        {
            error = exception.Message;
            return false;
        }

        if (request.Stage == ShaderStage.Pixel && !ValidatePixelOutputs(request.PixelOutputs, out error))
        {
            return false;
        }

        return new CompilationContext(request).TryCompile(out shader, out error);
    }

    private sealed partial class CompilationContext
    {
        private const string ResourcesName = "sharpemu_resources";
        private const string PushDataName = "sharpemu_push_data";
        private const string GlobalDataShareName = "sharpemu_gds";
        private const string GlobalDataShareWordsName = "sharpemu_gds_words";
        private const string DeviceArguments = "sharpemu_ranges, sharpemu_range_count, sharpemu_faults, sharpemu_fault_words";

        private readonly ShaderCompileRequest _request;
        private readonly Gen5MslArgumentLayout? _argumentLayout;
        private readonly Dictionary<DescriptorBindingKind, MslImageClass> _imageClasses = [];
        private readonly Dictionary<int, string> _indirectKeyScratch = [];
        private bool _hasGlobalDataShare;
        private bool _hasDeviceAddressTable;
        private bool _hasFlattenedTable;
        private bool _hasSamplers;

        private readonly record struct MslImageClass(
            string Field,
            string TextureType,
            string ComponentKind,
            bool IsStorage,
            bool Multisampled,
            ImageDimension Dimension,
            IReadOnlyList<uint> Resources);

        public CompilationContext(ShaderCompileRequest request)
        {
            _request = request;
            _argumentLayout = Gen5MslArgumentLayout.Build(request.Bindings);
            _stage = request.Stage switch
            {
                ShaderStage.Vertex => Gen5MslStage.Vertex,
                ShaderStage.Pixel => Gen5MslStage.Pixel,
                _ => Gen5MslStage.Compute,
            };
            _pixelOutputBindings = request.PixelOutputs;
            _usesPixelValidMask =
                _stage == Gen5MslStage.Pixel &&
                request.Program.Instructions.Any(static instruction => instruction.Control is Gen5ExportControl { ValidMask: true });
            _localSizeX = Math.Max(request.LocalSizeX, 1);
            _localSizeY = Math.Max(request.LocalSizeY, 1);
            _localSizeZ = Math.Max(request.LocalSizeZ, 1);
            _waveLaneCount = request.WaveSize == 64 ? 64u : 32u;
            _requiredVertexOutputCount = request.RequiredVertexOutputCount;
            _pixelInputEnable = request.PixelInputEnable;
            _pixelInputAddress = request.PixelInputAddress;
            _pixelInputCntl = new uint[32];
            for (uint index = 0; index < 32u; index++)
            {
                _pixelInputCntl[index] = request.PixelInputCntl is not null && index < (uint)request.PixelInputCntl.Count
                    ? request.PixelInputCntl[(int)index]
                    : index;
            }
        }

        // ---- declarations ----

        // Records which bindings the layout holds and the texture type of every image class.
        private void DeclareLayoutBindings()
        {
            var request = _request;
            var info = request.Resources.Info;
            foreach (var binding in request.Bindings.Descriptors)
            {
                switch (binding.Kind)
                {
                    case DescriptorBindingKind.Buffers:
                        break;
                    case DescriptorBindingKind.Samplers:
                        _hasSamplers = true;
                        break;
                    case DescriptorBindingKind.GlobalDataShare:
                        _hasGlobalDataShare = true;
                        break;
                    case DescriptorBindingKind.DeviceAddressPageTable:
                        _hasDeviceAddressTable = true;
                        break;
                    case DescriptorBindingKind.FaultBuffer:
                        break;
                    case DescriptorBindingKind.FlattenedResourceTable:
                        _hasFlattenedTable = true;
                        break;
                    case DescriptorBindingKind.ShaderData:
                        break;
                    default:
                        DeclareImageClass(binding, info);
                        break;
                }
            }

            _hasDeviceAddressTable &= request.Bindings.Find(DescriptorBindingKind.FaultBuffer) is not null;
            foreach (var memoryIndex in request.IndirectKeyMemoryIndices)
            {
                _indirectKeyScratch[memoryIndex] = $"indirect_image_key{memoryIndex}";
            }
        }

        // A storage class takes the least access its elements need; a sampled class none.
        private void DeclareImageClass(DescriptorBinding binding, ShaderResourceInfo info)
        {
            var (resourceClass, numericClass, dimension, atomic) = ImageDescriptorBinding.Describe(binding.Kind);
            if (resourceClass == ImageResourceClass.None)
            {
                throw new InvalidOperationException($"binding kind {binding.Kind} is not an image class");
            }

            var isStorage = resourceClass == ImageResourceClass.Storage;
            var componentKind = numericClass switch
            {
                ImageNumericClass.Uint => "uint",
                ImageNumericClass.Sint => "int",
                _ => "float",
            };
            string? access = null;
            if (isStorage)
            {
                var reads = false;
                var writes = false;
                foreach (var resource in binding.Resources)
                {
                    var image = info.Images[(int)resource];
                    reads |= image.Read || image.Atomic || atomic;
                    writes |= image.Written || image.Atomic || atomic;
                }

                access = writes ? (reads ? "read_write" : "write") : "read";
            }

            var multisampled = dimension is ImageDimension.Dim2DMsaa or ImageDimension.Dim2DMsaaArray;
            _imageClasses[binding.Kind] = new MslImageClass(
                Gen5MslArgumentLayout.ImageClassName(binding.Kind),
                Gen5MslArgumentLayout.TextureType(dimension, componentKind, access),
                componentKind,
                isStorage,
                multisampled,
                dimension,
                binding.Resources);
        }

        private void EmitResourcePrelude(StringBuilder source) =>
            source.Append(MslTemplates.Render(
                "resource_prelude",
                ("address_mask", FormatULong(DeviceAddressPaging.AddressMask)),
                ("page_bits", DeviceAddressPaging.PageBits.ToString(CultureInfo.InvariantCulture)),
                ("page_offset_mask", FormatULong(DeviceAddressPaging.PageOffsetMask)),
                ("search_steps", AddressRangeSearchSteps.ToString(CultureInfo.InvariantCulture))));

        // The argument buffer struct: one field per layout descriptor in layout order.
        private void EmitResourceStruct(StringBuilder source)
        {
            var layout = _argumentLayout!;
            if (layout.Fields.Count == 0)
            {
                return;
            }

            source.AppendLine();
            source.AppendLine($"struct {Gen5MslArgumentLayout.StructName}");
            source.AppendLine("{");
            foreach (var field in layout.Fields)
            {
                var declaration = field.FieldKind switch
                {
                    MslArgumentFieldKind.BufferPointers => $"array<device uint*, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.BufferByteCounts => $"array<uint, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.Textures => $"array<{_imageClasses[field.Kind].TextureType}, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.Samplers => $"array<sampler, {field.Count}> {field.Name}",
                    MslArgumentFieldKind.Pointer => $"{PointerType(field.Kind)} {field.Name}",
                    _ => $"uint {field.Name}",
                };
                source.AppendLine($"    {declaration} [[id({field.FirstId})]];");
            }

            source.AppendLine("};");
            source.AppendLine();
        }

        private static string PointerType(DescriptorBindingKind kind) => kind switch
        {
            DescriptorBindingKind.DeviceAddressPageTable => "device const Gen5AddressRange*",
            DescriptorBindingKind.FlattenedResourceTable or DescriptorBindingKind.ShaderData => "device const uint*",
            _ => "device uint*",
        };

        private void EmitResourceParameters(StringBuilder source)
        {
            if (_argumentLayout!.Fields.Count != 0)
            {
                source.AppendLine(
                    $"    constant {Gen5MslArgumentLayout.StructName}& {ResourcesName} [[buffer({Gen5MslArgumentLayout.ResourcesBufferIndex})]],");
            }

            if (_request.Bindings.UsesPushData)
            {
                source.AppendLine($"    constant uint* {PushDataName} [[buffer({Gen5MslArgumentLayout.PushDataBufferIndex})]],");
            }
        }

        // ---- initial state ----

        // Buffer locals, user data and the per-buffer byte biases from the packed offsets.
        private void EmitLayoutInitialState(StringBuilder source)
        {
            var request = _request;
            var layout = request.Bindings;
            var bufferCount = request.Resources.Info.Buffers.Count;
            for (var index = 0; index < bufferCount; index++)
            {
                source.AppendLine($"    device uint* b{index} = {ResourcesName}.buffers[{index}];");
            }

            source.AppendLine($"    uint bias[{Math.Max(bufferCount, 1)}] = {{}};");
            if (_hasGlobalDataShare)
            {
                source.AppendLine($"    device uint* {GlobalDataShareName} = {ResourcesName}.global_data_share;");
                source.AppendLine($"    uint {GlobalDataShareWordsName} = {ResourcesName}.global_data_share_bytes >> 2u;");
            }

            if (_hasDeviceAddressTable)
            {
                source.AppendLine($"    device const Gen5AddressRange* sharpemu_ranges = {ResourcesName}.address_ranges;");
                source.AppendLine($"    uint sharpemu_range_count = min({ResourcesName}.address_range_count, {MaxAddressRangeCount}u);");
                source.AppendLine($"    device uint* sharpemu_faults = {ResourcesName}.fault_bits;");
                source.AppendLine($"    uint sharpemu_fault_words = {ResourcesName}.fault_word_count;");
            }

            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
            {
                var register = layout.UserDataRegisters[index];
                if (register < ScalarRegisterFileCount)
                {
                    source.AppendLine($"    s[{register}] = {ShaderDataDword((uint)index)};");
                }
            }

            for (uint buffer = 0; buffer < layout.MemoryOffsetCount; buffer++)
            {
                source.AppendLine(
                    $"    bias[{buffer}] = ({ShaderDataDword(layout.MemoryOffsetDword + (buffer / 4))} >> {(buffer % 4) * 8}u) & 0xFFu;");
            }

            foreach (var scratch in _indirectKeyScratch.Values.OrderBy(static name => name, StringComparer.Ordinal))
            {
                source.AppendLine($"    uint {scratch} = 0u;");
            }
        }

        // One dword of this stage's block: from push data, or the shader-data buffer when spilled.
        private string ShaderDataDword(uint index)
        {
            var layout = _request.Bindings;
            if (layout.UsesPushData)
            {
                return $"{PushDataName}[{layout.PushDataStartDword + index}]";
            }

            return $"({index}u < {ResourcesName}.shader_data_words ? {ResourcesName}.shader_data[{index}u] : 0u)";
        }

        private string ComputeThreadLimit(uint component)
        {

            if (_request.Bindings is { UsesDispatchThreadLimits: true } layout)
            {
                return ShaderDataDword(layout.DispatchThreadLimitsDword + component);
            }

            var limit = component switch
            {
                0 => _request.ThreadCountX,
                1 => _request.ThreadCountY,
                _ => _request.ThreadCountZ,
            };
            return FormatUInt(limit);
        }

        // The shader base the host pushes for this draw, as two dwords.
        private (string Low, string High) ShaderBaseWords()
        {
            var layout = _request.Bindings;
            if (!layout.UsesShaderBase)
            {
                return ("0u", "0u");
            }

            return (ShaderDataDword(layout.ShaderBaseDword), ShaderDataDword(layout.ShaderBaseDword + 1));
        }

        private string LoadFlattenedWord(string slot) =>
            $"({slot} < {ResourcesName}.flattened_table_words ? {ResourcesName}.flattened_table[{slot}] : 0u)";

        private static string FormatULong(ulong value) => $"0x{value.ToString("X", CultureInfo.InvariantCulture)}ul";

        // A signed immediate widened to the address width.
        private static string SignedOffset64(int offset) => FormatULong(unchecked((ulong)(long)offset));

        // A written access is allowed only inside its handle's tracked range:
        // width <= size, address >= base, address - base <= size - width.
        private string IsWrittenAccessAllowed(int memoryIndex, string address, uint widthBytes)
        {
            if (!_hasFlattenedTable || !_request.WrittenRangeSlotByMemoryIndex.TryGetValue(memoryIndex, out var slot))
            {
                return "false";
            }

            var rangeBase = Temp("ulong", $"(ulong){LoadFlattenedWord($"{slot}u")} | ((ulong){LoadFlattenedWord($"{slot + 1}u")} << 32)");
            var rangeSize = Temp("ulong", $"(ulong){LoadFlattenedWord($"{slot + 2}u")}");
            var masked = Temp("ulong", $"{address} & {FormatULong(DeviceAddressPaging.AddressMask)}");
            return Temp(
                "bool",
                $"{widthBytes}ul <= {rangeSize} && {rangeBase} <= {masked} && ({masked} - {rangeBase}) <= ({rangeSize} - {widthBytes}ul)");
        }

        // The atomic expression over a device word pointer for one opcode suffix.
        private static bool TryGetAtomicExpression(string name, string value, string comparator, out Func<string, string> build)
        {
            Func<string, string>? candidate = name switch
            {
                "Add" => pointer => $"atomic_fetch_add_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Sub" => pointer => $"atomic_fetch_sub_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Umin" => pointer => $"atomic_fetch_min_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Umax" or "UMax" => pointer => $"atomic_fetch_max_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Smin" => pointer => $"(uint)atomic_fetch_min_explicit((device atomic_int*)({pointer}), as_type<int>({value}), memory_order_relaxed)",
                "Smax" => pointer => $"(uint)atomic_fetch_max_explicit((device atomic_int*)({pointer}), as_type<int>({value}), memory_order_relaxed)",
                "And" => pointer => $"atomic_fetch_and_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Or" => pointer => $"atomic_fetch_or_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Xor" => pointer => $"atomic_fetch_xor_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Swap" => pointer => $"atomic_exchange_explicit((device atomic_uint*)({pointer}), {value}, memory_order_relaxed)",
                "Cmpswap" => pointer => $"sharpemu_atomic_compare_swap((device atomic_uint*)({pointer}), {comparator}, {value})",
                "Inc" => pointer => $"sharpemu_atomic_step((device atomic_uint*)({pointer}), true)",
                "Dec" => pointer => $"sharpemu_atomic_step((device atomic_uint*)({pointer}), false)",
                _ => null,
            };
            build = candidate!;
            return candidate is not null;
        }

        // ---- scalar memory ----

        private bool TryEmitLayoutScalarMemory(Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control, out string error)
        {
            error = string.Empty;
            var request = _request;
            var dynamicOffset = control.DynamicOffsetRegister is { } register ? ScalarExpression(register) : "0u";
            string? deviceAddress = null;
            for (var component = 0; component < instruction.Destinations.Count; component++)
            {
                var destination = instruction.Destinations[component];
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "invalid scalar-memory destination";
                    return false;
                }

                if (!request.Memory.TryGetIndex(instruction.Pc, (uint)component, out var memoryIndex))
                {
                    error = $"scalar load component {component} has no memory record";
                    return false;
                }

                var entry = request.Memory[memoryIndex];
                if (request.IndirectOffsetKeyMemoryIndices.Contains(memoryIndex))
                    Line($"{_indirectKeyScratch[memoryIndex]} = {dynamicOffset};");
                string value;
                if (entry.PlanningOnly)
                {
                    // A planning-only read without a slot is a heap descriptor the host resolved; nothing is emitted.
                    if (!request.FlattenedSlotByMemoryIndex.TryGetValue(memoryIndex, out var slot))
                    {
                        continue;
                    }

                    if (!_hasFlattenedTable)
                    {
                        error = "flattened read without a flattened table binding";
                        return false;
                    }

                    value = Temp("uint", LoadFlattenedWord($"{slot}u"));
                }
                else if (entry.Kind == MemoryResourceKind.ScalarBuffer)
                {
                    if (entry.Resource == MemoryAccessInfo.NoResource)
                    {
                        error = "scalar buffer load has no dense buffer";
                        return false;
                    }

                    var bindingIndex = (int)entry.Resource;
                    var byteOffset = unchecked((uint)control.ImmediateOffsetBytes + ((uint)component * sizeof(uint)));
                    var address = Temp("uint", ApplyByteBias(bindingIndex, $"({dynamicOffset} + {FormatUInt(byteOffset)})"));
                    value = Temp("uint", LoadWord(bindingIndex, address));
                }
                else
                {
                    if (instruction.Sources.Count == 0 || instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
                    {
                        error = "invalid scalar-memory address";
                        return false;
                    }

                    if (!_hasDeviceAddressTable)
                    {
                        error = "device address access without an address range binding";
                        return false;
                    }

                    if (deviceAddress is null)
                    {
                        var baseRegister = instruction.Sources[0].Value;
                        deviceAddress = Temp(
                            "ulong",
                            $"({Scalar64Expression(baseRegister)} + (ulong){dynamicOffset} + {SignedOffset64(control.ImmediateOffsetBytes)}) & {FormatULong(DeviceAddressPaging.AddressMask & ~3ul)}");
                    }

                    var componentAddress = component == 0 ? deviceAddress : $"({deviceAddress} + {component * sizeof(uint)}ul)";
                    value = Temp("uint", $"sharpemu_load_device_dword({DeviceArguments}, {componentAddress})");
                }

                if (_indirectKeyScratch.TryGetValue(memoryIndex, out var keyScratch))
                {
                    Line($"{keyScratch} = {value};");
                }

                StoreScalar(destination.Value, value);
            }

            return true;
        }

        // ---- global memory ----

        private bool TryEmitLayoutGlobalMemory(Gen5ShaderInstruction instruction, Gen5GlobalMemoryControl control, out string error)
        {
            error = string.Empty;
            var request = _request;
            if (!request.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                error = "global access has no memory record";
                return false;
            }

            if (!_hasDeviceAddressTable)
            {
                error = "device address access without an address range binding";
                return false;
            }

            var memoryOpcode = control.UsesFlatAddress ? "Global" + instruction.Opcode["Flat".Length..] : instruction.Opcode;
            var address = control.UsesFlatAddress || control.ScalarAddress >= 125
                ? $"(ulong)v[{control.VectorAddress}] | ((ulong)v[{control.VectorAddress + 1}] << 32)"
                : $"{Scalar64Expression(control.ScalarAddress)} + (ulong)v[{control.VectorAddress}]";
            address = Temp("ulong", $"({address}) + {SignedOffset64(control.OffsetBytes)}");
            var entry = request.Memory[memoryIndex];
            var writes = entry.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            var accessBytes = Math.Max((entry.DataBits + 7) / 8, 1u) * Math.Max(entry.DataDwords, 1u);
            var allowed = writes ? IsWrittenAccessAllowed(memoryIndex, address, accessBytes) : "true";

            if (memoryOpcode.StartsWith("GlobalAtomic", StringComparison.Ordinal))
            {
                if (!TryGetAtomicExpression(
                        memoryOpcode["GlobalAtomic".Length..],
                        $"v[{control.SourceVectorRegister}]",
                        $"v[{control.SourceVectorRegister + 1}]",
                        out var build))
                {
                    error = $"unsupported global opcode {instruction.Opcode}";
                    return false;
                }

                Line($"if (exec && {allowed})");
                Line("{");
                _indent++;
                var valid = Temp("bool", "false");
                var pointer = Temp("device uint*", $"sharpemu_resolve_device_address({DeviceArguments}, {address}, {valid})");
                Line($"if ({valid})");
                Line("{");
                _indent++;
                var original = Temp("uint", build(pointer));
                if (control.Glc)
                {
                    Line($"v[{control.DestinationVectorRegister}] = {original};");
                }

                _indent--;
                Line("}");
                _indent--;
                Line("}");
                return true;
            }

            if (memoryOpcode.StartsWith("GlobalStore", StringComparison.Ordinal))
            {
                Line($"if (exec && {allowed})");
                Line("{");
                _indent++;
                if (TryGetSubdwordStoreInfo(memoryOpcode, out var byteCount, out var sourceShift))
                {
                    var source = sourceShift == 0
                        ? $"v[{control.SourceVectorRegister}]"
                        : $"(v[{control.SourceVectorRegister}] >> {sourceShift})";
                    Line($"sharpemu_store_device_bytes({DeviceArguments}, {address}, {source}, {byteCount}u);");
                }
                else
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        Line($"sharpemu_store_device_dword({DeviceArguments}, {address} + {index * sizeof(uint)}ul, v[{control.SourceVectorRegister + index}]);");
                    }
                }

                _indent--;
                Line("}");
                return true;
            }

            if (TryGetSubdwordLoadInfo(memoryOpcode, out var loadBytes, out var signExtend, out var d16, out var d16High))
            {
                var loaded = Temp(
                    "uint",
                    $"sharpemu_load_device_unaligned({DeviceArguments}, {address}, {loadBytes}u) & {FormatUInt(loadBytes == 1 ? 0xFFu : 0xFFFFu)}");
                if (signExtend)
                {
                    loaded = Temp("uint", $"(uint)extract_bits(as_type<int>({loaded}), 0u, {loadBytes * 8}u)");
                }

                var destination = control.DestinationVectorRegister;
                if (!d16)
                {
                    StoreVector(destination, loaded);
                    return true;
                }

                StoreVector(
                    destination,
                    d16High
                        ? $"(v[{destination}] & 0x0000FFFFu) | (({loaded} & 0xFFFFu) << 16)"
                        : $"(v[{destination}] & 0xFFFF0000u) | ({loaded} & 0xFFFFu)");
                return true;
            }

            if (!memoryOpcode.StartsWith("GlobalLoad", StringComparison.Ordinal))
            {
                error = $"unsupported global opcode {instruction.Opcode}";
                return false;
            }

            var aligned = Temp("ulong", $"{address} & ~3ul");
            for (uint index = 0; index < control.DwordCount; index++)
            {
                var componentAddress = index == 0 ? aligned : $"{aligned} + {index * sizeof(uint)}ul";
                StoreVector(
                    control.DestinationVectorRegister + index,
                    $"sharpemu_load_device_dword({DeviceArguments}, {componentAddress})");
            }

            return true;
        }

        // ---- buffers ----

        private bool TryResolveLayoutBuffer(uint pc, out int bindingIndex, out BufferResource resource)
        {
            bindingIndex = -1;
            resource = null!;
            var request = _request;
            if (!request.Memory.TryGetIndex(pc, 0, out var memoryIndex))
            {
                return false;
            }

            var entry = request.Memory[memoryIndex];
            if (entry.Resource == MemoryAccessInfo.NoResource || entry.Resource >= (uint)request.Resources.Info.Buffers.Count)
            {
                return false;
            }

            bindingIndex = (int)entry.Resource;
            resource = request.Resources.Info.Buffers[bindingIndex];
            return true;
        }

        // ---- images ----

        // An access over several descriptors takes one constant-element case per descriptor:
        // the mip operand selects a per-mip element, the indirect key a candidate.
        private bool TryGetImageElementCases(Gen5ShaderInstruction instruction, Gen5ImageControl image, out string selector, out IReadOnlyList<(uint Resource, uint Element)> elements, out string error)
        {
            selector = string.Empty;
            elements = [];
            error = string.Empty;
            var request = _request;
            if (!request.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                return false;
            }

            var entry = request.Memory[memoryIndex];
            var info = request.Resources.Info;
            if (entry.Resource == MemoryAccessInfo.NoResource || entry.Resource >= (uint)info.Images.Count)
            {
                return false;
            }

            var resourceIndex = (int)entry.Resource;
            var imageInfo = info.Images[resourceIndex];
            var kind = ImageDescriptorBinding.ForImage(imageInfo);
            if (kind is null || !_imageClasses.TryGetValue(kind.Value, out var imageClass))
            {
                return false;
            }

            var classElements = imageClass.Resources.ToList();
            var element = classElements.IndexOf((uint)resourceIndex);
            if (element < 0)
            {
                return false;
            }

            if (imageInfo.MipMode == ImageMipMode.DynamicStorage && instruction.Opcode is "ImageLoadMip" or "ImageStoreMip")
            {
                // A mip past the last descriptor matches no case and does nothing.
                selector = Temp("uint", ImageIntegerAddress(image, 2));
                elements = Enumerable.Range(0, (int)imageInfo.MipCount)
                    .Select(mip => ((uint)resourceIndex, (uint)element + (uint)mip)).ToList();
                return true;
            }

            if (request.IndirectRootByMemoryIndex.TryGetValue(memoryIndex, out var keyMemoryIndex) && imageInfo.IndirectSearchIterations != 0)
            {
                if (!_hasFlattenedTable)
                {
                    error = "indirect image access without a flattened table binding";
                    return false;
                }

                var candidates = info.Images[(int)imageInfo.IndirectRoot].IndirectResources;
                var candidateElements = new List<(uint Resource, uint Element)>();
                foreach (var candidate in candidates)
                {
                    var candidateKind = ImageDescriptorBinding.ForImage(info.Images[(int)candidate]);
                    if (candidateKind is null || !_imageClasses.TryGetValue(candidateKind.Value, out var candidateClass))
                    {
                        error = $"indirect candidate {candidate} has no declared binding class";
                        return false;
                    }
                    var candidateElement = candidateClass.Resources.ToList().IndexOf(candidate);
                    if (candidateElement < 0)
                    {
                        error = $"indirect candidate {candidate} is not an element of {candidateKind.Value}";
                        return false;
                    }

                    candidateElements.Add((candidate, (uint)candidateElement));
                }

                selector = SelectIndirectCandidate(imageInfo, keyMemoryIndex, (uint)candidateElements.Count);
                elements = candidateElements;
                return true;
            }

            return false;
        }

        // Resolves the image an instruction reads through its class array into a texture local,
        // with its sampler for sampling operations. Only two-dimensional images are accessible.
        private bool TryResolveLayoutImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string texture,
            out string samplerName,
            out string kind,
            out bool isStorage,
            out uint dstSelect,
            out string mipLevel,
            out string error,
            (uint Resource, uint Element)? fixedElement = null)
        {
            error = string.Empty;
            texture = string.Empty;
            samplerName = string.Empty;
            kind = "float";
            isStorage = false;
            dstSelect = DescriptorConstants.IdentityImageSwizzle;
            mipLevel = "0u";
            var request = _request;
            var info = request.Resources.Info;
            if (!request.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                error = "image access has no memory record";
                return false;
            }

            var entry = request.Memory[memoryIndex];
            if (entry.Resource == MemoryAccessInfo.NoResource || entry.Resource >= (uint)info.Images.Count)
            {
                error = "image access has no dense image";
                return false;
            }

            var resourceIndex = (int)(fixedElement?.Resource ?? entry.Resource);
            var imageInfo = info.Images[resourceIndex];
            var bindingKind = ImageDescriptorBinding.ForImage(imageInfo);
            if (bindingKind is null || !_imageClasses.TryGetValue(bindingKind.Value, out var imageClass))
            {
                error = $"image {resourceIndex} has no declared binding class";
                return false;
            }

            if (imageClass.Multisampled)
            {
                error = "multisampled image access is not supported";
                return false;
            }

            if (imageClass.Dimension != ImageDimension.Dim2D)
            {
                error = $"image dimension {imageClass.Dimension} is not supported on Metal";
                return false;
            }

            var element = imageClass.Resources.ToList().IndexOf((uint)resourceIndex);
            if (element < 0)
            {
                error = $"image {resourceIndex} is not an element of {bindingKind.Value}";
                return false;
            }

            // Several descriptors (per-mip elements, indirect candidates) need the caller's constant case.
            var severalDescriptors =
                (imageInfo.MipMode == ImageMipMode.DynamicStorage && instruction.Opcode is "ImageLoadMip" or "ImageStoreMip") ||
                (request.IndirectRootByMemoryIndex.ContainsKey(memoryIndex) && imageInfo.IndirectSearchIterations != 0);
            if (fixedElement is null && severalDescriptors)
            {
                error = "image access over several descriptors needs a constant element case";
                return false;
            }

            // A sampled mip load keeps its mip operand; a storage case is already its own mip view.
            if (!imageClass.IsStorage && instruction.Opcode == "ImageLoadMip")
            {
                mipLevel = Temp("uint", ImageIntegerAddress(image, 2));
            }

            texture = Temp(imageClass.TextureType, $"{ResourcesName}.{imageClass.Field}[{fixedElement?.Element ?? (uint)element}u]");
            kind = imageClass.ComponentKind;
            isStorage = imageClass.IsStorage;
            if (UsesSampler(instruction.Opcode))
            {
                if (!_hasSamplers)
                {
                    error = "sampling without a sampler binding";
                    return false;
                }

                if (!request.Resources.SamplerByMemoryIndex.TryGetValue(memoryIndex, out var samplerIndex))
                {
                    samplerIndex = entry.Sampler;
                }

                if (samplerIndex == MemoryAccessInfo.NoResource)
                {
                    error = "sampled access has no dense sampler";
                    return false;
                }

                samplerName = Temp("sampler", $"{ResourcesName}.samplers[{samplerIndex}]");
            }

            dstSelect = imageInfo.ShaderSwizzle;
            return true;
        }

        // Searches the sorted key mapping of an indirect table for the captured key; the
        // result is the candidate-local index, candidate 0 when the key is absent or out of range.
        private string SelectIndirectCandidate(ImageResource imageInfo, int keyMemoryIndex, uint candidateCount)
        {
            var key = _indirectKeyScratch.TryGetValue(keyMemoryIndex, out var scratch) ? scratch : "0u";
            var mapping = imageInfo.IndirectMappingOffset;
            var count = Temp("uint", LoadFlattenedWord($"{mapping}u"));
            var low = Temp("uint", "0u");
            var high = Temp("uint", count);
            for (uint iteration = 0; iteration < imageInfo.IndirectSearchIterations; iteration++)
            {
                var middle = Temp("uint", $"{low} + (({high} - {low}) >> 1u)");
                var probeSlot = Temp("uint", $"{mapping + 1}u + ({middle} << 1u)");
                var probeKey = Temp("uint", LoadFlattenedWord(probeSlot));
                var moveUp = Temp("bool", $"{probeKey} < {key} && {low} < {high}");
                Line($"{low} = {moveUp} ? {middle} + 1u : {low};");
                Line($"{high} = {moveUp} ? {high} : {middle};");
            }

            var foundSlot = Temp("uint", $"{mapping + 1}u + ({low} << 1u)");
            var foundKey = Temp("uint", LoadFlattenedWord(foundSlot));
            var found = Temp("bool", $"{low} < {count} && {foundKey} == {key}");
            var mappedSlot = Temp("uint", $"{foundSlot} + 1u");

            // The mapping holds candidate-local indices: the root's candidate list in order.
            var mapped = Temp("uint", LoadFlattenedWord(mappedSlot));
            return Temp("uint", $"{found} && {mapped} < {candidateCount}u ? {mapped} : 0u");
        }

        // ---- global data share ----

        // DS operations with the GDS bit run over the shared buffer with device atomics;
        // lane operations have no GDS form.
        private bool TryEmitGlobalDataShare(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (!_hasGlobalDataShare)
            {
                error = "GDS access without a global data share binding";
                return false;
            }

            var opcode = instruction.Opcode;
            if (opcode.Contains("Swizzle", StringComparison.Ordinal) || opcode.Contains("Bpermute", StringComparison.Ordinal) ||
                opcode.Contains("Permute", StringComparison.Ordinal) || opcode.Contains("Addtid", StringComparison.Ordinal))
            {
                error = $"lane operation {opcode} is not valid on the global data share";
                return false;
            }

            switch (opcode)
            {
                case "DsAppend":
                case "DsConsume":
                    return TryEmitGlobalDataShareCounter(instruction, control, out error);
                case "DsWriteB32":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreGlobalDataShareWord(index, RawSource(instruction, 1));
                    return true;
                }

                case "DsWriteB64":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreGlobalDataShareWord(index, RawSource(instruction, 1));
                    StoreGlobalDataShareWord($"({index} + 1u)", RawSource(instruction, 2));
                    return true;
                }

                case "DsWrite2B64":
                case "DsWrite2St64B64":
                    return TryEmitDataShareWritePair64(instruction, control, out error);
                case "DsWrite2B32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    var first = Temp("uint", GlobalDataShareIndex(address, control.Offset0 * sizeof(uint)));
                    var second = Temp("uint", GlobalDataShareIndex(address, control.Offset1 * sizeof(uint)));
                    StoreGlobalDataShareWord(first, RawSource(instruction, 1));
                    StoreGlobalDataShareWord(second, RawSource(instruction, 2));
                    return true;
                }

                case "DsReadB32":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreVector(instruction.Destinations[0].Value, LoadGlobalDataShareWord(index));
                    return true;
                }

                case "DsReadB64":
                {
                    var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
                    StoreVector(instruction.Destinations[0].Value, LoadGlobalDataShareWord(index));
                    StoreVector(instruction.Destinations[1].Value, LoadGlobalDataShareWord($"({index} + 1u)"));
                    return true;
                }

                case "DsRead2B64":
                    return TryEmitDataShareReadPair64(instruction, control, out error);
                case "DsRead2B32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    var first = Temp("uint", GlobalDataShareIndex(address, control.Offset0 * sizeof(uint)));
                    var second = Temp("uint", GlobalDataShareIndex(address, control.Offset1 * sizeof(uint)));
                    StoreVector(instruction.Destinations[0].Value, LoadGlobalDataShareWord(first));
                    StoreVector(instruction.Destinations[1].Value, LoadGlobalDataShareWord(second));
                    return true;
                }

                default:
                    if (Gen5ShaderTranslator.IsDataShareAtomic(opcode))
                    {
                        return TryEmitGlobalDataShareAtomic(instruction, control, out error);
                    }

                    error = $"unsupported GDS opcode {opcode}";
                    return false;
            }
        }

        private bool TryEmitDataShareWritePair64(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            var offsetStride = instruction.Opcode == "DsWrite2St64B64" ? 512u : 8u;
            if (instruction.Sources.Count < 5)
            {
                error = "The paired 64-bit write requires an address and two source-register pairs.";
                return false;
            }

            Line("if (exec)");
            Line("{");
            _indent++;
            var address = Temp("uint", RawSource(instruction, 0));
            // Equal offsets select only the first source pair.
            var componentCount = control.Offset0 == control.Offset1 ? 2 : 4;
            for (var component = 0; component < componentCount; component++)
            {
                var pairOffset = component < 2 ? control.Offset0 : control.Offset1;
                var byteOffset = pairOffset * offsetStride + (uint)(component % 2) * sizeof(uint);
                var value = RawSource(instruction, component + 1);
                if (control.Gds)
                    StoreGlobalDataShareWord(GlobalDataShareIndex(address, byteOffset), value);
                else
                    Line($"sharpemu_lds[{LdsIndex(address, byteOffset)}] = {value};");
            }
            _indent--;
            Line("}");
            return true;
        }

        private bool TryEmitDataShareReadPair64(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 1 || instruction.Destinations.Count < 4)
            {
                error = "The paired 64-bit read requires an address and four destination registers.";
                return false;
            }

            Line("if (exec)");
            Line("{");
            _indent++;
            var address = Temp("uint", RawSource(instruction, 0));
            var values = new string[4];
            // Capture both values before an overlapping destination changes the address.
            for (var component = 0; component < values.Length; component++)
            {
                var pairOffset = component < 2 ? control.Offset0 : control.Offset1;
                var byteOffset = pairOffset * sizeof(ulong) + (uint)(component % 2) * sizeof(uint);
                values[component] = Temp("uint", control.Gds
                    ? LoadGlobalDataShareWord(GlobalDataShareIndex(address, byteOffset))
                    : $"sharpemu_lds[{LdsIndex(address, byteOffset)}]");
            }
            for (var component = 0; component < values.Length; component++)
                StoreVector(instruction.Destinations[component].Value, values[component], guardWithExec: false);
            _indent--;
            Line("}");
            return true;
        }

        private static string GlobalDataShareIndex(string address, uint offsetBytes) =>
            offsetBytes == 0 ? $"({address}) >> 2u" : $"(({address}) + {offsetBytes}u) >> 2u";

        private static string LoadGlobalDataShareWord(string index) =>
            $"({index} < {GlobalDataShareWordsName} ? {GlobalDataShareName}[{index}] : 0u)";

        private void StoreGlobalDataShareWord(string index, string value) =>
            Line($"if (exec && {index} < {GlobalDataShareWordsName}) {{ {GlobalDataShareName}[{index}] = {value}; }}");

        private bool TryEmitGlobalDataShareAtomic(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            var name = instruction.Opcode switch
            {
                "DsAddU32" or "DsAddRtnU32" => "Add",
                "DsSubU32" or "DsSubRtnU32" => "Sub",
                "DsIncU32" or "DsIncRtnU32" => "Inc",
                "DsDecU32" or "DsDecRtnU32" => "Dec",
                "DsMinI32" or "DsMinRtnI32" => "Smin",
                "DsMaxI32" or "DsMaxRtnI32" => "Smax",
                "DsMinU32" or "DsMinRtnU32" => "Umin",
                "DsMaxU32" or "DsMaxRtnU32" => "Umax",
                "DsAndB32" or "DsAndRtnB32" => "And",
                "DsOrB32" or "DsOrRtnB32" => "Or",
                "DsXorB32" or "DsXorRtnB32" => "Xor",
                "DsWrxchgRtnB32" => "Swap",
                "DsCmpstB32" or "DsCmpstRtnB32" => "Cmpswap",
                _ => string.Empty,
            };
            var isCompare = name == "Cmpswap";
            var value = instruction.Sources.Count > (isCompare ? 2 : 1) ? RawSource(instruction, isCompare ? 2 : 1) : "0u";
            var comparator = instruction.Sources.Count > 1 ? RawSource(instruction, 1) : "0u";
            if (name.Length == 0 || !TryGetAtomicExpression(name, value, comparator, out var build))
            {
                error = $"unsupported GDS opcode {instruction.Opcode}";
                return false;
            }

            var index = Temp("uint", GlobalDataShareIndex(RawSource(instruction, 0), control.SingleOffsetBytes));
            Line($"if (exec && {index} < {GlobalDataShareWordsName})");
            Line("{");
            _indent++;
            var original = Temp("uint", build($"{GlobalDataShareName} + {index}"));
            if (instruction.Destinations.Count > 0)
            {
                Line($"v[{instruction.Destinations[0].Value}] = {original};");
            }

            _indent--;
            Line("}");
            return true;
        }

        // Append/consume on the GDS counter at M0's base: M0 must carry a size, and the word must be inside the buffer.
        private bool TryEmitGlobalDataShareCounter(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 1 || instruction.Destinations.Count < 1)
            {
                error = $"missing {instruction.Opcode} operand";
                return false;
            }

            var offset = control.SingleOffsetBytes;
            var m0 = Temp("uint", RawSource(instruction, 0));
            var baseAddress = Temp("uint", $"{m0} >> 16u");
            var sizeBytes = Temp("uint", $"{m0} & 0xFFFFu");
            var index = Temp("uint", GlobalDataShareIndex(baseAddress, offset));
            var inBounds = Temp("bool", $"{sizeBytes} != 0u && {index} < {GlobalDataShareWordsName}");
            var destination = instruction.Destinations[0].Value;
            var operation = instruction.Opcode == "DsAppend" ? "add" : "sub";
            var word = $"(device atomic_uint*)({GlobalDataShareName} + {index})";

            // Graphics stages model one logical lane, so the counter grows by one per invocation.
            if (_stage != Gen5MslStage.Compute)
            {
                var original = Temp("uint", "0u");
                Line($"if (exec && {inBounds}) {{ {original} = atomic_fetch_{operation}_explicit({word}, 1u, memory_order_relaxed); }}");
                StoreVector(destination, $"{inBounds} ? {original} : 0u");
                return true;
            }

            string count;
            string first;
            if (IsWave64)
            {
                Line("sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot(exec);");
                Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
                var low = Temp("uint", "sharpemu_wave_scratch[0]");
                var high = Temp("uint", "sharpemu_wave_scratch[1]");
                count = Temp("uint", $"popcount({low}) + popcount({high})");
                first = Temp(
                    "uint",
                    $"({low} != 0u) ? (uint)ctz({low}) : (({high} != 0u) ? (32u + (uint)ctz({high})) : 0u)");
                Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
                var atomic = $"atomic_fetch_{operation}_explicit({word}, {count}, memory_order_relaxed)";
                var broadcast = EmitWave64ReadFirstLane($"{inBounds} ? {atomic} : 0u");
                StoreVector(destination, $"{inBounds} ? {broadcast} : 0u");
                return true;
            }

            var mask = Temp("uint", "sharpemu_ballot(exec)");
            count = Temp("uint", $"popcount({mask})");
            first = Temp("uint", $"{mask} == 0u ? 0u : (uint)ctz({mask})");
            var firstValue = Temp("uint", "0u");
            Line($"if (exec && {inBounds} && sharpemu_lane == {first})");
            Line("{");
            _indent++;
            Line($"{firstValue} = atomic_fetch_{operation}_explicit({word}, {count}, memory_order_relaxed);");
            _indent--;
            Line("}");
            var result = Temp("uint", $"simd_broadcast({firstValue}, {first})");
            StoreVector(destination, $"{inBounds} ? {result} : 0u");
            return true;
        }
    }
}
