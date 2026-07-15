// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private const uint ScalarRegisterCount = 256;
    private const uint VectorRegisterCount = 512;
    private const uint LdsDwordCount = 8192;
    private const uint RdnaWaveLaneCount = 32;

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        Gen5PixelOutputKind outputKind,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1) =>
        TryCompilePixelShader(
            state,
            evaluation,
            [new Gen5PixelOutputBinding(0, 0, outputKind)],
            out shader,
            out error,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            scalarRegisterBufferIndex);

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1)
    {
        if (outputs.Count > 8 || outputs.Any(output => output.GuestSlot > 7))
        {
            shader = default!;
            error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
            return false;
        }

        if (outputs.Select(output => output.GuestSlot).Distinct().Count() != outputs.Count ||
            outputs.Select(output => output.HostLocation).Distinct().Count() != outputs.Count)
        {
            shader = default!;
            error = "pixel output guest slots and host locations must be unique";
            return false;
        }

        if (!outputs
                .OrderBy(output => output.HostLocation)
                .Select((output, index) => output.HostLocation == (uint)index)
                .All(isDense => isDense))
        {
            shader = default!;
            error = "pixel output host locations must be dense in the 0..N-1 range";
            return false;
        }

        var context = new CompilationContext(
            Gen5SpirvStage.Pixel,
            state,
            evaluation,
            outputs,
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            scalarRegisterBufferIndex);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1)
    {
        var context = new CompilationContext(
            Gen5SpirvStage.Vertex,
            state,
            evaluation,
            [],
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            scalarRegisterBufferIndex);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out Gen5SpirvShader shader,
        out string error)
    {
        var context = new CompilationContext(
            Gen5SpirvStage.Compute,
            state,
            evaluation,
            [],
            Math.Max(localSizeX, 1),
            Math.Max(localSizeY, 1),
            Math.Max(localSizeZ, 1),
            0,
            -1,
            0,
            -1);
        return context.TryCompile(out shader, out error);
    }

    private sealed partial class CompilationContext
    {
        private readonly SpirvModuleBuilder _module = new();
        private readonly Gen5SpirvStage _stage;
        private readonly Gen5ShaderState _state;
        private readonly Gen5ShaderEvaluation _evaluation;
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        private readonly uint _localSizeX;
        private readonly uint _localSizeY;
        private readonly uint _localSizeZ;
        private readonly int _globalBufferBase;
        private readonly int _totalGlobalBufferCount;
        private readonly int _imageBindingBase;
        private readonly int _scalarRegisterBufferIndex;
        private readonly List<uint> _interfaces = [];
        private readonly Dictionary<uint, uint> _pixelInputs = [];
        private readonly Dictionary<uint, SpirvPixelOutput> _pixelOutputs = [];
        private readonly Dictionary<uint, uint> _vertexOutputs = [];
        private readonly Dictionary<uint, SpirvVertexInput> _vertexInputsByPc = [];
        private readonly List<SpirvImageResource> _imageResources = [];
        private readonly Dictionary<uint, int> _imageBindingByPc = [];
        private readonly Dictionary<uint, int> _bufferBindingByPc = [];
        private uint _voidType;
        private uint _boolType;
        private uint _uintType;
        private uint _intType;
        private uint _longType;
        private uint _ulongType;
        private uint _floatType;
        private uint _vec2Type;
        private uint _vec3Type;
        private uint _vec4Type;
        private uint _uvec2Type;
        private uint _uvec3Type;
        private uint _uvec4Type;
        private uint _privateUintPointer;
        private uint _privateBoolPointer;
        private uint _scalarRegisters;
        private uint _vectorRegisters;
        private uint _scc;
        private uint _vcc;
        private uint _exec;
        private uint _programCounter;
        private uint _programActive;
        private uint _globalBuffers;
        private uint _storageUintPointer;
        private uint _lds;
        private uint _workgroupUintPointer;
        private uint _positionOutput;
        private uint _vertexIndexInput;
        private uint _instanceIndexInput;
        private uint _fragCoordInput;
        private uint _localInvocationIdInput;
        private uint _workGroupIdInput;
        private uint _subgroupInvocationIdInput;
        private uint _glsl;

        private enum ImageComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        private readonly record struct SpirvImageResource(
            uint Variable,
            uint ImageType,
            uint ObjectType,
            uint ComponentType,
            uint VectorType,
            ImageComponentKind ComponentKind,
            bool IsStorage);

        private readonly record struct SpirvVertexInput(
            uint Variable,
            uint Type,
            uint ComponentCount);

        private readonly record struct SpirvPixelOutput(
            uint Variable,
            uint Type,
            Gen5PixelOutputKind Kind);

        public CompilationContext(
            Gen5SpirvStage stage,
            Gen5ShaderState state,
            Gen5ShaderEvaluation evaluation,
            IReadOnlyList<Gen5PixelOutputBinding> pixelOutputBindings,
            uint localSizeX,
            uint localSizeY,
            uint localSizeZ,
            int globalBufferBase,
            int totalGlobalBufferCount,
            int imageBindingBase,
            int scalarRegisterBufferIndex)
        {
            _stage = stage;
            _state = state;
            _evaluation = evaluation;
            _pixelOutputBindings = pixelOutputBindings;
            _localSizeX = localSizeX;
            _localSizeY = localSizeY;
            _localSizeZ = localSizeZ;
            _globalBufferBase = globalBufferBase;
            _totalGlobalBufferCount = totalGlobalBufferCount < 0
                ? evaluation.GlobalMemoryBindings.Count
                : totalGlobalBufferCount;
            _imageBindingBase = imageBindingBase;
            _scalarRegisterBufferIndex = scalarRegisterBufferIndex;
        }

        public bool TryCompile(out Gen5SpirvShader shader, out string error)
        {
            shader = default!;
            error = string.Empty;
            try
            {
                DeclareModule();
                var blocks = BuildBasicBlocks(_state.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                var functionType = _module.TypeFunction(_voidType);
                var main = _module.BeginFunction(_voidType, functionType);
                _module.AddName(main, "main");
                _module.AddLabel();
                EmitInitialState();

                var loopHeader = _module.AllocateId();
                var switchHeader = _module.AllocateId();
                var switchMerge = _module.AllocateId();
                var loopContinue = _module.AllocateId();
                var loopMerge = _module.AllocateId();
                var defaultLabel = _module.AllocateId();
                var caseLabels = new uint[blocks.Count];
                for (var index = 0; index < caseLabels.Length; index++)
                {
                    caseLabels[index] = _module.AllocateId();
                }

                _module.AddStatement(SpirvOp.Branch, loopHeader);
                _module.AddLabel(loopHeader);
                _module.AddStatement(SpirvOp.LoopMerge, loopMerge, loopContinue, 0);
                _module.AddStatement(SpirvOp.Branch, switchHeader);

                _module.AddLabel(switchHeader);
                var selector = Load(_uintType, _programCounter);
                _module.AddStatement(SpirvOp.SelectionMerge, switchMerge, 0);
                var switchOperands = new uint[2 + (blocks.Count * 2)];
                switchOperands[0] = selector;
                switchOperands[1] = defaultLabel;
                for (var index = 0; index < blocks.Count; index++)
                {
                    switchOperands[2 + (index * 2)] = (uint)index;
                    switchOperands[3 + (index * 2)] = caseLabels[index];
                }

                _module.AddStatement(SpirvOp.Switch, switchOperands);
                for (var index = 0; index < blocks.Count; index++)
                {
                    _module.AddLabel(caseLabels[index]);
                    if (!TryEmitBlock(blocks, index, out error))
                    {
                        error = $"block=0x{blocks[index].StartPc:X}: {error}";
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Branch, switchMerge);
                }

                _module.AddLabel(defaultLabel);
                Store(_programActive, _module.ConstantBool(false));
                _module.AddStatement(SpirvOp.Branch, switchMerge);

                _module.AddLabel(switchMerge);
                _module.AddStatement(SpirvOp.Branch, loopContinue);
                _module.AddLabel(loopContinue);
                var active = Load(_boolType, _programActive);
                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    active,
                    loopHeader,
                    loopMerge);
                _module.AddLabel(loopMerge);
                _module.AddStatement(SpirvOp.Return);
                _module.EndFunction();

                var model = _stage switch
                {
                    Gen5SpirvStage.Vertex => SpirvExecutionModel.Vertex,
                    Gen5SpirvStage.Pixel => SpirvExecutionModel.Fragment,
                    _ => SpirvExecutionModel.GLCompute,
                };
                _module.AddEntryPoint(model, main, "main", _interfaces);
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    _module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
                }
                else if (_stage == Gen5SpirvStage.Compute)
                {
                    _module.AddExecutionMode(
                        main,
                        SpirvExecutionMode.LocalSize,
                        _localSizeX,
                        _localSizeY,
                        _localSizeZ);
                }

                var attributeCount = _stage == Gen5SpirvStage.Vertex
                    ? (uint)_vertexOutputs.Count
                    : (uint)_pixelInputs.Count;
                shader = new Gen5SpirvShader(
                    _module.Build(),
                    _evaluation.GlobalMemoryBindings,
                    _evaluation.ImageBindings,
                    attributeCount,
                    _stage == Gen5SpirvStage.Vertex
                        ? _evaluation.VertexInputs ?? []
                        : []);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void DeclareModule()
        {
            _module.AddCapability(SpirvCapability.Shader);
            _module.AddCapability(SpirvCapability.Int64);
            _module.AddCapability(SpirvCapability.ImageQuery);
            if (_evaluation.ImageBindings.Any(
                    static binding =>
                        (binding.Opcode.StartsWith(
                             "ImageSample",
                             StringComparison.Ordinal) ||
                         binding.Opcode.StartsWith(
                             "ImageGather4",
                             StringComparison.Ordinal)) &&
                        binding.Opcode.EndsWith("O", StringComparison.Ordinal)))
            {
                _module.AddCapability(SpirvCapability.ImageGatherExtended);
            }

            if (UsesSubgroupOperations())
            {
                _module.AddCapability(SpirvCapability.GroupNonUniform);
                if (UsesSubgroupShuffle())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformShuffle);
                }

                if (UsesWaveControl())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformVote);
                }
            }

            _glsl = _module.ImportExtInst("GLSL.std.450");
            _voidType = _module.TypeVoid();
            _boolType = _module.TypeBool();
            _uintType = _module.TypeInt(32, signed: false);
            _intType = _module.TypeInt(32, signed: true);
            _longType = _module.TypeInt(64, signed: true);
            _ulongType = _module.TypeInt(64, signed: false);
            _floatType = _module.TypeFloat(32);
            _vec2Type = _module.TypeVector(_floatType, 2);
            _vec3Type = _module.TypeVector(_floatType, 3);
            _vec4Type = _module.TypeVector(_floatType, 4);
            _uvec2Type = _module.TypeVector(_uintType, 2);
            _uvec3Type = _module.TypeVector(_uintType, 3);
            _uvec4Type = _module.TypeVector(_uintType, 4);
            _privateUintPointer =
                _module.TypePointer(SpirvStorageClass.Private, _uintType);
            _privateBoolPointer =
                _module.TypePointer(SpirvStorageClass.Private, _boolType);

            var scalarArrayType = _module.TypeArray(_uintType, ScalarRegisterCount);
            var vectorArrayType = _module.TypeArray(_uintType, VectorRegisterCount);
            var privateScalarArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, scalarArrayType);
            var privateVectorArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, vectorArrayType);
            _scalarRegisters = _module.AddGlobalVariable(
                privateScalarArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(scalarArrayType));
            _vectorRegisters = _module.AddGlobalVariable(
                privateVectorArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(vectorArrayType));
            _scc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _vcc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _exec = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            _programCounter = _module.AddGlobalVariable(
                _privateUintPointer,
                SpirvStorageClass.Private,
                _module.Constant(_uintType, 0));
            _programActive = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            _interfaces.Add(_scalarRegisters);
            _interfaces.Add(_vectorRegisters);
            _interfaces.Add(_scc);
            _interfaces.Add(_vcc);
            _interfaces.Add(_exec);
            _interfaces.Add(_programCounter);
            _interfaces.Add(_programActive);
            _module.AddName(_scalarRegisters, "sgpr");
            _module.AddName(_vectorRegisters, "vgpr");

            DeclareBuffers();
            DeclareImages();
            DeclareLds();
            DeclareStageInterface();
        }

        private void DeclareLds()
        {
            if (_stage != Gen5SpirvStage.Compute || !UsesLds())
            {
                return;
            }

            var ldsArrayType = _module.TypeArray(_uintType, LdsDwordCount);
            var ldsPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, ldsArrayType);
            _workgroupUintPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _lds = _module.AddGlobalVariable(
                ldsPointer,
                SpirvStorageClass.Workgroup);
            _module.AddName(_lds, "lds");
            _interfaces.Add(_lds);
        }

        private void DeclareBuffers()
        {
            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                foreach (var pc in _evaluation.GlobalMemoryBindings[index].InstructionPcs)
                {
                    _bufferBindingByPc.TryAdd(pc, _globalBufferBase + index);
                }
            }

            if (_totalGlobalBufferCount == 0)
            {
                return;
            }

            var runtimeArray = _module.TypeRuntimeArray(_uintType);
            _module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, sizeof(uint));
            var block = _module.TypeStruct(runtimeArray);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var descriptors = _module.TypeArray(
                block,
                (uint)_totalGlobalBufferCount);
            var descriptorsPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, descriptors);
            _storageUintPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, _uintType);
            _globalBuffers = _module.AddGlobalVariable(
                descriptorsPointer,
                SpirvStorageClass.StorageBuffer);
            _module.AddName(_globalBuffers, "guestBuffers");
            _module.AddDecoration(_globalBuffers, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(_globalBuffers, SpirvDecoration.Binding, 0);
            _interfaces.Add(_globalBuffers);
        }

        private void DeclareImages()
        {
            for (var index = 0; index < _evaluation.ImageBindings.Count; index++)
            {
                var binding = _evaluation.ImageBindings[index];
                _imageBindingByPc.TryAdd(binding.Pc, index);
                var isStorage =
                    Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
                var (format, componentKind) =
                    DecodeImageFormat(binding.ResourceDescriptor);
                var componentType = componentKind switch
                {
                    ImageComponentKind.Sint => _intType,
                    ImageComponentKind.Uint => _uintType,
                    _ => _floatType,
                };
                if (isStorage && format == SpirvImageFormat.Unknown)
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageReadWithoutFormat);
                    _module.AddCapability(
                        SpirvCapability.StorageImageWriteWithoutFormat);
                }
                else if (isStorage && RequiresExtendedStorageImageFormat(format))
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageExtendedFormats);
                }

                var imageType = _module.TypeImage(
                    componentType,
                    SpirvImageDim.Dim2D,
                    depth: false,
                    arrayed: false,
                    multisampled: false,
                    sampled: isStorage ? 2u : 1u,
                    isStorage ? format : SpirvImageFormat.Unknown);
                var objectType = isStorage
                    ? imageType
                    : _module.TypeSampledImage(imageType);
                var pointer = _module.TypePointer(
                    SpirvStorageClass.UniformConstant,
                    objectType);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.UniformConstant);
                _module.AddName(variable, isStorage ? $"image{index}" : $"tex{index}");
                _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Binding,
                    (uint)(_imageBindingBase + index + 1));
                _imageResources.Add(
                    new SpirvImageResource(
                        variable,
                        imageType,
                        objectType,
                        componentType,
                        _module.TypeVector(componentType, 4),
                        componentKind,
                        isStorage));
                _interfaces.Add(variable);
            }
        }

        private static bool RequiresExtendedStorageImageFormat(
            SpirvImageFormat format) =>
            format is not SpirvImageFormat.Unknown and
                not SpirvImageFormat.Rgba32f and
                not SpirvImageFormat.Rgba32i and
                not SpirvImageFormat.Rgba32ui;

        private static (SpirvImageFormat Format, ImageComponentKind Kind)
            DecodeImageFormat(IReadOnlyList<uint> descriptor)
        {
            if (descriptor.Count < 2)
            {
                return (SpirvImageFormat.Unknown, ImageComponentKind.Float);
            }

            var dataFormat = (descriptor[1] >> 20) & 0x1FFu;
            var numberType = (descriptor[1] >> 26) & 0xFu;
            return (dataFormat, numberType) switch
            {
                (1, _) => (SpirvImageFormat.R8, ImageComponentKind.Float),
                (2, _) => (SpirvImageFormat.R16f, ImageComponentKind.Float),
                (3, _) => (SpirvImageFormat.Rg8, ImageComponentKind.Float),
                (4, 4) => (SpirvImageFormat.R32ui, ImageComponentKind.Uint),
                (4, 5) => (SpirvImageFormat.R32i, ImageComponentKind.Sint),
                (4, _) => (SpirvImageFormat.R32f, ImageComponentKind.Float),
                (5, 4) => (SpirvImageFormat.Rg16ui, ImageComponentKind.Uint),
                (5, 5) => (SpirvImageFormat.Rg16i, ImageComponentKind.Sint),
                (5, 0) => (SpirvImageFormat.Rg16, ImageComponentKind.Float),
                (5, _) => (SpirvImageFormat.Rg16f, ImageComponentKind.Float),
                (6 or 7, _) => (
                    SpirvImageFormat.R11fG11fB10f,
                    ImageComponentKind.Float),
                (9, 4) => (SpirvImageFormat.Rgb10A2ui, ImageComponentKind.Uint),
                (9, _) => (SpirvImageFormat.Rgb10A2, ImageComponentKind.Float),
                (10, 4) => (SpirvImageFormat.Rgba8ui, ImageComponentKind.Uint),
                (10, 5) => (SpirvImageFormat.Rgba8i, ImageComponentKind.Sint),
                (10, _) => (SpirvImageFormat.Rgba8, ImageComponentKind.Float),
                (11, 4) => (SpirvImageFormat.Rg32ui, ImageComponentKind.Uint),
                (11, 5) => (SpirvImageFormat.Rg32i, ImageComponentKind.Sint),
                (11, _) => (SpirvImageFormat.Rg32f, ImageComponentKind.Float),
                (12, 4) => (SpirvImageFormat.Rgba16ui, ImageComponentKind.Uint),
                (12, 5) => (SpirvImageFormat.Rgba16i, ImageComponentKind.Sint),
                (12, 0) => (SpirvImageFormat.Rgba16, ImageComponentKind.Float),
                (12, _) => (SpirvImageFormat.Rgba16f, ImageComponentKind.Float),
                (13 or 14, 4) => (
                    SpirvImageFormat.Rgba32ui,
                    ImageComponentKind.Uint),
                (13 or 14, 5) => (
                    SpirvImageFormat.Rgba32i,
                    ImageComponentKind.Sint),
                (13 or 14, _) => (
                    SpirvImageFormat.Rgba32f,
                    ImageComponentKind.Float),
                (20, _) => (SpirvImageFormat.R32ui, ImageComponentKind.Uint),
                (22, _) => (SpirvImageFormat.Rgba16f, ImageComponentKind.Float),
                (29, _) => (SpirvImageFormat.R32f, ImageComponentKind.Float),
                (36, _) => (SpirvImageFormat.R8, ImageComponentKind.Float),
                (49, _) => (SpirvImageFormat.R8ui, ImageComponentKind.Uint),
                (56 or 62 or 64, _) => (
                    SpirvImageFormat.Rgba8,
                    ImageComponentKind.Float),
                (71, _) => (SpirvImageFormat.Rgba16f, ImageComponentKind.Float),
                (75, _) => (SpirvImageFormat.Rg32f, ImageComponentKind.Float),
                (_, 4) => (SpirvImageFormat.Unknown, ImageComponentKind.Uint),
                (_, 5) => (SpirvImageFormat.Unknown, ImageComponentKind.Sint),
                _ => (SpirvImageFormat.Unknown, ImageComponentKind.Float),
            };
        }

        private void DeclareStageInterface()
        {
            if (UsesSubgroupOperations())
            {
                var subgroupPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                _subgroupInvocationIdInput = _module.AddGlobalVariable(
                    subgroupPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _subgroupInvocationIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
                _interfaces.Add(_subgroupInvocationIdInput);
            }

            if (_stage == Gen5SpirvStage.Vertex)
            {
                DeclareVertexInputs();

                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                _vertexIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _vertexIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.VertexIndex);
                _interfaces.Add(_vertexIndexInput);

                _instanceIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _instanceIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.InstanceIndex);
                _interfaces.Add(_instanceIndexInput);

                var outputPointer =
                    _module.TypePointer(SpirvStorageClass.Output, _vec4Type);
                _positionOutput = _module.AddGlobalVariable(
                    outputPointer,
                    SpirvStorageClass.Output);
                _module.AddDecoration(
                    _positionOutput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.Position);
                _interfaces.Add(_positionOutput);

                var parameters = _state.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5ExportControl>()
                    .Where(export => export.Target is >= 32 and < 64)
                    .Select(export => export.Target - 32)
                    .Distinct()
                    .Order()
                    .ToArray();
                foreach (var parameter in parameters)
                {
                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddDecoration(variable, SpirvDecoration.Location, parameter);
                    _vertexOutputs.Add(parameter, variable);
                    _interfaces.Add(variable);
                }
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var inputVec4Pointer =
                    _module.TypePointer(SpirvStorageClass.Input, _vec4Type);
                var attributes = _state.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5InterpolationControl>()
                    .Select(control => control.Attribute)
                    .Distinct()
                    .Order()
                    .ToArray();
                foreach (var attribute in attributes)
                {
                    var variable = _module.AddGlobalVariable(
                        inputVec4Pointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(variable, SpirvDecoration.Location, attribute);
                    _pixelInputs.Add(attribute, variable);
                    _interfaces.Add(variable);
                }

                _fragCoordInput = _module.AddGlobalVariable(
                    inputVec4Pointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _fragCoordInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.FragCoord);
                _interfaces.Add(_fragCoordInput);

                foreach (var binding in _pixelOutputBindings)
                {
                    var outputType = GetPixelOutputType(binding.Kind);
                    var outputPointer =
                        _module.TypePointer(SpirvStorageClass.Output, outputType);
                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddName(variable, $"mrt{binding.GuestSlot}");
                    _module.AddDecoration(
                        variable,
                        SpirvDecoration.Location,
                        binding.HostLocation);
                    _pixelOutputs.Add(
                        binding.GuestSlot,
                        new SpirvPixelOutput(variable, outputType, binding.Kind));
                    _interfaces.Add(variable);
                }
            }
            else
            {
                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uvec3Type);
                _localInvocationIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _localInvocationIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.LocalInvocationId);
                _workGroupIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _workGroupIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.WorkgroupId);
                _interfaces.Add(_localInvocationIdInput);
                _interfaces.Add(_workGroupIdInput);
            }
        }

        private void DeclareVertexInputs()
        {
            foreach (var input in _evaluation.VertexInputs ?? [])
            {
                var type = input.ComponentCount switch
                {
                    1u => _floatType,
                    2u => _vec2Type,
                    3u => _vec3Type,
                    4u => _vec4Type,
                    _ => 0u,
                };
                if (type == 0)
                {
                    continue;
                }

                var pointer = _module.TypePointer(SpirvStorageClass.Input, type);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.Input);
                _module.AddName(variable, $"attr{input.Location}");
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Location,
                    input.Location);
                _vertexInputsByPc.TryAdd(
                    input.Pc,
                    new SpirvVertexInput(
                        variable,
                        type,
                        input.ComponentCount));
                _interfaces.Add(variable);
            }
        }

        private void EmitInitialState()
        {
            if (_scalarRegisterBufferIndex >= 0)
            {
                for (uint index = 0; index < ScalarRegisterCount; index++)
                {
                    StoreS(index, LoadBufferWord(_scalarRegisterBufferIndex, UInt(index)));
                }
            }
            else
            {
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterCount;
                     index++)
                {
                    var value = _evaluation.InitialScalarRegisters[(int)index];
                    if (value != 0)
                    {
                        StoreS(index, UInt(value));
                    }
                }
            }

            Store(_scc, _module.ConstantBool(false));
            if (_subgroupInvocationIdInput != 0)
            {
                StoreWaveMask(106, _module.ConstantBool(false));
                StoreWaveMask(126, _module.ConstantBool(true));
            }
            else
            {
                Store(_vcc, _module.ConstantBool(false));
                Store(_exec, _module.ConstantBool(true));
            }
            Store(_programCounter, UInt(0));
            Store(_programActive, _module.ConstantBool(true));

            if (_stage == Gen5SpirvStage.Vertex)
            {
                StoreV(5, Load(_uintType, _vertexIndexInput), guardWithExec: false);
                StoreV(8, Load(_uintType, _instanceIndexInput), guardWithExec: false);
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var fragCoord = Load(_vec4Type, _fragCoordInput);
                var x = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    fragCoord,
                    0);
                var y = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    fragCoord,
                    1);
                StoreV(2, Bitcast(_uintType, x), guardWithExec: false);
                StoreV(3, Bitcast(_uintType, y), guardWithExec: false);
                foreach (var output in _pixelOutputs.Values)
                {
                    Store(output.Variable, _module.ConstantNull(output.Type));
                }
            }
            else
            {
                var localId = Load(_uvec3Type, _localInvocationIdInput);
                for (uint component = 0; component < 3; component++)
                {
                    var value = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        localId,
                        component);
                    StoreV(component, value, guardWithExec: false);
                }

                if (_state.ComputeSystemRegisters is { } registers)
                {
                    var workGroupId = Load(_uvec3Type, _workGroupIdInput);
                    StoreComputeSystemRegister(
                        registers.WorkGroupXRegister,
                        workGroupId,
                        0);
                    StoreComputeSystemRegister(
                        registers.WorkGroupYRegister,
                        workGroupId,
                        1);
                    StoreComputeSystemRegister(
                        registers.WorkGroupZRegister,
                        workGroupId,
                        2);
                    if (registers.ThreadGroupSizeRegister is { } sizeRegister)
                    {
                        StoreS(
                            sizeRegister,
                            UInt(checked(_localSizeX * _localSizeY * _localSizeZ)));
                    }
                }
            }
        }

        private void StoreComputeSystemRegister(
            uint? register,
            uint workGroupId,
            uint component)
        {
            if (register is null)
            {
                return;
            }

            var value = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                workGroupId,
                component);
            StoreS(register.Value, value);
        }

        private bool TryEmitBlock(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            out string error)
        {
            error = string.Empty;
            var block = blocks[blockIndex];
            for (var index = block.StartIndex; index < block.EndIndex; index++)
            {
                var instruction = _state.Program.Instructions[index];
                if (IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm")
                {
                    continue;
                }

                if (!TryEmitInstruction(instruction, out error))
                {
                    error = $"pc=0x{instruction.Pc:X} {instruction.Opcode}: {error}";
                    return false;
                }
            }

            var terminator = _state.Program.Instructions[block.EndIndex - 1];
            if (terminator.Opcode == "SEndpgm")
            {
                Store(_programActive, _module.ConstantBool(false));
                return true;
            }

            var fallthrough = blockIndex + 1 < blocks.Count
                ? (uint)(blockIndex + 1)
                : uint.MaxValue;
            if (terminator.Opcode == "SBranch")
            {
                if (!TryGetBranchTargetPc(terminator, out var targetPc))
                {
                    error = "invalid scalar branch target";
                    return false;
                }

                if (IsExitBranchTarget(_state.Program.Instructions, targetPc))
                {
                    Store(_programActive, _module.ConstantBool(false));
                    return true;
                }

                if (!TryFindBlock(blocks, targetPc, out var targetBlock))
                {
                    error = $"invalid scalar branch target pc=0x{terminator.Pc:X} target=0x{targetPc:X} blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                Store(_programCounter, UInt((uint)targetBlock));
                return true;
            }

            if (terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                var hasTarget = TryGetBranchTargetPc(terminator, out var targetPc);
                var targetBlock = -1;
                var hasTargetBlock = hasTarget && TryFindBlock(blocks, targetPc, out targetBlock);
                var targetExits = hasTarget && IsExitBranchTarget(_state.Program.Instructions, targetPc);
                var hasCondition = TryGetBranchCondition(terminator.Opcode, out var condition);
                if (!hasTarget || (!hasTargetBlock && !targetExits) || !hasCondition)
                {
                    error =
                        $"invalid conditional scalar branch opcode={terminator.Opcode} " +
                        $"pc=0x{terminator.Pc:X} " +
                        $"target={(hasTarget ? $"0x{targetPc:X}" : "invalid")} " +
                        $"target_block={(hasTargetBlock ? targetBlock.ToString() : targetExits ? "exit" : "missing")} " +
                        $"fallthrough={(fallthrough == uint.MaxValue ? "end" : fallthrough.ToString())} " +
                        $"condition={hasCondition} " +
                        $"blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                var takenBlock = targetExits ? uint.MaxValue : (uint)targetBlock;
                var selected = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    condition,
                    UInt(takenBlock),
                    UInt(fallthrough));
                Store(_programCounter, selected);
                return true;
            }

            if (fallthrough == uint.MaxValue)
            {
                Store(_programActive, _module.ConstantBool(false));
            }
            else
            {
                Store(_programCounter, UInt(fallthrough));
            }

            return true;
        }

        private static string FormatBlockStarts(IReadOnlyList<ShaderBlock> blocks)
        {
            const int maxBlocks = 32;
            var count = Math.Min(blocks.Count, maxBlocks);
            var starts = new string[count];
            for (var index = 0; index < count; index++)
            {
                starts[index] = $"0x{blocks[index].StartPc:X}";
            }

            return blocks.Count <= maxBlocks
                ? string.Join(",", starts)
                : string.Join(",", starts) + $",...({blocks.Count})";
        }

        private static bool IsExitBranchTarget(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint targetPc)
        {
            if (instructions.Count == 0)
            {
                return false;
            }

            var last = instructions[^1];
            var lastEndPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
            return targetPc >= lastEndPc;
        }

        private bool TryGetBranchCondition(string opcode, out uint condition)
        {
            condition = opcode switch
            {
                "SCbranchScc0" => LogicalNot(Load(_boolType, _scc)),
                "SCbranchScc1" => Load(_boolType, _scc),
                "SCbranchVccz" => LogicalNot(SubgroupAny(Load(_boolType, _vcc))),
                "SCbranchVccnz" => SubgroupAny(Load(_boolType, _vcc)),
                "SCbranchExecz" => LogicalNot(SubgroupAny(Load(_boolType, _exec))),
                "SCbranchExecnz" => SubgroupAny(Load(_boolType, _exec)),
                _ => 0,
            };
            return condition != 0;
        }

        private bool TryEmitInstruction(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode is
                "SNop" or
                "SWaitcnt" or
                "SInstPrefetch" or
                "STtraceData" or
                "VInterpMovF32")
            {
                return true;
            }

            if (instruction.Opcode == "SBarrier")
            {
                var workgroup = UInt(2);
                var semantics = UInt(0x108);
                _module.AddStatement(
                    SpirvOp.ControlBarrier,
                    workgroup,
                    workgroup,
                    semantics);
                return true;
            }

            if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
            {
                return TryEmitScalarMemory(instruction, scalarMemory, out error);
            }

            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                return TryEmitInterpolation(instruction, interpolation, out error);
            }

            if (instruction.Control is Gen5ImageControl image)
            {
                return TryEmitImage(instruction, image, out error);
            }

            if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                return TryEmitGlobalMemory(instruction, globalMemory, out error);
            }

            if (instruction.Control is Gen5BufferMemoryControl bufferMemory)
            {
                return TryEmitBufferMemory(instruction, bufferMemory, out error);
            }

            if (instruction.Control is Gen5ExportControl export)
            {
                return TryEmitExport(instruction, export, out error);
            }

            if (instruction.Control is Gen5DataShareControl)
            {
                return TryEmitDataShare(instruction, out error);
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk)
            {
                return TryEmitScalarAlu(instruction, out error);
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sopp or
                Gen5ShaderEncoding.Smrd or
                Gen5ShaderEncoding.Smem)
            {
                return true;
            }

            return TryEmitVectorAlu(instruction, out error);
        }

        private bool TryEmitDataShare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5SpirvStage.Compute ||
                _lds == 0 ||
                _workgroupUintPointer == 0 ||
                instruction.Control is not Gen5DataShareControl control)
            {
                error = "invalid LDS instruction";
                return false;
            }

            if (control.Gds)
            {
                error = "GDS data share is not implemented";
                return false;
            }

            switch (instruction.Opcode)
            {
                case "DsWriteB32":
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing LDS write source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    StoreLds(
                        LdsPointer(address, EffectiveDsOffsetBytes(control.Offset0)),
                        GetRawSource(instruction, 1));
                    return true;
                }
                case "DsWriteB64":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS write64 source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = EffectiveDsOffsetBytes(control.Offset0);
                    StoreLds(LdsPointer(address, offset), GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(address, offset + sizeof(uint)),
                        GetRawSource(instruction, 2));
                    return true;
                }
                case "DsWrite2B32":
                case "DsWrite2St64B32":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS write2 source";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsWrite2St64B32";
                    var address = GetRawSource(instruction, 0);
                    StoreLds(
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset0, st64)),
                        GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset1, st64)),
                        GetRawSource(instruction, 2));
                    return true;
                }
                case "DsReadB32":
                {
                    if (instruction.Destinations.Count < 1 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var value = Load(
                        _uintType,
                        LdsPointer(address, EffectiveDsOffsetBytes(control.Offset0)));
                    StoreV(instruction.Destinations[0].Value, value);
                    return true;
                }
                case "DsRead2B32":
                case "DsRead2St64B32":
                {
                    if (instruction.Destinations.Count < 2 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read2 operand";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsRead2St64B32";
                    var address = GetRawSource(instruction, 0);
                    var first = Load(
                        _uintType,
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset0, st64)));
                    var second = Load(
                        _uintType,
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset1, st64)));
                    StoreV(instruction.Destinations[0].Value, first);
                    StoreV(instruction.Destinations[1].Value, second);
                    return true;
                }
                default:
                    error = $"unsupported LDS opcode {instruction.Opcode}";
                    return false;
            }
        }

        private static uint EffectiveDsOffsetBytes(uint offset, bool st64 = false) =>
            offset * (st64 ? 256u : sizeof(uint));

        private uint LdsPointer(uint address, uint offsetBytes)
        {
            var addressWithOffset = offsetBytes == 0
                ? address
                : IAdd(address, UInt(offsetBytes));
            var index = ShiftRightLogical(addressWithOffset, UInt(2));
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _workgroupUintPointer,
                _lds,
                index);
        }

        private void StoreLds(uint pointer, uint value)
        {
            var active = Load(_boolType, _exec);
            var oldValue = Load(_uintType, pointer);
            var selected = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                active,
                value,
                oldValue);
            Store(pointer, selected);
        }

        private bool TryEmitInterpolation(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5SpirvStage.Pixel ||
                !_pixelInputs.TryGetValue(interpolation.Attribute, out var input) ||
                !TryGetVectorDestination(instruction, out var destination))
            {
                error = "invalid interpolated attribute";
                return false;
            }

            var vector = Load(_vec4Type, input);
            var component = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                vector,
                interpolation.Channel);
            StoreV(destination, Bitcast(_uintType, component));
            return true;
        }

        private bool TryEmitScalarMemory(
            Gen5ShaderInstruction instruction,
            Gen5ScalarMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.ScalarRegister)
                    {
                        StoreS(destination.Value, UInt(0));
                    }
                }

                return true;
            }

            var dynamicOffset = control.DynamicOffsetRegister is { } register
                ? LoadS(register)
                : UInt(0);
            var byteAddress = IAdd(
                dynamicOffset,
                UInt(unchecked((uint)control.ImmediateOffsetBytes)));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            for (var index = 0; index < instruction.Destinations.Count; index++)
            {
                var destination = instruction.Destinations[index];
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "invalid scalar-memory destination";
                    return false;
                }

                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt((uint)index));
                StoreS(destination.Value, LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private bool TryEmitGlobalMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                error = "missing global-memory binding";
                return false;
            }

            var byteAddress = IAdd(
                LoadV(control.VectorAddress),
                UInt(unchecked((uint)control.OffsetBytes)));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt(index));
                StoreV(
                    control.VectorData + index,
                    LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private bool TryEmitBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_stage == Gen5SpirvStage.Vertex &&
                _vertexInputsByPc.TryGetValue(instruction.Pc, out var vertexInput))
            {
                return TryEmitVertexInputFetch(control, vertexInput, out error);
            }

            if (_stage == Gen5SpirvStage.Vertex &&
                IsFormatBufferLoad(instruction.Opcode))
            {
                error = $"missing vertex input for {instruction.Opcode} pc=0x{instruction.Pc:X}";
                return false;
            }

            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                error = "missing buffer-memory binding";
                return false;
            }

            var scalarOffset = instruction.Sources.Count > 2
                ? GetRawSource(instruction, 2)
                : UInt(0);
            var stride = ShiftRightLogical(LoadS(control.ScalarResource + 1), UInt(16));
            stride = BitwiseAnd(stride, UInt(0x3FFF));
            var vectorIndex = control.IndexEnabled
                ? LoadV(control.VectorAddress)
                : UInt(0);
            var vectorOffset = control.OffsetEnabled
                ? LoadV(control.VectorAddress + (control.IndexEnabled ? 1u : 0u))
                : UInt(0);
            var byteAddress = IAdd(
                UInt(unchecked((uint)control.OffsetBytes)),
                scalarOffset);
            byteAddress = IAdd(byteAddress, vectorOffset);
            byteAddress = IAdd(
                byteAddress,
                _module.AddInstruction(SpirvOp.IMul, _uintType, vectorIndex, stride));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode == "BufferAtomicAdd")
            {
                EmitExecConditional(() =>
                {
                    var original = _module.AddInstruction(
                        SpirvOp.AtomicIAdd,
                        _uintType,
                        BufferWordPointer(bindingIndex, dwordAddress),
                        UInt(1),
                        UInt(0x48),
                        LoadV(control.VectorData));
                    if (control.Glc)
                    {
                        StoreV(control.VectorData, original);
                    }
                });

                return true;
            }

            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var address = index == 0
                            ? dwordAddress
                            : IAdd(dwordAddress, UInt(index));
                        StoreBufferWord(
                            bindingIndex,
                            address,
                            LoadV(control.VectorData + index));
                    }
                });

                return true;
            }

            if (!instruction.Opcode.StartsWith("BufferLoad", StringComparison.Ordinal) &&
                !instruction.Opcode.StartsWith("TBufferLoad", StringComparison.Ordinal))
            {
                error = $"unsupported buffer opcode {instruction.Opcode}";
                return false;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt(index));
                StoreV(
                    control.VectorData + index,
                    LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private static bool IsFormatBufferLoad(string opcode) =>
            opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
            opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal);

        private bool TryEmitVertexInputFetch(
            Gen5BufferMemoryControl control,
            SpirvVertexInput input,
            out string error)
        {
            error = string.Empty;
            if (control.DwordCount == 0 ||
                control.DwordCount > input.ComponentCount)
            {
                error =
                    $"invalid vertex input fetch components={control.DwordCount} " +
                    $"input={input.ComponentCount}";
                return false;
            }

            var loaded = Load(input.Type, input.Variable);
            for (uint component = 0; component < control.DwordCount; component++)
            {
                var value = input.ComponentCount == 1
                    ? loaded
                    : _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        loaded,
                        component);
                StoreV(control.VectorData + component, Bitcast(_uintType, value));
            }

            return true;
        }

        private bool TryEmitImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string error)
        {
            error = string.Empty;
            if (!_imageBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex) ||
                bindingIndex >= _imageResources.Count)
            {
                error = "unresolved image binding";
                return false;
            }

            var resource = _imageResources[bindingIndex];
            var imageObject = Load(resource.ObjectType, resource.Variable);
            if (instruction.Opcode == "ImageGetResinfo")
            {
                var queryImage = resource.IsStorage
                    ? imageObject
                    : _module.AddInstruction(
                        SpirvOp.Image,
                        resource.ImageType,
                        imageObject);
                var size = _module.AddInstruction(
                    resource.IsStorage
                        ? SpirvOp.ImageQuerySize
                        : SpirvOp.ImageQuerySizeLod,
                    _module.TypeVector(_intType, 2),
                    resource.IsStorage
                        ? [queryImage]
                        : [queryImage, UInt(0)]);
                uint outputIndex = 0;
                for (uint component = 0; component < 4; component++)
                {
                    if ((image.Dmask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    uint value;
                    if (component < 2)
                    {
                        var signedValue = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _intType,
                            size,
                            component);
                        value = Bitcast(_uintType, signedValue);
                    }
                    else
                    {
                        value = UInt(1);
                    }

                    StoreV(image.VectorData + outputIndex++, value);
                }

                return true;
            }

            if (instruction.Opcode is "ImageStore" or "ImageStoreMip")
            {
                if (!resource.IsStorage)
                {
                    error = "image store is not bound as storage";
                    return false;
                }

                var coordinates = BuildIntegerCoordinates(image, 0);
                var components = new uint[4];
                uint sourceIndex = 0;
                for (var component = 0; component < components.Length; component++)
                {
                    if ((image.Dmask & (1u << component)) != 0)
                    {
                        var raw = LoadV(image.VectorData + sourceIndex++);
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint => Bitcast(_intType, raw),
                            ImageComponentKind.Uint => raw,
                            _ => Bitcast(_floatType, raw),
                        };
                    }
                    else
                    {
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint =>
                                _module.Constant(_intType, 0),
                            ImageComponentKind.Uint => UInt(0),
                            _ => Float(0),
                        };
                    }
                }

                var texel = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    resource.VectorType,
                    components);
                if (TryGetImageBounds(
                        _evaluation.ImageBindings[bindingIndex].ResourceDescriptor,
                        out var width,
                        out var height))
                {
                    EmitBoundsCheckedImageWrite(
                        coordinates,
                        width,
                        height,
                        imageObject,
                        texel);
                }
                else
                {
                    EmitExecConditional(
                        () => _module.AddStatement(
                            SpirvOp.ImageWrite,
                            imageObject,
                            coordinates,
                            texel));
                }

                return true;
            }

            if (resource.IsStorage)
            {
                error = $"unsupported storage image opcode {instruction.Opcode}";
                return false;
            }

            uint sampled;
            var writeAllComponents = false;
            if (instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                var coordinates = TryGetImageBounds(
                        _evaluation.ImageBindings[bindingIndex].ResourceDescriptor,
                        out var width,
                        out var height)
                    ? BuildClampedIntegerCoordinates(image, 0, width, height)
                    : BuildIntegerCoordinates(image, 0);
                var mipLevel = _evaluation.ImageBindings[bindingIndex].MipLevel ?? 0;
                var fetchedImage = _module.AddInstruction(
                    SpirvOp.Image,
                    resource.ImageType,
                    imageObject);
                sampled = _module.AddInstruction(
                    SpirvOp.ImageFetch,
                    resource.VectorType,
                    fetchedImage,
                    coordinates,
                    2,
                    UInt(mipLevel));
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageSample",
                         StringComparison.Ordinal))
            {
                var hasOffset =
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal);
                var hasCompare =
                    instruction.Opcode.Contains("SampleC", StringComparison.Ordinal);
                var start = (hasOffset ? 1 : 0) + (hasCompare ? 1 : 0);
                var coordinates = BuildFloatCoordinates(image, start);
                var explicitLod =
                    instruction.Opcode.Contains("Lz", StringComparison.Ordinal) ||
                    instruction.Opcode.Contains("SampleL", StringComparison.Ordinal);
                var lod = instruction.Opcode.Contains("Lz", StringComparison.Ordinal)
                    ? Float(0)
                    : Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(start + 2)));
                var offset = hasOffset ? BuildImageOffset(image, 0) : 0u;
                var imageOperands =
                    (explicitLod ? 2u : 0u) | (hasOffset ? 0x10u : 0u);
                var reference = hasCompare
                    ? Bitcast(_floatType, LoadV(image.GetAddressRegister(hasOffset ? 1 : 0)))
                    : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };

                if (imageOperands != 0)
                {
                    operands.Add(imageOperands);
                    if (explicitLod)
                    {
                        operands.Add(lod);
                    }

                    if (hasOffset)
                    {
                        operands.Add(offset);
                    }
                }

                sampled = _module.AddInstruction(
                    explicitLod
                        ? SpirvOp.ImageSampleExplicitLod
                        : SpirvOp.ImageSampleImplicitLod,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    sampled = EmitManualDepthCompare(resource, sampled, reference);
                }
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageGather4",
                         StringComparison.Ordinal))
            {
                var hasOffset =
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal);
                var hasCompare =
                    instruction.Opcode.Contains("Gather4C", StringComparison.Ordinal);
                var start = (hasOffset ? 1 : 0) + (hasCompare ? 1 : 0);
                var coordinates = BuildFloatCoordinates(image, start);
                var offset = hasOffset ? BuildImageOffset(image, 0) : 0u;
                var reference = hasCompare
                    ? Bitcast(_floatType, LoadV(image.GetAddressRegister(hasOffset ? 1 : 0)))
                    : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };
                if (hasCompare)
                {
                    operands.Add(UInt(0));
                }
                else
                {
                    uint component = 0;
                    while (component < 3 &&
                           (image.Dmask & (1u << (int)component)) == 0)
                    {
                        component++;
                    }

                    operands.Add(UInt(component));
                }

                if (hasOffset)
                {
                    operands.Add(0x10u);
                    operands.Add(offset);
                }

                sampled = _module.AddInstruction(
                    SpirvOp.ImageGather,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    var compared = new uint[4];
                    for (var component = 0u; component < 4; component++)
                    {
                        var texel = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            resource.ComponentType,
                            sampled,
                            component);
                        compared[component] = EmitDepthCompareScalar(resource, texel, reference);
                    }

                    sampled = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        resource.VectorType,
                        compared);
                }

                writeAllComponents = true;
            }
            else
            {
                error = $"unsupported image opcode {instruction.Opcode}";
                return false;
            }

            uint output = 0;
            for (uint component = 0; component < 4; component++)
            {
                if (!writeAllComponents &&
                    (image.Dmask & (1u << (int)component)) == 0)
                {
                    continue;
                }

                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    resource.ComponentType,
                    sampled,
                    component);
                var raw = resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => value,
                    _ => Bitcast(_uintType, value),
                };
                StoreV(image.VectorData + output++, raw);
            }

            return true;
        }

        private uint EmitDepthCompareScalar(
            SpirvImageResource resource,
            uint texel,
            uint reference)
        {
            var texelAsFloat = resource.ComponentKind switch
            {
                ImageComponentKind.Uint => _module.AddInstruction(
                    SpirvOp.ConvertUToF, _floatType, texel),
                ImageComponentKind.Sint => _module.AddInstruction(
                    SpirvOp.ConvertSToF, _floatType, texel),
                _ => texel,
            };
            var passes = _module.AddInstruction(
                SpirvOp.FOrdLessThanEqual,
                _boolType,
                reference,
                texelAsFloat);
            return _module.AddInstruction(
                SpirvOp.Select,
                resource.ComponentType,
                passes,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                },
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(0),
                    ImageComponentKind.Sint => _module.Constant(_intType, 0),
                    _ => Float(0),
                });
        }

        private uint EmitManualDepthCompare(
            SpirvImageResource resource,
            uint sampledVector,
            uint reference)
        {
            var texel = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                resource.ComponentType,
                sampledVector,
                0u);
            var scalar = EmitDepthCompareScalar(resource, texel, reference);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                scalar,
                scalar,
                scalar,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                });
        }

        private uint BuildFloatCoordinates(Gen5ImageControl image, int start)
        {
            var x = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start)));
            var y = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start + 1)));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec2Type,
                x,
                y);
        }

        private uint BuildIntegerCoordinates(Gen5ImageControl image, int start)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(start)));
            var y = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(start + 1)));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint BuildClampedIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            uint width,
            uint height)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadV(image.GetAddressRegister(start))),
                width);
            var y = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadV(image.GetAddressRegister(start + 1))),
                height);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint ClampSignedCoordinate(uint value, uint extent)
        {
            var zero = _module.Constant(_intType, 0);
            var max = _module.Constant(_intType, Math.Max(extent, 1) - 1);
            var belowZero = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                value,
                zero);
            var atLeastZero = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                belowZero,
                zero,
                value);
            var aboveMax = _module.AddInstruction(
                SpirvOp.SGreaterThan,
                _boolType,
                atLeastZero,
                max);
            return _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                aboveMax,
                max,
                atLeastZero);
        }

        private void EmitBoundsCheckedImageWrite(
            uint coordinates,
            uint width,
            uint height,
            uint imageObject,
            uint texel)
        {
            var x = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                coordinates,
                0);
            var y = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                coordinates,
                1);
            var zero = _module.Constant(_intType, 0);
            var xNonNegative = _module.AddInstruction(
                SpirvOp.SGreaterThanEqual,
                _boolType,
                x,
                zero);
            var yNonNegative = _module.AddInstruction(
                SpirvOp.SGreaterThanEqual,
                _boolType,
                y,
                zero);
            var xInRange = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                x,
                _module.Constant(_intType, width));
            var yInRange = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                y,
                _module.Constant(_intType, height));
            var lowerInRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                xNonNegative,
                yNonNegative);
            var upperInRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                xInRange,
                yInRange);
            var inRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                lowerInRange,
                upperInRange);
            inRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                inRange);
            var writeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                inRange,
                writeLabel,
                mergeLabel);
            _module.AddLabel(writeLabel);
            _module.AddStatement(
                SpirvOp.ImageWrite,
                imageObject,
                coordinates,
                texel);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private static bool TryGetImageBounds(
            IReadOnlyList<uint> descriptor,
            out uint width,
            out uint height)
        {
            width = 0;
            height = 0;
            if (descriptor.Count < 3)
            {
                return false;
            }

            width = (((descriptor[1] >> 30) & 0x3u) |
                     ((descriptor[2] & 0xFFFu) << 2)) + 1;
            height = ((descriptor[2] >> 14) & 0x3FFFu) + 1;
            return width != 0 && height != 0 && width <= 16384 && height <= 16384;
        }

        private uint BuildImageOffset(Gen5ImageControl image, int component)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var packed = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(component)));
            var x = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(0),
                UInt(6));
            var y = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(8),
                UInt(6));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

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

            if (_stage == Gen5SpirvStage.Pixel)
            {
                if (!_pixelOutputs.TryGetValue(export.Target, out var output))
                {
                    return true;
                }

                var values = new uint[4];
                for (var component = 0; component < 4; component++)
                {
                    var enabled = (export.EnableMask & (1u << component)) != 0;
                    if (!enabled)
                    {
                        values[component] = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            output.Kind switch
                            {
                                Gen5PixelOutputKind.Uint => _uintType,
                                Gen5PixelOutputKind.Sint => _intType,
                                _ => _floatType,
                            },
                            Load(output.Type, output.Variable),
                            (uint)component);
                        continue;
                    }

                    if (export.Compressed)
                    {
                        var value = LoadCompressedExportComponent(
                            instruction,
                            component);
                        values[component] = output.Kind switch
                        {
                            Gen5PixelOutputKind.Uint => _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                value),
                            Gen5PixelOutputKind.Sint => _module.AddInstruction(
                                SpirvOp.ConvertFToS,
                                _intType,
                                value),
                            _ => value,
                        };
                        continue;
                    }

                    var raw = LoadV(instruction.Sources[component].Value);
                    values[component] = output.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => raw,
                        Gen5PixelOutputKind.Sint => Bitcast(_intType, raw),
                        _ => Bitcast(_floatType, raw),
                    };
                }

                var vector = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    output.Type,
                    values);
                vector = _module.AddInstruction(
                    SpirvOp.Select,
                    output.Type,
                    Load(_boolType, _exec),
                    vector,
                    Load(output.Type, output.Variable));
                Store(output.Variable, vector);
                return true;
            }

            if (_stage != Gen5SpirvStage.Vertex)
            {
                return true;
            }

            uint outputVariable;
            if (export.Target is >= 12 and < 16)
            {
                if (export.Target != 12)
                {
                    return true;
                }

                outputVariable = _positionOutput;
            }
            else if (export.Target is >= 32 and < 64 &&
                     _vertexOutputs.TryGetValue(export.Target - 32, out var parameter))
            {
                outputVariable = parameter;
            }
            else
            {
                return true;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                components[component] = (export.EnableMask & (1u << component)) != 0
                    ? export.Compressed
                        ? LoadCompressedExportComponent(instruction, component)
                        : Bitcast(
                            _floatType,
                            LoadV(instruction.Sources[component].Value))
                    : Float(component == 3 ? 1f : 0f);
            }

            var outputValue = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec4Type,
                components);
            outputValue = _module.AddInstruction(
                SpirvOp.Select,
                _vec4Type,
                Load(_boolType, _exec),
                outputValue,
                Load(_vec4Type, outputVariable));
            Store(outputVariable, outputValue);
            return true;
        }

        private uint LoadCompressedExportComponent(
            Gen5ShaderInstruction instruction,
            int component)
        {
            var packed = LoadV(instruction.Sources[component >> 1].Value);
            var unpacked = Ext(62, _vec2Type, packed);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private uint GetPixelOutputType(Gen5PixelOutputKind kind) =>
            kind switch
            {
                Gen5PixelOutputKind.Uint => _uvec4Type,
                Gen5PixelOutputKind.Sint => _module.TypeVector(_intType, 4),
                _ => _vec4Type,
            };

        private uint LoadBufferWord(int binding, uint dwordAddress)
        {
            var pointer = BufferWordPointer(binding, dwordAddress);
            return Load(_uintType, pointer);
        }

        private void StoreBufferWord(int binding, uint dwordAddress, uint value)
        {
            var pointer = BufferWordPointer(binding, dwordAddress);
            Store(pointer, value);
        }

        private uint BufferWordPointer(int binding, uint dwordAddress) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageUintPointer,
                _globalBuffers,
                UInt((uint)binding),
                UInt(0),
                dwordAddress);

        private uint ScalarPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _scalarRegisters,
                UInt(register));

        private uint VectorPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _vectorRegisters,
                UInt(register));

        private uint LoadS(uint register) => Load(_uintType, ScalarPointer(register));

        private uint LoadV(uint register) => Load(_uintType, VectorPointer(register));

        private void StoreS(uint register, uint value)
        {
            Store(ScalarPointer(register), value);
            if (register is 106 or 107)
            {
                Store(_vcc, IsWaveMaskActive(LoadS64(106)));
            }
            else if (register is 126 or 127)
            {
                Store(_exec, IsWaveMaskActive(LoadS64(126)));
            }
        }

        private void StoreV(uint register, uint value, bool guardWithExec = true)
        {
            if (guardWithExec)
            {
                var active = Load(_boolType, _exec);
                var oldValue = LoadV(register);
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    active,
                    value,
                    oldValue);
            }

            Store(VectorPointer(register), value);
        }

        private uint Load(uint type, uint pointer) =>
            _module.AddInstruction(SpirvOp.Load, type, pointer);

        private void Store(uint pointer, uint value) =>
            _module.AddStatement(SpirvOp.Store, pointer, value);

        private uint UInt(uint value) => _module.Constant(_uintType, value);

        private uint Float(float value) => _module.ConstantFloat(_floatType, value);

        private uint Bitcast(uint type, uint value) =>
            _module.AddInstruction(SpirvOp.Bitcast, type, value);

        private uint IAdd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.IAdd, _uintType, left, right);

        private uint ShiftLeftLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightArithmetic(uint left, uint right) =>
            Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.ShiftRightArithmetic,
                    _intType,
                    Bitcast(_intType, left),
                    BitwiseAnd(right, UInt(31))));

        private uint ShiftLeftLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint ShiftRightLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint BitwiseAnd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _uintType, left, right);

        private uint BitwiseAnd64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _ulongType, left, right);

        private uint BitwiseOr(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _uintType, left, right);

        private uint BitwiseXor(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseXor, _uintType, left, right);

        private uint LogicalNot(uint value) =>
            _module.AddInstruction(SpirvOp.LogicalNot, _boolType, value);

        private uint SubgroupAny(uint condition) =>
            _subgroupInvocationIdInput == 0
                ? condition
                : _module.AddInstruction(
                    SpirvOp.GroupNonUniformAny,
                    _boolType,
                    UInt(3),
                    condition);

        private uint CurrentLaneBit()
        {
            if (_subgroupInvocationIdInput == 0)
            {
                return _module.Constant64(_ulongType, 1);
            }

            var lane = Load(_uintType, _subgroupInvocationIdInput);
            var maskedLane = BitwiseAnd(lane, UInt(RdnaWaveLaneCount - 1));
            var shifted = ShiftLeftLogical64(
                _module.Constant64(_ulongType, 1),
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    maskedLane));
            return _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                IsCurrentLaneInRdnaWave(),
                shifted,
                _module.Constant64(_ulongType, 0));
        }

        private uint IsCurrentLaneInRdnaWave() =>
            _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                Load(_uintType, _subgroupInvocationIdInput),
                UInt(RdnaWaveLaneCount));

        private uint BooleanToLaneMask(uint condition) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                condition,
                CurrentLaneBit(),
                _module.Constant64(_ulongType, 0));

        private uint IsWaveMaskActive(uint mask) =>
            _subgroupInvocationIdInput == 0
                ? IsNotZero64(mask)
                : IsCurrentLaneSet(mask);

        private uint IsCurrentLaneSet(uint mask) =>
            IsNotZero64(
                _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    mask,
                    CurrentLaneBit()));

        private void StoreWaveMask(uint register, uint condition) =>
            StoreS64(register, BooleanToLaneMask(condition));

        private void EmitExecConditional(Action emit)
        {
            var activeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var active = Load(_boolType, _exec);
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                active,
                activeLabel,
                mergeLabel);
            _module.AddLabel(activeLabel);
            emit();
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private bool UsesLds() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Control is Gen5DataShareControl);

        private bool UsesSubgroupShuffle() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode is "VPermlane16B32" or "VPermlanex16B32");

        private bool UsesWaveControl() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchExec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchVcc", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
                instruction.Sources.Any(IsWaveMaskOperand) ||
                instruction.Destinations.Any(IsWaveMaskOperand));

        private bool UsesSubgroupOperations() =>
            _stage == Gen5SpirvStage.Compute &&
            (UsesSubgroupShuffle() || UsesWaveControl());

        private static bool IsWaveMaskOperand(Gen5Operand operand) =>
            operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value is 106 or 107 or 126 or 127;

        private static bool TryGetVectorDestination(
            Gen5ShaderInstruction instruction,
            out uint destination)
        {
            if (instruction.Destinations.Count != 0 &&
                instruction.Destinations[0].Kind == Gen5OperandKind.VectorRegister)
            {
                destination = instruction.Destinations[0].Value;
                return true;
            }

            destination = 0;
            return false;
        }

        private static bool IsBranch(string opcode) =>
            opcode == "SBranch" ||
            opcode.StartsWith("SCbranch", StringComparison.Ordinal);

        private static bool TryGetBranchTargetPc(
            Gen5ShaderInstruction instruction,
            out uint targetPc)
        {
            targetPc = 0;
            if (instruction.Encoding != Gen5ShaderEncoding.Sopp ||
                instruction.Words.Count == 0)
            {
                return false;
            }

            var offset = unchecked((short)(instruction.Words[0] & 0xFFFF));
            var nextPc = (long)instruction.Pc +
                (instruction.Words.Count * sizeof(uint));
            var target = nextPc + (offset * sizeof(uint));
            if (target < 0 || target > uint.MaxValue)
            {
                return false;
            }

            targetPc = (uint)target;
            return true;
        }

        private static IReadOnlyList<ShaderBlock> BuildBasicBlocks(
            IReadOnlyList<Gen5ShaderInstruction> instructions)
        {
            if (instructions.Count == 0)
            {
                return [];
            }

            var leaders = new SortedSet<uint> { instructions[0].Pc };
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (IsBranch(instruction.Opcode) &&
                    TryGetBranchTargetPc(instruction, out var targetPc))
                {
                    leaders.Add(targetPc);
                }

                if ((IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm") &&
                    index + 1 < instructions.Count)
                {
                    leaders.Add(instructions[index + 1].Pc);
                }
            }

            var starts = leaders
                .Where(pc => instructions.Any(instruction => instruction.Pc == pc))
                .ToArray();
            var blocks = new List<ShaderBlock>(starts.Length);
            for (var index = 0; index < starts.Length; index++)
            {
                var startIndex = FindInstructionIndex(instructions, starts[index]);
                var endIndex = index + 1 < starts.Length
                    ? FindInstructionIndex(instructions, starts[index + 1])
                    : instructions.Count;
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    blocks.Add(new ShaderBlock(starts[index], startIndex, endIndex));
                }
            }

            return blocks;
        }

        private static int FindInstructionIndex(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint pc)
        {
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Pc == pc)
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool TryFindBlock(
            IReadOnlyList<ShaderBlock> blocks,
            uint pc,
            out int block)
        {
            for (var index = 0; index < blocks.Count; index++)
            {
                if (blocks[index].StartPc == pc)
                {
                    block = index;
                    return true;
                }
            }

            block = -1;
            return false;
        }

        private readonly record struct ShaderBlock(
            uint StartPc,
            int StartIndex,
            int EndIndex);
    }
}
