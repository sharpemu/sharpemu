// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private const uint ScalarRegisterCount = 256;
    private const uint VectorRegisterCount = 512;
    private const uint LdsDwordCount = 8192;
    // Graphics stages model LDS as a per-invocation Private array rather than
    // real workgroup-shared memory. A full 32 KB Private array per vertex/pixel
    // invocation is wasteful and risks Metal compile limits, and per-invocation
    // write-then-read correctness only needs deterministic address→slot masking,
    // so a smaller array is safe.
    private const uint PrivateLdsDwordCount = 2048;
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
        int initialScalarBufferIndex = -1,
        int pixelRenderTargetSlot = 0,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong storageBufferOffsetAlignment = 1) =>
        TryCompilePixelShader(
            state,
            evaluation,
            [new Gen5PixelOutputBinding((uint)pixelRenderTargetSlot, 0, outputKind)],
            out shader,
            out error,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            initialScalarBufferIndex,
            pixelInputEnable,
            pixelInputAddress,
            storageBufferOffsetAlignment);

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong storageBufferOffsetAlignment = 1)
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
            initialScalarBufferIndex,
            pixelInputEnable: pixelInputEnable,
            pixelInputAddress: pixelInputAddress,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment);
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
        int initialScalarBufferIndex = -1,
        int requiredVertexOutputCount = 0,
        ulong storageBufferOffsetAlignment = 1)
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
            initialScalarBufferIndex,
            requiredVertexOutputCount: requiredVertexOutputCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out Gen5SpirvShader shader,
        out string error,
        int totalGlobalBufferCount = -1,
        int initialScalarBufferIndex = -1,
        uint waveLaneCount = 32,
        ulong storageBufferOffsetAlignment = 1)
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
            totalGlobalBufferCount,
            0,
            initialScalarBufferIndex,
            waveLaneCount: waveLaneCount,
            storageBufferOffsetAlignment: storageBufferOffsetAlignment);
        return context.TryCompile(out shader, out error);
    }

    private sealed partial class CompilationContext
    {
        private const uint ImageDescriptorDwords = 8;
        private const uint SamplerDescriptorDwords = 4;
        private const int ScalarRegisterCount = 128;
        private const long InitialScalarDefinition = -1;
        private const long ConflictingScalarDefinition = -2;
        private const long UnreachableScalarDefinition = -3;

        private readonly SpirvModuleBuilder _module = new();
        private readonly Gen5SpirvStage _stage;
        private readonly Gen5ShaderState _state;
        private readonly Gen5ShaderEvaluation _evaluation;
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        private readonly uint _waveLaneCount;
        private readonly bool _emulateWave64;

        // Safety valve for the PC-dispatcher loop. Each iteration executes one
        // GCN basic block; a correctly-translated shader always reaches its
        // terminal block (pc out of range -> default -> exit) well within any
        // real control flow. A mistranslated shader whose loop-exit condition is
        // wrong would otherwise spin the dispatcher forever, hanging the single
        // Metal queue and freezing every later submission (black screen, no
        // recovery). Bounding the iteration count guarantees the invocation
        // terminates instead: the effect may be wrong for that shader, but the
        // GPU never wedges. 0 disables the guard (original unbounded behaviour).
        private static readonly int _maxDispatcherSteps =
            int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MAX_STEPS"),
                out var maxSteps) && maxSteps >= 0
                ? maxSteps
                : 100_000;

        // Diagnostic coverage probe. When enabled, every selected MRT export
        // writes opaque magenta while preserving the shader's control flow,
        // EXEC mask, geometry and raster state. This separates missing
        // rasterization from valid fragments whose translated values are zero.
        private static readonly bool _forcePixelMagenta =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_FORCE_PIXEL_MAGENTA"),
                "1",
                StringComparison.Ordinal);

        // Which pixel-shader MRT export target (EXP_MRT0..7 == render-target
        // slot) is routed to the single fragment output. The offscreen draw
        // path renders one bound color target per pass, so a multi-render-target
        // (deferred G-buffer) draw compiles one pixel variant per slot, each
        // selecting that slot's export here.
        // Vertex stage only: the fragment shader paired with this vertex shader
        // declares interpolated inputs for locations 0..(this-1). Metal requires
        // every fragment input location to be written by the vertex shader, so
        // the vertex stage must export at least this many param outputs (any it
        // does not naturally export are zero-filled) or pipeline creation fails
        // with "Fragment input(s) `user(locnN)` ... not written by vertex shader".
        private readonly int _requiredVertexOutputCount;
        private readonly uint _localSizeX;
        private readonly uint _localSizeY;
        private readonly uint _localSizeZ;
        private readonly int _globalBufferBase;
        private readonly int _totalGlobalBufferCount;
        private readonly int _imageBindingBase;
        private readonly int _initialScalarBufferIndex;
        private readonly uint _pixelInputEnable;
        private readonly uint _pixelInputAddress;
        private readonly ulong _storageBufferOffsetAlignment;
        private readonly List<uint> _interfaces = [];
        private readonly Dictionary<uint, uint> _pixelInputs = [];
        private readonly Dictionary<uint, SpirvPixelOutput> _pixelOutputs = [];
        private readonly Dictionary<uint, uint> _vertexOutputs = [];
        private readonly Dictionary<uint, SpirvVertexInput> _vertexInputsByPc = [];
        private readonly List<SpirvImageResource> _imageResources = [];
        private readonly Dictionary<uint, int> _imageBindingByPc = [];
        private readonly Dictionary<uint, int> _bufferBindingByPc = [];
        private readonly Dictionary<uint, long[]> _scalarDefinitionsBeforePc = [];
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
        private uint _privateVec2Pointer;
        private uint _privateBoolPointer;
        private uint _runtimeBufferBiases;
        private uint _scalarRegisters;
        private uint _vectorRegisters;
        private uint _packedHalfRegisters;
        private uint _scc;
        private uint _vcc;
        private uint _exec;
        private uint _reachedPixelExport;
        private uint _programCounter;
        private uint _programActive;
        private uint _iterationGuard;
        private uint _globalBuffers;
        private uint _gfx10BufferFormatTable;
        private uint _storageBlockPointer;
        private uint _storageUintPointer;
        private uint _lds;
        private uint _ldsElementPointer;
        private uint _ldsDwordMask;
        private uint _positionOutput;
        private uint _vertexIndexInput;
        private uint _instanceIndexInput;
        private uint _fragCoordInput;
        private uint _localInvocationIdInput;
        private uint _localInvocationIndexInput;
        private uint _workGroupIdInput;
        private uint _computeDispatchLimit;
        private uint _pushConstantUintPointer;
        private uint _subgroupInvocationIdInput;
        private uint _waveMaskScratch;
        private uint _waveMaskScratchElementPointer;
        private uint _waveBroadcastScratch;
        private bool _waveScratchInLds;
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
            int initialScalarBufferIndex,
            uint pixelInputEnable = 0,
            uint pixelInputAddress = 0,
            int requiredVertexOutputCount = 0,
            uint waveLaneCount = 32,
            ulong storageBufferOffsetAlignment = 1)
        {
            _stage = stage;
            _requiredVertexOutputCount = requiredVertexOutputCount;
            _state = state;
            _evaluation = evaluation;
            _pixelOutputBindings = pixelOutputBindings;
            _waveLaneCount = waveLaneCount == 64 ? 64u : 32u;
            _emulateWave64 =
                stage == Gen5SpirvStage.Compute &&
                _waveLaneCount == 64 &&
                (ulong)localSizeX * localSizeY * localSizeZ == 64;
            _localSizeX = localSizeX;
            _localSizeY = localSizeY;
            _localSizeZ = localSizeZ;
            _globalBufferBase = globalBufferBase;
            _totalGlobalBufferCount = totalGlobalBufferCount < 0
                ? evaluation.GlobalMemoryBindings.Count
                : totalGlobalBufferCount;
            _imageBindingBase = imageBindingBase;
            _initialScalarBufferIndex = initialScalarBufferIndex;
            _pixelInputEnable = pixelInputEnable;
            _pixelInputAddress = pixelInputAddress;
            if (storageBufferOffsetAlignment == 0 ||
                (storageBufferOffsetAlignment & (storageBufferOffsetAlignment - 1)) != 0 ||
                storageBufferOffsetAlignment > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(storageBufferOffsetAlignment),
                    storageBufferOffsetAlignment,
                    "storage-buffer offset alignment must be a uint-sized power of two");
            }

            _storageBufferOffsetAlignment = storageBufferOffsetAlignment;
        }

        public bool TryCompile(out Gen5SpirvShader shader, out string error)
        {
            shader = default!;
            error = string.Empty;
            try
            {
                if (Environment.GetEnvironmentVariable(
                        "SHARPEMU_TRACE_TITLE_INTERFACE") == "1" &&
                    _state.Program.Address is 0x0000000500780000ul or
                        0x0000000500781200ul)
                {
                    Console.Error.WriteLine(
                        $"[AGC][TITLE-INTERFACE] stage={_stage} " +
                        $"address=0x{_state.Program.Address:X16} " +
                        $"required_vertex_outputs={_requiredVertexOutputCount} " +
                        $"ps_ena=0x{_pixelInputEnable:X8} ps_addr=0x{_pixelInputAddress:X8}");
                    foreach (var instruction in _state.Program.Instructions)
                    {
                        if (instruction.Control is Gen5ExportControl export)
                        {
                            Console.Error.WriteLine(
                                $"[AGC][TITLE-INTERFACE] pc=0x{instruction.Pc:X4} " +
                                $"export_target={export.Target} mask=0x{export.EnableMask:X} " +
                                $"compressed={export.Compressed} src=[" +
                                string.Join(',', instruction.Sources) + "]");
                        }
                        else if (instruction.Control is Gen5InterpolationControl interpolation)
                        {
                            Console.Error.WriteLine(
                                $"[AGC][TITLE-INTERFACE] pc=0x{instruction.Pc:X4} " +
                                $"attribute={interpolation.Attribute} " +
                                $"channel={interpolation.Channel} dst=[" +
                                string.Join(',', instruction.Destinations) + "]");
                        }
                        else if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
                        {
                            var bindingIndex = -1;
                            for (var index = 0;
                                 index < _evaluation.GlobalMemoryBindings.Count;
                                 index++)
                            {
                                if (_evaluation.GlobalMemoryBindings[index]
                                    .InstructionPcs.Contains(instruction.Pc))
                                {
                                    bindingIndex = index;
                                    break;
                                }
                            }

                            var binding = bindingIndex >= 0
                                ? _evaluation.GlobalMemoryBindings[bindingIndex]
                                : null;
                            var byteOffset = scalarMemory.ImmediateOffsetBytes;
                            var sample = binding is not null &&
                                         scalarMemory.DynamicOffsetRegister is null &&
                                         byteOffset >= 0 &&
                                         byteOffset < binding.DataLength
                                ? Convert.ToHexString(
                                    binding.Data.AsSpan(
                                        byteOffset,
                                        Math.Min(
                                            checked((int)scalarMemory.DestinationCount * 4),
                                            binding.DataLength - byteOffset)))
                                : "dynamic-or-unavailable";
                            Console.Error.WriteLine(
                                $"[AGC][TITLE-INTERFACE] pc=0x{instruction.Pc:X4} " +
                                $"scalar_load={instruction.Opcode} binding={bindingIndex} " +
                                $"scalar=s{binding?.ScalarAddress} " +
                                $"base=0x{binding?.BaseAddress:X16} length={binding?.DataLength} " +
                                $"offset={byteOffset} dynamic={scalarMemory.DynamicOffsetRegister} " +
                                $"bytes={sample}");
                        }
                    }
                }

                DeclareModule();
                var blocks = BuildBasicBlocks(_state.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                BuildScalarDefinitionInfo(blocks, _state.Program.Instructions);

                var functionType = _module.TypeFunction(_voidType);
                var main = _module.BeginFunction(_voidType, functionType);
                _module.AddName(main, "main");
                _module.AddLabel();
                if (_stage == Gen5SpirvStage.Pixel &&
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_TITLE_EARLY_COLOR") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    var earlyOutput = _pixelOutputs
                        .OrderBy(static pair => pair.Key)
                        .Select(static pair => pair.Value)
                        .First();
                    var earlyColor = earlyOutput.Kind switch
                    {
                        Gen5PixelOutputKind.Float =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                earlyOutput.Type,
                                Float(1f),
                                Float(0f),
                                Float(1f),
                                Float(1f)),
                        Gen5PixelOutputKind.Sint =>
                            _module.ConstantNull(earlyOutput.Type),
                        _ => _module.ConstantNull(earlyOutput.Type),
                    };
                    Store(earlyOutput.Variable, earlyColor);
                    _module.AddStatement(SpirvOp.Return);
                    _module.AddLabel();
                }
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
                if (_maxDispatcherSteps > 0)
                {
                    var steps = IAdd(Load(_uintType, _iterationGuard), UInt(1));
                    Store(_iterationGuard, steps);
                    var withinLimit = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        steps,
                        UInt((uint)_maxDispatcherSteps));
                    active = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        active,
                        withinLimit);
                }

                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    active,
                    loopHeader,
                    loopMerge);
                _module.AddLabel(loopMerge);
                if (_stage == Gen5SpirvStage.Pixel &&
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_TRACE_TITLE_SHADER_STATE") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    var stateOutput = _pixelOutputs
                        .OrderBy(static pair => pair.Key)
                        .Select(static pair => pair.Value)
                        .FirstOrDefault(static output =>
                            output.Kind == Gen5PixelOutputKind.Float);
                    if (stateOutput.Variable != 0)
                    {
                        uint EncodeBool(uint condition) =>
                            _module.AddInstruction(
                                SpirvOp.Select,
                                _floatType,
                                condition,
                                Float(1f),
                                Float(0f));

                        Store(
                            stateOutput.Variable,
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                stateOutput.Type,
                                EncodeBool(Load(_boolType, _exec)),
                                EncodeBool(IsWaveMaskActive(LoadS64(52))),
                                EncodeBool(Load(_boolType, _reachedPixelExport)),
                                Float(1f)));
                    }

                    StoreS64(
                        126,
                        _module.Constant64(_ulongType, 1));
                }
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    // A fragment lane removed from EXEC is not a request to
                    // write the output variable's zero initializer. It is a
                    // killed fragment and must not participate in color,
                    // depth, or blend operations. Keep EXEC masking during
                    // translation, then terminate lanes that remain inactive
                    // when the guest pixel shader exits.
                    var returnLabel = _module.AllocateId();
                    var killLabel = _module.AllocateId();
                    // Materialize the condition before SelectionMerge: SPIR-V
                    // requires the merge instruction to be immediately followed
                    // by its structured branch terminator.
                    var laneActive = Load(_boolType, _exec);
                    _module.AddStatement(
                        SpirvOp.SelectionMerge,
                        returnLabel,
                        0);
                    _module.AddStatement(
                        SpirvOp.BranchConditional,
                        laneActive,
                        returnLabel,
                        killLabel);
                    _module.AddLabel(killLabel);
                    _module.AddStatement(SpirvOp.Kill);
                    _module.AddLabel(returnLabel);
                }

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

                if (UsesSubgroupBroadcast() || UsesWaveControl())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformBallot);
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
            _privateVec2Pointer =
                _module.TypePointer(SpirvStorageClass.Private, _vec2Type);
            _privateBoolPointer =
                _module.TypePointer(SpirvStorageClass.Private, _boolType);

            var scalarArrayType = _module.TypeArray(_uintType, ScalarRegisterCount);
            var vectorArrayType = _module.TypeArray(_uintType, VectorRegisterCount);
            var packedHalfArrayType = _module.TypeArray(_vec2Type, VectorRegisterCount);
            var privateScalarArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, scalarArrayType);
            var privateVectorArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, vectorArrayType);
            var privatePackedHalfArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, packedHalfArrayType);
            _scalarRegisters = _module.AddGlobalVariable(
                privateScalarArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(scalarArrayType));
            _vectorRegisters = _module.AddGlobalVariable(
                privateVectorArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(vectorArrayType));
            _packedHalfRegisters = _module.AddGlobalVariable(
                privatePackedHalfArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(packedHalfArrayType));
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
            _reachedPixelExport = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _programCounter = _module.AddGlobalVariable(
                _privateUintPointer,
                SpirvStorageClass.Private,
                _module.Constant(_uintType, 0));
            _programActive = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            if (_maxDispatcherSteps > 0)
            {
                _iterationGuard = _module.AddGlobalVariable(
                    _privateUintPointer,
                    SpirvStorageClass.Private,
                    _module.Constant(_uintType, 0));
                _interfaces.Add(_iterationGuard);
                _module.AddName(_iterationGuard, "pcGuard");
            }

            _interfaces.Add(_scalarRegisters);
            _interfaces.Add(_vectorRegisters);
            _interfaces.Add(_packedHalfRegisters);
            _interfaces.Add(_scc);
            _interfaces.Add(_vcc);
            _interfaces.Add(_exec);
            _interfaces.Add(_reachedPixelExport);
            _interfaces.Add(_programCounter);
            _interfaces.Add(_programActive);
            _module.AddName(_scalarRegisters, "sgpr");
            _module.AddName(_vectorRegisters, "vgpr");
            _module.AddName(_packedHalfRegisters, "vgprPackedHalf");

            var runtimeBufferBiasCount =
                _globalBufferBase + _evaluation.GlobalMemoryBindings.Count;
            if (_initialScalarBufferIndex >= 0 && runtimeBufferBiasCount > 0)
            {
                var biasArrayType = _module.TypeArray(
                    _uintType,
                    (uint)runtimeBufferBiasCount);
                var privateBiasArrayPointer = _module.TypePointer(
                    SpirvStorageClass.Private,
                    biasArrayType);
                _runtimeBufferBiases = _module.AddGlobalVariable(
                    privateBiasArrayPointer,
                    SpirvStorageClass.Private,
                    _module.ConstantNull(biasArrayType));
                _module.AddName(_runtimeBufferBiases, "guestBufferByteBias");
                _interfaces.Add(_runtimeBufferBiases);
            }

            DeclareBuffers();
            DeclareImages();
            DeclareLds();
            DeclareWave64Scratch();
            DeclareStageInterface();
            DeclareComputeDispatchLimit();
        }

        private void DeclareComputeDispatchLimit()
        {
            if (_stage != Gen5SpirvStage.Compute)
            {
                return;
            }

            // RDNA DISPATCH_* can express exact thread dimensions, including
            // a final partially populated workgroup. Vulkan dispatches whole
            // workgroups, so the command path supplies the exact exclusive
            // thread bounds through a small push-constant block and excess
            // invocations are disabled before any guest instruction executes.
            var block = _module.TypeStruct(_uvec3Type);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var blockPointer =
                _module.TypePointer(SpirvStorageClass.PushConstant, block);
            _pushConstantUintPointer =
                _module.TypePointer(SpirvStorageClass.PushConstant, _uintType);
            _computeDispatchLimit = _module.AddGlobalVariable(
                blockPointer,
                SpirvStorageClass.PushConstant);
            _module.AddName(_computeDispatchLimit, "dispatchThreadLimit");
            _interfaces.Add(_computeDispatchLimit);
        }

        private void DeclareWave64Scratch()
        {
            if (!_emulateWave64 || !UsesSubgroupOperations())
            {
                return;
            }

            // Metal exposes 32 KiB of threadgroup memory on the Apple GPUs we
            // target. Some PS5 compute shaders legitimately request all of it,
            // so allocating another workgroup variable for the wave64 bridge
            // makes pipeline creation fail. Reuse the final three dwords of the
            // existing LDS allocation in that case. The translator already
            // bounds guest LDS accesses to this fixed allocation; keeping the
            // bridge inside it preserves the host limit and still provides the
            // cross-subgroup rendezvous needed to model one 64-lane guest wave.
            if (_lds != 0)
            {
                _waveScratchInLds = true;
                _waveMaskScratchElementPointer = _ldsElementPointer;
                return;
            }

            var maskArrayType = _module.TypeArray(_uintType, 2);
            var maskArrayPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, maskArrayType);
            _waveMaskScratchElementPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _waveMaskScratch = _module.AddGlobalVariable(
                maskArrayPointer,
                SpirvStorageClass.Workgroup);
            _module.AddName(_waveMaskScratch, "wave64MaskScratch");
            _interfaces.Add(_waveMaskScratch);

            var uintPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _waveBroadcastScratch = _module.AddGlobalVariable(
                uintPointer,
                SpirvStorageClass.Workgroup);
            _module.AddName(_waveBroadcastScratch, "wave64BroadcastScratch");
            _interfaces.Add(_waveBroadcastScratch);
        }

        private void DeclareLds()
        {
            if (!UsesLds())
            {
                return;
            }

            // Compute shaders get genuine workgroup-shared LDS. Graphics stages
            // (NGG export/vertex, pixel) cannot use the Workgroup storage class
            // in SPIR-V, but they still emit ds_write/ds_read — typically as
            // per-invocation scratch/spill or as NGG staging whose cross-lane
            // reads don't feed this stage's exports. Model those as a
            // per-invocation Private array so the shader is valid SPIR-V and its
            // draw stops being dropped. Index masking in LdsPointer keeps the
            // arbitrary computed addresses inside the array.
            var storageClass = _stage == Gen5SpirvStage.Compute
                ? SpirvStorageClass.Workgroup
                : SpirvStorageClass.Private;
            var dwordCount = _stage == Gen5SpirvStage.Compute
                ? LdsDwordCount
                : PrivateLdsDwordCount;
            _ldsDwordMask = dwordCount - 1;

            var ldsArrayType = _module.TypeArray(_uintType, dwordCount);
            var ldsPointer = _module.TypePointer(storageClass, ldsArrayType);
            _ldsElementPointer = _module.TypePointer(storageClass, _uintType);
            _lds = storageClass == SpirvStorageClass.Workgroup
                ? _module.AddGlobalVariable(ldsPointer, storageClass)
                : _module.AddGlobalVariable(
                    ldsPointer,
                    storageClass,
                    _module.ConstantNull(ldsArrayType));
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
            _storageBlockPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, block);
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

            var unifiedFormat = (descriptor[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out var dataFormat,
                    out var numberType))
            {
                return (SpirvImageFormat.Unknown, ImageComponentKind.Float);
            }

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

                if (_waveLaneCount == 64)
                {
                    _localInvocationIndexInput = _module.AddGlobalVariable(
                        subgroupPointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(
                        _localInvocationIndexInput,
                        SpirvDecoration.BuiltIn,
                        (uint)SpirvBuiltIn.LocalInvocationIndex);
                    _interfaces.Add(_localInvocationIndexInput);
                }
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
                    // Cover every location the paired fragment shader reads, even
                    // ones this vertex program never exports, so Metal's exact
                    // vertex-out/fragment-in interface match succeeds. Extras are
                    // zero-filled in EmitInitialState.
                    .Concat(Enumerable
                        .Range(0, Math.Max(_requiredVertexOutputCount, 0))
                        .Select(location => (uint)location))
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

                var declaredPixelOutputs =
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_TITLE_SINGLE_MRT") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul
                        ? _pixelOutputBindings.Take(1)
                        : _pixelOutputBindings;
                foreach (var binding in declaredPixelOutputs)
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
            if (_initialScalarBufferIndex >= 0)
            {
                // Initial scalar registers arrive in a per-draw buffer instead
                // of being baked as constants, so animated user data (colors,
                // scroll offsets) reuses one translation and pipeline. Only
                // registers the program can observe need loading.
                var consumed = Gen5ShaderTranslator.ComputeConsumedScalarMask(_state.Program);
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterCount;
                     index++)
                {
                    if (Gen5ShaderTranslator.IsScalarConsumed(consumed, index))
                    {
                        StoreS(
                            index,
                            LoadBufferWord(_initialScalarBufferIndex, UInt(index)));
                    }
                }

                var runtimeBufferBiasCount =
                    _globalBufferBase + _evaluation.GlobalMemoryBindings.Count;
                for (var binding = 0;
                     binding < runtimeBufferBiasCount;
                     binding++)
                {
                    Store(
                        RuntimeBufferBiasPointer(binding),
                        LoadBufferWord(
                            _initialScalarBufferIndex,
                            UInt(checked(256u + (uint)binding))));
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
            Store(_reachedPixelExport, _module.ConstantBool(false));
            if (_subgroupInvocationIdInput != 0)
            {
                StoreWaveMask(106, _module.ConstantBool(false));
                StoreWaveMask(126, _module.ConstantBool(true));
            }
            else
            {
                // Graphics stages emulate one logical wave lane. Keep the
                // guest-visible VCC/EXEC scalar pairs synchronized with the
                // internal booleans: shaders commonly save EXEC from s126:s127
                // and restore it after divergent work. Initializing only _exec
                // left those registers at zero, so the first restore disabled
                // every fragment before its color export.
                StoreS64(106, _module.Constant64(_ulongType, 0));
                StoreS64(126, _module.Constant64(_ulongType, 1));
            }
            Store(_programCounter, UInt(0));
            Store(_programActive, _module.ConstantBool(true));

            if (_stage == Gen5SpirvStage.Vertex)
            {
                StoreV(5, Load(_uintType, _vertexIndexInput), guardWithExec: false);
                StoreV(8, Load(_uintType, _instanceIndexInput), guardWithExec: false);

                // Give every declared param output a defined starting value.
                // Outputs the program actually exports overwrite this; the
                // extras that only exist to satisfy the fragment interface stay
                // zero. The explicit store also keeps SPIRV-Cross from pruning
                // an unexported output (which would re-break the interface).
                foreach (var output in _vertexOutputs.Values)
                {
                    Store(output, _module.ConstantNull(_vec4Type));
                }
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var fragCoord = Load(_vec4Type, _fragCoordInput);
                EmitPixelInputState(fragCoord);
                foreach (var output in _pixelOutputs.Values)
                {
                    Store(output.Variable, _module.ConstantNull(output.Type));
                }
            }
            else
            {
                var localId = Load(_uvec3Type, _localInvocationIdInput);
                var workGroupId = Load(_uvec3Type, _workGroupIdInput);
                var invocationInBounds = _module.ConstantBool(true);
                for (uint component = 0; component < 3; component++)
                {
                    var localComponent = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        localId,
                        component);
                    StoreV(component, localComponent, guardWithExec: false);

                    var groupComponent = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        workGroupId,
                        component);
                    var localSize = component switch
                    {
                        0 => _localSizeX,
                        1 => _localSizeY,
                        _ => _localSizeZ,
                    };
                    var globalComponent = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            groupComponent,
                            UInt(localSize)),
                        localComponent);
                    var limitPointer = _module.AddInstruction(
                        SpirvOp.AccessChain,
                        _pushConstantUintPointer,
                        _computeDispatchLimit,
                        UInt(0),
                        UInt(component));
                    var componentInBounds = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        globalComponent,
                        Load(_uintType, limitPointer));
                    invocationInBounds = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        invocationInBounds,
                        componentInBounds);
                }

                Store(_programActive, invocationInBounds);

                if (_state.ComputeSystemRegisters is { } registers)
                {
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

        private void EmitPixelInputState(uint fragCoord)
        {
            uint vgpr = 0;

            // Pixel input VGPRs are compacted in SPI_PS_INPUT_ADDR order. The
            // interpolation inputs occupy register slots even though V_INTERP
            // is lowered directly from SPIR-V interpolants; position inputs
            // following them must still land in the hardware-selected VGPRs.
            AdvancePixelInput(0, 2, ref vgpr); // PERSP_SAMPLE
            AdvancePixelInput(1, 2, ref vgpr); // PERSP_CENTER
            AdvancePixelInput(2, 2, ref vgpr); // PERSP_CENTROID
            AdvancePixelInput(3, 3, ref vgpr); // PERSP_PULL_MODEL
            AdvancePixelInput(4, 2, ref vgpr); // LINEAR_SAMPLE
            AdvancePixelInput(5, 2, ref vgpr); // LINEAR_CENTER
            AdvancePixelInput(6, 2, ref vgpr); // LINEAR_CENTROID
            AdvancePixelInput(7, 1, ref vgpr); // LINE_STIPPLE

            EmitPixelPositionInput(8, 0, fragCoord, ref vgpr); // POS_X_FLOAT
            EmitPixelPositionInput(9, 1, fragCoord, ref vgpr); // POS_Y_FLOAT
            EmitPixelPositionInput(10, 2, fragCoord, ref vgpr); // POS_Z_FLOAT
            EmitPixelPositionInput(11, 3, fragCoord, ref vgpr); // POS_W_FLOAT

            // FRONT_FACE, ANCILLARY, SAMPLE_COVERAGE and POS_FIXED_PT follow
            // position inputs. Reserve their compact slots until their SPIR-V
            // builtins are needed by a guest shader.
            AdvancePixelInput(12, 1, ref vgpr);
            AdvancePixelInput(13, 1, ref vgpr);
            AdvancePixelInput(14, 1, ref vgpr);
            AdvancePixelInput(15, 1, ref vgpr);
        }

        private void AdvancePixelInput(int bit, uint dwordCount, ref uint vgpr)
        {
            if ((_pixelInputAddress & (1u << bit)) != 0)
            {
                vgpr += dwordCount;
            }
        }

        private void EmitPixelPositionInput(
            int bit,
            uint component,
            uint fragCoord,
            ref uint vgpr)
        {
            var mask = 1u << bit;
            if ((_pixelInputAddress & mask) == 0)
            {
                return;
            }

            if ((_pixelInputEnable & mask) != 0)
            {
                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    fragCoord,
                    component);
                StoreV(vgpr, Bitcast(_uintType, value), guardWithExec: false);
            }

            vgpr++;
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

                CapturePixelVgprs(instruction);
                CapturePixelVgprPoints(instruction);
                MarkPixelPath(instruction);
                CapturePixelExec(instruction);
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
            if (_lds == 0 ||
                _ldsElementPointer == 0 ||
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
                        LdsPointer(address, control.Offset0),
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
                    var offset = control.Offset0;
                    StoreLds(LdsPointer(address, offset), GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(address, offset + sizeof(uint)),
                        GetRawSource(instruction, 2));
                    return true;
                }
                case "DsWriteB96":
                case "DsWriteB128":
                {
                    // ds_write_b96 stores 3 consecutive dwords, ds_write_b128
                    // stores 4, from data0..data0+N at the address's offset.
                    var dwordCount = instruction.Opcode == "DsWriteB128" ? 4 : 3;
                    if (instruction.Sources.Count < 1 + dwordCount)
                    {
                        error = "missing LDS write128 source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = control.Offset0;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        StoreLds(
                            LdsPointer(address, offset + (uint)(dword * sizeof(uint))),
                            GetRawSource(instruction, 1 + dword));
                    }

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
                            EffectiveDsPairOffsetBytes(control.Offset0, st64)),
                        GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(
                            address,
                            EffectiveDsPairOffsetBytes(control.Offset1, st64)),
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
                        LdsPointer(address, control.Offset0));
                    StoreV(instruction.Destinations[0].Value, value);
                    return true;
                }
                case "DsReadB96":
                case "DsReadB128":
                {
                    // ds_read_b96 loads 3 consecutive dwords, ds_read_b128 loads
                    // 4, into dest..dest+N from the address's offset.
                    var dwordCount = instruction.Opcode == "DsReadB128" ? 4 : 3;
                    if (instruction.Destinations.Count < dwordCount ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read128 operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = control.Offset0;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        var value = Load(
                            _uintType,
                            LdsPointer(address, offset + (uint)(dword * sizeof(uint))));
                        StoreV(instruction.Destinations[dword].Value, value);
                    }

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
                            EffectiveDsPairOffsetBytes(control.Offset0, st64)));
                    var second = Load(
                        _uintType,
                        LdsPointer(
                            address,
                            EffectiveDsPairOffsetBytes(control.Offset1, st64)));
                    StoreV(instruction.Destinations[0].Value, first);
                    StoreV(instruction.Destinations[1].Value, second);
                    return true;
                }
                default:
                    if (Gen5ShaderTranslator.IsDataShareAtomic(instruction.Opcode))
                    {
                        return TryEmitDataShareAtomic(instruction, control, out error);
                    }

                    error = $"unsupported LDS opcode {instruction.Opcode}";
                    return false;
            }
        }

        private static uint EffectiveDsPairOffsetBytes(uint offset, bool st64 = false) =>
            offset * (st64 ? 256u : sizeof(uint));

        private uint LdsPointer(uint address, uint offsetBytes)
        {
            var addressWithOffset = offsetBytes == 0
                ? address
                : IAdd(address, UInt(offsetBytes));
            // Mask the dword index into the array bounds. LDS is a power-of-two
            // dword count, so this is a no-op for in-range compute addresses but
            // prevents out-of-bounds access when a graphics-stage scratch write
            // uses an arbitrary computed byte address.
            var index = BitwiseAnd(
                ShiftRightLogical(addressWithOffset, UInt(2)),
                UInt(_ldsDwordMask));
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _ldsElementPointer,
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

        private bool TryEmitDataShareAtomic(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
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
                error = $"unsupported LDS opcode {instruction.Opcode}";
                return false;
            }

            var address = GetRawSource(instruction, 0);
            var pointer = LdsPointer(address, control.Offset0);
            EmitExecConditional(() =>
            {
                var original = EmitAtomic(
                    atomicOp,
                    _uintType,
                    pointer,
                    scope: 2,
                    semantics: 0x108,
                    // DS_CMPST sources: DATA0 is the comparator, DATA1 the new value.
                    value: () => GetRawSource(
                        instruction,
                        atomicOp == SpirvOp.AtomicCompareExchange ? 2 : 1),
                    comparator: () => GetRawSource(instruction, 1));
                if (instruction.Destinations.Count > 0)
                {
                    StoreV(instruction.Destinations[0].Value, original);
                }
            });

            return true;
        }

        // Maps the AMD atomic-op name suffix shared by buffer/image atomics to a SPIR-V opcode.
        // Inc/Dec approximate the AMD wrap-clamp semantics (MEM = tmp >= DATA ? 0 : tmp + 1),
        // which is exact for the common 0xFFFFFFFF clamp operand.
        private static bool TryGetAtomicOp(string name, out SpirvOp op)
        {
            op = name switch
            {
                "Swap" => SpirvOp.AtomicExchange,
                "Cmpswap" => SpirvOp.AtomicCompareExchange,
                "Add" => SpirvOp.AtomicIAdd,
                "Sub" => SpirvOp.AtomicISub,
                "Smin" => SpirvOp.AtomicSMin,
                "Umin" => SpirvOp.AtomicUMin,
                "Smax" => SpirvOp.AtomicSMax,
                "Umax" => SpirvOp.AtomicUMax,
                "And" => SpirvOp.AtomicAnd,
                "Or" => SpirvOp.AtomicOr,
                "Xor" => SpirvOp.AtomicXor,
                "Inc" => SpirvOp.AtomicIIncrement,
                "Dec" => SpirvOp.AtomicIDecrement,
                _ => SpirvOp.Nop,
            };
            return op != SpirvOp.Nop;
        }

        private uint EmitAtomic(
            SpirvOp op,
            uint type,
            uint pointer,
            uint scope,
            uint semantics,
            Func<uint> value,
            Func<uint> comparator)
        {
            if (op is SpirvOp.AtomicIIncrement or SpirvOp.AtomicIDecrement)
            {
                return _module.AddInstruction(
                    op,
                    type,
                    pointer,
                    UInt(scope),
                    UInt(semantics));
            }

            if (op == SpirvOp.AtomicCompareExchange)
            {
                // The unequal semantics must not contain Release; downgrade it to Acquire.
                return _module.AddInstruction(
                    op,
                    type,
                    pointer,
                    UInt(scope),
                    UInt(semantics),
                    UInt((semantics & ~0x8u) | 0x2u),
                    value(),
                    comparator());
            }

            return _module.AddInstruction(
                op,
                type,
                pointer,
                UInt(scope),
                UInt(semantics),
                value());
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
            var scalarAddress = instruction.Sources.Count != 0 &&
                instruction.Sources[0].Kind == Gen5OperandKind.ScalarRegister
                ? instruction.Sources[0].Value
                : uint.MaxValue;
            if (!TryResolveDominatingBufferBinding(
                    instruction.Pc,
                    scalarAddress,
                    registerCount: instruction.Opcode.StartsWith(
                        "SBufferLoad",
                        StringComparison.Ordinal) ? 4u : 2u,
                    out var bindingIndex))
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
            byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
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
            if (!TryResolveDominatingBufferBinding(
                    instruction.Pc,
                    control.ScalarAddress,
                    registerCount: 2,
                    out var bindingIndex))
            {
                error = "missing global-memory binding";
                return false;
            }

            var byteAddress = IAdd(
                LoadV(control.VectorAddress),
                UInt(unchecked((uint)control.OffsetBytes)));
            byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode is "GlobalAtomicAdd" or "GlobalAtomicUMax")
            {
                EmitExecConditional(() =>
                {
                    EmitConditional(IsBufferWordInRange(bindingIndex, dwordAddress), () =>
                    {
                        var original = _module.AddInstruction(
                            instruction.Opcode == "GlobalAtomicAdd"
                                ? SpirvOp.AtomicIAdd
                                : SpirvOp.AtomicUMax,
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
                });
                return true;
            }

            if (instruction.Opcode.StartsWith("GlobalStore", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (TryGetSubdwordStoreInfo(
                            instruction.Opcode,
                            out var byteCount,
                            out var sourceShift))
                    {
                        StoreBufferBytes(
                            bindingIndex,
                            byteAddress,
                            LoadV(control.VectorData),
                            byteCount,
                            sourceShift);
                        return;
                    }

                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var address = index == 0
                            ? byteAddress
                            : IAdd(byteAddress, UInt(index * sizeof(uint)));
                        StoreBufferBytes(
                            bindingIndex,
                            address,
                            LoadV(control.VectorData + index),
                            sizeof(uint),
                            0);
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
                    LoadSubdwordBufferValue(
                        bindingIndex,
                        byteAddress,
                        LoadV(control.VectorData),
                        loadByteCount,
                        signExtend,
                        d16,
                        d16High));
                return true;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index * sizeof(uint)));
                StoreV(
                    control.VectorData + index,
                    LoadUnalignedBufferWord(bindingIndex, address));
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

            if (!TryResolveDominatingBufferBinding(
                    instruction.Pc,
                    control.ScalarResource,
                    registerCount: 4,
                    out var bindingIndex))
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
            byteAddress = ApplyGuestBufferByteBias(bindingIndex, byteAddress);
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode.StartsWith("BufferAtomic", StringComparison.Ordinal))
            {
                if (!TryGetAtomicOp(instruction.Opcode["BufferAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported buffer opcode {instruction.Opcode}";
                    return false;
                }

                EmitExecConditional(() =>
                {
                    var inRange = IsBufferWordInRange(bindingIndex, dwordAddress);
                    EmitConditional(inRange, () =>
                    {
                        var original = EmitAtomic(
                            atomicOp,
                            _uintType,
                            BufferWordPointer(bindingIndex, dwordAddress),
                            scope: 1,
                            semantics: 0x48,
                            value: () => LoadV(control.VectorData),
                            comparator: () => LoadV(control.VectorData + 1));
                        if (control.Glc)
                        {
                            StoreV(control.VectorData, original);
                        }
                    });
                });

                return true;
            }

            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreFormat", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreByte", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreShort", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (TryGetSubdwordStoreInfo(
                            instruction.Opcode,
                            out var byteCount,
                            out var sourceShift))
                    {
                        StoreBufferBytes(
                            bindingIndex,
                            byteAddress,
                            LoadV(control.VectorData),
                            byteCount,
                            sourceShift);
                        return;
                    }

                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var address = index == 0
                            ? byteAddress
                            : IAdd(byteAddress, UInt(index * sizeof(uint)));
                        StoreBufferBytes(
                            bindingIndex,
                            address,
                            LoadV(control.VectorData + index),
                            sizeof(uint),
                            0);
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
                    LoadSubdwordBufferValue(
                        bindingIndex,
                        byteAddress,
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
                error = $"unsupported buffer opcode {instruction.Opcode}";
                return false;
            }

            // MUBUF format loads take their element format and destination
            // swizzle from the GFX10 buffer descriptor.  Keep raw dword loads
            // on the byte-address >> 2 path below: unlike typed loads they do
            // not perform component conversion or dst_sel processing.
            // Vertex shaders normally expose indexed format loads as Vulkan
            // attributes. Loads with an additional per-lane byte offset cannot
            // be represented by a fixed attribute description, so the scalar
            // evaluator captures their descriptor as storage instead. Preserve
            // typed conversion for both MUBUF and MTBUF in that fallback path.
            if (IsFormatBufferLoad(instruction.Opcode))
            {
                EmitBufferFormatLoad(
                    bindingIndex,
                    byteAddress,
                    control.ScalarResource,
                    control.VectorData,
                    control.DwordCount);
                return true;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index * sizeof(uint)));
                StoreV(
                    control.VectorData + index,
                    LoadUnalignedBufferWord(bindingIndex, address));
            }

            return true;
        }

        private void EmitBufferFormatLoad(
            int bindingIndex,
            uint byteAddress,
            uint scalarResource,
            uint vectorData,
            uint componentCount)
        {
            var descriptorWord3 = LoadS(scalarResource + 3);
            var unifiedFormat = BitwiseAnd(
                ShiftRightLogical(descriptorWord3, UInt(12)),
                UInt(0x7F));
            var (dataFormat, numberFormat) = DecodeGfx10BufferFormat(unifiedFormat);

            var canonical = new uint[4];
            for (var component = 0; component < canonical.Length; component++)
            {
                canonical[component] = LoadGfx10BufferFormatComponent(
                    bindingIndex,
                    byteAddress,
                    dataFormat,
                    numberFormat,
                    component);
            }

            var one = Gfx10FormatOne(numberFormat);
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = BitwiseAnd(
                    ShiftRightLogical(descriptorWord3, UInt(destination * 3)),
                    UInt(7));
                var value = UInt(0);
                value = SelectUInt(selector, 1, one, value);
                value = SelectUInt(selector, 4, canonical[0], value);
                value = SelectUInt(selector, 5, canonical[1], value);
                value = SelectUInt(selector, 6, canonical[2], value);
                value = SelectUInt(selector, 7, canonical[3], value);
                StoreV(vectorData + destination, value);
            }
        }

        private (uint DataFormat, uint NumberFormat) DecodeGfx10BufferFormat(
            uint unifiedFormat)
        {
            // The descriptor is loaded at execution time, so format decoding
            // must remain dynamic too. Generate one module-level lookup table
            // from the same authoritative decoder used by descriptor
            // evaluation rather than specializing the shader to the SRD seen
            // at compile time (compiled compute shaders may be reused with new
            // SRDs). A table also avoids emitting 77 compares at every format
            // load site, which matters in buffer-heavy compute kernels.
            if (_gfx10BufferFormatTable == 0)
            {
                const uint formatCount = 128;
                var entries = new uint[formatCount];
                for (uint format = 0; format < formatCount; format++)
                {
                    Gfx10UnifiedFormat.TryDecode(
                        format,
                        out var decodedDataFormat,
                        out var decodedNumberFormat);
                    entries[format] = UInt(
                        decodedDataFormat | (decodedNumberFormat << 8));
                }

                var tableType = _module.TypeArray(_uintType, formatCount);
                var tablePointer = _module.TypePointer(
                    SpirvStorageClass.Private,
                    tableType);
                _gfx10BufferFormatTable = _module.AddGlobalVariable(
                    tablePointer,
                    SpirvStorageClass.Private,
                    _module.ConstantComposite(tableType, entries));
                _module.AddName(_gfx10BufferFormatTable, "gfx10BufferFormats");
                _interfaces.Add(_gfx10BufferFormatTable);
            }

            var entryPointer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _gfx10BufferFormatTable,
                unifiedFormat);
            var entry = Load(_uintType, entryPointer);
            return (
                BitwiseAnd(entry, UInt(0xFF)),
                BitwiseAnd(
                    ShiftRightLogical(entry, UInt(8)),
                    UInt(0xFF)));
        }

        private uint LoadGfx10BufferFormatComponent(
            int bindingIndex,
            uint elementAddress,
            uint dataFormat,
            uint numberFormat,
            int component)
        {
            var byteOffset = UInt(0);
            var bitOffset = UInt(0);
            var bitCount = UInt(0);

            void SetLayout(uint format, uint bytes, uint bits, uint count)
            {
                var matches = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    dataFormat,
                    UInt(format));
                byteOffset = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(bytes),
                    byteOffset);
                bitOffset = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(bits),
                    bitOffset);
                bitCount = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(count),
                    bitCount);
            }

            // Legacy DATA_FORMAT layouts selected by the GFX10 unified format.
            // Packed formats keep their bit offset in the first dword; byte
            // offsets are used for naturally aligned vector components.
            switch (component)
            {
                case 0:
                    SetLayout(1, 0, 0, 8);   // 8
                    SetLayout(2, 0, 0, 16);  // 16
                    SetLayout(3, 0, 0, 8);   // 8_8
                    SetLayout(4, 0, 0, 32);  // 32
                    SetLayout(5, 0, 0, 16);  // 16_16
                    SetLayout(6, 0, 0, 10);  // 10_11_11
                    SetLayout(7, 0, 0, 11);  // 11_11_10
                    SetLayout(8, 0, 0, 10);  // 10_10_10_2
                    SetLayout(9, 0, 0, 2);   // 2_10_10_10
                    SetLayout(10, 0, 0, 8);  // 8_8_8_8
                    SetLayout(11, 0, 0, 32); // 32_32
                    SetLayout(12, 0, 0, 16); // 16_16_16_16
                    SetLayout(13, 0, 0, 32); // 32_32_32
                    SetLayout(14, 0, 0, 32); // 32_32_32_32
                    break;
                case 1:
                    SetLayout(3, 1, 0, 8);
                    SetLayout(5, 2, 0, 16);
                    SetLayout(6, 0, 10, 11);
                    SetLayout(7, 0, 11, 11);
                    SetLayout(8, 0, 10, 10);
                    SetLayout(9, 0, 2, 10);
                    SetLayout(10, 1, 0, 8);
                    SetLayout(11, 4, 0, 32);
                    SetLayout(12, 2, 0, 16);
                    SetLayout(13, 4, 0, 32);
                    SetLayout(14, 4, 0, 32);
                    break;
                case 2:
                    SetLayout(6, 0, 21, 11);
                    SetLayout(7, 0, 22, 10);
                    SetLayout(8, 0, 20, 10);
                    SetLayout(9, 0, 12, 10);
                    SetLayout(10, 2, 0, 8);
                    SetLayout(12, 4, 0, 16);
                    SetLayout(13, 8, 0, 32);
                    SetLayout(14, 8, 0, 32);
                    break;
                case 3:
                    SetLayout(8, 0, 30, 2);
                    SetLayout(9, 0, 22, 10);
                    SetLayout(10, 3, 0, 8);
                    SetLayout(12, 6, 0, 16);
                    SetLayout(14, 12, 0, 32);
                    break;
            }

            var packed = LoadUnalignedBufferWord(
                bindingIndex,
                IAdd(elementAddress, byteOffset));
            var raw = _module.AddInstruction(
                SpirvOp.BitFieldUExtract,
                _uintType,
                packed,
                bitOffset,
                bitCount);
            var converted = ConvertGfx10BufferComponent(
                raw,
                bitCount,
                numberFormat,
                dataFormat);
            var valid = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                bitCount,
                UInt(0));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                valid,
                converted,
                component == 3 ? Gfx10FormatOne(numberFormat) : UInt(0));
        }

        private uint ConvertGfx10BufferComponent(
            uint raw,
            uint bitCount,
            uint numberFormat,
            uint dataFormat)
        {
            var widthIs32 = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                bitCount,
                UInt(32));
            var lowMask = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                ShiftLeftLogical(UInt(1), bitCount),
                UInt(1));
            lowMask = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                widthIs32,
                UInt(uint.MaxValue),
                lowMask);

            var signedRaw = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                Bitcast(_intType, raw),
                UInt(0),
                bitCount);
            var signedBits = Bitcast(_uintType, signedRaw);
            var unsignedFloat = _module.AddInstruction(
                SpirvOp.ConvertUToF,
                _floatType,
                raw);
            var signedFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                _floatType,
                signedRaw);

            var unorm = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    unsignedFloat,
                    _module.AddInstruction(
                        SpirvOp.ConvertUToF,
                        _floatType,
                        lowMask)));
            var signedMaximum = ShiftRightLogical(lowMask, UInt(1));
            var snormFloat = _module.AddInstruction(
                SpirvOp.FDiv,
                _floatType,
                signedFloat,
                _module.AddInstruction(
                    SpirvOp.ConvertUToF,
                    _floatType,
                    signedMaximum));
            snormFloat = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    snormFloat,
                    Float(-1f)),
                Float(-1f),
                snormFloat);
            var snorm = Bitcast(_uintType, snormFloat);
            var uscaled = Bitcast(_uintType, unsignedFloat);
            var sscaled = Bitcast(_uintType, signedFloat);

            var unpackedHalf = Ext(62, _vec2Type, BitwiseAnd(raw, UInt(0xFFFF)));
            var half = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    unpackedHalf,
                    0));
            var floating = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    bitCount,
                    UInt(16)),
                half,
                raw);

            // DATA_FORMAT 10_11_11 and 11_11_10 use unsigned mini-floats
            // when NUM_FORMAT is FLOAT, not ordinary integer bit patterns.
            var isPackedFloat = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    dataFormat,
                    UInt(6)),
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    dataFormat,
                    UInt(7)));
            floating = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                isPackedFloat,
                DecodeUnsignedMiniFloat(raw, bitCount),
                floating);

            var result = raw;
            result = SelectUInt(numberFormat, 0, unorm, result);
            result = SelectUInt(numberFormat, 1, snorm, result);
            result = SelectUInt(numberFormat, 2, uscaled, result);
            result = SelectUInt(numberFormat, 3, sscaled, result);
            result = SelectUInt(numberFormat, 4, raw, result);
            result = SelectUInt(numberFormat, 5, signedBits, result);
            result = SelectUInt(numberFormat, 7, floating, result);
            return result;
        }

        private uint DecodeUnsignedMiniFloat(uint raw, uint bitCount)
        {
            var mantissaBits = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                bitCount,
                UInt(5));
            var mantissaMask = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                ShiftLeftLogical(UInt(1), mantissaBits),
                UInt(1));
            var mantissa = BitwiseAnd(raw, mantissaMask);
            var exponent = BitwiseAnd(
                ShiftRightLogical(raw, mantissaBits),
                UInt(0x1F));
            var mantissaShift = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                UInt(23),
                mantissaBits);
            var normalBits = BitwiseOr(
                ShiftLeftLogical(IAdd(exponent, UInt(112)), UInt(23)),
                ShiftLeftLogical(mantissa, mantissaShift));
            var subnormal = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.FMul,
                    _floatType,
                    _module.AddInstruction(
                        SpirvOp.ConvertUToF,
                        _floatType,
                        mantissa),
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            mantissaBits,
                            UInt(6)),
                        Float(1f / 1_048_576f), // 2^-20 for 11-bit UFLOAT
                        Float(1f / 524_288f)))); // 2^-19 for 10-bit UFLOAT
            var special = BitwiseOr(
                UInt(0x7F800000),
                ShiftLeftLogical(mantissa, mantissaShift));
            var result = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    exponent,
                    UInt(0)),
                subnormal,
                normalBits);
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    exponent,
                    UInt(31)),
                special,
                result);
        }

        private uint Gfx10FormatOne(uint numberFormat)
        {
            var isUint = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                numberFormat,
                UInt(4));
            var isSint = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                numberFormat,
                UInt(5));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    isUint,
                    isSint),
                UInt(1),
                UInt(0x3F800000));
        }

        private uint SelectUInt(
            uint selector,
            uint expected,
            uint whenTrue,
            uint whenFalse) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    selector,
                    UInt(expected)),
                whenTrue,
                whenFalse);

        private uint LoadUnalignedBufferWord(int bindingIndex, uint byteAddress)
        {
            var result = UInt(0);
            for (uint index = 0; index < 4; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index));
                var dwordAddress = ShiftRightLogical(address, UInt(2));
                var bitOffset = ShiftLeftLogical(BitwiseAnd(address, UInt(3)), UInt(3));
                var value = BitwiseAnd(
                    ShiftRightLogical(LoadBufferWord(bindingIndex, dwordAddress), bitOffset),
                    UInt(0xFF));
                result = BitwiseOr(result, ShiftLeftLogical(value, UInt(index * 8)));
            }

            return result;
        }

        private uint LoadSubdwordBufferValue(
            int bindingIndex,
            uint byteAddress,
            uint previous,
            uint byteCount,
            bool signExtend,
            bool d16,
            bool d16High)
        {
            var width = byteCount * 8;
            var raw = BitwiseAnd(
                LoadUnalignedBufferWord(bindingIndex, byteAddress),
                UInt(byteCount == 1 ? 0xFFu : 0xFFFFu));
            if (signExtend)
            {
                raw = Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, raw),
                        UInt(0),
                        UInt(width)));
            }

            if (!d16)
            {
                return raw;
            }

            var half = BitwiseAnd(raw, UInt(0xFFFF));
            return d16High
                ? BitwiseOr(
                    BitwiseAnd(previous, UInt(0x0000_FFFF)),
                    ShiftLeftLogical(half, UInt(16)))
                : BitwiseOr(
                    BitwiseAnd(previous, UInt(0xFFFF_0000)),
                    half);
        }

        private void StoreBufferBytes(
            int bindingIndex,
            uint byteAddress,
            uint value,
            uint byteCount,
            uint sourceShift)
        {
            value = ShiftRightLogical(value, UInt(sourceShift));
            for (uint index = 0; index < byteCount; index++)
            {
                var address = index == 0
                    ? byteAddress
                    : IAdd(byteAddress, UInt(index));
                var dwordAddress = ShiftRightLogical(address, UInt(2));
                var shift = ShiftLeftLogical(BitwiseAnd(address, UInt(3)), UInt(3));
                var oldValue = LoadBufferWord(bindingIndex, dwordAddress);
                var byteMask = ShiftLeftLogical(UInt(0xFF), shift);
                var sourceByte = BitwiseAnd(
                    ShiftRightLogical(value, UInt(index * 8)),
                    UInt(0xFF));
                var updated = BitwiseOr(
                    BitwiseAnd(
                        oldValue,
                        _module.AddInstruction(SpirvOp.Not, _uintType, byteMask)),
                    ShiftLeftLogical(sourceByte, shift));
                StoreBufferWord(bindingIndex, dwordAddress, updated);
            }
        }

        private static bool TryGetSubdwordLoadInfo(
            string opcode,
            out uint byteCount,
            out bool signExtend,
            out bool d16,
            out bool d16High)
        {
            byteCount = opcode.Contains("byte", StringComparison.OrdinalIgnoreCase) ? 1u : 2u;
            signExtend = opcode.Contains("Sbyte", StringComparison.Ordinal) ||
                opcode.Contains("Sshort", StringComparison.Ordinal);
            d16 = opcode.Contains("D16", StringComparison.Ordinal);
            d16High = opcode.EndsWith("D16Hi", StringComparison.Ordinal);
            return opcode.Contains("LoadUbyte", StringComparison.Ordinal) ||
                opcode.Contains("LoadSbyte", StringComparison.Ordinal) ||
                opcode.Contains("LoadUshort", StringComparison.Ordinal) ||
                opcode.Contains("LoadSshort", StringComparison.Ordinal) ||
                opcode.Contains("LoadShortD16", StringComparison.Ordinal);
        }

        private static bool TryGetSubdwordStoreInfo(
            string opcode,
            out uint byteCount,
            out uint sourceShift)
        {
            byteCount = opcode.Contains("StoreByte", StringComparison.Ordinal) ? 1u : 2u;
            sourceShift = opcode.EndsWith("D16Hi", StringComparison.Ordinal) ? 16u : 0u;
            return opcode.Contains("StoreByte", StringComparison.Ordinal) ||
                opcode.Contains("StoreShort", StringComparison.Ordinal);
        }

        private static bool IsFormatBufferLoad(string opcode) =>
            opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
            opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal);

        private static bool UsesSampler(string opcode) =>
            opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
            opcode.StartsWith("ImageGather", StringComparison.Ordinal);

        private bool TryResolveDominatingBufferBinding(
            uint pc,
            uint scalarRegister,
            uint registerCount,
            out int bindingIndex)
        {
            if (_bufferBindingByPc.TryGetValue(pc, out bindingIndex))
            {
                return true;
            }

            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                var binding = _evaluation.GlobalMemoryBindings[index];
                if (binding.ScalarAddress != scalarRegister)
                {
                    continue;
                }

                foreach (var candidatePc in binding.InstructionPcs)
                {
                    if (!HasSameScalarDefinitions(
                            candidatePc,
                            pc,
                            scalarRegister,
                            registerCount))
                    {
                        continue;
                    }

                    bindingIndex = _globalBufferBase + index;
                    _bufferBindingByPc.Add(pc, bindingIndex);
                    return true;
                }
            }

            bindingIndex = -1;
            return false;
        }

        private bool TryResolveDominatingImageBinding(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl control,
            out int bindingIndex)
        {
            if (_imageBindingByPc.TryGetValue(instruction.Pc, out bindingIndex) &&
                bindingIndex < _imageResources.Count)
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
                    UsesSampler(instruction.Opcode) &&
                    !HasSameScalarDefinitions(
                        candidate.Pc,
                        instruction.Pc,
                        control.ScalarSampler,
                        SamplerDescriptorDwords))
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
            if (firstRegister + registerCount > ScalarRegisterCount ||
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
                if (definition is ConflictingScalarDefinition or
                        UnreachableScalarDefinition ||
                    target[register] != definition)
                {
                    return false;
                }
            }

            return true;
        }

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
            if (!TryResolveDominatingImageBinding(instruction, image, out var bindingIndex))
            {
                var candidates = _evaluation.ImageBindings
                    .Where(binding =>
                        binding.Control.ScalarResource == image.ScalarResource &&
                        binding.Control.ScalarSampler == image.ScalarSampler)
                    .Take(16)
                    .Select(binding =>
                        $"{binding.Opcode}@0x{binding.Pc:X}" +
                        $"/r={HasSameScalarDefinitions(binding.Pc, instruction.Pc, image.ScalarResource, ImageDescriptorDwords)}" +
                        $"/s={!UsesSampler(instruction.Opcode) || HasSameScalarDefinitions(binding.Pc, instruction.Pc, image.ScalarSampler, SamplerDescriptorDwords)}");
                error =
                    $"unresolved image binding t=s{image.ScalarResource} " +
                    $"s=s{image.ScalarSampler} " +
                    $"candidates=[{string.Join(',', candidates)}]";
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
                        var raw = LoadImageStoreComponent(
                            image,
                            resource,
                            sourceIndex++);
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
                var imageSize = _module.AddInstruction(
                    SpirvOp.ImageQuerySize,
                    _module.TypeVector(_intType, 2),
                    imageObject);
                EmitBoundsCheckedImageWrite(
                    coordinates,
                    imageSize,
                    imageObject,
                    texel);

                return true;
            }

            if (instruction.Opcode.StartsWith("ImageAtomic", StringComparison.Ordinal))
            {
                if (!resource.IsStorage)
                {
                    error = "image atomic is not bound as storage";
                    return false;
                }

                if (resource.ComponentKind == ImageComponentKind.Float ||
                    !TryGetAtomicOp(instruction.Opcode["ImageAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported storage image opcode {instruction.Opcode}";
                    return false;
                }

                var signed = resource.ComponentKind == ImageComponentKind.Sint;
                var atomicImageSize = _module.AddInstruction(
                    SpirvOp.ImageQuerySize,
                    _module.TypeVector(_intType, 2),
                    imageObject);
                var coordinates = BuildClampedIntegerCoordinates(
                    image,
                    0,
                    atomicImageSize);
                EmitExecConditional(() =>
                {
                    var pointer = _module.AddInstruction(
                        SpirvOp.ImageTexelPointer,
                        _module.TypePointer(SpirvStorageClass.Image, resource.ComponentType),
                        resource.Variable,
                        coordinates,
                        UInt(0));
                    uint LoadData(uint register) => signed
                        ? Bitcast(_intType, LoadV(register))
                        : LoadV(register);
                    var original = EmitAtomic(
                        atomicOp,
                        resource.ComponentType,
                        pointer,
                        scope: 1,
                        semantics: 0x808,
                        value: () => LoadData(image.VectorData),
                        comparator: () => LoadData(image.VectorData + 1));
                    if (image.Glc)
                    {
                        StoreV(
                            image.VectorData,
                            signed ? Bitcast(_uintType, original) : original);
                    }
                });

                return true;
            }

            if (resource.IsStorage &&
                instruction.Opcode is not ("ImageLoad" or "ImageLoadMip"))
            {
                error = $"unsupported storage image opcode {instruction.Opcode}";
                return false;
            }

            uint sampled;
            var writeAllComponents = false;
            if (instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                if (resource.IsStorage)
                {
                    var imageSize = _module.AddInstruction(
                        SpirvOp.ImageQuerySize,
                        _module.TypeVector(_intType, 2),
                        imageObject);
                    var coordinates = BuildClampedIntegerCoordinates(
                        image,
                        0,
                        imageSize);
                    sampled = _module.AddInstruction(
                        SpirvOp.ImageRead,
                        resource.VectorType,
                        imageObject,
                        coordinates);
                }
                else
                {
                    var mipLevel = _evaluation.ImageBindings[bindingIndex].MipLevel ?? 0;
                    var fetchedImage = _module.AddInstruction(
                        SpirvOp.Image,
                        resource.ImageType,
                        imageObject);
                    var imageSize = _module.AddInstruction(
                        SpirvOp.ImageQuerySizeLod,
                        _module.TypeVector(_intType, 2),
                        fetchedImage,
                        UInt(mipLevel));
                    var coordinates = BuildClampedIntegerCoordinates(
                        image,
                        0,
                        imageSize);
                    sampled = _module.AddInstruction(
                        SpirvOp.ImageFetch,
                        resource.VectorType,
                        fetchedImage,
                        coordinates,
                        2,
                        UInt(mipLevel));
                }
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageSample",
                         StringComparison.Ordinal))
            {
                var hasOffset =
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal);
                var hasCompare =
                    instruction.Opcode.Contains("SampleC", StringComparison.Ordinal);
                var hasGradients =
                    instruction.Opcode.Contains("SampleD", StringComparison.Ordinal);
                var hasZeroLod =
                    instruction.Opcode.Contains("Lz", StringComparison.Ordinal);
                var hasLod = !hasZeroLod &&
                    instruction.Opcode.Contains("SampleL", StringComparison.Ordinal);
                var hasBias =
                    instruction.Opcode.Contains("SampleB", StringComparison.Ordinal);

                // RDNA MIMG address operands are ordered as
                // {offset}{bias/lod}{z-compare}{derivatives}{body}.  The old
                // lowering treated SAMPLE_D as body-first and consequently
                // sampled gradients as coordinates in every captured
                // derivative operation.
                var addressCursor = 0;
                var offset = 0u;
                if (hasOffset)
                {
                    addressCursor = AlignFullImageAddress(image, addressCursor);
                    offset = BuildImageOffset(image, addressCursor);
                    addressCursor += ImageFullAddressSlots(image);
                }

                // SAMPLE_B prefixes the body with a bias. SAMPLE_L instead
                // carries LOD as the final body component (x, y, lod for 2D),
                // per the RDNA image-address table.
                var lodOrBias = hasBias
                    ? LoadImageFloatAddress(image, addressCursor++)
                    : 0u;
                var reference = 0u;
                if (hasCompare)
                {
                    // PCF references remain full-width even when A16 packs the
                    // ordinary address components two per VGPR.
                    addressCursor = AlignFullImageAddress(image, addressCursor);
                    reference = Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(
                            ImageAddressRegister(image, addressCursor))));
                    addressCursor += ImageFullAddressSlots(image);
                }

                var gradientX = hasGradients
                    ? BuildFloatCoordinates(image, addressCursor)
                    : 0u;
                var gradientY = hasGradients
                    ? BuildFloatCoordinates(image, addressCursor + 2)
                    : 0u;
                if (hasGradients)
                {
                    addressCursor += 4;
                }

                var coordinates = BuildFloatCoordinates(image, addressCursor);
                var explicitLod = hasGradients || hasZeroLod || hasLod;
                var lod = hasZeroLod
                    ? Float(0)
                    : hasLod
                        ? LoadImageFloatAddress(image, addressCursor + 2)
                        : lodOrBias;
                if (hasOffset)
                {
                    // Vulkan before maintenance8 forbids the dynamic Offset
                    // image operand on non-gather sampling operations. RDNA
                    // offsets are per-lane VGPR values, so ConstOffset is not
                    // equivalent. Fold the texel offset into normalized sample
                    // coordinates using the queried mip extent instead.
                    var offsetLod = explicitLod && !hasGradients
                        ? lod
                        : Float(0);
                    coordinates = ApplyDynamicSampleOffset(
                        resource,
                        imageObject,
                        coordinates,
                        offset,
                        offsetLod);
                }

                var imageOperands =
                    hasGradients ? 4u : explicitLod ? 2u : hasBias ? 1u : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };

                if (imageOperands != 0)
                {
                    operands.Add(imageOperands);
                    if (hasGradients)
                    {
                        operands.Add(gradientX);
                        operands.Add(gradientY);
                    }
                    else if (explicitLod)
                    {
                        operands.Add(lod);
                    }
                    else if (hasBias)
                    {
                        operands.Add(lodOrBias);
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
                var addressCursor = 0;
                var offset = 0u;
                if (hasOffset)
                {
                    offset = BuildImageOffset(image, addressCursor);
                    addressCursor += ImageFullAddressSlots(image);
                }

                var reference = 0u;
                if (hasCompare)
                {
                    addressCursor = AlignFullImageAddress(image, addressCursor);
                    reference = Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(
                            ImageAddressRegister(image, addressCursor))));
                    addressCursor += ImageFullAddressSlots(image);
                }

                var coordinates = BuildFloatCoordinates(image, addressCursor);
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

            var outputValues = new List<uint>(4);
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
                outputValues.Add(raw);
            }

            if (_stage == Gen5SpirvStage.Pixel &&
                PixelImageCaptureAddressMatches() &&
                uint.TryParse(
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_CAPTURE_PIXEL_IMAGE_PC"),
                    out var captureImagePc) &&
                instruction.Pc == captureImagePc)
            {
                var captureBase = 248u;
                if (uint.TryParse(
                        Environment.GetEnvironmentVariable(
                            "SHARPEMU_CAPTURE_PIXEL_IMAGE_VGPR_BASE"),
                        out var requestedCaptureBase))
                {
                    captureBase = requestedCaptureBase;
                }
                captureBase = captureBase <= 252 ? captureBase : 248u;
                for (var component = 0; component < 4; component++)
                {
                    StoreV(
                        captureBase + (uint)component,
                        component < outputValues.Count
                            ? outputValues[component]
                            : Bitcast(_uintType, Float(1)));
                }
            }

            if (image.D16)
            {
                for (var index = 0; index < outputValues.Count; index += 2)
                {
                    var low = outputValues[index];
                    var high = index + 1 < outputValues.Count
                        ? outputValues[index + 1]
                        : UInt(0);
                    StoreV(
                        image.VectorData + (uint)(index / 2),
                        PackImageD16(resource, low, high));
                }
            }
            else
            {
                for (var index = 0; index < outputValues.Count; index++)
                {
                    StoreV(image.VectorData + (uint)index, outputValues[index]);
                }
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
            var x = LoadImageFloatAddress(image, start);
            var y = LoadImageFloatAddress(image, start + 1);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec2Type,
                x,
                y);
        }

        private static int ImageAddressRegister(
            Gen5ImageControl image,
            int component) => image.A16 ? component / 2 : component;

        private static int ImageFullAddressSlots(Gen5ImageControl image) =>
            image.A16 ? 2 : 1;

        private static int AlignFullImageAddress(
            Gen5ImageControl image,
            int component) => image.A16 ? (component + 1) & ~1 : component;

        private uint LoadImageFloatAddress(Gen5ImageControl image, int component)
        {
            var raw = LoadV(image.GetAddressRegister(
                ImageAddressRegister(image, component)));
            if (!image.A16)
            {
                return Bitcast(_floatType, raw);
            }

            var unpacked = Ext(62, _vec2Type, raw);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private uint LoadImageIntegerAddress(Gen5ImageControl image, int component)
        {
            var raw = LoadV(image.GetAddressRegister(
                ImageAddressRegister(image, component)));
            if (!image.A16)
            {
                return raw;
            }

            return BitwiseAnd(
                ShiftRightLogical(raw, UInt((uint)((component & 1) * 16))),
                UInt(0xFFFF));
        }

        private uint LoadImageStoreComponent(
            Gen5ImageControl image,
            SpirvImageResource resource,
            uint component)
        {
            if (!image.D16)
            {
                return LoadV(image.VectorData + component);
            }

            var packed = LoadV(image.VectorData + component / 2);
            if (resource.ComponentKind == ImageComponentKind.Float)
            {
                var unpacked = Ext(62, _vec2Type, packed);
                return Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        unpacked,
                        component & 1));
            }

            var shifted = ShiftRightLogical(packed, UInt((component & 1) * 16));
            var low = BitwiseAnd(shifted, UInt(0xFFFF));
            if (resource.ComponentKind != ImageComponentKind.Sint)
            {
                return low;
            }

            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    Bitcast(_intType, low),
                    UInt(0),
                    UInt(16)));
        }

        private uint PackImageD16(
            SpirvImageResource resource,
            uint low,
            uint high)
        {
            if (resource.ComponentKind == ImageComponentKind.Float)
            {
                var pair = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec2Type,
                    Bitcast(_floatType, low),
                    Bitcast(_floatType, high));
                return Ext(58, _uintType, pair);
            }

            return BitwiseOr(
                BitwiseAnd(low, UInt(0xFFFF)),
                ShiftLeftLogical(BitwiseAnd(high, UInt(0xFFFF)), UInt(16)));
        }

        private uint BuildIntegerCoordinates(Gen5ImageControl image, int start)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = Bitcast(_intType, LoadImageIntegerAddress(image, start));
            var y = Bitcast(_intType, LoadImageIntegerAddress(image, start + 1));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint BuildClampedIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            uint imageSize)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadImageIntegerAddress(image, start)),
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _intType,
                    imageSize,
                    0));
            var y = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadImageIntegerAddress(image, start + 1)),
                _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _intType,
                    imageSize,
                    1));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint ClampSignedCoordinate(uint value, uint extent)
        {
            var zero = _module.Constant(_intType, 0);
            var max = _module.AddInstruction(
                SpirvOp.ISub,
                _intType,
                extent,
                _module.Constant(_intType, 1));
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
            uint imageSize,
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
            var width = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                imageSize,
                0);
            var height = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                imageSize,
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
                width);
            var yInRange = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                y,
                height);
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

        private uint BuildImageOffset(Gen5ImageControl image, int component)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var packed = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(
                    ImageAddressRegister(image, component))));
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

        private uint ApplyDynamicSampleOffset(
            SpirvImageResource resource,
            uint sampledImage,
            uint coordinates,
            uint texelOffset,
            uint lod)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var image = _module.AddInstruction(
                SpirvOp.Image,
                resource.ImageType,
                sampledImage);
            var signedLod = _module.AddInstruction(
                SpirvOp.ConvertFToS,
                _intType,
                lod);
            var lodIsNegative = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                signedLod,
                _module.Constant(_intType, 0));
            var clampedLod = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                lodIsNegative,
                _module.Constant(_intType, 0),
                signedLod);
            var size = _module.AddInstruction(
                SpirvOp.ImageQuerySizeLod,
                ivec2,
                image,
                clampedLod);
            var sizeFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                _vec2Type,
                size);
            var offsetFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                _vec2Type,
                texelOffset);
            var normalizedOffset = _module.AddInstruction(
                SpirvOp.FDiv,
                _vec2Type,
                offsetFloat,
                sizeFloat);
            return _module.AddInstruction(
                SpirvOp.FAdd,
                _vec2Type,
                coordinates,
                normalizedOffset);
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

                Store(_reachedPixelExport, _module.ConstantBool(true));

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
                if (output.Kind == Gen5PixelOutputKind.Float &&
                    PixelExportVgprAddressMatches() &&
                    uint.TryParse(
                        Environment.GetEnvironmentVariable(
                            "SHARPEMU_FORCE_PIXEL_EXPORT_VGPR_BASE"),
                        out var debugVgprBase))
                {
                    var registerBase = debugVgprBase + export.Target * 4;
                    vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        output.Type,
                        Bitcast(_floatType, LoadV(registerBase)),
                        Bitcast(_floatType, LoadV(registerBase + 1)),
                        Bitcast(_floatType, LoadV(registerBase + 2)),
                        Bitcast(_floatType, LoadV(registerBase + 3)));
                }
                if (output.Kind == Gen5PixelOutputKind.Float &&
                    PixelExportVgprAddressMatches() &&
                    uint.TryParse(
                        Environment.GetEnvironmentVariable(
                            "SHARPEMU_FORCE_PIXEL_EXPORT_PACK_VGPR_BASE"),
                        out var debugPackVgprBase))
                {
                    var registerBase = debugPackVgprBase + export.Target * 4;
                    var lowPair = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        TruncateFloat32ForPack(Bitcast(_floatType, LoadV(registerBase))),
                        TruncateFloat32ForPack(Bitcast(_floatType, LoadV(registerBase + 1))));
                    var highPair = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        TruncateFloat32ForPack(Bitcast(_floatType, LoadV(registerBase + 2))),
                        TruncateFloat32ForPack(Bitcast(_floatType, LoadV(registerBase + 3))));
                    var unpackedLow = Ext(62, _vec2Type, Ext(58, _uintType, lowPair));
                    var unpackedHigh = Ext(62, _vec2Type, Ext(58, _uintType, highPair));
                    vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        output.Type,
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedLow,
                            0),
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedLow,
                            1),
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedHigh,
                            0),
                        _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            unpackedHigh,
                            1));
                }
                if (_forcePixelMagenta && PixelExportDebugAddressMatches())
                {
                    vector = output.Kind switch
                    {
                        Gen5PixelOutputKind.Float =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                output.Type,
                                Float(1f),
                                Float(0f),
                                Float(1f),
                                Float(1f)),
                        Gen5PixelOutputKind.Sint =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                output.Type,
                                Bitcast(_intType, UInt(1)),
                                Bitcast(_intType, UInt(0)),
                                Bitcast(_intType, UInt(1)),
                                Bitcast(_intType, UInt(1))),
                        _ =>
                            _module.AddInstruction(
                                SpirvOp.CompositeConstruct,
                                output.Type,
                                UInt(1),
                                UInt(0),
                                UInt(1),
                                UInt(1)),
                    };
                }
                if (Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_TITLE_EXPORT_EXEC") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    Store(_exec, _module.ConstantBool(true));
                    StoreS64(
                        126,
                        _module.Constant64(_ulongType, 1));
                }
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
            if (_state.Program.Address == 0x0000000500780000ul &&
                export.Target is >= 32 and < 36 &&
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_FORCE_TITLE_VERTEX_OUTPUTS_ONE") == "1")
            {
                outputValue = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec4Type,
                    Float(1f),
                    Float(1f),
                    Float(1f),
                    Float(1f));
            }
            outputValue = _module.AddInstruction(
                SpirvOp.Select,
                _vec4Type,
                Load(_boolType, _exec),
                outputValue,
                Load(_vec4Type, outputVariable));
            Store(outputVariable, outputValue);
            return true;
        }

        private bool PixelExportDebugAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "SHARPEMU_FORCE_PIXEL_EXPORT_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return true;
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private bool PixelImageCaptureAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "SHARPEMU_CAPTURE_PIXEL_IMAGE_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return false;
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private void CapturePixelVgprs(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches() ||
                !uint.TryParse(
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_CAPTURE_PIXEL_VGPR_PC"),
                    out var capturePc) ||
                instruction.Pc != capturePc)
            {
                return;
            }

            var sourceText = Environment.GetEnvironmentVariable(
                "SHARPEMU_CAPTURE_PIXEL_VGPR_SOURCES");
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                return;
            }

            var destinationBase = 248u;
            if (uint.TryParse(
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_CAPTURE_PIXEL_VGPR_DEST_BASE"),
                    out var requestedDestinationBase))
            {
                destinationBase = requestedDestinationBase;
            }

            var sources = sourceText.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            if (sources.Length is 0 or > 4 ||
                destinationBase > 252 ||
                destinationBase + (uint)sources.Length > 256)
            {
                return;
            }

            for (var index = 0; index < sources.Length; index++)
            {
                if (!uint.TryParse(sources[index], out var source) ||
                    source >= 256)
                {
                    return;
                }
            }

            for (var index = 0; index < sources.Length; index++)
            {
                _ = uint.TryParse(sources[index], out var source);
                StoreV(
                    destinationBase + (uint)index,
                    LoadV(source),
                    guardWithExec:
                        Environment.GetEnvironmentVariable(
                            "SHARPEMU_CAPTURE_PIXEL_VGPR_IGNORE_EXEC") != "1");
            }
        }

        private bool PixelVgprCaptureAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return false;
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private void CapturePixelVgprPoints(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches())
            {
                return;
            }

            var captureText = Environment.GetEnvironmentVariable(
                "SHARPEMU_CAPTURE_PIXEL_VGPR_POINTS");
            if (string.IsNullOrWhiteSpace(captureText))
            {
                return;
            }

            foreach (var capture in captureText.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var fields = capture.Split(':');
                if (fields.Length != 3 ||
                    !uint.TryParse(fields[0], out var pc) ||
                    !uint.TryParse(fields[1], out var source) ||
                    !uint.TryParse(fields[2], out var destination) ||
                    pc != instruction.Pc || source >= 256 || destination >= 256)
                {
                    continue;
                }

                StoreV(
                    destination,
                    LoadV(source),
                    guardWithExec:
                        Environment.GetEnvironmentVariable(
                            "SHARPEMU_CAPTURE_PIXEL_VGPR_IGNORE_EXEC") != "1");
            }
        }

        private void MarkPixelPath(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches())
            {
                return;
            }

            var markerText = Environment.GetEnvironmentVariable(
                "SHARPEMU_MARK_PIXEL_PCS");
            if (string.IsNullOrWhiteSpace(markerText))
            {
                return;
            }

            foreach (var marker in markerText.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var separator = marker.IndexOf(':');
                if (separator <= 0 || separator == marker.Length - 1 ||
                    !uint.TryParse(marker.AsSpan(0, separator), out var pc) ||
                    !uint.TryParse(marker.AsSpan(separator + 1), out var register) ||
                    pc != instruction.Pc || register >= 256)
                {
                    continue;
                }

                StoreV(
                    register,
                    Bitcast(_uintType, Float(1)),
                    guardWithExec: false);
            }
        }

        private void CapturePixelExec(Gen5ShaderInstruction instruction)
        {
            if (_stage != Gen5SpirvStage.Pixel ||
                !PixelVgprCaptureAddressMatches())
            {
                return;
            }

            var captureText = Environment.GetEnvironmentVariable(
                "SHARPEMU_CAPTURE_PIXEL_EXEC_PCS");
            if (string.IsNullOrWhiteSpace(captureText))
            {
                return;
            }

            foreach (var capture in captureText.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var separator = capture.IndexOf(':');
                if (separator <= 0 || separator == capture.Length - 1 ||
                    !uint.TryParse(capture.AsSpan(0, separator), out var pc) ||
                    !uint.TryParse(capture.AsSpan(separator + 1), out var register) ||
                    pc != instruction.Pc || register >= 256)
                {
                    continue;
                }

                var value = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    Load(_boolType, _exec),
                    Float(1),
                    Float(0));
                StoreV(
                    register,
                    Bitcast(_uintType, value),
                    guardWithExec: false);
            }
        }

        private bool PixelExportVgprAddressMatches()
        {
            var addressFilter = Environment.GetEnvironmentVariable(
                "SHARPEMU_FORCE_PIXEL_EXPORT_VGPR_ADDRESS");
            if (string.IsNullOrWhiteSpace(addressFilter))
            {
                return PixelExportDebugAddressMatches();
            }

            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _state.Program.Address == address;
        }

        private uint LoadCompressedExportComponent(
            Gen5ShaderInstruction instruction,
            int component)
        {
            if (TryLoadPackedHalfExportComponent(
                    instruction,
                    component,
                    out var shadowValue))
            {
                return shadowValue;
            }

            var packed = LoadV(instruction.Sources[component >> 1].Value);
            var unpacked = Ext(62, _vec2Type, packed);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private bool TryLoadPackedHalfExportComponent(
            Gen5ShaderInstruction exportInstruction,
            int component,
            out uint value)
        {
            value = 0;
            var packedSource = exportInstruction.Sources[component >> 1];
            var tracePackedExport =
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_PACKED_EXPORT") == "1" &&
                _state.Program.Address == 0x0000000500781200ul;
            if (tracePackedExport)
            {
                Console.Error.WriteLine(
                    $"[AGC][PACKED-EXPORT] exp_pc=0x{exportInstruction.Pc:X} " +
                    $"component={component} source={packedSource.Kind}:" +
                    $"{packedSource.Value}");
                if (component == 0 && exportInstruction.Pc == 0x630)
                {
                    foreach (var decoded in _state.Program.Instructions.Where(
                                 static decoded => decoded.Pc <= 0x640))
                    {
                        Console.Error.WriteLine(
                            $"[AGC][TITLE-IR] 0x{decoded.Pc:X4} " +
                            $"{decoded.Opcode} dst=[" +
                            string.Join(',', decoded.Destinations) +
                            "] src=[" +
                            string.Join(',', decoded.Sources) + "] words=[" +
                            string.Join(',', decoded.Words.Select(static word => $"{word:X8}")) +
                            "] ctrl=" + decoded.Control);
                    }
                }
            }
            if (packedSource.Kind != Gen5OperandKind.VectorRegister)
            {
                if (tracePackedExport)
                {
                    Console.Error.WriteLine(
                        "[AGC][PACKED-EXPORT] rejected: source is not a VGPR");
                }
                return false;
            }

            for (var index = _state.Program.Instructions.Count - 1; index >= 0; index--)
            {
                var candidate = _state.Program.Instructions[index];
                if (candidate.Pc >= exportInstruction.Pc)
                {
                    continue;
                }

                if (exportInstruction.Pc - candidate.Pc > 128)
                {
                    break;
                }

                if (!candidate.Destinations.Any(destination =>
                        destination.Kind == Gen5OperandKind.VectorRegister &&
                        destination.Value == packedSource.Value))
                {
                    continue;
                }

                if (tracePackedExport)
                {
                    Console.Error.WriteLine(
                        $"[AGC][PACKED-EXPORT] nearest_pc=0x{candidate.Pc:X} " +
                        $"opcode={candidate.Opcode} distance=" +
                        $"{exportInstruction.Pc - candidate.Pc}");
                }

                if (candidate.Opcode != "VCvtPkrtzF16F32" ||
                    candidate.Sources.Count < 2)
                {
                    if (tracePackedExport)
                    {
                        Console.Error.WriteLine(
                            "[AGC][PACKED-EXPORT] rejected: nearest writer is " +
                            candidate.Opcode);
                    }
                    return false;
                }

                var packedPointer = PackedHalfPointer(packedSource.Value);
                if (Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_PACKED_EXPORT_STORE_ONE") == "1" &&
                    _state.Program.Address == 0x0000000500781200ul)
                {
                    Store(
                        packedPointer,
                        _module.AddInstruction(
                            SpirvOp.CompositeConstruct,
                            _vec2Type,
                            Float(1f),
                            Float(1f)));
                }

                var packedPair = Load(
                    _vec2Type,
                    packedPointer);
                value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    packedPair,
                    (uint)(component & 1));
                if (Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_PACKED_EXPORT_ONE") == "1")
                {
                    value = Float(1f);
                }
                if (tracePackedExport)
                {
                    Console.Error.WriteLine(
                        "[AGC][PACKED-EXPORT] selected shadow pair");
                }
                return true;
            }

            if (tracePackedExport)
            {
                Console.Error.WriteLine(
                    "[AGC][PACKED-EXPORT] rejected: no nearby writer");
            }
            return false;
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
            var inRange = IsBufferWordInRange(binding, dwordAddress);
            var safeAddress = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                dwordAddress,
                UInt(0));
            var value = Load(_uintType, BufferWordPointer(binding, safeAddress));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                value,
                UInt(0));
        }

        private uint ApplyGuestBufferByteBias(int binding, uint byteAddress)
        {
            var evaluationBinding = binding - _globalBufferBase;
            if ((uint)evaluationBinding >=
                (uint)_evaluation.GlobalMemoryBindings.Count)
            {
                // Runtime SGPR blocks and other synthetic descriptors do not
                // alias guest virtual memory and are always bound at offset 0.
                return byteAddress;
            }

            if (_initialScalarBufferIndex >= 0)
            {
                // Descriptor offsets must satisfy Vulkan's storage-buffer
                // alignment. The presenter therefore rounds the shared guest
                // allocation offset down and packs the discarded low address
                // bits after the 256 initial SGPRs in the per-dispatch runtime
                // block. Keeping this value runtime-stable prevents rotating
                // guest allocations from producing a new multi-megabyte SPIR-V
                // module and Metal pipeline while preserving exact byte access.
                var runtimeByteBias = Load(
                    _uintType,
                    RuntimeBufferBiasPointer(binding));
                return IAdd(byteAddress, runtimeByteBias);
            }

            // The presenter binds the shared allocation at the largest aligned
            // offset not greater than this guest resource's offset. Because the
            // allocation base is aligned to the same power of two, the bytes
            // discarded from the descriptor offset are exactly the low address
            // bits below. Adding them here keeps scalar, MUBUF and GLOBAL paths
            // byte-exact, including atomics and resources that overlap another
            // descriptor at an unaligned guest address.
            var byteBias =
                _evaluation.GlobalMemoryBindings[evaluationBinding].BaseAddress &
                (_storageBufferOffsetAlignment - 1);
            return byteBias == 0
                ? byteAddress
                : IAdd(byteAddress, UInt(checked((uint)byteBias)));
        }

        private void StoreBufferWord(int binding, uint dwordAddress, uint value)
        {
            EmitConditional(
                IsBufferWordInRange(binding, dwordAddress),
                () => Store(BufferWordPointer(binding, dwordAddress), value));
        }

        private uint IsBufferWordInRange(int binding, uint dwordAddress)
        {
            var buffer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageBlockPointer,
                _globalBuffers,
                UInt((uint)binding));
            var length = _module.AddInstruction(
                SpirvOp.ArrayLength,
                _uintType,
                buffer,
                0);
            return _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                dwordAddress,
                length);
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

        private uint RuntimeBufferBiasPointer(int binding) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _runtimeBufferBiases,
                UInt(checked((uint)binding)));

        private uint VectorPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _vectorRegisters,
                UInt(register));

        private uint PackedHalfPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateVec2Pointer,
                _packedHalfRegisters,
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

        private void StorePackedHalf(uint register, uint value)
        {
            var active = Load(_boolType, _exec);
            if (Environment.GetEnvironmentVariable(
                    "SHARPEMU_FORCE_PACKED_STORE_EXEC_VALUES") == "1" &&
                _state.Program.Address == 0x0000000500781200ul)
            {
                var activePair = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec2Type,
                    Float(1f),
                    Float(1f));
                var inactivePair = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    _vec2Type,
                    Float(0.5f),
                    Float(0.5f));
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _vec2Type,
                    active,
                    activePair,
                    inactivePair);
                Store(PackedHalfPointer(register), value);
                return;
            }

            value = _module.AddInstruction(
                SpirvOp.Select,
                _vec2Type,
                active,
                value,
                Load(_vec2Type, PackedHalfPointer(register)));
            Store(PackedHalfPointer(register), value);
        }

        private uint Load(uint type, uint pointer)
        {
            if (pointer == 0)
            {
                throw new InvalidOperationException(
                    "SPIR-V generator attempted OpLoad from id 0.");
            }

            return _module.AddInstruction(SpirvOp.Load, type, pointer);
        }

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

        private uint BitwiseOr64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, left, right);

        private uint BitwiseOr(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _uintType, left, right);

        private uint BitwiseXor(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseXor, _uintType, left, right);

        private uint LogicalNot(uint value) =>
            _module.AddInstruction(SpirvOp.LogicalNot, _boolType, value);

        private uint SubgroupAny(uint condition) =>
            _subgroupInvocationIdInput == 0
                ? condition
                : _emulateWave64
                    ? IsNotZero64(BooleanToWaveMask(condition))
                : _module.AddInstruction(
                    SpirvOp.GroupNonUniformAny,
                    _boolType,
                    UInt(3),
                    condition);

        private uint GuestWaveLane()
        {
            if (_waveLaneCount == 64 && _localInvocationIndexInput != 0)
            {
                return BitwiseAnd(
                    Load(_uintType, _localInvocationIndexInput),
                    UInt(63));
            }

            if (_subgroupInvocationIdInput != 0)
            {
                return BitwiseAnd(
                    Load(_uintType, _subgroupInvocationIdInput),
                    UInt(31));
            }

            // Graphics stages without subgroup support have one logical lane;
            // they must not emit OpLoad for absent SPIR-V input ID zero.
            return UInt(0);
        }

        private uint CurrentLaneBit()
        {
            if (_subgroupInvocationIdInput == 0)
            {
                return _module.Constant64(_ulongType, 1);
            }

            var maskedLane = GuestWaveLane();
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
                UInt(32));

        private uint BooleanToLaneMask(uint condition) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                condition,
                CurrentLaneBit(),
                _module.Constant64(_ulongType, 0));

        private uint BooleanToWaveMask(uint condition)
        {
            if (_subgroupInvocationIdInput == 0)
            {
                return BooleanToLaneMask(condition);
            }

            var ballot = _module.AddInstruction(
                SpirvOp.GroupNonUniformBallot,
                _uvec4Type,
                UInt(3),
                condition);
            var low = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                ballot,
                0);
            if (_emulateWave64)
            {
                var subgroupLane =
                    Load(_uintType, _subgroupInvocationIdInput);
                var firstLane = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    subgroupLane,
                    UInt(0));
                var half = ShiftRightLogical(GuestWaveLane(), UInt(5));
                EmitConditional(firstLane, () =>
                {
                    Store(WaveMaskScratchPointer(half), low);
                });
                EmitWave64Barrier();
                var lowMask = Load(
                    _uintType,
                    WaveMaskScratchPointer(UInt(0)));
                var highMask = Load(
                    _uintType,
                    WaveMaskScratchPointer(UInt(1)));
                var combined = BitwiseOr64(
                    _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        lowMask),
                    ShiftLeftLogical64(
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _ulongType,
                            highMask),
                        _module.Constant64(_ulongType, 32)));
                EmitWave64Barrier();
                return combined;
            }

            var widened = _module.AddInstruction(SpirvOp.UConvert, _ulongType, low);
            if (_waveLaneCount != 64)
            {
                return widened;
            }

            return _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                _module.AddInstruction(
                    SpirvOp.UGreaterThanEqual,
                    _boolType,
                    GuestWaveLane(),
                    UInt(32)),
                ShiftLeftLogical64(
                    widened,
                    _module.Constant64(_ulongType, 32)),
                widened);
        }

        private uint WaveMaskScratchPointer(uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _waveMaskScratchElementPointer,
                _waveScratchInLds ? _lds : _waveMaskScratch,
                _waveScratchInLds ? IAdd(UInt(LdsDwordCount - 3), index) : index);

        private uint WaveBroadcastScratchPointer() =>
            _waveScratchInLds
                ? _module.AddInstruction(
                    SpirvOp.AccessChain,
                    _ldsElementPointer,
                    _lds,
                    UInt(LdsDwordCount - 1))
                : _waveBroadcastScratch;

        private void EmitWave64Barrier()
        {
            var workgroup = UInt(2);
            _module.AddStatement(
                SpirvOp.ControlBarrier,
                workgroup,
                workgroup,
                UInt(0x108));
        }

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
            StoreS64(register, BooleanToWaveMask(condition));

        private void EmitExecConditional(Action emit)
        {
            var active = Load(_boolType, _exec);
            EmitConditional(active, emit);
        }

        private void EmitConditional(uint condition, Action emit)
        {
            var activeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                condition,
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
                instruction.Control is Gen5DppControl or Gen5Dpp8Control ||
                instruction.Opcode is "VPermlane16B32" or "VPermlanex16B32" or "VReadlaneB32");

        private bool UsesSubgroupBroadcast() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode == "VReadfirstlaneB32");

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
            (UsesSubgroupShuffle() ||
             UsesSubgroupBroadcast() ||
             UsesWaveControl() ||
             _state.Program.Instructions.Any(static instruction =>
                 instruction.Opcode is "VMbcntLoU32B32" or "VMbcntHiU32B32"));

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

        private void BuildScalarDefinitionInfo(
            IReadOnlyList<ShaderBlock> blocks,
            IReadOnlyList<Gen5ShaderInstruction> instructions)
        {
            var predecessors = new HashSet<int>[blocks.Count];
            for (var index = 0; index < blocks.Count; index++)
            {
                predecessors[index] = [];
            }

            void AddEdge(int source, int destination)
            {
                if (destination < 0 || destination >= blocks.Count)
                {
                    return;
                }

                predecessors[destination].Add(source);
            }

            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                var block = blocks[blockIndex];
                var terminator = instructions[block.EndIndex - 1];
                var hasFallthrough = blockIndex + 1 < blocks.Count;
                if (terminator.Opcode == "SEndpgm")
                {
                    continue;
                }

                if (terminator.Opcode == "SBranch")
                {
                    if (TryGetBranchTargetPc(terminator, out var targetPc) &&
                        TryFindBlock(blocks, targetPc, out var targetBlock))
                    {
                        AddEdge(blockIndex, targetBlock);
                    }

                    continue;
                }

                if (terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
                {
                    if (TryGetBranchTargetPc(terminator, out var targetPc) &&
                        TryFindBlock(blocks, targetPc, out var targetBlock))
                    {
                        AddEdge(blockIndex, targetBlock);
                    }

                    if (hasFallthrough)
                    {
                        AddEdge(blockIndex, blockIndex + 1);
                    }

                    continue;
                }

                if (hasFallthrough)
                {
                    AddEdge(blockIndex, blockIndex + 1);
                }
            }

            var blockInputs = new long[blocks.Count][];
            var blockOutputs = new long[blocks.Count][];
            var hasOutput = new bool[blocks.Count];
            var initialDefinitions = Enumerable.Repeat(
                InitialScalarDefinition,
                ScalarRegisterCount).ToArray();

            static void MergeDefinitions(
                long[] destination,
                long[] source,
                ref bool hasInput)
            {
                if (!hasInput)
                {
                    Array.Copy(source, destination, ScalarRegisterCount);
                    hasInput = true;
                    return;
                }

                for (var register = 0; register < ScalarRegisterCount; register++)
                {
                    if (destination[register] != source[register])
                    {
                        destination[register] = ConflictingScalarDefinition;
                    }
                }
            }

            static void ApplyScalarDefinitions(
                long[] definitions,
                ShaderBlock block,
                IReadOnlyList<Gen5ShaderInstruction> blockInstructions)
            {
                for (var instructionIndex = block.StartIndex;
                     instructionIndex < block.EndIndex;
                     instructionIndex++)
                {
                    var instruction = blockInstructions[instructionIndex];
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.ScalarRegister &&
                            destination.Value < ScalarRegisterCount)
                        {
                            definitions[destination.Value] = instruction.Pc + 1L;
                        }
                    }
                }
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                {
                    var input = Enumerable.Repeat(
                        UnreachableScalarDefinition,
                        ScalarRegisterCount).ToArray();
                    var hasInput = false;
                    if (blockIndex == 0)
                    {
                        MergeDefinitions(input, initialDefinitions, ref hasInput);
                    }

                    foreach (var predecessor in predecessors[blockIndex])
                    {
                        if (hasOutput[predecessor])
                        {
                            MergeDefinitions(
                                input,
                                blockOutputs[predecessor],
                                ref hasInput);
                        }
                    }

                    if (!hasInput)
                    {
                        continue;
                    }

                    var output = (long[])input.Clone();
                    ApplyScalarDefinitions(output, blocks[blockIndex], instructions);
                    if (!hasOutput[blockIndex] ||
                        !blockInputs[blockIndex].AsSpan().SequenceEqual(input) ||
                        !blockOutputs[blockIndex].AsSpan().SequenceEqual(output))
                    {
                        blockInputs[blockIndex] = input;
                        blockOutputs[blockIndex] = output;
                        hasOutput[blockIndex] = true;
                        changed = true;
                    }
                }
            }

            _scalarDefinitionsBeforePc.Clear();
            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                if (!hasOutput[blockIndex])
                {
                    continue;
                }

                var definitions = (long[])blockInputs[blockIndex].Clone();
                var block = blocks[blockIndex];
                for (var instructionIndex = block.StartIndex;
                     instructionIndex < block.EndIndex;
                     instructionIndex++)
                {
                    var instruction = instructions[instructionIndex];
                    if (instruction.Control is Gen5ImageControl or
                            Gen5ScalarMemoryControl or
                            Gen5GlobalMemoryControl or
                            Gen5BufferMemoryControl)
                    {
                        _scalarDefinitionsBeforePc[instruction.Pc] =
                            (long[])definitions.Clone();
                    }
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.ScalarRegister &&
                            destination.Value < ScalarRegisterCount)
                        {
                            definitions[destination.Value] = instruction.Pc + 1L;
                        }
                    }
                }
            }
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
