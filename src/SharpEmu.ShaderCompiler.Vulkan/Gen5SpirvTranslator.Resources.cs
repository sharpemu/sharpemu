// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// The resource path over a compile request: every binding comes from the layout, every
// descriptor index from the memory table, user data from push data.
public static partial class Gen5SpirvTranslator
{
    public const int DeviceAddressPageBits = DeviceAddressPaging.PageBits;
    public const ulong DeviceAddressPageSize = DeviceAddressPaging.PageSize;

    private const ulong DeviceAddressMask = DeviceAddressPaging.AddressMask;

    public static bool TryCompileProgram(ShaderCompileRequest request, out Gen5SpirvShader shader, out string error)
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

        if (request.Stage == ShaderStage.Pixel)
        {
            if (request.PixelOutputs.Count > 8 || request.PixelOutputs.Any(output => output.GuestSlot > 7))
            {
                error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
                return false;
            }

            if (request.PixelOutputs.Select(output => output.GuestSlot).Distinct().Count() != request.PixelOutputs.Count ||
                request.PixelOutputs.Select(output => output.HostLocation).Distinct().Count() != request.PixelOutputs.Count)
            {
                error = "pixel output guest slots and host locations must be unique";
                return false;
            }

            if (request.PixelOutputs.Any(output => output.HostLocation >= request.PixelOutputs.Count))
            {
                error = "pixel output host locations must be dense in the 0..N-1 range";
                return false;
            }
        }

