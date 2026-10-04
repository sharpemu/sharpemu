// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private const uint VectorRegisterCount = 512;
    private const uint LdsDwordCount = 8192;
    // Graphics stages model LDS as a per-invocation Private array rather than
    // real workgroup-shared memory. A full 32 KB Private array per vertex/pixel
    // invocation is wasteful and risks Metal compile limits, and per-invocation
    // write-then-read correctness only needs deterministic address→slot masking,
    // so a smaller array is safe.
    private const uint PrivateLdsDwordCount = 2048;
    private const uint RdnaWaveLaneCount = 32;
    private const uint Wave64ExchangeSlotCount = 4;
    private const uint Wave64ExchangeDwordCount = Wave64ExchangeSlotCount * 2;
    private static int _warnedMemRealtimeFallback;
    private static int _warnedGather4LApproximation;
    internal static SpirvImageFormat DecodeStorageImageFormat(
        uint dataFormat,
        uint numberType) =>
        CompilationContext.DecodeStorageImageFormat(dataFormat, numberType);

    // Vulkan guarantees substantially less per-workgroup capacity on physical
    // Z than on X/Y. Preserve the guest's logical axes while moving a tall Z
    // workload onto the host axis with the most guaranteed headroom.
    public static int[] ComputeWorkgroupAxisOrder(
        uint sizeX,
        uint sizeY,
        uint sizeZ)
    {
        // The shader compiler canonicalizes disabled/zero NUM_THREAD dimensions
        // to one. Keep every external consumer of this mapping on that exact
        // physical layout as well.
        sizeX = Math.Max(sizeX, 1u);
        sizeY = Math.Max(sizeY, 1u);
        sizeZ = Math.Max(sizeZ, 1u);
        if (sizeZ <= 64)
        {
            return [0, 1, 2];
        }

        var sizes = new[] { sizeX, sizeY, sizeZ };
        var byDescendingSize = new[] { 0, 1, 2 };
        Array.Sort(byDescendingSize, (left, right) =>
        {
            var sizeOrder = sizes[right].CompareTo(sizes[left]);
            return sizeOrder != 0 ? sizeOrder : left.CompareTo(right);
        });
        var physicalAxisOfLogical = new int[3];
        for (var physical = 0; physical < 3; physical++)
        {
            physicalAxisOfLogical[byDescendingSize[physical]] = physical;
        }

        return physicalAxisOfLogical;
    }

    private sealed partial class CompilationContext
    {
        private const int ScalarRegisterCount = 128;

        // M0. Used as the runtime index added to the register numbers encoded in
        // the V_MOVREL* instructions, and as the LDS/GDS base elsewhere.
        private const uint M0ScalarRegister = 124;

        private readonly SpirvModuleBuilder _module = new();
        private readonly Gen5SpirvStage _stage;
        private readonly ShaderMeshInfo? _mesh;
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        private readonly bool _usesPixelValidMask;
        private readonly bool _enableGraphicsSubgroupOperations;
        private readonly uint _waveLaneCount;
        private readonly bool _pairWave64;
        private readonly bool _emulateWave64;
        private readonly uint _guestWaveCount;
        private readonly bool _usesBarrierPhases;

        // The normal safety valve counts only taken backward/self edges. Blocks
        // are ordered by guest PC, so every dispatcher cycle contains at least
        // one such edge while arbitrarily long acyclic paths remain untouched.
        // Compute stays unbounded by default because its storage writes cannot
        // be rolled back if an invocation is stopped partway through.
        private const int DefaultGraphicsDispatcherBackedges = 32;
        private readonly int _maxDispatcherSteps;
        private readonly int _maxDispatcherBackedges;

        private static int ReadDispatcherLimit(string name, int defaultValue) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0
                ? value
                : defaultValue;

        // Diagnostic coverage probe. When enabled, every selected MRT export
        // writes opaque magenta while preserving the shader's control flow,
        // EXEC mask, geometry and raster state. This separates missing
        // rasterization from valid fragments whose translated values are zero.
        private static readonly bool _forcePixelMagenta =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_FORCE_PIXEL_MAGENTA"),
                "1",
                StringComparison.Ordinal);

        private bool PixelExportDebugHashMatches()
        {
            var hashFilter = Environment.GetEnvironmentVariable("SHARPEMU_FORCE_PIXEL_EXPORT_HASH");
            if (string.IsNullOrWhiteSpace(hashFilter))
            {
                return true;
            }

            var span = hashFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var hash) &&
                   _request.Hash == hash;
        }

        // The architectural packed VGPR is the source of truth for compressed
        // exports.  The nearby-conversion shadow is only a diagnostic aid: its
        // bounded backwards scan is not control-flow or alias aware and can
        // therefore reuse stale float values.  Keep it explicit opt-in.
        private static readonly bool _enablePackedHalfExportShadow =
            string.Equals(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_ENABLE_PACKED_EXPORT_SHADOW"),
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
        private readonly uint _hostLocalSizeX;
        private readonly uint _hostLocalSizeY;
        private readonly uint _hostLocalSizeZ;
        private readonly int[] _physicalAxisOfLogical;
        private readonly uint _pixelInputEnable;
        private readonly uint _pixelInputAddress;
        private readonly uint[] _pixelInputCntl;
        private readonly List<uint> _interfaces = [];
        private readonly Dictionary<uint, uint> _pixelInputs = [];
        private readonly Dictionary<uint, SpirvPixelOutput> _pixelOutputs = [];
        private readonly Dictionary<uint, uint> _vertexOutputs = [];
        private readonly Dictionary<uint, SpirvMeshOutput> _meshParameterOutputs = [];
        private readonly Dictionary<uint, SpirvVertexInput> _vertexInputsByPc = [];
        // Branch/loop-aware storage for fixed VGPR lanes whose READLANE uses
        // were proven by the scalar value graph on every incoming path.
        private readonly Dictionary<(uint Register, uint Lane), uint> _fixedLaneShadows = [];
        // Values written to a fixed VGPR lane within the current guest basic
        // block. A matching V_READLANE can consume the captured SSA value
        // directly instead of asking Vulkan to broadcast from a fragment lane
        // which may not exist in the host subgroup.
        private readonly Dictionary<(uint Register, uint Lane), uint> _fixedLaneValues = [];
        private bool _magentaProbeLogged;
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
        private uint _scalarArrayType;
        private uint _vectorArrayType;
        private uint _functionUintPointer;
        private uint _functionVec2Pointer;
        private bool _functionScopeState;
        // Some host drivers miscompile control flow when these long-lived
        // per-invocation flags are backed by Private bool variables. Keep the
        // storage as canonical uint 0/1 and convert at the Load/Store boundary.
        private readonly HashSet<uint> _flagVariables = [];
        private uint _runtimeBufferBiases;
        // One Private variable per register the program touches, declared on first use. A whole
        // register-file array is kept only for the VGPRs of a program that indexes them at run time
        // (V_MOVREL*): MoltenVK lowers a 512-entry private array to thread memory, and every
        // register access of every invocation then spills through it.
        private readonly Dictionary<uint, uint> _scalarRegisterVariables = new();
        private readonly Dictionary<uint, uint> _vectorRegisterVariables = new();
        private uint _scalarRegisters;
        private uint _vectorRegisters;
        private uint _vectorRegistersLow;
        private uint _vectorRegistersHigh;
        private (uint First, uint Last) _dynamicVectorRange;
        private uint _packedHalfRegisters;
        private uint _packedHalfRegistersLow;
        private uint _packedHalfRegistersHigh;
        private uint _scc;
        private uint _vcc;
        private uint _vccLow;
        private uint _vccHigh;
        private uint _exec;
        private uint _execLow;
        private uint _execHigh;
        private int _laneHalf;
        private bool _emittingPairedInstruction;
        private bool _hasPendingWaveMask;
        private uint _pendingWaveMaskRegister;
        private uint _pendingWaveMaskCondition;
        private uint _reachedPixelExport;
        private uint _pixelValidMaskActive;
        private uint _programCounter;
        private uint _programActive;
        private uint _programBarrierWait;
        private uint _barrierPhaseActiveWaves;
        private uint _barrierPhaseActiveWavePointer;
        private uint _dispatcherStepGuard;
        private uint _dispatcherBackedgeGuard;
        private uint _dispatcherGuardHit;
        private uint _iterationGuard;
        private readonly Dictionary<(uint Register, uint Lane), uint> _laneSpillSlots = [];
        private uint _globalBuffers;
        private uint _globalBuffers64;
        private uint _gfx10BufferFormatTable;
        private uint _storageBlockPointer;
        private uint _storageUlongBlockPointer;
        private uint _storageUintPointer;
        private uint _lds;
        private uint _ldsElementPointer;
        private uint _lds64ElementPointer;
        private uint _ldsDwordMask;
        private uint _ldsDwordCount;
        private uint _scratch;
        private uint _scratchLow;
        private uint _scratchHigh;
        private uint _scratchElementPointer;
        private uint _scratchDwordCount;
        private uint _scratchByteCount;
        private uint _positionOutput;
        private uint _pointSizeOutput;
        private uint _cullDistanceOutput;
        private uint _layerOutput;
        private uint _viewportIndexOutput;
        private uint _clipDistanceCount;
        private uint _cullDistanceCount;
        private SpirvMeshOutput _meshPositionOutput;
        private uint _meshAllocation;
        private uint _meshPrimitiveData;
        private uint _meshPrimitiveOutput;
        private uint _meshCullOutput;
        private uint _meshIndexScratch;
        private uint _vertexIndexInput;
        private uint _instanceIndexInput;
        private uint _fragCoordInput;
        private uint _localInvocationIdInput;
        private uint _localInvocationIndexInput;
        private uint _workGroupIdInput;
        private uint _pushConstantUintPointer;
        private uint _subgroupIdInput;
        private uint _subgroupInvocationIdInput;
        private uint _wave64Exchange;
        private uint _wave64ExchangeElementPointer;
        private uint _wave64ExchangeOffset;
        private uint _wave64ExchangeParity;
        private Ir.Gen5Wave64HalfMaskPlan? _halfMaskPlan;
        private bool _halfMaskPlanBuilt;
        private uint? _emittingPc;
        private uint _glsl;

        private enum ImageComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        private enum VertexInputComponentKind
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
            bool IsStorage,
            bool Arrayed,
            bool Cube = false,
            bool Multisampled = false,
            SpirvImageDim Dimension = SpirvImageDim.Dim2D,
            uint ConversionFormat = 0,
            uint ShaderSwizzle = 0,
            int EmulatedCompareFunction = -1);

        private readonly record struct SpirvVertexInput(
            uint Variable,
            uint Type,
            uint ComponentType,
            uint ComponentCount,
            VertexInputComponentKind ComponentKind,
            uint NumberFormat,
            uint DestinationSelect);

        private readonly record struct SpirvPixelOutput(
            uint Variable,
            uint Type,
            Gen5PixelOutputKind Kind,
            Gen5ColorComponentMapping ComponentMapping,
            byte TargetOutputMode);

        private readonly record struct SpirvMeshOutput(
            uint Variable,
            uint StagingVariable);

        public bool TryCompile(out Gen5SpirvShader shader, out string error)
        {
            shader = default!;
            error = string.Empty;
            try
            {
                if (Environment.GetEnvironmentVariable(
                        "SHARPEMU_TRACE_TITLE_INTERFACE") == "1" &&
                    _request.Program.Address is 0x0000000500780000ul or
                        0x0000000500781200ul)
                {
                    Console.Error.WriteLine(
                        $"[AGC][TITLE-INTERFACE] stage={_stage} " +
                        $"address=0x{_request.Program.Address:X16} " +
                        $"required_vertex_outputs={_requiredVertexOutputCount} " +
                        $"ps_ena=0x{_pixelInputEnable:X8} ps_addr=0x{_pixelInputAddress:X8}");
                    foreach (var instruction in _request.Program.Instructions)
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

                    }
                }

                var hasBarriers = _request.Program.Instructions.Any(
                    static instruction => instruction.Opcode == "SBarrier");
                var blocks = BuildBasicBlocks(
                    _request.Program.Instructions,
                    splitAfterBarrier: hasBarriers);
                // The fallback when the full structurer declines: blocks in
                // program order behind a next-block guard, with natural loops
                // emitted as structured loops.
                var structuredForward =
                    StructuredForwardBlocks &&
                    blocks.Count != 0 &&
                    TryBuildLoopRegions(blocks, out _loopLatchByHeader);
                _functionScopeState =
                    structuredForward &&
                    !hasBarriers &&
                    !_pairWave64 &&
                    !UsesWave64Exchange() &&
                    _request.FixedLaneReads.Count == 0 &&
                    !_request.Program.Instructions.Any(static instruction =>
                        IsIndirectControlFlow(instruction.Opcode) ||
                        instruction.Opcode is "SSubvectorLoopBegin" or "SSubvectorLoopEnd") &&
                    blocks.Count != 0;
                DeclareModule();
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }


                var functionType = _module.TypeFunction(_voidType);
                var main = _module.BeginFunction(_voidType, functionType);
                _module.AddName(main, "main");
                _module.AddLabel();
                if (_functionScopeState)
                {
                    DeclareRegisterFiles();
                }
                if (_stage == Gen5SpirvStage.Pixel &&
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_TITLE_EARLY_COLOR") == "1" &&
                    _request.Program.Address == 0x0000000500781200ul)
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

                if (TryGetAcyclicBarrierBlocks(blocks, out var barrierBlocks))
                {
                    if (!TryEmitForwardOnlyBlocks(
                            blocks,
                            barrierBlocks,
                            out error))
                    {
                        return false;
                    }
                }
                else if (!hasBarriers &&
                         StructuredControlFlow &&
                         TryStructureRange(blocks, 0, blocks.Count, blocks.Count, emit: false, out _))
                {
                    // As the dispatcher does before its first block: an
                    // out-of-bounds invocation runs nothing.
                    var runLabel = _module.AllocateId();
                    var doneLabel = _module.AllocateId();
                    var isActive = Load(_boolType, _programActive);
                    _module.AddStatement(SpirvOp.SelectionMerge, doneLabel, 0);
                    _module.AddStatement(SpirvOp.BranchConditional, isActive, runLabel, doneLabel);
                    _module.AddLabel(runLabel);
                    if (!TryStructureRange(blocks, 0, blocks.Count, blocks.Count, emit: true, out error))
                    {
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Branch, doneLabel);
                    _module.AddLabel(doneLabel);
                }
                else
                {
                if (hasBarriers && !_usesBarrierPhases)
                {
                    // Keep the legacy dispatcher's exact block/step shape for
                    // rejected topologies. Barrier splitting is needed only by
                    // the static phase plan (and by the existing wave64 phase
                    // dispatcher, which already requested it).
                    blocks = BuildBasicBlocks(_request.Program.Instructions);
                }
                var phaseHeader = _usesBarrierPhases
                    ? _module.AllocateId()
                    : 0;
                var phaseContinue = _usesBarrierPhases
                    ? _module.AllocateId()
                    : 0;
                var phaseMerge = _usesBarrierPhases
                    ? _module.AllocateId()
                    : 0;
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

                if (_usesBarrierPhases)
                {
                    _module.AddStatement(SpirvOp.Branch, phaseHeader);
                    _module.AddLabel(phaseHeader);
                    _module.AddStatement(
                        SpirvOp.LoopMerge,
                        phaseMerge,
                        phaseContinue,
                        0);
                }
                if (_functionScopeState)
                {
                    if (!TryEmitStructuredRange(
                            blocks,
                            0,
                            blocks.Count - 1,
                            -1,
                            out error))
                    {
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Branch, loopMerge);
                }
                else
                {
                _module.AddStatement(SpirvOp.Branch, loopHeader);
                _module.AddLabel(loopHeader);
                // Check before the first block can write from an inactive invocation.
                var programIsActive = Load(_boolType, _programActive);
                if (_usesBarrierPhases)
                {
                    programIsActive = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        programIsActive,
                        LogicalNot(Load(_boolType, _programBarrierWait)));
                }
                _module.AddStatement(SpirvOp.LoopMerge, loopMerge, loopContinue, 0);
                _module.AddStatement(SpirvOp.BranchConditional, programIsActive, switchHeader, loopMerge);

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
                    if (!TryEmitBlock(
                            blocks,
                            index,
                            false,
                            false,
                            out error))
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
                    // This legacy total-block limit is diagnostic-only. Do not
                    // charge an invocation that completed in the current block.
                    var currentSteps = Load(_uintType, _dispatcherStepGuard);
                    var nextSteps = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        active,
                        IAdd(currentSteps, UInt(1)),
                        currentSteps);
                    Store(_dispatcherStepGuard, nextSteps);
                    var limitReached = _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        nextSteps,
                        UInt((uint)_maxDispatcherSteps));
                    var exhausted = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        active,
                        limitReached);
                    Store(
                        _dispatcherGuardHit,
                        _module.AddInstruction(
                            SpirvOp.LogicalOr,
                            _boolType,
                            Load(_boolType, _dispatcherGuardHit),
                            exhausted));
                    active = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        active,
                        LogicalNot(exhausted));
                    Store(_programActive, active);
                }

                if (_usesBarrierPhases)
                {
                    active = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        active,
                        LogicalNot(Load(_boolType, _programBarrierWait)));
                }

                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    active,
                    loopHeader,
                    loopMerge);
                }

                _module.AddLabel(loopMerge);
                if (_usesBarrierPhases)
                {
                    var anyWaveActive = EmitBarrierPhaseRendezvous();
                    _module.AddStatement(SpirvOp.Branch, phaseContinue);
                    _module.AddLabel(phaseContinue);
                    Store(_programBarrierWait, _module.ConstantBool(false));
                    _module.AddStatement(
                        SpirvOp.BranchConditional,
                        anyWaveActive,
                        phaseHeader,
                        phaseMerge);
                    _module.AddLabel(phaseMerge);
                }
                }
                if (_stage == Gen5SpirvStage.Pixel &&
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_TRACE_TITLE_SHADER_STATE") == "1" &&
                    _request.Program.Address == 0x0000000500781200ul)
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
                    // EXP.VM publishes EXEC as the pixel-valid mask. EXEC may
                    // subsequently be restored for control-flow convergence,
                    // so the final EXEC value is not the fragment-validity
                    // decision. Programs without VM retain the old EXEC
                    // fallback so malformed shaders still fail conservatively.
                    var returnLabel = _module.AllocateId();
                    var killLabel = _module.AllocateId();
                    // Materialize the condition before SelectionMerge: SPIR-V
                    // requires the merge instruction to be immediately followed
                    // by its structured branch terminator.
                    var laneActive = Load(
                        _boolType,
                        _usesPixelValidMask ? _pixelValidMaskActive : _exec);
                    if (_dispatcherGuardHit != 0)
                    {
                        // A timed-out fragment may hold only partially computed
                        // exports. Discard it rather than presenting those values
                        // as a successful shader result.
                        laneActive = _module.AddInstruction(
                            SpirvOp.LogicalAnd,
                            _boolType,
                            laneActive,
                            LogicalNot(Load(_boolType, _dispatcherGuardHit)));
                    }
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

                if (_stage == Gen5SpirvStage.Mesh)
                {
                    EmitMeshEpilogue();
                }

                _module.AddStatement(SpirvOp.Return);
                _module.EndFunction();

                var model = _stage switch
                {
                    Gen5SpirvStage.Vertex => SpirvExecutionModel.Vertex,
                    Gen5SpirvStage.Mesh => SpirvExecutionModel.MeshEXT,
                    Gen5SpirvStage.Pixel => SpirvExecutionModel.Fragment,
                    _ => SpirvExecutionModel.GLCompute,
                };
                _module.AddEntryPoint(model, main, "main", _interfaces);
                if (_request.ShaderSignedZeroInfNanPreserveFloat32Supported)
                {
                    _module.AddExecutionMode(
                        main,
                        SpirvExecutionMode.SignedZeroInfNanPreserve,
                        32);
                }
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    _module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
                    if (_request.PixelEarlyDepth)
                    {
                        _module.AddExecutionMode(
                            main,
                            SpirvExecutionMode.EarlyFragmentTests);
                    }
                }
                else if (_stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh)
                {
                    var logicalSizes = new[] { _localSizeX, _localSizeY, _localSizeZ };
                    var physicalSizes = new uint[3];
                    for (var logical = 0; logical < 3; logical++)
                    {
                        physicalSizes[_physicalAxisOfLogical[logical]] = logicalSizes[logical];
                    }

                    _module.AddExecutionMode(
                        main,
                        SpirvExecutionMode.LocalSize,
                        _hostLocalSizeX,
                        _hostLocalSizeY,
                        _hostLocalSizeZ);
                    if (_stage == Gen5SpirvStage.Mesh)
                    {
                        _module.AddExecutionMode(
                            main,
                            SpirvExecutionMode.OutputVertices,
                            _mesh!.MaxVertices);
                        _module.AddExecutionMode(
                            main,
                            SpirvExecutionMode.OutputPrimitivesEXT,
                            _mesh.MaxPrimitives);
                        _module.AddExecutionMode(
                            main,
                            SpirvExecutionMode.OutputTrianglesEXT);
                    }
                }

                var attributeCount = _stage switch
                {
                    Gen5SpirvStage.Vertex => (uint)_vertexOutputs.Count,
                    Gen5SpirvStage.Mesh => (uint)_meshParameterOutputs.Count,
                    _ => (uint)_pixelInputs.Count,
                };
                shader = new Gen5SpirvShader(_module.Build(), attributeCount);
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
            if (_request.ShaderDeviceClockSupported &&
                _request.Program.Instructions.Any(static instruction =>
                    instruction.Opcode.Equals("SMemRealtime", StringComparison.OrdinalIgnoreCase)))
            {
                _module.AddExtension("SPV_KHR_shader_clock");
                _module.AddCapability(SpirvCapability.ShaderClockKhr);
            }
            if (_request.ShaderSignedZeroInfNanPreserveFloat32Supported)
            {
                _module.AddExtension("SPV_KHR_float_controls");
                _module.AddCapability(
                    SpirvCapability.SignedZeroInfNanPreserve);
            }
            if (_stage == Gen5SpirvStage.Mesh)
            {
                _module.AddCapability(SpirvCapability.MeshShadingEXT);
                _module.AddExtension("SPV_EXT_mesh_shader");
            }
            if (_stage == Gen5SpirvStage.Vertex)
            {
                _module.AddCapability(SpirvCapability.ClipDistance);
            }
            _module.AddCapability(SpirvCapability.Int64);
            if (_request.Program.Instructions.Any(static instruction =>
                    IsBufferInt64Atomic(instruction.Opcode)))
            {
                _module.AddCapability(SpirvCapability.Int64Atomics);
            }
            _module.AddCapability(SpirvCapability.ImageQuery);
            if (UsesSubgroupOperations())
            {
                _module.AddCapability(SpirvCapability.GroupNonUniform);
                _module.AddCapability(SpirvCapability.GroupNonUniformBallot);

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
            _privateVec2Pointer =
                _module.TypePointer(SpirvStorageClass.Private, _vec2Type);

            _scalarArrayType = _module.TypeArray(_uintType, ScalarRegisterCount);
            _vectorArrayType = _module.TypeArray(_uintType, VectorRegisterCount);
            var registerStorage = _functionScopeState
                ? SpirvStorageClass.Function
                : SpirvStorageClass.Private;
            _functionUintPointer = _module.TypePointer(registerStorage, _uintType);
            _functionVec2Pointer = _module.TypePointer(registerStorage, _vec2Type);

            if (_request.Program.Instructions.Any(static instruction =>
                    instruction.Opcode.StartsWith("VMovrel", StringComparison.Ordinal)))
            {
                // Resolve relative accesses over only the registers the program
                // can reach, avoiding a full private array on ordinary waves.
                _dynamicVectorRange = DynamicVectorRange(_request.Program);
            }

            if (!_functionScopeState)
            {
                uint Flag(bool initialValue)
                {
                    var variable = _module.AddGlobalVariable(
                        _privateUintPointer,
                        SpirvStorageClass.Private,
                        UInt(initialValue ? 1u : 0u));
                    _flagVariables.Add(variable);
                    return variable;
                }

                IEnumerable<FixedLaneReadBinding> fixedLaneBindings =
                    _request.FixedLaneWaveSize == _request.WaveSize
                        ? _request.FixedLaneReads.Values
                        : Array.Empty<FixedLaneReadBinding>();
                foreach (var binding in fixedLaneBindings
                             .Distinct()
                             .OrderBy(static binding => binding.Register)
                             .ThenBy(static binding => binding.Lane))
                {
                    var shadow = _module.AddGlobalVariable(
                        _privateUintPointer,
                        SpirvStorageClass.Private,
                        _module.Constant(_uintType, 0));
                    _fixedLaneShadows[(binding.Register, binding.Lane)] = shadow;
                    _interfaces.Add(shadow);
                    _module.AddName(shadow, $"fixedLane_v{binding.Register}_{binding.Lane}");
                }

                // Paired Wave64 needs an independently addressable VGPR bank
                // for each physical subgroup. Other shaders use upstream's
                // one-variable-per-register representation.
                if (_pairWave64)
                {
                    var privateVectorArrayPointer =
                        _module.TypePointer(SpirvStorageClass.Private, _vectorArrayType);
                    _vectorRegisters = _module.AddGlobalVariable(
                        privateVectorArrayPointer,
                        SpirvStorageClass.Private,
                        _module.ConstantNull(_vectorArrayType));
                    _vectorRegistersLow = _vectorRegisters;
                    _vectorRegistersHigh = _module.AddGlobalVariable(
                        privateVectorArrayPointer,
                        SpirvStorageClass.Private,
                        _module.ConstantNull(_vectorArrayType));
                }

                _scc = Flag(false);
                _vcc = Flag(false);
                _vccLow = _vcc;
                if (_pairWave64)
                {
                    _vccHigh = Flag(false);
                }

                _exec = Flag(true);
                _execLow = _exec;
                if (_pairWave64)
                {
                    _execHigh = Flag(true);
                }

                _reachedPixelExport = Flag(false);
                if (_usesPixelValidMask)
                {
                    _pixelValidMaskActive = Flag(true);
                }

                _programCounter = _module.AddGlobalVariable(
                    _privateUintPointer,
                    SpirvStorageClass.Private,
                    _module.Constant(_uintType, 0));
                _programActive = Flag(true);
                if (UsesWave64Exchange())
                {
                    _wave64ExchangeParity = _module.AddGlobalVariable(
                        _privateUintPointer,
                        SpirvStorageClass.Private,
                        UInt(0));
                    _interfaces.Add(_wave64ExchangeParity);
                    _module.AddName(_wave64ExchangeParity, "wave64ExchangeParity");
                }

                if (_usesBarrierPhases)
                {
                    _programBarrierWait = Flag(false);
                    var activeWaveArray = _module.TypeArray(_uintType, _guestWaveCount);
                    _barrierPhaseActiveWavePointer =
                        _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
                    _barrierPhaseActiveWaves = _module.AddGlobalVariable(
                        _module.TypePointer(SpirvStorageClass.Workgroup, activeWaveArray),
                        SpirvStorageClass.Workgroup);
                }

                if (_maxDispatcherSteps > 0)
                {
                    _dispatcherStepGuard = _module.AddGlobalVariable(
                        _privateUintPointer,
                        SpirvStorageClass.Private,
                        UInt(0));
                    _iterationGuard = _module.AddGlobalVariable(
                        _privateUintPointer,
                        SpirvStorageClass.Private,
                        UInt(0));
                    _interfaces.Add(_dispatcherStepGuard);
                    _interfaces.Add(_iterationGuard);
                    _module.AddName(
                        _dispatcherStepGuard,
                        $"dispatcherStepGuardLimit{_maxDispatcherSteps}");
                    _module.AddName(_iterationGuard, "pcGuard");
                }

                if (_maxDispatcherBackedges > 0)
                {
                    _dispatcherBackedgeGuard = _module.AddGlobalVariable(
                        _privateUintPointer,
                        SpirvStorageClass.Private,
                        UInt(0));
                    _interfaces.Add(_dispatcherBackedgeGuard);
                    _module.AddName(
                        _dispatcherBackedgeGuard,
                        $"dispatcherBackedgeGuardLimit{_maxDispatcherBackedges}");
                }

                if (_maxDispatcherSteps > 0 || _maxDispatcherBackedges > 0)
                {
                    _dispatcherGuardHit = Flag(false);
                    _interfaces.Add(_dispatcherGuardHit);
                    _module.AddName(_dispatcherGuardHit, "dispatcherGuardHit");
                }

                foreach (var slot in FindLaneSpillSlots())
                {
                    var variable = _module.AddGlobalVariable(
                        _privateUintPointer,
                        SpirvStorageClass.Private,
                        UInt(0));
                    _interfaces.Add(variable);
                    _module.AddName(variable, $"v{slot.Register}_lane{slot.Lane}");
                    _laneSpillSlots.Add(slot, variable);
                }

                if (_vectorRegisters != 0)
                {
                    _interfaces.Add(_vectorRegisters);
                    _module.AddName(_vectorRegisters, "vgpr");
                }

                _interfaces.Add(_scc);
                _interfaces.Add(_vcc);
                _interfaces.Add(_exec);
                if (_pairWave64)
                {
                    _interfaces.Add(_vectorRegistersHigh);
                    _interfaces.Add(_vccHigh);
                    _interfaces.Add(_execHigh);
                    _module.AddName(_vectorRegistersHigh, "vgprHigh");
                    _module.AddName(_vccHigh, "vccHigh");
                    _module.AddName(_execHigh, "execHigh");
                }

                _interfaces.Add(_reachedPixelExport);
                if (_pixelValidMaskActive != 0)
                {
                    _interfaces.Add(_pixelValidMaskActive);
                    _module.AddName(_pixelValidMaskActive, "pixelValidMaskActive");
                }

                _interfaces.Add(_programCounter);
                _interfaces.Add(_programActive);
                if (_usesBarrierPhases)
                {
                    _interfaces.Add(_programBarrierWait);
                    _interfaces.Add(_barrierPhaseActiveWaves);
                    _module.AddName(_programBarrierWait, "programBarrierWait");
                    _module.AddName(_barrierPhaseActiveWaves, "barrierPhaseActiveWaves");
                }
            }
            {
                DeclareLayoutBindings();
                DeclareScratch();
                DeclareLds();
                DeclareWave64Exchange();
                DeclareStageInterface();
                return;
            }
        }

        // Function-scope register storage lets structured shaders be promoted
        // by the driver without leaving large unused Private globals behind.
        private void DeclareRegisterFiles()
        {
            uint Variable(uint valueType, uint initializer) =>
                _module.AddFunctionVariable(
                    _module.TypePointer(SpirvStorageClass.Function, valueType),
                    initializer);

            uint Flag(bool initialValue)
            {
                var variable = Variable(_uintType, UInt(initialValue ? 1u : 0u));
                _flagVariables.Add(variable);
                return variable;
            }

            _scalarRegisters = Variable(
                _scalarArrayType,
                _module.ConstantNull(_scalarArrayType));
            _vectorRegisters = Variable(
                _vectorArrayType,
                _module.ConstantNull(_vectorArrayType));
            _vectorRegistersLow = _vectorRegisters;
            var packedArrayType = _module.TypeArray(_vec2Type, VectorRegisterCount);
            _packedHalfRegisters = Variable(
                packedArrayType,
                _module.ConstantNull(packedArrayType));
            _packedHalfRegistersLow = _packedHalfRegisters;

            _scc = Flag(false);
            _vcc = Flag(false);
            _vccLow = _vcc;
            _exec = Flag(true);
            _execLow = _exec;
            _reachedPixelExport = Flag(false);
            if (_usesPixelValidMask)
            {
                _pixelValidMaskActive = Flag(true);
                _module.AddName(_pixelValidMaskActive, "pixelValidMaskActive");
            }

            _programCounter = Variable(_uintType, UInt(0));
            _programActive = Flag(true);
            if (_maxDispatcherSteps > 0)
            {
                _iterationGuard = Variable(_uintType, UInt(0));
                _module.AddName(_iterationGuard, "pcGuard");
            }
            if (_maxDispatcherBackedges > 0)
            {
                // Structured shaders still pass each natural-loop latch through
                // EmitDispatcherBackedgeGuard. Keep its state in Function storage
                // alongside the other structured per-invocation state; otherwise
                // the first backward branch attempts to load SPIR-V pointer id 0.
                _dispatcherBackedgeGuard = Variable(_uintType, UInt(0));
                _module.AddName(
                    _dispatcherBackedgeGuard,
                    $"dispatcherBackedgeGuardLimit{_maxDispatcherBackedges}");
                _dispatcherGuardHit = Flag(false);
                _module.AddName(_dispatcherGuardHit, "dispatcherGuardHit");
            }

            _module.AddName(_scalarRegisters, "sgpr");
            _module.AddName(_vectorRegisters, "vgpr");
            _module.AddName(_packedHalfRegisters, "vgprPackedHalf");

            foreach (var slot in FindLaneSpillSlots())
            {
                var variable = Variable(_uintType, UInt(0));
                _module.AddName(variable, $"v{slot.Register}_lane{slot.Lane}");
                _laneSpillSlots.Add(slot, variable);
            }
        }

        private List<(uint Register, uint Lane)> FindLaneSpillSlots()
        {
            var slots = new List<(uint Register, uint Lane)>();
            if (_emulateWave64)
            {
                return slots;
            }

            var ownsLaneZero = !UsesSubgroupOperations();
            var readRegisters = _request.Program.Instructions
                .Where(static instruction =>
                    instruction.Opcode == "VReadlaneB32" &&
                    instruction.Sources.Count > 0 &&
                    instruction.Sources[0].Kind == Gen5OperandKind.VectorRegister)
                .Select(static instruction => instruction.Sources[0].Value)
                .ToHashSet();
            foreach (var instruction in _request.Program.Instructions)
            {
                if (instruction.Opcode == "VWritelaneB32" &&
                    TryGetVectorDestination(instruction, out var register) &&
                    readRegisters.Contains(register) &&
                    TryGetConstantLane(instruction, out var lane) &&
                    (lane != 0 || !ownsLaneZero) &&
                    !slots.Contains((register, lane)))
                {
                    slots.Add((register, lane));
                }
            }

            return slots;
        }

        private bool TryGetConstantLane(
            Gen5ShaderInstruction instruction,
            out uint lane)
        {
            lane = 0;
            if (instruction.Sources.Count < 2)
            {
                return false;
            }

            var operand = instruction.Sources[1];
            uint value;
            if (operand.Kind == Gen5OperandKind.LiteralConstant)
            {
                value = operand.Value;
            }
            else if (operand.Kind != Gen5OperandKind.EncodedConstant ||
                     !TryDecodeInlineConstant(operand.Value, out value))
            {
                return false;
            }

            lane = value & LaneSelectMask;
            return true;
        }

        private uint LaneSelectMask =>
            _stage == Gen5SpirvStage.Compute ? _waveLaneCount - 1 : 63u;

        private uint ReadLaneSpillSlot(
            Gen5ShaderInstruction instruction,
            uint value)
        {
            // A private spill is valid only at a read which the control-flow
            // analysis proved is reached by the matching fixed-lane write on
            // every path. A slot merely existing for some other read of this
            // VGPR is not provenance: an ordinary/multi-dword/image write, a
            // branch without the write, or a loop backedge can invalidate it.
            if (_request.FixedLaneWaveSize != _request.WaveSize ||
                !_request.FixedLaneReads.TryGetValue(instruction.Pc, out var provenLane) ||
                instruction.Sources[0].Kind != Gen5OperandKind.VectorRegister ||
                instruction.Sources[0].Value != provenLane.Register)
            {
                return value;
            }

            var register = instruction.Sources[0].Value;
            if (TryGetConstantLane(instruction, out var lane) &&
                lane == provenLane.Lane)
            {
                return _laneSpillSlots.TryGetValue((register, lane), out var slot)
                    ? Load(_uintType, slot)
                    : value;
            }

            return value;
        }

        private void DeclareWave64Exchange()
        {
            if (!UsesWave64Exchange())
            {
                return;
            }

            // Metal exposes 32 KiB of threadgroup memory on the Apple GPUs we
            // target. Some PS5 compute shaders legitimately request all of it,
            // so allocating another workgroup variable for the wave64 bridge
            // makes pipeline creation fail. Reuse the final dwords of the
            // existing LDS allocation in that case. The translator already
            // bounds guest LDS accesses to this fixed allocation; keeping the
            // bridge inside it preserves the host limit and still provides the
            // cross-subgroup rendezvous needed to model one 64-lane guest wave.
            if (_lds != 0)
            {
                _wave64Exchange = _lds;
                _wave64ExchangeElementPointer = _ldsElementPointer;
                _wave64ExchangeOffset = _ldsDwordMask + 1 < LdsDwordCount
                    ? _ldsDwordMask + 1
                    : LdsDwordCount - Wave64ExchangeDwordCount;
                return;
            }

            var exchangeArrayType =
                _module.TypeArray(_uintType, Wave64ExchangeDwordCount);
            _wave64ExchangeElementPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _wave64Exchange = _module.AddGlobalVariable(
                _module.TypePointer(
                    SpirvStorageClass.Workgroup,
                    exchangeArrayType),
                SpirvStorageClass.Workgroup);
            _wave64ExchangeOffset = 0;
            _module.AddName(_wave64Exchange, "wave64Exchange");
            _interfaces.Add(_wave64Exchange);
        }

        private bool UsesWave64Exchange() =>
            _emulateWave64 && UsesSubgroupOperations();
        private uint ComputeLdsGuestDwordCount()
        {
            var declared = _request.LocalDataShareDwords;
            return declared == 0 || declared > LdsDwordCount
                ? LdsDwordCount
                : System.Numerics.BitOperations.RoundUpToPowerOf2(declared);
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
            var workgroupStage =
                _stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh;
            var storageClass = workgroupStage
                ? SpirvStorageClass.Workgroup
                : SpirvStorageClass.Private;
            var requestedMeshDwords = Math.Max(_request.LocalDataShareDwords, 1u);
            var meshDwordCount = Math.Min(requestedMeshDwords, LdsDwordCount);
            var dwordCount = _stage switch
            {
                Gen5SpirvStage.Compute => UsesWave64Exchange()
                    ? ComputeLdsGuestDwordCount()
                    : LdsDwordCount,
                Gen5SpirvStage.Mesh => meshDwordCount,
                _ => PrivateLdsDwordCount,
            };
            _ldsDwordCount = dwordCount;
            _ldsDwordMask = dwordCount - 1;
            var arrayDwordCount =
                workgroupStage && UsesWave64Exchange() && dwordCount < LdsDwordCount
                    ? dwordCount + Wave64ExchangeDwordCount
                    : dwordCount;
            var ldsArrayType = _module.TypeArray(_uintType, arrayDwordCount);
            var ldsPointer = _module.TypePointer(storageClass, ldsArrayType);
            _ldsElementPointer = _module.TypePointer(storageClass, _uintType);
            _lds64ElementPointer = _module.TypePointer(storageClass, _ulongType);
            _lds = storageClass == SpirvStorageClass.Workgroup
                ? _module.AddGlobalVariable(ldsPointer, storageClass)
                : _module.AddGlobalVariable(
                    ldsPointer,
                    storageClass,
                    _module.ConstantNull(ldsArrayType));
            _module.AddName(_lds, "lds");
            _interfaces.Add(_lds);
        }

        internal static SpirvImageFormat DecodeStorageImageFormat(
            uint dataFormat,
            uint numberType) =>
            (dataFormat, numberType) switch
            {
                (1, 0 or 9) => SpirvImageFormat.R8,
                (1, 1) => SpirvImageFormat.R8Snorm,
                (1, 4) => SpirvImageFormat.R8ui,
                (1, 5) => SpirvImageFormat.R8i,
                (2, 0) => SpirvImageFormat.R16,
                (2, 1) => SpirvImageFormat.R16Snorm,
                (2, 4) => SpirvImageFormat.R16ui,
                (2, 5) => SpirvImageFormat.R16i,
                (2, 7) => SpirvImageFormat.R16f,
                (3, 0 or 9) => SpirvImageFormat.Rg8,
                (3, 1) => SpirvImageFormat.Rg8Snorm,
                (3, 4) => SpirvImageFormat.Rg8ui,
                (3, 5) => SpirvImageFormat.Rg8i,
                (4, 4) => SpirvImageFormat.R32ui,
                (4, 5) => SpirvImageFormat.R32i,
                (4, 7) => SpirvImageFormat.R32f,
                (5, 0) => SpirvImageFormat.Rg16,
                (5, 1) => SpirvImageFormat.Rg16Snorm,
                (5, 4) => SpirvImageFormat.Rg16ui,
                (5, 5) => SpirvImageFormat.Rg16i,
                (5, 7) => SpirvImageFormat.Rg16f,
                (6 or 7, 7) => SpirvImageFormat.R11fG11fB10f,
                (8 or 9, 0) => SpirvImageFormat.Rgb10A2,
                (8 or 9, 4) => SpirvImageFormat.Rgb10A2ui,
                (10, 0 or 9) => SpirvImageFormat.Rgba8,
                (10, 1) => SpirvImageFormat.Rgba8Snorm,
                (10, 4) => SpirvImageFormat.Rgba8ui,
                (10, 5) => SpirvImageFormat.Rgba8i,
                (11, 4) => SpirvImageFormat.Rg32ui,
                (11, 5) => SpirvImageFormat.Rg32i,
                (11, 7) => SpirvImageFormat.Rg32f,
                (12, 0) => SpirvImageFormat.Rgba16,
                (12, 1) => SpirvImageFormat.Rgba16Snorm,
                (12, 4) => SpirvImageFormat.Rgba16ui,
                (12, 5) => SpirvImageFormat.Rgba16i,
                (12, 7) => SpirvImageFormat.Rgba16f,
                (13 or 14, 4) => SpirvImageFormat.Rgba32ui,
                (13 or 14, 5) => SpirvImageFormat.Rgba32i,
                (13 or 14, 7) => SpirvImageFormat.Rgba32f,
                _ => SpirvImageFormat.Unknown,
            };

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
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    // Vulkan requires integer fragment inputs, including subgroup
                    // built-ins, to use flat interpolation.
                    _module.AddDecoration(
                        _subgroupInvocationIdInput,
                        SpirvDecoration.Flat);
                }
                _interfaces.Add(_subgroupInvocationIdInput);

                if (_pairWave64 || _usesBarrierPhases)
                {
                    // Vulkan does not require a subgroup to contain consecutive
                    // LocalInvocationIndex values. Paired wave64 uses the
                    // explicit subgroup identity for guest-wave numbering;
                    // the barrier-phase dispatcher also needs it to publish
                    // one active flag per physical/guest wave.
                    _subgroupIdInput = _module.AddGlobalVariable(
                        subgroupPointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(
                        _subgroupIdInput,
                        SpirvDecoration.BuiltIn,
                        (uint)SpirvBuiltIn.SubgroupId);
                    _interfaces.Add(_subgroupIdInput);
                }

                // The compute path declares LocalInvocationIndex here; mesh
                // declares the same built-in with its other stage interfaces.
                // Vertex/pixel stages can use native subgroup operations but
                // cannot combine subgroup32 halves through a workgroup.
                if (_stage == Gen5SpirvStage.Compute && _waveLaneCount == 64)
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

                DeclareAuxPositionOutputs();

                var parameters = _request.Program.Instructions
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
            else if (_stage == Gen5SpirvStage.Mesh)
            {
                DeclareMeshInterface();
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var inputVec4Pointer =
                    _module.TypePointer(SpirvStorageClass.Input, _vec4Type);
                var attributes = _request.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5InterpolationControl>()
                    .Select(control => control.Attribute)
                    .Distinct()
                    .Order()
                    .ToArray();
                DeclareInterpolationParameters();
                // Several PS input slots may read one VS parameter (the guest compiler emits a
                // slot per use). Relocating a duplicate left it on a location the vertex program
                // never writes, so it read zero: Astro Bot's save-slot card normals decoded to NaN
                // and a machine's albedo came out black. Slots of one kind share one host input;
                // when a V_INTERP_MOV slot is among them, the others read the per-vertex values
                // (interpolated with the barycentrics, or vertex 0 for a flat slot).
                var shared = new Dictionary<uint, uint>();
                bool IsFlatSlot(uint attribute) =>
                    ((attribute < (uint)_pixelInputCntl.Length ? _pixelInputCntl[attribute] : 0u) & 0x400u) != 0 ||
                    _flatParameterAttributes.Contains(attribute);
                foreach (var group in attributes.GroupBy(attribute =>
                             (attribute < (uint)_pixelInputCntl.Length ? _pixelInputCntl[attribute] : attribute) & 0x1Fu))
                {
                    uint? perVertexOwner = null;
                    foreach (var attribute in group)
                    {
                        if (_perVertexAttributes.Contains(attribute))
                        {
                            perVertexOwner = attribute;
                            break;
                        }
                    }

                    var owners = new Dictionary<bool, uint>();
                    foreach (var attribute in group)
                    {
                        if (perVertexOwner is { } perVertex)
                        {
                            if (attribute != perVertex)
                            {
                                shared.Add(attribute, perVertex);
                                if (!_perVertexAttributes.Contains(attribute))
                                {
                                    _perVertexSourcedAttributes.Add(attribute, IsFlatSlot(attribute));
                                }
                            }

                            continue;
                        }

                        var flat = IsFlatSlot(attribute);
                        if (owners.TryGetValue(flat, out var owner))
                        {
                            shared.Add(attribute, owner);
                        }
                        else
                        {
                            owners.Add(flat, attribute);
                        }
                    }
                }

                if (_perVertexSourcedAttributes.ContainsValue(false))
                {
                    DeclarePerspectiveBarycentric();
                }

                attributes = attributes.Where(attribute => !shared.ContainsKey(attribute)).ToArray();
                var locations = Gen5PixelInputMapping.ResolveLocations(
                    _pixelInputCntl,
                    attributes);
                for (var index = 0; index < attributes.Length; index++)
                {
                    var attribute = attributes[index];
                    // V_INTERP_MOV reads one vertex of the primitive, so its attribute is a
                    // per-vertex array rather than an interpolated value.
                    var variable = _module.AddGlobalVariable(
                        _perVertexAttributes.Contains(attribute)
                            ? _module.TypePointer(SpirvStorageClass.Input, _module.TypeArray(_vec4Type, 3))
                            : inputVec4Pointer,
                        SpirvStorageClass.Input);
                    // VINTRP ATTR selects the PS input slot. SPI_PS_INPUT_CNTL
                    // maps that slot to a VS parameter export location.
                    _module.AddDecoration(
                        variable,
                        SpirvDecoration.Location,
                        locations[index]);
                    if (_perVertexAttributes.Contains(attribute))
                    {
                        _module.AddDecoration(variable, SpirvDecoration.PerVertexKhr);
                    }
                    else if (IsFlatInterpolationParameter(attribute) ||
                             _flatParameterAttributes.Contains(attribute))
                    {
                        _module.AddDecoration(variable, SpirvDecoration.Flat);
                    }
                    else if (((_pixelInputEnable & _pixelInputAddress) & 0x20u) != 0)
                    {
                        // SPI requests linear interpolation for parameter inputs.
                        // The qualifier belongs on the fragment input only; flat
                        // and explicit per-vertex inputs take precedence.
                        _module.AddDecoration(variable, SpirvDecoration.NoPerspective);
                    }

                    _pixelInputs.Add(attribute, variable);
                    _interfaces.Add(variable);
                }

                foreach (var (attribute, owner) in shared)
                {
                    _pixelInputs.Add(attribute, _pixelInputs[owner]);
                }

                _fragCoordInput = _module.AddGlobalVariable(
                    inputVec4Pointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _fragCoordInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.FragCoord);
                _interfaces.Add(_fragCoordInput);
                DeclarePixelSystemInputs();

                var declaredPixelOutputs =
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_TITLE_SINGLE_MRT") == "1" &&
                    _request.Program.Address == 0x0000000500781200ul
                        ? _pixelOutputBindings.Take(1)
                        : _pixelOutputBindings;
                foreach (var binding in declaredPixelOutputs)
                {
                    var outputKind = binding.TargetOutputMode == 7
                        ? Gen5PixelOutputKind.Uint
                        : binding.Kind;
                    var outputType = GetPixelOutputType(outputKind);
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
                        binding.ExportTarget,
                        new SpirvPixelOutput(
                            variable,
                            outputType,
                            outputKind,
                            binding.ComponentMapping,
                            binding.TargetOutputMode));
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
            foreach (var input in _request.VertexInputs)
            {
                var inputComponentCount = input.InputComponentCount;
                var componentKind = input.NumberFormat switch
                {
                    4 => VertexInputComponentKind.Uint,
                    5 => VertexInputComponentKind.Sint,
                    _ => VertexInputComponentKind.Float,
                };
                var componentType = componentKind switch
                {
                    VertexInputComponentKind.Uint => _uintType,
                    VertexInputComponentKind.Sint => _intType,
                    _ => _floatType,
                };
                var type = inputComponentCount switch
                {
                    1u => componentType,
                    >= 2u and <= 4u =>
                        _module.TypeVector(componentType, inputComponentCount),
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
                var vertexInput = new SpirvVertexInput(
                    variable,
                    type,
                    componentType,
                    input.ComponentCount,
                    componentKind,
                    input.NumberFormat,
                    input.DestinationSelect);
                _vertexInputsByPc.TryAdd(input.Pc, vertexInput);
                foreach (var aliasPc in input.AliasPcs ?? [])
                {
                    _vertexInputsByPc.TryAdd(aliasPc, vertexInput);
                }

                _interfaces.Add(variable);
            }
        }

        private void EmitInitialState()
        {
            {
                EmitLayoutInitialState();
            }

            Store(_scc, _module.ConstantBool(false));
            Store(_reachedPixelExport, _module.ConstantBool(false));
            if (_pixelValidMaskActive != 0)
            {
                Store(_pixelValidMaskActive, _module.ConstantBool(true));
            }
            if (_subgroupInvocationIdInput != 0 && _emulateWave64)
            {
                StoreS64(106, BothHalvesOfOwnBallot(_module.ConstantBool(false)));
                StoreS64(126, BothHalvesOfOwnBallot(_module.ConstantBool(true)));
            }
            else if (_subgroupInvocationIdInput != 0)
            {
                if (_pairWave64 || _emulateWave64)
                {
                    StoreS64(106, _module.Constant64(_ulongType, 0));
                    StoreS64(126, _module.Constant64(_ulongType, ulong.MaxValue));
                }
                else
                {
                    StoreWaveMask(106, _module.ConstantBool(false));
                    StoreWaveMask(126, _module.ConstantBool(true));
                }
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

            // Primitive (NGG) vertex programs rebuild EXEC from the merged wave info in s3:
            // vertex count in bits 0-7, primitive count in bits 8-15. Left at zero, EXEC
            // becomes all 64 lanes, and a waterfall loop then waits forever for lanes that
            // do not exist to clear their bits (Astro Bot intro_next froze the GPU this way).
            if (_stage == Gen5SpirvStage.Vertex && _request.UserDataBase > 3)
            {
                uint laneCount;
                if (_subgroupInvocationIdInput == 0)
                {
                    laneCount = UInt(1);
                }
                else
                {
                    var lanes = BooleanToWaveMask(_module.ConstantBool(true));
                    laneCount = IAdd(
                        _module.AddInstruction(SpirvOp.BitCount, _uintType, Narrow(lanes)),
                        _module.AddInstruction(SpirvOp.BitCount, _uintType, Narrow(ShiftRightLogical64(lanes, _module.Constant64(_ulongType, 32)))));
                }

                StoreS(3, BitwiseOr(laneCount, ShiftLeftLogical(laneCount, UInt(8))));
            }

            Store(_programCounter, UInt(0));
            Store(_programActive, _module.ConstantBool(true));
            if (_programBarrierWait != 0)
            {
                Store(_programBarrierWait, _module.ConstantBool(false));
            }
            if (_dispatcherStepGuard != 0)
            {
                Store(_dispatcherStepGuard, UInt(0));
            }
            if (_dispatcherBackedgeGuard != 0)
            {
                Store(_dispatcherBackedgeGuard, UInt(0));
            }
            if (_dispatcherGuardHit != 0)
            {
                Store(_dispatcherGuardHit, _module.ConstantBool(false));
            }

            if (_stage == Gen5SpirvStage.Vertex)
            {
                // The PS5 NGG vertex ABI seeds the current wave shape in s2:s3.
                // Simple fullscreen/UI shaders often ignore these registers, but
                // geometry programs use them to derive the active vertex and
                // primitive lanes. Leaving the zero-initialized values in place
                // can therefore preserve UI while silently producing no meshes.
                StoreS(2, UInt(_request.WaveSize << 12));
                StoreS(3, UInt((1u << 28) | _request.WaveSize));
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
                if (_pointSizeOutput != 0)
                {
                    Store(_pointSizeOutput, Float(0f));
                }
                if (_layerOutput != 0)
                {
                    Store(_layerOutput, UInt(0));
                }
                if (_viewportIndexOutput != 0)
                {
                    Store(_viewportIndexOutput, UInt(0));
                }
                InitializeDistanceOutput(_clipDistanceOutput, _clipDistanceCount);
                InitializeDistanceOutput(_cullDistanceOutput, _cullDistanceCount);
                InitializeZeroPositionClipGuard();
            }
            else if (_stage == Gen5SpirvStage.Mesh)
            {
                EmitMeshInitialState();
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var fragCoord = Load(_vec4Type, _fragCoordInput);
                EmitPixelInputState(fragCoord);
                foreach (var output in _pixelOutputs.Values)
                {
                    var zero = output.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => _module.Constant(_uintType, 0),
                        Gen5PixelOutputKind.Sint => _module.Constant(_intType, 0),
                        _ => _module.ConstantFloat(_floatType, 0f),
                    };
                    var one = output.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => _module.Constant(_uintType, 1),
                        Gen5PixelOutputKind.Sint => _module.Constant(_intType, 1),
                        _ => _module.ConstantFloat(_floatType, 1f),
                    };
                    Store(
                        output.Variable,
                        _module.ConstantComposite(output.Type, zero, zero, zero, one));
                }
            }
            else
            {
                var workGroupId = Load(_uvec3Type, _workGroupIdInput);
                var usesDispatchThreadLimits =
                    _request.Bindings.UsesDispatchThreadLimits ||
                    _request.ThreadCountX != ShaderCompileRequest.UnboundedThreadCount ||
                    _request.ThreadCountY != ShaderCompileRequest.UnboundedThreadCount ||
                    _request.ThreadCountZ != ShaderCompileRequest.UnboundedThreadCount;
                if (_pairWave64)
                {
                    SelectLaneHalf(0);
                    var lowInBounds = EmitComputeLaneInitialState(
                        workGroupId,
                        usesDispatchThreadLimits);
                    SelectLaneHalf(1);
                    var highInBounds = EmitComputeLaneInitialState(
                        workGroupId,
                        usesDispatchThreadLimits);
                    SelectLaneHalf(0);
                    StorePairedWaveMask(126, lowInBounds, highInBounds);
                }
                else
                {
                    var invocationInBounds = EmitComputeLaneInitialState(
                        workGroupId,
                        usesDispatchThreadLimits);
                    if (usesDispatchThreadLimits)
                    {
                        if (_emulateWave64)
                        {
                            // Every host invocation must remain in the dispatcher
                            // because later wave-mask operations rendezvous at
                            // workgroup scope. Model out-of-range guest lanes with
                            // EXEC instead of allowing them to leave early.
                            StoreWaveMask(126, invocationInBounds);
                        }
                        else
                        {
                            Store(_programActive, invocationInBounds);
                        }
                    }
                }

                if (_request.ComputeSystemRegisters is { } registers)
                {
                    StoreComputeSystemRegister(
                        registers.WorkGroupXRegister,
                        workGroupId,
                        (uint)_physicalAxisOfLogical[0]);
                    StoreComputeSystemRegister(
                        registers.WorkGroupYRegister,
                        workGroupId,
                        (uint)_physicalAxisOfLogical[1]);
                    StoreComputeSystemRegister(
                        registers.WorkGroupZRegister,
                        workGroupId,
                        (uint)_physicalAxisOfLogical[2]);
                    if (registers.ThreadGroupSizeRegister is { } sizeRegister)
                    {
                        StoreS(
                            sizeRegister,
                            UInt(checked(_localSizeX * _localSizeY * _localSizeZ)));
                    }
                }
            }
        }

        private uint EmitComputeLaneInitialState(
            uint workGroupId,
            bool usesDispatchThreadLimits)
        {
            var localId = _pairWave64
                ? 0u
                : Load(_uvec3Type, _localInvocationIdInput);
            var logicalIndex = _pairWave64
                ? GuestLocalInvocationIndex()
                : 0u;
            var invocationInBounds = _pairWave64
                ? _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    logicalIndex,
                    UInt(checked(_localSizeX * _localSizeY * _localSizeZ)))
                : _module.ConstantBool(true);
            uint divisor = 1;
            for (uint component = 0; component < 3; component++)
            {
                var physicalComponent = (uint)_physicalAxisOfLogical[component];
                var localSize = component switch
                {
                    0 => _localSizeX,
                    1 => _localSizeY,
                    _ => _localSizeZ,
                };
                var localComponent = _pairWave64
                    ? _module.AddInstruction(
                        SpirvOp.UMod,
                        _uintType,
                        divisor == 1
                            ? logicalIndex
                            : _module.AddInstruction(
                                SpirvOp.UDiv,
                                _uintType,
                                logicalIndex,
                                UInt(divisor)),
                        UInt(localSize))
                    : _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        localId,
                        physicalComponent);
                StoreV(component, localComponent, guardWithExec: false);
                var groupComponent = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    workGroupId,
                    physicalComponent);
                var globalComponent = IAdd(
                    _module.AddInstruction(
                        SpirvOp.IMul,
                        _uintType,
                        groupComponent,
                        UInt(localSize)),
                    localComponent);
                if (usesDispatchThreadLimits)
                {
                    var limit = ComputeThreadLimit(component);
                    var componentInBounds = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        globalComponent,
                        limit);
                    invocationInBounds = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        invocationInBounds,
                        componentInBounds);
                }

                divisor = checked(divisor * localSize);
            }

            return invocationInBounds;
        }

        private void EmitPixelInputState(uint fragCoord)
        {
            uint vgpr = 0;

            // Keep input registers in SPI_PS_INPUT_ADDR order, including slots
            // whose values are read through the interpolated input interface.
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
            // position inputs.
            EmitPixelSystemInput(12, _frontFacingInput == 0 ? 0 : LoadFrontFaceInput(), ref vgpr);
            EmitPixelSystemInput(13, _ancillaryLayerInput == 0 ? 0 : LoadAncillaryInput(), ref vgpr);
            EmitPixelSystemInput(14, _sampleMaskInput == 0 ? 0 : LoadSampleCoverageInput(), ref vgpr);
            EmitPixelSystemInput(15, (_pixelInputAddress & _pixelInputEnable & 0x8000u) == 0 ? 0 : LoadFixedPointPositionInput(fragCoord), ref vgpr);
        }

        private void AdvancePixelInput(int bit, uint dwordCount, ref uint vgpr)
        {
            if ((_pixelInputAddress & (1u << bit)) != 0)
            {
                // Shaders that interpolate by hand (P1/P2 on per-vertex attributes) read
                // the I/J barycentrics from these registers.
                if (_barycentricInputs.TryGetValue(bit, out var barycentricInput))
                {
                    var coordinates = LoadBarycentricCoordinates(bit, barycentricInput);
                    for (uint component = 0; component < 2; component++)
                    {
                        var coordinate = _module.AddInstruction(
                            SpirvOp.CompositeExtract, _floatType, coordinates, component + 1);
                        StoreV(vgpr + component, Bitcast(_uintType, coordinate), guardWithExec: false);
                    }
                }

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
                if (component == 3)
                {
                    value = _module.AddInstruction(SpirvOp.FDiv, _floatType, Float(1f), value);
                }

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

        private enum SharedMemoryPhase { None, Read, Write }

        private bool TryEmitForwardOnlyBlocks(
            IReadOnlyList<ShaderBlock> blocks,
            IReadOnlySet<int> barrierBlocks,
            out string error)
        {
            error = string.Empty;
            for (var index = 0; index < blocks.Count; index++)
            {
                var bodyLabel = _module.AllocateId();
                var mergeLabel = _module.AllocateId();
                var pcMatches = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    Load(_uintType, _programCounter),
                    UInt((uint)index));
                var shouldExecute = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    Load(_boolType, _programActive),
                    pcMatches);
                _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    shouldExecute,
                    bodyLabel,
                    mergeLabel);

                _module.AddLabel(bodyLabel);
                if (!TryEmitBlock(
                        blocks,
                        index,
                        true,
                        barrierBlocks.Contains(index),
                        out error))
                {
                    error = $"block=0x{blocks[index].StartPc:X}: {error}";
                    return false;
                }

                _module.AddStatement(SpirvOp.Branch, mergeLabel);
                _module.AddLabel(mergeLabel);
                if (barrierBlocks.Contains(index))
                {
                    // Every path in this acyclic phase has converged here.
                    // Keep the guest barrier outside the per-block guard so
                    // every host invocation reaches the workgroup rendezvous.
                    EmitWorkgroupBarrier();
                }
            }

            // A conditional exit stores the sentinel PC rather than clearing
            // active. The dispatcher would deactivate it through its default
            // case; do the same after every possible forward block was visited.
            Store(_programActive, _module.ConstantBool(false));
            return true;
        }

        private bool TryEmitBlock(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            bool acyclicPath,
            bool deferBarrier,
            out string error)
        {
            if (!TryEmitBlockBody(
                    blocks,
                    blockIndex,
                    acyclicPath,
                    deferBarrier,
                    out error))
            {
                return false;
            }

            return TryEmitBlockTerminator(blocks, blockIndex, out error);
        }

        // Every instruction of a block except its terminating branch or s_endpgm.
        private bool TryEmitBlockBody(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            out string error) =>
            TryEmitBlockBody(
                blocks,
                blockIndex,
                acyclicPath: false,
                deferBarrier: false,
                out error);

        private bool TryEmitBlockBody(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            bool acyclicPath,
            bool deferBarrier,
            out string error)
        {
            error = string.Empty;
            var block = blocks[blockIndex];
            _fixedLaneValues.Clear();
            var halfMaskPlan = HalfMaskPlan();
            // Keep LDS writes and later reads ordered across every invocation
            // that represents one guest wave. The fallback spans a workgroup,
            // and the half-mask plan places workgroup barriers only at dataflow-
            // proven hazards. If analysis must give up, retain the bounded
            // single-block/acyclic fallback. A paired wave is exactly one host
            // subgroup and may always rendezvous at each basic-block boundary.
            var synchronizeSharedMemory =
                _pairWave64 ||
                (_emulateWave64 && halfMaskPlan is null &&
                 (blocks.Count == 1 || acyclicPath));
            var sharedMemoryPhase = SharedMemoryPhase.None;
            for (var index = block.StartIndex; index < block.EndIndex; index++)
            {
                var instruction = _request.Program.Instructions[index];
                if (halfMaskPlan is not null)
                {
                    if (halfMaskPlan.ExactPairsBefore.TryGetValue(
                            instruction.Pc,
                            out var exactPairs))
                    {
                        EmitExactWaveMasks(exactPairs);
                    }

                    if (halfMaskPlan.SharedMemoryBarriersBefore.Contains(
                            instruction.Pc))
                    {
                        EmitWave64Barrier();
                    }
                }

                if (IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm")
                {
                    continue;
                }

                if (synchronizeSharedMemory)
                {
                    var nextPhase = instruction.Control is Gen5DataShareControl { Gds: false }
                        ? instruction.Opcode.StartsWith("DsRead", StringComparison.Ordinal) ? SharedMemoryPhase.Read
                        : instruction.Opcode.StartsWith("DsWrite", StringComparison.Ordinal) ? SharedMemoryPhase.Write : SharedMemoryPhase.None
                        : SharedMemoryPhase.None;
                    if (instruction.Opcode == "SBarrier") sharedMemoryPhase = SharedMemoryPhase.None;
                    if (nextPhase != SharedMemoryPhase.None)
                    {
                        if (sharedMemoryPhase != SharedMemoryPhase.None && sharedMemoryPhase != nextPhase) EmitWave64Barrier();
                        sharedMemoryPhase = nextPhase;
                    }
                }

                if (deferBarrier && instruction.Opcode == "SBarrier")
                {
                    continue;
                }

                PrepareFixedLaneValues(instruction);
                _execKnownFull = IsExecKnownFull(instruction.Pc);
                _emittingPc = instruction.Pc;
                var emitted = TryEmitProgramInstruction(instruction, out error);
                _emittingPc = null;
                _execKnownFull = false;
                if (!emitted)
                {
                    error = $"pc=0x{instruction.Pc:X} {instruction.Opcode}: {error}";
                    return false;
                }

                CapturePixelVgprs(instruction);
                CapturePixelVgprPoints(instruction);
                MarkPixelPath(instruction);
                CapturePixelExec(instruction);
            }

            if (synchronizeSharedMemory && sharedMemoryPhase != SharedMemoryPhase.None) EmitWave64Barrier();
            return true;
        }

        // Stores the next dispatcher block (or ends the program) for a block's terminator.
        private bool TryEmitBlockTerminator(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            out string error)
        {
            error = string.Empty;
            var block = blocks[blockIndex];
            var terminator = _request.Program.Instructions[block.EndIndex - 1];
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

                if (IsExitBranchTarget(_request.Program.Instructions, targetPc))
                {
                    Store(_programActive, _module.ConstantBool(false));
                    return true;
                }

                if (!TryFindBlock(blocks, targetPc, out var targetBlock))
                {
                    error = $"invalid scalar branch target pc=0x{terminator.Pc:X} target=0x{targetPc:X} blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                EmitDispatcherBackedgeGuard(
                    blockIndex,
                    targetBlock,
                    _module.ConstantBool(true));
                Store(_programCounter, UInt((uint)targetBlock));
                return true;
            }

            if (terminator.Opcode is "SSubvectorLoopBegin" or "SSubvectorLoopEnd")
            {
                var hasTarget = TryGetBranchTargetPc(terminator, out var targetPc);
                var targetBlock = -1;
                var hasTargetBlock = hasTarget && TryFindBlock(blocks, targetPc, out targetBlock);
                var targetExits = hasTarget && IsExitBranchTarget(_request.Program.Instructions, targetPc);
                if (!hasTarget || (!hasTargetBlock && !targetExits) ||
                    !TryEmitSubvectorLoopState(terminator, out var condition, out error))
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = $"invalid subvector loop branch pc=0x{terminator.Pc:X} " +
                            $"target={(hasTarget ? $"0x{targetPc:X}" : "invalid")}";
                    }
                    return false;
                }

                var takenBlock = targetExits ? uint.MaxValue : (uint)targetBlock;
                var selected = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    condition,
                    UInt(takenBlock),
                    UInt(fallthrough));
                if (!targetExits)
                {
                    EmitDispatcherBackedgeGuard(blockIndex, targetBlock, condition);
                }
                Store(_programCounter, selected);
                return true;
            }

            if (terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                var hasTarget = TryGetBranchTargetPc(terminator, out var targetPc);
                var targetBlock = -1;
                var hasTargetBlock = hasTarget && TryFindBlock(blocks, targetPc, out targetBlock);
                var targetExits = hasTarget && IsExitBranchTarget(_request.Program.Instructions, targetPc);
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
                if (!targetExits)
                {
                    EmitDispatcherBackedgeGuard(
                        blockIndex,
                        targetBlock,
                        condition);
                }
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

        private bool TryEmitProgramInstruction(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            if (_pairWave64 && instruction.Opcode == "VReadfirstlaneB32")
            {
                return TryEmitPairedReadFirstLane(instruction, out error);
            }

            if (!_pairWave64 || !IsLaneInstruction(instruction))
            {
                return TryEmitInstruction(instruction, out error);
            }

            error = string.Empty;
            _emittingPairedInstruction = true;
            _hasPendingWaveMask = false;
            try
            {
                SelectLaneHalf(0);
                if (!TryEmitInstruction(instruction, out error))
                {
                    return false;
                }

                SelectLaneHalf(1);
                if (!TryEmitInstruction(instruction, out error))
                {
                    return false;
                }

                if (_hasPendingWaveMask)
                {
                    error = "paired instruction left an unmatched wave-mask write";
                    return false;
                }

                return true;
            }
            finally
            {
                _emittingPairedInstruction = false;
                _hasPendingWaveMask = false;
                _pendingWaveMaskRegister = 0;
                _pendingWaveMaskCondition = 0;
                SelectLaneHalf(0);
            }
        }

        private static bool IsLaneInstruction(Gen5ShaderInstruction instruction) =>
            instruction.Control is
                Gen5ImageControl or
                Gen5GlobalMemoryControl or
                Gen5BufferMemoryControl or
                Gen5ExportControl or
                Gen5InterpolationControl or
                Gen5DataShareControl ||
            instruction.Encoding is
                Gen5ShaderEncoding.Mubuf or
                Gen5ShaderEncoding.Mtbuf or
                Gen5ShaderEncoding.Vop1 or
                Gen5ShaderEncoding.Vop2 or
                Gen5ShaderEncoding.Vopc or
                Gen5ShaderEncoding.Vop3 or
                Gen5ShaderEncoding.Vintrp or
                Gen5ShaderEncoding.Ds or
                Gen5ShaderEncoding.Flat or
                Gen5ShaderEncoding.Vop3p or
                Gen5ShaderEncoding.Mimg or
                Gen5ShaderEncoding.Exp;

        private bool TryEmitPairedReadFirstLane(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                instruction.Sources.Count == 0)
            {
                error = "invalid read-first-lane operands";
                return false;
            }

            var savedHalf = _laneHalf;
            uint lowValue;
            uint highValue;
            uint lowActive;
            uint highActive;
            try
            {
                SelectLaneHalf(0);
                lowValue = GetRawSource(instruction, 0);
                lowActive = Load(_boolType, _exec);
                SelectLaneHalf(1);
                highValue = GetRawSource(instruction, 0);
                highActive = Load(_boolType, _exec);
            }
            finally
            {
                SelectLaneHalf(savedHalf);
            }

            uint ActiveMask(uint active)
            {
                var ballot = _module.AddInstruction(
                    SpirvOp.GroupNonUniformBallot,
                    _uvec4Type,
                    UInt(3),
                    active);
                return _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    ballot,
                    0);
            }

            var lowMask = ActiveMask(lowActive);
            var highMask = ActiveMask(highActive);
            var useLowBank = IsNotZero(lowMask);
            var selectedMask = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                useLowBank,
                lowMask,
                highMask);
            var anyActive = IsNotZero(selectedMask);
            var firstActive = Ext(73, _uintType, selectedMask);
            var safeFirstActive = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                anyActive,
                firstActive,
                UInt(0));
            var selectedValue = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                useLowBank,
                lowValue,
                highValue);
            var broadcast = _module.AddInstruction(
                SpirvOp.GroupNonUniformBroadcast,
                _uintType,
                UInt(3),
                selectedValue,
                safeFirstActive);
            StoreS(instruction.Destinations[0].Value, broadcast);
            return true;
        }

        private void EmitDispatcherBackedgeGuard(
            int sourceBlock,
            int targetBlock,
            uint branchTaken)
        {
            if (_maxDispatcherBackedges <= 0 || targetBlock > sourceBlock)
            {
                return;
            }

            // BuildBasicBlocks orders blocks by guest PC. Charging only a taken
            // edge to the same or an earlier block catches every cycle without
            // imposing a limit on forward-only control flow.
            var current = Load(_uintType, _dispatcherBackedgeGuard);
            var next = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                branchTaken,
                IAdd(current, UInt(1)),
                current);
            Store(_dispatcherBackedgeGuard, next);

            var limitReached = _module.AddInstruction(
                SpirvOp.UGreaterThanEqual,
                _boolType,
                next,
                UInt((uint)_maxDispatcherBackedges));
            var exhausted = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                branchTaken,
                limitReached);
            Store(
                _dispatcherGuardHit,
                _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    Load(_boolType, _dispatcherGuardHit),
                    exhausted));
            Store(
                _programActive,
                _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    Load(_boolType, _programActive),
                    LogicalNot(exhausted)));
        }

        // Set SHARPEMU_STRUCTURED_FORWARD_BLOCKS=0 to retain the dispatcher
        // for diagnostics. Unsupported topology is rejected by analysis and
        // falls back to that dispatcher automatically.
        private static readonly bool StructuredForwardBlocks = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_FORWARD_BLOCKS"),
            "0",
            StringComparison.Ordinal);

        // Forward-only, properly nested control flow is emitted as structured selections instead of
        // the PC-dispatch loop: a loop around a switch keeps every register live across iterations,
        // blocks cannot be optimized together, and the Metal compiler spills. SHARPEMU_STRUCTURED_CF=0
        // keeps the dispatcher for every program.
        private static readonly bool StructuredControlFlow =
            Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_CF") != "0";

        private static bool IsStructuredCondition(string opcode) => opcode is
            "SCbranchScc0" or "SCbranchScc1" or "SCbranchVccz" or "SCbranchVccnz" or
            "SCbranchExecz" or "SCbranchExecnz" or
            "SCbranchCdbgsys" or "SCbranchCdbguser" or "SCbranchCdbgsysOrUser" or "SCbranchCdbgsysAndUser";

        // The block a branch continues at, or blocks.Count for a branch past the last instruction.
        private bool TryResolveBranchBlock(IReadOnlyList<ShaderBlock> blocks, Gen5ShaderInstruction branch, out int target)
        {
            target = -1;
            if (!TryGetBranchTargetPc(branch, out var targetPc))
            {
                return false;
            }

            if (IsExitBranchTarget(_request.Program.Instructions, targetPc))
            {
                target = blocks.Count;
                return true;
            }

            return TryFindBlock(blocks, targetPc, out target);
        }

        // Emits (or, without emit, only checks) blocks [begin, end). A branch from the range's last
        // block to leave falls through to the range end. Inside a loop body, latch is the block
        // whose backward branch the caller emits as the loop's continue, and a branch to end would
        // leave the loop, which a structured body cannot express.
        private bool TryStructureRange(
            IReadOnlyList<ShaderBlock> blocks,
            int begin,
            int end,
            int leave,
            bool emit,
            out string error,
            int latch = -1)
        {
            error = string.Empty;
            var instructions = _request.Program.Instructions;
            var index = begin;
            while (index < end)
            {
                // A loop body starts at its own header, which must not open the loop again.
                if (!(latch >= 0 && index == begin) && index != latch &&
                    TryFindLoopLatch(blocks, index, end, out var loopLatch))
                {
                    if (!TryStructureLoop(blocks, index, loopLatch, emit, out error))
                    {
                        return false;
                    }

                    index = loopLatch + 1;
                    continue;
                }

                if (emit && !TryEmitBlockBody(blocks, index, out error))
                {
                    error = $"block=0x{blocks[index].StartPc:X}: {error}";
                    return false;
                }

                if (index == latch)
                {
                    return true;
                }

                var terminator = instructions[blocks[index].EndIndex - 1];
                var next = index + 1;
                if (terminator.Opcode == "SEndpgm")
                {
                    if (next != blocks.Count)
                    {
                        error = "s_endpgm before the last block";
                        return false;
                    }

                    if (emit)
                    {
                        Store(_programActive, _module.ConstantBool(false));
                    }

                    index = next;
                    continue;
                }

                if (terminator.Opcode == "SBranch")
                {
                    if (!TryResolveBranchBlock(blocks, terminator, out var branchTarget) ||
                        !(branchTarget == next || (next == end && branchTarget == leave)))
                    {
                        error = $"unstructured s_branch at 0x{terminator.Pc:X}";
                        return false;
                    }

                    index = next;
                    continue;
                }

                // Subvector-loop branches update EXEC state as part of deciding
                // whether the branch is taken. The generic structurer cannot
                // represent that state transition, so retain the dispatcher path
                // which lowers both the branch and its architectural side effects.
                if (terminator.Opcode is "SSubvectorLoopBegin" or "SSubvectorLoopEnd")
                {
                    error = $"unsupported structured {terminator.Opcode} at 0x{terminator.Pc:X}";
                    return false;
                }

                if (!terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
                {
                    index = next;
                    continue;
                }

                if (!IsStructuredCondition(terminator.Opcode) ||
                    !TryResolveBranchBlock(blocks, terminator, out var target) || target <= index)
                {
                    error = $"unstructured {terminator.Opcode} at 0x{terminator.Pc:X}";
                    return false;
                }

                // if (taken) break; -- only out of the innermost loop, to the block after its latch.
                if (_structuredLoopExit >= 0 && target == _structuredLoopExit)
                {
                    if (emit)
                    {
                        TryGetBranchCondition(terminator.Opcode, out var leaveLoop);
                        var breakLabel = _module.AllocateId();
                        var stayLabel = _module.AllocateId();
                        _module.AddStatement(SpirvOp.SelectionMerge, stayLabel, 0);
                        _module.AddStatement(SpirvOp.BranchConditional, leaveLoop, breakLabel, stayLabel);
                        _module.AddLabel(breakLabel);
                        _module.AddStatement(SpirvOp.Branch, _structuredLoopMerge);
                        _module.AddLabel(stayLabel);
                    }

                    index = next;
                    continue;
                }

                if (target > end || (latch >= 0 && target == end))
                {
                    error = $"unstructured {terminator.Opcode} at 0x{terminator.Pc:X}";
                    return false;
                }

                if (target == next)
                {
                    index = next;
                    continue;
                }

                // if/else: the skipped range ends with an s_branch over the taken range.
                var thenLast = instructions[blocks[target - 1].EndIndex - 1];
                if (target < blocks.Count && target - 1 > index && thenLast.Opcode == "SBranch" &&
                    TryResolveBranchBlock(blocks, thenLast, out var joinTarget) && joinTarget > target && joinTarget <= end &&
                    !(latch >= 0 && joinTarget == end))
                {
                    if (!emit)
                    {
                        if (!TryStructureRange(blocks, next, target, joinTarget, emit: false, out error) ||
                            !TryStructureRange(blocks, target, joinTarget, joinTarget, emit: false, out error))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        TryGetBranchCondition(terminator.Opcode, out var taken);
                        var thenLabel = _module.AllocateId();
                        var elseLabel = _module.AllocateId();
                        var joinLabel = _module.AllocateId();
                        _module.AddStatement(SpirvOp.SelectionMerge, joinLabel, 0);
                        _module.AddStatement(SpirvOp.BranchConditional, taken, elseLabel, thenLabel);
                        _module.AddLabel(thenLabel);
                        if (!TryStructureRange(blocks, next, target, joinTarget, emit: true, out error))
                        {
                            return false;
                        }

                        _module.AddStatement(SpirvOp.Branch, joinLabel);
                        _module.AddLabel(elseLabel);
                        if (!TryStructureRange(blocks, target, joinTarget, joinTarget, emit: true, out error))
                        {
                            return false;
                        }

                        _module.AddStatement(SpirvOp.Branch, joinLabel);
                        _module.AddLabel(joinLabel);
                    }

                    index = joinTarget;
                    continue;
                }

                // if (!taken) { skipped range }
                if (!emit)
                {
                    if (!TryStructureRange(blocks, next, target, target, emit: false, out error))
                    {
                        return false;
                    }
                }
                else
                {
                    TryGetBranchCondition(terminator.Opcode, out var taken);
                    var bodyLabel = _module.AllocateId();
                    var mergeLabel = _module.AllocateId();
                    _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
                    _module.AddStatement(SpirvOp.BranchConditional, taken, mergeLabel, bodyLabel);
                    _module.AddLabel(bodyLabel);
                    if (!TryStructureRange(blocks, next, target, target, emit: true, out error))
                    {
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Branch, mergeLabel);
                    _module.AddLabel(mergeLabel);
                }

                index = target;
            }

            return true;
        }

        // The innermost structured loop's exit block and merge label, for its breaks; -1 outside loops.
        private int _structuredLoopExit = -1;
        private uint _structuredLoopMerge;

        // The last block in [header, end) that branches back to header, unconditionally or on a
        // wave-uniform condition.
        private bool TryFindLoopLatch(IReadOnlyList<ShaderBlock> blocks, int header, int end, out int latch)
        {
            latch = -1;
            var instructions = _request.Program.Instructions;
            for (var index = end - 1; index >= header; index--)
            {
                var terminator = instructions[blocks[index].EndIndex - 1];
                if ((IsStructuredCondition(terminator.Opcode) || terminator.Opcode == "SBranch") &&
                    TryResolveBranchBlock(blocks, terminator, out var target) && target == header)
                {
                    latch = index;
                    return true;
                }
            }

            return false;
        }

        // do { blocks [header, latch] } while (latch condition), bounded by the same step guard as
        // the dispatcher so a guest loop that never ends cannot wedge the GPU queue.
        private bool TryStructureLoop(IReadOnlyList<ShaderBlock> blocks, int header, int latch, bool emit, out string error)
        {
            var outerExit = _structuredLoopExit;
            var outerMerge = _structuredLoopMerge;
            try
            {
                return TryStructureLoopCore(blocks, header, latch, emit, out error);
            }
            finally
            {
                _structuredLoopExit = outerExit;
                _structuredLoopMerge = outerMerge;
            }
        }

        private bool TryStructureLoopCore(IReadOnlyList<ShaderBlock> blocks, int header, int latch, bool emit, out string error)
        {
            _structuredLoopExit = latch + 1;
            if (!emit)
            {
                _structuredLoopMerge = 0;
                return TryStructureRange(blocks, header, latch + 1, latch + 1, emit: false, out error, latch);
            }

            var headerLabel = _module.AllocateId();
            var bodyLabel = _module.AllocateId();
            var continueLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _structuredLoopMerge = mergeLabel;
            _module.AddStatement(SpirvOp.Branch, headerLabel);
            _module.AddLabel(headerLabel);
            _module.AddStatement(SpirvOp.LoopMerge, mergeLabel, continueLabel, 0);
            _module.AddStatement(SpirvOp.Branch, bodyLabel);
            _module.AddLabel(bodyLabel);
            if (!TryStructureRange(blocks, header, latch + 1, latch + 1, emit: true, out error, latch))
            {
                return false;
            }

            _module.AddStatement(SpirvOp.Branch, continueLabel);
            _module.AddLabel(continueLabel);
            var terminator = _request.Program.Instructions[blocks[latch].EndIndex - 1];
            var again = _module.ConstantBool(true);
            if (terminator.Opcode != "SBranch")
            {
                TryGetBranchCondition(terminator.Opcode, out again);
            }

            if (_maxDispatcherSteps > 0)
            {
                var steps = IAdd(Load(_uintType, _iterationGuard), UInt(1));
                Store(_iterationGuard, steps);
                var withinLimit = _module.AddInstruction(SpirvOp.ULessThan, _boolType, steps, UInt((uint)_maxDispatcherSteps));
                again = _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, again, withinLimit);
            }

            EmitDispatcherBackedgeGuard(latch, header, again);
            if (_maxDispatcherBackedges > 0)
            {
                // EmitDispatcherBackedgeGuard marks the invocation inactive at
                // the limit. Feed that state into the structured continue edge
                // so the optimized loop preserves the dispatcher's safety valve.
                again = LogicalAnd(again, Load(_boolType, _programActive));
            }

            _module.AddStatement(SpirvOp.BranchConditional, again, headerLabel, mergeLabel);
            _module.AddLabel(mergeLabel);
            return true;
        }


        private Dictionary<int, int> _loopLatchByHeader = [];

        private bool TryBuildLoopRegions(
            IReadOnlyList<ShaderBlock> blocks,
            out Dictionary<int, int> latchByHeader)
        {
            latchByHeader = [];
            for (var index = 0; index < blocks.Count; index++)
            {
                var terminator = _request.Program.Instructions[blocks[index].EndIndex - 1];
                if (terminator.Opcode != "SBranch" &&
                    !terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryGetBranchTargetPc(terminator, out var targetPc))
                {
                    return false;
                }

                if (IsExitBranchTarget(_request.Program.Instructions, targetPc))
                {
                    continue;
                }

                if (!TryFindBlock(blocks, targetPc, out var target))
                {
                    return false;
                }

                if (target <= index)
                {
                    latchByHeader[target] = latchByHeader.TryGetValue(
                        target,
                        out var latch)
                        ? Math.Max(latch, index)
                        : index;
                }
            }

            foreach (var (outerHeader, outerLatch) in latchByHeader)
            {
                foreach (var (innerHeader, innerLatch) in latchByHeader)
                {
                    if (outerHeader < innerHeader &&
                        innerHeader <= outerLatch &&
                        innerLatch > outerLatch)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryEmitStructuredRange(
            IReadOnlyList<ShaderBlock> blocks,
            int first,
            int last,
            int enclosingHeader,
            out string error)
        {
            error = string.Empty;
            for (var index = first; index <= last;)
            {
                if (index != enclosingHeader &&
                    _loopLatchByHeader.TryGetValue(index, out var latch))
                {
                    if (!TryEmitStructuredLoop(blocks, index, latch, out error))
                    {
                        return false;
                    }

                    index = latch + 1;
                    continue;
                }

                var isNext = LogicalAnd(
                    Load(_boolType, _programActive),
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        Load(_uintType, _programCounter),
                        UInt((uint)index)));
                var blockBody = _module.AllocateId();
                var blockMerge = _module.AllocateId();
                _module.AddStatement(SpirvOp.SelectionMerge, blockMerge, 0);
                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    isNext,
                    blockBody,
                    blockMerge);
                _module.AddLabel(blockBody);
                if (!TryEmitBlock(blocks, index, false, false, out error))
                {
                    error = $"block=0x{blocks[index].StartPc:X}: {error}";
                    return false;
                }

                _module.AddStatement(SpirvOp.Branch, blockMerge);
                _module.AddLabel(blockMerge);
                index++;
            }

            return true;
        }

        private bool TryEmitStructuredLoop(
            IReadOnlyList<ShaderBlock> blocks,
            int header,
            int latch,
            out string error)
        {
            var singleBlock = header == latch;
            var entryMerge = singleBlock ? _module.AllocateId() : 0u;
            var skippedEntry = singleBlock ? _module.AllocateId() : 0u;
            var loopHeader = _module.AllocateId();
            var loopBody = _module.AllocateId();
            var loopContinue = _module.AllocateId();
            var loopMerge = _module.AllocateId();
            if (singleBlock)
            {
                var enters = LogicalAnd(
                    Load(_boolType, _programActive),
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        Load(_uintType, _programCounter),
                        UInt((uint)header)));
                _module.AddStatement(SpirvOp.SelectionMerge, entryMerge, 0);
                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    enters,
                    loopHeader,
                    skippedEntry);
            }
            else
            {
                _module.AddStatement(SpirvOp.Branch, loopHeader);
            }

            _module.AddLabel(loopHeader);
            _module.AddStatement(
                SpirvOp.LoopMerge,
                loopMerge,
                loopContinue,
                0);
            _module.AddStatement(SpirvOp.Branch, loopBody);
            _module.AddLabel(loopBody);
            if (!(singleBlock
                    ? TryEmitBlock(blocks, header, false, false, out error)
                    : TryEmitStructuredRange(blocks, header, latch, header, out error)))
            {
                return false;
            }

            _module.AddStatement(SpirvOp.Branch, loopContinue);
            _module.AddLabel(loopContinue);
            var again = LogicalAnd(
                Load(_boolType, _programActive),
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    Load(_uintType, _programCounter),
                    UInt((uint)header)));
            if (_maxDispatcherSteps > 0)
            {
                var steps = IAdd(Load(_uintType, _iterationGuard), UInt(1));
                Store(_iterationGuard, steps);
                again = LogicalAnd(
                    again,
                    _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        steps,
                        UInt((uint)_maxDispatcherSteps)));
            }

            _module.AddStatement(
                SpirvOp.BranchConditional,
                again,
                loopHeader,
                loopMerge);
            _module.AddLabel(loopMerge);
            if (singleBlock)
            {
                _module.AddStatement(SpirvOp.Branch, entryMerge);
                _module.AddLabel(skippedEntry);
                if (_maxDispatcherSteps > 0)
                {
                    Store(
                        _iterationGuard,
                        IAdd(Load(_uintType, _iterationGuard), UInt(1)));
                }
                _module.AddStatement(SpirvOp.Branch, entryMerge);
                _module.AddLabel(entryMerge);
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
                "SCbranchVccz" => LogicalNot(WaveMaskAny(106, _vcc)),
                "SCbranchVccnz" => WaveMaskAny(106, _vcc),
                "SCbranchExecz" => LogicalNot(WaveMaskAny(126, _exec)),
                "SCbranchExecnz" => WaveMaskAny(126, _exec),
                // The emulator does not expose a shader debug session.
                "SCbranchCdbgsys" or
                "SCbranchCdbguser" or
                "SCbranchCdbgsysOrUser" or
                "SCbranchCdbgsysAndUser" => _module.ConstantBool(false),
                _ => 0,
            };
            return condition != 0;
        }

        private uint WaveMaskAny(uint register, uint lanePredicate) =>
            _pairWave64 || _emulateWave64
                ? IsNotZero64(LoadS64(register))
                : SubgroupAny(Load(_boolType, lanePredicate));

        private bool TryEmitSubvectorLoopState(
            Gen5ShaderInstruction instruction,
            out uint branchCondition,
            out string error)
        {
            branchCondition = 0;
            error = string.Empty;
            if (instruction.Destinations.Count != 1 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = $"{instruction.Opcode} requires one scalar destination";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            var zero = UInt(0);
            var low = LoadS(126);
            var high = LoadS(127);
            var saved = LoadS(destination);
            if (instruction.Opcode == "SSubvectorLoopBegin")
            {
                var lowActive = IsNotZero(low);
                branchCondition = LogicalNot(IsNotZero(BitwiseOr(low, high)));
                StoreS(
                    destination,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        branchCondition,
                        saved,
                        _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            lowActive,
                            high,
                            low)));
                // Preserve the ISA assignment order when SDST aliases EXEC_HI.
                StoreS(
                    127,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        lowActive,
                        zero,
                        LoadS(127)));
                return true;
            }

            var highActive = IsNotZero(high);
            branchCondition = LogicalAnd(
                LogicalNot(highActive),
                IsNotZero(saved));
            StoreS(
                127,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    branchCondition,
                    saved,
                    high));
            StoreS(
                destination,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    branchCondition,
                    low,
                    LoadS(destination)));
            StoreS(
                126,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    highActive,
                    saved,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        branchCondition,
                        zero,
                        LoadS(126))));
            return true;
        }

        private bool TryEmitInstruction(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode.Equals("SMemRealtime", StringComparison.OrdinalIgnoreCase))
            {
                return TryEmitMemRealtime(instruction, out error);
            }

            // No shader trap handler is installed, so S_TRAP has no effect.
            if (instruction.Opcode == "STrap")
            {
                return true;
            }
            if (instruction.Opcode is
                "SNop" or
                "SWaitcnt" or
                "SInstPrefetch" or
                "STtraceData" or
                // Wave scheduling priority hint; no effect on results.
                "SSetprio")
            {
                return true;
            }

            if (instruction.Opcode == "SSendmsg")
            {
                return TryEmitSendMessage(instruction, out error);
            }

            if (instruction.Opcode == "SBarrier")
            {
                if (_usesBarrierPhases)
                {
                    // A guest workgroup barrier cannot be emitted inside the
                    // private-PC switch: separate guest waves may reach it in
                    // different dispatcher iterations (or through different
                    // static barrier sites). Park this wave at the end of its
                    // current phase; the uniform outer dispatcher performs the
                    // actual workgroup rendezvous after every wave has parked
                    // or terminated.
                    Store(_programBarrierWait, _module.ConstantBool(true));
                }
                else if (_stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh)
                {
                    // s_waitcnt vmcnt(0) + s_barrier also publishes buffer and image
                    // stores to the workgroup: AcquireRelease over uniform, workgroup
                    // and image memory.
                    var workgroup = UInt(2);
                    var semantics = UInt(0x8 | 0x40 | 0x100 | 0x800);
                    _module.AddStatement(
                        SpirvOp.ControlBarrier,
                        workgroup,
                        workgroup,
                        semantics);
                }
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

            if (instruction.Control is Gen5BvhRayControl bvhRay)
            {
                return TryEmitBvhMissFallback(bvhRay, out error);
            }

            if (instruction.Control is Gen5ImageControl image)
            {
                return TryEmitImage(instruction, image, out error);
            }

            if (instruction.Control is Gen5RayIntersectControl rayIntersect)
            {
                _deviceAddressInstructionPc = instruction.Pc;
                EmitRayIntersect(rayIntersect, instruction.Opcode == "ImageBvh64IntersectRay");
                return true;
            }

            if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                return TryEmitGlobalMemory(instruction, globalMemory, out error);
            }

            if (instruction.Control is Gen5ScratchMemoryControl scratchMemory)
            {
                return TryEmitScratchMemory(instruction, scratchMemory, out error);
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
            if (instruction.Control is Gen5DataShareControl { Gds: true } globalShare)
            {
                return TryEmitGlobalDataShare(instruction, globalShare, out error);
            }

            if (instruction.Control is not Gen5DataShareControl control)
            {
                error = "invalid LDS instruction";
                return false;
            }

            switch (instruction.Opcode)
            {
                case "DsSwizzleB32":
                    return TryEmitDataShareSwizzle(instruction, control, out error);
                case "DsBpermuteB32":
                    return TryEmitDataShareBpermute(instruction, control, out error);
            }

            if (_lds == 0 || _ldsElementPointer == 0)
            {
                error = "invalid LDS instruction";
                return false;
            }

            switch (instruction.Opcode)
            {
                case "DsAppend":
                case "DsConsume":
                    return TryEmitDataShareWaveCounter(instruction, control, out error);
                case "DsMskorB32":
                    return TryEmitDataShareAtomic(instruction, control, out error);
                case "DsWriteB32":
                case "DsWriteAddtidB32":
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing LDS write source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    if (instruction.Opcode == "DsWriteAddtidB32")
                    {
                        address = IAdd(BitwiseAnd(address, UInt(0xFFFF)), ShiftLeftLogical(GuestWaveLane(), UInt(2)));
                    }
                    StoreLds(
                        LdsPointer(address, control.SingleOffsetBytes),
                        GetRawSource(instruction, 1));
                    return true;
                }
                case "DsWriteB8":
                case "DsWriteB16":
                case "DsWriteB8D16Hi":
                case "DsWriteB16D16Hi":
                    return TryEmitDataShareSubwordWrite(instruction, control, out error);
                case "DsWriteB64":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS write64 source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = control.SingleOffsetBytes;
                    StoreLds(LdsPointer(address, offset), GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(address, offset + sizeof(uint)),
                        GetRawSource(instruction, 2));
                    return true;
                }
                case "DsAddU64":
                case "DsOrB64":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS 64-bit atomic source";
                        return false;
                    }

                    var atomicAddress = GetRawSource(instruction, 0);
                    var isOr = instruction.Opcode == "DsOrB64";
                    // The LDS allocation is a uint array. Reinterpreting its
                    // pointer as ulong with OpBitcast is invalid SPIR-V, so use
                    // the dword fallback even when native 64-bit atomics exist.
                    // The pair is not atomic against concurrent 64-bit use.
                    var lowPointer = LdsPointer(
                        atomicAddress,
                        control.SingleOffsetBytes);
                    var highPointer = LdsPointer(
                        atomicAddress,
                        control.SingleOffsetBytes + sizeof(uint));
                    EmitExecConditional(() =>
                    {
                        var lowValue = GetRawSource(instruction, 1);
                        var highValue = GetRawSource(instruction, 2);
                        if (isOr)
                        {
                            // OR is bitwise-independent: no dword influences the
                            // other, so two 32-bit ORs are the 64-bit OR.
                            EmitAtomic(
                                SpirvOp.AtomicOr,
                                _uintType,
                                lowPointer,
                                scope: 2,
                                semantics: 0x108,
                                value: () => lowValue,
                                comparator: () => lowValue);
                            EmitAtomic(
                                SpirvOp.AtomicOr,
                                _uintType,
                                highPointer,
                                scope: 2,
                                semantics: 0x108,
                                value: () => highValue,
                                comparator: () => highValue);
                            return;
                        }

                        // ADD needs the carry out of the low dword folded into
                        // the high one. Each lane detects its own wrap from the
                        // pre-add value its atomic returned and adds exactly one
                        // carry, so the high dword ends up with the true count of
                        // wraps however the lanes interleave.
                        var originalLow = EmitAtomic(
                            SpirvOp.AtomicIAdd,
                            _uintType,
                            lowPointer,
                            scope: 2,
                            semantics: 0x108,
                            value: () => lowValue,
                            comparator: () => lowValue);
                        var sumLow = IAdd(originalLow, lowValue);
                        var wrapped = _module.AddInstruction(
                            SpirvOp.ULessThan,
                            _boolType,
                            sumLow,
                            originalLow);
                        var carry = _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            wrapped,
                            UInt(1),
                            UInt(0));
                        var highWithCarry = IAdd(highValue, carry);
                        EmitAtomic(
                            SpirvOp.AtomicIAdd,
                            _uintType,
                            highPointer,
                            scope: 2,
                            semantics: 0x108,
                            value: () => highWithCarry,
                            comparator: () => highWithCarry);
                    });

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
                    var offset = control.SingleOffsetBytes;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        StoreLds(
                            LdsPointer(address, offset + (uint)(dword * sizeof(uint))),
                            GetRawSource(instruction, 1 + dword));
                    }

                    return true;
                }
                case "DsWrite2B64":
                case "DsWrite2St64B64":
                    return TryEmitDataShareWritePair64(instruction, control, out error);
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
                case "DsReadAddtidB32":
                {
                    if (instruction.Destinations.Count < 1 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    if (instruction.Opcode == "DsReadAddtidB32")
                    {
                        address = IAdd(BitwiseAnd(address, UInt(0xFFFF)), ShiftLeftLogical(GuestWaveLane(), UInt(2)));
                    }
                    var value = Load(
                        _uintType,
                        LdsPointer(address, control.SingleOffsetBytes));
                    StoreV(instruction.Destinations[0].Value, value);
                    return true;
                }
                case "DsReadI8":
                case "DsReadU8":
                case "DsReadI16":
                case "DsReadU16":
                case "DsReadU16D16":
                case "DsReadU16D16Hi":
                    return TryEmitDataShareSubwordRead(instruction, control, out error);
                case "DsReadB64":
                case "DsReadB96":
                case "DsReadB128":
                {
                    var dwordCount = instruction.Opcode switch { "DsReadB64" => 2, "DsReadB96" => 3, _ => 4 };
                    if (instruction.Destinations.Count < dwordCount ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = control.SingleOffsetBytes;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        var value = Load(
                            _uintType,
                            LdsPointer(address, offset + (uint)(dword * sizeof(uint))));
                        StoreV(instruction.Destinations[dword].Value, value);
                    }

                    return true;
                }
                case "DsRead2B64":
                case "DsRead2St64B64":
                    return TryEmitDataShareReadPair64(instruction, control, out error);
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

        private bool TryEmitDataShareWaveCounter(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
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
            var inBounds = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                UInt(offset + 3),
                sizeBytes);
            var pointer = LdsPointer(baseAddress, offset);
            var destination = instruction.Destinations[0].Value;
            var active = Load(_boolType, _exec);

            var activeMask = BooleanToWaveMask(active);
            var activeLow = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                activeMask);
            var activeHigh = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(
                    activeMask,
                    _module.Constant64(_ulongType, 32)));
            // OpBitCount needs a 32-bit operand without maintenance9.
            var activeCount = IAdd(
                _module.AddInstruction(SpirvOp.BitCount, _uintType, activeLow),
                _module.AddInstruction(SpirvOp.BitCount, _uintType, activeHigh));
            var firstLane = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                IsNotZero(activeLow),
                Ext(73, _uintType, activeLow),
                IAdd(Ext(73, _uintType, activeHigh), UInt(32)));
            var isFirstActive = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                inBounds,
                _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    active,
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        GuestWaveLane(),
                        firstLane)));

            EmitConditional(isFirstActive, () =>
            {
                uint original;
                if (_stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh)
                {
                    original = EmitAtomic(
                        instruction.Opcode == "DsAppend"
                            ? SpirvOp.AtomicIAdd
                            : SpirvOp.AtomicISub,
                        _uintType,
                        pointer,
                        scope: 2,
                        semantics: 0x108,
                        value: () => activeCount,
                        comparator: () => UInt(0));
                }
                else
                {
                    // Vulkan graphics stages cannot use Workgroup storage.
                    // Keep the counter in the first active lane's private LDS
                    // model, but preserve the RDNA2 wave operation: change it
                    // by the active-lane count and broadcast the old value.
                    original = Load(_uintType, pointer);
                    var changed = instruction.Opcode == "DsAppend"
                        ? IAdd(original, activeCount)
                        : _module.AddInstruction(
                            SpirvOp.ISub,
                            _uintType,
                            original,
                            activeCount);
                    Store(pointer, changed);
                }

                StoreV(destination, original);
            });

            var firstValue = LoadV(destination);
            uint broadcast;
            if (_emulateWave64)
            {
                broadcast = ExchangeWave64Value(isFirstActive, firstValue);
            }
            else
            {
                broadcast = ShuffleLane(firstValue, firstLane);
            }

            var validResult = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                inBounds,
                IsNotZero64(activeMask));
            StoreV(
                destination,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    validResult,
                    broadcast,
                    UInt(0)));
            return true;
        }

        private uint LdsPointer(uint address, uint offsetBytes)
        {
            var addressWithOffset = offsetBytes == 0
                ? address
                : IAdd(address, UInt(offsetBytes));
            // Keep arbitrary guest addresses inside the declared allocation.
            // Compute/private arrays are powers of two; mesh LDS uses the exact
            // guest size so it does not exceed VK_EXT_mesh_shader shared-memory
            // limits and therefore needs modulo for non-power-of-two sizes.
            var dwordIndex = ShiftRightLogical(addressWithOffset, UInt(2));
            var index = (_ldsDwordCount & (_ldsDwordCount - 1)) == 0
                ? BitwiseAnd(dwordIndex, UInt(_ldsDwordMask))
                : _module.AddInstruction(
                    SpirvOp.UMod,
                    _uintType,
                    dwordIndex,
                    UInt(_ldsDwordCount));
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _ldsElementPointer,
                _lds,
                index);
        }

        private bool TryEmitDataShareSubwordWrite(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 2)
            {
                error = $"missing {(control.Gds ? "GDS" : "LDS")} subword write operand";
                return false;
            }

            var bitCount = instruction.Opcode is "DsWriteB8" or "DsWriteB8D16Hi" ? 8u : 16u;
            var address = GetRawSource(instruction, 0);
            var byteAddress = control.SingleOffsetBytes == 0
                ? address
                : IAdd(address, UInt(control.SingleOffsetBytes));
            var byteInWord = BitwiseAnd(byteAddress, UInt(3));
            var shift = ShiftLeftLogical(byteInWord, UInt(3));
            var source = GetRawSource(instruction, 1);
            if (instruction.Opcode is "DsWriteB8D16Hi" or "DsWriteB16D16Hi")
            {
                // D16_HI selects the upper half of the data VGPR. B8 then takes
                // that half's low byte (original bits 23:16), matching RDNA SDWA
                // selectors BYTE_2 and WORD_1 used by compatible decoders.
                source = ShiftRightLogical(source, UInt(16));
            }

            var fieldMask = bitCount == 8 ? 0xFFu : 0xFFFFu;
            var mask = ShiftLeftLogical(UInt(fieldMask), shift);
            var inserted = ShiftLeftLogical(BitwiseAnd(source, UInt(fieldMask)), shift);
            var crossesDword = bitCount == 16
                ? _module.AddInstruction(SpirvOp.IEqual, _boolType, byteInWord, UInt(3))
                : 0u;
            var nextMask = UInt(0xFF);
            var nextInserted = bitCount == 16
                ? ShiftRightLogical(BitwiseAnd(source, UInt(fieldMask)), UInt(8))
                : 0u;

            uint Merge(uint observed, uint wordMask, uint wordValue) => BitwiseOr(
                    BitwiseAnd(observed, _module.AddInstruction(SpirvOp.Not, _uintType, wordMask)),
                    wordValue);
            void UpdateShared(uint pointer, uint wordMask, uint wordValue, uint scope) =>
                EmitAtomicWordUpdate(
                    pointer,
                    observed => Merge(observed, wordMask, wordValue),
                    scope: scope);
            void UpdatePrivate(uint pointer, uint wordMask, uint wordValue) =>
                Store(pointer, Merge(Load(_uintType, pointer), wordMask, wordValue));

            EmitExecConditional(() =>
            {
                if (control.Gds)
                {
                    var index = GlobalDataShareIndex(address, control.SingleOffsetBytes);
                    EmitConditional(
                        IsBlockWordInRange(_globalDataShare, index),
                        () => UpdateShared(BlockWordPointer(_globalDataShare, index), mask, inserted, 1));
                    if (bitCount == 16)
                    {
                        var nextIndex = IAdd(index, UInt(1));
                        EmitConditional(
                            crossesDword,
                            () => EmitConditional(
                                IsBlockWordInRange(_globalDataShare, nextIndex),
                                () => UpdateShared(
                                    BlockWordPointer(_globalDataShare, nextIndex),
                                    nextMask,
                                    nextInserted,
                                    1)));
                    }
                }
                else
                {
                    var pointer = LdsPointer(address, control.SingleOffsetBytes);
                    if (_stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh)
                    {
                        UpdateShared(pointer, mask, inserted, 2);
                        if (bitCount == 16)
                        {
                            EmitConditional(
                                crossesDword,
                                () => UpdateShared(
                                    LdsPointer(address, control.SingleOffsetBytes + sizeof(uint)),
                                    nextMask,
                                    nextInserted,
                                    2));
                        }
                    }
                    else
                    {
                        // Graphics LDS is represented as per-invocation Private
                        // storage, where SPIR-V atomics are invalid and no other
                        // invocation can race this read-modify-write.
                        UpdatePrivate(pointer, mask, inserted);
                        if (bitCount == 16)
                        {
                            EmitConditional(
                                crossesDword,
                                () => UpdatePrivate(
                                    LdsPointer(address, control.SingleOffsetBytes + sizeof(uint)),
                                    nextMask,
                                    nextInserted));
                        }
                    }
                }
            });
            return true;
        }

        private bool TryEmitDataShareSubwordRead(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count < 1 || instruction.Sources.Count < 1)
            {
                error = $"missing {(control.Gds ? "GDS" : "LDS")} subword read operand";
                return false;
            }

            var bitCount = instruction.Opcode is "DsReadI8" or "DsReadU8" ? 8u : 16u;
            var signed = instruction.Opcode is "DsReadI8" or "DsReadI16";
            var address = GetRawSource(instruction, 0);
            var byteAddress = control.SingleOffsetBytes == 0
                ? address
                : IAdd(address, UInt(control.SingleOffsetBytes));
            var word = control.Gds
                ? LoadBlockWord(
                    _globalDataShare,
                    GlobalDataShareIndex(address, control.SingleOffsetBytes))
                : Load(_uintType, LdsPointer(address, control.SingleOffsetBytes));
            var bitOffset = ShiftLeftLogical(BitwiseAnd(byteAddress, UInt(3)), UInt(3));
            if (bitCount == 16)
            {
                // In UNALIGNED mode a halfword at byte offset three spans two
                // LDS/GDS dwords. Assemble a low-aligned value before extracting
                // so OpBitField*Extract never receives an out-of-range 24+16 field.
                var nextWord = control.Gds
                    ? LoadBlockWord(
                        _globalDataShare,
                        GlobalDataShareIndex(
                            address,
                            control.SingleOffsetBytes + sizeof(uint)))
                    : Load(
                        _uintType,
                        LdsPointer(
                            address,
                            control.SingleOffsetBytes + sizeof(uint)));
                var crossesDword = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    BitwiseAnd(byteAddress, UInt(3)),
                    UInt(3));
                var low = ShiftRightLogical(word, bitOffset);
                var carryShift = _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    UInt(32),
                    bitOffset);
                var combined = BitwiseOr(low, ShiftLeftLogical(nextWord, carryShift));
                word = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    crossesDword,
                    combined,
                    low);
                bitOffset = UInt(0);
            }
            var extracted = _module.AddInstruction(
                signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract,
                signed ? _intType : _uintType,
                signed ? Bitcast(_intType, word) : word,
                bitOffset,
                UInt(bitCount));
            var value = signed ? Bitcast(_uintType, extracted) : extracted;

            var destination = instruction.Destinations[0].Value;
            if (instruction.Opcode is "DsReadU16D16" or "DsReadU16D16Hi")
            {
                // Native D16 reads update only one destination half.
                var old = LoadV(destination);
                value = instruction.Opcode == "DsReadU16D16Hi"
                    ? BitwiseOr(
                        BitwiseAnd(old, UInt(0x0000FFFF)),
                        ShiftLeftLogical(BitwiseAnd(value, UInt(0xFFFF)), UInt(16)))
                    : BitwiseOr(
                        BitwiseAnd(old, UInt(0xFFFF0000)),
                        BitwiseAnd(value, UInt(0xFFFF)));
            }

            StoreV(destination, value);
            return true;
        }

        private void StoreLds(uint pointer, uint value)
        {
            var active = Load(_boolType, _exec);
            // Inactive lanes must not read and write back another lane's shared value.
            EmitConditional(active, () => Store(pointer, value));
        }

        private bool TryEmitDataShareAtomic(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode == "DsMskorB32")
            {
                if (instruction.Sources.Count < 3)
                {
                    error = "missing LDS masked-OR source";
                    return false;
                }

                var maskedPointer = LdsPointer(
                    GetRawSource(instruction, 0),
                    control.SingleOffsetBytes);
                EmitExecConditional(() =>
                {
                    var original = Load(_uintType, maskedPointer);
                    var updated = BitwiseOr(
                        BitwiseAnd(
                            original,
                            _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                GetRawSource(instruction, 1))),
                        GetRawSource(instruction, 2));
                    EmitAtomic(
                        SpirvOp.AtomicCompareExchange,
                        _uintType,
                        maskedPointer,
                        scope: 2,
                        semantics: 0x108,
                        value: () => updated,
                        comparator: () => original);
                });
                return true;
            }

            if (instruction.Opcode is "DsMinF32" or "DsMaxF32")
            {
                if (instruction.Sources.Count < 3)
                {
                    error = $"missing LDS operands for {instruction.Opcode}";
                    return false;
                }

                var floatAddress = GetRawSource(instruction, 0);
                var floatPointer = LdsPointer(floatAddress, control.SingleOffsetBytes);
                EmitExecConditional(() =>
                    EmitDataShareFloatAtomic(
                        floatPointer,
                        GetRawSource(instruction, 1),
                        GetRawSource(instruction, 2),
                        instruction.Opcode == "DsMaxF32",
                        scope: 2,
                        semantics: 0x108));
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
                error = $"unsupported LDS opcode {instruction.Opcode}";
                return false;
            }

            var address = GetRawSource(instruction, 0);
            var pointer = LdsPointer(address, control.SingleOffsetBytes);
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

        private void EmitDataShareFloatAtomic(
            uint pointer,
            uint data,
            uint compare,
            bool maxValue,
            uint scope,
            uint semantics)
        {
            var preheader = _module.AllocateId();
            var header = _module.AllocateId();
            var continueLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var exchanged = _module.AllocateId();

            _module.AddStatement(SpirvOp.Branch, preheader);
            _module.AddLabel(preheader);
            // OpAtomicLoad cannot use Release or AcquireRelease semantics.
            var loadSemantics = (semantics & ~0xCu) | 0x2u;
            var initial = _module.AddInstruction(
                SpirvOp.AtomicLoad,
                _uintType,
                pointer,
                UInt(scope),
                UInt(loadSemantics));
            _module.AddStatement(SpirvOp.Branch, header);

            _module.AddLabel(header);
            var observed = _module.AddInstruction(
                SpirvOp.Phi,
                _uintType,
                initial,
                preheader,
                exchanged,
                continueLabel);
            var observedFloat = Bitcast(_floatType, observed);
            var compareFloat = Bitcast(_floatType, compare);
            var replace = _module.AddInstruction(
                maxValue ? SpirvOp.FOrdGreaterThan : SpirvOp.FOrdLessThan,
                _boolType,
                maxValue ? observedFloat : compareFloat,
                maxValue ? compareFloat : observedFloat);
            var next = _module.AddInstruction(SpirvOp.Select, _uintType, replace, data, observed);

            _module.AddStatement(
                SpirvOp.AtomicCompareExchange,
                _uintType,
                exchanged,
                pointer,
                UInt(scope),
                UInt(semantics),
                UInt(loadSemantics),
                next,
                observed);
            var success = _module.AddInstruction(SpirvOp.IEqual, _boolType, exchanged, observed);
            _module.AddStatement(SpirvOp.LoopMerge, mergeLabel, continueLabel, 0);
            _module.AddStatement(SpirvOp.BranchConditional, success, mergeLabel, continueLabel);
            _module.AddLabel(continueLabel);
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(mergeLabel);
        }

        // SPIR-V core has no floating-point min/max atomic. Preserve the raw
        // IEEE-754 bits and perform the ordered comparison in a uint CAS loop.
        private uint EmitBufferFloatAtomic(
            uint pointer,
            uint value,
            bool maxValue,
            uint scope,
            uint semantics)
        {
            var preheader = _module.AllocateId();
            var header = _module.AllocateId();
            var continueLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var exchanged = _module.AllocateId();

            _module.AddStatement(SpirvOp.Branch, preheader);
            _module.AddLabel(preheader);
            // OpAtomicLoad cannot use Release or AcquireRelease semantics.
            var loadSemantics = (semantics & ~0xCu) | 0x2u;
            var initial = _module.AddInstruction(
                SpirvOp.AtomicLoad,
                _uintType,
                pointer,
                UInt(scope),
                UInt(loadSemantics));
            _module.AddStatement(SpirvOp.Branch, header);

            _module.AddLabel(header);
            var observed = _module.AddInstruction(
                SpirvOp.Phi,
                _uintType,
                initial,
                preheader,
                exchanged,
                continueLabel);
            var observedFloat = Bitcast(_floatType, observed);
            var valueFloat = Bitcast(_floatType, value);
            var replace = _module.AddInstruction(
                maxValue ? SpirvOp.FOrdGreaterThan : SpirvOp.FOrdLessThan,
                _boolType,
                valueFloat,
                observedFloat);
            var next = _module.AddInstruction(SpirvOp.Select, _uintType, replace, value, observed);

            _module.AddStatement(
                SpirvOp.AtomicCompareExchange,
                _uintType,
                exchanged,
                pointer,
                UInt(scope),
                UInt(semantics),
                UInt(loadSemantics),
                next,
                observed);
            var success = _module.AddInstruction(SpirvOp.IEqual, _boolType, exchanged, observed);
            _module.AddStatement(SpirvOp.LoopMerge, mergeLabel, continueLabel, 0);
            _module.AddStatement(SpirvOp.BranchConditional, success, mergeLabel, continueLabel);
            _module.AddLabel(continueLabel);
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(mergeLabel);
            return observed;
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
                "Umax" or "UMax" => SpirvOp.AtomicUMax,
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

            if (_perVertexAttributes.Contains(interpolation.Attribute))
            {
                return TryEmitInterpolationParameter(instruction, interpolation, input, destination, out error);
            }

            if (_perVertexSourcedAttributes.TryGetValue(interpolation.Attribute, out var flatSource))
            {
                EmitPerVertexSourcedInterpolation(interpolation, input, destination, flatSource);
                return true;
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
            if (instruction.Opcode.Equals("SMemRealtime", StringComparison.OrdinalIgnoreCase))
            {
                return TryEmitMemRealtime(instruction, out error);
            }

            error = string.Empty;
            return TryEmitLayoutScalarMemory(instruction, control, out error);
        }

        private bool TryEmitMemRealtime(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count < 2 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                instruction.Destinations[1].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "S_MEMREALTIME requires a scalar-register pair destination";
                return false;
            }

            if (!_request.ShaderDeviceClockSupported)
            {
                // Retain the existing explicit fallback on devices that do not expose
                // VK_KHR_shader_clock. Supported devices take the real clock path below.
                if (System.Threading.Interlocked.Exchange(
                        ref _warnedMemRealtimeFallback,
                        1) == 0)
                {
                    Console.Error.WriteLine(
                        "Warning: S_MEMREALTIME uses UINT64_MAX because the Vulkan shader device clock is unavailable.");
                }

                StoreS(instruction.Destinations[0].Value, UInt(uint.MaxValue));
                StoreS(instruction.Destinations[1].Value, UInt(uint.MaxValue));
                return true;
            }

            var shift = _request.ShaderDeviceClockShift;
            if (shift > 31)
            {
                error = $"S_MEMREALTIME device-clock shift {shift} exceeds 31";
                return false;
            }

            // The extension permits a uvec2 result. Read both halves in one operation so
            // they cannot tear, then scale the host clock toward the guest's 100 MHz rate.
            var clock = _module.AddInstruction(
                SpirvOp.ReadClockKhr,
                _uvec2Type,
                UInt(1)); // SpvScopeDevice
            var low = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                clock,
                0);
            var high = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                clock,
                1);
            if (shift != 0)
            {
                var shiftedLow = ShiftRightLogical(low, UInt(shift));
                var carriedHigh = _module.AddInstruction(
                    SpirvOp.ShiftLeftLogical,
                    _uintType,
                    high,
                    UInt(32 - shift));
                low = BitwiseOr(shiftedLow, carriedHigh);
                high = ShiftRightLogical(high, UInt(shift));
            }

            StoreS(instruction.Destinations[0].Value, low);
            StoreS(instruction.Destinations[1].Value, high);
            return true;
        }

        private bool TryEmitGlobalMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            out string error)
        {
            error = string.Empty;
            {
                return TryEmitLayoutGlobalMemory(instruction, control, out error);
            }
        }

        private bool TryEmitBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode is "BufferWbinvl1" or "BufferWbinvl1Vol")
            {
                // The host storage path is already coherent; the guest instruction
                // invalidates a cache level and does not access the buffer itself.
                return true;
            }

            if (control.Typed && instruction.Opcode.Contains("D16", StringComparison.Ordinal))
            {
                error = $"unsupported buffer opcode {instruction.Opcode}";
                return false;
            }

            if (_stage == Gen5SpirvStage.Vertex &&
                _vertexInputsByPc.TryGetValue(instruction.Pc, out var vertexInput))
            {
                return TryEmitVertexInputFetch(control, vertexInput, out error);
            }

            if (_request.Memory.TryGetIndex(instruction.Pc, 0, out var planningMemoryIndex) &&
                _request.Memory[planningMemoryIndex].PlanningOnly)
            {
                if (control.DwordCount != 1 || instruction.Opcode != "BufferLoadDword" ||
                    control.IndexEnabled || control.OffsetEnabled || control.Typed || control.Glc || control.Slc ||
                    !_request.FlattenedSlotByMemoryIndex.TryGetValue(planningMemoryIndex, out var slot))
                {
                    error = "unsupported planning-only buffer read";
                    return false;
                }

                StoreV(control.VectorData, LoadFlattenedWord(UInt(slot)));
                return true;
            }

            if (_request.Memory.TryGetIndex(instruction.Pc, 0, out var deviceMemoryIndex) &&
                _request.Memory[deviceMemoryIndex].DeviceDescriptor)
            {
                return TryEmitDeviceDescriptorBufferMemory(instruction, control, out error);
            }

            if (_request.Memory.TryGetIndex(instruction.Pc, 0, out var accessMemoryIndex))
            {
                var accessMemory = _request.Memory[accessMemoryIndex];
                if (accessMemory.PlanningOnly)
                {
                    // The access belongs to the linearized tail of a fused
                    // shader.  Its descriptor is supplied by the continuation
                    // object and is intentionally absent from this plan; keep
                    // the dead linear instruction side-effect free.
                    return true;
                }

                var strategy = accessMemory.BufferDescriptor?.ChooseStrategy(
                        control.Typed,
                        accessMemory.Formatted,
                        accessMemory.Access == MemoryAccess.Atomic)
                    ?? BufferLoweringStrategy.NativeBinding;
                if (strategy == BufferLoweringStrategy.PhysicalStorageBuffer)
                {
                    return TryEmitPhysicalStorageBufferMemory(instruction, control, out error);
                }

                if (strategy == BufferLoweringStrategy.BoundedCandidateTable)
                {
                    return TryEmitBoundedCandidateTableMemory(instruction, control, accessMemoryIndex, out error);
                }
            }

            if (!TryResolveLayoutBuffer(instruction.Pc, out var bindingIndex, out _))
            {
                error = "missing buffer-memory binding";
                return false;
            }

            return EmitResolvedBufferMemory(instruction, control, bindingIndex, out error);
        }

        // One buffer operation against a resolved candidate binding: the dense binding path
        // and every arm of a bounded candidate table share it, so the operation itself
        // stays independent of how its descriptor was selected.
        private bool EmitResolvedBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            int bindingIndex,
            out string error)
        {
            error = string.Empty;
            var info = _request.Resources.Info;
            if (bindingIndex < 0 || bindingIndex >= info.Buffers.Count)
            {
                error = $"buffer binding {bindingIndex} is out of range";
                return false;
            }

            var specialized = info.Buffers[bindingIndex];
            var stride = UInt(specialized.PackedStride & 0x3FFF);
            var descriptorFormat = specialized.DescriptorFormat;
            var descriptorWord3 = UInt((specialized.DescriptorFormat << 12) | (specialized.DescriptorSwizzle & 0xFFF));

            var scalarOffset = instruction.Sources.Count > 2
                ? GetRawSource(instruction, 2)
                : UInt(0);
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
                var atomicSuffix = instruction.Opcode["BufferAtomic".Length..];
                if (atomicSuffix is "SwapX2" or "OrX2")
                {
                    var x2AtomicOp = atomicSuffix == "SwapX2"
                        ? SpirvOp.AtomicExchange
                        : SpirvOp.AtomicOr;
                    EmitExecConditional(() =>
                    {
                        // X2 atomics operate on one little-endian 64-bit value. Keep
                        // both source dwords alive before GLC reuses VDATA for the
                        // pre-operation result.
                        var value = Pair64(
                            LoadV(control.VectorData),
                            LoadV(control.VectorData + 1));
                        if (control.Glc)
                        {
                            StoreV(control.VectorData, UInt(0));
                            StoreV(control.VectorData + 1, UInt(0));
                        }

                        var aligned = _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            BitwiseAnd(byteAddress, UInt(7)),
                            UInt(0));
                        var qwordAddress = ShiftRightLogical(byteAddress, UInt(3));
                        var inRange = _module.AddInstruction(
                            SpirvOp.LogicalAnd,
                            _boolType,
                            aligned,
                            IsBufferUlongInRange(bindingIndex, qwordAddress));
                        EmitConditional(inRange, () =>
                        {
                            var original = EmitAtomic(
                                x2AtomicOp,
                                _ulongType,
                                BufferUlongPointer(
                                    bindingIndex,
                                    qwordAddress),
                                scope: 1,
                                semantics: 0x48,
                                value: () => value,
                                comparator: () => ULong(0));
                            if (control.Glc)
                            {
                                StoreV(control.VectorData, Narrow(original));
                                StoreV(
                                    control.VectorData + 1,
                                    Narrow(ShiftRightLogical64(original, ULong(32))));
                            }
                        });
                    });

                    return true;
                }

                if (atomicSuffix is "Fmin" or "Fmax")
                {
                    EmitExecConditional(() =>
                    {
                        // GLC replaces VDATA with the pre-operation value. Capture the
                        // input before initializing the out-of-range result to zero.
                        var value = LoadV(control.VectorData);
                        if (control.Glc)
                        {
                            StoreV(control.VectorData, UInt(0));
                        }

                        var inRange = IsBufferWordInRange(bindingIndex, dwordAddress);
                        EmitConditional(inRange, () =>
                        {
                            var original = EmitBufferFloatAtomic(
                                BufferWordPointer(bindingIndex, dwordAddress),
                                value,
                                maxValue: atomicSuffix == "Fmax",
                                scope: 1,
                                semantics: 0x48);
                            if (control.Glc)
                            {
                                StoreV(control.VectorData, original);
                            }
                        });
                    });

                    return true;
                }

                if (!TryGetAtomicOp(atomicSuffix, out var atomicOp))
                {
                    error = $"unsupported buffer opcode {instruction.Opcode}";
                    return false;
                }

                EmitExecConditional(() =>
                {
                    // Preserve atomic operands before GLC reuses VDATA for the return.
                    var value = LoadV(control.VectorData);
                    var comparator = atomicOp == SpirvOp.AtomicCompareExchange
                        ? LoadV(control.VectorData + 1)
                        : UInt(0);
                    if (control.Glc)
                    {
                        StoreV(control.VectorData, UInt(0));
                    }
                    var inRange = IsBufferWordInRange(bindingIndex, dwordAddress);
                    EmitConditional(inRange, () =>
                    {
                        var original = EmitAtomic(
                            atomicOp,
                            _uintType,
                            BufferWordPointer(bindingIndex, dwordAddress),
                            scope: 1,
                            semantics: 0x48,
                            value: () => value,
                            comparator: () => comparator);
                        if (control.Glc)
                        {
                            StoreV(control.VectorData, original);
                        }
                    });
                });

                return true;
            }

            if (instruction.Opcode is "BufferStoreFormatX" or "BufferStoreFormatXy" or
                "BufferStoreFormatXyz" or "BufferStoreFormatXyzw")
            {
                if (descriptorFormat == 0)
                {
                    return true;
                }

                if (!Gfx10UnifiedFormat.TryDecode(descriptorFormat, out _, out _))
                {
                    error = $"unsupported buffer store format {descriptorFormat}";
                    return false;
                }

                EmitExecConditional(() => TryEmitBufferFormatStore(
                    bindingIndex, byteAddress, control, descriptorWord3, descriptorFormat));
                return true;
            }

            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreFormat", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("TBufferStoreFormat", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreByte", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreShort", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    if (control.Typed && TryEmitBufferFormatStore(bindingIndex, byteAddress, control, descriptorWord3, control.TypedFormat))
                    {
                        return;
                    }

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

                    // BUFFER_STORE/LOAD_DWORD(x2/x3/x4) are dword-aligned by the GCN ISA, same as the GLOBAL case above — no per-byte reassembly needed.
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var indexedDwordAddress = index == 0
                            ? dwordAddress
                            : IAdd(dwordAddress, UInt(index));
                        StoreBufferWord(
                            bindingIndex,
                            indexedDwordAddress,
                            LoadV(control.VectorData + index));
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

            // A typed load converts with the instruction format, a formatted untyped load
            // with the descriptor format and swizzle; raw dword loads take the path below.
            if (IsFormatBufferLoad(instruction.Opcode))
            {
                if (!control.Typed)
                {
                    // An INVALID or otherwise unknown descriptor format makes
                    // BUFFER_LOAD_FORMAT* a raw dword load on the guest. This is
                    // distinct from a known format whose swizzle selects zero:
                    // preserve each transferred dword and let the raw path below
                    // perform the normal per-component bounds checks.
                    if (Gfx10UnifiedFormat.TryDecode(
                            descriptorFormat,
                            out var descriptorDataFormat,
                            out _) &&
                        Gfx10UnifiedFormat.ComponentCount(descriptorDataFormat) != 0)
                    {
                        EmitBufferFormatLoad(
                            bindingIndex,
                            byteAddress,
                            descriptorWord3,
                            control.VectorData,
                            control.DwordCount);
                        return true;
                    }
                }

                if (control.Typed &&
                    TryEmitTypedBufferFormatLoad(bindingIndex, byteAddress, control, descriptorWord3))
                {
                    return true;
                }
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var indexedDwordAddress = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt(index));
                StoreV(
                    control.VectorData + index,
                    LoadBufferWord(bindingIndex, indexedDwordAddress));
            }

            return true;
        }

        // BufferLoweringStrategy.BoundedCandidateTable: a runtime V# whose descriptors cannot
        // be reconstructed from its raw words. The runtime V# sits in the scalar resource
        // registers; its base-address dword is the probe key into the flattened candidate
        // mapping, and each arm binds a native candidate and runs the ordinary buffer op.
        private bool TryEmitBoundedCandidateTableMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            int memoryIndex,
            out string error)
        {
            error = string.Empty;
            if (!_request.BufferCandidateTableByMemoryIndex.TryGetValue(memoryIndex, out var table))
            {
                // A formatted load needs a statically known Vulkan view format.  Most
                // runtime V#s prove a bounded SRT candidate set above, but a few Yotei
                // material-lookup paths merge descriptors through control flow and have
                // no finite, safe candidate table.  Do not reinterpret an arbitrary
                // device address with a guessed format: a null read is the defined
                // fallback, matching the bindless-image fallback in ResourceTracker.
                if (instruction.Opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal))
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        StoreV(control.VectorData + index, UInt(0));
                    }

                    return true;
                }

                error = $"runtime buffer descriptor has no candidate table for {instruction.Opcode}";
                return false;
            }

            if (table.CandidateCount == 0)
            {
                error = "runtime buffer descriptor candidate table is empty";
                return false;
            }

            if (table.CandidateCount == 1)
            {
                return EmitResolvedBufferMemory(instruction, control, (int)table.FirstCandidate, out error);
            }

            if (!HasFlattenedTable)
            {
                error = "runtime buffer descriptor candidate table without a flattened table binding";
                return false;
            }

            if (instruction.Sources.Count < 2 || instruction.Sources[1].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "runtime buffer descriptor has no scalar resource base";
                return false;
            }

            var probeKey = LoadS(instruction.Sources[1].Value);
            var selector = SelectBufferCandidate(table, probeKey);
            var emitted = true;
            var caseError = string.Empty;
            for (uint index = 0; index < table.CandidateCount && emitted; index++)
            {
                var candidate = table.FirstCandidate + index;
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, selector, UInt(index)), () =>
                {
                    if (!EmitResolvedBufferMemory(instruction, control, (int)candidate, out caseError))
                    {
                        emitted = false;
                    }
                });
            }

            error = caseError;
            return emitted;
        }

        // Searches the sorted base-address mapping of a candidate table for the runtime V#'s
        // probe key; the result is the candidate-local index, candidate 0 when absent.
        private uint SelectBufferCandidate(BufferCandidateTableUse table, uint key)
        {
            var mapping = UInt(table.MappingOffset);
            var count = LoadFlattenedWord(mapping);
            var low = UInt(0);
            var high = count;
            for (uint iteration = 0; iteration < table.SearchIterations; iteration++)
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
            var mapped = LoadFlattenedWord(IAdd(foundSlot, UInt(1)));
            var inRange = LogicalAnd(
                found,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, mapped, UInt(table.CandidateCount)));
            return _module.AddInstruction(SpirvOp.Select, _uintType, inRange, mapped, UInt(0));
        }

        private bool TryEmitDeviceDescriptorBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (control.Typed || instruction.Opcode.StartsWith("TBuffer", StringComparison.Ordinal))
            {
                error = $"device buffer descriptor operation {instruction.Opcode} is not supported";
                return false;
            }

            _deviceAddressInstructionPc = instruction.Pc;
            var (baseAddress, size, stride, descriptorWord3) = LoadDeviceBufferDescriptor(control.ScalarResource);
            var scalarOffset = instruction.Sources.Count > 2
                ? GetRawSource(instruction, 2)
                : UInt(0);
            var vectorIndex = control.IndexEnabled
                ? LoadV(control.VectorAddress)
                : UInt(0);
            var vectorOffset = control.OffsetEnabled
                ? LoadV(control.VectorAddress + (control.IndexEnabled ? 1u : 0u))
                : UInt(0);
            var byteAddress = IAdd(UInt(unchecked((uint)control.OffsetBytes)), scalarOffset);
            byteAddress = IAdd(byteAddress, vectorOffset);
            byteAddress = IAdd(byteAddress, _module.AddInstruction(SpirvOp.IMul, _uintType, vectorIndex, stride));

            if (IsFormatBufferLoad(instruction.Opcode))
            {
                EmitDeviceBufferFormatLoad(
                    baseAddress,
                    size,
                    byteAddress,
                    descriptorWord3,
                    control.VectorData,
                    control.DwordCount);
                return true;
            }

            if (instruction.Opcode.Contains("Format", StringComparison.Ordinal))
            {
                error = $"device buffer descriptor operation {instruction.Opcode} is not supported";
                return false;
            }

            if (instruction.Opcode.StartsWith("BufferAtomic", StringComparison.Ordinal))
            {
                return TryEmitDeviceDescriptorBufferAtomic(instruction, control, baseAddress, size, byteAddress, out error);
            }

            if (TryGetSubdwordStoreInfo(instruction.Opcode, out var storeByteCount, out var sourceShift))
            {
                EmitExecConditional(() =>
                {
                    var inRange = IsDeviceBufferByteRangeInRange(size, byteAddress, storeByteCount);
                    var address = And64(IAdd64(baseAddress, Widen(byteAddress)), ULong(DeviceAddressMask));
                    EmitConditional(inRange, () =>
                        StoreDeviceBytes(address, LoadV(control.VectorData), storeByteCount, sourceShift, _module.ConstantBool(true)));
                });
                return true;
            }

            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var componentAddress = index == 0
                            ? byteAddress
                            : IAdd(byteAddress, UInt(index * sizeof(uint)));
                        StoreDeviceBufferWord(
                            baseAddress,
                            size,
                            componentAddress,
                            LoadV(control.VectorData + index));
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
                var inRange = IsDeviceBufferByteRangeInRange(size, byteAddress, loadByteCount);
                var address = And64(IAdd64(baseAddress, Widen(byteAddress)), ULong(DeviceAddressMask));
                Store(_deviceBufferWordScratch, UInt(0));
                EmitConditional(inRange, () => Store(
                    _deviceBufferWordScratch,
                    LoadSubdwordDeviceValue(
                        address,
                        LoadV(control.VectorData),
                        loadByteCount,
                        signExtend,
                        d16,
                        d16High)));
                StoreV(control.VectorData, Load(_uintType, _deviceBufferWordScratch));
                return true;
            }

            if (instruction.Opcode.StartsWith("BufferLoadDword", StringComparison.Ordinal))
            {
                for (uint index = 0; index < control.DwordCount; index++)
                {
                    var componentAddress = index == 0
                        ? byteAddress
                        : IAdd(byteAddress, UInt(index * sizeof(uint)));
                    StoreV(
                        control.VectorData + index,
                        LoadDeviceBufferWord(baseAddress, size, componentAddress));
                }

                return true;
            }

            error = $"unsupported device buffer descriptor opcode {instruction.Opcode}";
            return false;
        }

        private bool TryEmitDeviceDescriptorBufferAtomic(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            uint baseAddress,
            uint size,
            uint byteAddress,
            out string error)
        {
            error = string.Empty;
            var atomicSuffix = instruction.Opcode["BufferAtomic".Length..];
            if (atomicSuffix is "SwapX2" or "OrX2")
            {
                var atomicOp = atomicSuffix == "SwapX2" ? SpirvOp.AtomicExchange : SpirvOp.AtomicOr;
                EmitExecConditional(() =>
                {
                    var valid = IsDeviceBufferByteRangeInRange(size, byteAddress, 2 * sizeof(uint));
                    var firstAddress = And64(IAdd64(baseAddress, Widen(byteAddress)), ULong(DeviceAddressMask & ~3ul));
                    var secondAddress = And64(IAdd64(baseAddress, Widen(IAdd(byteAddress, UInt(sizeof(uint))))), ULong(DeviceAddressMask & ~3ul));
                    EmitConditional(valid, () =>
                    {
                        var (firstPointer, firstMapped) = ResolveDeviceAddress(firstAddress);
                        var (secondPointer, secondMapped) = ResolveDeviceAddress(secondAddress);
                        var bothMapped = LogicalAnd(firstMapped, secondMapped);
                        EmitConditional(bothMapped, () =>
                        {
                            var originalLow = EmitAtomic(
                                atomicOp, _uintType, DeviceWordPointer(firstPointer), 1, 0x48,
                                () => LoadV(control.VectorData), () => UInt(0));
                            var originalHigh = EmitAtomic(
                                atomicOp, _uintType, DeviceWordPointer(secondPointer), 1, 0x48,
                                () => LoadV(control.VectorData + 1), () => UInt(0));
                            if (control.Glc)
                            {
                                StoreV(control.VectorData, originalLow);
                                StoreV(control.VectorData + 1, originalHigh);
                            }
                        });
                    });
                });
                return true;
            }

            if (atomicSuffix is "Fmin" or "Fmax")
            {
                EmitExecConditional(() =>
                {
                    var valid = IsDeviceBufferByteRangeInRange(size, byteAddress, sizeof(uint));
                    var address = And64(IAdd64(baseAddress, Widen(byteAddress)), ULong(DeviceAddressMask & ~3ul));
                    EmitConditional(valid, () =>
                    {
                        var (pointer, mapped) = ResolveDeviceAddress(address);
                        EmitConditional(mapped, () =>
                        {
                            var original = EmitBufferFloatAtomic(
                                DeviceWordPointer(pointer),
                                LoadV(control.VectorData),
                                maxValue: atomicSuffix == "Fmax",
                                scope: 1,
                                semantics: 0x48);
                            if (control.Glc)
                                StoreV(control.VectorData, original);
                        });
                    });
                });
                return true;
            }

            if (!TryGetAtomicOp(atomicSuffix, out var atomicOperation))
            {
                error = $"unsupported buffer atomic opcode {instruction.Opcode}";
                return false;
            }

            EmitExecConditional(() =>
            {
                var valid = IsDeviceBufferByteRangeInRange(size, byteAddress, sizeof(uint));
                var address = And64(IAdd64(baseAddress, Widen(byteAddress)), ULong(DeviceAddressMask & ~3ul));
                EmitConditional(valid, () =>
                {
                    var (pointer, mapped) = ResolveDeviceAddress(address);
                    EmitConditional(mapped, () =>
                    {
                        var original = EmitAtomic(
                            atomicOperation,
                            _uintType,
                            DeviceWordPointer(pointer),
                            1,
                            0x48,
                            () => LoadV(control.VectorData),
                            () => LoadV(control.VectorData + 1));
                        if (control.Glc)
                            StoreV(control.VectorData, original);
                    });
                });
            });
            return true;
        }

        private void EmitDeviceBufferFormatLoad(
            uint baseAddress,
            uint size,
            uint byteAddress,
            uint descriptorWord3,
            uint vectorData,
            uint componentCount)
        {
            var unifiedFormat = BitwiseAnd(
                ShiftRightLogical(descriptorWord3, UInt(12)),
                UInt(0x7F));
            var (dataFormat, numberFormat) = DecodeGfx10BufferFormat(unifiedFormat);

            var canonical = new uint[4];
            var componentBounds = new uint[4];
            for (var component = 0; component < canonical.Length; component++)
            {
                canonical[component] = LoadGfx10DeviceBufferFormatComponent(
                    baseAddress,
                    size,
                    byteAddress,
                    dataFormat,
                    numberFormat,
                    component,
                    out componentBounds[component]);
            }

            var selectors = new uint[componentCount];
            var inBounds = _module.ConstantBool(true);
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = BitwiseAnd(
                    ShiftRightLogical(descriptorWord3, UInt(destination * 3)),
                    UInt(7));
                selectors[destination] = selector;
                var selectedInBounds = _module.ConstantBool(true);
                for (uint component = 0; component < 4; component++)
                {
                    selectedInBounds = _module.AddInstruction(
                        SpirvOp.Select,
                        _boolType,
                        _module.AddInstruction(SpirvOp.IEqual, _boolType, selector, UInt(component + 4)),
                        componentBounds[component],
                        selectedInBounds);
                }

                inBounds = _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, inBounds, selectedInBounds);
            }

            var one = Gfx10FormatOne(numberFormat);
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = selectors[destination];
                var constant = SelectUInt(selector, 1, one, UInt(0));
                var value = constant;
                value = SelectUInt(selector, 4, canonical[0], value);
                value = SelectUInt(selector, 5, canonical[1], value);
                value = SelectUInt(selector, 6, canonical[2], value);
                value = SelectUInt(selector, 7, canonical[3], value);
                StoreV(
                    vectorData + destination,
                    _module.AddInstruction(SpirvOp.Select, _uintType, inBounds, value, constant));
            }
        }

        private uint LoadGfx10DeviceBufferFormatComponent(
            uint baseAddress,
            uint size,
            uint elementAddress,
            uint dataFormat,
            uint numberFormat,
            int component,
            out uint componentInBounds)
        {
            var byteOffset = UInt(0);
            var bitOffset = UInt(0);
            var bitCount = UInt(0);

            void SetLayout(uint format, uint bytes, uint bits, uint count)
            {
                var matches = _module.AddInstruction(SpirvOp.IEqual, _boolType, dataFormat, UInt(format));
                byteOffset = _module.AddInstruction(SpirvOp.Select, _uintType, matches, UInt(bytes), byteOffset);
                bitOffset = _module.AddInstruction(SpirvOp.Select, _uintType, matches, UInt(bits), bitOffset);
                bitCount = _module.AddInstruction(SpirvOp.Select, _uintType, matches, UInt(count), bitCount);
            }

            foreach (var layout in Gfx10UnifiedFormat.ComponentLayouts)
            {
                if (layout.Component == (uint)component)
                    SetLayout(layout.DataFormat, layout.ByteOffset, layout.BitOffset, layout.BitCount);
            }

            var componentAddress = IAdd(elementAddress, byteOffset);
            var componentBytes = ShiftRightLogical(IAdd(IAdd(bitOffset, bitCount), UInt(7)), UInt(3));
            var lastByteOffset = _module.AddInstruction(SpirvOp.ISub, _uintType, componentBytes, UInt(1));
            var hasComponent = _module.AddInstruction(SpirvOp.INotEqual, _boolType, bitCount, UInt(0));
            var inRange = IsDeviceBufferElementInRange(size, componentAddress, lastByteOffset);
            var accessAllowed = LogicalAnd(hasComponent, inRange);
            componentInBounds = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                LogicalNot(hasComponent),
                inRange);
            var packed = UInt(0);
            for (uint index = 0; index < sizeof(uint); index++)
            {
                var address = index == 0 ? componentAddress : IAdd(componentAddress, UInt(index));
                var word = LoadDeviceBufferWord(baseAddress, size, address, accessAllowed);
                var shift = ShiftLeftLogical(BitwiseAnd(address, UInt(3)), UInt(3));
                var value = BitwiseAnd(ShiftRightLogical(word, shift), UInt(0xFF));
                packed = BitwiseOr(packed, ShiftLeftLogical(value, UInt(index * 8)));
            }

            var raw = _module.AddInstruction(SpirvOp.BitFieldUExtract, _uintType, packed, bitOffset, bitCount);
            var converted = ConvertGfx10BufferComponent(raw, bitCount, numberFormat, dataFormat);
            var valid = _module.AddInstruction(SpirvOp.INotEqual, _boolType, bitCount, UInt(0));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                valid,
                converted,
                component == 3 ? Gfx10FormatOne(numberFormat) : UInt(0));
        }

        private void EmitBufferFormatLoad(
            int bindingIndex,
            uint byteAddress,
            uint descriptorWord3,
            uint vectorData,
            uint componentCount)
        {
            var unifiedFormat = BitwiseAnd(
                ShiftRightLogical(descriptorWord3, UInt(12)),
                UInt(0x7F));
            var (dataFormat, numberFormat) = DecodeGfx10BufferFormat(unifiedFormat);

            var canonical = new uint[4];
            var componentBounds = new uint[4];
            for (var component = 0; component < canonical.Length; component++)
            {
                canonical[component] = LoadGfx10BufferFormatComponent(
                    bindingIndex,
                    byteAddress,
                    dataFormat,
                    numberFormat,
                    component,
                    out componentBounds[component]);
            }

            // Only selected memory components contribute to the shared bounds check.
            var selectors = new uint[componentCount];
            var inBounds = _module.ConstantBool(true);
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = BitwiseAnd(
                    ShiftRightLogical(descriptorWord3, UInt(destination * 3)), UInt(7));
                selectors[destination] = selector;
                var selectedInBounds = _module.ConstantBool(true);
                for (uint component = 0; component < 4; component++)
                {
                    selectedInBounds = _module.AddInstruction(
                        SpirvOp.Select, _boolType,
                        _module.AddInstruction(SpirvOp.IEqual, _boolType, selector, UInt(component + 4)),
                        componentBounds[component], selectedInBounds);
                }

                inBounds = _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, inBounds, selectedInBounds);
            }

            var one = Gfx10FormatOne(numberFormat);
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = selectors[destination];
                var constant = SelectUInt(selector, 1, one, UInt(0));
                var value = constant;
                value = SelectUInt(selector, 4, canonical[0], value);
                value = SelectUInt(selector, 5, canonical[1], value);
                value = SelectUInt(selector, 6, canonical[2], value);
                value = SelectUInt(selector, 7, canonical[3], value);
                StoreV(
                    vectorData + destination,
                    _module.AddInstruction(SpirvOp.Select, _uintType, inBounds, value, constant));
            }
        }

        // Check the first and last dwords of the required range together.
        private uint IsBufferElementInRange(int bindingIndex, uint byteAddress, uint lastByteOffset)
        {
            var first = IsBufferWordInRange(bindingIndex, ShiftRightLogical(byteAddress, UInt(2)));
            var last = IsBufferWordInRange(
                bindingIndex,
                ShiftRightLogical(IAdd(byteAddress, lastByteOffset), UInt(2)));
            return _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, first, last);
        }

        // Component i of a typed load comes from memory component i; components the
        // format does not have read as zero. An unbound descriptor reads as zero.
        private bool TryEmitTypedBufferFormatLoad(
            int bindingIndex,
            uint byteAddress,
            Gen5BufferMemoryControl control,
            uint descriptorWord3)
        {
            if (!Gfx10UnifiedFormat.TryDecode(control.TypedFormat, out var dataFormat, out var numberFormat))
            {
                return false;
            }

            var componentCount = Gfx10UnifiedFormat.ComponentCount(dataFormat);
            if (componentCount == 0)
            {
                return false;
            }

            // All transferred components must be bound and inside the binding.
            var valid = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                IsDescriptorBound(descriptorWord3),
                IsBufferElementInRange(
                    bindingIndex,
                    byteAddress,
                    UInt(Gfx10UnifiedFormat.GetAccessByteSize(dataFormat, control.DwordCount) - 1)));
            var dataFormatId = UInt(dataFormat);
            var numberFormatId = UInt(numberFormat);
            for (uint destination = 0; destination < control.DwordCount; destination++)
            {
                var value = destination < componentCount
                    ? LoadGfx10BufferFormatComponent(
                        bindingIndex,
                        byteAddress,
                        dataFormatId,
                        numberFormatId,
                        (int)destination,
                        out _)
                    : UInt(0);
                StoreV(
                    control.VectorData + destination,
                    _module.AddInstruction(SpirvOp.Select, _uintType, valid, value, UInt(0)));
            }

            return true;
        }

        // A formatted store converts each register with the selected number format and places
        // the bits at the component's offset; all transferred components are stored or dropped.
        private bool TryEmitBufferFormatStore(
            int bindingIndex,
            uint byteAddress,
            Gen5BufferMemoryControl control,
            uint descriptorWord3,
            uint unifiedFormat)
        {
            if (!Gfx10UnifiedFormat.TryDecode(unifiedFormat, out var dataFormat, out var numberFormat))
            {
                return false;
            }

            var componentCount = Math.Min(control.DwordCount, Gfx10UnifiedFormat.ComponentCount(dataFormat));
            if (componentCount == 0)
            {
                return false;
            }

            var elementBytes = Gfx10UnifiedFormat.GetAccessByteSize(dataFormat, componentCount);
            var allowed = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                IsDescriptorBound(descriptorWord3),
                IsBufferElementInRange(bindingIndex, byteAddress, UInt(elementBytes - 1)));
            EmitConditional(allowed, () =>
            {
                if (Gfx10UnifiedFormat.HasWholeDwordComponents(dataFormat))
                {
                    // Dword components are dword aligned and keep their register bits.
                    var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
                    for (uint component = 0; component < componentCount; component++)
                    {
                        Gfx10UnifiedFormat.TryGetComponentLayout(dataFormat, component, out var byteOffset, out _, out _);
                        StoreBufferWord(
                            bindingIndex,
                            byteOffset == 0 ? dwordAddress : IAdd(dwordAddress, UInt(byteOffset / 4)),
                            LoadV(control.VectorData + component));
                    }

                    return;
                }

                var element = new (uint Value, uint Mask)[(elementBytes + 3) / 4];
                for (var index = 0; index < element.Length; index++)
                {
                    element[index] = (UInt(0), 0);
                }

                for (uint component = 0; component < componentCount; component++)
                {
                    Gfx10UnifiedFormat.TryGetComponentLayout(dataFormat, component, out var byteOffset, out var bitOffset, out var bitCount);
                    var encoded = EncodeGfx10BufferComponent(
                        LoadV(control.VectorData + component),
                        bitCount,
                        numberFormat,
                        dataFormat);
                    var dword = (int)(byteOffset / 4);
                    var bit = ((byteOffset & 3) * 8) + bitOffset;
                    var (value, mask) = element[dword];
                    element[dword] = (
                        BitwiseOr(value, bit == 0 ? encoded : ShiftLeftLogical(encoded, UInt(bit))),
                        mask | (((1u << (int)bitCount) - 1) << (int)bit));
                }

                StoreBufferElementBits(bindingIndex, byteAddress, element);
            });
            return true;
        }

        // Converts one register to the bits its component stores, per the number format.
        private uint EncodeGfx10BufferComponent(uint value, uint bitCount, uint numberFormat, uint dataFormat)
        {
            if (bitCount == 32)
            {
                return value;
            }

            var mask = (1u << (int)bitCount) - 1;
            var signedMaximum = mask >> 1;
            var signedMinimum = -(int)signedMaximum - 1;
            switch (numberFormat)
            {
                case 0:
                    return RoundedFloatToUnsigned(value, 0f, 1f, mask);
                case 1:
                    return BitwiseAnd(RoundedFloatToSigned(value, -1f, 1f, signedMaximum), UInt(mask));
                case 2:
                    return RoundedFloatToUnsigned(value, 0f, mask, 1f);
                case 3:
                    return BitwiseAnd(RoundedFloatToSigned(value, signedMinimum, signedMaximum, 1f), UInt(mask));
                case 4:
                    return Ext(38, _uintType, value, UInt(mask));
                case 5:
                    return BitwiseAnd(
                        Bitcast(
                            _uintType,
                            Ext(
                                45,
                                _intType,
                                Bitcast(_intType, value),
                                _module.Constant(_intType, unchecked((uint)signedMinimum)),
                                _module.Constant(_intType, signedMaximum))),
                        UInt(mask));
                case 7:
                    if (dataFormat is 6 or 7)
                    {
                        return EncodeUnsignedMiniFloat(value, bitCount);
                    }

                    if (bitCount == 16)
                    {
                        var pair = _module.AddInstruction(
                            SpirvOp.CompositeConstruct,
                            _vec2Type,
                            Bitcast(_floatType, value),
                            Float(0f));
                        return BitwiseAnd(Ext(58, _uintType, pair), UInt(0xFFFF));
                    }

                    return BitwiseAnd(value, UInt(mask));
                default:
                    return BitwiseAnd(value, UInt(mask));
            }
        }

        // NaN stores as zero; the value is clamped, scaled and rounded to nearest even.
        private uint ClampedScaledFloat(uint value, float minimum, float maximum, float scale)
        {
            var input = Bitcast(_floatType, value);
            input = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                _module.AddInstruction(SpirvOp.IsNan, _boolType, input),
                Float(0f),
                input);
            var clamped = Ext(43, _floatType, input, Float(minimum), Float(maximum));
            if (scale != 1f)
            {
                clamped = _module.AddInstruction(SpirvOp.FMul, _floatType, clamped, Float(scale));
            }

            return Ext(2, _floatType, clamped);
        }

        private uint RoundedFloatToUnsigned(uint value, float minimum, float maximum, float scale) =>
            _module.AddInstruction(
                SpirvOp.ConvertFToU,
                _uintType,
                ClampedScaledFloat(value, minimum, maximum, scale));

        private uint RoundedFloatToSigned(uint value, float minimum, float maximum, float scale) =>
            Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.ConvertFToS,
                    _intType,
                    ClampedScaledFloat(value, minimum, maximum, scale)));

        // Encodes a float as an unsigned 10 or 11 bit mini-float, rounding to nearest even.
        private uint EncodeUnsignedMiniFloat(uint value, uint bitCount)
        {
            var mantissaBits = (int)bitCount - 5;
            var shift = 23 - mantissaBits;
            var mantissaMask = (1u << mantissaBits) - 1;
            var input = Bitcast(_floatType, value);
            var isNan = _module.AddInstruction(SpirvOp.IsNan, _boolType, input);
            var positive = _module.AddInstruction(SpirvOp.FOrdGreaterThan, _boolType, input, Float(0f));
            var exponent = BitwiseAnd(ShiftRightLogical(value, UInt(23)), UInt(0xFF));

            // Below 2^-14 the result is a denormal: the mantissa alone scales the value.
            var denormal = _module.AddInstruction(
                SpirvOp.ConvertFToU,
                _uintType,
                Ext(
                    2,
                    _floatType,
                    _module.AddInstruction(
                        SpirvOp.FMul,
                        _floatType,
                        input,
                        Float(1u << (14 + mantissaBits)))));

            // Rounding the dropped mantissa bits may carry into the exponent.
            var roundBias = IAdd(
                UInt((1u << (shift - 1)) - 1),
                BitwiseAnd(ShiftRightLogical(value, UInt((uint)shift)), UInt(1)));
            var rounded = IAdd(value, roundBias);
            var roundedExponent = BitwiseAnd(ShiftRightLogical(rounded, UInt(23)), UInt(0xFF));
            var normal = BitwiseOr(
                ShiftLeftLogical(
                    _module.AddInstruction(SpirvOp.ISub, _uintType, roundedExponent, UInt(112)),
                    UInt((uint)mantissaBits)),
                BitwiseAnd(ShiftRightLogical(rounded, UInt((uint)shift)), UInt(mantissaMask)));
            var overflow = _module.AddInstruction(
                SpirvOp.UGreaterThanEqual,
                _boolType,
                roundedExponent,
                UInt(143));
            var result = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                overflow,
                UInt(31u << mantissaBits),
                normal);
            result = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, exponent, UInt(113)),
                denormal,
                result);
            result = _module.AddInstruction(SpirvOp.Select, _uintType, positive, result, UInt(0));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                isNan,
                UInt((1u << (int)bitCount) - 1),
                result);
        }

        // A descriptor whose format field is INVALID is not bound.
        private uint IsDescriptorBound(uint descriptorWord3)
        {
            var descriptorFormat = BitwiseAnd(
                ShiftRightLogical(descriptorWord3, UInt(12)),
                UInt(0x7F));
            return _module.AddInstruction(SpirvOp.INotEqual, _boolType, descriptorFormat, UInt(0));
        }

        private (uint DataFormat, uint NumberFormat) DecodeGfx10BufferFormat(
            uint unifiedFormat)
        {
            // A specialized descriptor makes the format a translation-time constant.
            if (_module.TryGetConstantValue(unifiedFormat, out var knownFormat) && knownFormat < 128)
            {
                Gfx10UnifiedFormat.TryDecode(knownFormat, out var knownDataFormat, out var knownNumberFormat);
                return (UInt(knownDataFormat), UInt(knownNumberFormat));
            }

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
            int component,
            out uint componentInBounds)
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

            // The layout is selected at run time from the descriptor's data format.
            foreach (var layout in Gfx10UnifiedFormat.ComponentLayouts)
            {
                if (layout.Component == (uint)component)
                {
                    SetLayout(layout.DataFormat, layout.ByteOffset, layout.BitOffset, layout.BitCount);
                }
            }

            var packed = LoadUnalignedBufferWord(
                bindingIndex,
                IAdd(elementAddress, byteOffset));
            var componentBytes = ShiftRightLogical(IAdd(IAdd(bitOffset, bitCount), UInt(7)), UInt(3));
            componentInBounds = _module.AddInstruction(
                SpirvOp.LogicalOr, _boolType,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, bitCount, UInt(0)),
                IsBufferElementInRange(bindingIndex, IAdd(elementAddress, byteOffset),
                    _module.AddInstruction(SpirvOp.ISub, _uintType, componentBytes, UInt(1))));
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

        // A byte or short store merges into its dword atomically, so neighbouring
        // sub-word stores from other invocations keep their bytes.
        private void StoreBufferBytes(
            int bindingIndex,
            uint byteAddress,
            uint value,
            uint byteCount,
            uint sourceShift)
        {
            if (sourceShift != 0)
            {
                value = ShiftRightLogical(value, UInt(sourceShift));
            }

            StoreBufferElementBits(
                bindingIndex,
                byteAddress,
                [(value, byteCount == 1 ? 0xFFu : 0xFFFFu)]);
        }

        // Stores an element of up to four dwords at any byte alignment. Each touched
        // dword keeps the bits outside the element's mask.
        private void StoreBufferElementBits(
            int bindingIndex,
            uint byteAddress,
            IReadOnlyList<(uint Value, uint Mask)> element)
        {
            var alignment = BitwiseAnd(byteAddress, UInt(3));
            var shift = ShiftLeftLogical(alignment, UInt(3));
            var aligned = _module.AddInstruction(SpirvOp.IEqual, _boolType, alignment, UInt(0));
            var carryShift = _module.AddInstruction(SpirvOp.ISub, _uintType, UInt(32), shift);
            var firstDword = ShiftRightLogical(byteAddress, UInt(2));
            for (var index = 0; index <= element.Count; index++)
            {
                var value = UInt(0);
                var mask = UInt(0);
                if (index < element.Count)
                {
                    var (elementValue, elementMask) = element[index];
                    value = ShiftLeftLogical(BitwiseAnd(elementValue, UInt(elementMask)), shift);
                    mask = ShiftLeftLogical(UInt(elementMask), shift);
                }

                if (index > 0)
                {
                    // An unaligned element carries its high bits into the next dword.
                    var (previousValue, previousMask) = element[index - 1];
                    value = BitwiseOr(
                        value,
                        _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            aligned,
                            UInt(0),
                            ShiftRightLogical(BitwiseAnd(previousValue, UInt(previousMask)), carryShift)));
                    mask = BitwiseOr(
                        mask,
                        _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            aligned,
                            UInt(0),
                            ShiftRightLogical(UInt(previousMask), carryShift)));
                }

                StoreBufferMaskedWord(
                    bindingIndex,
                    index == 0 ? firstDword : IAdd(firstDword, UInt((uint)index)),
                    value,
                    mask);
            }
        }

        // Writes the masked bits of one dword: a plain store for a full mask, else an
        // atomic read-modify-write that keeps the other bits.
        private void StoreBufferMaskedWord(int bindingIndex, uint dwordAddress, uint value, uint mask)
        {
            var touched = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                _module.AddInstruction(SpirvOp.INotEqual, _boolType, mask, UInt(0)),
                IsBufferWordInRange(bindingIndex, dwordAddress));
            EmitConditional(touched, () =>
            {
                var pointer = BufferWordPointer(bindingIndex, dwordAddress);
                var full = _module.AddInstruction(SpirvOp.IEqual, _boolType, mask, UInt(uint.MaxValue));
                EmitConditional(
                    full,
                    () => Store(pointer, value),
                    () => EmitAtomicWordUpdate(
                        pointer,
                        observed => BitwiseOr(
                            BitwiseAnd(observed, _module.AddInstruction(SpirvOp.Not, _uintType, mask)),
                            value)));
            });
        }

        // Compare-exchange loop: the merge runs on the observed word until the
        // exchange succeeds. Returns the observed (pre-exchange) word at the
        // point the loop exits, which is a valid use here because it is
        // defined by the OpPhi in `header`, and `header` dominates `mergeLabel`.
        private uint EmitAtomicWordUpdate(
            uint pointer,
            Func<uint, uint> merge,
            uint loadSemantics = 0,
            uint equalSemantics = 0,
            uint unequalSemantics = 0,
            uint scope = 1)
        {
            var preheader = _module.AllocateId();
            var header = _module.AllocateId();
            var continueLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var exchanged = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, preheader);
            _module.AddLabel(preheader);
            var initial = _module.AddInstruction(
                SpirvOp.AtomicLoad,
                _uintType,
                pointer,
                UInt(scope),
                UInt(loadSemantics));
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(header);
            var observed = _module.AddInstruction(
                SpirvOp.Phi,
                _uintType,
                initial,
                preheader,
                exchanged,
                continueLabel);
            var next = merge(observed);
            // The phi above names this result before it is emitted.
            _module.AddStatement(
                SpirvOp.AtomicCompareExchange,
                _uintType,
                exchanged,
                pointer,
                UInt(scope),
                UInt(equalSemantics),
                UInt(unequalSemantics),
                next,
                observed);
            var success = _module.AddInstruction(SpirvOp.IEqual, _boolType, exchanged, observed);
            _module.AddStatement(SpirvOp.LoopMerge, mergeLabel, continueLabel, 0);
            _module.AddStatement(SpirvOp.BranchConditional, success, mergeLabel, continueLabel);
            _module.AddLabel(continueLabel);
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(mergeLabel);
            return observed;
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
            opcode.StartsWith("ImageGather", StringComparison.Ordinal) ||
            opcode == "ImageGetLod";

        private bool TryEmitVertexInputFetch(
            Gen5BufferMemoryControl control,
            SpirvVertexInput input,
            out string error)
        {
            error = string.Empty;
            if (control.DwordCount == 0)
            {
                error =
                    $"invalid vertex input fetch components={control.DwordCount}";
                return false;
            }

            var loaded = Load(input.Type, input.Variable);
            for (uint component = 0; component < control.DwordCount; component++)
            {
                uint raw;
                var selector = (input.DestinationSelect >> (int)(component * 3)) & 0x7u;
                if (selector == 0)
                {
                    raw = UInt(0);
                }
                else if (selector == 1)
                {
                    raw = UInt(input.NumberFormat is 4u or 5u ? 1u : 0x3F80_0000u);
                }
                else if (selector is >= 4u and <= 7u)
                {
                    var sourceComponent = selector - 4u;
                    if (sourceComponent >= input.ComponentCount)
                    {
                        error =
                            $"vertex input destination selector exceeds source components: selector={selector} components={input.ComponentCount}";
                        return false;
                    }

                    var value = input.ComponentCount == 1
                        ? loaded
                        : _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            input.ComponentType,
                            loaded,
                            sourceComponent);
                    raw = input.ComponentKind == VertexInputComponentKind.Uint
                        ? value
                        : Bitcast(_uintType, value);
                }
                else
                {
                    error = $"unsupported vertex input destination selector={selector}";
                    return false;
                }

                StoreV(control.VectorData + component, raw);
            }

            return true;
        }

        private bool TryEmitImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string error)
        {
            error = string.Empty;
            SpirvImageResource resource;
            uint imageObject;
            uint dstSelect;
            uint mipLevel;
            {
                if (TryGetImageElementCases(instruction, image, out var selector, out var elements, out error))
                {
                    // One case per descriptor over a constant element, like a switch on the selector.
                    var emitted = true;
                    var caseError = string.Empty;
                    for (var index = 0; index < elements.Count && emitted; index++)
                    {
                        var elementCase = elements[index];
                        EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, selector, UInt((uint)index)), () =>
                        {
                            if (!TryResolveLayoutImage(instruction, image, out var caseResource, out var caseImageObject, out var caseDstSelect, out caseError,
                                    elementCase))
                            {
                                emitted = false;
                                return;
                            }

                            // A sampled mip load keeps its mip operand; a storage case is already its own mip view.
                            var caseMipLevel = instruction.Opcode == "ImageLoadMip" && !caseResource.IsStorage
                                ? LoadImageIntegerAddress(image, (int)ImageCoordinateComponentCount(caseResource))
                                : UInt(0);
                            if (!EmitImageOperation(instruction, image, caseResource, caseImageObject, caseDstSelect, caseMipLevel, out caseError))
                            {
                                emitted = false;
                            }
                        });
                    }

                    error = caseError;
                    return emitted;
                }

                if (error.Length != 0)
                {
                    return false;
                }

                if (!TryResolveLayoutImage(instruction, image, out resource, out imageObject, out dstSelect, out error))
                {
                    return false;
                }

                // A sampled mip load reads its level from the address operand after the coordinates.
                mipLevel = instruction.Opcode == "ImageLoadMip"
                    ? LoadImageIntegerAddress(image, (int)ImageCoordinateComponentCount(resource))
                    : UInt(0);
            }

            return EmitImageOperation(instruction, image, resource, imageObject, dstSelect, mipLevel, out error);
        }

        private bool TryEmitBvhMissFallback(
            Gen5BvhRayControl bvh,
            out string error)
        {
            error = string.Empty;

            // Guest AMD BVH nodes cannot be consumed by a host acceleration
            // structure directly.  Until raw-node traversal is available,
            // return a conservative no-intersection result instead of binding
            // the BVH descriptor as an ordinary texture (which is incorrect).
            // Triangle node types are 0..3; box/instance/AABB types are 4..7.
            var nodeType = BitwiseAnd(
                LoadV(bvh.GetAddressRegister(0)),
                UInt(7));
            var triangle = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                nodeType,
                UInt(4));
            var barycentricMode = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                BitwiseAnd(LoadS(bvh.ScalarResource + 3), UInt(1u << 24)),
                UInt(0));
            var triangleT = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                barycentricMode,
                UInt(0x7F80_0000),
                UInt(0));
            var triangleDenominator = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                barycentricMode,
                UInt(0x3F80_0000),
                UInt(0));
            var invalidNode = UInt(uint.MaxValue);
            uint SelectResult(uint triangleValue) => _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                triangle,
                triangleValue,
                invalidNode);

            StoreV(bvh.VectorData, SelectResult(triangleT));
            StoreV(bvh.VectorData + 1, SelectResult(triangleDenominator));
            StoreV(bvh.VectorData + 2, SelectResult(UInt(0)));
            StoreV(bvh.VectorData + 3, SelectResult(UInt(0)));
            return true;
        }

        // One image operation over a resolved image object; its results are register writes.
        private bool EmitImageOperation(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            SpirvImageResource resource,
            uint imageObject,
            uint dstSelect,
            uint mipLevel,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode == "ImageGetResinfo")
            {
                var sizeComponentCount = ImageCoordinateComponentCount(resource);
                // ImageGetResinfo never goes through UsesSampler(), so imageObject here is
                // always the raw OpTypeImage value, not a sampled-image combo — no OpImage
                // extraction needed (or valid) for either the storage or sampled case.
                var queryImage = imageObject;
                var size = _module.AddInstruction(
                    resource.IsStorage || resource.Multisampled
                        ? SpirvOp.ImageQuerySize
                        : SpirvOp.ImageQuerySizeLod,
                    ImageIntegerCoordinateType(sizeComponentCount),
                    resource.IsStorage || resource.Multisampled
                        ? [queryImage]
                        : [queryImage, LoadImageIntegerAddress(image, 0)]);
                // NVIDIA's compiler crashes on an unused level query of a 3D image.
                var levels = (image.Dmask & 0x8u) != 0 && !resource.IsStorage && !resource.Multisampled
                    ? _module.AddInstruction(
                        SpirvOp.ImageQueryLevels,
                        _uintType,
                        queryImage)
                    : UInt(1);
                uint outputIndex = 0;
                for (uint component = 0; component < 4; component++)
                {
                    if ((image.Dmask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    uint value;
                    if (component < sizeComponentCount)
                    {
                        var signedValue = sizeComponentCount == 1
                            ? size
                            : _module.AddInstruction(
                                SpirvOp.CompositeExtract,
                                _intType,
                                size,
                                component);
                        value = Bitcast(_uintType, signedValue);
                    }
                    else if (component == 3)
                    {
                        value = levels;
                    }
                    else
                    {
                        value = UInt(0);
                    }

                    StoreV(image.VectorData + outputIndex++, value);
                }

                return true;
            }

            if (instruction.Opcode == "ImageGetLod")
            {
                var coordinateComponentCount = ImageCoordinateComponentCount(resource);
                var coordinates = BuildFloatCoordinates(
                    image,
                    0,
                    coordinateComponentCount,
                    resource);
                var queried = _module.AddInstruction(
                    SpirvOp.ImageQueryLod,
                    _vec2Type,
                    imageObject,
                    coordinates);
                uint outputIndex = 0;
                var mask = image.Dmask != 0 ? image.Dmask : 1u;
                for (uint component = 0; component < 2; component++)
                {
                    if ((mask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    var value = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        queried,
                        component);
                    StoreV(image.VectorData + outputIndex++, Bitcast(_uintType, value));
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

                var coordinateComponentCount =
                    ImageCoordinateComponentCount(resource);
                var coordinates = BuildIntegerCoordinates(
                    image,
                    0,
                    coordinateComponentCount);
                var components = new uint[4];
                for (var component = 0; component < components.Length; component++)
                {
                    var sourceIndex = Gen5ShaderTranslator.GetImageStoreSourceIndex(
                        dstSelect,
                        image.Dmask,
                        component);
                    if (sourceIndex >= 0)
                    {
                        var raw = LoadImageStoreComponent(
                            image,
                            resource,
                            (uint)sourceIndex);
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
                texel = PackImageTexel(resource, texel);
                EmitExecConditional(() =>
                    _module.AddStatement(
                        SpirvOp.ImageWrite,
                        imageObject,
                        coordinates,
                        texel));

                return true;
            }

            if (instruction.Opcode.StartsWith("ImageAtomic", StringComparison.Ordinal))
            {
                if (!resource.IsStorage)
                {
                    error = "image atomic is not bound as storage";
                    return false;
                }

                // IMAGE_ATOMIC_FMIN/FMAX target float-format storage images and
                // have no native SPIR-V storage-image atomic without pulling in
                // SPV_EXT_shader_atomic_float_min_max. Lower them as a
                // compare-and-swap loop on the underlying bit pattern instead
                // (same technique used by other float-atomic emulations that
                // avoid that extension dependency).
                if (instruction.Opcode is "ImageAtomicFmax" or "ImageAtomicFmin")
                {
                    var isMax = instruction.Opcode == "ImageAtomicFmax";
                    var floatCoordinateCount = ImageCoordinateComponentCount(resource);
                    var floatAtomicImageSize = _module.AddInstruction(
                        SpirvOp.ImageQuerySize,
                        ImageIntegerCoordinateType(floatCoordinateCount),
                        imageObject);
                    var floatCoordinates = BuildClampedIntegerCoordinates(
                        image,
                        0,
                        floatAtomicImageSize,
                        floatCoordinateCount);
                    EmitExecConditional(() =>
                    {
                        var pointer = _module.AddInstruction(
                            SpirvOp.ImageTexelPointer,
                            _module.TypePointer(SpirvStorageClass.Image, _uintType),
                            resource.Variable,
                            floatCoordinates,
                            UInt(0));
                        var srcBits = Bitcast(_uintType, LoadV(image.VectorData));
                        var old = EmitAtomicWordUpdate(
                            pointer,
                            observed =>
                            {
                                var oldFloat = Bitcast(_floatType, observed);
                                var srcFloat = Bitcast(_floatType, srcBits);
                                var pickSrc = _module.AddInstruction(
                                    isMax ? SpirvOp.FOrdLessThan : SpirvOp.FOrdGreaterThan,
                                    _boolType,
                                    oldFloat,
                                    srcFloat);
                                var chosen = _module.AddInstruction(
                                    SpirvOp.Select, _floatType, pickSrc, srcFloat, oldFloat);
                                return Bitcast(_uintType, chosen);
                            },
                            loadSemantics: 0x802,
                            equalSemantics: 0x808,
                            unequalSemantics: 0x802);
                        if (image.Glc)
                        {
                            StoreV(image.VectorData, old);
                        }
                    });
                    return true;
                }

                if (resource.ComponentKind == ImageComponentKind.Float ||
                    !TryGetAtomicOp(instruction.Opcode["ImageAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported storage image opcode {instruction.Opcode}";
                    return false;
                }

                var signed = resource.ComponentKind == ImageComponentKind.Sint;
                var coordinateComponentCount =
                    ImageCoordinateComponentCount(resource);
                var coordinates = BuildIntegerCoordinates(
                    image,
                    0,
                    coordinateComponentCount);
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
                    var coordinateComponentCount =
                        ImageCoordinateComponentCount(resource);
                    var coordinates = BuildIntegerCoordinates(
                        image,
                        0,
                        coordinateComponentCount);
                    sampled = _module.AddInstruction(
                        SpirvOp.ImageRead,
                        resource.VectorType,
                        imageObject,
                        coordinates);
                }
                else
                {
                    // A sampled image is fetched through its image type; a request has no sampler here.
                    var fetchedImage = imageObject;
                    var coordinateComponentCount =
                        ImageCoordinateComponentCount(resource);
                    var coordinates = BuildIntegerCoordinates(
                        image,
                        0,
                        coordinateComponentCount);
                    if (resource.Multisampled)
                    {
                        var sample = LoadImageIntegerAddress(
                            image,
                            (int)coordinateComponentCount);
                        sampled = _module.AddInstruction(
                            SpirvOp.ImageFetch,
                            resource.VectorType,
                            fetchedImage,
                            coordinates,
                            0x40,
                            sample);
                    }
                    else
                    {
                        sampled = _module.AddInstruction(
                            SpirvOp.ImageFetch,
                            resource.VectorType,
                            fetchedImage,
                            coordinates,
                            2,
                            mipLevel);
                    }
                }

                sampled = UnpackImageTexel(resource, sampled);
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageSample",
                         StringComparison.Ordinal))
            {
                var sampleFlags = ImageSampleOpcodeInfo.Decode(instruction.Opcode);
                var hasOffset = (sampleFlags & ImageSampleFlags.Offset) != 0;
                var hasCompare = (sampleFlags & ImageSampleFlags.Compare) != 0;
                var hasGradients = (sampleFlags & ImageSampleFlags.Derivative) != 0;
                var hasZeroLod = (sampleFlags & ImageSampleFlags.LevelZero) != 0;
                var hasLod = (sampleFlags & ImageSampleFlags.Lod) != 0;
                var hasBias = (sampleFlags & ImageSampleFlags.Bias) != 0;

                if (hasCompare && resource.ConversionFormat != GuestImageFormat.Invalid)
                {
                    error = "image sample uses depth comparison with a packed integer image";
                    return false;
                }

                // RDNA MIMG address operands are ordered as
                // {offset}{bias/lod}{z-compare}{derivatives}{body}.  The old
                // lowering treated SAMPLE_D as body-first and consequently
                // sampled gradients as coordinates in every captured
                // derivative operation.
                var spatialComponentCount =
                    ImageSpatialComponentCount(resource);
                var coordinateComponentCount =
                    ImageCoordinateComponentCount(resource);
                var addressCursor = 0;
                var offset = 0u;
                if (hasOffset)
                {
                    addressCursor = AlignFullImageAddress(image, addressCursor);
                    offset = BuildImageOffset(
                        image,
                        addressCursor,
                        spatialComponentCount);
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
                    ? BuildFloatCoordinates(
                        image,
                        addressCursor,
                        spatialComponentCount,
                        resource)
                    : 0u;
                var gradientY = hasGradients
                    ? BuildFloatCoordinates(
                        image,
                        addressCursor + (int)spatialComponentCount,
                        spatialComponentCount,
                        resource)
                    : 0u;
                if (hasGradients)
                {
                    addressCursor += checked((int)(spatialComponentCount * 2));
                }

                var coordinates = BuildFloatCoordinates(
                    image,
                    addressCursor,
                    coordinateComponentCount,
                    resource);
                // Non-pixel samples require explicit derivatives or a level of detail.
                // Use level zero when the instruction supplies neither.
                var explicitLod = hasGradients || hasZeroLod || hasLod ||
                    _stage != Gen5SpirvStage.Pixel;
                var lod = hasZeroLod
                    ? Float(0)
                    : hasLod
                        ? LoadImageFloatAddress(
                            image,
                            addressCursor + (int)coordinateComponentCount)
                        : explicitLod
                            ? Float(0)
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

                if (hasCompare && resource.EmulatedCompareFunction >= 0)
                {
                    // A color format cannot back a Vulkan depth-compare view; compare
                    // the sampled first channel like RDNA does for such formats.
                    var texel = _module.AddInstruction(
                        explicitLod ? SpirvOp.ImageSampleExplicitLod : SpirvOp.ImageSampleImplicitLod,
                        resource.VectorType,
                        [.. operands]);
                    var depth = EmulatedDepthCompare(
                        reference,
                        _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, texel, 0u),
                        resource.EmulatedCompareFunction);
                    sampled = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        resource.VectorType,
                        depth,
                        depth,
                        depth,
                        depth);
                }
                else if (hasCompare)
                {
                    // The sampler carries the compare; RDNA replicates the scalar
                    // comparison result into every enabled destination component.
                    var drefOperands = new List<uint> { imageObject, coordinates, reference };
                    drefOperands.AddRange(operands.Skip(2));
                    var depth = _module.AddInstruction(
                        explicitLod ? SpirvOp.ImageSampleDrefExplicitLod : SpirvOp.ImageSampleDrefImplicitLod,
                        _floatType,
                        [.. drefOperands]);
                    sampled = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        resource.VectorType,
                        depth,
                        depth,
                        depth,
                        depth);
                }
                else
                {
                    sampled = _module.AddInstruction(
                        explicitLod
                            ? SpirvOp.ImageSampleExplicitLod
                            : SpirvOp.ImageSampleImplicitLod,
                        resource.VectorType,
                        [.. operands]);
                    sampled = UnpackImageTexel(resource, sampled);
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
                var gatherHorizontal =
                    instruction.Opcode == "ImageGather4H";
                var hasExplicitLod =
                    instruction.Opcode == "ImageGather4L";

                // SPIR-V/Vulkan has no explicit-LOD operand for OpImageGather.
                // Use the documented mip-level-zero approximation, while
                // preserving the final guest LOD address component for decoding.
                if (hasExplicitLod &&
                    System.Threading.Interlocked.Exchange(
                        ref _warnedGather4LApproximation,
                        1) == 0)
                {
                    Console.Error.WriteLine(
                        "Warning: IMAGE_GATHER4_L is approximated with OpImageGather at mip level zero; its explicit LOD is ignored.");
                }

                if (hasCompare && resource.ConversionFormat != GuestImageFormat.Invalid)
                {
                    error = "image gather uses depth comparison with a packed integer image";
                    return false;
                }
                var spatialComponentCount =
                    ImageSpatialComponentCount(resource);
                var coordinateComponentCount =
                    ImageCoordinateComponentCount(resource);
                var addressCursor = 0;
                var offset = 0u;
                if (hasOffset)
                {
                    offset = BuildImageOffset(
                        image,
                        addressCursor,
                        spatialComponentCount);
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

                var coordinates = BuildFloatCoordinates(
                    image,
                    addressCursor,
                    coordinateComponentCount,
                    resource);

                if (resource.Dimension == SpirvImageDim.Dim1D)
                {
                    var levelZero =
                        instruction.Opcode.Contains("Lz", StringComparison.Ordinal);
                    if (resource.Arrayed ||
                        hasCompare ||
                        hasOffset ||
                        gatherHorizontal ||
                        !levelZero)
                    {
                        error = resource.Arrayed
                            ? "unsupported 1D-array image gather"
                            : "unsupported 1D image gather variant";
                        return false;
                    }

                    sampled = EmitOneDimensionalGatherLz(
                        image,
                        resource,
                        imageObject,
                        coordinates);
                    sampled = UnpackImageGather(resource, image.Dmask, sampled);
                    writeAllComponents = true;
                    goto GatherComplete;
                }

                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };
                var emulatedCompare = hasCompare && resource.EmulatedCompareFunction >= 0;
                if (emulatedCompare)
                {
                    // Gather the first channel and compare each texel in the shader.
                    operands.Add(UInt(0));
                }
                else if (hasCompare)
                {
                    operands.Add(reference);
                }
                else
                {
                    uint component = 0;
                    if (resource.ConversionFormat == GuestImageFormat.Invalid)
                    {
                        while (component < 3 &&
                               (image.Dmask & (1u << (int)component)) == 0)
                        {
                            component++;
                        }
                    }

                    operands.Add(UInt(component));
                }

                if (hasOffset)
                {
                    operands.Add(0x10u);
                    operands.Add(offset);
                }
                else if (gatherHorizontal)
                {
                    if (resource.Dimension == SpirvImageDim.Dim1D)
                    {
                        error = "unsupported 1D horizontal image gather";
                        return false;
                    }

                    operands.Add(0x20u);
                    operands.Add(BuildHorizontalGatherOffsets());
                }

                sampled = _module.AddInstruction(
                    hasCompare && !emulatedCompare ? SpirvOp.ImageDrefGather : SpirvOp.ImageGather,
                    resource.VectorType,
                    [.. operands]);
                if (emulatedCompare)
                {
                    var gathered = sampled;
                    var compared = new uint[4];
                    for (var texel = 0u; texel < 4; texel++)
                    {
                        compared[texel] = EmulatedDepthCompare(
                            reference,
                            _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, gathered, texel),
                            resource.EmulatedCompareFunction);
                    }

                    sampled = _module.AddInstruction(SpirvOp.CompositeConstruct, resource.VectorType, compared);
                }
                else if (!hasCompare)
                {
                    sampled = UnpackImageGather(resource, image.Dmask, sampled);
                }

                writeAllComponents = true;
            GatherComplete:;
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

        private uint EmitOneDimensionalGatherLz(
            Gen5ImageControl image,
            SpirvImageResource resource,
            uint sampledImage,
            uint coordinate)
        {
            var imageValue = Load(resource.ImageType, resource.Variable);
            var width = _module.AddInstruction(
                SpirvOp.ImageQuerySizeLod,
                _intType,
                imageValue,
                _module.Constant(_intType, 0));
            var widthFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                _floatType,
                width);
            var centered = _module.AddInstruction(
                SpirvOp.FSub,
                _floatType,
                _module.AddInstruction(
                    SpirvOp.FMul,
                    _floatType,
                    coordinate,
                    widthFloat),
                Float(0.5f));
            var left = Ext(8, _floatType, centered);

            uint component = 0;
            if (resource.ConversionFormat == GuestImageFormat.Invalid)
            {
                while (component < 3 &&
                       (image.Dmask & (1u << (int)component)) == 0)
                {
                    component++;
                }
            }

            var values = new uint[2];
            for (var index = 0; index < values.Length; index++)
            {
                var sampleCoordinate = _module.AddInstruction(
                    SpirvOp.FDiv,
                    _floatType,
                    _module.AddInstruction(
                        SpirvOp.FAdd,
                        _floatType,
                        left,
                        Float(index == 0 ? 0.5f : 1.5f)),
                    widthFloat);
                var texel = _module.AddInstruction(
                    SpirvOp.ImageSampleExplicitLod,
                    resource.VectorType,
                    sampledImage,
                    sampleCoordinate,
                    2u,
                    Float(0));
                values[index] = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    resource.ComponentType,
                    texel,
                    component);
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                values[0],
                values[1],
                values[1],
                values[0]);
        }

        private static bool TryGetPackedImageConversion(
            SpirvImageResource resource,
            out int componentCount,
            out ReadOnlySpan<uint> componentBits,
            out ReadOnlySpan<uint> componentOffsets)
        {
            if (resource.ConversionFormat == GuestImageFormat.Format11x2x10Uint)
            {
                componentCount = 3;
                componentBits = [11u, 11u, 10u];
                componentOffsets = [0u, 11u, 22u];
                return true;
            }

            componentCount = 0;
            componentBits = default;
            componentOffsets = default;
            return false;
        }

        private uint UnpackImageTexel(
            SpirvImageResource resource,
            uint texel)
        {
            if (resource.ConversionFormat is GuestImageFormat.Format8Uscaled or GuestImageFormat.Format8x2Uscaled)
            {
                var scaled = _module.AddInstruction(SpirvOp.FMul, resource.VectorType, texel,
                    _module.AddInstruction(SpirvOp.CompositeConstruct, resource.VectorType,
                        Float(255), Float(255), Float(1), Float(1)));
                var channels = new uint[4];
                for (var component = 0; component < channels.Length; component++)
                {
                    var selector = (resource.ShaderSwizzle >> (component * 3)) & 7u;
                    channels[component] = selector switch
                    {
                        1u => Float(1),
                        >= 4u => _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, scaled, selector - 4u),
                        _ => Float(0),
                    };
                }
                return _module.AddInstruction(SpirvOp.CompositeConstruct, resource.VectorType, channels);
            }

            if (!TryGetPackedImageConversion(
                    resource,
                    out var componentCount,
                    out var componentBits,
                    out var componentOffsets))
            {
                return texel;
            }

            var packed = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                texel,
                0u);
            var components = new uint[4];
            for (var component = 0; component < componentCount; component++)
            {
                components[component] = _module.AddInstruction(
                    SpirvOp.BitFieldUExtract,
                    _uintType,
                    packed,
                    UInt(componentOffsets[component]),
                    UInt(componentBits[component]));
            }

            for (var component = componentCount; component < components.Length; component++)
            {
                components[component] = components[component % componentCount];
            }

            var selected = new uint[4];
            for (var component = 0; component < selected.Length; component++)
            {
                var selector = (resource.ShaderSwizzle >> (component * 3)) & 7u;
                selected[component] = selector switch
                {
                    1u => UInt(1),
                    >= 4u => components[(selector - 4u) % (uint)componentCount],
                    _ => UInt(0),
                };
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                selected);
        }

        private uint UnpackImageGather(
            SpirvImageResource resource,
            uint dmask,
            uint gathered)
        {
            if (!TryGetPackedImageConversion(
                    resource,
                    out var componentCount,
                    out var componentBits,
                    out var componentOffsets))
            {
                return gathered;
            }

            uint component = 0;
            while (component < 3 && (dmask & (1u << (int)component)) == 0)
            {
                component++;
            }

            var selector = (resource.ShaderSwizzle >> ((int)component * 3)) & 7u;
            if (selector < 4u)
            {
                var value = selector == 1u ? UInt(1) : UInt(0);
                return _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    resource.VectorType,
                    value,
                    value,
                    value,
                    value);
            }

            var physical = (selector - 4u) % (uint)componentCount;
            var values = new uint[4];
            for (var lane = 0; lane < values.Length; lane++)
            {
                var packed = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    gathered,
                    (uint)lane);
                values[lane] = _module.AddInstruction(
                    SpirvOp.BitFieldUExtract,
                    _uintType,
                    packed,
                    UInt(componentOffsets[(int)physical]),
                    UInt(componentBits[(int)physical]));
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                values);
        }

        private uint PackImageTexel(
            SpirvImageResource resource,
            uint texel)
        {
            if (!TryGetPackedImageConversion(
                    resource,
                    out var componentCount,
                    out var componentBits,
                    out var componentOffsets))
            {
                return texel;
            }

            var packed = UInt(0);
            for (var component = 0; component < componentCount; component++)
            {
                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _uintType,
                    texel,
                    (uint)component);
                var maximum = (1u << (int)componentBits[component]) - 1u;
                var within = _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    value,
                    UInt(maximum));
                var clamped = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    within,
                    value,
                    UInt(maximum));
                var shifted = componentOffsets[component] == 0
                    ? clamped
                    : ShiftLeftLogical(clamped, UInt(componentOffsets[component]));
                packed = BitwiseOr(packed, shifted);
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                packed,
                UInt(0),
                UInt(0),
                UInt(0));
        }

        private uint BuildHorizontalGatherOffsets()
        {
            var vec2Int = _module.TypeVector(_intType, 2);
            var offsets = new uint[4];
            for (var index = 0; index < offsets.Length; index++)
            {
                offsets[index] = _module.ConstantComposite(
                    vec2Int,
                    _module.Constant(_intType, unchecked((uint)(index - 1))),
                    _module.Constant(_intType, 0));
            }

            return _module.ConstantComposite(
                _module.TypeArray(vec2Int, 4),
                offsets);
        }

        private static uint ImageSpatialComponentCount(
            SpirvImageResource resource) =>
            ImageSpatialComponentCountOf(resource.Dimension);

        private static uint ImageCoordinateComponentCount(
            SpirvImageResource resource) =>
            ImageSpatialComponentCount(resource) + (resource.Arrayed ? 1u : 0u);

        private uint ImageIntegerCoordinateType(uint componentCount) =>
            componentCount == 1
                ? _intType
                : _module.TypeVector(_intType, componentCount);

        private uint IntegerTypeForComponents(uint componentCount) =>
            componentCount == 1 ? _intType : _module.TypeVector(_intType, componentCount);

        private uint FloatTypeForComponents(uint componentCount) =>
            componentCount == 1 ? _floatType : _module.TypeVector(_floatType, componentCount);

        private uint BuildFloatCoordinates(
            Gen5ImageControl image,
            int start,
            uint componentCount,
            SpirvImageResource resource)
        {
            var components = new uint[checked((int)componentCount)];
            for (var component = 0; component < components.Length; component++)
            {
                components[component] = LoadImageFloatAddress(
                    image,
                    start + component);
            }

            if (resource.Cube && components.Length >= 2)
            {
                components[0] = _module.AddInstruction(SpirvOp.FSub, _floatType, components[0], Float(1));
                components[1] = _module.AddInstruction(SpirvOp.FSub, _floatType, components[1], Float(1));
                if (components.Length >= 3)
                {
                    var guestLayer = _module.AddInstruction(SpirvOp.ConvertFToU, _uintType, components[2]);
                    var padding = ShiftLeftLogical(ShiftRightLogical(guestLayer, UInt(3)), UInt(1));
                    var hostLayer = _module.AddInstruction(SpirvOp.ISub, _uintType, guestLayer, padding);
                    components[2] = _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, hostLayer);
                }
            }

            if (componentCount == 1)
            {
                return components[0];
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _module.TypeVector(_floatType, componentCount),
                components);
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

        private uint BuildIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            uint componentCount)
        {
            var components = new uint[checked((int)componentCount)];
            for (var component = 0; component < components.Length; component++)
            {
                components[component] = Bitcast(
                    _intType,
                    LoadImageIntegerAddress(image, start + component));
            }

            if (componentCount == 1)
            {
                return components[0];
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ImageIntegerCoordinateType(componentCount),
                components);
        }

        private uint BuildClampedIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            uint imageSize,
            uint componentCount)
        {
            var components = new uint[checked((int)componentCount)];
            for (var component = 0; component < components.Length; component++)
            {
                var extent = componentCount == 1
                    ? imageSize
                    : _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _intType,
                        imageSize,
                        (uint)component);
                components[component] = ClampSignedCoordinate(
                    Bitcast(
                        _intType,
                        LoadImageIntegerAddress(image, start + component)),
                    extent);
            }

            return componentCount == 1
                ? components[0]
                : _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    ImageIntegerCoordinateType(componentCount),
                    components);
        }

        private uint ClampSignedCoordinate(uint value, uint extent)
        {
            var zero = _module.Constant(_intType, 0);
            var maximum = _module.AddInstruction(
                SpirvOp.ISub,
                _intType,
                extent,
                _module.Constant(_intType, 1));
            var belowZero = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                value,
                zero);
            var nonNegative = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                belowZero,
                zero,
                value);
            var aboveMaximum = _module.AddInstruction(
                SpirvOp.SGreaterThan,
                _boolType,
                nonNegative,
                maximum);
            return _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                aboveMaximum,
                maximum,
                nonNegative);
        }

        private uint BuildImageOffset(
            Gen5ImageControl image,
            int component,
            uint componentCount)
        {
            var packed = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(
                    ImageAddressRegister(image, component))));
            var components = new uint[checked((int)componentCount)];
            for (var index = 0; index < components.Length; index++)
            {
                components[index] = _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    packed,
                    UInt((uint)(index * 8)),
                    UInt(6));
            }

            if (componentCount == 1)
            {
                return components[0];
            }

            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _module.TypeVector(_intType, componentCount),
                components);
        }

        private uint ApplyDynamicSampleOffset(
            SpirvImageResource resource,
            uint sampledImage,
            uint coordinates,
            uint texelOffset,
            uint lod)
        {
            var spatialComponentCount = ImageSpatialComponentCount(resource);
            var coordinateComponentCount =
                ImageCoordinateComponentCount(resource);
            var spatialIntegerType =
                spatialComponentCount == 1
                    ? _intType
                    : _module.TypeVector(_intType, spatialComponentCount);
            var spatialFloatType =
                spatialComponentCount == 1
                    ? _floatType
                    : _module.TypeVector(_floatType, spatialComponentCount);
            var queryComponentCount = resource.Arrayed
                ? coordinateComponentCount
                : spatialComponentCount;
            var queryIntegerType =
                queryComponentCount == 1
                    ? _intType
                    : _module.TypeVector(_intType, queryComponentCount);
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
                queryIntegerType,
                image,
                clampedLod);
            if (resource.Arrayed)
            {
                if (spatialComponentCount == 1)
                {
                    size = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _intType,
                        size,
                        0u);
                }
                else
                {
                    var spatialSizeComponents =
                        new uint[checked((int)spatialComponentCount)];
                    for (uint component = 0;
                         component < spatialComponentCount;
                         component++)
                    {
                        spatialSizeComponents[component] = component;
                    }

                    size = _module.AddInstruction(
                        SpirvOp.VectorShuffle,
                        spatialIntegerType,
                        [size, size, .. spatialSizeComponents]);
                }
            }

            var sizeFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                spatialFloatType,
                size);
            var offsetFloat = _module.AddInstruction(
                SpirvOp.ConvertSToF,
                spatialFloatType,
                texelOffset);
            var normalizedOffset = _module.AddInstruction(
                SpirvOp.FDiv,
                spatialFloatType,
                offsetFloat,
                sizeFloat);
            if (!resource.Arrayed)
            {
                return _module.AddInstruction(
                    SpirvOp.FAdd,
                    spatialFloatType,
                    coordinates,
                    normalizedOffset);
            }

            var arrayOffsetComponents =
                new uint[checked((int)coordinateComponentCount)];
            for (uint component = 0;
                 component < spatialComponentCount;
                 component++)
            {
                arrayOffsetComponents[component] =
                    spatialComponentCount == 1
                        ? normalizedOffset
                        : _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _floatType,
                            normalizedOffset,
                            component);
            }
            arrayOffsetComponents[coordinateComponentCount - 1] = Float(0);
            var arrayOffset = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _module.TypeVector(_floatType, coordinateComponentCount),
                arrayOffsetComponents);
            return _module.AddInstruction(
                SpirvOp.FAdd,
                _module.TypeVector(_floatType, coordinateComponentCount),
                coordinates,
                arrayOffset);
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

            if (_stage == Gen5SpirvStage.Mesh)
            {
                return TryEmitMeshExport(instruction, export);
            }

            if (_stage == Gen5SpirvStage.Pixel)
            {
                // RDNA2 EXP.VM communicates the current EXEC mask even for a
                // NULL export or an MRT not bound by this host pass. Record it
                // before resolving the data target; the final VM export wins.
                if (export.ValidMask && _pixelValidMaskActive != 0)
                {
                    Store(_pixelValidMaskActive, Load(_boolType, _exec));
                }

                if (!_pixelOutputs.TryGetValue(export.Target, out var output))
                {
                    return true;
                }

                Store(_reachedPixelExport, _module.ConstantBool(true));

                var values = new uint[4];
                Span<uint> unormPairs = stackalloc uint[2];
                for (var component = 0; component < 4; component++)
                {
                    var enabled = (export.EnableMask & (1u << component)) != 0;
                    if (!enabled)
                    {
                        var outputComponent = (uint)component;
                        if (!output.ComponentMapping.IsIdentity)
                        {
                            for (var physicalComponent = 0u; physicalComponent < 4; physicalComponent++)
                            {
                                if (output.ComponentMapping.Map(physicalComponent) == component)
                                {
                                    outputComponent = physicalComponent;
                                    break;
                                }
                            }
                        }

                        values[component] = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            output.Kind switch
                            {
                                Gen5PixelOutputKind.Uint => _uintType,
                                Gen5PixelOutputKind.Sint => _intType,
                                _ => _floatType,
                            },
                            Load(output.Type, output.Variable),
                            outputComponent);
                        continue;
                    }

                    if (export.Compressed)
                    {
                        if (output.TargetOutputMode == 7)
                        {
                            values[component] = LoadCompressedUintExportComponent(
                                instruction,
                                component);
                            continue;
                        }

                        uint value;
                        if (output.TargetOutputMode == 5)
                        {
                            var pair = component >> 1;
                            var unpacked = unormPairs[pair];
                            if (unpacked == 0)
                            {
                                unpacked = Ext(
                                    61,
                                    _vec2Type,
                                    LoadV(instruction.Sources[pair].Value));
                                unormPairs[pair] = unpacked;
                            }

                            value = _module.AddInstruction(
                                SpirvOp.CompositeExtract,
                                _floatType,
                                unpacked,
                                (uint)(component & 1));
                        }
                        else
                        {
                            value = LoadCompressedExportComponent(
                                instruction,
                                component,
                                output.TargetOutputMode);
                        }
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
                        Bitcast(_floatType, EmitHalfToFloat(EmitFloatToHalfRtz(LoadV(registerBase)))),
                        Bitcast(_floatType, EmitHalfToFloat(EmitFloatToHalfRtz(LoadV(registerBase + 1)))));
                    var highPair = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        Bitcast(_floatType, EmitHalfToFloat(EmitFloatToHalfRtz(LoadV(registerBase + 2)))),
                        Bitcast(_floatType, EmitHalfToFloat(EmitFloatToHalfRtz(LoadV(registerBase + 3)))));
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
                if (_forcePixelMagenta &&
                    PixelExportDebugAddressMatches() &&
                    PixelExportDebugHashMatches())
                {
                    if (!_magentaProbeLogged)
                    {
                        Console.Error.WriteLine(
                            $"[GPU][MAGENTA-PROBE] applied hash=0x{_request.Hash:X16} " +
                            $"address=0x{_request.Program.Address:X16} mrt={export.Target}");
                        _magentaProbeLogged = true;
                    }

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
                if (output.ComponentMapping != Gen5ColorComponentMapping.Identity)
                {
                    vector = _module.AddInstruction(
                        SpirvOp.VectorShuffle,
                        output.Type,
                        vector,
                        vector,
                        output.ComponentMapping.Map(0),
                        output.ComponentMapping.Map(1),
                        output.ComponentMapping.Map(2),
                        output.ComponentMapping.Map(3));
                }
                if (Environment.GetEnvironmentVariable(
                        "SHARPEMU_FORCE_TITLE_EXPORT_EXEC") == "1" &&
                    _request.Program.Address == 0x0000000500781200ul)
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
                    EmitAuxPositionExport(instruction, export);
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
            if (export.Target == 12 && _request.ClipSpace.Enabled)
            {
                outputValue = ConvertPositionToClipSpace(outputValue);
            }
            if (_request.Program.Address == 0x0000000500780000ul &&
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
            if (export.Target == 12)
            {
                EmitZeroPositionClipGuard(outputValue);
            }
            Store(outputVariable, outputValue);
            return true;
        }

        private readonly record struct PositionExportComponent(
            uint ClipDistance,
            uint CullDistance,
            bool PointSize,
            bool Layer,
            bool Viewport);

        private static PositionExportComponent DecodePositionExportComponent(
            uint control,
            uint positionIndex,
            uint component)
        {
            if (positionIndex == 0 || component >= 4)
            {
                return new(uint.MaxValue, uint.MaxValue, false, false, false);
            }

            var slot = positionIndex - 1;
            var vector = 3u;
            for (var index = 0u; index < 3; index++)
            {
                if ((control & (1u << (int)(21 + index))) == 0)
                {
                    continue;
                }

                if (slot == 0)
                {
                    vector = index;
                    break;
                }

                slot--;
            }

            if (vector == 3)
            {
                return new(uint.MaxValue, uint.MaxValue, false, false, false);
            }

            if (vector == 0)
            {
                return new(
                    uint.MaxValue,
                    uint.MaxValue,
                    component == 0 && (control & (1u << 16)) != 0,
                    component == 2 && (control & (1u << 18)) != 0,
                    component == 2 && (control & (1u << 19)) != 0);
            }

            var scalar = (vector - 1) * 4 + component;
            var lower = scalar == 0 ? 0u : (1u << (int)scalar) - 1u;
            var clip = control & 0xffu;
            var cull = (control >> 8) & 0xffu;
            return new(
                (clip & (1u << (int)scalar)) != 0
                    ? (uint)System.Numerics.BitOperations.PopCount(clip & lower)
                    : uint.MaxValue,
                (cull & (1u << (int)scalar)) != 0
                    ? (uint)System.Numerics.BitOperations.PopCount(cull & lower)
                    : uint.MaxValue,
                false,
                false,
                false);
        }

        private void DeclareAuxPositionOutputs()
        {
            var needPointSize = false;
            var needLayer = false;
            var needViewport = false;
            var clipCount = 0u;
            var cullCount = 0u;

            foreach (var export in _request.Program.Instructions
                         .Select(static instruction => instruction.Control)
                         .OfType<Gen5ExportControl>()
                         .Where(static export => export.Target is >= 13 and < 16))
            {
                var positionIndex = export.Target - 12;
                for (var component = 0u; component < 4; component++)
                {
                    if ((export.EnableMask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    var output = DecodePositionExportComponent(
                        _request.PositionExportControl,
                        positionIndex,
                        component);
                    needPointSize |= output.PointSize;
                    needLayer |= output.Layer;
                    needViewport |= output.Viewport;
                    if (output.ClipDistance != uint.MaxValue)
                    {
                        clipCount = Math.Max(clipCount, output.ClipDistance + 1);
                    }
                    if (output.CullDistance != uint.MaxValue)
                    {
                        cullCount = Math.Max(cullCount, output.CullDistance + 1);
                    }
                }
            }

            if (needPointSize)
            {
                _pointSizeOutput = DeclareBuiltInOutput(
                    _floatType,
                    SpirvBuiltIn.PointSize,
                    "gl_PointSize");
            }
            if (needLayer)
            {
                _module.AddCapability(SpirvCapability.ShaderLayer);
                _layerOutput = DeclareBuiltInOutput(
                    _uintType,
                    SpirvBuiltIn.Layer,
                    "gl_Layer");
            }
            if (needViewport)
            {
                _module.AddCapability(SpirvCapability.ShaderViewportIndex);
                _viewportIndexOutput = DeclareBuiltInOutput(
                    _uintType,
                    SpirvBuiltIn.ViewportIndex,
                    "gl_ViewportIndex");
            }

            // Keep the guest clip distances at their architectural indices and
            // append one private plane used to reject an all-zero position.
            _invalidPositionClipDistance = clipCount++;
            if (clipCount != 0)
            {
                _module.AddCapability(SpirvCapability.ClipDistance);
                _clipDistanceCount = clipCount;
                _clipDistanceElementPointerType =
                    _module.TypePointer(SpirvStorageClass.Output, _floatType);
                _clipDistanceOutput = DeclareBuiltInOutput(
                    _module.TypeArray(_floatType, clipCount),
                    SpirvBuiltIn.ClipDistance,
                    "gl_ClipDistance");
            }
            if (cullCount != 0)
            {
                _module.AddCapability(SpirvCapability.CullDistance);
                _cullDistanceCount = cullCount;
                _cullDistanceOutput = DeclareBuiltInOutput(
                    _module.TypeArray(_floatType, cullCount),
                    SpirvBuiltIn.CullDistance,
                    "gl_CullDistance");
            }
        }

        private uint DeclareBuiltInOutput(
            uint type,
            SpirvBuiltIn builtIn,
            string name)
        {
            var pointer = _module.TypePointer(SpirvStorageClass.Output, type);
            var variable = _module.AddGlobalVariable(pointer, SpirvStorageClass.Output);
            _module.AddName(variable, name);
            _module.AddDecoration(
                variable,
                SpirvDecoration.BuiltIn,
                (uint)builtIn);
            _interfaces.Add(variable);
            return variable;
        }

        private void InitializeDistanceOutput(uint variable, uint count)
        {
            if (variable == 0)
            {
                return;
            }

            var pointerType = _module.TypePointer(SpirvStorageClass.Output, _floatType);
            for (var index = 0u; index < count; index++)
            {
                var pointer = _module.AddInstruction(
                    SpirvOp.AccessChain,
                    pointerType,
                    variable,
                    UInt(index));
                Store(pointer, Float(0f));
            }
        }

        private void EmitAuxPositionExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export)
        {
            var positionIndex = export.Target - 12;
            for (var component = 0u; component < 4; component++)
            {
                if ((export.EnableMask & (1u << (int)component)) == 0)
                {
                    continue;
                }

                var output = DecodePositionExportComponent(
                    _request.PositionExportControl,
                    positionIndex,
                    component);
                if (!output.PointSize &&
                    !output.Layer &&
                    !output.Viewport &&
                    output.ClipDistance == uint.MaxValue &&
                    output.CullDistance == uint.MaxValue)
                {
                    continue;
                }

                var raw = export.Compressed
                    ? Bitcast(
                        _uintType,
                        LoadCompressedExportComponent(instruction, (int)component))
                    : LoadV(instruction.Sources[(int)component].Value);
                if (output.Layer && _layerOutput != 0)
                {
                    StoreConditional(
                        _layerOutput,
                        BitwiseAnd(raw, UInt(0x7ff)),
                        _uintType);
                }
                if (output.Viewport && _viewportIndexOutput != 0)
                {
                    var viewport = _module.AddInstruction(
                        SpirvOp.BitFieldUExtract,
                        _uintType,
                        raw,
                        UInt(16),
                        UInt(4));
                    StoreConditional(_viewportIndexOutput, viewport, _uintType);
                }

                var value = Bitcast(_floatType, raw);
                if (output.PointSize && _pointSizeOutput != 0)
                {
                    StoreConditional(_pointSizeOutput, value, _floatType);
                }
                StoreDistanceConditional(
                    _clipDistanceOutput,
                    output.ClipDistance,
                    value);
                StoreDistanceConditional(
                    _cullDistanceOutput,
                    output.CullDistance,
                    value);
            }
        }

        private void StoreDistanceConditional(
            uint variable,
            uint index,
            uint value)
        {
            if (variable == 0 || index == uint.MaxValue)
            {
                return;
            }

            var pointer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _module.TypePointer(SpirvStorageClass.Output, _floatType),
                variable,
                UInt(index));
            StoreConditional(pointer, value, _floatType);
        }

        private void StoreConditional(uint variable, uint value, uint type)
        {
            var selected = _module.AddInstruction(
                SpirvOp.Select,
                type,
                Load(_boolType, _exec),
                value,
                Load(type, variable));
            Store(variable, selected);
        }

        private uint ConvertPositionToClipSpace(uint position)
        {
            var transform = _request.ClipSpace;
            var components = new uint[4];
            for (var component = 0u; component < 4; component++)
            {
                components[component] = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    position,
                    component);
            }

            components[0] = ConvertClipCoordinate(
                components[0],
                transform.ScaleX,
                transform.OffsetX,
                transform.HalfExtentX);
            components[1] = ConvertClipCoordinate(
                components[1],
                transform.ScaleY,
                transform.OffsetY,
                transform.HalfExtentY);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec4Type,
                components);
        }

        private uint ConvertClipCoordinate(
            uint coordinate,
            float scale,
            float offset,
            float halfExtent)
        {
            var window = _module.AddInstruction(
                SpirvOp.FMul,
                _floatType,
                coordinate,
                Float(scale));
            var biased = _module.AddInstruction(
                SpirvOp.FAdd,
                _floatType,
                window,
                Float(offset));
            var divided = _module.AddInstruction(
                SpirvOp.FDiv,
                _floatType,
                biased,
                Float(halfExtent));
            return _module.AddInstruction(
                SpirvOp.FSub,
                _floatType,
                divided,
                Float(1f));
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
                   _request.Program.Address == address;
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
                   _request.Program.Address == address;
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
                   _request.Program.Address == address;
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
                   _request.Program.Address == address;
        }

        private uint LoadCompressedExportComponent(
            Gen5ShaderInstruction instruction,
            int component,
            byte targetOutputMode = 0)
        {
            if (_enablePackedHalfExportShadow &&
                targetOutputMode != 5 &&
                TryLoadPackedHalfExportComponent(
                    instruction,
                    component,
                    out var shadowValue))
            {
                return shadowValue;
            }

            var packed = LoadV(instruction.Sources[component >> 1].Value);
            var unpacked = Ext(
                targetOutputMode == 5 ? 61u : 62u,
                _vec2Type,
                packed);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private uint LoadCompressedUintExportComponent(
            Gen5ShaderInstruction instruction,
            int component)
        {
            var packed = LoadV(instruction.Sources[component >> 1].Value);
            return _module.AddInstruction(
                SpirvOp.BitFieldUExtract,
                _uintType,
                packed,
                UInt((uint)((component & 1) * 16)),
                UInt(16));
        }

        private bool TryLoadPackedHalfExportComponent(
            Gen5ShaderInstruction exportInstruction,
            int component,
            out uint value)
        {
            value = 0;
            var packedSource = exportInstruction.Sources[component >> 1];
            var tracePackedExport =
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PACKED_EXPORT") == "1" &&
                TraceShaderAddressMatches("SHARPEMU_TRACE_PACKED_EXPORT_ADDRESS");
            if (tracePackedExport)
            {
                Console.Error.WriteLine(
                    $"[AGC][PACKED-EXPORT] exp_pc=0x{exportInstruction.Pc:X} " +
                    $"component={component} source={packedSource.Kind}:" +
                    $"{packedSource.Value}");
                if (component == 0 && exportInstruction.Pc == 0x630)
                {
                    foreach (var decoded in _request.Program.Instructions.Where(
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

            for (var index = _request.Program.Instructions.Count - 1; index >= 0; index--)
            {
                var candidate = _request.Program.Instructions[index];
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
                    _request.Program.Address == 0x0000000500781200ul)
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

        private bool TraceShaderAddressMatches(string environmentVariable)
        {
            var filter = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            var span = filter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                       span,
                       System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var address) &&
                   _request.Program.Address == address;
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
            {
                // The packed memory-offset byte of this buffer, loaded at entry.
                return IAdd(byteAddress, Load(_uintType, RuntimeBufferBiasPointer(binding)));
            }
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

        private uint BufferUlongPointer(int binding, uint qwordAddress) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageUlongPointer,
                _globalBuffers64,
                UInt((uint)binding),
                UInt(0),
                qwordAddress);

        private uint IsBufferUlongInRange(int binding, uint qwordAddress)
        {
            var buffer = _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageUlongBlockPointer,
                _globalBuffers64,
                UInt((uint)binding));
            var length = _module.AddInstruction(
                SpirvOp.ArrayLength,
                _uintType,
                buffer,
                0);
            return _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                qwordAddress,
                length);
        }

        private uint ScalarPointer(uint register)
        {
            if (register >= ScalarRegisterCount)
            {
                throw new InvalidOperationException($"Scalar register s{register} is outside the register file.");
            }

            if (_scalarRegisters != 0)
            {
                return _module.AddInstruction(
                    SpirvOp.AccessChain,
                    _functionUintPointer,
                    _scalarRegisters,
                    UInt(register));
            }

            return RegisterVariable(_scalarRegisterVariables, register, "s");
        }

        private uint RegisterVariable(Dictionary<uint, uint> variables, uint register, string prefix)
        {
            if (!variables.TryGetValue(register, out var variable))
            {
                variable = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, _module.ConstantNull(_uintType));
                _interfaces.Add(variable);
                _module.AddName(variable, $"{prefix}{register}");
                variables.Add(register, variable);
            }

            return variable;
        }

        private void SelectLaneHalf(int half)
        {
            _laneHalf = half;
            if (!_pairWave64)
            {
                return;
            }

            var high = half != 0;
            _vectorRegisters = high ? _vectorRegistersHigh : _vectorRegistersLow;
            _packedHalfRegisters = high
                ? _packedHalfRegistersHigh
                : _packedHalfRegistersLow;
            _vcc = high ? _vccHigh : _vccLow;
            _exec = high ? _execHigh : _execLow;
            if (_scratchLow != 0)
            {
                _scratch = high ? _scratchHigh : _scratchLow;
            }
        }

        private uint RuntimeBufferBiasPointer(int binding) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _runtimeBufferBiases,
                UInt(checked((uint)binding)));

        private uint VectorPointer(uint register)
        {
            if (_vectorRegisters != 0)
            {
                return _module.AddInstruction(
                    SpirvOp.AccessChain,
                    _functionUintPointer,
                    _vectorRegisters,
                    UInt(register));
            }

            if (register >= VectorRegisterCount)
            {
                throw new InvalidOperationException($"Vector register v{register} is outside the register file.");
            }

            return RegisterVariable(_vectorRegisterVariables, register, "v");
        }

        // The V_MOVREL* opcodes address the VGPR file with a register number that
        // is only known at run time (encoded number + M0), so the access chain
        // takes a computed index instead of a constant. The index is masked to
        // the array bounds: SPIR-V leaves an out-of-range Private access chain
        // undefined, and a mask costs nothing next to the surrounding load.
        // Every register a relative access can reach: from the lowest V_MOVREL* base to past the
        // highest register the program names (a multi-dword result may extend beyond its listed
        // first register). An index outside it reads zero and writes nothing.
        private static (uint First, uint Last) DynamicVectorRange(Gen5ShaderProgram program)
        {
            var first = VectorRegisterCount - 1;
            var last = 0u;
            foreach (var instruction in program.Instructions)
            {
                foreach (var operand in instruction.Sources.Concat(instruction.Destinations))
                {
                    if (operand.Kind == Gen5OperandKind.VectorRegister)
                    {
                        last = Math.Max(last, operand.Value);
                        if (instruction.Opcode.StartsWith("VMovrel", StringComparison.Ordinal))
                        {
                            first = Math.Min(first, operand.Value);
                        }
                    }
                }
            }

            last = Math.Min(last + 8, VectorRegisterCount - 1);
            return (Math.Min(first, last), last);
        }

        private uint LoadVDynamic(uint registerIndex)
        {
            var result = UInt(0);
            for (var register = _dynamicVectorRange.First; register <= _dynamicVectorRange.Last; register++)
            {
                result = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, registerIndex, UInt(register)),
                    Load(_uintType, VectorPointer(register)),
                    result);
            }

            return result;
        }

        private void StoreVDynamic(uint registerIndex, uint value)
        {
            // With EXEC known to be all ones the write needs no EXEC test.
            var exec = _execKnownFull ? 0 : Load(_boolType, _exec);
            for (var register = _dynamicVectorRange.First; register <= _dynamicVectorRange.Last; register++)
            {
                var pointer = VectorPointer(register);
                var selected = _module.AddInstruction(SpirvOp.IEqual, _boolType, registerIndex, UInt(register));
                if (exec != 0)
                {
                    selected = _module.AddInstruction(SpirvOp.LogicalAnd, _boolType, exec, selected);
                }
                Store(pointer, _module.AddInstruction(SpirvOp.Select, _uintType, selected, value, Load(_uintType, pointer)));
            }
        }

        private uint PackedHalfPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _functionVec2Pointer,
                PackedHalfRegisters(),
                UInt(register));

        // Declared on first use when Private. AMD's compiler keeps an unused 4 KiB private
        // array as a named .bss global: two stages then fail to link, and the driver copies
        // the NOBITS section as file data and reads past the end of the ELF.
        private uint PackedHalfRegisters()
        {
            if (_packedHalfRegisters != 0)
            {
                return _packedHalfRegisters;
            }

            var arrayType = _module.TypeArray(_vec2Type, VectorRegisterCount);
            _packedHalfRegisters = _module.AddGlobalVariable(
                _module.TypePointer(SpirvStorageClass.Private, arrayType),
                SpirvStorageClass.Private,
                _module.ConstantNull(arrayType));
            if (_pairWave64)
            {
                if (_laneHalf == 0)
                {
                    _packedHalfRegistersLow = _packedHalfRegisters;
                }
                else
                {
                    _packedHalfRegistersHigh = _packedHalfRegisters;
                }
            }
            else
            {
                _packedHalfRegistersLow = _packedHalfRegisters;
            }
            _interfaces.Add(_packedHalfRegisters);
            _module.AddName(
                _packedHalfRegisters,
                _pairWave64 && _laneHalf != 0
                    ? "vgprPackedHalfHigh"
                    : "vgprPackedHalf");
            return _packedHalfRegisters;
        }

        private uint LoadS(uint register) => Load(_uintType, ScalarPointer(register));

        private uint LoadV(uint register) => Load(_uintType, VectorPointer(register));

        private void StoreS(uint register, uint value)
        {
            Store(ScalarPointer(register), value);
            if (register is 106 or 107)
            {
                if (_pairWave64)
                {
                    RefreshWavePredicate(_vccLow, _vccHigh, LoadS64(106));
                }
                else if (_waveLaneCount != 32 || register == 106)
                {
                    Store(_vcc, IsLaneSetInMaskRegisters(106));
                }
            }
            else if (register is 126 or 127)
            {
                if (_pairWave64)
                {
                    RefreshWavePredicate(_execLow, _execHigh, LoadS64(126));
                }
                else if (_waveLaneCount != 32 || register == 126)
                {
                    Store(_exec, IsLaneSetInMaskRegisters(126));
                }
            }
        }

        private void RefreshWavePredicate(uint lowPointer, uint highPointer, uint mask)
        {
            if (!_pairWave64)
            {
                Store(lowPointer, IsWaveMaskActive(mask));
                return;
            }

            var savedHalf = _laneHalf;
            SelectLaneHalf(0);
            Store(lowPointer, IsWaveMaskActive(mask));
            SelectLaneHalf(1);
            Store(highPointer, IsWaveMaskActive(mask));
            SelectLaneHalf(savedHalf);
        }

        // In wave32 a lane mask is its low register alone (VCC_HI and EXEC_HI are ordinary SGPRs),
        // so the lane's bit is tested on one dword instead of a composed 64-bit mask.
        private uint IsLaneSetInMaskRegisters(uint lowRegister)
        {
            if (_waveLaneCount != 32)
            {
                return IsWaveMaskActive(LoadS64(lowRegister));
            }

            var laneBit = _subgroupInvocationIdInput == 0
                ? UInt(1)
                : ShiftLeftLogical(UInt(1), GuestWaveLane());
            return IsNotZero(BitwiseAnd(LoadS(lowRegister), laneBit));
        }

        // SHARPEMU_EXEC_GUARD_ELISION=0 guards every vector register write with EXEC again.
        private static readonly bool ExecGuardElision = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_EXEC_GUARD_ELISION"),
            "0",
            StringComparison.Ordinal);

        private IReadOnlySet<uint>? _execFullPcs;
        private bool _execKnownFull;

        private bool IsExecKnownFull(uint pc)
        {
            if (!ExecGuardElision || !_request.EnableExecGuardElision)
            {
                return false;
            }

            _execFullPcs ??= Ir.Gen5ExecFullAnalysis.Analyze(
                _request.Program,
                wave32: _waveLaneCount == 32);
            return _execFullPcs.Contains(pc);
        }

        private void StoreV(uint register, uint value, bool guardWithExec = true)
        {
            if (guardWithExec && !_execKnownFull)
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
            if (_execKnownFull)
            {
                Store(PackedHalfPointer(register), value);
                return;
            }

            var active = Load(_boolType, _exec);
            if (Environment.GetEnvironmentVariable(
                    "SHARPEMU_FORCE_PACKED_STORE_EXEC_VALUES") == "1" &&
                _request.Program.Address == 0x0000000500781200ul)
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

            if (_flagVariables.Contains(pointer))
            {
                return _module.AddInstruction(
                    SpirvOp.INotEqual,
                    _boolType,
                    _module.AddInstruction(SpirvOp.Load, _uintType, pointer),
                    UInt(0));
            }

            return _module.AddInstruction(SpirvOp.Load, type, pointer);
        }

        private void Store(uint pointer, uint value)
        {
            if (_flagVariables.Contains(pointer))
            {
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    value,
                    UInt(1),
                    UInt(0));
            }

            _module.AddStatement(SpirvOp.Store, pointer, value);
        }

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

        private uint _uintPairType;
        private uint _intPairType;

        private (uint Low, uint High) MultiplyExtended(uint left, uint right, bool signed)
        {
            var elementType = signed ? _intType : _uintType;
            if (signed && _intPairType == 0)
            {
                _intPairType = _module.TypeStruct(_intType, _intType);
            }
            else if (!signed && _uintPairType == 0)
            {
                _uintPairType = _module.TypeStruct(_uintType, _uintType);
            }

            var product = _module.AddInstruction(
                signed ? SpirvOp.SMulExtended : SpirvOp.UMulExtended,
                signed ? _intPairType : _uintPairType,
                left,
                right);
            return (
                _module.AddInstruction(SpirvOp.CompositeExtract, elementType, product, 0),
                _module.AddInstruction(SpirvOp.CompositeExtract, elementType, product, 1));
        }

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

        private uint ShiftRightArithmetic64(uint left, uint right) =>
            Bitcast(
                _ulongType,
                _module.AddInstruction(
                    SpirvOp.ShiftRightArithmetic,
                    _longType,
                    Bitcast(_longType, left),
                    BitwiseAnd64(right, _module.Constant64(_ulongType, 63))));

        // Vulkan requires OpBitCount's Base operand to be a 32-bit integer
        // unless maintenance9 is enabled.  Guest wave masks are 64-bit, so
        // count their halves separately and combine the two legal results.
        private uint BitCount64(uint value)
        {
            var low = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                value);
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(
                    value,
                    _module.Constant64(_ulongType, 32)));
            return IAdd(
                _module.AddInstruction(
                    SpirvOp.BitCount,
                    _uintType,
                    low),
                _module.AddInstruction(
                    SpirvOp.BitCount,
                    _uintType,
                    high));
        }
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
            if (_pairWave64)
            {
                return IAdd(
                    Load(_uintType, _subgroupInvocationIdInput),
                    UInt(checked((uint)_laneHalf * 32)));
            }

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

        // 1.0 when "reference <function> texel" holds, else 0.0. The function is the
        // guest sampler's depth compare field, which uses VkCompareOp's order.
        private uint EmulatedDepthCompare(uint reference, uint texel, int function)
        {
            SpirvOp op;
            switch (function)
            {
                case 0: return Float(0f);
                case 7: return Float(1f);
                case 1: op = SpirvOp.FOrdLessThan; break;
                case 2: op = SpirvOp.FOrdEqual; break;
                case 3: op = SpirvOp.FOrdLessThanEqual; break;
                case 4: op = SpirvOp.FOrdGreaterThan; break;
                case 5: op = SpirvOp.FUnordNotEqual; break;
                case 6: op = SpirvOp.FOrdGreaterThanEqual; break;
                default: throw new InvalidOperationException($"invalid depth compare function {function}");
            }

            var passed = _module.AddInstruction(op, _boolType, reference, texel);
            return _module.AddInstruction(SpirvOp.Select, _floatType, passed, Float(1f), Float(0f));
        }

        private uint ShuffleLane(uint value, uint lane) =>
            _subgroupInvocationIdInput == 0
                ? value
                : _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType, UInt(3), value, lane);

        // Convert the paired host-wave half into the guest workgroup index.
        private uint GuestLocalInvocationIndex()
        {
            if (!_pairWave64)
            {
                return Load(_uintType, _localInvocationIndexInput);
            }

            var waveBase = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                Load(_uintType, _subgroupIdInput),
                UInt(64));
            return IAdd(
                IAdd(
                    waveBase,
                    Load(_uintType, _subgroupInvocationIdInput)),
                UInt(checked((uint)_laneHalf * 32)));
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
            return _pairWave64 || _emulateWave64
                ? shifted
                : _module.AddInstruction(
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

            if (_emulateWave64)
            {
                var lane = GuestWaveLane();
                var owned = OwnHalfBallot(condition);
                var exchange = BeginWave64Exchange();
                var half = ShiftRightLogical(lane, UInt(5));
                EmitConditional(
                    IsHalfWaveLeader(lane),
                    () => Store(
                        Wave64ExchangePointer(exchange, half),
                        owned));
                EmitWave64Barrier();
                return Pair64(
                    Load(
                        _uintType,
                        Wave64ExchangePointer(exchange, UInt(0))),
                    Load(
                        _uintType,
                        Wave64ExchangePointer(exchange, UInt(1))));
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

        private uint PairedBooleanToWaveMask(uint lowCondition, uint highCondition)
        {
            var lowBallot = _module.AddInstruction(
                SpirvOp.GroupNonUniformBallot,
                _uvec4Type,
                UInt(3),
                lowCondition);
            var highBallot = _module.AddInstruction(
                SpirvOp.GroupNonUniformBallot,
                _uvec4Type,
                UInt(3),
                highCondition);
            var lowWord = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                lowBallot,
                0);
            var highWord = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                highBallot,
                0);
            return BitwiseOr64(
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    lowWord),
                ShiftLeftLogical64(
                    _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        highWord),
                    _module.Constant64(_ulongType, 32)));
        }

        private void StorePairedWaveMask(
            uint register,
            uint lowCondition,
            uint highCondition) =>
            StoreS64(
                register,
                PairedBooleanToWaveMask(lowCondition, highCondition));
        private uint OwnHalfBallot(uint condition)
        {
            var ballot = _module.AddInstruction(
                SpirvOp.GroupNonUniformBallot,
                _uvec4Type,
                UInt(3),
                condition);
            var component = ShiftRightLogical(Load(_uintType, _subgroupInvocationIdInput), UInt(5));
            // Avoid dynamic extraction of subgroup ballot words: on the NVIDIA
            // device tests it produced zero masks. Select the same word explicitly.
            var result = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, ballot, 0);
            for (uint index = 1; index < 4; index++)
            {
                var word = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, ballot, index);
                result = _module.AddInstruction(SpirvOp.Select, _uintType,
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, component, UInt(index)), word, result);
            }
            return result;
        }

        private uint BooleanToHalfWaveMask(uint condition)
        {
            var owned = _module.AddInstruction(SpirvOp.UConvert, _ulongType, OwnHalfBallot(condition));
            return _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                _module.AddInstruction(SpirvOp.UGreaterThanEqual, _boolType, GuestWaveLane(), UInt(32)),
                ShiftLeftLogical64(owned, _module.Constant64(_ulongType, 32)),
                owned);
        }

        private uint BothHalvesOfOwnBallot(uint condition)
        {
            var owned = _module.AddInstruction(SpirvOp.UConvert, _ulongType, OwnHalfBallot(condition));
            return BitwiseOr64(owned, ShiftLeftLogical64(owned, _module.Constant64(_ulongType, 32)));
        }

        private uint IsHalfWaveLeader(uint lane) =>
            _module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(lane, UInt(31)), UInt(0));
        private uint BeginWave64Exchange()
        {
            var parity = Load(_uintType, _wave64ExchangeParity);
            Store(
                _wave64ExchangeParity,
                BitwiseXor(parity, UInt(1)));
            return IAdd(
                UInt(_wave64ExchangeOffset),
                _module.AddInstruction(
                    SpirvOp.IMul,
                    _uintType,
                    parity,
                    UInt(Wave64ExchangeSlotCount)));
        }

        private uint Wave64ExchangePointer(uint exchange, uint slot) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _wave64ExchangeElementPointer,
                _wave64Exchange,
                IAdd(exchange, slot));

        private uint ExchangeWave64Value(uint writer, uint value)
        {
            var exchange = BeginWave64Exchange();
            EmitConditional(
                writer,
                () => Store(
                    Wave64ExchangePointer(exchange, UInt(0)),
                    value));
            EmitWave64Barrier();
            return Load(
                _uintType,
                Wave64ExchangePointer(exchange, UInt(0)));
        }

        private void EmitExactWaveMasks(IReadOnlyList<uint> pairs)
        {
            var lane = GuestWaveLane();
            var upperHalf = ShiftRightLogical(lane, UInt(5));
            var lowerHalf = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                upperHalf,
                UInt(0));
            const int pairsPerExchange = (int)Wave64ExchangeSlotCount / 2;
            for (var start = 0; start < pairs.Count; start += pairsPerExchange)
            {
                var count = Math.Min(pairsPerExchange, pairs.Count - start);
                var owned = new uint[count];
                for (var index = 0; index < count; index++)
                {
                    var pair = pairs[start + index];
                    owned[index] = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        lowerHalf,
                        LoadS(pair),
                        LoadS(pair + 1));
                }

                var exchange = BeginWave64Exchange();
                EmitConditional(IsHalfWaveLeader(lane), () =>
                {
                    for (var index = 0; index < count; index++)
                    {
                        Store(
                            Wave64ExchangePointer(
                                exchange,
                                IAdd(UInt((uint)index * 2), upperHalf)),
                            owned[index]);
                    }
                });
                EmitWave64Barrier();
                for (var index = 0; index < count; index++)
                {
                    var pair = pairs[start + index];
                    StoreS(
                        pair,
                        Load(
                            _uintType,
                            Wave64ExchangePointer(
                                exchange,
                                UInt((uint)index * 2))));
                    StoreS(
                        pair + 1,
                        Load(
                            _uintType,
                            Wave64ExchangePointer(
                                exchange,
                                UInt((uint)index * 2 + 1))));
                }
            }
        }

        private static readonly bool HalfWaveMasks = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_HALF_MASKS"),
            "0",
            StringComparison.Ordinal);

        private Ir.Gen5Wave64HalfMaskPlan? HalfMaskPlan()
        {
            if (_halfMaskPlanBuilt)
            {
                return _halfMaskPlan;
            }

            _halfMaskPlanBuilt = true;
            if (HalfWaveMasks &&
                UsesWave64Exchange() &&
                _subgroupInvocationIdInput != 0)
            {
                var sharedFlatPcs = _request.Memory.Entries
                    .Where(static memory =>
                        memory.AddressSpace is FlatAddressSpace.Shared or
                            FlatAddressSpace.SharedOrPrivate)
                    .Select(static memory => memory.Pc)
                    .ToHashSet();
                _halfMaskPlan = Ir.Gen5Wave64HalfMaskAnalysis.Analyze(
                    _request.Program,
                    sharedFlatPcs);
            }

            return _halfMaskPlan;
        }

        private uint BarrierPhaseActiveWavePointer(uint wave) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _barrierPhaseActiveWavePointer,
                _barrierPhaseActiveWaves,
                wave);

        private uint EmitBarrierPhaseRendezvous()
        {
            if (!_usesBarrierPhases ||
                _subgroupInvocationIdInput == 0 ||
                _subgroupIdInput == 0 ||
                _barrierPhaseActiveWaves == 0)
            {
                throw new InvalidOperationException(
                    "barrier-phase rendezvous requires wave64 subgroup state");
            }

            // Scalar control is uniform inside each physical subgroup used for
            // one guest wave, so lane zero can publish whether that wave still
            // has work.
            // Every slot is rewritten before the first barrier in every phase;
            // no separate initialization or atomic is required.
            var firstLane = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                Load(_uintType, _subgroupInvocationIdInput),
                UInt(0));
            EmitConditional(firstLane, () =>
            {
                var active = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    Load(_boolType, _programActive),
                    UInt(1),
                    UInt(0));
                Store(
                    BarrierPhaseActiveWavePointer(
                        Load(_uintType, _subgroupIdInput)),
                    active);
            });

            EmitWorkgroupBarrier();
            var anyWaveActive = _module.ConstantBool(false);
            for (uint wave = 0; wave < _guestWaveCount; wave++)
            {
                anyWaveActive = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    anyWaveActive,
                    IsNotZero(
                        Load(
                            _uintType,
                            BarrierPhaseActiveWavePointer(UInt(wave)))));
            }

            // Keep a fast subgroup from overwriting its slot for the next
            // phase while another subgroup is still consuming this phase.
            EmitWorkgroupBarrier();
            return anyWaveActive;
        }

        private void EmitWorkgroupBarrier()
        {
            var workgroup = UInt(2);
            _module.AddStatement(
                SpirvOp.ControlBarrier,
                workgroup,
                workgroup,
                UInt(0x108));
        }

        private void EmitWave64Barrier()
        {
            // Paired wave64 maps one guest wave to one 32-lane host subgroup.
            // Its implicit LDS ordering must not wait for unrelated guest
            // waves in the same workgroup, which can be on different scalar
            // control-flow paths. The fallback path spans host subgroups and
            // therefore still needs a workgroup rendezvous.
            var scope = UInt(_pairWave64 ? 3u : 2u);
            _module.AddStatement(
                SpirvOp.ControlBarrier,
                scope,
                scope,
                UInt(0x108));
        }

        // A wave-mask SGPR (VCC/EXEC) consumed as a per-lane predicate — the
        // condition of VCndmask, a VCC/EXEC branch, or the derived _vcc/_exec
        // bool — must be tested at the CURRENT lane's bit, exactly as the
        // hardware does, not as "the 64-bit value is non-zero". The two coincide
        // for comparison results (only the lane's own bit is ever set), so the
        // single-lane path historically used a cheaper whole-word non-zero test.
        // But bitwise-complement wave-mask idioms (S_NOT/S_ORN2/S_ANDN2/S_NAND/
        // S_NOR on a 64-bit mask) set the unused upper 63 bits; a whole-word test
        // then reports "lane active" even when this lane's bit is clear. Unity's
        // PostProcessing NaN killer does exactly this (`anyNaN | ~allFinite`),
        // which made every valid pixel read as NaN and get replaced with 0 —
        // zeroing the whole scene before tonemap. Extract the lane bit always.
        private uint IsWaveMaskActive(uint mask) =>
            IsCurrentLaneSet(mask);

        private uint IsCurrentLaneSet(uint mask) =>
            IsNotZero64(
                _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    mask,
                    CurrentLaneBit()));

        // In wave32 a lane mask fills its register alone and the next one keeps its value.
        // That includes VCC and EXEC: compilers use VCC_HI (s107) as an ordinary SGPR.
        private void StoreWaveMask(uint register, uint condition)
        {
            if (_waveLaneCount == 32)
            {
                StoreS(register, Narrow(BooleanToWaveMask(condition)));
                return;
            }

            if (_emulateWave64 &&
                _subgroupInvocationIdInput != 0 &&
                _emittingPc is { } pc &&
                HalfMaskPlan() is { } plan &&
                plan.HalfMaskWrites.TryGetValue(pc, out var halfRegister) &&
                halfRegister == register)
            {
                StoreS64(register, BooleanToHalfWaveMask(condition));
                return;
            }

            if (!_pairWave64 || !_emittingPairedInstruction)
            {
                StoreS64(register, BooleanToWaveMask(condition));
                return;
            }

            if (_laneHalf == 0)
            {
                if (_hasPendingWaveMask)
                {
                    throw new InvalidOperationException(
                        "paired instruction produced more than one pending wave mask");
                }

                _hasPendingWaveMask = true;
                _pendingWaveMaskRegister = register;
                _pendingWaveMaskCondition = condition;
                return;
            }

            if (!_hasPendingWaveMask || _pendingWaveMaskRegister != register)
            {
                throw new InvalidOperationException(
                    "paired instruction produced mismatched wave-mask writes");
            }

            StorePairedWaveMask(
                register,
                _pendingWaveMaskCondition,
                condition);
            _hasPendingWaveMask = false;
            _pendingWaveMaskRegister = 0;
            _pendingWaveMaskCondition = 0;
        }

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

        private void EmitConditional(uint condition, Action whenTrue, Action whenFalse)
        {
            var trueLabel = _module.AllocateId();
            var falseLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(SpirvOp.BranchConditional, condition, trueLabel, falseLabel);
            _module.AddLabel(trueLabel);
            whenTrue();
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(falseLabel);
            whenFalse();
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        // Only instructions that address LDS memory need the array. ds_swizzle/ds_bpermute move data
        // between lanes and GDS instructions use the global data share, so a graphics stage using
        // just those must not get an 8 KiB zero-initialized per-invocation array: Metal pays for it
        // in compile time (seconds for a large pixel shader) and in private memory per pixel.
        private bool UsesLds() =>
            _request.Program.Instructions.Any(static instruction =>
                instruction.Control is Gen5DataShareControl { Gds: false } &&
                instruction.Opcode is not ("DsSwizzleB32" or "DsBpermuteB32")) ||
            _request.Memory.Entries.Any(static memory =>
                memory.AddressSpace is FlatAddressSpace.Shared or FlatAddressSpace.SharedOrPrivate);

        internal static bool CanPairWave64(
            IReadOnlyList<Gen5ShaderInstruction> instructions) =>
            GetWave64PairingBlockers(instructions).Count == 0;

        // DPP/DPP8 operate inside 16/8-lane rows, and PERMLANE16/X16 only
        // exchanges the two 16-lane rows inside one 32-lane bank. DS_SWIZZLE
        // and DS_BPERMUTE have the same bank-local semantics: the subgroup
        // emitter is deliberately limited to the current 32-lane guest half.
        // They can therefore run independently over the paired low/high VGPR
        // files. READFIRSTLANE is combined explicitly above so the low bank
        // wins whenever it has an active guest lane. The remaining DS
        // operations need a simultaneous 64-lane view or a wave-scoped atomic
        // and cannot yet be split safely.
        internal static IReadOnlyList<string> GetWave64PairingBlockers(
            IReadOnlyList<Gen5ShaderInstruction> instructions) =>
            instructions
                .Where(static instruction => instruction.Opcode is
                    "DsAppend" or
                    "DsConsume" or
                    "DsPermuteB32")
                .Select(static instruction =>
                    $"{instruction.Opcode}@0x{instruction.Pc:X}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        private bool UsesSubgroupShuffle() =>
            _request.Program.Instructions.Any(instruction =>
                instruction.Control is Gen5DppControl or Gen5Dpp8Control ||
                instruction.Opcode is "VPermlane16B32" or "VPermlanex16B32" or "VReadlaneB32" or
                    "DsAppend" or "DsConsume" or "DsSwizzleB32" or "DsBpermuteB32");

        private bool UsesSubgroupBroadcast() =>
            _request.Program.Instructions.Any(instruction =>
                instruction.Opcode == "VReadfirstlaneB32");

        private bool UsesWaveControl() =>
            _request.Program.Instructions.Any(instruction =>
                instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode is "SSubvectorLoopBegin" or "SSubvectorLoopEnd" ||
                instruction.Opcode.StartsWith("SCbranchExec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchVcc", StringComparison.Ordinal) ||
                instruction.Encoding == Gen5ShaderEncoding.Vopc ||
                instruction.Control is Gen5Vop3Control { ScalarDestination: not null } ||
                instruction.Opcode is
                    "VAddcU32" or
                    "VAddCoU32" or
                    "VAddCoCiU32" or
                    "VSubbU32" or
                    "VSubbrevU32" or
                    "VSubCoU32" or
                    "VSubrevCoU32" or
                    "VMadU64U32" ||
                instruction.Sources.Any(IsWaveMaskOperand) ||
                instruction.Destinations.Any(IsWaveMaskOperand));

        private bool UsesSubgroupOperations() =>
            _pairWave64 || _usesBarrierPhases ||
            (_enableGraphicsSubgroupOperations &&
             (UsesSubgroupShuffle() ||
              UsesSubgroupBroadcast() ||
              UsesWaveControl() ||
              _request.Program.Instructions.Any(static instruction =>
                  instruction.Opcode is "VMbcntLoU32B32" or "VMbcntHiU32B32" or "DsWriteAddtidB32" or "DsReadAddtidB32")));

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
            opcode.StartsWith("SCbranch", StringComparison.Ordinal) ||
            opcode is "SSubvectorLoopBegin" or "SSubvectorLoopEnd";

        private bool TryGetAcyclicBarrierBlocks(
            IReadOnlyList<ShaderBlock> blocks,
            out IReadOnlySet<int> barrierBlocks)
        {
            var barriers = new HashSet<int>();
            barrierBlocks = barriers;

            // The opt-in total-step diagnostic deliberately observes every
            // dispatcher iteration. Keep that exact behavior when requested.
            // Dynamic PC changes cannot be proven acyclic from the decoded
            // instruction list and therefore retain the dispatcher as well.
            if (_maxDispatcherSteps > 0 ||
                _request.Program.Instructions.Any(static instruction =>
                    IsIndirectControlFlow(instruction.Opcode)))
            {
                return false;
            }

            var phaseByBlock = new int[blocks.Count];
            var phase = 0;
            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                phaseByBlock[blockIndex] = phase;
                var block = blocks[blockIndex];
                for (var instructionIndex = block.StartIndex;
                     instructionIndex < block.EndIndex;
                     instructionIndex++)
                {
                    var instruction = _request.Program.Instructions[
                        instructionIndex];
                    if (instruction.Opcode != "SBarrier")
                    {
                        continue;
                    }

                    // Static barriers are workgroup operations, and every
                    // barrier must be the unique end of its linear phase.
                    if (_stage is not (Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh) ||
                        instructionIndex != block.EndIndex - 1 ||
                        !barriers.Add(blockIndex))
                    {
                        return false;
                    }

                    phase++;
                }
            }

            var finalPhase = phase;
            for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
            {
                var terminator = _request.Program.Instructions[
                    blocks[blockIndex].EndIndex - 1];
                if (terminator.Opcode == "SEndpgm")
                {
                    // An exit before the last fixed boundary would let some
                    // guest waves skip a barrier. The phase dispatcher is the
                    // only safe lowering for that topology.
                    if (phaseByBlock[blockIndex] != finalPhase)
                    {
                        return false;
                    }

                    continue;
                }

                if (!IsBranch(terminator.Opcode))
                {
                    continue;
                }

                if (!IsStaticDirectBranch(terminator.Opcode))
                {
                    return false;
                }

                if (!TryGetBranchTargetPc(terminator, out var targetPc))
                {
                    return false;
                }

                if (IsExitBranchTarget(_request.Program.Instructions, targetPc))
                {
                    if (phaseByBlock[blockIndex] != finalPhase)
                    {
                        return false;
                    }

                    continue;
                }

                if (!TryFindBlock(blocks, targetPc, out var targetBlock) ||
                    targetBlock <= blockIndex ||
                    phaseByBlock[targetBlock] != phaseByBlock[blockIndex])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsStaticDirectBranch(string opcode) =>
            opcode is
                "SBranch" or
                "SCbranchScc0" or
                "SCbranchScc1" or
                "SCbranchVccz" or
                "SCbranchVccnz" or
                "SCbranchExecz" or
                "SCbranchExecnz" or
                "SCbranchCdbgsys" or
                "SCbranchCdbguser" or
                "SCbranchCdbgsysOrUser" or
                "SCbranchCdbgsysAndUser" or
                "SSubvectorLoopBegin" or
                "SSubvectorLoopEnd";

        private static bool IsIndirectControlFlow(string opcode) =>
            opcode is
                "SSetpcB64" or
                "SSwappcB64" or
                "SCallB64" or
                "SRfeB64" or
                "SCbranchJoin" or
                "SCbranchIFork" or
                "SCbranchGFork";

        private static bool TryGetBranchTargetPc(
            Gen5ShaderInstruction instruction,
            out uint targetPc)
        {
            targetPc = 0;
            var directEncoding = instruction.Encoding == Gen5ShaderEncoding.Sopp ||
                (instruction.Encoding == Gen5ShaderEncoding.Sopk &&
                 instruction.Opcode is "SSubvectorLoopBegin" or "SSubvectorLoopEnd");
            if (!directEncoding || instruction.Words.Count == 0)
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
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            bool splitAfterBarrier = false)
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

                if ((IsBranch(instruction.Opcode) ||
                     instruction.Opcode == "SEndpgm" ||
                     (splitAfterBarrier && instruction.Opcode == "SBarrier")) &&
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
