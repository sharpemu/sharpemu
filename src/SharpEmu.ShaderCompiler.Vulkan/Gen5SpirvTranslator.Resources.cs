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
        var profile = new ShaderTranslationProfile.Scope(ShaderTranslationProfile.Part.Validation);
        try { return TryCompileProfiled(request, out shader, out error, ref profile); }
        finally { profile.Dispose(); }
    }

    private static bool TryCompileProfiled(ShaderCompileRequest request, out Gen5SpirvShader shader,
        out string error, ref ShaderTranslationProfile.Scope profile)
    {
        shader = default!;
        if (request.CooperativeWave64Workgroup && (request.Stage != ShaderStage.Compute || request.WaveSize != 64 ||
            (ulong)request.LocalSizeX * request.LocalSizeY * request.LocalSizeZ is 0 or > 256 ||
            (request.LocalSizeX * request.LocalSizeY * request.LocalSizeZ) % 64 != 0))
        {
            error = "Cooperative wave64 requires a compute workgroup containing one to four complete waves.";
            return false;
        }

        if (request.TessellationHull is not null &&
            (request.Stage != ShaderStage.Compute || !request.CooperativeWave64Workgroup || !request.Bindings.UsesTessellationData))
        {
            error = "A merged hull program requires cooperative compute execution and tessellation runtime data.";
            return false;
        }

        if (request.Stage == ShaderStage.TessellationEvaluation &&
            (request.Tessellation is not { } tessellation ||
             !Enum.IsDefined(tessellation.Domain) || !Enum.IsDefined(tessellation.Spacing) ||
             tessellation.Spacing == Gen5TessellationSpacing.PowerOfTwo))
        {
            error = "Tessellation evaluation requires a supported tessellation configuration; power-of-two partitioning needs factor conversion.";
            return false;
        }

        try
        {
            BindingLayoutValidator.Validate(
                request.Bindings,
                request.Resources.Info,
                BindingLayout.CollectUserDataRegisters(request.Program, request.UserDataBase, request.UserDataCount,
                    request.ExcludedUserDataRegisters),
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
            if (request.PixelOutputs.Any(output => output.DualSourceAlpha) &&
                (request.PixelOutputs.Count != 1 || request.PixelOutputs[0].Index != 0 ||
                 request.PixelOutputs[0].HostLocation != 0 || request.PixelOutputs[0].Kind != Gen5PixelOutputKind.Float))
            {
                error = "synthetic alpha requires one floating-point primary output at location zero";
                return false;
            }
            var dualSource = request.PixelOutputs.Any(output => output.Index != 0);
            if (dualSource && (request.PixelOutputs.Count != 2 ||
                request.PixelOutputs.Any(output => output.HostLocation != 0 || output.Kind != Gen5PixelOutputKind.Float) ||
                !request.PixelOutputs.Select(output => output.Index).Order().SequenceEqual(new uint[] { 0, 1 })))
            {
                error = "dual-source pixel outputs require a primary and second source at host location zero with indices zero and one";
                return false;
            }
            if (request.PixelOutputs.Count > 8 || request.PixelOutputs.Any(output => output.GuestSlot > 7))
            {
                error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
                return false;
            }

            // A dual-source second output shares location 0 with index 1; every other output
            // has index 0 and its own location.
            var targets = request.PixelOutputs.Count(output => output.Index == 0);
            if (request.PixelOutputs.Select(output => output.GuestSlot).Distinct().Count() != request.PixelOutputs.Count ||
                request.PixelOutputs.Select(output => (output.HostLocation, output.Index)).Distinct().Count() != request.PixelOutputs.Count)
            {
                error = "pixel output guest slots and host locations must be unique";
                return false;
            }

            if (request.PixelOutputs.Any(output => output.HostLocation >= targets || output.Index > 1 ||
                    (output.Index == 1 && (output.HostLocation != 0 || targets != 1))))
            {
                error = "pixel output host locations must be dense in the 0..N-1 range, with a second source only at location 0 of one target";
                return false;
            }
        }

        profile.Switch(ShaderTranslationProfile.Part.ContextSetup);
        var callArguments = Gen5ShaderCallLinker.CallEntryArguments(request.Program);
        if (request.Bindings.CallArgumentCount != 0 && callArguments.Length != request.Bindings.CallArgumentCount)
        {
            error = $"the layout holds {request.Bindings.CallArgumentCount} call arguments for {callArguments.Length}";
            return false;
        }

        // Slots come from the linked program before its bodies are shared, as the host's layout does.
        var callTargets = Gen5ShaderCallLinker.CallTargets(request.Program);
        if (request.Bindings.CallTargetCount != 0 && callTargets.Length != request.Bindings.CallTargetCount)
        {
            error = $"the layout holds {request.Bindings.CallTargetCount} call targets for {callTargets.Length} callees";
            return false;
        }

        if (Gen5ShaderCallLinker.IsLinked(request.Program) && request.Bindings.Find(DescriptorBindingKind.DeviceAddressPageTable) is not null &&
            request.Bindings.Find(DescriptorBindingKind.DeviceAddressFallback) is null)
        {
            error = "the layout of a program with shader calls holds no device address fallback";
            return false;
        }

        request = ShareLinkedCallBodies(request, out var sharedCalls);
        return new CompilationContext(request, sharedCalls, request.Bindings.CallArgumentCount != 0 ? callArguments : null,
                request.Bindings.CallTargetCount != 0 ? callTargets : null)
            .TryCompile(out shader, out error, ref profile);
    }

    private sealed partial class CompilationContext
    {
        private readonly ShaderCompileRequest _request;
        // A program with shader calls reads device addresses and scratch without branches (ResolveCompactDeviceAddress): the
        // driver compiled the conditional forms several times slower in large linked modules. Every other program keeps them.
        private readonly bool _compactMemoryAccess;
        private readonly Dictionary<DescriptorBindingKind, LayoutImageClass> _imageClasses = [];
        private readonly Dictionary<int, uint> _indirectKeyScratch = [];
        private readonly Dictionary<int, uint> _heapArrays = [];
        private uint _pushData;
        private uint _shaderData;
        private uint _mipStatistics;
        private uint _helperInvocationInput;
        private uint _flattenedTable;
        private uint _pageTable;
        private uint _faultBuffer;
        private uint _globalDataShare;
        private uint _samplerArray;
        private uint _samplerType;
        private uint _samplerPointer;
        private uint _storageUlongPointer;
        private uint _physicalUintPointer;
        private uint _privateUlongPointer;
        private uint _tableBlock;
        private uint _tableBufferBases;
        private uint _tableBufferLengths;
        private uint _tableFlattenedLength;
        private readonly HashSet<uint> _physicalPointers = [];
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

        public CompilationContext(ShaderCompileRequest request, SharedCallBodies? sharedCalls = null,
            ulong[]? callArguments = null, ulong[]? callTargets = null)
        {
            _request = request;
            _compactMemoryAccess = Gen5ShaderCallLinker.IsLinked(request.Program);
            _sharedCalls = sharedCalls;
            _callArgumentSlots = callArguments?.Select((argument, slot) => (argument, slot)).ToDictionary(entry => entry.argument, entry => entry.slot);
            _callTargetSlots = callTargets?.Select((address, slot) => (address, slot)).ToDictionary(entry => entry.address, entry => entry.slot);
            _hasIndirectControlFlow = request.Program.Instructions.Any(
                static instruction => instruction.Opcode is "SSetpcB64" or "SSwappcB64");
            _usesBufferAtomics = request.Program.Instructions.Any(static instruction =>
                instruction.Opcode.StartsWith("BufferAtomic", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("GlobalAtomic", StringComparison.Ordinal));
            _usesCoherentBufferPairs = request.Program.Instructions.Any(static instruction =>
                instruction.Opcode is "BufferLoadDwordx2" or "BufferStoreDwordx2" &&
                instruction.Control is Gen5BufferMemoryControl { Glc: true });
            _stage = request.Stage switch
            {
                ShaderStage.Vertex => Gen5SpirvStage.Vertex,
                ShaderStage.Pixel => Gen5SpirvStage.Pixel,
                ShaderStage.Mesh => Gen5SpirvStage.Mesh,
                ShaderStage.TessellationEvaluation => Gen5SpirvStage.TessellationEvaluation,
                _ => Gen5SpirvStage.Compute,
            };
            _pixelOutputBindings = request.PixelOutputs;
            _usesPixelValidMask =
                _stage == Gen5SpirvStage.Pixel &&
                request.Program.Instructions.Any(static instruction => instruction.Control is Gen5ExportControl { ValidMask: true });
            _pixelExecExcludesHelpers =
                _stage == Gen5SpirvStage.Pixel && PixelLiveExec &&
                request.Program.Instructions.Any(static instruction => instruction.Opcode is "DsAppend" or "DsConsume");
            _enableGraphicsSubgroupOperations = _stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh || request.EnableGraphicsSubgroupOperations;
            _waveLaneCount = request.WaveSize == 64 ? 64u : 32u;
            _pairedMeshLanes = _stage == Gen5SpirvStage.Mesh && _waveLaneCount == 64 &&
                request.Mesh?.DeviceSubgroupLaneCount == 32;
            _localSizeX = Math.Max(request.LocalSizeX, 1);
            _localSizeY = Math.Max(request.LocalSizeY, 1);
            _localSizeZ = Math.Max(request.LocalSizeZ, 1);
            _physicalAxisOfLogical = ComputeWorkgroupAxisOrder(_localSizeX, _localSizeY, _localSizeZ);
            var systemRegisters = request.ComputeSystemRegisters;
            _physicalGroupAxisOfLogical = _stage == Gen5SpirvStage.Compute
                ? ComputeGroupAxisOrder(_localSizeX, _localSizeY, _localSizeZ,
                    systemRegisters?.WorkGroupXRegister is not null, systemRegisters?.WorkGroupYRegister is not null,
                    systemRegisters?.WorkGroupZRegister is not null)
                : _physicalAxisOfLogical;
            // A wave64 runs as one 64-lane host subgroup when the host pins it to one
            // (HostWave64Supported). Hosts without 64-lane subgroups bridge the two 32-lane
            // halves through workgroup memory.
            if (_waveLaneCount == 32)
            {
                _waveLowering = new NativeWave32Lowering(this);
            }
            else if (request.CooperativeWave64Workgroup)
            {
                // The waves of a cooperative workgroup run one block at a time through a workgroup
                // dispatcher and meet at workgroup barriers, which only the emulated lowering models.
                _waveLowering = new EmulatedWave64Lowering(this);
            }
            else if (request.HostWave64Supported ||
                     (_stage == Gen5SpirvStage.Mesh && request.Mesh?.DeviceSubgroupLaneCount == 64))
            {
                _waveLowering = new NativeWave64Lowering(this);
                _guestLaneFromSubgroup = true;
            }
            else if (_pairedMeshLanes)
            {
                _waveLowering = new NativeWave32Lowering(this);
            }
            else if (_stage == Gen5SpirvStage.Compute &&
                     request.HostComputeSubgroup32 &&
                     CanRunHalfWaves(request, _physicalAxisOfLogical))
            {
                _waveLowering = new HalfWave64Lowering(this);
            }
            else if (_stage == Gen5SpirvStage.Compute && request.HostComputeSubgroup32 && PairedWave64Enabled)
            {
                _pairedLanes = true;
                _waveLowering = new PairedWave64Lowering(this);
            }
            else
            {
                _waveLowering = new EmulatedWave64Lowering(this);
            }
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
                    case DescriptorBindingKind.MipStatistics:
                        _mipStatistics = DeclareWordBlock("mipStatistics", number);
                        break;
                    case DescriptorBindingKind.CallRecords:
                        _callRecords = DeclareWordBlock("callRecords", number);
                        break;
                    case DescriptorBindingKind.CallMarks:
                        _callMarks = DeclareWordBlock("callMarks", number);
                        break;
                    case DescriptorBindingKind.DeviceAddressFallback:
                        _deviceAddressFallback = DeclareWordBlock("deviceAddressFallback", number);
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

            if (info.UsesDeviceAddresses || _stage == Gen5SpirvStage.Mesh || layout.UsesTessellationData || layout.UsesTableBlock)
            {
                _module.AddCapability(SpirvCapability.PhysicalStorageBufferAddresses);
                _module.SetPhysicalStorageBuffer64MemoryModel();
                _physicalUintPointer = _module.TypePointer(SpirvStorageClass.PhysicalStorageBuffer, _uintType);
            }
            if (request.TessellationHull is not null)
            {
                _tessellationVertexIndexScratch = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(0));
                _interfaces.Add(_tessellationVertexIndexScratch);
            }

            if (layout.UsesTableBlock)
            {
                _privateUlongPointer = _module.TypePointer(SpirvStorageClass.Private, _ulongType);
                _tableBlock = _module.AddGlobalVariable(_privateUlongPointer, SpirvStorageClass.Private, ULong(0));
                _module.AddName(_tableBlock, "tableBlock");
                _interfaces.Add(_tableBlock);
                _tableFlattenedLength = DeclareFunctionUint(UInt(0));
                _module.AddName(_tableFlattenedLength, "tableFlattenedLength");
                if (info.Buffers.Count != 0)
                {
                    var count = (uint)info.Buffers.Count;
                    var baseArray = _module.TypeArray(_ulongType, count);
                    _tableBufferBases = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.Private, baseArray), SpirvStorageClass.Private, _module.ConstantNull(baseArray));
                    _module.AddName(_tableBufferBases, "tableBufferBases");
                    _interfaces.Add(_tableBufferBases);
                    var lengthArray = _module.TypeArray(_uintType, count);
                    _tableBufferLengths = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.Private, lengthArray), SpirvStorageClass.Private, _module.ConstantNull(lengthArray));
                    _module.AddName(_tableBufferLengths, "tableBufferLengths");
                    _interfaces.Add(_tableBufferLengths);
                }
            }

            if (info.UsesDeviceAddresses)
            {
                var privateUlongPointer = _module.TypePointer(SpirvStorageClass.Private, _ulongType);
                _deviceEntryScratch = _module.AddGlobalVariable(privateUlongPointer, SpirvStorageClass.Private, _module.Constant64(_ulongType, 0));
                _deviceWordScratch = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(0));
                _module.AddName(_deviceEntryScratch, "deviceAddressEntry");
                _module.AddName(_deviceWordScratch, "deviceAddressWord");
                _interfaces.Add(_deviceEntryScratch);
                _interfaces.Add(_deviceWordScratch);
                if (_compactMemoryAccess && !request.TraceDeviceAddressFaults)
                {
                    _deviceAddressFaultPage = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(uint.MaxValue));
                    _module.AddName(_deviceAddressFaultPage, "deviceAddressFaultPage");
                    _interfaces.Add(_deviceAddressFaultPage);
                }
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
            if (_usesBufferAtomics)
            {
                // A program with buffer atomics communicates through guest memory with its other thread groups, so its plain loads
                // must see their stores and not a cached line from before them.
                _module.AddDecoration(_globalBuffers, SpirvDecoration.Coherent);
            }

            _interfaces.Add(_globalBuffers);
            if (_usesCoherentBufferPairs)
            {
                DeclareBufferPairArray(count, bindingNumber);
            }
        }

        // The same buffers as dword pairs. The GPU does an aligned DWORDX2 access as one 64-bit access, so a thread
        // group polling another's {flag, value} pair with GLC loads never sees half of an update; two dword accesses can.
        private void DeclareBufferPairArray(uint count, uint bindingNumber)
        {
            var runtimeArray = _module.TypeRuntimeArray(_uvec2Type);
            _module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, 2 * sizeof(uint));
            var block = _module.TypeStruct(runtimeArray);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            _storagePairBlockPointer = _module.TypePointer(SpirvStorageClass.StorageBuffer, block);
            _storageUvec2Pointer = _module.TypePointer(SpirvStorageClass.StorageBuffer, _uvec2Type);
            _globalBufferPairs = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.StorageBuffer, _module.TypeArray(block, count)),
                SpirvStorageClass.StorageBuffer);
            _module.AddName(_globalBufferPairs, "guestBufferPairs");
            _module.AddDecoration(_globalBufferPairs, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(_globalBufferPairs, SpirvDecoration.Binding, bindingNumber);
            _module.AddDecoration(_globalBuffers, SpirvDecoration.Aliased);
            _module.AddDecoration(_globalBufferPairs, SpirvDecoration.Aliased);
            if (_usesBufferAtomics)
            {
                _module.AddDecoration(_globalBufferPairs, SpirvDecoration.Coherent);
            }

            _interfaces.Add(_globalBufferPairs);
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
            // Every atomic image (integer or float) is declared with the R32ui
            // format below, so its OpTypeImage Sampled Type must be uint too -
            // VUID-StandaloneSpirv-Image-04965 requires the two to match. The
            // float atomics (Fmin/Fmax) still operate on the real float bits;
            // they just get there through a bitcast + integer compare-exchange
            // (see Gen5SpirvTranslator.cs's ImageAtomicFmax/Fmin case) instead
            // of through the declared image/sampled type.
            // An atomic on a 64-bit texel (a 32_32 UINT image) is declared R64ui with 64-bit unsigned texels.
            var wide = numericClass == ImageNumericClass.Uint64;
            var kind = wide
                ? ImageComponentKind.Uint64
                : atomic
                    ? ImageComponentKind.Uint
                    : numericClass switch
                    {
                        ImageNumericClass.Uint => ImageComponentKind.Uint,
                        ImageNumericClass.Sint => ImageComponentKind.Sint,
                        _ => ImageComponentKind.Float,
                    };
            var componentType = kind switch
            {
                ImageComponentKind.Sint => _intType,
                ImageComponentKind.Uint => _uintType,
                ImageComponentKind.Uint64 => _ulongType,
                _ => _floatType,
            };
            if (wide)
            {
                _module.AddCapability(SpirvCapability.Int64Atomics);
                _module.AddCapability(SpirvCapability.Int64ImageExt);
                if (!_declaredImageInt64Extension)
                {
                    _declaredImageInt64Extension = true;
                    _module.AddExtension("SPV_EXT_shader_image_int64");
                }
            }

            // The image request builder binds guest 1D textures through 1D views,
            // so the shader declaration must be 1D as well.
            var spirvDimension = dimension switch
            {
                ImageDimension.Dim1D or ImageDimension.Dim1DArray => SpirvImageDim.Dim1D,
                ImageDimension.Dim3D => SpirvImageDim.Dim3D,
                _ => SpirvImageDim.Dim2D,
            };
            if (spirvDimension == SpirvImageDim.Dim1D)
            {
                _module.AddCapability(isStorage ? SpirvCapability.Image1D : SpirvCapability.Sampled1D);
            }
            var arrayed = dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray;
            var multisampled = dimension is ImageDimension.Dim2DMsaa or ImageDimension.Dim2DMsaaArray;
            var format = wide ? SpirvImageFormat.R64ui : atomic ? SpirvImageFormat.R32ui : SpirvImageFormat.Unknown;
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

            if (layout.UsesTableBlock)
            {
                Store(_tableBlock, Pair64(LoadShaderDataDword(UInt(layout.TableBlockDword)), LoadShaderDataDword(UInt(layout.TableBlockDword + 1))));
                if (layout.UsesFlattenedTable)
                {
                    Store(_tableFlattenedLength, LoadTableWord(UInt(0)));
                }

                for (uint buffer = 0; buffer < layout.BufferCount; buffer++)
                {
                    var record = BindingLayout.TableBlockHeaderDwords + buffer * BindingLayout.TableBufferDwords;
                    Store(TableBufferBasePointer((int)buffer), Pair64(LoadTableWord(UInt(record)), LoadTableWord(UInt(record + 1))));
                    Store(TableBufferLengthPointer((int)buffer), LoadTableWord(UInt(record + 2)));
                    Store(RuntimeBufferBiasPointer((int)buffer), LoadTableWord(UInt(record + 3)));
                }
            }
        }

        private uint PhysicalWordPointer(uint baseAddress, uint dwordIndex)
        {
            var pointer = _module.AddInstruction(SpirvOp.ConvertUToPtr, _physicalUintPointer,
                IAdd64(baseAddress, Widen(ShiftLeftLogical(dwordIndex, UInt(2)))));
            _physicalPointers.Add(pointer);
            return pointer;
        }

        private uint LoadTableWord(uint dwordIndex) => Load(_uintType, PhysicalWordPointer(Load(_ulongType, _tableBlock), dwordIndex));

        private uint TableBufferBasePointer(int binding) =>
            _module.AddInstruction(SpirvOp.AccessChain, _privateUlongPointer, _tableBufferBases, UInt((uint)binding));

        private uint TableBufferLengthPointer(int binding) =>
            _module.AddInstruction(SpirvOp.AccessChain, _privateUintPointer, _tableBufferLengths, UInt((uint)binding));

        private uint TableBufferWordPointer(int binding, uint dwordAddress) =>
            PhysicalWordPointer(Load(_ulongType, TableBufferBasePointer(binding)), dwordAddress);

        private uint TableBufferLength(int binding) => Load(_uintType, TableBufferLengthPointer(binding));

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
            var length = StorageBlockLength(block);
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
                StorageBlockLength(block));

        private uint _shaderDataLength;
        private uint _flattenedTableLength;

        private void InitializeStorageBlockLengths()
        {
            if (_stage is not (Gen5SpirvStage.Mesh or Gen5SpirvStage.Compute)) return;
            if (_shaderData != 0)
                _shaderDataLength = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _shaderData, 0);
            if (_flattenedTable != 0)
                _flattenedTableLength = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _flattenedTable, 0);
        }

        // A compact program's fallback address (ResolveCompactDeviceAddress): a readable address the host binds
        // (DescriptorBindingKind.DeviceAddressFallback), read once.
        private uint _deviceAddressFallback;
        private uint _deviceFallbackPointer;

        private void InitializeDeviceAddressFallback()
        {
            if (_compactMemoryAccess && _pageTable != 0)
            {
                _deviceFallbackPointer = Pair64(Load(_uintType, BlockWordPointer(_deviceAddressFallback, UInt(0))),
                    Load(_uintType, BlockWordPointer(_deviceAddressFallback, UInt(1))));
            }
        }

        private uint StorageBlockLength(uint block)
        {
            if (block == _shaderData && _shaderDataLength != 0) return _shaderDataLength;
            if (block == _flattenedTable && _flattenedTableLength != 0) return _flattenedTableLength;
            return _module.AddInstruction(SpirvOp.ArrayLength, _uintType, block, 0);
        }

        private uint LoadFlattenedWord(uint slot)
        {
            if (!_request.Bindings.UsesTableBlock)
            {
                return LoadBlockWord(_flattenedTable, slot);
            }

            var inRange = _module.AddInstruction(SpirvOp.ULessThan, _boolType, slot, Load(_uintType, _tableFlattenedLength));
            var safeSlot = _module.AddInstruction(SpirvOp.Select, _uintType, inRange, slot, UInt(0));
            var value = LoadTableWord(IAdd(UInt(_request.Bindings.TableFlattenedDword), safeSlot));
            return _module.AddInstruction(SpirvOp.Select, _uintType, inRange, value, UInt(0));
        }

        // A planned slot is inside the table the host binds, so its load skips the runtime bounds
        // check. Large shaders make hundreds of these reads, and every check costs the Metal
        // compiler time (one large pixel shader made about 376 of them).
        private uint LoadFlattenedSlot(uint slot)
        {
            // A shared call body's record moves the slots of the instruction being emitted (TryEmitShiftedFlatInstruction).
            if (_flatSlotShift != 0 && _shiftedFlatSlots is { } shifted && shifted.Contains(slot))
            {
                var index = IAdd(UInt(slot), _flatSlotShift);
                return _shiftedFlatSlotsReserved ? LoadReservedFlattenedWord(index) : LoadFlattenedWord(index);
            }

            if (slot >= _request.FlattenedTableReservedWords)
            {
                return LoadFlattenedWord(UInt(slot));
            }

            return _request.Bindings.UsesTableBlock
                ? LoadTableWord(UInt(_request.Bindings.TableFlattenedDword + slot))
                : Load(_uintType, BlockWordPointer(_flattenedTable, UInt(slot)));
        }

        private uint LoadReservedFlattenedWord(uint index) =>
            _request.Bindings.UsesTableBlock
                ? LoadTableWord(IAdd(UInt(_request.Bindings.TableFlattenedDword), index))
                : Load(_uintType, BlockWordPointer(_flattenedTable, index));

        // A linked program's address relative to the shader base the host pushes, which is the program's own address.
        private uint ProgramAddress(ulong address)
        {
            if (!_request.Bindings.UsesShaderBase)
            {
                return ULong(address);
            }

            var (low, high) = LoadShaderBase();
            return IAdd64(Pair64(low, high), ULong(unchecked(address - _request.Program.Address)));
        }

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
            if (_compactMemoryAccess)
            {
                return ResolveCompactDeviceAddress(address64);
            }

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
                    var recordStart = _module.AddInstruction(SpirvOp.ISub, _uintType, length, UInt(16));
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

        // ResolveDeviceAddress without a branch, for a compact program. An address past the table reads entry 0 and drops it;
        // page 0, the null page, never maps, so a load that is not made reads the host's fallback address instead
        // (LoadMappedDeviceDword). reportFault, when given, limits the fault to an access that is made.
        private (uint Pointer, uint Valid) ResolveCompactDeviceAddress(uint address64, uint reportFault = 0)
        {
            var masked = And64(address64, ULong(DeviceAddressMask));
            var pageIndex64 = _module.AddInstruction(SpirvOp.ShiftRightLogical, _ulongType, masked, ULong(DeviceAddressPageBits));
            var tableLength = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _pageTable, 0);
            var inTable = ULessThan64(pageIndex64, Widen(tableLength));
            var pageIndex = Narrow(pageIndex64);
            var entry = Load(_ulongType, _module.AddInstruction(SpirvOp.AccessChain, _storageUlongPointer, _pageTable, UInt(0),
                _module.AddInstruction(SpirvOp.Select, _uintType, inTable, pageIndex, UInt(0))));
            var mapped = LogicalAnd(
                LogicalAnd(inTable, _module.AddInstruction(SpirvOp.INotEqual, _boolType, pageIndex, UInt(0))),
                _module.AddInstruction(SpirvOp.INotEqual, _boolType, entry, ULong(0)));
            var missing = LogicalAnd(inTable, LogicalNot(mapped));
            if (reportFault != 0)
            {
                missing = LogicalAnd(missing, reportFault);
            }

            if (!_request.TraceDeviceAddressFaults)
            {
                // The invocation keeps its last missing page and reports it when it ends (FlushDeviceAddressFault):
                // an atomic at every load site made large shaders compile several times slower.
                Store(_deviceAddressFaultPage,
                    _module.AddInstruction(SpirvOp.Select, _uintType, missing, pageIndex, Load(_uintType, _deviceAddressFaultPage)));
                return (IAdd64(entry, And64(masked, ULong(DeviceAddressPageSize - 1))), mapped);
            }

            EmitConditional(missing, () =>
            {
                {
                    var length = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _faultBuffer, 0);
                    var recordStart = _module.AddInstruction(SpirvOp.ISub, _uintType, length, UInt(16));
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

        private uint _deviceAddressFaultPage;

        private void FlushDeviceAddressFault()
        {
            if (_deviceAddressFaultPage == 0)
            {
                return;
            }

            var page = Load(_uintType, _deviceAddressFaultPage);
            var word = ShiftRightLogical(page, UInt(5));
            EmitConditional(
                LogicalAnd(_module.AddInstruction(SpirvOp.INotEqual, _boolType, page, UInt(uint.MaxValue)), IsBlockWordInRange(_faultBuffer, word)),
                () => _module.AddInstruction(SpirvOp.AtomicOr, _uintType, BlockWordPointer(_faultBuffer, word), UInt(1), UInt(0),
                    ShiftLeftLogical(UInt(1), BitwiseAnd(page, UInt(31)))));
        }

        private uint DeviceWordPointer(uint pointerValue) =>
            _module.AddInstruction(SpirvOp.ConvertUToPtr, _physicalUintPointer, pointerValue);

        // A load that is not made reads the fallback address and drops the value, so it needs no branch.
        private uint LoadMappedDeviceDword(uint pointer, uint valid)
        {
            var safe = _module.AddInstruction(SpirvOp.Select, _ulongType, valid, pointer, _deviceFallbackPointer);
            var word = _module.AddInstruction(SpirvOp.Load, _uintType, DeviceWordPointer(safe), 2u, 4u);
            return _module.AddInstruction(SpirvOp.Select, _uintType, valid, word, UInt(0));
        }

        // Loads one aligned dword through a device address; an unmapped page reads zero.
        private uint LoadDeviceDword(uint address64)
        {
            var (pointer, valid) = ResolveDeviceAddress(address64);
            if (_compactMemoryAccess)
            {
                return LoadMappedDeviceDword(pointer, valid);
            }

            Store(_deviceWordScratch, UInt(0));
            EmitConditional(valid, () =>
                Store(_deviceWordScratch, _module.AddInstruction(SpirvOp.Load, _uintType, DeviceWordPointer(pointer), 2u, 4u)));
            return Load(_uintType, _deviceWordScratch);
        }

        // As above when allowed, in a compact program; otherwise the dword reads zero and reports no fault.
        private uint LoadDeviceDword(uint address64, uint allowed)
        {
            var (pointer, valid) = ResolveCompactDeviceAddress(address64, allowed);
            return LoadMappedDeviceDword(pointer, LogicalAnd(allowed, valid));
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
            uint highWord;
            if (_compactMemoryAccess)
            {
                highWord = LoadDeviceDword(IAdd64(aligned, ULong(4)), crosses);
            }
            else
            {
                Store(_deviceWordScratch, UInt(0));
                EmitConditional(crosses, () =>
                {
                    var high = LoadDeviceDword(IAdd64(aligned, ULong(4)));
                    Store(_deviceWordScratch, high);
                });
                highWord = Load(_uintType, _deviceWordScratch);
            }
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

            var rangeBase = Pair64(LoadFlattenedSlot(slot), LoadFlattenedSlot(slot + 1));
            var rangeSize = Widen(LoadFlattenedSlot(slot + 2));
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
        private (uint BaseAddress, uint Size, uint Stride, uint Word3) LoadDeviceBufferDescriptor(uint descriptor)
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
            return (baseAddress, size, stride, LoadS(descriptor + 3));
        }

        private uint IsDeviceBufferByteRangeInRange(uint size64, uint byteOffset, uint byteCount) =>
            ULessThan64(
                IAdd64(Widen(byteOffset), ULong((ulong)byteCount - 1)),
                size64);

        private uint IsDeviceBufferElementInRange(uint size64, uint byteOffset, uint lastByteOffset) =>
            ULessThan64(
                IAdd64(Widen(byteOffset), Widen(lastByteOffset)),
                size64);

        private uint LoadDeviceBufferWord(uint baseAddress, uint size64, uint byteOffset)
            => LoadDeviceBufferWord(baseAddress, size64, byteOffset, _module.ConstantBool(true));

        private uint LoadDeviceBufferWord(uint baseAddress, uint size64, uint byteOffset, uint accessAllowed)
        {
            var alignedOffset = BitwiseAnd(byteOffset, UInt(~3u));
            var inRange = LogicalAnd(
                accessAllowed,
                IsDeviceBufferByteRangeInRange(size64, alignedOffset, sizeof(uint)));
            var address = And64(
                IAdd64(baseAddress, Widen(alignedOffset)),
                ULong(DeviceAddressMask & ~3ul));
            if (_compactMemoryAccess)
            {
                return LoadDeviceDword(address, inRange);
            }

            Store(_deviceBufferWordScratch, UInt(0));
            EmitConditional(inRange, () => Store(_deviceBufferWordScratch, LoadDeviceDword(address)));
            return Load(_uintType, _deviceBufferWordScratch);
        }

        // Consecutive dwords of one buffer element or scalar load in a compact program, each with its own bounds check; a
        // refused dword reads zero.
        private uint[] LoadDeviceBufferWords(uint baseAddress, uint size64, uint byteOffset, int count, IReadOnlyList<uint>? allowed = null)
        {
            var alignedOffset = BitwiseAnd(byteOffset, UInt(~3u));
            var address = And64(
                IAdd64(baseAddress, Widen(alignedOffset)),
                ULong(DeviceAddressMask & ~3ul));
            var inRange = new uint[count];
            for (var index = 0; index < count; index++)
            {
                var dwordOffset = index == 0 ? alignedOffset : IAdd(alignedOffset, UInt((uint)index * sizeof(uint)));
                var fits = IsDeviceBufferByteRangeInRange(size64, dwordOffset, sizeof(uint));
                inRange[index] = allowed is null ? fits : LogicalAnd(allowed[index], fits);
            }

            return LoadDeviceDwordRange(address, inRange);
        }

        // Consecutive aligned dwords through the page table, in a compact program. A range of at most 16 dwords lies in one
        // page or straddles two, so it resolves its first and last dword only; a dword that is not allowed reads zero and
        // reports no fault.
        private uint[] LoadDeviceDwordRange(uint address64, IReadOnlyList<uint> allowed)
        {
            var count = allowed.Count;
            if (count is < 1 or > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(allowed), "a device dword range holds 1 to 16 dwords");
            }

            if (count == 1)
            {
                return [LoadDeviceDword(address64, allowed[0])];
            }

            // The page offset of the first dword decides which dwords lie in its page.
            var pageOffset = Narrow(And64(address64, ULong(DeviceAddressPageSize - 1)));
            var firstPage = new uint[count];
            var firstAllowed = allowed[0];
            var lastAllowed = _module.ConstantBool(false);
            firstPage[0] = _module.ConstantBool(true);
            for (var index = 1; index < count; index++)
            {
                firstPage[index] = _module.AddInstruction(SpirvOp.ULessThan, _boolType,
                    IAdd(pageOffset, UInt((uint)index * sizeof(uint))), UInt((uint)DeviceAddressPageSize));
                firstAllowed = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, firstAllowed, LogicalAnd(allowed[index], firstPage[index]));
                lastAllowed = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, lastAllowed, LogicalAnd(allowed[index], LogicalNot(firstPage[index])));
            }

            var (firstPointer, firstMapped) = ResolveCompactDeviceAddress(address64, firstAllowed);
            var (lastPointer, lastMapped) = ResolveCompactDeviceAddress(IAdd64(address64, ULong((ulong)(count - 1) * sizeof(uint))), lastAllowed);
            var values = new uint[count];
            for (var index = 0; index < count; index++)
            {
                var pointer = index == 0
                    ? firstPointer
                    : _module.AddInstruction(SpirvOp.Select, _ulongType, firstPage[index],
                        IAdd64(firstPointer, ULong((ulong)index * sizeof(uint))),
                        ISub64(lastPointer, ULong((ulong)(count - 1 - index) * sizeof(uint))));
                var mapped = index == 0
                    ? firstMapped
                    : _module.AddInstruction(SpirvOp.Select, _boolType, firstPage[index], firstMapped, lastMapped);
                values[index] = LoadMappedDeviceDword(pointer, LogicalAnd(allowed[index], mapped));
            }

            return values;
        }

        private void StoreDeviceBufferWord(uint baseAddress, uint size64, uint byteOffset, uint value)
        {
            var alignedOffset = BitwiseAnd(byteOffset, UInt(~3u));
            var inRange = IsDeviceBufferByteRangeInRange(size64, alignedOffset, sizeof(uint));
            var address = And64(
                IAdd64(baseAddress, Widen(alignedOffset)),
                ULong(DeviceAddressMask & ~3ul));
            EmitConditional(inRange, () => StoreDeviceDword(address, value, _module.ConstantBool(true)));
        }

        private uint LoadDeviceDescriptorBufferWord(uint descriptor, uint byteOffset)
        {
            var (baseAddress, size, _, _) = LoadDeviceBufferDescriptor(descriptor);
            return LoadDeviceBufferWord(baseAddress, size, byteOffset);
        }

        private bool TryEmitLayoutScalarMemory(Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control, out string error)
        {
            _deviceAddressInstructionPc = instruction.Pc;
            error = string.Empty;
            var request = _request;
            var dynamicOffset = control.DynamicOffsetRegister is { } register ? LoadS(register) : UInt(0);
            if (_compactMemoryAccess && TryEmitScalarDeviceRange(instruction, control, dynamicOffset))
            {
                return true;
            }

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

                    value = LoadFlattenedSlot(slot);
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
                    if (entry.Resource != MemoryAccessInfo.NoResource)
                    {
                        var bindingIndex = (int)entry.Resource;
                        // A scalar load ignores the two low bits of the base and of the offset alike.
                        var byteAddress = BitwiseAnd(
                            IAdd(dynamicOffset, UInt(unchecked((uint)control.ImmediateOffsetBytes + (uint)component * sizeof(uint)))),
                            UInt(~3u));
                        byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
                        value = LoadBufferWord(bindingIndex, ShiftRightLogical(byteAddress, UInt(2)));
                    }
                    else
                    {
                        // A runtime SRT V#: its SGPRs hold the descriptor, so build the
                        // guest address from the base and go through the device-address
                        // page table, bounded by the descriptor's stride and record count.
                        if (instruction.Sources.Count == 0 || instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
                        {
                            error = "runtime scalar buffer load has no scalar base";
                            return false;
                        }

                        var baseRegister = instruction.Sources[0].Value;
                        var descriptorWord1 = LoadS(baseRegister + 1);
                        var stride = BitwiseAnd(ShiftRightLogical(descriptorWord1, UInt(16)), UInt(0x3FFF));
                        var records = LoadS(baseRegister + 2);
                        var size = _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            _module.AddInstruction(SpirvOp.IEqual, _boolType, stride, UInt(0)),
                            records,
                            _module.AddInstruction(SpirvOp.IMul, _uintType, stride, records));
                        var baseAddress = Pair64(LoadS(baseRegister), BitwiseAnd(descriptorWord1, UInt(0xFFFF)));
                        var byteOffset = IAdd(dynamicOffset, UInt(unchecked((uint)control.ImmediateOffsetBytes + (uint)component * sizeof(uint))));
                        var address = And64(IAdd64(baseAddress, Widen(byteOffset)), ULong(DeviceAddressMask & ~3ul));
                        var inRange = _module.AddInstruction(
                            SpirvOp.ULessThan,
                            _boolType,
                            ShiftRightLogical(byteOffset, UInt(2)),
                            ShiftRightLogical(size, UInt(2)));
                        value = LoadBoundedDeviceDword(address, inRange);
                    }
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

        // A compact program's multi-dword scalar load whose every dword goes through the same device path reads them as one
        // range: the descriptor is decoded once and the page table is read once or twice instead of once per dword. Planned
        // (flattened) dwords, key reads and bound buffers keep the per-dword path.
        private bool TryEmitScalarDeviceRange(Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control, uint dynamicOffset)
        {
            var request = _request;
            var count = instruction.Destinations.Count;
            if (count < 2 || count > 16 || instruction.Sources.Count == 0 || instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                return false;
            }

            var path = -1;
            for (var component = 0; component < count; component++)
            {
                if (instruction.Destinations[component].Kind != Gen5OperandKind.ScalarRegister ||
                    !request.Memory.TryGetIndex(instruction.Pc, (uint)component, out var memoryIndex))
                {
                    return false;
                }

                var entry = request.Memory[memoryIndex];
                if (entry.PlanningOnly || request.IndirectOffsetKeyMemoryIndices.Contains(memoryIndex) ||
                    _indirectKeyScratch.ContainsKey(memoryIndex))
                {
                    return false;
                }

                // 0: a device descriptor, 1: a runtime SRT V#, 2: a scalar address.
                var entryPath = entry.Kind == MemoryResourceKind.ScalarBuffer
                    ? entry.DeviceDescriptor ? 0 : entry.Resource == MemoryAccessInfo.NoResource ? 1 : -1
                    : entry.Kind == MemoryResourceKind.ScalarAddress ? 2 : -1;
                if (entryPath < 0 || (component > 0 && entryPath != path))
                {
                    return false;
                }

                path = entryPath;
            }

            var baseRegister = instruction.Sources[0].Value;
            var byteOffset = IAdd(dynamicOffset, UInt(unchecked((uint)control.ImmediateOffsetBytes)));
            uint[] values;
            if (path == 0)
            {
                var (baseAddress, size, _, _) = LoadDeviceBufferDescriptor(baseRegister);
                values = LoadDeviceBufferWords(baseAddress, size, byteOffset, count);
            }
            else if (path == 1)
            {
                // As the per-dword path: bounded by the V#'s stride and record count.
                var descriptorWord1 = LoadS(baseRegister + 1);
                var stride = BitwiseAnd(ShiftRightLogical(descriptorWord1, UInt(16)), UInt(0x3FFF));
                var records = LoadS(baseRegister + 2);
                var size = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, stride, UInt(0)),
                    records,
                    _module.AddInstruction(SpirvOp.IMul, _uintType, stride, records));
                var baseAddress = Pair64(LoadS(baseRegister), BitwiseAnd(descriptorWord1, UInt(0xFFFF)));
                var address = And64(IAdd64(baseAddress, Widen(byteOffset)), ULong(DeviceAddressMask & ~3ul));
                var allowed = new uint[count];
                for (var component = 0; component < count; component++)
                {
                    var componentOffset = component == 0 ? byteOffset : IAdd(byteOffset, UInt((uint)component * sizeof(uint)));
                    allowed[component] = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        ShiftRightLogical(componentOffset, UInt(2)),
                        ShiftRightLogical(size, UInt(2)));
                }

                values = LoadDeviceDwordRange(address, allowed);
            }
            else
            {
                var address = IAdd64(
                    Pair64(LoadS(baseRegister), LoadS(baseRegister + 1)),
                    IAdd64(Widen(dynamicOffset), SignedOffset64(control.ImmediateOffsetBytes)));
                var allowed = Enumerable.Repeat(_module.ConstantBool(true), count).ToArray();
                values = LoadDeviceDwordRange(And64(address, ULong(DeviceAddressMask & ~3ul)), allowed);
            }

            for (var component = 0; component < count; component++)
            {
                StoreS(instruction.Destinations[component].Value, values[component]);
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

            if (control.UsesFlatAddress &&
                (instruction.Opcode.StartsWith("FlatLoadDword", StringComparison.Ordinal) ||
                 instruction.Opcode.StartsWith("FlatStoreDword", StringComparison.Ordinal)))
            {
                return TryEmitFlatDwordMemory(instruction, control, out error);
            }

            if (control.UsesFlatAddress && _request.Program.Instructions.Any(operation =>
                    operation.Sources.Any(source => source.Kind == Gen5OperandKind.EncodedConstant && source.Value is >= 235 and <= 238)))
            {
                error = $"local aperture access is not supported for {instruction.Opcode}";
                return false;
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
            if (control.UsesFlatAddress && entry.AddressSpace != FlatAddressSpace.Global)
            {
                return TryEmitFlatLocalMemory(instruction, control, memoryOpcode, entry.AddressSpace, address, out error);
            }

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

        // Routes each lane of an aperture-derived FLAT access like the hardware: LDS or
        // scratch at the low dword when the high dword is in an aperture, otherwise a
        // global access through the page table.
        private bool TryEmitFlatLocalMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            string memoryOpcode,
            FlatAddressSpace addressSpace,
            uint address,
            out string error)
        {
            error = string.Empty;
            var store = memoryOpcode.StartsWith("GlobalStoreDword", StringComparison.Ordinal);
            var load = memoryOpcode.StartsWith("GlobalLoadDword", StringComparison.Ordinal);
            if (!store && !load)
            {
                error = $"{instruction.Opcode} through the {addressSpace} flat aperture is not implemented";
                return false;
            }

            var canShare = addressSpace is FlatAddressSpace.Shared or FlatAddressSpace.SharedOrPrivate;
            var canPrivate = addressSpace is FlatAddressSpace.Private or FlatAddressSpace.SharedOrPrivate;
            if (canShare && _lds == 0)
            {
                error = "a shared flat aperture access has no LDS storage";
                return false;
            }

            if (canPrivate && (_scratch == 0 || _scratchDwordCount == 0))
            {
                error = "a private flat aperture access has no scratch storage";
                return false;
            }

            var offset = _module.AddInstruction(SpirvOp.UConvert, _uintType, address);
            var aperture = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(address, _module.Constant64(_ulongType, 32 + Gen5InlineConstants.ApertureShift)));
            var isShared = _module.AddInstruction(SpirvOp.IEqual, _boolType, aperture,
                UInt(Gen5InlineConstants.SharedApertureHigh >> Gen5InlineConstants.ApertureShift));
            var isPrivate = _module.AddInstruction(SpirvOp.IEqual, _boolType, aperture,
                UInt(Gen5InlineConstants.PrivateApertureHigh >> Gen5InlineConstants.ApertureShift));

            void Shared()
            {
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    var pointer = LdsPointer(offset, index * sizeof(uint));
                    if (store)
                        Store(pointer, LoadV(control.SourceVectorRegister + index));
                    else
                        StoreV(control.DestinationVectorRegister + index, Load(_uintType, pointer));
                }
            }

            void Private()
            {
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    var pointer = ScratchPointer(offset, index * sizeof(uint));
                    if (store)
                        Store(pointer, LoadV(control.SourceVectorRegister + index));
                    else
                        StoreV(control.DestinationVectorRegister + index, Load(_uintType, pointer));
                }
            }

            void Global()
            {
                var aligned = And64(address, ULong(~3ul));
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    var dwordAddress = index == 0 ? aligned : IAdd64(aligned, ULong((ulong)index * sizeof(uint)));
                    if (store)
                        StoreDeviceDword(dwordAddress, LoadV(control.SourceVectorRegister + index), _module.ConstantBool(true));
                    else
                        StoreV(control.DestinationVectorRegister + index, LoadDeviceDword(dwordAddress));
                }
            }

            // Inactive lanes must not touch any of the three storages.
            EmitExecConditional(() =>
            {
                if (canShare && canPrivate)
                    EmitConditional(isShared, Shared, () => EmitConditional(isPrivate, Private, Global));
                else if (canShare)
                    EmitConditional(isShared, Shared, Global);
                else
                    EmitConditional(isPrivate, Private, Global);
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
            if (control.DynamicOffsetRegister is { } dynamicOffsetRegister)
            {
                address = IAdd(address, LoadS(dynamicOffsetRegister));
            }
            if (control.OffsetBytes != 0)
            {
                address = IAdd(address, UInt(unchecked((uint)control.OffsetBytes)));
            }

            if (instruction.Opcode.StartsWith("ScratchStore", StringComparison.Ordinal))
            {
                if (!_compactMemoryAccess)
                {
                    EmitExecConditional(() =>
                    {
                        if (TryGetSubdwordStoreInfo(instruction.Opcode, out var byteCount, out var sourceShift))
                        {
                            StoreScratchBytes(address, LoadV(control.SourceVectorRegister), byteCount, sourceShift, 0);
                            return;
                        }

                        for (uint index = 0; index < control.DwordCount; index++)
                        {
                            Store(
                                ScratchPointer(address, index * sizeof(uint)),
                                control.SourceIsScalar
                                    ? LoadS(control.SourceVectorRegister + index)
                                    : LoadV(control.SourceVectorRegister + index));
                        }
                    });
                    return true;
                }

                // Scratch is the invocation's own and its index wraps, so an inactive lane of a compact program may touch it:
                // a store writes back the word it read, and a load's register write keeps the old value (StoreV). Neither
                // needs a branch.
                var active = _execKnownFull ? 0 : Load(_boolType, _exec);
                if (TryGetSubdwordStoreInfo(instruction.Opcode, out var storeBytes, out var storeShift))
                {
                    StoreScratchBytes(address, LoadV(control.SourceVectorRegister), storeBytes, storeShift, active);
                    return true;
                }

                for (uint index = 0; index < control.DwordCount; index++)
                {
                    var pointer = ScratchPointer(address, index * sizeof(uint));
                    var value = control.SourceIsScalar
                        ? LoadS(control.SourceVectorRegister + index)
                        : LoadV(control.SourceVectorRegister + index);
                    Store(pointer, active == 0
                        ? value
                        : _module.AddInstruction(SpirvOp.Select, _uintType, active, value, Load(_uintType, pointer)));
                }

                return true;
            }

            if (!instruction.Opcode.StartsWith("ScratchLoad", StringComparison.Ordinal))
            {
                error = $"unsupported scratch opcode {instruction.Opcode}";
                return false;
            }

            if (!_compactMemoryAccess)
            {
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

            if (TryGetSubdwordLoadInfo(instruction.Opcode, out var loadBytes, out var loadSignExtend, out var loadD16, out var loadD16High))
            {
                var destination = control.DestinationVectorRegister;
                var value = LoadScratchBytes(address, loadBytes, loadSignExtend);
                if (loadD16)
                {
                    value = loadD16High
                        ? BitwiseOr(BitwiseAnd(LoadV(destination), UInt(0x0000_FFFF)), ShiftLeftLogical(BitwiseAnd(value, UInt(0xFFFF)), UInt(16)))
                        : BitwiseOr(BitwiseAnd(LoadV(destination), UInt(0xFFFF_0000)), BitwiseAnd(value, UInt(0xFFFF)));
                }

                StoreV(destination, value);
                return true;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                StoreV(
                    control.DestinationVectorRegister + index,
                    Load(_uintType, ScratchPointer(address, index * sizeof(uint))));
            }

            return true;
        }

        private uint ScratchPointer(uint byteAddress, uint offsetBytes = 0)
        {
            var address = offsetBytes == 0
                ? byteAddress
                : IAdd(byteAddress, UInt(offsetBytes));
            // The index wraps, so even an inactive lane's address stays in the array; a compact program's single scratch
            // dword is element 0.
            var dwordIndex = _scratchDwordCount > 1
                ? _module.AddInstruction(
                    SpirvOp.UMod,
                    _uintType,
                    ShiftRightLogical(address, UInt(2)),
                    UInt(_scratchDwordCount))
                : _compactMemoryAccess ? UInt(0) : ShiftRightLogical(address, UInt(2));

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

        // active is 0 when every lane stores; otherwise an inactive lane writes back the word it read.
        private void StoreScratchBytes(uint byteAddress, uint value, uint byteCount, uint sourceShift, uint active)
        {
            if (sourceShift != 0)
            {
                value = ShiftRightLogical(value, UInt(sourceShift));
            }

            var pointer = ScratchPointer(byteAddress);
            var shift = ShiftLeftLogical(BitwiseAnd(byteAddress, UInt(3)), UInt(3));
            var elementMask = UInt(byteCount == 1 ? 0xFFu : 0xFFFFu);
            var shiftedMask = ShiftLeftLogical(elementMask, shift);
            var old = Load(_uintType, pointer);
            var merged = BitwiseOr(
                BitwiseAnd(
                    old,
                    _module.AddInstruction(SpirvOp.Not, _uintType, shiftedMask)),
                ShiftLeftLogical(BitwiseAnd(value, elementMask), shift));
            Store(pointer, active == 0 ? merged : _module.AddInstruction(SpirvOp.Select, _uintType, active, merged, old));
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

        // BufferLoweringStrategy.PhysicalStorageBuffer: a raw buffer access whose
        // V# was read from a runtime scalar address. The descriptor dwords live in
        // the SGPRs named by the instruction, so build the guest address from the
        // runtime base and go through the device-address page table.
        // num_records/stride bound the access, so a null or empty descriptor binds
        // nothing, matching the static path's range check. Formatted/typed/atomic
        // accesses never reach here; the chooser escalates them to
        // BoundedCandidateTable.
        private bool TryEmitPhysicalStorageBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (control.Typed || instruction.Opcode.Contains("Format", StringComparison.Ordinal))
            {
                error = $"runtime buffer descriptor does not support the formatted access {instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode.StartsWith("BufferAtomic", StringComparison.Ordinal))
            {
                error = $"runtime buffer descriptor does not support the atomic access {instruction.Opcode}";
                return false;
            }

            if (instruction.Sources.Count < 2 ||
                instruction.Sources[1].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "runtime buffer descriptor has no scalar resource base";
                return false;
            }

            var srsrc = instruction.Sources[1].Value;
            var scalarOffset = instruction.Sources.Count > 2
                ? GetRawSource(instruction, 2)
                : UInt(0);
            var vectorIndex = control.IndexEnabled ? LoadV(control.VectorAddress) : UInt(0);
            var vectorOffset = control.OffsetEnabled
                ? LoadV(control.VectorAddress + (control.IndexEnabled ? 1u : 0u))
                : UInt(0);

            var descriptorWord1 = LoadS(srsrc + 1);
            var stride = BitwiseAnd(ShiftRightLogical(descriptorWord1, UInt(16)), UInt(0x3FFF));
            var byteOffset = IAdd(UInt(unchecked((uint)control.OffsetBytes)), scalarOffset);
            byteOffset = IAdd(byteOffset, vectorOffset);
            byteOffset = IAdd(
                byteOffset,
                _module.AddInstruction(SpirvOp.IMul, _uintType, vectorIndex, stride));

            // size = stride == 0 ? num_records : stride * num_records, in bytes.
            var records = LoadS(srsrc + 2);
            var size = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, stride, UInt(0)),
                records,
                _module.AddInstruction(SpirvOp.IMul, _uintType, stride, records));

            var dwordOffset = ShiftRightLogical(byteOffset, UInt(2));
            var dwordSize = ShiftRightLogical(size, UInt(2));
            var baseAddress = Pair64(
                LoadS(srsrc),
                BitwiseAnd(descriptorWord1, UInt(0xFFFF)));
            var address = IAdd64(baseAddress, Widen(byteOffset));

            uint InRange(uint index)
            {
                var dword = index == 0 ? dwordOffset : IAdd(dwordOffset, UInt(index));
                return _module.AddInstruction(SpirvOp.ULessThan, _boolType, dword, dwordSize);
            }

            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreByte", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreShort", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (TryGetSubdwordStoreInfo(instruction.Opcode, out var byteCount, out var sourceShift))
                    {
                        StoreDeviceBytes(
                            address,
                            LoadV(control.VectorData),
                            byteCount,
                            sourceShift,
                            InRange(0));
                        return;
                    }

                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        StoreDeviceDword(
                            index == 0 ? address : IAdd64(address, ULong((ulong)index * sizeof(uint))),
                            LoadV(control.VectorData + index),
                            InRange(index));
                    }
                });
                return true;
            }

            if (TryGetSubdwordLoadInfo(
                    instruction.Opcode,
                    out var loadByteCount,
                    out var signExtend,
                    out var d16,
                    out var d16High))
            {
                StoreV(
                    control.VectorData,
                    LoadSubdwordDeviceValue(
                        address,
                        LoadV(control.VectorData),
                        loadByteCount,
                        signExtend,
                        d16,
                        d16High));
                return true;
            }

            if (!instruction.Opcode.StartsWith("BufferLoad", StringComparison.Ordinal) &&
                !instruction.Opcode.StartsWith("TBufferLoad", StringComparison.Ordinal))
            {
                error = $"unsupported runtime buffer opcode {instruction.Opcode}";
                return false;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                StoreV(
                    control.VectorData + index,
                    LoadBoundedDeviceDword(
                        index == 0 ? address : IAdd64(address, ULong((ulong)index * sizeof(uint))),
                        InRange(index)));
            }

            return true;
        }

        private uint LoadBoundedDeviceDword(uint address64, uint inRange)
        {
            var (pointer, valid) = ResolveDeviceAddress(address64);
            if (_compactMemoryAccess)
            {
                return LoadMappedDeviceDword(pointer, LogicalAnd(inRange, valid));
            }

            Store(_deviceWordScratch, UInt(0));
            EmitConditional(LogicalAnd(inRange, valid), () =>
                Store(
                    _deviceWordScratch,
                    _module.AddInstruction(
                        SpirvOp.Load,
                        _uintType,
                        DeviceWordPointer(pointer),
                        2u,
                        4u)));
            return Load(_uintType, _deviceWordScratch);
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
            var element = classElements.IndexOf(info.GetCanonicalImageBinding((uint)resourceIndex));
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

        private bool HasFlattenedTable => _flattenedTable != 0 || _request.Bindings is { UsesTableBlock: true, UsesFlattenedTable: true };

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

            var element = imageClass.Resources.ToList().IndexOf(info.GetCanonicalImageBinding((uint)resourceIndex));
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

            // A call record's image is the element of set 1 the host resolved for the record its callee's entry found, not its
            // null element in set 0.
            var heapImageSlot = BindlessHeap.ImageSlot(kind.Value);
            var fromHeap = false;
            uint elementPointer;
            uint imageValue;
            if (fixedElement is null && heapImageSlot >= 0 &&
                request.CallRecordImageByMemoryIndex.TryGetValue(memoryIndex, out var recordImage))
            {
                (elementPointer, imageValue) = LoadHeapElement(heapImageSlot, imageClass.ImageType, CallRecordElement(recordImage.Field));
                fromHeap = true;
            }
            else
            {
                var elementIndex = UInt(fixedElement?.Element ?? (uint)element);
                elementPointer = _module.AddInstruction(SpirvOp.AccessChain, imageClass.ElementPointer, imageClass.Variable, elementIndex);
                imageValue = Load(imageClass.ImageType, elementPointer);
            }

            uint objectType;
            if (UsesSampler(instruction.Opcode))
            {
                uint sampler;
                if (fixedElement is null && request.CallRecordSamplerByMemoryIndex.TryGetValue(memoryIndex, out var recordSampler))
                {
                    (_, sampler) = LoadHeapElement(recordSampler.Slot, _module.TypeSampler(), CallRecordElement(recordSampler.Field));
                    fromHeap = true;
                }
                else
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

                    var samplerElement = request.Bindings.Find(DescriptorBindingKind.Samplers)!.Resources.ToList()
                        .IndexOf(info.SamplerBinding(samplerIndex));
                    var samplerPointer = _module.AddInstruction(SpirvOp.AccessChain, _samplerPointer, _samplerArray, UInt((uint)samplerElement));
                    sampler = Load(_samplerType, samplerPointer);
                }

                objectType = _module.TypeSampledImage(imageClass.ImageType);
                imageObject = _module.AddInstruction(SpirvOp.SampledImage, objectType, imageValue, sampler);
                if (fromHeap)
                {
                    _module.AddDecoration(imageObject, SpirvDecoration.NonUniform);
                }
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
                imageInfo.EmulatedCompareFunction,
                resourceIndex);
            dstSelect = imageInfo.ShaderSwizzle;
            return true;
        }

        // ---- bindless heap ----

        // One runtime array of set 1, declared at its first use.
        private uint HeapArray(int slot, uint elementType)
        {
            if (_heapArrays.TryGetValue(slot, out var variable))
            {
                return variable;
            }

            if (_heapArrays.Count == 0)
            {
                _module.AddCapability(SpirvCapability.RuntimeDescriptorArray);
                _module.AddCapability(SpirvCapability.ShaderNonUniform);
                _module.AddCapability(SpirvCapability.SampledImageArrayNonUniformIndexing);
            }

            variable = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.UniformConstant, _module.TypeRuntimeArray(elementType)),
                SpirvStorageClass.UniformConstant);
            _module.AddName(variable, $"heapArray{slot}");
            _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, BindlessHeap.DescriptorSet);
            _module.AddDecoration(variable, SpirvDecoration.Binding, (uint)slot);
            _interfaces.Add(variable);
            _heapArrays[slot] = variable;
            return variable;
        }

        // Loads one element of a set-1 array.
        private (uint Pointer, uint Value) LoadHeapElement(int slot, uint elementType, uint index)
        {
            var array = HeapArray(slot, elementType);
            var pointer = _module.AddInstruction(
                SpirvOp.AccessChain, _module.TypePointer(SpirvStorageClass.UniformConstant, elementType), array, index);
            _module.AddDecoration(pointer, SpirvDecoration.NonUniform);
            var value = Load(elementType, pointer);
            _module.AddDecoration(value, SpirvDecoration.NonUniform);
            return (pointer, value);
        }

        // ---- call records ----

        private uint _callMarks;

        // A call that reaches a callee the program does not hold sets the callee's bit (CallMarks.Bit of the call's target and
        // the caller's hash) before it returns at once, so the host can link the callees that calls reach; a plain read first
        // keeps a marked callee from repeating the atomic.
        private void EmitCallMark(ShaderCallFaultControl call)
        {
            if (_callMarks == 0)
            {
                return;
            }

            uint Multiply(uint value, uint factor) => _module.AddInstruction(SpirvOp.IMul, _uintType, value, UInt(factor));
            uint Xor(uint left, uint right) => _module.AddInstruction(SpirvOp.BitwiseXor, _uintType, left, right);
            uint XorShift(uint value, uint shift) => Xor(value, ShiftRightLogical(value, UInt(shift)));
            var value = Xor(Xor(LoadS(call.AddressRegister), Multiply(LoadS(call.AddressRegister + 1), 0x9E3779B1u)),
                UInt(CallMarks.Seed(_request.Hash)));
            value = Multiply(XorShift(value, 16), 0x7FEB352Du);
            value = Multiply(XorShift(value, 15), 0x846CA68Bu);
            var bit = BitwiseAnd(XorShift(value, 16), UInt(CallMarks.Bits - 1));
            var pointer = BlockWordPointer(_callMarks, ShiftRightLogical(bit, UInt(5)));
            var mask = ShiftLeftLogical(UInt(1), BitwiseAnd(bit, UInt(31)));
            var unmarked = _module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(Load(_uintType, pointer), mask), UInt(0));
            EmitConditional(unmarked, () => _module.AddInstruction(SpirvOp.AtomicOr, _uintType, pointer, UInt(1), UInt(0), mask));
        }

        private uint _callRecords;
        private uint _callRecordRow;

        private uint CallRecordWord(uint index) => Load(_uintType, BlockWordPointer(_callRecords, index));

        private uint _callRecordSearch;

        // A run-time call entry finds its record among the dispatch's call records (searchCallRecords); the row stays in a
        // private variable for the callee's record accesses.
        private void EmitCallRecordLookup(ShaderCallEntryControl call)
        {
            if (_callRecords == 0 || _callRecordRow == 0)
            {
                return;
            }

            if (_callRecordSearch == 0)
            {
                _callRecordSearch = _module.AllocateId();
            }

            var callee = UInt(_callTargetSlots is { } slots && slots.TryGetValue(call.Callee, out var slot) ? (uint)slot : uint.MaxValue);
            Store(_callRecordRow, _module.AddInstruction(SpirvOp.FunctionCall, _uintType, _callRecordSearch,
                LoadS(call.ArgumentRegister), LoadS(call.ArgumentRegister + 1), callee));
        }

        // One function for every entry, after every function that calls it (BindlessHeap.CallRecordKeyWords): a branchless
        // binary search for the first row not below (argument low, argument high, callee slot), which it returns when the row
        // holds the key, else no row.
        private void EmitCallRecordSearch()
        {
            if (_callRecordSearch == 0)
            {
                return;
            }

            const uint dontInline = 2;
            var functionType = _module.TypeFunction(_uintType, _uintType, _uintType, _uintType);
            _module.BeginFunction(_uintType, functionType, dontInline, _callRecordSearch);
            _module.AddName(_callRecordSearch, "searchCallRecords");
            var low = _module.AddFunctionParameter(_uintType);
            var high = _module.AddFunctionParameter(_uintType);
            var callee = _module.AddFunctionParameter(_uintType);
            _module.AddLabel();
            var count = CallRecordWord(UInt(0));
            uint KeyWord(uint row, uint word) => CallRecordWord(IAdd(UInt(1 + word),
                _module.AddInstruction(SpirvOp.IMul, _uintType, row, UInt(BindlessHeap.CallRecordKeyWords))));
            uint Below(uint row)
            {
                var rowLow = KeyWord(row, 0);
                var rowHigh = KeyWord(row, 1);
                var rowCallee = KeyWord(row, 2);
                var highBelow = _module.AddInstruction(SpirvOp.ULessThan, _boolType, rowHigh, high);
                var highEqual = _module.AddInstruction(SpirvOp.IEqual, _boolType, rowHigh, high);
                var lowBelow = _module.AddInstruction(SpirvOp.ULessThan, _boolType, rowLow, low);
                var lowEqual = _module.AddInstruction(SpirvOp.IEqual, _boolType, rowLow, low);
                var calleeBelow = _module.AddInstruction(SpirvOp.ULessThan, _boolType, rowCallee, callee);
                return LogicalOr(highBelow, LogicalAnd(highEqual, LogicalOr(lowBelow, LogicalAnd(lowEqual, calleeBelow))));
            }

            var position = UInt(0);
            for (var step = (BindlessHeap.CallRecordMaximumRows + 1) >> 1; step != 0; step >>= 1)
            {
                var probe = IAdd(position, UInt(step));
                var inRange = _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, probe, count);
                var row = _module.AddInstruction(SpirvOp.Select, _uintType, inRange,
                    _module.AddInstruction(SpirvOp.ISub, _uintType, probe, UInt(1)), UInt(0));
                position = _module.AddInstruction(SpirvOp.Select, _uintType, LogicalAnd(inRange, Below(row)), probe, position);
            }

            var found = _module.AddInstruction(SpirvOp.ULessThan, _boolType, position, count);
            var safe = _module.AddInstruction(SpirvOp.Select, _uintType, found, position, UInt(0));
            var equal = LogicalAnd(found, LogicalAnd(
                _module.AddInstruction(SpirvOp.IEqual, _boolType, KeyWord(safe, 0), low),
                LogicalAnd(_module.AddInstruction(SpirvOp.IEqual, _boolType, KeyWord(safe, 1), high),
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, KeyWord(safe, 2), callee))));
            _module.AddStatement(SpirvOp.ReturnValue, _module.AddInstruction(SpirvOp.Select, _uintType, equal, position, UInt(uint.MaxValue)));
            _module.EndFunction();
        }

        // The set-1 element of one field of the record the last call entry found, or the null element; every word it reads
        // lies inside the buffer.
        private uint CallRecordElement(int field)
        {
            if (_callRecords == 0 || _callRecordRow == 0)
            {
                return UInt(BindlessHeap.CallRecordNullIndex);
            }

            var words = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _callRecords, 0);
            uint Clamped(uint index) => _module.AddInstruction(SpirvOp.Select, _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, index, words), index, UInt(0));
            var row = Load(_uintType, _callRecordRow);
            var count = CallRecordWord(UInt(0));
            var found = _module.AddInstruction(SpirvOp.ULessThan, _boolType, row, count);
            var safe = _module.AddInstruction(SpirvOp.Select, _uintType, found, row, UInt(0));
            var rowsEnd = IAdd(UInt(1), _module.AddInstruction(SpirvOp.IMul, _uintType, count, UInt(BindlessHeap.CallRecordKeyWords)));
            var first = CallRecordWord(Clamped(IAdd(rowsEnd, safe)));
            var element = CallRecordWord(Clamped(IAdd(first, UInt((uint)field))));
            return _module.AddInstruction(SpirvOp.Select, _uintType, found, element, UInt(BindlessHeap.CallRecordNullIndex));
        }

        private const uint MipStatisticsEntries = 257;
        private const uint MipStatisticsNoCounter = 0x8000;

        private void RecordMipStatistics(SpirvImageResource resource, uint imageObject, uint coordinates, uint? explicitLod, uint bias)
        {
            if (_mipStatistics == 0 || _stage != Gen5SpirvStage.Pixel || resource.ResourceIndex < 0 ||
                resource.IsStorage || resource.Multisampled || resource.Cube)
            {
                return;
            }

            var field = LoadShaderDataDword(UInt(_request.Bindings.MipStatisticsDword + (uint)resource.ResourceIndex));
            uint lod;
            if (explicitLod is { } value)
            {
                lod = value;
            }
            else
            {
                var queried = _module.AddInstruction(
                    SpirvOp.ImageQueryLod,
                    _vec2Type,
                    imageObject,
                    SpatialCoordinates(resource, coordinates));
                lod = _module.AddInstruction(
                    SpirvOp.FAdd,
                    _floatType,
                    _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, queried, 1u),
                    bias);
            }

            var counter = BitwiseAnd(field, UInt(0xFF));
            var baseLevel = _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, BitwiseAnd(ShiftRightLogical(field, UInt(8)), UInt(0xF)));
            var threshold = _module.AddInstruction(
                SpirvOp.FMul,
                _floatType,
                _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, BitwiseAnd(ShiftRightLogical(field, UInt(16)), UInt(0xFFF))),
                Float(1f / 256f));
            var absolute = Ext(43, _floatType, _module.AddInstruction(SpirvOp.FAdd, _floatType, lod, baseLevel), Float(0f), Float(14f));
            var level = _module.AddInstruction(SpirvOp.ConvertFToU, _uintType, absolute);
            var clamped = _module.AddInstruction(SpirvOp.FOrdLessThan, _boolType, absolute, threshold);
            var record = LogicalAnd(
                Load(_boolType, _exec),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(field, UInt(MipStatisticsNoCounter)), UInt(0)));
            record = LogicalAnd(record, _module.AddInstruction(SpirvOp.LogicalNot, _boolType, Load(_boolType, HelperInvocationInput())));
            var finestPointer = MipStatisticsPointer(counter);
            var countPointer = MipStatisticsPointer(IAdd(counter, UInt(MipStatisticsEntries)));
            EmitConditional(record, () =>
            {
                if (!_request.MipStatisticsSubgroupReduction)
                {
                    _module.AddInstruction(SpirvOp.AtomicUMin, _uintType, finestPointer, UInt(1), UInt(0), level);
                    EmitConditional(clamped, () =>
                        _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, countPointer, UInt(1), UInt(0), UInt(1)));
                    return;
                }

                _module.AddCapability(SpirvCapability.GroupNonUniform);
                _module.AddCapability(SpirvCapability.GroupNonUniformBallot);
                _module.AddCapability(SpirvCapability.GroupNonUniformArithmetic);
                var finest = _module.AddInstruction(SpirvOp.GroupNonUniformUMin, _uintType, UInt(3), 0u, level);
                var ballot = _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type, UInt(3), clamped);
                var count = _module.AddInstruction(SpirvOp.GroupNonUniformBallotBitCount, _uintType, UInt(3), 0u, ballot);
                var leader = _module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3));
                EmitConditional(leader, () =>
                {
                    _module.AddInstruction(SpirvOp.AtomicUMin, _uintType, finestPointer, UInt(1), UInt(0), finest);
                    EmitConditional(IsNotZero(count), () =>
                        _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, countPointer, UInt(1), UInt(0), count));
                });
            });
        }

        private uint MipStatisticsPointer(uint index) =>
            BlockWordPointer(_mipStatistics, index);

        private uint HelperInvocationInput()
        {
            if (_helperInvocationInput == 0)
            {
                _helperInvocationInput = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Input, _boolType),
                    SpirvStorageClass.Input);
                _module.AddDecoration(_helperInvocationInput, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.HelperInvocation);
                _module.AddName(_helperInvocationInput, "helperInvocation");
                _interfaces.Add(_helperInvocationInput);
            }

            return _helperInvocationInput;
        }

        private uint SpatialCoordinates(SpirvImageResource resource, uint coordinates)
        {
            var spatial = ImageSpatialComponentCountOf(resource.Dimension);
            if (!resource.Arrayed)
            {
                return coordinates;
            }

            if (spatial == 1)
            {
                return _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, coordinates, 0u);
            }

            var components = new uint[spatial + 2];
            components[0] = coordinates;
            components[1] = coordinates;
            for (var index = 0u; index < spatial; index++)
            {
                components[index + 2] = index;
            }

            return _module.AddInstruction(SpirvOp.VectorShuffle, _module.TypeVector(_floatType, spatial), components);
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
                case "DsOrderedCount":
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
                if (instruction.Sources.Count < 2)
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
                            instruction.Opcode == "DsMaxF32",
                            scope: 1,
                            semantics: 0x48,
                            privateMemory: false));
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
        // DS_ORDERED_COUNT adds the first active lane's ADDR register to the dword offset0[7:2] picks instead. M0's size holds
        // the wave's launch order there (and the packer id in its low bits for a pixel wave), so it does not gate the access.
        // The waves are not held back into launch order here: they take their ranges in the order they arrive.
        private bool TryEmitGlobalDataShareCounter(Gen5ShaderInstruction instruction, Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 1 || instruction.Destinations.Count < 1)
            {
                error = $"missing {instruction.Opcode} operand";
                return false;
            }

            var ordered = instruction.Opcode == "DsOrderedCount";
            if (ordered && (control.Offset1 & 0xF0u) != 0)
            {
                // Offset1[7:4] selects an operation other than the one-dword add: LLVM sets bit 4 for a swap and bits 7:6
                // to the dword count less one.
                error = $"unsupported DsOrderedCount operation (offset1 0x{control.Offset1:X2})";
                return false;
            }

            var offset = ordered ? control.Offset0 & 0xFCu : control.SingleOffsetBytes;
            var m0 = GetRawSource(instruction, 0);
            var baseAddress = ShiftRightLogical(m0, UInt(16));
            var sizeBytes = BitwiseAnd(m0, UInt(0xFFFF));
            var index = GlobalDataShareIndex(baseAddress, offset);
            if (ordered && (control.Offset1 & 4) != 0)
            {
                index = IAdd(index, BitwiseAnd(m0, UInt(3)));
            }

            var inBounds = ordered
                ? IsBlockWordInRange(_globalDataShare, index)
                : LogicalAnd(IsNotZero(sizeBytes), IsBlockWordInRange(_globalDataShare, index));
            var destination = instruction.Destinations[0].Value;
            if (_pairedLanes)
            {
                // The leader invocation holds the first active lane: in the lower half when that half has one.
                var leaderCount = ordered
                    ? _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        IsNotZero(ExecHalfBallot(0)),
                        VectorSourceOfHalf(instruction, 1, 0),
                        VectorSourceOfHalf(instruction, 1, 1))
                    : 0;
                EmitPairedWaveCounter(destination, inBounds, count => EmitAtomic(
                    instruction.Opcode == "DsConsume" ? SpirvOp.AtomicISub : SpirvOp.AtomicIAdd,
                    _uintType,
                    BlockWordPointer(_globalDataShare, index),
                    scope: 1,
                    semantics: 0x48,
                    value: () => ordered ? leaderCount : count,
                    comparator: () => UInt(0)));
                return true;
            }

            var active = Load(_boolType, _exec);
            if (_stage == Gen5SpirvStage.Pixel)
            {
                active = LogicalAnd(active, _module.AddInstruction(SpirvOp.LogicalNot, _boolType, Load(_boolType, HelperInvocationInput())));
            }

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
                    instruction.Opcode == "DsConsume" ? SpirvOp.AtomicISub : SpirvOp.AtomicIAdd,
                    _uintType,
                    BlockWordPointer(_globalDataShare, index),
                    scope: 1,
                    semantics: 0x48,
                    value: () => ordered ? GetRawSource(instruction, 1) : activeCount,
                    comparator: () => UInt(0));
                StoreV(destination, original);
            });

            var firstValue = LoadV(destination);
            var broadcast = BroadcastWaveValue(isFirstActive, firstValue, firstLane);

            var validResult = LogicalAnd(inBounds, IsNotZero64(activeMask));
            StoreV(destination, _module.AddInstruction(SpirvOp.Select, _uintType, validResult, broadcast, UInt(0)));
            return true;
        }
    }
}