        return new CompilationContext(request).TryCompile(out shader, out error);
    }

    private sealed partial class CompilationContext
    {
        private readonly ShaderCompileRequest _request;
        private readonly Dictionary<DescriptorBindingKind, LayoutImageClass> _imageClasses = [];
        private readonly Dictionary<int, uint> _indirectKeyScratch = [];
        private uint _pushData;
        private uint _shaderData;
        private uint _flattenedTable;
        private uint _pageTable;
        private uint _faultBuffer;
        private uint _globalDataShare;
        private uint _samplerArray;
        private uint _samplerType;
        private uint _samplerPointer;
        private uint _storageUlongPointer;
        private uint _physicalUintPointer;
        private uint _deviceEntryScratch;
        private uint _deviceWordScratch;
        private uint _deviceBufferWordScratch;
        private uint _pushDataBlockPointer;
        private uint _wordRuntimeArray;
        private uint _addressRuntimeArray;

        private readonly record struct LayoutImageClass(
            uint Variable,
            uint ImageType,
            uint ElementPointer,
            uint ComponentType,
            ImageComponentKind Kind,
            bool IsStorage,
            bool Arrayed,
            bool Cube,
            bool Multisampled,
            SpirvImageDim Dimension,
            IReadOnlyList<uint> Resources);

        public CompilationContext(ShaderCompileRequest request)
        {
            _request = request;
            _stage = request.Stage switch
            {
                ShaderStage.Vertex => Gen5SpirvStage.Vertex,
                ShaderStage.Pixel => Gen5SpirvStage.Pixel,
                _ => Gen5SpirvStage.Compute,
            };
            _pixelOutputBindings = request.PixelOutputs;
            _usesPixelValidMask =
                _stage == Gen5SpirvStage.Pixel &&
                request.Program.Instructions.Any(static instruction => instruction.Control is Gen5ExportControl { ValidMask: true });
            _enableGraphicsSubgroupOperations = _stage == Gen5SpirvStage.Compute || request.EnableGraphicsSubgroupOperations;
            _waveLaneCount = request.WaveSize == 64 ? 64u : 32u;
            _localSizeX = Math.Max(request.LocalSizeX, 1);
            _localSizeY = Math.Max(request.LocalSizeY, 1);
            _localSizeZ = Math.Max(request.LocalSizeZ, 1);
            _emulateWave64 = _stage == Gen5SpirvStage.Compute && _waveLaneCount == 64 && (ulong)_localSizeX * _localSizeY * _localSizeZ == 64;
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

        private void DeclareLayoutBindings()
        {
            var request = _request;
            var layout = request.Bindings;
            var info = request.Resources.Info;
            if (request.Program.Instructions.Any(static instruction =>
                    instruction.Control is Gen5ImageControl &&
                    (instruction.Opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
                     instruction.Opcode.StartsWith("ImageGather4", StringComparison.Ordinal)) &&
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal)))
            {
                _module.AddCapability(SpirvCapability.ImageGatherExtended);
            }

            _storageUintPointer = _module.TypePointer(SpirvStorageClass.StorageBuffer, _uintType);
            _storageUlongPointer = _module.TypePointer(SpirvStorageClass.StorageBuffer, _ulongType);
            foreach (var binding in layout.Descriptors)
            {
                var number = BindingLayout.NativeBindingIndex(request.Stage, binding.Kind);
                switch (binding.Kind)
                {
                    case DescriptorBindingKind.Buffers:
                        DeclareBufferArray((uint)binding.Resources.Count, number);
                        break;
                    case DescriptorBindingKind.Samplers:
                        DeclareSamplerArray((uint)binding.Resources.Count, number);
                        break;
                    case DescriptorBindingKind.GlobalDataShare:
                        _globalDataShare = DeclareWordBlock("globalDataShare", number);
                        break;
                    case DescriptorBindingKind.DeviceAddressPageTable:
                        _pageTable = DeclareDeviceAddressPageTable(number);
                        break;
                    case DescriptorBindingKind.FaultBuffer:
                        _faultBuffer = DeclareWordBlock("faultBuffer", number);
                        break;
                    case DescriptorBindingKind.FlattenedResourceTable:
                        _flattenedTable = DeclareWordBlock("flattenedResourceTable", number);
                        break;
                    case DescriptorBindingKind.ShaderData:
                        _shaderData = DeclareWordBlock("shaderData", number);
                        break;
                    default:
                        DeclareImageClass(binding, number);
                        break;
                }
            }

            if (layout.UsesPushData)
            {
                var arrayType = _module.TypeArray(_uintType, PushData.DwordCount);
                _module.AddDecoration(arrayType, SpirvDecoration.ArrayStride, sizeof(uint));
                var block = _module.TypeStruct(arrayType);
                _module.AddDecoration(block, SpirvDecoration.Block);
                _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
                _pushDataBlockPointer = _module.TypePointer(SpirvStorageClass.PushConstant, block);
                _pushConstantUintPointer = _module.TypePointer(SpirvStorageClass.PushConstant, _uintType);
                _pushData = _module.AddGlobalVariable(_pushDataBlockPointer, SpirvStorageClass.PushConstant);
                _module.AddName(_pushData, "pushData");
                _interfaces.Add(_pushData);
            }

            if (info.Buffers.Count != 0)
            {
                var biasArrayType = _module.TypeArray(_uintType, (uint)info.Buffers.Count);
                var privateBiasArrayPointer = _module.TypePointer(SpirvStorageClass.Private, biasArrayType);
                _runtimeBufferBiases = _module.AddGlobalVariable(privateBiasArrayPointer, SpirvStorageClass.Private, _module.ConstantNull(biasArrayType));
                _module.AddName(_runtimeBufferBiases, "guestBufferByteBias");
                _interfaces.Add(_runtimeBufferBiases);
            }

            if (info.UsesDeviceAddresses)
            {
                _module.AddCapability(SpirvCapability.PhysicalStorageBufferAddresses);
                _module.SetPhysicalStorageBuffer64MemoryModel();
                _physicalUintPointer = _module.TypePointer(SpirvStorageClass.PhysicalStorageBuffer, _uintType);
                var privateUlongPointer = _module.TypePointer(SpirvStorageClass.Private, _ulongType);
                _deviceEntryScratch = _module.AddGlobalVariable(privateUlongPointer, SpirvStorageClass.Private, _module.Constant64(_ulongType, 0));
                _deviceWordScratch = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(0));
                _module.AddName(_deviceEntryScratch, "deviceAddressEntry");
                _module.AddName(_deviceWordScratch, "deviceAddressWord");
                _interfaces.Add(_deviceEntryScratch);
                _interfaces.Add(_deviceWordScratch);
                if (request.Memory.Entries.Any(static memory => memory.DeviceDescriptor))
                {
                    _deviceBufferWordScratch = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(0));
                    _module.AddName(_deviceBufferWordScratch, "deviceBufferWord");
                    _interfaces.Add(_deviceBufferWordScratch);
                }
            }

            foreach (var memoryIndex in request.IndirectKeyMemoryIndices)
            {
                var scratch = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(0));
                _module.AddName(scratch, $"indirectImageKey{memoryIndex}");
                _interfaces.Add(scratch);
                _indirectKeyScratch[memoryIndex] = scratch;
            }
        }

        // The runtime array types are shared by every block, so their stride is decorated once.
        private uint WordRuntimeArray()
        {
            if (_wordRuntimeArray == 0)
            {
                _wordRuntimeArray = _module.TypeRuntimeArray(_uintType);
                _module.AddDecoration(_wordRuntimeArray, SpirvDecoration.ArrayStride, sizeof(uint));
            }

            return _wordRuntimeArray;
        }

        private uint AddressRuntimeArray()
        {
            if (_addressRuntimeArray == 0)
            {
                _addressRuntimeArray = _module.TypeRuntimeArray(_ulongType);
                _module.AddDecoration(_addressRuntimeArray, SpirvDecoration.ArrayStride, sizeof(ulong));
            }

            return _addressRuntimeArray;
        }

        private void DeclareBufferArray(uint count, uint bindingNumber)
        {
            var runtimeArray = WordRuntimeArray();
            var block = _module.TypeStruct(runtimeArray);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var descriptors = _module.TypeArray(block, count);
            var descriptorsPointer = _module.TypePointer(SpirvStorageClass.StorageBuffer, descriptors);
            _storageBlockPointer = _module.TypePointer(SpirvStorageClass.StorageBuffer, block);
            _globalBuffers = _module.AddGlobalVariable(descriptorsPointer, SpirvStorageClass.StorageBuffer);
            _module.AddName(_globalBuffers, "guestBuffers");
            _module.AddDecoration(_globalBuffers, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(_globalBuffers, SpirvDecoration.Binding, bindingNumber);
            _interfaces.Add(_globalBuffers);
        }

        // One storage block holding a runtime array of dwords.
        private uint DeclareWordBlock(string name, uint bindingNumber)
        {
            var block = _module.TypeStruct(WordRuntimeArray());
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var variable = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.StorageBuffer, block), SpirvStorageClass.StorageBuffer);
            _module.AddName(variable, name);
            _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(variable, SpirvDecoration.Binding, bindingNumber);
            _interfaces.Add(variable);
            return variable;
        }

        private uint DeclareDeviceAddressPageTable(uint bindingNumber)
        {
            var block = _module.TypeStruct(AddressRuntimeArray());
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.NonWritable);
            var variable = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.StorageBuffer, block), SpirvStorageClass.StorageBuffer);
            _module.AddName(variable, "deviceAddressPageTable");
            _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(variable, SpirvDecoration.Binding, bindingNumber);
            _interfaces.Add(variable);
            return variable;
        }

        private void DeclareSamplerArray(uint count, uint bindingNumber)
        {
            _samplerType = _module.TypeSampler();
            var arrayType = _module.TypeArray(_samplerType, count);
            _samplerPointer = _module.TypePointer(SpirvStorageClass.UniformConstant, _samplerType);
            _samplerArray = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.UniformConstant, arrayType), SpirvStorageClass.UniformConstant);
            _module.AddName(_samplerArray, "samplers");
            _module.AddDecoration(_samplerArray, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(_samplerArray, SpirvDecoration.Binding, bindingNumber);
            _interfaces.Add(_samplerArray);
        }

        // One array of images per binding class; an element per resource, or per mip
        // for a dynamic-mip storage image.
        private void DeclareImageClass(DescriptorBinding binding, uint bindingNumber)
        {
            var (resourceClass, numericClass, dimension, atomic) = ImageDescriptorBinding.Describe(binding.Kind);
            var cube = ImageDescriptorBinding.IsCube(binding.Kind);
            if (resourceClass == ImageResourceClass.None)
            {
                throw new InvalidOperationException($"binding kind {binding.Kind} is not an image class");
            }

            var isStorage = resourceClass == ImageResourceClass.Storage;
            var kind = numericClass switch
            {
                ImageNumericClass.Uint => ImageComponentKind.Uint,
                ImageNumericClass.Sint => ImageComponentKind.Sint,
                _ => ImageComponentKind.Float,
            };
            var componentType = kind switch
            {
                ImageComponentKind.Sint => _intType,
                ImageComponentKind.Uint => _uintType,
                _ => _floatType,
            };
            var spirvDimension = dimension switch
            {
                ImageDimension.Dim1D or ImageDimension.Dim1DArray => SpirvImageDim.Dim1D,
                ImageDimension.Dim3D => SpirvImageDim.Dim3D,
                _ => SpirvImageDim.Dim2D,
            };
            var arrayed = dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray;
            var multisampled = dimension is ImageDimension.Dim2DMsaa or ImageDimension.Dim2DMsaaArray;
            if (spirvDimension == SpirvImageDim.Dim1D)
            {
                _module.AddCapability(isStorage ? SpirvCapability.Image1D : SpirvCapability.Sampled1D);
            }

            var format = atomic ? SpirvImageFormat.R32ui : SpirvImageFormat.Unknown;
            if (isStorage && !atomic)
            {
                _module.AddCapability(SpirvCapability.StorageImageReadWithoutFormat);
                _module.AddCapability(SpirvCapability.StorageImageWriteWithoutFormat);
            }

            var imageType = _module.TypeImage(componentType, spirvDimension, depth: false, arrayed, multisampled, sampled: isStorage ? 2u : 1u, format);
            var arrayType = _module.TypeArray(imageType, (uint)binding.Resources.Count);
            var elementPointer = _module.TypePointer(SpirvStorageClass.UniformConstant, imageType);
            var variable = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.UniformConstant, arrayType), SpirvStorageClass.UniformConstant);
            _module.AddName(variable, $"{binding.Kind}");
            _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(variable, SpirvDecoration.Binding, bindingNumber);
            _interfaces.Add(variable);
            _imageClasses[binding.Kind] = new LayoutImageClass(variable, imageType, elementPointer, componentType, kind, isStorage, arrayed, cube, multisampled, spirvDimension, binding.Resources);
        }

        // ---- initial state ----

        // User data comes from push data or the shader-data buffer, and the packed memory
        // offset bytes become the per-buffer byte biases.
        private void EmitLayoutInitialState()
        {
            var layout = _request.Bindings;
            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
            {
                StoreS(layout.UserDataRegisters[index], LoadShaderDataDword(UInt((uint)index)));
            }

            for (uint buffer = 0; buffer < layout.MemoryOffsetCount; buffer++)
            {
                var packed = LoadShaderDataDword(UInt(layout.MemoryOffsetDword + (buffer / 4)));
                var bias = BitwiseAnd(ShiftRightLogical(packed, UInt((buffer % 4) * 8)), UInt(0xFF));
                Store(RuntimeBufferBiasPointer((int)buffer), bias);
            }
        }

        private uint LoadShaderDataDword(uint dwordIndex)
        {
            var layout = _request.Bindings;
            if (layout.UsesPushData)
            {
                var pointer = _module.AddInstruction(
                    SpirvOp.AccessChain,
                    _pushConstantUintPointer,
                    _pushData,
                    UInt(0),
                    IAdd(UInt(layout.PushDataStartDword), dwordIndex));
                return Load(_uintType, pointer);
            }

            return LoadBlockWord(_shaderData, dwordIndex);
        }

        // A bounds-checked dword of a storage block; outside the block reads zero.
        private uint LoadBlockWord(uint block, uint dwordIndex)
        {
            var length = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, block, 0);
            var inRange = _module.AddInstruction(SpirvOp.ULessThan, _boolType, dwordIndex, length);
            var safeIndex = _module.AddInstruction(SpirvOp.Select, _uintType, inRange, dwordIndex, UInt(0));
            var pointer = _module.AddInstruction(SpirvOp.AccessChain, _storageUintPointer, block, UInt(0), safeIndex);
            var value = Load(_uintType, pointer);
            return _module.AddInstruction(SpirvOp.Select, _uintType, inRange, value, UInt(0));
        }

        private uint BlockWordPointer(uint block, uint dwordIndex) =>
            _module.AddInstruction(SpirvOp.AccessChain, _storageUintPointer, block, UInt(0), dwordIndex);

        private uint IsBlockWordInRange(uint block, uint dwordIndex) =>
            _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                dwordIndex,
                _module.AddInstruction(SpirvOp.ArrayLength, _uintType, block, 0));

        private uint LoadFlattenedWord(uint slot) => LoadBlockWord(_flattenedTable, slot);

        // The shader base the host pushes for this draw, as two dwords.
        private (uint Low, uint High) LoadShaderBase()
        {
            var layout = _request.Bindings;
            if (!layout.UsesShaderBase)
            {
                return (UInt(0), UInt(0));
            }

            return (LoadShaderDataDword(UInt(layout.ShaderBaseDword)), LoadShaderDataDword(UInt(layout.ShaderBaseDword + 1)));
        }

        private uint ComputeThreadLimit(uint component)
        {
            if (_request.Bindings is { UsesDispatchThreadLimits: true } layout)
            {
                return LoadShaderDataDword(UInt(layout.DispatchThreadLimitsDword + component));
            }

            var limit = component switch
            {
                0 => _request.ThreadCountX,
                1 => _request.ThreadCountY,
                _ => _request.ThreadCountZ,
            };
            return UInt(limit);
        }

        // ---- 64-bit helpers ----

        private uint ULong(ulong value) => _module.Constant64(_ulongType, value);

        private uint Widen(uint value) => _module.AddInstruction(SpirvOp.UConvert, _ulongType, value);

        private uint Narrow(uint value) => _module.AddInstruction(SpirvOp.UConvert, _uintType, value);

        private uint Pair64(uint low, uint high) =>
            _module.AddInstruction(
                SpirvOp.BitwiseOr,
                _ulongType,
                Widen(low),
                _module.AddInstruction(SpirvOp.ShiftLeftLogical, _ulongType, Widen(high), ULong(32)));

        private uint IAdd64(uint left, uint right) => _module.AddInstruction(SpirvOp.IAdd, _ulongType, left, right);

        private uint ISub64(uint left, uint right) => _module.AddInstruction(SpirvOp.ISub, _ulongType, left, right);

        private uint And64(uint left, uint right) => _module.AddInstruction(SpirvOp.BitwiseAnd, _ulongType, left, right);

        private uint ULessThan64(uint left, uint right) => _module.AddInstruction(SpirvOp.ULessThan, _boolType, left, right);

        private uint ULessThanEqual64(uint left, uint right) => _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, left, right);

        private uint LogicalAnd(uint left, uint right) => _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, left, right);

        // A signed immediate widened to the address width.
        private uint SignedOffset64(int offset) => ULong(unchecked((ulong)(long)offset));

        // ---- device addresses ----

        // Resolves a guest address through the page table. A missing page records a
        // fault and resolves to an invalid pointer; an address past the table reads as unmapped.
        private uint _deviceAddressInstructionPc;

        private (uint Pointer, uint Valid) ResolveDeviceAddress(uint address64)
        {
            var masked = And64(address64, ULong(DeviceAddressMask));
            var pageIndex64 = _module.AddInstruction(SpirvOp.ShiftRightLogical, _ulongType, masked, ULong(DeviceAddressPageBits));
            var tableLength = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _pageTable, 0);
            var inTable = ULessThan64(pageIndex64, Widen(tableLength));
            var pageIndex = Narrow(pageIndex64);
            Store(_deviceEntryScratch, ULong(0));
            EmitConditional(inTable, () =>
            {
                var entryPointer = _module.AddInstruction(SpirvOp.AccessChain, _storageUlongPointer, _pageTable, UInt(0), pageIndex);
                Store(_deviceEntryScratch, Load(_ulongType, entryPointer));
            });
            var entry = Load(_ulongType, _deviceEntryScratch);
            var mapped = _module.AddInstruction(SpirvOp.INotEqual, _boolType, entry, ULong(0));
            EmitConditional(LogicalAnd(inTable, LogicalNot(mapped)), () =>
            {
                if (_request.TraceDeviceAddressFaults)
                {
                    var length = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _faultBuffer, 0);
                    var recordStart = _module.AddInstruction(SpirvOp.ISub, _uintType, length, UInt(8));
                    var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType,
                        BlockWordPointer(_faultBuffer, recordStart), UInt(1), UInt(0), UInt(0), UInt(1), UInt(0));
                    EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, claim, UInt(0)), () =>
                    {
                        uint[] values = [UInt((uint)_request.Hash), UInt((uint)(_request.Hash >> 32)),
                            UInt(_deviceAddressInstructionPc), Narrow(address64),
                            Narrow(_module.AddInstruction(SpirvOp.ShiftRightLogical, _ulongType, address64, ULong(32))),
                            UInt((uint)_request.Stage), UInt(0)];
                        for (var index = 0; index < values.Length; index++)
                            Store(BlockWordPointer(_faultBuffer, IAdd(recordStart, UInt((uint)index + 1))), values[index]);
                    });
                }
                var word = ShiftRightLogical(pageIndex, UInt(5));
                var bit = ShiftLeftLogical(UInt(1), BitwiseAnd(pageIndex, UInt(31)));
                EmitConditional(IsBlockWordInRange(_faultBuffer, word), () =>
                    _module.AddInstruction(SpirvOp.AtomicOr, _uintType, BlockWordPointer(_faultBuffer, word), UInt(1), UInt(0), bit));
            });
            var pointer = IAdd64(entry, And64(masked, ULong(DeviceAddressPageSize - 1)));
            return (pointer, mapped);
        }

        private uint DeviceWordPointer(uint pointerValue) =>
            _module.AddInstruction(SpirvOp.ConvertUToPtr, _physicalUintPointer, pointerValue);

        // Loads one aligned dword through a device address; an unmapped page reads zero.
        private uint LoadDeviceDword(uint address64)
        {
            var (pointer, valid) = ResolveDeviceAddress(address64);
            Store(_deviceWordScratch, UInt(0));
            EmitConditional(valid, () =>
                Store(_deviceWordScratch, _module.AddInstruction(SpirvOp.Load, _uintType, DeviceWordPointer(pointer), 2u, 4u)));
            return Load(_uintType, _deviceWordScratch);
        }

        private void StoreDeviceDword(uint address64, uint value, uint allowed)
        {
            var (pointer, valid) = ResolveDeviceAddress(address64);
            EmitConditional(LogicalAnd(allowed, valid), () =>
                _module.AddStatement(SpirvOp.Store, DeviceWordPointer(pointer), value, 2u, 4u));
        }

        private void StoreDeviceMaskedWord(uint address64, uint value, uint mask, uint allowed)
        {
            var touched = LogicalAnd(allowed, _module.AddInstruction(SpirvOp.INotEqual, _boolType, mask, UInt(0)));
            EmitConditional(touched, () =>
            {
                var (pointer, valid) = ResolveDeviceAddress(address64);
                EmitConditional(valid, () =>
                {
                    var wordPointer = DeviceWordPointer(pointer);
                    var full = _module.AddInstruction(SpirvOp.IEqual, _boolType, mask, UInt(uint.MaxValue));
                    EmitConditional(
                        full,
                        () => _module.AddStatement(SpirvOp.Store, wordPointer, value, 2u, 4u),
                        () => EmitAtomicWordUpdate(
                            wordPointer,
                            observed => BitwiseOr(
                                BitwiseAnd(observed, _module.AddInstruction(SpirvOp.Not, _uintType, mask)),
                                value)));
                });
            });
        }

        // An unaligned dword assembled from the two aligned dwords it spans.
        private uint LoadUnalignedDeviceWord(uint address64, uint byteCount)
        {
            var alignment = Narrow(And64(address64, ULong(3)));
            var aligned = And64(address64, ULong(~3ul));
            var shift = ShiftLeftLogical(alignment, UInt(3));
            var low = LoadDeviceDword(aligned);
            var crosses = _module.AddInstruction(SpirvOp.UGreaterThan, _boolType, IAdd(alignment, UInt(byteCount)), UInt(4));
            Store(_deviceWordScratch, UInt(0));
            EmitConditional(crosses, () =>
            {
                var high = LoadDeviceDword(IAdd64(aligned, ULong(4)));
                Store(_deviceWordScratch, high);
            });
            var highWord = Load(_uintType, _deviceWordScratch);
            var carry = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, shift, UInt(0)),
                UInt(0),
                ShiftLeftLogical(highWord, _module.AddInstruction(SpirvOp.ISub, _uintType, UInt(32), shift)));
            return BitwiseOr(ShiftRightLogical(low, shift), carry);
        }

        private uint LoadSubdwordDeviceValue(uint address64, uint previous, uint byteCount, bool signExtend, bool d16, bool d16High)
        {
            var width = byteCount * 8;
            var raw = BitwiseAnd(LoadUnalignedDeviceWord(address64, byteCount), UInt(byteCount == 1 ? 0xFFu : 0xFFFFu));
            if (signExtend)
            {
                raw = Bitcast(
                    _uintType,
                    _module.AddInstruction(SpirvOp.BitFieldSExtract, _intType, Bitcast(_intType, raw), UInt(0), UInt(width)));
            }

            if (!d16)
            {
                return raw;
            }

            var half = BitwiseAnd(raw, UInt(0xFFFF));
            return d16High
                ? BitwiseOr(BitwiseAnd(previous, UInt(0x0000_FFFF)), ShiftLeftLogical(half, UInt(16)))
                : BitwiseOr(BitwiseAnd(previous, UInt(0xFFFF_0000)), half);
        }

        // A sub-word store merges into the dwords it touches through the atomic update.
        private void StoreDeviceBytes(uint address64, uint value, uint byteCount, uint sourceShift, uint allowed)
        {
            if (sourceShift != 0)
            {
                value = ShiftRightLogical(value, UInt(sourceShift));
            }

            var elementMask = byteCount == 1 ? 0xFFu : 0xFFFFu;
            var alignment = Narrow(And64(address64, ULong(3)));
            var aligned = And64(address64, ULong(~3ul));
            var shift = ShiftLeftLogical(alignment, UInt(3));
            var carryShift = _module.AddInstruction(SpirvOp.ISub, _uintType, UInt(32), shift);
            var crosses = _module.AddInstruction(SpirvOp.UGreaterThan, _boolType, IAdd(alignment, UInt(byteCount)), UInt(4));
            var masked = BitwiseAnd(value, UInt(elementMask));
            StoreDeviceMaskedWord(aligned, ShiftLeftLogical(masked, shift), ShiftLeftLogical(UInt(elementMask), shift), allowed);
            var carryValue = _module.AddInstruction(SpirvOp.Select, _uintType, crosses, ShiftRightLogical(masked, carryShift), UInt(0));
            var carryMask = _module.AddInstruction(SpirvOp.Select, _uintType, crosses, ShiftRightLogical(UInt(elementMask), carryShift), UInt(0));
            StoreDeviceMaskedWord(IAdd64(aligned, ULong(4)), carryValue, carryMask, allowed);
        }

        // A written access is allowed only inside its handle's tracked range:
        // width <= size, address >= base, address - base <= size - width.
        private uint IsWrittenAccessAllowed(int memoryIndex, uint address64, uint widthBytes)
        {
            if (!_request.WrittenRangeSlotByMemoryIndex.TryGetValue(memoryIndex, out var slot))
            {
                return _module.ConstantBool(false);
            }

            var rangeBase = Pair64(LoadFlattenedWord(UInt(slot)), LoadFlattenedWord(UInt(slot + 1)));
            var rangeSize = Widen(LoadFlattenedWord(UInt(slot + 2)));
            var width = ULong(widthBytes);
            var masked = And64(address64, ULong(DeviceAddressMask));
            var fits = ULessThanEqual64(width, rangeSize);
            var aboveBase = ULessThanEqual64(rangeBase, masked);
            var insideEnd = ULessThanEqual64(ISub64(masked, rangeBase), ISub64(rangeSize, width));
            return LogicalAnd(fits, LogicalAnd(aboveBase, insideEnd));
        }

        // ---- scalar memory ----

        // One dword of a scalar buffer load through the V# in s[descriptor:descriptor+3]:
        // base in dwords 0-1 (48 bits), stride in dword 1 [29:16], records in dword 2.
        // A load past the end of the buffer returns zero, as on hardware.
        private uint LoadDeviceDescriptorBufferWord(uint descriptor, uint byteOffset)
        {
            var word1 = LoadS(descriptor + 1);
            var baseAddress = Pair64(LoadS(descriptor), BitwiseAnd(word1, UInt(0xFFFF)));
            var stride = BitwiseAnd(ShiftRightLogical(word1, UInt(16)), UInt(0x3FFF));
            var records = Widen(LoadS(descriptor + 2));
            var size = _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, stride, UInt(0)),
                records,
                _module.AddInstruction(SpirvOp.IMul, _ulongType, records, Widen(stride)));
            var aligned = Widen(BitwiseAnd(byteOffset, UInt(~3u)));
            var inRange = ULessThan64(IAdd64(aligned, ULong(sizeof(uint) - 1)), size);
            Store(_deviceBufferWordScratch, UInt(0));
            EmitConditional(inRange, () =>
                Store(_deviceBufferWordScratch, LoadDeviceDword(And64(IAdd64(baseAddress, aligned), ULong(DeviceAddressMask & ~3ul)))));
            return Load(_uintType, _deviceBufferWordScratch);
        }

        private bool TryEmitLayoutScalarMemory(Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control, out string error)
        {
            _deviceAddressInstructionPc = instruction.Pc;
            error = string.Empty;
            var request = _request;
            var dynamicOffset = control.DynamicOffsetRegister is { } register ? LoadS(register) : UInt(0);
            uint? deviceAddress = null;
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
                    Store(_indirectKeyScratch[memoryIndex], dynamicOffset);
                uint value;
                if (entry.PlanningOnly)
                {
                    // A planning-only read without a slot is a heap descriptor the host resolved; nothing is emitted.
                    if (!request.FlattenedSlotByMemoryIndex.TryGetValue(memoryIndex, out var slot))
                    {
                        continue;
                    }

                    value = LoadFlattenedWord(UInt(slot));
                }
                else if (entry.Kind == MemoryResourceKind.ScalarBuffer && entry.DeviceDescriptor)
                {
                    if (instruction.Sources.Count == 0 || instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
                    {
                        error = "invalid scalar-buffer descriptor";
                        return false;
                    }

                    value = LoadDeviceDescriptorBufferWord(
                        instruction.Sources[0].Value,
                        IAdd(dynamicOffset, UInt(unchecked((uint)control.ImmediateOffsetBytes + (uint)component * sizeof(uint)))));
                }
                else if (entry.Kind == MemoryResourceKind.ScalarBuffer)
                {
                    if (entry.Resource == MemoryAccessInfo.NoResource)
                    {
                        error = "scalar buffer load has no dense buffer";
                        return false;
                    }

                    var bindingIndex = (int)entry.Resource;
                    var byteAddress = IAdd(dynamicOffset, UInt(unchecked((uint)control.ImmediateOffsetBytes + (uint)component * sizeof(uint))));
                    byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
                    value = LoadBufferWord(bindingIndex, ShiftRightLogical(byteAddress, UInt(2)));
                }
                else
                {
                    if (instruction.Sources.Count == 0 || instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
                    {
                        error = "invalid scalar-memory address";
                        return false;
                    }

                    if (deviceAddress is null)
                    {
                        var baseRegister = instruction.Sources[0].Value;
                        var address = IAdd64(
                            Pair64(LoadS(baseRegister), LoadS(baseRegister + 1)),
                            IAdd64(Widen(dynamicOffset), SignedOffset64(control.ImmediateOffsetBytes)));
                        deviceAddress = And64(address, ULong(DeviceAddressMask & ~3ul));
                    }

                    value = LoadDeviceDword(component == 0 ? deviceAddress.Value : IAdd64(deviceAddress.Value, ULong((ulong)component * sizeof(uint))));
                }

                if (!request.IndirectOffsetKeyMemoryIndices.Contains(memoryIndex) &&
                    _indirectKeyScratch.TryGetValue(memoryIndex, out var keyScratch))
                {
                    Store(keyScratch, value);
                }

                StoreS(destination.Value, value);
            }

            return true;
        }

        // ---- global memory ----

        private bool TryEmitLayoutGlobalMemory(Gen5ShaderInstruction instruction, Gen5GlobalMemoryControl control, out string error)
        {
            _deviceAddressInstructionPc = instruction.Pc;
            error = string.Empty;
            if (instruction.Opcode.StartsWith("Scratch", StringComparison.Ordinal))
            {
                return TryEmitScratchMemory(instruction, control, out error);
            }

            var request = _request;
            if (!request.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                error = "global access has no memory record";
                return false;
            }

            var memoryOpcode = control.UsesFlatAddress ? "Global" + instruction.Opcode["Flat".Length..] : instruction.Opcode;
            uint address;
            if (control.UsesFlatAddress || control.ScalarAddress >= 125)
            {
                address = Pair64(LoadV(control.VectorAddress), LoadV(control.VectorAddress + 1));
            }
            else
            {
                address = IAdd64(Pair64(LoadS(control.ScalarAddress), LoadS(control.ScalarAddress + 1)), Widen(LoadV(control.VectorAddress)));
            }

            address = IAdd64(address, SignedOffset64(control.OffsetBytes));
            var entry = request.Memory[memoryIndex];
            var writes = entry.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            var accessBytes = Math.Max((entry.DataBits + 7) / 8, 1u) * Math.Max(entry.DataDwords, 1u);
            var allowed = writes ? IsWrittenAccessAllowed(memoryIndex, address, accessBytes) : _module.ConstantBool(true);

            if (memoryOpcode.StartsWith("GlobalAtomic", StringComparison.Ordinal))
            {
                if (!TryGetAtomicOp(memoryOpcode["GlobalAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported global opcode {instruction.Opcode}";
                    return false;
                }

                EmitExecConditional(() =>
                {
                    EmitConditional(allowed, () =>
                    {
                        var (pointer, valid) = ResolveDeviceAddress(address);
                        EmitConditional(valid, () =>
                        {
                            var original = EmitAtomic(
                                atomicOp,
                                _uintType,
                                DeviceWordPointer(pointer),
                                scope: 1,
                                semantics: 0x48,
                                value: () => LoadV(control.SourceVectorRegister),
                                comparator: () => LoadV(control.SourceVectorRegister + 1));
                            if (control.Glc)
                            {
                                StoreV(control.DestinationVectorRegister, original);
                            }
                        });
                    });
                });
                return true;
            }

            if (memoryOpcode.StartsWith("GlobalStore", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (TryGetSubdwordStoreInfo(memoryOpcode, out var byteCount, out var sourceShift))
                    {
                        StoreDeviceBytes(address, LoadV(control.SourceVectorRegister), byteCount, sourceShift, allowed);
                        return;
                    }

                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        StoreDeviceDword(
                            index == 0 ? address : IAdd64(address, ULong((ulong)index * sizeof(uint))),
                            LoadV(control.SourceVectorRegister + index),
                            allowed);
                    }
                });
                return true;
            }

            // Inactive lanes must not access memory or record page faults.
            EmitExecConditional(() =>
            {
                if (TryGetSubdwordLoadInfo(memoryOpcode, out var loadByteCount, out var signExtend, out var d16, out var d16High))
                {
                    StoreV(
                        control.DestinationVectorRegister,
                        LoadSubdwordDeviceValue(address, LoadV(control.DestinationVectorRegister), loadByteCount, signExtend, d16, d16High));
                    return;
                }

                var aligned = And64(address, ULong(~3ul));
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    StoreV(
                        control.DestinationVectorRegister + index,
                        LoadDeviceDword(index == 0 ? aligned : IAdd64(aligned, ULong((ulong)index * sizeof(uint)))));
                }
            });

            return true;
        }

        private bool TryEmitScratchMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_scratch == 0 || _scratchDwordCount == 0)
            {
                error = "scratch storage was not declared";
                return false;
            }

            var address = control.ScalarAddress < 125
                ? LoadS(control.ScalarAddress)
                : LoadV(control.VectorAddress);
            if (control.OffsetBytes != 0)
            {
                address = IAdd(address, UInt(unchecked((uint)control.OffsetBytes)));
            }

            if (instruction.Opcode.StartsWith("ScratchStore", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (TryGetSubdwordStoreInfo(instruction.Opcode, out var byteCount, out var sourceShift))
                    {
                        StoreScratchBytes(address, LoadV(control.SourceVectorRegister), byteCount, sourceShift);
                        return;
                    }

                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        Store(
                            ScratchPointer(address, index * sizeof(uint)),
                            LoadV(control.SourceVectorRegister + index));
                    }
                });
                return true;
            }

            if (!instruction.Opcode.StartsWith("ScratchLoad", StringComparison.Ordinal))
            {
                error = $"unsupported scratch opcode {instruction.Opcode}";
                return false;
            }

            EmitExecConditional(() =>
            {
                if (TryGetSubdwordLoadInfo(instruction.Opcode, out var byteCount, out var signExtend, out var d16, out var d16High))
                {
                    var destination = control.DestinationVectorRegister;
                    var value = LoadScratchBytes(address, byteCount, signExtend);
                    if (d16)
                    {
                        value = d16High
                            ? BitwiseOr(BitwiseAnd(LoadV(destination), UInt(0x0000_FFFF)), ShiftLeftLogical(BitwiseAnd(value, UInt(0xFFFF)), UInt(16)))
                            : BitwiseOr(BitwiseAnd(LoadV(destination), UInt(0xFFFF_0000)), BitwiseAnd(value, UInt(0xFFFF)));
                    }

                    StoreV(destination, value);
                    return;
                }

                for (uint index = 0; index < control.DwordCount; index++)
                {
                    StoreV(
                        control.DestinationVectorRegister + index,
                        Load(_uintType, ScratchPointer(address, index * sizeof(uint))));
                }
            });
            return true;
        }

        private uint ScratchPointer(uint byteAddress, uint offsetBytes = 0)
        {
            var address = offsetBytes == 0
                ? byteAddress
                : IAdd(byteAddress, UInt(offsetBytes));
            var dwordIndex = ShiftRightLogical(address, UInt(2));
            if (_scratchDwordCount > 1)
            {
                dwordIndex = _module.AddInstruction(
                    SpirvOp.UMod,
                    _uintType,
                    dwordIndex,
                    UInt(_scratchDwordCount));
            }

            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _scratchElementPointer,
                _scratch,
                dwordIndex);
        }

        private uint LoadScratchBytes(uint byteAddress, uint byteCount, bool signExtend)
        {
            var shift = ShiftLeftLogical(BitwiseAnd(byteAddress, UInt(3)), UInt(3));
            var value = BitwiseAnd(
                ShiftRightLogical(Load(_uintType, ScratchPointer(byteAddress)), shift),
                UInt(byteCount == 1 ? 0xFFu : 0xFFFFu));
            if (signExtend)
            {
                value = Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, value),
                        UInt(0),
                        UInt(byteCount * 8)));
            }

            return value;
        }

        private void StoreScratchBytes(uint byteAddress, uint value, uint byteCount, uint sourceShift)
        {
            if (sourceShift != 0)
            {
                value = ShiftRightLogical(value, UInt(sourceShift));
            }

            var pointer = ScratchPointer(byteAddress);
            var shift = ShiftLeftLogical(BitwiseAnd(byteAddress, UInt(3)), UInt(3));
            var elementMask = UInt(byteCount == 1 ? 0xFFu : 0xFFFFu);
            var shiftedMask = ShiftLeftLogical(elementMask, shift);
            var merged = BitwiseOr(
                BitwiseAnd(
                    Load(_uintType, pointer),
                    _module.AddInstruction(SpirvOp.Not, _uintType, shiftedMask)),
                ShiftLeftLogical(BitwiseAnd(value, elementMask), shift));
            Store(pointer, merged);
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
        private bool TryGetImageElementCases(Gen5ShaderInstruction instruction, Gen5ImageControl image, out uint selector, out IReadOnlyList<(uint Resource, uint Element)> elements, out string error)
        {
            selector = 0;
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
                var coordinateComponentCount = imageClass.Arrayed ? ImageSpatialComponentCountOf(imageClass.Dimension) + 1 : ImageSpatialComponentCountOf(imageClass.Dimension);
                selector = LoadImageIntegerAddress(image, (int)coordinateComponentCount);
                elements = Enumerable.Range(0, (int)imageInfo.MipCount)
                    .Select(mip => ((uint)resourceIndex, (uint)element + (uint)mip)).ToList();
                return true;
            }

            if (request.IndirectRootByMemoryIndex.TryGetValue(memoryIndex, out var keyMemoryIndex) && imageInfo.IndirectSearchIterations != 0)
            {
                if (!HasFlattenedTable)
                {
                    error = "indirect image access without a flattened table binding";
                    return false;
                }

                var candidates = info.Images[(int)imageInfo.IndirectRoot].IndirectResources;
                var candidateElements = new List<(uint Resource, uint Element)>();
                foreach (var candidate in candidates)
                {
                    if (candidate >= info.Images.Count)
                    {
                        error = $"indirect candidate {candidate} is outside the image table";
                        return false;
                    }
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

        private bool HasFlattenedTable => _flattenedTable != 0;

        // Resolves the image an instruction reads through its class array, joined with
        // its sampler for sampling operations.
        private bool TryResolveLayoutImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out SpirvImageResource resource,
            out uint imageObject,
            out uint dstSelect,
            out string error,
            (uint Resource, uint Element)? fixedElement = null)
        {
            error = string.Empty;
            resource = default;
            imageObject = 0;
            dstSelect = DescriptorConstants.IdentityImageSwizzle;
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
            if ((uint)resourceIndex >= info.Images.Count)
            {
                error = $"image {resourceIndex} is outside the image table";
                return false;
            }
            var imageInfo = info.Images[resourceIndex];
            var kind = ImageDescriptorBinding.ForImage(imageInfo);
            if (kind is null || !_imageClasses.TryGetValue(kind.Value, out var imageClass))
            {
                error = $"image {resourceIndex} has no declared binding class";
                return false;
            }

            var element = imageClass.Resources.ToList().IndexOf((uint)resourceIndex);
            if (element < 0)
            {
                error = $"image {resourceIndex} is not an element of {kind.Value}";
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

            var elementIndex = UInt(fixedElement?.Element ?? (uint)element);
            var elementPointer = _module.AddInstruction(SpirvOp.AccessChain, imageClass.ElementPointer, imageClass.Variable, elementIndex);
            var imageValue = Load(imageClass.ImageType, elementPointer);
            uint objectType;
            if (UsesSampler(instruction.Opcode))
            {
                if (_samplerArray == 0)
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

                var samplerPointer = _module.AddInstruction(SpirvOp.AccessChain, _samplerPointer, _samplerArray, UInt(samplerIndex));
                var sampler = Load(_samplerType, samplerPointer);
                objectType = _module.TypeSampledImage(imageClass.ImageType);
                imageObject = _module.AddInstruction(SpirvOp.SampledImage, objectType, imageValue, sampler);
            }
            else
            {
                objectType = imageClass.ImageType;
                imageObject = imageValue;
            }

            resource = new SpirvImageResource(
                elementPointer,
                imageClass.ImageType,
                objectType,
                imageClass.ComponentType,
                _module.TypeVector(imageClass.ComponentType, 4),
                imageClass.Kind,
                imageClass.IsStorage,
                imageClass.Arrayed,
                imageClass.Cube,
                imageClass.Multisampled,
                imageClass.Dimension,
                imageInfo.ConversionFormat,
                imageInfo.ShaderSwizzle,
                imageInfo.EmulatedCompareFunction);
            dstSelect = imageInfo.ShaderSwizzle;
            return true;
        }

        private static uint ImageSpatialComponentCountOf(SpirvImageDim dimension) => dimension switch
        {
            SpirvImageDim.Dim1D => 1u,
            SpirvImageDim.Dim3D => 3u,
            _ => 2u,
        };

        // Searches the sorted key mapping of an indirect table for the captured key; the
        // result is the candidate-local index, candidate 0 when the key is absent or out of range.
        private uint SelectIndirectCandidate(ImageResource imageInfo, int keyMemoryIndex, uint candidateCount)
        {
            var key = _indirectKeyScratch.TryGetValue(keyMemoryIndex, out var scratch) ? Load(_uintType, scratch) : UInt(0);
            var mapping = UInt(imageInfo.IndirectMappingOffset);
            var count = LoadFlattenedWord(mapping);
            var low = UInt(0);
            var high = count;
            for (uint iteration = 0; iteration < imageInfo.IndirectSearchIterations; iteration++)
            {
                var span = _module.AddInstruction(SpirvOp.ISub, _uintType, high, low);
                var middle = IAdd(low, ShiftRightLogical(span, UInt(1)));
                var probeSlot = IAdd(IAdd(mapping, UInt(1)), ShiftLeftLogical(middle, UInt(1)));
                var probeKey = LoadFlattenedWord(probeSlot);
                var moveUp = LogicalAnd(
                    _module.AddInstruction(SpirvOp.ULessThan, _boolType, probeKey, key),
                    _module.AddInstruction(SpirvOp.ULessThan, _boolType, low, high));
                low = _module.AddInstruction(SpirvOp.Select, _uintType, moveUp, IAdd(middle, UInt(1)), low);
                high = _module.AddInstruction(SpirvOp.Select, _uintType, moveUp, high, middle);
            }

            var foundSlot = IAdd(IAdd(mapping, UInt(1)), ShiftLeftLogical(low, UInt(1)));
            var found = LogicalAnd(
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, low, count),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, LoadFlattenedWord(foundSlot), key));
            // The mapping holds candidate-local indices: the root's candidate list in order.
            var mapped = LoadFlattenedWord(IAdd(foundSlot, UInt(1)));
            var inRange = LogicalAnd(found, _module.AddInstruction(SpirvOp.ULessThan, _boolType, mapped, UInt(candidateCount)));
            return _module.AddInstruction(SpirvOp.Select, _uintType, inRange, mapped, UInt(0));
        }

        // ---- global data share ----

        // DS operations with the GDS bit run over the shared storage buffer with device
        // scope; lane operations have no GDS form.
        private bool TryEmitGlobalDataShare(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (_globalDataShare == 0)
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
                    EmitExecConditional(() => StoreGlobalDataShareWord(GlobalDataShareIndex(GetRawSource(instruction, 0), control.SingleOffsetBytes), GetRawSource(instruction, 1)));
                    return true;
                case "DsWriteB64":
                    EmitExecConditional(() =>
                    {
                        var index = GlobalDataShareIndex(GetRawSource(instruction, 0), control.SingleOffsetBytes);
                        StoreGlobalDataShareWord(index, GetRawSource(instruction, 1));
                        StoreGlobalDataShareWord(IAdd(index, UInt(1)), GetRawSource(instruction, 2));
                    });
                    return true;
                case "DsWrite2B64":
                case "DsWrite2St64B64":
                    return TryEmitDataShareWritePair64(instruction, control, out error);
                case "DsWrite2B32":
                    EmitExecConditional(() =>
                    {
                        var address = GetRawSource(instruction, 0);
                        StoreGlobalDataShareWord(GlobalDataShareIndex(address, control.Offset0 * sizeof(uint)), GetRawSource(instruction, 1));
                        StoreGlobalDataShareWord(GlobalDataShareIndex(address, control.Offset1 * sizeof(uint)), GetRawSource(instruction, 2));
                    });
                    return true;
                case "DsReadB32":
                    StoreV(instruction.Destinations[0].Value, LoadBlockWord(_globalDataShare, GlobalDataShareIndex(GetRawSource(instruction, 0), control.SingleOffsetBytes)));
                    return true;
                case "DsReadI8":
                {
                    var address = GetRawSource(instruction, 0);
                    var byteAddress = control.SingleOffsetBytes == 0
                        ? address
                        : IAdd(address, UInt(control.SingleOffsetBytes));
                    var word = LoadBlockWord(_globalDataShare, GlobalDataShareIndex(address, control.SingleOffsetBytes));
                    var shift = ShiftLeftLogical(BitwiseAnd(byteAddress, UInt(3)), UInt(3));
                    var packed = ShiftRightLogical(word, shift);
                    var signedByte = _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, packed),
                        UInt(0),
                        UInt(8));
                    StoreV(instruction.Destinations[0].Value, Bitcast(_uintType, signedByte));
                    return true;
                }
                case "DsReadB64":
                {
                    var index = GlobalDataShareIndex(GetRawSource(instruction, 0), control.SingleOffsetBytes);
                    StoreV(instruction.Destinations[0].Value, LoadBlockWord(_globalDataShare, index));
                    StoreV(instruction.Destinations[1].Value, LoadBlockWord(_globalDataShare, IAdd(index, UInt(1))));
                    return true;
                }
                case "DsRead2B64":
                    return TryEmitDataShareReadPair64(instruction, control, out error);
                case "DsRead2B32":
                {
                    var address = GetRawSource(instruction, 0);
                    StoreV(instruction.Destinations[0].Value, LoadBlockWord(_globalDataShare, GlobalDataShareIndex(address, control.Offset0 * sizeof(uint))));
                    StoreV(instruction.Destinations[1].Value, LoadBlockWord(_globalDataShare, GlobalDataShareIndex(address, control.Offset1 * sizeof(uint))));
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

            EmitExecConditional(() =>
            {
                var address = GetRawSource(instruction, 0);
                // Equal offsets select only the first source pair.
                var componentCount = control.Offset0 == control.Offset1 ? 2 : 4;
                for (var component = 0; component < componentCount; component++)
                {
                    var pairOffset = component < 2 ? control.Offset0 : control.Offset1;
                    var byteOffset = pairOffset * offsetStride + (uint)(component % 2) * sizeof(uint);
                    var value = GetRawSource(instruction, component + 1);
                    if (control.Gds)
                        StoreGlobalDataShareWord(GlobalDataShareIndex(address, byteOffset), value);
                    else
                        StoreLds(LdsPointer(address, byteOffset), value);
                }
            });
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

            EmitExecConditional(() =>
            {
                var address = GetRawSource(instruction, 0);
                var values = new uint[4];
                // Capture both values before an overlapping destination changes the address.
                for (var component = 0; component < values.Length; component++)
                {
                    var pairOffset = component < 2 ? control.Offset0 : control.Offset1;
                    var byteOffset = pairOffset * sizeof(ulong) + (uint)(component % 2) * sizeof(uint);
                    values[component] = control.Gds
                        ? LoadBlockWord(_globalDataShare, GlobalDataShareIndex(address, byteOffset))
                        : Load(_uintType, LdsPointer(address, byteOffset));
                }
                for (var component = 0; component < values.Length; component++)
                    StoreV(instruction.Destinations[component].Value, values[component], guardWithExec: false);
            });
            return true;
        }

        private uint GlobalDataShareIndex(uint address, uint offsetBytes) =>
            ShiftRightLogical(offsetBytes == 0 ? address : IAdd(address, UInt(offsetBytes)), UInt(2));

        private void StoreGlobalDataShareWord(uint index, uint value) =>
            EmitConditional(IsBlockWordInRange(_globalDataShare, index), () => Store(BlockWordPointer(_globalDataShare, index), value));

        private bool TryEmitGlobalDataShareAtomic(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Opcode is "DsMinF32" or "DsMaxF32")
            {
                if (instruction.Sources.Count < 3)
                {
                    error = $"missing GDS operands for {instruction.Opcode}";
                    return false;
                }

                var floatIndex = GlobalDataShareIndex(GetRawSource(instruction, 0), control.SingleOffsetBytes);
                EmitExecConditional(() =>
                {
                    EmitConditional(IsBlockWordInRange(_globalDataShare, floatIndex), () =>
                        EmitDataShareFloatAtomic(
                            BlockWordPointer(_globalDataShare, floatIndex),
                            GetRawSource(instruction, 1),
                            GetRawSource(instruction, 2),
                            instruction.Opcode == "DsMaxF32",
                            scope: 1,
                            semantics: 0x48));
                });
                return true;
            }

            var atomicOp = instruction.Opcode switch
            {
                "DsAddU32" or "DsAddRtnU32" => SpirvOp.AtomicIAdd,
                "DsSubU32" or "DsSubRtnU32" => SpirvOp.AtomicISub,
                "DsIncU32" or "DsIncRtnU32" => SpirvOp.AtomicIIncrement,
                "DsDecU32" or "DsDecRtnU32" => SpirvOp.AtomicIDecrement,
                "DsMinI32" or "DsMinRtnI32" => SpirvOp.AtomicSMin,
                "DsMaxI32" or "DsMaxRtnI32" => SpirvOp.AtomicSMax,
                "DsMinU32" or "DsMinRtnU32" => SpirvOp.AtomicUMin,
                "DsMaxU32" or "DsMaxRtnU32" => SpirvOp.AtomicUMax,
                "DsAndB32" or "DsAndRtnB32" => SpirvOp.AtomicAnd,
                "DsOrB32" or "DsOrRtnB32" => SpirvOp.AtomicOr,
                "DsXorB32" or "DsXorRtnB32" => SpirvOp.AtomicXor,
                "DsWrxchgRtnB32" => SpirvOp.AtomicExchange,
                "DsCmpstB32" or "DsCmpstRtnB32" => SpirvOp.AtomicCompareExchange,
                _ => SpirvOp.Nop,
            };
            if (atomicOp == SpirvOp.Nop)
            {
                error = $"unsupported GDS opcode {instruction.Opcode}";
                return false;
            }

            var index = GlobalDataShareIndex(GetRawSource(instruction, 0), control.SingleOffsetBytes);
            EmitExecConditional(() =>
            {
                EmitConditional(IsBlockWordInRange(_globalDataShare, index), () =>
                {
                    var original = EmitAtomic(
                        atomicOp,
                        _uintType,
                        BlockWordPointer(_globalDataShare, index),
                        scope: 1,
                        semantics: 0x48,
                        value: () => GetRawSource(instruction, atomicOp == SpirvOp.AtomicCompareExchange ? 2 : 1),
                        comparator: () => GetRawSource(instruction, 1));
                    if (instruction.Destinations.Count > 0)
                    {
                        StoreV(instruction.Destinations[0].Value, original);
                    }
                });
            });
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
            var m0 = GetRawSource(instruction, 0);
            var baseAddress = ShiftRightLogical(m0, UInt(16));
            var sizeBytes = BitwiseAnd(m0, UInt(0xFFFF));
            var index = GlobalDataShareIndex(baseAddress, offset);
            var inBounds = LogicalAnd(IsNotZero(sizeBytes), IsBlockWordInRange(_globalDataShare, index));
            var destination = instruction.Destinations[0].Value;
            var active = Load(_boolType, _exec);
            var activeMask = BooleanToWaveMask(active);
            var activeLow = Narrow(activeMask);
            var activeHigh = Narrow(ShiftRightLogical64(activeMask, ULong(32)));
            var activeCount = IAdd(
                _module.AddInstruction(SpirvOp.BitCount, _uintType, activeLow),
                _module.AddInstruction(SpirvOp.BitCount, _uintType, activeHigh));
            var firstLane = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                IsNotZero(activeLow),
                Ext(73, _uintType, activeLow),
                IAdd(Ext(73, _uintType, activeHigh), UInt(32)));
            var isFirstActive = LogicalAnd(inBounds, LogicalAnd(active, _module.AddInstruction(SpirvOp.IEqual, _boolType, GuestWaveLane(), firstLane)));
            EmitConditional(isFirstActive, () =>
            {
                var original = EmitAtomic(
                    instruction.Opcode == "DsAppend" ? SpirvOp.AtomicIAdd : SpirvOp.AtomicISub,
                    _uintType,
                    BlockWordPointer(_globalDataShare, index),
                    scope: 1,
                    semantics: 0x48,
                    value: () => activeCount,
                    comparator: () => UInt(0));
                StoreV(destination, original);
            });

            var firstValue = LoadV(destination);
            uint broadcast;
            if (_emulateWave64)
            {
                EmitConditional(isFirstActive, () => Store(WaveBroadcastScratchPointer(), firstValue));
                EmitWave64Barrier();
                broadcast = Load(_uintType, WaveBroadcastScratchPointer());
                EmitWave64Barrier();
            }
            else if (_stage == Gen5SpirvStage.Compute || _enableGraphicsSubgroupOperations)
            {
                // Every active lane receives the first lane's old counter, in graphics stages too.
                broadcast = _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType, UInt(3), firstValue, firstLane);
            }
            else
            {
                // Without subgroup operations a graphics invocation is its own wave of one lane.
                broadcast = firstValue;
            }

            var validResult = LogicalAnd(inBounds, IsNotZero64(activeMask));
            StoreV(destination, _module.AddInstruction(SpirvOp.Select, _uintType, validResult, broadcast, UInt(0)));
            return true;
        }
    }
}
