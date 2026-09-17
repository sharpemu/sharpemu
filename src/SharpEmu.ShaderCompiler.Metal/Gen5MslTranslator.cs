// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

// Compiles shader requests into Metal source with argument-buffer bindings.
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

    private static bool ValidatePixelOutputs(IReadOnlyList<Gen5PixelOutputBinding> outputs, out string error)
    {
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

        return true;
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

        private readonly Gen5MslStage _stage;
        private readonly uint _localSizeX;
        private readonly uint _localSizeY;
        private readonly uint _localSizeZ;
        private readonly uint _waveLaneCount;
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        private readonly bool _usesPixelValidMask;
        private readonly uint _pixelInputEnable;
        private readonly uint _pixelInputAddress;
        private readonly uint[] _pixelInputCntl;
        private readonly SortedSet<uint> _pixelAttributes = [];
        private readonly SortedSet<uint> _vertexOutputs = [];
        private readonly Dictionary<uint, ShaderVertexInput> _vertexInputsByPc = [];
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

                var blocks = BuildBasicBlocks(_request.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                {
                    DeclareLayoutBindings();
                }

                foreach (var instruction in _request.Program.Instructions)
                {
                    _usesLds |= instruction.Control is Gen5DataShareControl { Gds: false };
                    _usesFormatLoads |= IsFormatBufferLoad(instruction.Opcode) ||
                        instruction.Opcode.StartsWith("TBufferStoreFormat", StringComparison.Ordinal);
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

                    foreach (var input in _request.VertexInputs)
                    {
                        if (input.ComponentCount is >= 1 and <= 4)
                        {
                            _vertexInputsByPc.TryAdd(input.Pc, input);
                            foreach (var aliasPc in input.AliasPcs ?? [])
                            {
                                _vertexInputsByPc.TryAdd(aliasPc, input);
                            }
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
                    AttributeCount: _stage switch
                    {
                        Gen5MslStage.Pixel => (uint)_pixelAttributes.Count,
                        Gen5MslStage.Vertex => (uint)_vertexOutputs.Count,
                        _ => 0,
                    },
                    _localSizeX,
                    _localSizeY,
                    _localSizeZ,
                    ArgumentLayout: _argumentLayout);
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
            foreach (var instruction in _request.Program.Instructions)
            {
                if (instruction.Control is Gen5DppControl or Gen5Dpp8Control ||
                    instruction.Opcode is "VPermlane16B32" or "VPermlanex16B32"
                        or "VReadlaneB32" or "VReadfirstlaneB32"
                        or "VMbcntLoU32B32" or "VMbcntHiU32B32"
                        or "DsAppend" or "DsConsume" ||
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

            {
                // A request binds one argument buffer per stage; its lengths replace the uniforms.
                EmitResourcePrelude(source);
                EmitResourceStruct(source);
            }

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
                var attributes = _pixelAttributes.ToArray();
                var locations = Gen5PixelInputMapping.ResolveLocations(
                    _pixelInputCntl,
                    attributes);
                for (var index = 0; index < attributes.Length; index++)
                {
                    var attribute = attributes[index];
                    var cntl = attribute < (uint)_pixelInputCntl.Length
                        ? _pixelInputCntl[attribute]
                        : attribute;
                    var flat = (cntl & 0x400u) != 0 ? ", flat" : string.Empty;
                    source.AppendLine(
                        $"    float4 attr{attribute} [[user(locn{locations[index]}){flat}]];");
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

            {
                EmitResourceParameters(source);
            }

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
                // EXP.VM publishes EXEC as the pixel-valid mask. EXEC can be
                // restored afterward, so only malformed shaders without VM
                // fall back to the final EXEC value.
                source.AppendLine(
                    _usesPixelValidMask
                        ? "    if (!pixel_valid_mask_active)"
                        : "    if (!exec)");
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
            foreach (var (component, format, bytes, bitOffset, bitCount) in Gfx10UnifiedFormat.ComponentLayouts)
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

        private void EmitRegisterFile(StringBuilder source)
        {
            source.AppendLine($"    uint s[{ScalarRegisterFileCount}] = {{}};");
            source.AppendLine($"    uint v[{VectorRegisterFileCount}] = {{}};");
            source.AppendLine("    bool exec = true;");
            if (_usesPixelValidMask)
            {
                source.AppendLine("    bool pixel_valid_mask_active = true;");
            }
            source.AppendLine("    bool vcc = false;");
            source.AppendLine("    bool scc = false;");
            source.AppendLine("    uint pc = 0u;");
            source.AppendLine("    bool active = true;");
            source.AppendLine("    uint steps = 0u;");
        }

        private void EmitInitialState(StringBuilder source)
        {
            {
                EmitLayoutInitialState(source);
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
                    $"    active = (sharpemu_group_id.x * {_localSizeX}u + sharpemu_local_id.x) < {ComputeThreadLimit(0)}");
                source.AppendLine(
                    $"        && (sharpemu_group_id.y * {_localSizeY}u + sharpemu_local_id.y) < {ComputeThreadLimit(1)}");
                source.AppendLine(
                    $"        && (sharpemu_group_id.z * {_localSizeZ}u + sharpemu_local_id.z) < {ComputeThreadLimit(2)};");

                if (_request.ComputeSystemRegisters is { } registers)
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
            var instructions = _request.Program.Instructions;
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
                // The emulator does not expose a shader debug session.
                "SCbranchCdbgsys" or
                "SCbranchCdbguser" or
                "SCbranchCdbgsysOrUser" or
                "SCbranchCdbgsysAndUser" => "false",
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
            {
                return TryEmitLayoutScalarMemory(instruction, control, out error);
            }
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
            if (control.Typed && instruction.Opcode.Contains("D16", StringComparison.Ordinal))
            {
                error = $"unsupported buffer opcode {instruction.Opcode}";
                return false;
            }

            if (_stage == Gen5MslStage.Vertex &&
                _vertexInputsByPc.TryGetValue(instruction.Pc, out var vertexInput))
            {
                return TryEmitVertexInputFetch(control, vertexInput, out error);
            }

            int bindingIndex;
            string stride;
            string descriptorWord3;
            {
                // The dense buffer, its stride and its format come from the specialization.
                if (!TryResolveLayoutBuffer(instruction.Pc, out bindingIndex, out var specialized))
                {
                    error = "missing buffer-memory binding";
                    return false;
                }

                stride = FormatUInt(specialized.PackedStride & 0x3FFF);
                descriptorWord3 = FormatUInt((specialized.DescriptorFormat << 12) | (specialized.DescriptorSwizzle & 0xFFF));
            }

            var scalarOffset = instruction.Sources.Count > 2
                ? SourceExpression(instruction.Sources[2], instruction)
                : "0u";
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
            if (control.Typed &&
                instruction.Opcode.StartsWith("TBufferStore", StringComparison.Ordinal) &&
                TryEmitTypedBufferFormatStore(bindingIndex, address, control, descriptorWord3))
            {
                return true;
            }

            // A typed load converts with the instruction format, a formatted untyped load
            // with the descriptor format and swizzle; raw accesses take the byte path below.
            if (IsFormatBufferLoad(instruction.Opcode))
            {
                if (!control.Typed)
                {
                    EmitBufferFormatLoad(
                        bindingIndex,
                        address,
                        descriptorWord3,
                        control.VectorData,
                        control.DwordCount);
                    return true;
                }

                if (TryEmitTypedBufferFormatLoad(bindingIndex, address, control, descriptorWord3))
                {
                    return true;
                }
            }

            return TryEmitResolvedMemoryAccess(
                instruction.Opcode,
                bindingIndex,
                address,
                control.VectorData,
                control.VectorData,
                control.DwordCount,
                control.Glc,
                out error);
        }

        private void EmitBufferFormatLoad(
            int bindingIndex,
            string byteAddress,
            string descriptorWord3,
            uint vectorData,
            uint componentCount)
        {
            // Format and destination swizzle come from descriptor word 3, a register
            // or a specialization constant; the prelude table decodes the unified format.
            var word3 = Temp("uint", descriptorWord3);
            var entry = Temp("uint", $"sharpemu_gfx10_formats[({word3} >> 12) & 0x7Fu]");
            var dataFormat = Temp("uint", $"{entry} & 0xFFu");
            var numberFormat = Temp("uint", $"({entry} >> 8) & 0xFFu");
            var canonical = new string[4];
            var componentBounds = new string[4];
            for (var component = 0; component < 4; component++)
            {
                var missing = component == 3
                    ? $"sharpemu_format_one({numberFormat})"
                    : "0u";
                canonical[component] = LoadFormatComponent(
                    bindingIndex,
                    byteAddress,
                    dataFormat,
                    numberFormat,
                    component,
                    missing,
                    out componentBounds[component]);
            }

            // Only selected memory components contribute to the shared bounds check.
            var selectors = new string[componentCount];
            var inBounds = Temp("bool", "true");
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = Temp("uint", $"({word3} >> {destination * 3}u) & 7u");
                selectors[destination] = selector;
                Line($"{inBounds} = {inBounds} && ({selector} == 4u ? {componentBounds[0]} : " +
                    $"{selector} == 5u ? {componentBounds[1]} : {selector} == 6u ? {componentBounds[2]} : " +
                    $"{selector} == 7u ? {componentBounds[3]} : true);");
            }

            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = selectors[destination];
                var constant = Temp("uint", $"{selector} == 1u ? sharpemu_format_one({numberFormat}) : 0u");
                StoreVector(
                    vectorData + destination,
                    $"!{inBounds} ? {constant} : " +
                    $"{selector} == 4u ? {canonical[0]} : " +
                    $"{selector} == 5u ? {canonical[1]} : " +
                    $"{selector} == 6u ? {canonical[2]} : " +
                    $"{selector} == 7u ? {canonical[3]} : {constant}");
            }
        }

        // Check the required range as one access, without an overflowing end address.
        private string ElementInBounds(int bindingIndex, string byteAddress, string elementBytes)
        {
            var bytes = BufferBytes(bindingIndex);
            return Temp("bool", $"{elementBytes} <= {bytes} && {byteAddress} <= {bytes} - {elementBytes}");
        }

        // Component i of a typed load comes from memory component i; components the
        // format does not have read as zero. An unbound descriptor reads as zero.
        private bool TryEmitTypedBufferFormatLoad(
            int bindingIndex,
            string byteAddress,
            Gen5BufferMemoryControl control,
            string descriptorWord3)
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
            var accessBytes = Gfx10UnifiedFormat.GetAccessByteSize(dataFormat, control.DwordCount);
            var inBounds = ElementInBounds(bindingIndex, byteAddress, $"{accessBytes}u");
            var valid = Temp(
                "bool",
                $"(({descriptorWord3} >> 12) & 0x7Fu) != 0u && {inBounds}");
            for (uint destination = 0; destination < control.DwordCount; destination++)
            {
                var value = destination < componentCount
                    ? LoadFormatComponent(
                        bindingIndex,
                        byteAddress,
                        $"{dataFormat}u",
                        $"{numberFormat}u",
                        (int)destination,
                        "0u",
                        out _)
                    : "0u";
                StoreVector(control.VectorData + destination, $"{valid} ? {value} : 0u");
            }

            return true;
        }

        // A typed store converts each register with the format's number format and places
        // the bits at the component's offset; all transferred components are stored or dropped.
        private bool TryEmitTypedBufferFormatStore(
            int bindingIndex,
            string byteAddress,
            Gen5BufferMemoryControl control,
            string descriptorWord3)
        {
            if (!Gfx10UnifiedFormat.TryDecode(control.TypedFormat, out var dataFormat, out var numberFormat))
            {
                return false;
            }

            var componentCount = Math.Min(control.DwordCount, Gfx10UnifiedFormat.ComponentCount(dataFormat));
            if (componentCount == 0)
            {
                return false;
            }

            var elementBytes = Gfx10UnifiedFormat.GetAccessByteSize(dataFormat, componentCount);
            var inBounds = ElementInBounds(bindingIndex, byteAddress, $"{elementBytes}u");
            var allowed = Temp(
                "bool",
                $"(({descriptorWord3} >> 12) & 0x7Fu) != 0u && {inBounds}");
            Line($"if (exec && {allowed})");
            Line("{");
            _indent++;
            if (Gfx10UnifiedFormat.HasWholeDwordComponents(dataFormat))
            {
                // Dword components are dword aligned and keep their register bits.
                for (uint component = 0; component < componentCount; component++)
                {
                    Gfx10UnifiedFormat.TryGetComponentLayout(dataFormat, component, out var byteOffset, out _, out _);
                    var address = byteOffset == 0 ? byteAddress : $"({byteAddress} + {byteOffset}u)";
                    Line($"sharpemu_store_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {address}, v[{control.VectorData + component}], 4u);");
                }
            }
            else
            {
                var dwordCount = (elementBytes + 3) / 4;
                var values = new string[4];
                var masks = new uint[4];
                Array.Fill(values, "0u");
                for (uint component = 0; component < componentCount; component++)
                {
                    Gfx10UnifiedFormat.TryGetComponentLayout(dataFormat, component, out var byteOffset, out var bitOffset, out var bitCount);
                    var encoded = Temp(
                        "uint",
                        $"sharpemu_format_encode(v[{control.VectorData + component}], {bitCount}u, {numberFormat}u, {dataFormat}u)");
                    var dword = (int)(byteOffset / 4);
                    var bit = ((byteOffset & 3) * 8) + bitOffset;
                    var placed = bit == 0 ? encoded : $"({encoded} << {bit}u)";
                    values[dword] = values[dword] == "0u" ? placed : $"{values[dword]} | {placed}";
                    masks[dword] |= ((1u << (int)bitCount) - 1) << (int)bit;
                }

                Line(
                    $"sharpemu_store_element(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress}, {dwordCount}u, " +
                    $"uint4({values[0]}, {values[1]}, {values[2]}, {values[3]}), " +
                    $"uint4(0x{masks[0]:X}u, 0x{masks[1]:X}u, 0x{masks[2]:X}u, 0x{masks[3]:X}u));");
            }

            _indent--;
            Line("}");
            return true;
        }

        private string LoadFormatComponent(
            int bindingIndex,
            string byteAddress,
            string dataFormat,
            string numberFormat,
            int component,
            string missing,
            out string componentInBounds)
        {
            var byteOffset = Temp("uint", "0u");
            var bitOffset = Temp("uint", "0u");
            var bitCount = Temp("uint", "0u");
            Line($"sharpemu_format_layout({dataFormat}, {component}u, {byteOffset}, {bitOffset}, {bitCount});");
            var componentBytes = Temp("uint", $"({bitOffset} + {bitCount} + 7u) >> 3u");
            var rangeInBounds = ElementInBounds(bindingIndex, $"({byteAddress} + {byteOffset})", componentBytes);
            componentInBounds = Temp("bool", $"{bitCount} == 0u || {rangeInBounds}");
            var packed = Temp(
                "uint",
                LoadWord(bindingIndex, $"({byteAddress} + {byteOffset})"));
            var raw = Temp(
                "uint",
                $"{bitCount} == 0u ? 0u : extract_bits({packed}, {bitOffset}, {bitCount})");
            return Temp(
                "uint",
                $"{bitCount} == 0u ? {missing} : sharpemu_format_convert({raw}, {bitCount}, {numberFormat}, {dataFormat})");
        }

        private string LdsIndex(string address, uint offsetBytes)
        {
            var wordMask = _stage == Gen5MslStage.Compute ? LdsDwordMask : PrivateLdsDwordCount - 1;
            return offsetBytes == 0
                ? $"((({address}) >> 2) & {wordMask}u)"
                : $"(((({address}) + {offsetBytes}u) >> 2) & {wordMask}u)";
        }

        private bool TryEmitDataShare(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            if (control.Gds)
            {
                {
                    return TryEmitGlobalDataShare(instruction, control, out error);
                }
            }

            void StoreLds(string index, string value)
            {
                // Exec-guarded like every other lane-visible write.
                Line($"if (exec) {{ sharpemu_lds[{index}] = {value}; }}");
            }

            switch (instruction.Opcode)
            {
                case "DsAppend":
                case "DsConsume":
                {
                    if (instruction.Sources.Count < 1 || instruction.Destinations.Count < 1)
                    {
                        error = $"missing {instruction.Opcode} operand";
                        return false;
                    }

                    var offset = control.SingleOffsetBytes;
                    var m0 = Temp("uint", RawSource(instruction, 0));
                    var baseAddress = Temp("uint", $"{m0} >> 16u");
                    var sizeBytes = Temp("uint", $"{m0} & 0xFFFFu");
                    var inBounds = Temp("bool", $"{offset + 3}u < {sizeBytes}");
                    var index = LdsIndex(baseAddress, offset);
                    var destination = instruction.Destinations[0].Value;
                    var operation = instruction.Opcode == "DsAppend" ? "add" : "sub";

                    // Graphics stages use the existing one-lane LDS model.
                    if (_stage != Gen5MslStage.Compute)
                    {
                        var original = Temp("uint", $"sharpemu_lds[{index}]");
                        var assignment = operation == "add" ? "+=" : "-=";
                        Line($"if (exec && {inBounds}) {{ sharpemu_lds[{index}] {assignment} 1u; }}");
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
                        var atomic =
                            $"atomic_fetch_{operation}_explicit((threadgroup atomic_uint*)&sharpemu_lds[{index}], {count}, memory_order_relaxed)";
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
                    Line($"{firstValue} = atomic_fetch_{operation}_explicit((threadgroup atomic_uint*)&sharpemu_lds[{index}], {count}, memory_order_relaxed);");
                    _indent--;
                    Line("}");
                    var result = Temp("uint", $"simd_broadcast({firstValue}, {first})");
                    StoreVector(destination, $"{inBounds} ? {result} : 0u");
                    return true;
                }
                case "DsAddU32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    var value = Temp("uint", RawSource(instruction, 1));
                    Line("if (exec)");
                    Line("{");
                    _indent++;
                    Line($"atomic_fetch_add_explicit((threadgroup atomic_uint*)&sharpemu_lds[{LdsIndex(address, control.SingleOffsetBytes)}], {value}, memory_order_relaxed);");
                    _indent--;
                    Line("}");
                    return true;
                }
                case "DsWriteB32":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    StoreLds(LdsIndex(address, control.SingleOffsetBytes), RawSource(instruction, 1));
                    return true;
                }
                case "DsWriteB64":
                {
                    var address = Temp("uint", RawSource(instruction, 0));
                    var offset = control.SingleOffsetBytes;
                    StoreLds(LdsIndex(address, offset), RawSource(instruction, 1));
                    StoreLds(LdsIndex(address, offset + sizeof(uint)), RawSource(instruction, 2));
                    return true;
                }
                case "DsWriteB96":
                case "DsWriteB128":
                {
                    var dwordCount = instruction.Opcode == "DsWriteB128" ? 4 : 3;
                    var address = Temp("uint", RawSource(instruction, 0));
                    var offset = control.SingleOffsetBytes;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        StoreLds(
                            LdsIndex(address, offset + (uint)(dword * sizeof(uint))),
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
                        $"sharpemu_lds[{LdsIndex(address, control.SingleOffsetBytes)}]");
                    return true;
                }
                case "DsReadB64":
                case "DsReadB96":
                case "DsReadB128":
                {
                    var dwordCount = instruction.Opcode switch { "DsReadB64" => 2, "DsReadB96" => 3, _ => 4 };
                    if (instruction.Destinations.Count < dwordCount)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = Temp("uint", RawSource(instruction, 0));
                    var offset = control.SingleOffsetBytes;
                    for (var dword = 0; dword < dwordCount; dword++)
                    {
                        StoreVector(
                            instruction.Destinations[dword].Value,
                            $"sharpemu_lds[{LdsIndex(address, offset + (uint)(dword * sizeof(uint)))}]");
                    }

                    return true;
                }
                case "DsRead2B64":
                    return TryEmitDataShareReadPair64(instruction, control, out error);
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
            uint sourceVectorRegister,
            uint destinationVectorRegister,
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
                    $"{function}((device atomic_uint*)(b{bindingIndex} + ({byteAddress} >> 2)), v[{sourceVectorRegister}], memory_order_relaxed)");
                if (glc)
                {
                    Line($"v[{destinationVectorRegister}] = {original};");
                }

                _indent--;
                Line("}");
                _indent--;
                Line("}");
                return true;
            }

            if (opcode.StartsWith("GlobalStore", StringComparison.Ordinal) ||
                opcode.StartsWith("BufferStore", StringComparison.Ordinal) ||
                opcode.StartsWith("TBufferStore", StringComparison.Ordinal))
            {
                Line("if (exec)");
                Line("{");
                _indent++;
                if (TryGetSubdwordStoreInfo(opcode, out var storeBytes, out var sourceShift))
                {
                    var source = sourceShift == 0
                        ? $"v[{sourceVectorRegister}]"
                        : $"(v[{sourceVectorRegister}] >> {sourceShift})";
                    Line($"sharpemu_store_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress}, {source}, {storeBytes}u);");
                }
                else
                {
                    for (uint index = 0; index < dwordCount; index++)
                    {
                        Line($"sharpemu_store_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress} + {index * 4}u, v[{sourceVectorRegister + index}], 4u);");
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
                    StoreVector(destinationVectorRegister, loaded);
                    return true;
                }

                // D16 loads merge into one half of the destination register.
                StoreVector(
                    destinationVectorRegister,
                    d16High
                        ? $"(v[{destinationVectorRegister}] & 0x0000FFFFu) | (({loaded} & 0xFFFFu) << 16)"
                        : $"(v[{destinationVectorRegister}] & 0xFFFF0000u) | ({loaded} & 0xFFFFu)");
                return true;
            }

            if (opcode.StartsWith("GlobalLoad", StringComparison.Ordinal) ||
                opcode.StartsWith("BufferLoad", StringComparison.Ordinal) ||
                opcode.StartsWith("TBufferLoad", StringComparison.Ordinal))
            {
                for (uint index = 0; index < dwordCount; index++)
                {
                    StoreVector(
                        destinationVectorRegister + index,
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
            opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal);

        private string BufferBytes(int bindingIndex) =>
            $"{ResourcesName}.buffer_bytes[{bindingIndex}]";

        private string LoadWord(int bindingIndex, string byteAddress) =>
            $"sharpemu_load_word(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress})";

        private string ApplyByteBias(int bindingIndex, string byteAddress) =>
            $"({byteAddress} + bias[{bindingIndex}])";

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
    }
}
