// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

/// <summary>
/// Gen5 (gfx10) -> Metal Shading Language codegen. Consumes the backend-neutral
/// (Gen5ShaderState, Gen5ShaderEvaluation) contract out of Gen5ShaderTranslator /
/// Gen5ShaderScalarEvaluator and emits MSL source text; the Metal renderer compiles
/// it with MTLLibrary at bind time.
///
/// The execution model mirrors Gen5SpirvTranslator: one GPU invocation is one GCN
/// lane (wave32 — natively the Apple simdgroup width), the register file is typeless
/// 32-bit uints (float ALU bitcasts through as_type&lt;float&gt;), and control flow is a
/// PC-dispatcher loop — a bounded while over a switch of GCN basic blocks — rather
/// than reconstructed structured control flow. EXEC/VCC live in their architectural
/// SGPRs (s106/s107, s126/s127) as raw data, with per-lane bools as synced views.
/// Graphics stages model a single logical wave lane (lane 0, ballots degrade to bit
/// 0, shuffle-family selects resolve to the lane's own value) — the SPIR-V
/// translator's no-subgroup fallback — because Metal leaves simdgroup ops undefined
/// inside the divergent dispatcher loop. Compute threads map one-to-one onto real
/// simdgroup lanes and shuffle for real.
///
/// Wave64: 64-bit masks are carried faithfully as data — every B64 mask op,
/// saveexec, and VCCZ/EXECZ test reads and writes the full register pair. A
/// wave64 guest wave is two 32-wide Apple simdgroups co-resident in one
/// threadgroup; cross-lane ops that span the full 64 lanes (ballots into
/// EXEC/VCC, read-first-lane) rendezvous the two halves through threadgroup
/// scratch with a barrier — the guest's scalar PC keeps all 64 lanes lockstep
/// through the dispatcher, so the barriers are reached uniformly. This mirrors
/// the SPIR-V translator's bridge and shares its scope: the scratch is indexed
/// by half, so it is correct for a one-wave (64-thread) workgroup; readlane
/// across halves stays a 32-wide shuffle (same as the SPIR-V path). Wave-
/// agnostic wave64 kernels translate per-thread unchanged.
///
/// Buffer argument contract (documented for the Metal backend):
///   [[buffer(globalBufferBase + i)]]  global memory binding i, in
///                                     Gen5ShaderEvaluation.GlobalMemoryBindings order
///   [[buffer(uniformsIndex)]]         one SharpEmuUniforms constant buffer holding the
///                                     compute dispatch limit and per-buffer byte
///                                     lengths, where uniformsIndex is
///                                     globalBufferBase + totalGlobalBufferCount
/// </summary>
public static partial class Gen5MslTranslator
{
    private const uint ScalarRegisterFileCount = 128;
    private const uint VectorRegisterFileCount = 256;
    private const uint LdsDwordCount = 8192;
    private const uint LdsDwordMask = LdsDwordCount - 1;
    // Graphics stages model LDS as per-invocation scratch; a full 32 KB array
    // per fragment/vertex invocation risks Metal compile limits, and
    // per-invocation write-then-read correctness only needs deterministic
    // address masking (mirrors the SPIR-V translator's Private-array choice).
    private const uint PrivateLdsDwordCount = 2048;
    private const uint VccLoRegister = 106;
    private const uint VccHiRegister = 107;
    private const uint ExecLoRegister = 126;
    private const uint ExecHiRegister = 127;

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        Gen5PixelOutputKind outputKind,
        out Gen5MslShader shader,
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
        out Gen5MslShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong storageBufferOffsetAlignment = 1)
    {
        shader = default!;
        error = string.Empty;
        if (outputs.Count > 8)
        {
            error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
            return false;
        }

        for (var index = 0; index < outputs.Count; index++)
        {
            if (outputs[index].GuestSlot > 7)
            {
                error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
                return false;
            }

            for (var other = index + 1; other < outputs.Count; other++)
            {
                if (outputs[other].GuestSlot == outputs[index].GuestSlot ||
                    outputs[other].HostLocation == outputs[index].HostLocation)
                {
                    error = "pixel output guest slots and host locations must be unique";
                    return false;
                }
            }
        }

        // Host locations must be dense 0..N-1 so [[color(n)]] attachments match.
        for (uint location = 0; location < outputs.Count; location++)
        {
            var found = false;
            foreach (var output in outputs)
            {
                found |= output.HostLocation == location;
            }

            if (!found)
            {
                error = "pixel output host locations must be dense in the 0..N-1 range";
                return false;
            }
        }

        var context = new CompilationContext(
            Gen5MslStage.Pixel,
            state,
            evaluation,
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            initialScalarBufferIndex,
            waveLaneCount: 32,
            storageBufferOffsetAlignment,
            pixelOutputBindings: outputs,
            imageBindingBase: imageBindingBase,
            pixelInputEnable: pixelInputEnable,
            pixelInputAddress: pixelInputAddress);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out Gen5MslShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int initialScalarBufferIndex = -1,
        int requiredVertexOutputCount = 0,
        ulong storageBufferOffsetAlignment = 1)
    {
        var context = new CompilationContext(
            Gen5MslStage.Vertex,
            state,
            evaluation,
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            initialScalarBufferIndex,
            waveLaneCount: 32,
            storageBufferOffsetAlignment,
            imageBindingBase: imageBindingBase,
            requiredVertexOutputCount: requiredVertexOutputCount);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out Gen5MslShader shader,
        out string error,
        int totalGlobalBufferCount = -1,
        int initialScalarBufferIndex = -1,
        uint waveLaneCount = 32,
        ulong storageBufferOffsetAlignment = 1)
    {
        var context = new CompilationContext(
            Gen5MslStage.Compute,
            state,
            evaluation,
            Math.Max(localSizeX, 1),
            Math.Max(localSizeY, 1),
            Math.Max(localSizeZ, 1),
            globalBufferBase: 0,
            totalGlobalBufferCount,
            initialScalarBufferIndex,
            waveLaneCount,
            storageBufferOffsetAlignment);
        return context.TryCompile(out shader, out error);
    }

    private sealed partial class CompilationContext
    {
        // Safety valve for the PC-dispatcher loop, mirroring the SPIR-V
        // translator: a mistranslated loop-exit condition must terminate the
        // invocation instead of wedging the GPU queue.
        private static readonly int _maxDispatcherSteps =
            int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MAX_STEPS"),
                out var maxSteps) && maxSteps >= 0
                ? maxSteps
                : 100_000;

        private const long InitialScalarDefinition = -1;
        private const long ConflictingScalarDefinition = -2;
        private const long UnreachableScalarDefinition = -3;

        private readonly Gen5MslStage _stage;
        private readonly Gen5ShaderState _state;
        private readonly Gen5ShaderEvaluation _evaluation;
        private readonly uint _localSizeX;
        private readonly uint _localSizeY;
        private readonly uint _localSizeZ;
        private readonly int _globalBufferBase;
        private readonly int _totalGlobalBufferCount;
        private readonly int _initialScalarBufferIndex;
        private readonly uint _waveLaneCount;
        private readonly ulong _storageBufferOffsetAlignment;
        private readonly Dictionary<uint, long[]> _scalarDefinitionsBeforePc = [];
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        private readonly int _imageBindingBase;
        private readonly uint _pixelInputEnable;
        private readonly uint _pixelInputAddress;
        private readonly Dictionary<uint, int> _imageBindingByPc = [];
        private readonly Dictionary<uint, int> _bufferBindingByPc = [];
        private readonly List<(bool IsStorage, string ComponentKind)> _imageKinds = [];
        // Per storage-image binding: whether the body reads it, writes it, or
        // both. Metal caps access::read_write textures at 8 per function, so each
        // binding is declared with the minimal access it actually uses.
        private bool[] _imageBindingReads = [];
        private bool[] _imageBindingWrites = [];
        private readonly SortedSet<uint> _pixelAttributes = [];
        private readonly SortedSet<uint> _vertexOutputs = [];
        private readonly Dictionary<uint, Gen5VertexInputBinding> _vertexInputsByPc = [];
        private readonly int _requiredVertexOutputCount;
        private readonly StringBuilder _body = new();
        private int _indent;
        private int _nextTemp;
        private bool _usesLds;
        private bool _usesFormatLoads;
        private bool _usesWaveScratch;

        /// <summary>True when this stage emulates a 64-lane guest wave across two
        /// 32-wide Apple simdgroups (compute only; graphics stages use the
        /// single-lane model regardless of guest wave size).</summary>
        private bool IsWave64 => _waveLaneCount == 64 && _stage == Gen5MslStage.Compute;

        public CompilationContext(
            Gen5MslStage stage,
            Gen5ShaderState state,
            Gen5ShaderEvaluation evaluation,
            uint localSizeX,
            uint localSizeY,
            uint localSizeZ,
            int globalBufferBase,
            int totalGlobalBufferCount,
            int initialScalarBufferIndex,
            uint waveLaneCount,
            ulong storageBufferOffsetAlignment,
            IReadOnlyList<Gen5PixelOutputBinding>? pixelOutputBindings = null,
            int imageBindingBase = 0,
            uint pixelInputEnable = 0,
            uint pixelInputAddress = 0,
            int requiredVertexOutputCount = 0)
        {
            _pixelOutputBindings = pixelOutputBindings ?? [];
            _imageBindingBase = imageBindingBase;
            _pixelInputEnable = pixelInputEnable;
            _pixelInputAddress = pixelInputAddress;
            _requiredVertexOutputCount = requiredVertexOutputCount;
            _stage = stage;
            _state = state;
            _evaluation = evaluation;
            _localSizeX = localSizeX;
            _localSizeY = localSizeY;
            _localSizeZ = localSizeZ;
            _globalBufferBase = globalBufferBase;
            _totalGlobalBufferCount = totalGlobalBufferCount < 0
                ? evaluation.GlobalMemoryBindings.Count
                : totalGlobalBufferCount;
            _initialScalarBufferIndex = initialScalarBufferIndex;
            _waveLaneCount = waveLaneCount == 64 ? 64u : 32u;
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

        public bool TryCompile(out Gen5MslShader shader, out string error)
        {
            shader = default!;
            error = string.Empty;
            try
            {
                // A 64-lane guest wave that uses cross-lane ops needs the
                // threadgroup-scratch bridge (ballots span both 32-wide halves,
                // read-first-lane broadcasts across them). Programs without such
                // ops are wave-size-agnostic and need no scratch.
                _usesWaveScratch = IsWave64 && UsesWaveSensitiveOperations();

                var blocks = BuildBasicBlocks(_state.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                BuildScalarDefinitionInfo(blocks, _state.Program.Instructions);
                DeclareImageKinds();
                foreach (var instruction in _state.Program.Instructions)
                {
                    _usesLds |= instruction.Control is Gen5DataShareControl { Gds: false };
                    _usesFormatLoads |= IsFormatBufferLoad(instruction.Opcode);
                    if (instruction.Control is Gen5InterpolationControl interpolationControl)
                    {
                        _pixelAttributes.Add(interpolationControl.Attribute);
                    }

                    if (_stage == Gen5MslStage.Vertex &&
                        instruction.Control is Gen5ExportControl { Target: >= 32 and < 64 } vertexExport)
                    {
                        _vertexOutputs.Add(vertexExport.Target - 32);
                    }
                }

                if (_stage == Gen5MslStage.Vertex)
                {
                    // Cover every location the paired fragment shader reads,
                    // even ones this vertex program never exports, so Metal's
                    // exact vertex-out/fragment-in interface match succeeds.
                    // Extras stay zero-filled.
                    for (uint location = 0; location < _requiredVertexOutputCount; location++)
                    {
                        _vertexOutputs.Add(location);
                    }

                    foreach (var input in _evaluation.VertexInputs ?? [])
                    {
                        if (input.ComponentCount is >= 1 and <= 4)
                        {
                            _vertexInputsByPc.TryAdd(input.Pc, input);
                        }
                    }
                }

                // Emit the dispatcher body first: block translation discovers
                // nothing that changes the signature in the compute stage, but
                // keeping the order body-then-wrap matches how the pixel/vertex
                // stages will need it (their IO discovery happens during block
                // translation).
                _indent = 2;
                for (var index = 0; index < blocks.Count; index++)
                {
                    Line($"case {index}u:");
                    Line("{");
                    _indent++;
                    if (!TryEmitBlock(blocks, index, out error))
                    {
                        error = $"block=0x{blocks[index].StartPc:X}: {error}";
                        return false;
                    }

                    _indent--;
                    Line("}");
                    Line("break;");
                }

                var source = new StringBuilder();
                EmitModule(source, blocks.Count);
                shader = new Gen5MslShader(
                    source.ToString(),
                    EntryPointName,
                    _stage,
                    _evaluation.GlobalMemoryBindings,
                    _evaluation.ImageBindings,
                    AttributeCount: _stage switch
                    {
                        Gen5MslStage.Pixel => (uint)_pixelAttributes.Count,
                        Gen5MslStage.Vertex => (uint)_vertexOutputs.Count,
                        _ => 0,
                    },
                    VertexInputs: _stage == Gen5MslStage.Vertex
                        ? _evaluation.VertexInputs ?? []
                        : [],
                    _localSizeX,
                    _localSizeY,
                    _localSizeZ,
                    UniformsBufferIndex: UniformsBufferIndex,
                    ImageBindingBase: _imageBindingBase,
                    SamplerSlots: _samplerSlots,
                    SamplerCount: _samplerCount,
                    SamplerArgBufferIndex: _samplerCount > 0 ? SamplerArgBufferIndex : -1);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        /// <summary>Mirrors the SPIR-V translator's subgroup-usage predicates:
        /// the ops whose results depend on the wave width or on other lanes.
        /// A program without them is wave-size-agnostic.</summary>
        private bool UsesWaveSensitiveOperations()
        {
            foreach (var instruction in _state.Program.Instructions)
            {
                if (instruction.Control is Gen5DppControl or Gen5Dpp8Control ||
                    instruction.Opcode is "VPermlane16B32" or "VPermlanex16B32"
                        or "VReadlaneB32" or "VReadfirstlaneB32"
                        or "VMbcntLoU32B32" or "VMbcntHiU32B32" ||
                    instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                    instruction.Opcode.StartsWith("SCbranchExec", StringComparison.Ordinal) ||
                    instruction.Opcode.StartsWith("SCbranchVcc", StringComparison.Ordinal) ||
                    instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal))
                {
                    return true;
                }

                foreach (var operand in instruction.Sources)
                {
                    if (IsWaveMaskOperand(operand))
                    {
                        return true;
                    }
                }

                foreach (var operand in instruction.Destinations)
                {
                    if (IsWaveMaskOperand(operand))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsWaveMaskOperand(Gen5Operand operand) =>
            operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value is VccLoRegister or VccHiRegister or ExecLoRegister or ExecHiRegister;

        /// <summary>Rewrites the trailing comma of the last emitted parameter
        /// line into the closing parenthesis. Every stage emits each entry
        /// parameter with a trailing comma so optional parameters never need
        /// to know whether they are last.</summary>
        private static void CloseParameterList(StringBuilder source)
        {
            var index = source.Length - 1;
            while (index >= 0 && (source[index] == '\n' || source[index] == '\r'))
            {
                index--;
            }

            if (index >= 0 && source[index] == ',')
            {
                source.Remove(index, source.Length - index);
                source.AppendLine(")");
            }
        }

        private string EntryPointName => _stage switch
        {
            Gen5MslStage.Vertex => "gen5_vs",
            Gen5MslStage.Pixel => "gen5_ps",
            _ => "gen5_cs",
        };

        private int UniformsBufferIndex => _globalBufferBase + _totalGlobalBufferCount;

        private void EmitModule(StringBuilder source, int blockCount)
        {
            source.AppendLine("// Generated by SharpEmu Gen5MslTranslator.");
            source.AppendLine("#include <metal_stdlib>");
            source.AppendLine();
            source.AppendLine("using namespace metal;");
            source.AppendLine();

            // Uniforms: dispatch bounds plus per-buffer byte lengths. Metal has
            // no OpArrayLength equivalent, so buffer extents travel with the
            // dispatch instead of being queried in-shader.
            if (_usesFormatLoads)
            {
                EmitFormatLoadPrelude(source);
            }

            source.AppendLine("struct SharpEmuUniforms");
            source.AppendLine("{");
            source.AppendLine("    uint dispatch_limit_x;");
            source.AppendLine("    uint dispatch_limit_y;");
            source.AppendLine("    uint dispatch_limit_z;");
            source.AppendLine("    uint reserved;");
            source.AppendLine($"    uint buffer_bytes[{Math.Max(_totalGlobalBufferCount, 1)}];");
            source.AppendLine("};");
            source.AppendLine();
            EmitSamplerArgumentBufferStruct(source);
            EmitPrelude(source);
            source.AppendLine();

            if (_stage == Gen5MslStage.Vertex)
            {
                // Stage IO structs: fetched attributes from the evaluated vertex
                // inputs (bound via MTLVertexDescriptor), position plus the
                // param outputs the paired fragment shader reads.
                if (_vertexInputsByPc.Count != 0)
                {
                    source.AppendLine("struct Gen5VsIn");
                    source.AppendLine("{");
                    var declared = new HashSet<uint>();
                    foreach (var input in _vertexInputsByPc.Values)
                    {
                        if (!declared.Add(input.Location))
                        {
                            continue;
                        }

                        var fieldType = input.ComponentCount == 1
                            ? "float"
                            : $"float{input.ComponentCount}";
                        source.AppendLine(
                            $"    {fieldType} in{input.Location} [[attribute({input.Location})]];");
                    }

                    source.AppendLine("};");
                    source.AppendLine();
                }

                source.AppendLine("struct Gen5VsOut");
                source.AppendLine("{");
                source.AppendLine("    float4 sharpemu_position [[position]];");
                foreach (var location in _vertexOutputs)
                {
                    source.AppendLine($"    float4 param{location} [[user(locn{location})]];");
                }

                source.AppendLine("};");
                source.AppendLine();
                source.AppendLine($"vertex Gen5VsOut {EntryPointName}(");
                if (_vertexInputsByPc.Count != 0)
                {
                    source.AppendLine("    Gen5VsIn sharpemu_vin [[stage_in]],");
                }
            }
            else if (_stage == Gen5MslStage.Pixel)
            {
                // Stage IO structs: interpolated attributes discovered from the
                // program's V_INTERP controls, MRT outputs from the bindings.
                source.AppendLine("struct Gen5PsIn");
                source.AppendLine("{");
                source.AppendLine("    float4 sharpemu_frag_coord [[position]];");
                foreach (var attribute in _pixelAttributes)
                {
                    source.AppendLine($"    float4 attr{attribute} [[user(locn{attribute})]];");
                }

                source.AppendLine("};");
                source.AppendLine();
                source.AppendLine("struct Gen5PsOut");
                source.AppendLine("{");
                foreach (var binding in _pixelOutputBindings)
                {
                    var fieldType = binding.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => "uint4",
                        Gen5PixelOutputKind.Sint => "int4",
                        _ => "float4",
                    };
                    source.AppendLine(
                        $"    {fieldType} mrt{binding.GuestSlot} [[color({binding.HostLocation})]];");
                }

                source.AppendLine("};");
                source.AppendLine();
                source.AppendLine($"fragment Gen5PsOut {EntryPointName}(");
                source.AppendLine("    Gen5PsIn sharpemu_in [[stage_in]],");
            }
            else
            {
                source.AppendLine($"kernel void {EntryPointName}(");
            }

            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                source.AppendLine(
                    $"    device uint* b{index} [[buffer({_globalBufferBase + index})]],");
            }

            if (_initialScalarBufferIndex >= 0)
            {
                // The per-dispatch scalar-state buffer sits at its flat slot,
                // past every stage's global bindings; the shader only reads it.
                source.AppendLine(
                    $"    const device uint* b{_initialScalarBufferIndex} " +
                    $"[[buffer({_initialScalarBufferIndex})]],");
            }

            source.AppendLine(
                $"    constant SharpEmuUniforms& sharpemu_uniforms [[buffer({UniformsBufferIndex})]],");
            EmitImageArguments(source);
            if (_stage == Gen5MslStage.Compute)
            {
                source.AppendLine("    uint3 sharpemu_local_id [[thread_position_in_threadgroup]],");
                source.AppendLine("    uint3 sharpemu_group_id [[threadgroup_position_in_grid]],");
            }

            if (_stage == Gen5MslStage.Vertex)
            {
                source.AppendLine("    uint sharpemu_vertex_id [[vertex_id]],");
                source.AppendLine("    uint sharpemu_instance_id [[instance_id]],");
            }
            else if (_stage == Gen5MslStage.Compute && IsWave64)
            {
                // A 64-lane guest wave is two 32-wide Apple simdgroups. Metal
                // packs a threadgroup's threads into simdgroups in ascending
                // thread_index order, so thread_index_in_threadgroup & 63 is the
                // guest lane and its low bit-5 selects the half. Both halves sit
                // in one threadgroup, so a threadgroup_barrier rendezvous bridges
                // the wave for 64-wide ballots (see EmitWave64Ballot).
                source.AppendLine("    uint sharpemu_tg_index [[thread_index_in_threadgroup]],");
            }
            else if (_stage == Gen5MslStage.Compute)
            {
                // Compute threads map one-to-one onto guest lanes, so wave ops
                // address the invocation's real simdgroup lane.
                source.AppendLine("    uint sharpemu_lane [[thread_index_in_simdgroup]],");
            }

            CloseParameterList(source);
            source.AppendLine("{");
            if (_stage == Gen5MslStage.Compute && IsWave64)
            {
                source.AppendLine("    uint sharpemu_lane = sharpemu_tg_index & 63u;");
                if (_usesWaveScratch && !_usesLds)
                {
                    // Two dwords bridge each half's ballot; the third carries a
                    // broadcast value for read-first-lane. Indexed only by half,
                    // so correct for a one-wave (64-thread) workgroup — larger
                    // workgroups would need per-wave scratch (matches the SPIR-V
                    // translator's bridge scope). When the shader also uses LDS the
                    // bridge instead aliases the top of that allocation (below) so
                    // total threadgroup memory stays within Metal's 32 KB limit.
                    source.AppendLine("    threadgroup uint sharpemu_wave_scratch[3];");
                }
            }
            if (_stage != Gen5MslStage.Compute)
            {
                // Graphics stages model a single logical wave lane — the SPIR-V
                // translator's no-subgroup fallback — because Metal leaves
                // simdgroup ops undefined inside the divergent dispatcher loop.
                // Ballots degrade to bit 0 in the prelude and shuffle-family
                // selects resolve to the lane's own value.
                source.AppendLine("    const uint sharpemu_lane = 0u;");
            }
            if (_usesLds)
            {
                if (_stage == Gen5MslStage.Compute)
                {
                    // 32 KB of guest LDS as workgroup-shared memory; the address
                    // is masked into bounds like the SPIR-V translator.
                    source.AppendLine($"    threadgroup uint sharpemu_lds[{LdsDwordCount}];");
                }
                else
                {
                    // Graphics stages model LDS as per-invocation scratch (the
                    // SPIR-V translator's Private-array trick), sized smaller
                    // because only write-then-read correctness is needed.
                    source.AppendLine($"    thread uint sharpemu_lds[{PrivateLdsDwordCount}] = {{}};");
                }
            }

            if (_usesWaveScratch && _usesLds && _stage == Gen5MslStage.Compute)
            {
                // Reuse the final three dwords of the LDS allocation for the
                // wave64 bridge. A separate threadgroup array would push total
                // threadgroup memory past Metal's 32 KB limit for shaders that
                // request the full LDS (mirrors the SPIR-V translator). Guest LDS
                // accesses are bounds-masked into the same allocation, so this
                // trades a rare top-of-LDS collision for a compilable pipeline.
                source.AppendLine(
                    $"    threadgroup uint* sharpemu_wave_scratch = &sharpemu_lds[{LdsDwordCount - 3}u];");
            }

            EmitRegisterFile(source);
            if (_stage == Gen5MslStage.Pixel)
            {
                source.AppendLine("    Gen5PsOut sharpemu_out = {};");
            }
            else if (_stage == Gen5MslStage.Vertex)
            {
                // Zero-initialized: param outputs the program never exports
                // stay (0,0,0,0) to satisfy the fragment interface.
                source.AppendLine("    Gen5VsOut sharpemu_out = {};");
            }

            EmitInitialState(source);
            source.AppendLine();
            source.AppendLine("    while (active)");
            source.AppendLine("    {");
            source.AppendLine("        switch (pc)");
            source.AppendLine("        {");
            source.Append(_body);
            source.AppendLine("        default:");
            source.AppendLine("            active = false;");
            source.AppendLine("            break;");
            source.AppendLine("        }");
            if (_maxDispatcherSteps > 0)
            {
                source.AppendLine($"        if (++steps >= {_maxDispatcherSteps}u)");
                source.AppendLine("        {");
                source.AppendLine("            active = false;");
                source.AppendLine("        }");
            }

            source.AppendLine("    }");
            if (_stage == Gen5MslStage.Pixel)
            {
                // A lane still removed from EXEC when the guest shader exits is
                // a killed fragment; it must not contribute color or blending.
                source.AppendLine("    if (!exec)");
                source.AppendLine("    {");
                source.AppendLine("        discard_fragment();");
                source.AppendLine("    }");
                source.AppendLine("    return sharpemu_out;");
            }
            else if (_stage == Gen5MslStage.Vertex)
            {
                source.AppendLine("    return sharpemu_out;");
            }

            source.AppendLine("}");
        }

        // Shared helpers: MSL allows free functions, so unaligned and subdword
        // access is a byte-pointer cast instead of the manual word-combining
        // the SPIR-V translator inlines at every site. All access is
        // range-checked against the binding's byte length; loads outside the
        // buffer produce zero and stores are dropped, matching the SPIR-V
        // translator's robust-access behavior. The static text lives in
        // Templates/prelude.msl; only the wave-ballot expression varies.
        //
        // Graphics stages model one logical wave lane (lane 0), so a ballot is
        // just that lane's bit: "value ? 1 : 0". A real simd_ballot cannot be
        // used there: the translated program runs inside the divergent
        // while(active){switch(pc)} dispatcher, where Metal leaves cross-lane
        // ops undefined, so lanes at different pc corrupt each other's EXEC/VCC
        // reconstruction and kill whole quads (all fragments discarded). This
        // is the SPIR-V translator's no-subgroup fallback model. Compute
        // mirrors the SPIR-V translator's compute path instead: threads map
        // one-to-one onto real simdgroup lanes and ballots are real, so masks
        // parked in VCC/EXEC hold each lane's actual bit.
        private void EmitPrelude(StringBuilder source) =>
            source.Append(MslTemplates.Render(
                "prelude",
                ("ballot_return", _stage == Gen5MslStage.Compute
                    ? "(uint)(uint64_t)simd_ballot(value)"
                    : "value ? 1u : 0u")));

        // The GFX10 unified-format table is baked from the same authoritative
        // decoder descriptor evaluation uses (dataFormat | numberFormat << 8);
        // the descriptor is read at execution time, so decoding stays dynamic —
        // compiled shaders may be reused with new SRDs. The static conversion
        // functions live in Templates/format_prelude.msl.
        private static void EmitFormatLoadPrelude(StringBuilder source)
        {
            var table = new StringBuilder();
            for (uint format = 0; format < 128; format++)
            {
                Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out var numberFormat);
                if ((format & 15) == 0)
                {
                    if (format != 0)
                    {
                        table.AppendLine();
                    }

                    table.Append("   ");
                }

                table.Append($" 0x{dataFormat | (numberFormat << 8):X}u,");
            }

            var layoutCases = new StringBuilder();
            var first = true;
            foreach (var (component, format, bytes, bitOffset, bitCount) in FormatComponentLayouts())
            {
                if (!first)
                {
                    layoutCases.AppendLine();
                }

                first = false;
                layoutCases.Append(
                    $"    case {component * 16 + format}u: byteOff = {bytes}u; bitOff = {bitOffset}u; bits = {bitCount}u; break;");
            }

            source.AppendLine(MslTemplates.Render(
                "format_prelude",
                ("format_table", table.ToString()),
                ("layout_cases", layoutCases.ToString())));
        }

        /// <summary>
        /// The legacy DATA_FORMAT component layouts the SPIR-V translator encodes
        /// in LoadGfx10BufferFormatComponent, as (component, format, byteOffset,
        /// bitOffset, bitCount) tuples.
        /// </summary>
        private static IEnumerable<(uint Component, uint Format, uint Bytes, uint BitOffset, uint BitCount)> FormatComponentLayouts()
        {
            // Component 0.
            yield return (0, 1, 0, 0, 8);
            yield return (0, 2, 0, 0, 16);
            yield return (0, 3, 0, 0, 8);
            yield return (0, 4, 0, 0, 32);
            yield return (0, 5, 0, 0, 16);
            yield return (0, 6, 0, 0, 10);
            yield return (0, 7, 0, 0, 11);
            yield return (0, 8, 0, 0, 10);
            yield return (0, 9, 0, 0, 2);
            yield return (0, 10, 0, 0, 8);
            yield return (0, 11, 0, 0, 32);
            yield return (0, 12, 0, 0, 16);
            yield return (0, 13, 0, 0, 32);
            yield return (0, 14, 0, 0, 32);
            // Component 1.
            yield return (1, 3, 1, 0, 8);
            yield return (1, 5, 2, 0, 16);
            yield return (1, 6, 0, 10, 11);
            yield return (1, 7, 0, 11, 11);
            yield return (1, 8, 0, 10, 10);
            yield return (1, 9, 0, 2, 10);
            yield return (1, 10, 1, 0, 8);
            yield return (1, 11, 4, 0, 32);
            yield return (1, 12, 2, 0, 16);
            yield return (1, 13, 4, 0, 32);
            yield return (1, 14, 4, 0, 32);
            // Component 2.
            yield return (2, 6, 0, 21, 11);
            yield return (2, 7, 0, 22, 10);
            yield return (2, 8, 0, 20, 10);
            yield return (2, 9, 0, 12, 10);
            yield return (2, 10, 2, 0, 8);
            yield return (2, 12, 4, 0, 16);
            yield return (2, 13, 8, 0, 32);
            yield return (2, 14, 8, 0, 32);
            // Component 3.
            yield return (3, 8, 0, 30, 2);
            yield return (3, 9, 0, 22, 10);
            yield return (3, 10, 3, 0, 8);
            yield return (3, 12, 6, 0, 16);
            yield return (3, 14, 12, 0, 32);
        }

        private void EmitRegisterFile(StringBuilder source)
        {
            source.AppendLine($"    uint s[{ScalarRegisterFileCount}] = {{}};");
            source.AppendLine($"    uint v[{VectorRegisterFileCount}] = {{}};");
            source.AppendLine("    bool exec = true;");
            source.AppendLine("    bool vcc = false;");
            source.AppendLine("    bool scc = false;");
            source.AppendLine("    uint pc = 0u;");
            source.AppendLine("    bool active = true;");
            source.AppendLine("    uint steps = 0u;");
        }

        private void EmitInitialState(StringBuilder source)
        {
            if (_initialScalarBufferIndex >= 0)
            {
                // Initial scalar registers arrive in a per-dispatch buffer so
                // animated user data reuses one translation, mirroring the
                // SPIR-V translator. Word 256+i of the same buffer carries the
                // per-binding byte bias for suballocated guest buffers.
                var consumed = Gen5ShaderTranslator.ComputeConsumedScalarMask(_state.Program);
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterFileCount;
                     index++)
                {
                    if (Gen5ShaderTranslator.IsScalarConsumed(consumed, index))
                    {
                        source.AppendLine(
                            $"    s[{index}] = b{_initialScalarBufferIndex}[{index}];");
                    }
                }

                var biasCount = _globalBufferBase + _evaluation.GlobalMemoryBindings.Count;
                source.AppendLine($"    uint bias[{Math.Max(biasCount, 1)}] = {{}};");
                for (var binding = 0; binding < biasCount; binding++)
                {
                    source.AppendLine(
                        $"    bias[{binding}] = b{_initialScalarBufferIndex}[{256 + binding}];");
                }
            }
            else
            {
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterFileCount;
                     index++)
                {
                    var value = _evaluation.InitialScalarRegisters[(int)index];
                    if (value != 0)
                    {
                        source.AppendLine($"    s[{index}] = 0x{value:X}u;");
                    }
                }

                var biasCount = _globalBufferBase + _evaluation.GlobalMemoryBindings.Count;
                source.AppendLine($"    uint bias[{Math.Max(biasCount, 1)}] = {{}};");
            }

            if (_stage == Gen5MslStage.Compute)
            {
                source.AppendLine("    v[0] = sharpemu_local_id.x;");
                source.AppendLine("    v[1] = sharpemu_local_id.y;");
                source.AppendLine("    v[2] = sharpemu_local_id.z;");

                // Partial-group guard: lanes whose global id falls outside the
                // guest dispatch stay inactive, matching the SPIR-V bounds
                // check driven by the same uniform.
                source.AppendLine(
                    $"    active = (sharpemu_group_id.x * {_localSizeX}u + sharpemu_local_id.x) < sharpemu_uniforms.dispatch_limit_x");
                source.AppendLine(
                    $"        && (sharpemu_group_id.y * {_localSizeY}u + sharpemu_local_id.y) < sharpemu_uniforms.dispatch_limit_y");
                source.AppendLine(
                    $"        && (sharpemu_group_id.z * {_localSizeZ}u + sharpemu_local_id.z) < sharpemu_uniforms.dispatch_limit_z;");

                if (_state.ComputeSystemRegisters is { } registers)
                {
                    EmitComputeSystemRegister(source, registers.WorkGroupXRegister, "sharpemu_group_id.x");
                    EmitComputeSystemRegister(source, registers.WorkGroupYRegister, "sharpemu_group_id.y");
                    EmitComputeSystemRegister(source, registers.WorkGroupZRegister, "sharpemu_group_id.z");
                    if (registers.ThreadGroupSizeRegister is { } sizeRegister &&
                        sizeRegister < ScalarRegisterFileCount)
                    {
                        source.AppendLine(
                            $"    s[{sizeRegister}] = {checked(_localSizeX * _localSizeY * _localSizeZ)}u;");
                    }
                }
            }
            else if (_stage == Gen5MslStage.Pixel)
            {
                EmitPixelInputState(source);
            }
            else if (_stage == Gen5MslStage.Vertex)
            {
                // Hardware-selected VGPRs for the vertex and instance indices.
                source.AppendLine("    v[5] = sharpemu_vertex_id;");
                source.AppendLine("    v[8] = sharpemu_instance_id;");
            }

            // VCC/EXEC live in their architectural SGPRs (see StoreScalar);
            // establish the entry state over whatever the initial-scalar block
            // carried so the register file and the bool views agree from the
            // first instruction.
            source.AppendLine($"    s[{VccLoRegister}] = 0u;");
            source.AppendLine($"    s[{VccHiRegister}] = 0u;");
            if (IsWave64)
            {
                // All 64 lanes are active at entry (before the dispatcher masks
                // any off), and this runs at a uniform point, so the bridge
                // barriers are safe here too.
                EmitBallotStoreAtEntry(source, ExecLoRegister, "true");
            }
            else
            {
                source.AppendLine($"    s[{ExecLoRegister}] = sharpemu_ballot(true);");
                source.AppendLine($"    s[{ExecHiRegister}] = 0u;");
            }
        }

        /// <summary>Entry-time form of <see cref="EmitBallotStore"/> writing to
        /// <paramref name="source"/> at the fixed indent of the module prologue.</summary>
        private void EmitBallotStoreAtEntry(StringBuilder source, uint loRegister, string condition)
        {
            if (!_usesWaveScratch)
            {
                // No cross-lane ops: the high half stays zero and the low half
                // is this simdgroup's ballot, matching the wave-agnostic path.
                source.AppendLine($"    s[{loRegister}] = sharpemu_ballot({condition});");
                source.AppendLine($"    s[{loRegister + 1}] = 0u;");
                return;
            }

            source.AppendLine(
                $"    sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot({condition});");
            source.AppendLine("    threadgroup_barrier(mem_flags::mem_threadgroup);");
            source.AppendLine($"    s[{loRegister}] = sharpemu_wave_scratch[0];");
            source.AppendLine($"    s[{loRegister + 1}] = sharpemu_wave_scratch[1];");
            source.AppendLine("    threadgroup_barrier(mem_flags::mem_threadgroup);");
        }

        private static void EmitComputeSystemRegister(
            StringBuilder source,
            uint? scalarRegister,
            string expression)
        {
            if (scalarRegister is { } register && register < ScalarRegisterFileCount)
            {
                source.AppendLine($"    s[{register}] = {expression};");
            }
        }

        // ---- dispatcher blocks ----

        private bool TryEmitBlock(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            out string error)
        {
            error = string.Empty;
            var block = blocks[blockIndex];
            var instructions = _state.Program.Instructions;
            for (var index = block.StartIndex; index < block.EndIndex; index++)
            {
                var instruction = instructions[index];
                var isTerminator = index == block.EndIndex - 1;
                if (instruction.Opcode == "SEndpgm")
                {
                    Line("active = false;");
                    return true;
                }

                if (instruction.Opcode == "SBranch")
                {
                    // A branch to (or past) the program's end is an exit — the
                    // pattern sprite alpha-kill shaders use to skip their tail.
                    if (IsExitBranchTarget(instructions, instruction))
                    {
                        Line("active = false;");
                        return true;
                    }

                    if (!TryGetBranchTargetBlock(blocks, instruction, out var target))
                    {
                        error = $"branch target outside program at pc=0x{instruction.Pc:X}";
                        return false;
                    }

                    Line($"pc = {target}u;");
                    return true;
                }

                if (instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
                {
                    if (!TryGetBranchCondition(instruction.Opcode, out var condition))
                    {
                        error = $"unsupported conditional branch {instruction.Opcode}";
                        return false;
                    }

                    var fallthrough = blockIndex + 1;
                    if (IsExitBranchTarget(instructions, instruction))
                    {
                        // Taken → exit; not taken → fall through (or exit when
                        // this is the last block anyway).
                        if (fallthrough >= blocks.Count)
                        {
                            Line("active = false;");
                        }
                        else
                        {
                            Line($"pc = {fallthrough}u;");
                            Line($"active = !({condition});");
                        }

                        return true;
                    }

                    if (!TryGetBranchTargetBlock(blocks, instruction, out var target))
                    {
                        error = $"branch target outside program at pc=0x{instruction.Pc:X}";
                        return false;
                    }

                    if (fallthrough >= blocks.Count)
                    {
                        Line($"pc = ({condition}) ? {target}u : 0xFFFFFFFFu;");
                        Line($"active = ({condition});");
                    }
                    else
                    {
                        Line($"pc = ({condition}) ? {target}u : {fallthrough}u;");
                    }

                    return true;
                }

                if (!TryEmitInstruction(instruction, out error))
                {
                    error = $"pc=0x{instruction.Pc:X4} {instruction.Opcode}: {error}";
                    return false;
                }

                if (isTerminator)
                {
                    // Fall through to the next block (or exit at program end).
                    if (blockIndex + 1 < blocks.Count)
                    {
                        Line($"pc = {blockIndex + 1}u;");
                    }
                    else
                    {
                        Line("active = false;");
                    }
                }
            }

            return true;
        }

        private bool TryGetBranchCondition(string opcode, out string condition)
        {
            condition = opcode switch
            {
                "SCbranchScc0" => "!scc",
                "SCbranchScc1" => "scc",
                // VCCZ/EXECZ test the full architectural register pair, which
                // also covers programs that parked plain data in VCC.
                "SCbranchVccz" => $"(s[{VccLoRegister}] | s[{VccHiRegister}]) == 0u",
                "SCbranchVccnz" => $"(s[{VccLoRegister}] | s[{VccHiRegister}]) != 0u",
                "SCbranchExecz" => $"(s[{ExecLoRegister}] | s[{ExecHiRegister}]) == 0u",
                "SCbranchExecnz" => $"(s[{ExecLoRegister}] | s[{ExecHiRegister}]) != 0u",
                _ => string.Empty,
            };
            return condition.Length != 0;
        }

        private static bool TryGetBranchTargetBlock(
            IReadOnlyList<ShaderBlock> blocks,
            Gen5ShaderInstruction instruction,
            out int block)
        {
            block = -1;
            return TryGetBranchTargetPc(instruction, out var targetPc) &&
                TryFindBlock(blocks, targetPc, out block);
        }

        /// <summary>True when the branch lands at or past the last instruction's
        /// end — an exit, matching the SPIR-V translator's handling.</summary>
        private static bool IsExitBranchTarget(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            Gen5ShaderInstruction instruction)
        {
            if (instructions.Count == 0 ||
                !TryGetBranchTargetPc(instruction, out var targetPc))
            {
                return false;
            }

            var last = instructions[^1];
            var lastEndPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
            return targetPc >= lastEndPc;
        }

        // ---- instruction dispatch ----

        private bool TryEmitInstruction(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            switch (instruction.Opcode)
            {
                case "SNop":
                case "SWaitcnt":
                case "SInstPrefetch":
                case "STtraceData":
                case "SClause":
                case "VNop":
                // NGG shaders bracket their exports with s_sendmsg
                // (GS_ALLOC_REQ/DEALLOC) to reserve hardware export space;
                // exports are translated directly, so the message is moot.
                case "SSendmsg":
                    return true;
                case "SBarrier":
                    Line("threadgroup_barrier(mem_flags::mem_threadgroup | mem_flags::mem_device);");
                    return true;
            }

            if (instruction.Control is Gen5ImageControl imageControl)
            {
                return TryEmitImage(instruction, imageControl, out error);
            }

            if (instruction.Control is Gen5ExportControl exportControl)
            {
                return TryEmitExport(instruction, exportControl, out error);
            }

            if (instruction.Control is Gen5InterpolationControl interpolationControl)
            {
                return TryEmitInterpolation(instruction, interpolationControl, out error);
            }

            if (instruction.Control is Gen5DataShareControl dataShare)
            {
                return TryEmitDataShare(instruction, dataShare, out error);
            }

            if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
            {
                return TryEmitScalarMemory(instruction, scalarMemory, out error);
            }

            if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                return TryEmitGlobalMemory(instruction, globalMemory, out error);
            }

            if (instruction.Control is Gen5BufferMemoryControl bufferMemory)
            {
                return TryEmitBufferMemory(instruction, bufferMemory, out error);
            }

            if (instruction.Opcode.StartsWith("V", StringComparison.Ordinal))
            {
                return TryEmitVectorAlu(instruction, out error);
            }

            if (instruction.Opcode.StartsWith("S", StringComparison.Ordinal))
            {
                return TryEmitScalarAlu(instruction, out error);
            }

            error = "unsupported instruction";
            return false;
        }

        // ---- memory ----

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
                        StoreScalar(destination.Value, "0u");
                    }
                }

                return true;
            }

            var offset = control.DynamicOffsetRegister is { } register
                ? $"(s[{register}] + 0x{unchecked((uint)control.ImmediateOffsetBytes):X}u)"
                : $"0x{unchecked((uint)control.ImmediateOffsetBytes):X}u";
            var address = Temp("uint", ApplyByteBias(bindingIndex, offset));
            for (var index = 0; index < instruction.Destinations.Count; index++)
            {
                var destination = instruction.Destinations[index];
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "invalid scalar-memory destination";
                    return false;
                }

                StoreScalar(
                    destination.Value,
                    LoadWord(bindingIndex, $"({address} + {index * 4}u)"));
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

            var address = Temp(
                "uint",
                ApplyByteBias(
                    bindingIndex,
                    $"(v[{control.VectorAddress}] + 0x{unchecked((uint)control.OffsetBytes):X}u)"));
            return TryEmitResolvedMemoryAccess(
                instruction.Opcode,
                bindingIndex,
                address,
                control.VectorData,
                control.DwordCount,
                control.Glc,
                out error);
        }

        private bool TryEmitBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_stage == Gen5MslStage.Vertex &&
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
                ? SourceExpression(instruction.Sources[2], instruction)
                : "0u";
            var stride = $"((s[{control.ScalarResource + 1}] >> 16) & 0x3FFFu)";
            var vectorIndex = control.IndexEnabled
                ? $"v[{control.VectorAddress}]"
                : "0u";
            var vectorOffset = control.OffsetEnabled
                ? $"v[{control.VectorAddress + (control.IndexEnabled ? 1u : 0u)}]"
                : "0u";
            var address = Temp(
                "uint",
                ApplyByteBias(
                    bindingIndex,
                    $"(0x{unchecked((uint)control.OffsetBytes):X}u + {scalarOffset} + {vectorOffset} + ({vectorIndex} * {stride}))"));
            // Typed MUBUF/MTBUF loads convert through the descriptor's unified
            // format; raw dword loads and every store take the byte path below
            // (format stores write raw dwords, matching the SPIR-V translator).
            if (IsFormatBufferLoad(instruction.Opcode) &&
                !instruction.Opcode.StartsWith("BufferStore", StringComparison.Ordinal))
            {
                EmitBufferFormatLoad(
                    bindingIndex,
                    address,
                    control.ScalarResource,
                    control.VectorData,
                    control.DwordCount);
                return true;
            }

            return TryEmitResolvedMemoryAccess(
                instruction.Opcode,
                bindingIndex,
                address,
                control.VectorData,
                control.DwordCount,
                control.Glc,
                out error);
        }

        private void EmitBufferFormatLoad(
            int bindingIndex,
            string byteAddress,
            uint scalarResource,
            uint vectorData,
            uint componentCount)
        {
            // Format and destination swizzle come from descriptor word 3 at
            // execution time; the prelude table decodes the unified format the
            // same way descriptor evaluation does.
            var word3 = Temp("uint", ScalarExpression(scalarResource + 3));
            var entry = Temp("uint", $"sharpemu_gfx10_formats[({word3} >> 12) & 0x7Fu]");
            var dataFormat = Temp("uint", $"{entry} & 0xFFu");
            var numberFormat = Temp("uint", $"({entry} >> 8) & 0xFFu");
            var canonical = new string[4];
            for (var component = 0; component < 4; component++)
            {
                var byteOff = Temp("uint", "0u");
                var bitOff = Temp("uint", "0u");
                var bits = Temp("uint", "0u");
                Line($"sharpemu_format_layout({dataFormat}, {component}u, {byteOff}, {bitOff}, {bits});");
                var packed = Temp(
                    "uint",
                    LoadWord(bindingIndex, $"({byteAddress} + {byteOff})"));
                var raw = Temp(
                    "uint",
                    $"{bits} == 0u ? 0u : extract_bits({packed}, {bitOff}, {bits})");
                var missing = component == 3
                    ? $"sharpemu_format_one({numberFormat})"
                    : "0u";
                canonical[component] = Temp(
                    "uint",
                    $"{bits} == 0u ? {missing} : sharpemu_format_convert({raw}, {bits}, {numberFormat}, {dataFormat})");
            }

            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = Temp("uint", $"({word3} >> {destination * 3}u) & 7u");
                StoreVector(
                    vectorData + destination,
                    $"{selector} == 1u ? sharpemu_format_one({numberFormat}) : " +
                    $"{selector} == 4u ? {canonical[0]} : " +
                    $"{selector} == 5u ? {canonical[1]} : " +
                    $"{selector} == 6u ? {canonical[2]} : " +
                    $"{selector} == 7u ? {canonical[3]} : 0u");
            }
        }

        private bool TryEmitDataShare(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            if (control.Gds)
            {
                error = "GDS data share is not implemented";
                return false;
            }

            var ldsMask = _stage == Gen5MslStage.Compute
                ? LdsDwordMask
                : PrivateLdsDwordCount - 1;
            string LdsIndex(string address, uint offsetBytes) =>
                offsetBytes == 0
                    ? $"((({address}) >> 2) & {ldsMask}u)"
                    : $"(((({address}) + {offsetBytes}u) >> 2) & {ldsMask}u)";

            void StoreLds(string index, string value)
            {
                // Exec-guarded like every other lane-visible write.
                Line($"if (exec) {{ sharpemu_lds[{index}] = {value}; }}");
            }

            switch (instruction.Opcode)
            {
                case "DsAddU32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    var value = Temp("uint", RawSource(instruction, 1));
                    Line("if (exec)");
                    Line("{");
                    _indent++;
                    Line($"atomic_fetch_add_explicit((threadgroup atomic_uint*)&sharpemu_lds[{LdsIndex(address, control.Offset0)}], {value}, memory_order_relaxed);");
                    _indent--;
                    Line("}");
                    return true;
                }
                case "DsWriteB32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    StoreLds(LdsIndex(address, control.Offset0), RawSource(instruction, 1));
                    return true;
                }
                case "DsWriteB64":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    StoreLds(LdsIndex(address, control.Offset0), RawSource(instruction, 1));
                    StoreLds(LdsIndex(address, control.Offset0 + sizeof(uint)), RawSource(instruction, 2));
                    return true;
                }
                case "DsWriteB96":
                case "DsWriteB128":
                {
                    var dwordCount = instruction.Opcode == "DsWriteB128" ? 4 : 3;
                    var address = Temp("uint", RawSource(instruction, 0));
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        StoreLds(
                            LdsIndex(address, control.Offset0 + (uint)(dword * sizeof(uint))),
                            RawSource(instruction, 1 + dword));
                    }

                    return true;
                }
                case "DsWrite2B32":
                case "DsWrite2St64B32":
                {
                    var st64 = instruction.Opcode == "DsWrite2St64B32";
                    var address = Temp("uint", RawSource(instruction, 0));
                    StoreLds(
                        LdsIndex(address, EffectiveDsPairOffsetBytes(control.Offset0, st64)),
                        RawSource(instruction, 1));
                    StoreLds(
                        LdsIndex(address, EffectiveDsPairOffsetBytes(control.Offset1, st64)),
                        RawSource(instruction, 2));
                    return true;
                }
                case "DsReadB32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    StoreVector(
                        instruction.Destinations[0].Value,
                        $"sharpemu_lds[{LdsIndex(address, control.Offset0)}]");
                    return true;
                }
                case "DsReadB96":
                case "DsReadB128":
                {
                    var dwordCount = instruction.Opcode == "DsReadB128" ? 4 : 3;
                    if (instruction.Destinations.Count < dwordCount)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = Temp("uint", RawSource(instruction, 0));
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        StoreVector(
                            instruction.Destinations[dword].Value,
                            $"sharpemu_lds[{LdsIndex(address, control.Offset0 + (uint)(dword * sizeof(uint)))}]");
                    }

                    return true;
                }
                case "DsRead2B32":
                case "DsRead2St64B32":
                {
                    if (instruction.Destinations.Count < 2)
                    {
                        error = "missing LDS read2 operand";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsRead2St64B32";
                    var address = Temp("uint", RawSource(instruction, 0));
                    StoreVector(
                        instruction.Destinations[0].Value,
                        $"sharpemu_lds[{LdsIndex(address, EffectiveDsPairOffsetBytes(control.Offset0, st64))}]");
                    StoreVector(
                        instruction.Destinations[1].Value,
                        $"sharpemu_lds[{LdsIndex(address, EffectiveDsPairOffsetBytes(control.Offset1, st64))}]");
                    return true;
                }
                default:
                    error = $"unsupported LDS opcode {instruction.Opcode}";
                    return false;
            }
        }

        private static uint EffectiveDsPairOffsetBytes(uint offset, bool st64) =>
            offset * (st64 ? 256u : sizeof(uint));

        private bool TryEmitResolvedMemoryAccess(
            string opcode,
            int bindingIndex,
            string byteAddress,
            uint vectorData,
            uint dwordCount,
            bool glc,
            out string error)
        {
            error = string.Empty;
            if (opcode is "GlobalAtomicAdd" or "BufferAtomicAdd" or
                "GlobalAtomicUMax" or "BufferAtomicUMax")
            {
                var function = opcode.EndsWith("Add", StringComparison.Ordinal)
                    ? "atomic_fetch_add_explicit"
                    : "atomic_fetch_max_explicit";
                Line("if (exec)");
                Line("{");
                _indent++;
                Line($"if ({byteAddress} + 4u <= {BufferBytes(bindingIndex)} && ({byteAddress} & 3u) == 0u)");
                Line("{");
                _indent++;
                var original = Temp(
                    "uint",
                    $"{function}((device atomic_uint*)(b{bindingIndex} + ({byteAddress} >> 2)), v[{vectorData}], memory_order_relaxed)");
                if (glc)
                {
                    Line($"v[{vectorData}] = {original};");
                }

                _indent--;
                Line("}");
                _indent--;
                Line("}");
                return true;
            }

            if (opcode.StartsWith("GlobalStore", StringComparison.Ordinal) ||
                opcode.StartsWith("BufferStore", StringComparison.Ordinal))
            {
                Line("if (exec)");
                Line("{");
                _indent++;
                if (TryGetSubdwordStoreInfo(opcode, out var storeBytes, out var sourceShift))
                {
                    var source = sourceShift == 0
                        ? $"v[{vectorData}]"
                        : $"(v[{vectorData}] >> {sourceShift})";
                    Line($"sharpemu_store_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress}, {source}, {storeBytes}u);");
                }
                else
                {
                    for (uint index = 0; index < dwordCount; index++)
                    {
                        Line($"sharpemu_store_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress} + {index * 4}u, v[{vectorData + index}], 4u);");
                    }
                }

                _indent--;
                Line("}");
                return true;
            }

            if (TryGetSubdwordLoadInfo(opcode, out var loadBytes, out var signExtend, out var d16, out var d16High))
            {
                var loaded = Temp(
                    "uint",
                    $"sharpemu_load_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress}, {loadBytes}u, {(signExtend ? "true" : "false")})");
                if (!d16)
                {
                    StoreVector(vectorData, loaded);
                    return true;
                }

                // D16 loads merge into one half of the destination register.
                StoreVector(
                    vectorData,
                    d16High
                        ? $"(v[{vectorData}] & 0x0000FFFFu) | (({loaded} & 0xFFFFu) << 16)"
                        : $"(v[{vectorData}] & 0xFFFF0000u) | ({loaded} & 0xFFFFu)");
                return true;
            }

            if (opcode.StartsWith("GlobalLoad", StringComparison.Ordinal) ||
                opcode.StartsWith("BufferLoad", StringComparison.Ordinal))
            {
                for (uint index = 0; index < dwordCount; index++)
                {
                    StoreVector(
                        vectorData + index,
                        LoadWord(bindingIndex, $"({byteAddress} + {index * 4}u)"));
                }

                return true;
            }

            error = $"unsupported memory opcode {opcode}";
            return false;
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
            opcode.StartsWith("TBufferLoad", StringComparison.Ordinal);

        private string BufferBytes(int bindingIndex) =>
            $"sharpemu_uniforms.buffer_bytes[{_globalBufferBase + bindingIndex}]";

        private string LoadWord(int bindingIndex, string byteAddress) =>
            $"sharpemu_load_word(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress})";

        private string ApplyByteBias(int bindingIndex, string byteAddress) =>
            $"({byteAddress} + bias[{_globalBufferBase + bindingIndex}])";

        // ---- binding resolution (ports the SPIR-V dominating-definition scheme) ----

        private bool TryResolveDominatingBufferBinding(
            uint pc,
            uint scalarAddress,
            uint registerCount,
            out int bindingIndex)
        {
            if (_bufferBindingByPc.TryGetValue(pc, out bindingIndex))
            {
                return true;
            }

            var candidates = _evaluation.GlobalMemoryBindings;
            for (var index = 0; index < candidates.Count; index++)
            {
                var binding = candidates[index];
                foreach (var bindingPc in binding.InstructionPcs)
                {
                    if (bindingPc == pc)
                    {
                        bindingIndex = index;
                        _bufferBindingByPc.Add(pc, index);
                        return true;
                    }
                }
            }

            // No direct PC match: accept a binding only when the descriptor
            // registers hold the exact same definitions here as at one of the
            // binding's own access points — the scalar-definition dataflow the
            // SPIR-V translator uses for descriptors shared across sites.
            for (var index = 0; index < candidates.Count; index++)
            {
                var binding = candidates[index];
                if (binding.ScalarAddress != scalarAddress)
                {
                    continue;
                }

                foreach (var candidatePc in binding.InstructionPcs)
                {
                    if (!HasSameScalarDefinitions(candidatePc, pc, scalarAddress, registerCount))
                    {
                        continue;
                    }

                    bindingIndex = index;
                    _bufferBindingByPc.Add(pc, index);
                    return true;
                }
            }

            bindingIndex = -1;
            return false;
        }

        // ---- writer helpers ----

        private void Line(string text)
        {
            for (var index = 0; index < _indent; index++)
            {
                _body.Append("    ");
            }

            _body.AppendLine(text);
        }

        private string Temp(string type, string expression)
        {
            var name = $"t{_nextTemp++}";
            Line($"{type} {name} = {expression};");
            return name;
        }

        // VCC (s106:s107) and EXEC (s126:s127) are architectural SGPRs: programs
        // freely use them as scratch data registers (s_buffer_load into s[106],
        // then v_rcp_f32 of that value is real RDNA2 code). The register file
        // holds their raw 32-bit values as the source of truth; the bools
        // vcc/exec are cached per-lane views kept in sync at every write so
        // control flow stays cheap. Reading them back as data returns the file.
        private void StoreScalar(uint register, string expression)
        {
            switch (register)
            {
                case VccLoRegister:
                {
                    var value = Temp("uint", expression);
                    Line($"s[{VccLoRegister}] = {value};");
                    Line($"vcc = (({value}) >> sharpemu_lane & 1u) != 0u;");
                    return;
                }

                case ExecLoRegister:
                {
                    var value = Temp("uint", expression);
                    Line($"s[{ExecLoRegister}] = {value};");
                    Line($"exec = (({value}) >> sharpemu_lane & 1u) != 0u;");
                    return;
                }

                case VccHiRegister:
                case ExecHiRegister:
                    // Wave32: the high halves carry no lanes, but keep the data.
                    Line($"s[{register}] = {expression};");
                    return;
            }

            if (register < ScalarRegisterFileCount)
            {
                Line($"s[{register}] = {expression};");
            }
        }

        private void StoreVector(uint register, string expression, bool guardWithExec = true)
        {
            if (register >= VectorRegisterFileCount)
            {
                return;
            }

            if (guardWithExec)
            {
                Line($"if (exec) {{ v[{register}] = {expression}; }}");
            }
            else
            {
                Line($"v[{register}] = {expression};");
            }
        }

        private string ScalarExpression(uint register) =>
            register < ScalarRegisterFileCount ? $"s[{register}]" : "0u";

        private string SourceExpression(
            Gen5Operand operand,
            Gen5ShaderInstruction instruction)
        {
            switch (operand.Kind)
            {
                case Gen5OperandKind.ScalarRegister:
                    return ScalarExpression(operand.Value);
                case Gen5OperandKind.VectorRegister:
                    return $"v[{operand.Value}]";
                case Gen5OperandKind.LiteralConstant:
                    return FormatUInt(operand.Value);
                case Gen5OperandKind.EncodedConstant:
                    // 251/252/253 read the VCCZ/EXECZ/SCC status bits as data.
                    if (operand.Value == 251)
                    {
                        return $"((s[{VccLoRegister}] | s[{VccHiRegister}]) == 0u ? 1u : 0u)";
                    }

                    if (operand.Value == 252)
                    {
                        return $"((s[{ExecLoRegister}] | s[{ExecHiRegister}]) == 0u ? 1u : 0u)";
                    }

                    if (operand.Value == 253)
                    {
                        return "(scc ? 1u : 0u)";
                    }

                    if (Gen5InlineConstants.TryDecode(operand.Value, out var constant))
                    {
                        return FormatUInt(constant);
                    }

                    throw new NotSupportedException(
                        $"unsupported encoded constant {operand.Value} in {instruction.Opcode}");
                default:
                    throw new NotSupportedException($"unsupported operand kind {operand.Kind}");
            }
        }

        private static string FormatUInt(uint value) =>
            value <= 9 ? $"{value}u" : $"0x{value.ToString("X", CultureInfo.InvariantCulture)}u";

        private static string AsFloat(string expression) => $"as_type<float>({expression})";

        private static string AsUInt(string expression) => $"as_type<uint>({expression})";

        // ---- basic blocks (ports BuildBasicBlocks from the SPIR-V translator) ----

        private readonly record struct ShaderBlock(
            uint StartPc,
            int StartIndex,
            int EndIndex);

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

            var starts = new List<uint>(leaders.Count);
            foreach (var pc in leaders)
            {
                if (FindInstructionIndex(instructions, pc) >= 0)
                {
                    starts.Add(pc);
                }
            }

            var blocks = new List<ShaderBlock>(starts.Count);
            for (var index = 0; index < starts.Count; index++)
            {
                var startIndex = FindInstructionIndex(instructions, starts[index]);
                var endIndex = index + 1 < starts.Count
                    ? FindInstructionIndex(instructions, starts[index + 1])
                    : instructions.Count;
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    blocks.Add(new ShaderBlock(starts[index], startIndex, endIndex));
                }
            }

            return blocks;
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

        // ---- scalar-definition dataflow (ports BuildScalarDefinitionInfo) ----

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
            var initialDefinitions = new long[ScalarRegisterFileCount];
            Array.Fill(initialDefinitions, InitialScalarDefinition);

            static void MergeDefinitions(
                long[] destination,
                long[] source,
                ref bool hasInput)
            {
                if (!hasInput)
                {
                    Array.Copy(source, destination, (int)ScalarRegisterFileCount);
                    hasInput = true;
                    return;
                }

                for (var register = 0; register < ScalarRegisterFileCount; register++)
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
                            destination.Value < ScalarRegisterFileCount)
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
                    var input = new long[ScalarRegisterFileCount];
                    Array.Fill(input, UnreachableScalarDefinition);
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
                            destination.Value < ScalarRegisterFileCount)
                        {
                            definitions[destination.Value] = instruction.Pc + 1L;
                        }
                    }
                }
            }
        }
    }
}
