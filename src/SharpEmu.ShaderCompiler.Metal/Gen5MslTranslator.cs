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
/// than reconstructed structured control flow. EXEC/VCC live as per-lane bools whose
/// guest-visible mask registers (s106/s107, s126/s127) are materialized with
/// simd_ballot on read and re-derived from the lane bit on write.
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
    private const uint VccLoRegister = 106;
    private const uint VccHiRegister = 107;
    private const uint ExecLoRegister = 126;
    private const uint ExecHiRegister = 127;

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
        private readonly StringBuilder _body = new();
        private int _indent;
        private int _nextTemp;

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
            ulong storageBufferOffsetAlignment)
        {
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
                if (_waveLaneCount != 32)
                {
                    // gfx10 titles run wave32 for compute in the cases the
                    // emulator dispatches today; Apple simdgroups are 32 wide,
                    // so wave64 needs the SPIR-V translator's emulation scheme.
                    // Fail loudly instead of translating with wrong lane math.
                    error = "wave64 compute is not supported by the MSL translator yet";
                    return false;
                }

                var blocks = BuildBasicBlocks(_state.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                BuildScalarDefinitionInfo(blocks, _state.Program.Instructions);

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
                    AttributeCount: 0,
                    VertexInputs: [],
                    _localSizeX,
                    _localSizeY,
                    _localSizeZ);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
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
            source.AppendLine("struct SharpEmuUniforms");
            source.AppendLine("{");
            source.AppendLine("    uint dispatch_limit_x;");
            source.AppendLine("    uint dispatch_limit_y;");
            source.AppendLine("    uint dispatch_limit_z;");
            source.AppendLine("    uint reserved;");
            source.AppendLine($"    uint buffer_bytes[{Math.Max(_totalGlobalBufferCount, 1)}];");
            source.AppendLine("};");
            source.AppendLine();
            EmitPrelude(source);
            source.AppendLine();

            source.AppendLine($"kernel void {EntryPointName}(");
            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                source.AppendLine(
                    $"    device uint* b{index} [[buffer({_globalBufferBase + index})]],");
            }

            source.AppendLine(
                $"    constant SharpEmuUniforms& sharpemu_uniforms [[buffer({UniformsBufferIndex})]],");
            source.AppendLine("    uint3 sharpemu_local_id [[thread_position_in_threadgroup]],");
            source.AppendLine("    uint3 sharpemu_group_id [[threadgroup_position_in_grid]],");
            source.AppendLine("    uint sharpemu_lane [[thread_index_in_simdgroup]])");
            source.AppendLine("{");
            EmitRegisterFile(source);
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
            source.AppendLine("}");
        }

        private static void EmitPrelude(StringBuilder source)
        {
            // Shared helpers: MSL allows free functions, so unaligned and
            // subdword access is a byte-pointer cast instead of the manual
            // word-combining the SPIR-V translator inlines at every site. All
            // access is range-checked against the binding's byte length; loads
            // outside the buffer produce zero and stores are dropped, matching
            // the SPIR-V translator's robust-access behavior.
            source.AppendLine("static inline uint sharpemu_load_word(device uint* b, uint bytes, uint addr)");
            source.AppendLine("{");
            source.AppendLine("    if ((addr & 3u) == 0u)");
            source.AppendLine("    {");
            source.AppendLine("        return addr + 4u <= bytes ? b[addr >> 2] : 0u;");
            source.AppendLine("    }");
            source.AppendLine("    uint value = 0u;");
            source.AppendLine("    device const uchar* p = (device const uchar*)b;");
            source.AppendLine("    for (uint i = 0u; i < 4u; i++)");
            source.AppendLine("    {");
            source.AppendLine("        if (addr + i < bytes)");
            source.AppendLine("        {");
            source.AppendLine("            value |= (uint)p[addr + i] << (i * 8u);");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine("    return value;");
            source.AppendLine("}");
            source.AppendLine();
            source.AppendLine("static inline uint sharpemu_load_bytes(device uint* b, uint bytes, uint addr, uint count, bool signExtend)");
            source.AppendLine("{");
            source.AppendLine("    uint value = 0u;");
            source.AppendLine("    device const uchar* p = (device const uchar*)b;");
            source.AppendLine("    for (uint i = 0u; i < count; i++)");
            source.AppendLine("    {");
            source.AppendLine("        if (addr + i < bytes)");
            source.AppendLine("        {");
            source.AppendLine("            value |= (uint)p[addr + i] << (i * 8u);");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine("    if (signExtend && count < 4u)");
            source.AppendLine("    {");
            source.AppendLine("        uint shift = 32u - (count * 8u);");
            source.AppendLine("        value = (uint)(((int)(value << shift)) >> shift);");
            source.AppendLine("    }");
            source.AppendLine("    return value;");
            source.AppendLine("}");
            source.AppendLine();
            source.AppendLine("static inline void sharpemu_store_bytes(device uint* b, uint bytes, uint addr, uint value, uint count)");
            source.AppendLine("{");
            source.AppendLine("    device uchar* p = (device uchar*)b;");
            source.AppendLine("    for (uint i = 0u; i < count; i++)");
            source.AppendLine("    {");
            source.AppendLine("        if (addr + i < bytes)");
            source.AppendLine("        {");
            source.AppendLine("            p[addr + i] = (uchar)((value >> (i * 8u)) & 0xFFu);");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine("}");
            source.AppendLine();
            source.AppendLine("static inline uint sharpemu_ballot(bool value)");
            source.AppendLine("{");
            source.AppendLine("    return (uint)(uint64_t)simd_ballot(value);");
            source.AppendLine("}");
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

                    if (!TryGetBranchTargetBlock(blocks, instruction, out var target))
                    {
                        error = $"branch target outside program at pc=0x{instruction.Pc:X}";
                        return false;
                    }

                    var fallthrough = blockIndex + 1;
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
                "SCbranchVccz" => "sharpemu_ballot(vcc) == 0u",
                "SCbranchVccnz" => "sharpemu_ballot(vcc) != 0u",
                "SCbranchExecz" => "sharpemu_ballot(exec) == 0u",
                "SCbranchExecnz" => "sharpemu_ballot(exec) != 0u",
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
                    return true;
                case "SBarrier":
                    Line("threadgroup_barrier(mem_flags::mem_threadgroup | mem_flags::mem_device);");
                    return true;
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
            if (!TryResolveDominatingBufferBinding(
                    instruction.Pc,
                    control.ScalarResource,
                    registerCount: 4,
                    out var bindingIndex))
            {
                error = "missing buffer-memory binding";
                return false;
            }

            if (IsFormatBufferLoad(instruction.Opcode) ||
                instruction.Opcode.StartsWith("BufferStoreFormat", StringComparison.Ordinal))
            {
                // Typed MUBUF/MTBUF format conversion arrives with the memory
                // phase that ports EmitBufferFormatLoad.
                error = $"format buffer access {instruction.Opcode} is not translated yet";
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
            return TryEmitResolvedMemoryAccess(
                instruction.Opcode,
                bindingIndex,
                address,
                control.VectorData,
                control.DwordCount,
                control.Glc,
                out error);
        }

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
                if (TryGetSubdwordStoreInfo(opcode, out var storeBytes))
                {
                    Line($"sharpemu_store_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress}, v[{vectorData}], {storeBytes}u);");
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

            if (TryGetSubdwordLoadInfo(opcode, out var loadBytes, out var signExtend))
            {
                StoreVector(
                    vectorData,
                    $"sharpemu_load_bytes(b{bindingIndex}, {BufferBytes(bindingIndex)}, {byteAddress}, {loadBytes}u, {(signExtend ? "true" : "false")})");
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
            out bool signExtend)
        {
            (byteCount, signExtend) = opcode switch
            {
                "GlobalLoadUbyte" or "BufferLoadUbyte" => (1u, false),
                "GlobalLoadSbyte" or "BufferLoadSbyte" => (1u, true),
                "GlobalLoadUshort" or "BufferLoadUshort" => (2u, false),
                "GlobalLoadSshort" or "BufferLoadSshort" => (2u, true),
                _ => (0u, false),
            };
            return byteCount != 0;
        }

        private static bool TryGetSubdwordStoreInfo(string opcode, out uint byteCount)
        {
            byteCount = opcode switch
            {
                "GlobalStoreByte" or "BufferStoreByte" => 1u,
                "GlobalStoreShort" or "BufferStoreShort" => 2u,
                _ => 0u,
            };
            return byteCount != 0;
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
            bindingIndex = -1;
            var candidates = _evaluation.GlobalMemoryBindings;
            for (var index = 0; index < candidates.Count; index++)
            {
                var binding = candidates[index];
                foreach (var bindingPc in binding.InstructionPcs)
                {
                    if (bindingPc == pc)
                    {
                        bindingIndex = index;
                        return true;
                    }
                }
            }

            // No direct PC match: fall back to the most recent binding whose
            // descriptor registers are untouched between its defining load and
            // this instruction (the scalar-definition dataflow the SPIR-V
            // translator uses for shared descriptors).
            if (scalarAddress >= ScalarRegisterFileCount ||
                !_scalarDefinitionsBeforePc.TryGetValue(pc, out var definitions))
            {
                return false;
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                var binding = candidates[index];
                if (binding.ScalarAddress != scalarAddress)
                {
                    continue;
                }

                var consistent = true;
                for (uint register = 0; register < registerCount; register++)
                {
                    var value = scalarAddress + register;
                    if (value >= ScalarRegisterFileCount ||
                        definitions[value] == ConflictingScalarDefinition)
                    {
                        consistent = false;
                        break;
                    }
                }

                if (consistent)
                {
                    bindingIndex = index;
                    return true;
                }
            }

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

        private void StoreScalar(uint register, string expression)
        {
            switch (register)
            {
                case VccLoRegister:
                    Line($"vcc = (({expression}) >> sharpemu_lane & 1u) != 0u;");
                    return;
                case ExecLoRegister:
                    Line($"exec = (({expression}) >> sharpemu_lane & 1u) != 0u;");
                    return;
                case VccHiRegister:
                case ExecHiRegister:
                    // Wave32: the high mask halves hold no lanes.
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

        private string ScalarExpression(uint register) => register switch
        {
            VccLoRegister => "sharpemu_ballot(vcc)",
            ExecLoRegister => "sharpemu_ballot(exec)",
            VccHiRegister or ExecHiRegister => "0u",
            _ when register < ScalarRegisterFileCount => $"s[{register}]",
            _ => "0u",
        };

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
