// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace SharpEmu.ShaderCompiler;

public static class Gen5ShaderScalarEvaluator
{
    // When a scalar POINTER load can't be resolved statically (its descriptor
    // register read back garbage — e.g. 0 or 0xFFFFFFFF, a per-draw descriptor
    // setup race), abort-and-drop-the-draw loses the whole pass. Demon's Souls'
    // deferred-lighting / composite pixel shaders hit this intermittently, so
    // the passes that would produce the composite's feeder targets get dropped
    // and the frame stays black. Degrading instead (feed 0, keep translating,
    // like the buffer-load path already does) lets the pass render with the
    // unresolved resource missing rather than not at all. STRICT reverts.
    private static readonly bool _strictScalarLoad =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_STRICT_SCALAR_LOAD"),
            "1",
            StringComparison.Ordinal);

    // A stale buffer descriptor should not discard an otherwise valid shader
    // pass.  Treat it as an all-zero buffer by default; callers that need
    // strict diagnostics can restore the old failure behaviour explicitly.
    private static readonly bool _strictBufferLoad =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_STRICT_BUFFER_LOAD"),
            "1",
            StringComparison.Ordinal);
    private static readonly object _scalarFallbackTraceGate = new();
    private static readonly HashSet<(ulong Shader, uint Pc)> _tracedScalarFallbacks = [];

    // Uniform forward branches select material/resource bodies that remain
    // statically present in the translated shader. Discover the skipped body's
    // descriptors by default; SHARPEMU_CFG_RESOURCE_DISCOVERY=0 is a diagnostic
    // opt-out. Conditional branches are deliberately not forked because their
    // fall-through is already scanned and forking vector-mask conditions grows
    // exponentially without adding descriptor coverage.
    private static readonly bool _cfgResourceDiscovery =
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_CFG_RESOURCE_DISCOVERY"),
            "0",
            StringComparison.Ordinal);

    /// <summary>
    /// Optional fallback for global-memory reads that ctx.Memory cannot satisfy (the
    /// emulator installs the HLE-tracked libc heap reader here at module load). Kept as
    /// a hook so this project never depends on the HLE module implementations.
    /// </summary>
    public static Gen5FallbackMemoryReader? FallbackMemoryReader { get; set; }

    public delegate bool Gen5FallbackMemoryReader(ulong baseAddress, Span<byte> destination);

    /// <summary>
    /// Pool used for large draw-time guest-memory snapshots. The HLE host installs
    /// its bounded transfer pool; standalone compiler tools use the shared pool.
    /// </summary>
    public static ArrayPool<byte> GlobalMemoryPool { get; set; } = ArrayPool<byte>.Shared;

    private const int ScalarRegisterCount = 256;
    private const int VectorRegisterCount = 256;
    private const uint NullScalarRegister = 125;
    private const int ImageDescriptorDwords = 8;
    private const int SamplerDescriptorDwords = 4;
    private const int MaxGlobalMemoryBindingBytes = 16 * 1024 * 1024;
    public static long GlobalMemoryReadCount;
    public static long GlobalMemoryReadBytes;
    public static long GlobalMemoryReadCacheHits;
    public static long GlobalMemoryReadPvmBytes;
    public static long GlobalMemoryReadLibcBytes;
    public static long GlobalMemoryReadReuses;

    private const ulong RdnaWaveMask = 0xFFFF_FFFFUL;

    static Gen5ShaderScalarEvaluator()
    {
        RunScalarLoadSelfChecks();
    }

    private readonly record struct BufferDescriptor(
        ulong BaseAddress,
        uint Stride,
        uint NumRecords,
        ulong SizeBytes,
        uint NumberFormat,
        uint DataFormat);

    private enum ConditionalBranchResolution
    {
        Fallthrough,
        Taken,
        Unknown,
    }

    private sealed class ScalarDataflowState
    {
        public ScalarDataflowState(
            uint[] registers,
            bool[] knownRegisters,
            bool[] conflictingRegisters,
            uint[] vectorConstants,
            bool[] knownVectorConstants,
            bool[] conflictingVectorConstants,
            ulong execMask,
            bool execMaskKnown,
            bool scalarConditionCode,
            bool scalarConditionCodeKnown)
        {
            Registers = registers;
            KnownRegisters = knownRegisters;
            ConflictingRegisters = conflictingRegisters;
            VectorConstants = vectorConstants;
            KnownVectorConstants = knownVectorConstants;
            ConflictingVectorConstants = conflictingVectorConstants;
            ExecMask = execMask;
            ExecMaskKnown = execMaskKnown;
            ScalarConditionCode = scalarConditionCode;
            ScalarConditionCodeKnown = scalarConditionCodeKnown;
        }

        public uint[] Registers { get; }
        public bool[] KnownRegisters { get; }
        public bool[] ConflictingRegisters { get; }
        public uint[] VectorConstants { get; }
        public bool[] KnownVectorConstants { get; }
        public bool[] ConflictingVectorConstants { get; }
        public ulong ExecMask { get; set; }
        public bool ExecMaskKnown { get; set; }
        public bool ScalarConditionCode { get; set; }
        public bool ScalarConditionCodeKnown { get; set; }

        public ScalarDataflowState Clone() =>
            new(
                (uint[])Registers.Clone(),
                (bool[])KnownRegisters.Clone(),
                (bool[])ConflictingRegisters.Clone(),
                (uint[])VectorConstants.Clone(),
                (bool[])KnownVectorConstants.Clone(),
                (bool[])ConflictingVectorConstants.Clone(),
                ExecMask,
                ExecMaskKnown,
                ScalarConditionCode,
                ScalarConditionCodeKnown);

        public bool Join(ScalarDataflowState incoming)
        {
            var changed = false;
            for (var index = 0; index < Registers.Length; index++)
            {
                if (!KnownRegisters[index])
                {
                    if (incoming.ConflictingRegisters[index] &&
                        !ConflictingRegisters[index])
                    {
                        ConflictingRegisters[index] = true;
                        changed = true;
                    }

                    continue;
                }

                if (!incoming.KnownRegisters[index] ||
                    Registers[index] != incoming.Registers[index])
                {
                    ConflictingRegisters[index] =
                        incoming.ConflictingRegisters[index] ||
                        incoming.KnownRegisters[index];
                    KnownRegisters[index] = false;
                    Registers[index] = 0;
                    changed = true;
                }
            }

            for (var index = 0; index < VectorConstants.Length; index++)
            {
                if (!KnownVectorConstants[index])
                {
                    if (incoming.ConflictingVectorConstants[index] &&
                        !ConflictingVectorConstants[index])
                    {
                        ConflictingVectorConstants[index] = true;
                        changed = true;
                    }

                    continue;
                }

                if (!incoming.KnownVectorConstants[index] ||
                    VectorConstants[index] != incoming.VectorConstants[index])
                {
                    ConflictingVectorConstants[index] =
                        incoming.ConflictingVectorConstants[index] ||
                        incoming.KnownVectorConstants[index];
                    KnownVectorConstants[index] = false;
                    VectorConstants[index] = 0;
                    changed = true;
                }
            }

            if (ExecMaskKnown &&
                (!incoming.ExecMaskKnown || ExecMask != incoming.ExecMask))
            {
                ExecMaskKnown = false;
                ExecMask = 0;
                changed = true;
            }

            if (ScalarConditionCodeKnown &&
                (!incoming.ScalarConditionCodeKnown ||
                 ScalarConditionCode != incoming.ScalarConditionCode))
            {
                ScalarConditionCodeKnown = false;
                ScalarConditionCode = false;
                changed = true;
            }

            return changed;
        }

        public void MarkUnknown(uint firstRegister, uint count)
        {
            var end = Math.Min((ulong)KnownRegisters.Length, (ulong)firstRegister + count);
            for (var index = firstRegister; index < end; index++)
            {
                if (index == NullScalarRegister)
                {
                    continue;
                }

                KnownRegisters[index] = false;
                ConflictingRegisters[index] = false;
                Registers[index] = 0;
            }

            if (firstRegister <= 127 && end > 126)
            {
                ExecMaskKnown = false;
                ExecMask = 0;
            }
        }

        public void MarkKnown(uint firstRegister, uint count)
        {
            var end = Math.Min((ulong)KnownRegisters.Length, (ulong)firstRegister + count);
            for (var index = firstRegister; index < end; index++)
            {
                if (index == NullScalarRegister)
                {
                    continue;
                }

                KnownRegisters[index] = true;
                ConflictingRegisters[index] = false;
            }
        }

        public void MarkVectorUnknown(uint register)
        {
            if (register >= VectorConstants.Length)
            {
                return;
            }

            KnownVectorConstants[register] = false;
            ConflictingVectorConstants[register] = false;
            VectorConstants[register] = 0;
        }

        public void MarkVectorKnown(uint register, uint value)
        {
            if (register >= VectorConstants.Length)
            {
                return;
            }

            KnownVectorConstants[register] = true;
            ConflictingVectorConstants[register] = false;
            VectorConstants[register] = value;
        }
    }

    public static bool TryResolveImageBindings(
        CpuContext ctx,
        Gen5ShaderState state,
        out IReadOnlyList<Gen5ImageBinding> bindings,
        out string error)
    {
        if (TryEvaluate(ctx, state, out var evaluation, out error))
        {
            bindings = evaluation.ImageBindings;
            return true;
        }

        bindings = [];
        return false;
    }

    public static bool TryEvaluate(
        CpuContext ctx,
        Gen5ShaderState state,
        out Gen5ShaderEvaluation evaluation,
        out string error,
        bool resolveVertexInputs = false,
        uint? requiredVertexRecordCount = null)
    {
        evaluation = default!;
        error = string.Empty;
        var scalarRegisters = new uint[ScalarRegisterCount];
        var knownScalarRegisters = new bool[ScalarRegisterCount];
        for (var index = 0;
             index < state.UserData.Count &&
             state.UserDataScalarRegisterBase + (uint)index < scalarRegisters.Length;
             index++)
        {
            var scalarRegister = state.UserDataScalarRegisterBase + (uint)index;
            scalarRegisters[scalarRegister] =
                state.UserData[index];
            knownScalarRegisters[scalarRegister] = true;
        }

        if (state.GraphicsStageMode == Gen5GraphicsStageMode.NggPassthrough)
        {
            var register = Gen5GraphicsAbi.MergedWaveInfoScalarRegister;
            scalarRegisters[register] =
                Gen5GraphicsAbi.SeedNggPassthroughMergedWaveInfo(
                    scalarRegisters[register]);
            knownScalarRegisters[register] = true;
        }

        if (state.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            computeSystemRegisters.ClearStaticValues(scalarRegisters);
        }

        scalarRegisters[NullScalarRegister] = 0;
        knownScalarRegisters[NullScalarRegister] = true;

        var execMask = RdnaWaveMask;
        WriteScalarPair(scalarRegisters, 106, 0, ref execMask);
        WriteScalarPair(scalarRegisters, 126, execMask, ref execMask);
        knownScalarRegisters[106] = true;
        knownScalarRegisters[107] = true;
        knownScalarRegisters[126] = true;
        knownScalarRegisters[127] = true;
        var initialScalarRegisters = (uint[])scalarRegisters.Clone();

        var initialState = new ScalarDataflowState(
            scalarRegisters,
            knownScalarRegisters,
            new bool[ScalarRegisterCount],
            new uint[VectorRegisterCount],
            new bool[VectorRegisterCount],
            new bool[VectorRegisterCount],
            execMask,
            execMaskKnown: true,
            scalarConditionCode: false,
            scalarConditionCodeKnown: false);
        if (state.ComputeSystemRegisters is { } systemRegisters)
        {
            MarkRuntimeRegisterUnknown(
                initialState,
                systemRegisters.WorkGroupXRegister);
            MarkRuntimeRegisterUnknown(
                initialState,
                systemRegisters.WorkGroupYRegister);
            MarkRuntimeRegisterUnknown(
                initialState,
                systemRegisters.WorkGroupZRegister);
            MarkRuntimeRegisterUnknown(
                initialState,
                systemRegisters.ThreadGroupSizeRegister);
        }

        var resolvedByPc = new Dictionary<uint, Gen5ImageBinding>();
        var globalMemoryBindings = new List<Gen5GlobalMemoryBinding>();
        var globalMemoryByAddress = new Dictionary<(uint ScalarAddress, ulong BaseAddress), Gen5GlobalMemoryBinding>();
        var vertexInputBindings = new List<Gen5VertexInputBinding>();
        var runtimeScalarRegisters = CollectRuntimeScalarRegisters(state.Program);
        var scalarRegisterSnapshots = new Dictionary<uint, IReadOnlyList<uint>>();
        var instructions = state.Program.Instructions;
        var instructionIndexByPc = instructions
            .Select((instruction, index) => (instruction.Pc, Index: index))
            .ToDictionary(item => item.Pc, item => item.Index);
        var entryStates = new ScalarDataflowState?[instructions.Count];
        var worklist = new Queue<int>();
        ScalarDataflowState? exitState = null;
        if (instructions.Count != 0)
        {
            EnqueueDataflowState(entryStates, worklist, 0, initialState);
        }

        var iterations = 0;
        var maximumIterations = Math.Max(
            1,
            instructions.Count *
            (((ScalarRegisterCount + VectorRegisterCount) * 2) + 4));
        while (worklist.Count != 0)
        {
            if (++iterations > maximumIterations)
            {
                error = $"scalar-dataflow-did-not-converge iterations={iterations} instructions={instructions.Count}";
                return false;
            }

            var instructionIndex = worklist.Dequeue();
            var dataflowState = entryStates[instructionIndex]!.Clone();
            var instruction = instructions[instructionIndex];
            scalarRegisterSnapshots[instruction.Pc] =
                (uint[])dataflowState.Registers.Clone();

            if (instruction.Opcode == "SEndpgm")
            {
                JoinExitState(ref exitState, dataflowState);
                continue;
            }

            if (instruction.Opcode == "SBranch")
            {
                if (!TryGetSoppBranchTargetPc(instruction, out var targetPc) ||
                    !TryEnqueueBranchTarget(
                        instructionIndexByPc,
                        entryStates,
                        worklist,
                        targetPc,
                        dataflowState))
                {
                    error = $"invalid-scalar-branch-target pc=0x{instruction.Pc:X} target=0x{targetPc:X}";
                    return false;
                }

                continue;
            }

            if (instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                if (!TryGetSoppBranchTargetPc(instruction, out var targetPc) ||
                    !TryResolveConditionalBranch(
                        instruction.Opcode,
                        dataflowState,
                        out var resolution))
                {
                    error = $"invalid-conditional-scalar-branch pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                    return false;
                }

                if (resolution is ConditionalBranchResolution.Taken or ConditionalBranchResolution.Unknown &&
                    !TryEnqueueBranchTarget(
                        instructionIndexByPc,
                        entryStates,
                        worklist,
                        targetPc,
                        dataflowState))
                {
                    error = $"invalid-scalar-branch-target pc=0x{instruction.Pc:X} target=0x{targetPc:X}";
                    return false;
                }

                if (resolution is ConditionalBranchResolution.Fallthrough or ConditionalBranchResolution.Unknown)
                {
                    EnqueueFallthrough(
                        entryStates,
                        worklist,
                        instructionIndex,
                        dataflowState,
                        ref exitState);
                }

                continue;
            }

            MarkVectorMaskWrites(instruction, dataflowState);

            if (instruction.Encoding == Gen5ShaderEncoding.Sopc)
            {
                if (AreScalarCompareInputsKnown(instruction, dataflowState))
                {
                    if (!TryExecuteScalarCompare(
                            instruction,
                            dataflowState.Registers,
                            out var scalarConditionCode,
                            out error))
                    {
                        return false;
                    }

                    dataflowState.ScalarConditionCode = scalarConditionCode;
                    dataflowState.ScalarConditionCodeKnown = true;
                }
                else
                {
                    dataflowState.ScalarConditionCode = false;
                    dataflowState.ScalarConditionCodeKnown = false;
                }
            }
            else if (instruction.Encoding == Gen5ShaderEncoding.Sopk &&
                     instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
            {
                if (AreScalarCompareKInputsKnown(instruction, dataflowState))
                {
                    if (!TryExecuteScalarCompareK(
                            instruction,
                            dataflowState.Registers,
                            out var scalarConditionCode,
                            out error))
                    {
                        return false;
                    }

                    dataflowState.ScalarConditionCode = scalarConditionCode;
                    dataflowState.ScalarConditionCodeKnown = true;
                }
                else
                {
                    dataflowState.ScalarConditionCode = false;
                    dataflowState.ScalarConditionCodeKnown = false;
                }
            }
            else if (instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopk)
            {
                if (instruction.Opcode is "SSetpcB64" or "SSwappcB64")
                {
                    JoinExitState(ref exitState, dataflowState);
                    continue;
                }

                if (AreScalarAluInputsKnown(instruction, dataflowState))
                {
                    var scalarConditionCode = dataflowState.ScalarConditionCode;
                    var pathExecMask = dataflowState.ExecMask;
                    if (!TryExecuteScalarAlu(
                            instruction,
                            state.Program.Address,
                            dataflowState.Registers,
                            ref pathExecMask,
                            ref scalarConditionCode,
                            out error))
                    {
                        return false;
                    }

                    dataflowState.ExecMask = pathExecMask;
                    dataflowState.ScalarConditionCode = scalarConditionCode;
                    MarkScalarAluOutputsKnown(instruction, dataflowState);
                }
                else
                {
                    MarkScalarAluOutputsUnknown(instruction, dataflowState);
                }
            }
            else if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
            {
                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value < ScalarRegisterCount)
                    {
                        runtimeScalarRegisters.Add(destination.Value);
                    }
                }

                if (AreScalarLoadInputsKnown(instruction, dataflowState))
                {
                    if (!TryExecuteScalarLoad(
                            ctx,
                            state,
                            instruction,
                            scalarMemory,
                            dataflowState.Registers,
                            globalMemoryBindings,
                            globalMemoryByAddress,
                            runtimeScalarRegisters,
                            recordBinding: true,
                            out error))
                    {
                        return false;
                    }

                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.ScalarRegister)
                        {
                            dataflowState.MarkKnown(destination.Value, 1);
                        }
                    }
                }
                else
                {
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.ScalarRegister)
                        {
                            dataflowState.MarkUnknown(destination.Value, 1);
                        }
                    }
                }
            }
            else if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                if (globalMemory.ScalarAddress >= ScalarRegisterCount - 1)
                {
                    error =
                        $"global-address-register-range pc=0x{instruction.Pc:X} " +
                        $"s{globalMemory.ScalarAddress}";
                    return false;
                }

                if (!AreRegistersKnown(
                        dataflowState,
                        globalMemory.ScalarAddress,
                        2))
                {
                    error =
                        $"global-address-path-dependent pc=0x{instruction.Pc:X} " +
                        $"s{globalMemory.ScalarAddress}";
                    return false;
                }

                var baseAddress =
                    dataflowState.Registers[globalMemory.ScalarAddress] |
                    ((ulong)dataflowState.Registers[globalMemory.ScalarAddress + 1] << 32);
                if (baseAddress == 0)
                {
                    error = $"global-address-null pc=0x{instruction.Pc:X}";
                    return false;
                }

                var key = (globalMemory.ScalarAddress, baseAddress);
                var writable = WritesGlobalMemory(instruction.Opcode);
                if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
                {
                    if (existingBinding.InstructionPcs is List<uint> instructionPcs &&
                        !instructionPcs.Contains(instruction.Pc))
                    {
                        instructionPcs.Add(instruction.Pc);
                    }
                    existingBinding.Writable |= writable;
                }
                else
                {
                    if (!TryReadGlobalMemory(ctx, baseAddress, out var data, out var dataLength))
                    {
                        error =
                            $"global-memory-read-failed pc=0x{instruction.Pc:X} " +
                            $"address=0x{baseAddress:X16}";
                        return false;
                    }

                    var binding = new Gen5GlobalMemoryBinding(
                        globalMemory.ScalarAddress,
                        baseAddress,
                        new List<uint> { instruction.Pc },
                        data,
                        dataLength,
                        DataPooled: true);
                    binding.Writable = writable;
                    globalMemoryByAddress.Add(key, binding);
                    globalMemoryBindings.Add(binding);
                }
            }
            else if (instruction.Control is Gen5BufferMemoryControl bufferMemory)
            {
                if (bufferMemory.ScalarResource >= ScalarRegisterCount - 3)
                {
                    error =
                        $"buffer-resource-register-range pc=0x{instruction.Pc:X} " +
                        $"s{bufferMemory.ScalarResource}";
                    return false;
                }

                if (!AreRegistersKnown(
                        dataflowState,
                        bufferMemory.ScalarResource,
                        4))
                {
                    error =
                        $"buffer-descriptor-path-dependent pc=0x{instruction.Pc:X} " +
                        $"s{bufferMemory.ScalarResource}";
                    return false;
                }

                if (!TryDecodeBufferDescriptor(
                        dataflowState.Registers,
                        bufferMemory.ScalarResource,
                        strictType: true,
                        out var bufferDescriptor))
                {
                    error =
                        $"buffer-descriptor-invalid pc=0x{instruction.Pc:X} " +
                        $"s{bufferMemory.ScalarResource}";
                    return false;
                }

                var writable = WritesGlobalMemory(instruction.Opcode);

                if (bufferDescriptor.BaseAddress == 0)
                {
                    // A descriptor in a sibling block can be null for this
                    // invocation even though the GPU branch never executes the
                    // memory instruction. Vulkan still requires a descriptor
                    // for the statically present block, so bind a bounded zero
                    // buffer to this exact PC. It is never reused for another
                    // resource register or instruction.
                    var nullKey = (bufferMemory.ScalarResource, 0UL);
                    if (globalMemoryByAddress.TryGetValue(nullKey, out var nullBinding))
                    {
                        nullBinding.Writable |= writable;
                        if (nullBinding.InstructionPcs is List<uint> nullInstructionPcs &&
                            !nullInstructionPcs.Contains(instruction.Pc))
                        {
                            nullInstructionPcs.Add(instruction.Pc);
                        }
                    }
                    else
                    {
                        var binding = new Gen5GlobalMemoryBinding(
                            bufferMemory.ScalarResource,
                            0,
                            new List<uint> { instruction.Pc },
                            new byte[sizeof(uint)],
                            sizeof(uint),
                            DataPooled: false)
                        {
                            Writable = writable,
                        };
                        globalMemoryByAddress.Add(nullKey, binding);
                        globalMemoryBindings.Add(binding);
                    }

                    continue;
                }

                if (resolveVertexInputs &&
                    IsVertexFetchCandidate(instruction, bufferMemory, bufferDescriptor))
                {
                    if (instruction.Sources.Count <= 2 ||
                        !TryEvaluateScalarOperand(
                            instruction.Sources[2],
                            dataflowState.Registers,
                            out var scalarOffset))
                    {
                        error =
                            $"vertex-input-offset-unresolved pc=0x{instruction.Pc:X} " +
                            $"s{bufferMemory.ScalarResource}";
                        return false;
                    }

                    var bindingOffset = unchecked(
                        (uint)bufferMemory.OffsetBytes + scalarOffset);
                    var vertexReadBytes = bufferDescriptor.SizeBytes;
                    if (requiredVertexRecordCount is > 0)
                    {
                        // Resource descriptors commonly span an entire UE vertex
                        // arena (several MiB), while one draw references only a
                        // few hundred records. Preserve the indexed draw's exact
                        // reachable byte range instead of snapshotting the full
                        // arena for every attribute of every draw.
                        var lastRecordOffset =
                            (ulong)(requiredVertexRecordCount.Value - 1) *
                            bufferDescriptor.Stride;
                        var elementBytes =
                            (ulong)Math.Max(bufferMemory.DwordCount, 1u) * sizeof(uint);
                        var recordSpan = Math.Max(
                            (ulong)bufferDescriptor.Stride,
                            SaturatingAdd(bindingOffset, elementBytes));
                        var requiredBytes = SaturatingAdd(
                            lastRecordOffset,
                            recordSpan);
                        vertexReadBytes = Math.Min(vertexReadBytes, requiredBytes);
                    }

                    if (!TryCreateVertexInputBinding(
                            instruction,
                            bufferMemory,
                            bufferDescriptor,
                            checked((int)Math.Min(
                                vertexReadBytes,
                                (ulong)MaxGlobalMemoryBindingBytes)),
                            (uint)vertexInputBindings.Count,
                            scalarOffset,
                            out var vertexInputBinding))
                    {
                        error =
                            $"vertex-input-binding-failed pc=0x{instruction.Pc:X} " +
                            $"s{bufferMemory.ScalarResource}";
                        return false;
                    }

                    if (!TryAddVertexInputBinding(
                            vertexInputBindings,
                            vertexInputBinding,
                            out error))
                    {
                        return false;
                    }
                }
                else
                {
                    var key = (bufferMemory.ScalarResource, bufferDescriptor.BaseAddress);
                    if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
                    {
                        existingBinding.Writable |= writable;
                        if (existingBinding.InstructionPcs is List<uint> instructionPcs &&
                            !instructionPcs.Contains(instruction.Pc))
                        {
                            instructionPcs.Add(instruction.Pc);
                        }
                    }
                    else
                    {
                        var dataPooled = true;
                        if (!TryReadGlobalMemory(
                                ctx,
                                bufferDescriptor.BaseAddress,
                                bufferDescriptor.SizeBytes,
                                out var data,
                                out var dataLength))
                        {
                            var descriptorWords = string.Join(
                                ':',
                                Enumerable.Range(0, 4).Select(index =>
                                    $"{dataflowState.Registers[bufferMemory.ScalarResource + (uint)index]:X8}"));
                            if (_strictBufferLoad)
                            {
                                error =
                                    $"buffer-memory-read-failed pc=0x{instruction.Pc:X} " +
                                    $"address=0x{bufferDescriptor.BaseAddress:X16} " +
                                    $"bytes={bufferDescriptor.SizeBytes} " +
                                    $"stride={bufferDescriptor.Stride} records={bufferDescriptor.NumRecords} " +
                                    $"s{bufferMemory.ScalarResource}=[{descriptorWords}]";
                                return false;
                            }

                            dataLength = checked((int)Math.Min(
                                bufferDescriptor.SizeBytes,
                                (ulong)MaxGlobalMemoryBindingBytes));
                            data = new byte[Math.Max(dataLength, sizeof(uint))];
                            dataLength = data.Length;
                            dataPooled = false;
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] AGC buffer read unavailable; using zero buffer " +
                                $"pc=0x{instruction.Pc:X} address=0x{bufferDescriptor.BaseAddress:X16} " +
                                $"bytes={bufferDescriptor.SizeBytes} guest_writeback=disabled " +
                                $"s{bufferMemory.ScalarResource}=[{descriptorWords}]");
                        }

                        var binding = new Gen5GlobalMemoryBinding(
                            bufferMemory.ScalarResource,
                            bufferDescriptor.BaseAddress,
                            new List<uint> { instruction.Pc },
                            data,
                            dataLength,
                            DataPooled: dataPooled)
                        {
                            Writable = writable,
                            WriteBackToGuest = dataPooled,
                        };
                        globalMemoryByAddress.Add(key, binding);
                        globalMemoryBindings.Add(binding);
                    }
                }
            }
            else if (instruction.Control is Gen5ImageControl image)
            {
                if (!TryCopyKnownRegisters(
                        dataflowState,
                        image.ScalarResource,
                        ImageDescriptorDwords,
                        out var resourceDescriptor))
                {
                    var reason = HasConflictingRegisters(
                        dataflowState,
                        image.ScalarResource,
                        ImageDescriptorDwords)
                        ? "conflicting"
                        : "path-dependent";
                    error =
                        $"{reason} image binding pc=0x{instruction.Pc:X} " +
                        $"resource=s{image.ScalarResource}";
                    return false;
                }

                IReadOnlyList<uint> samplerDescriptor = [];
                if (UsesSampler(instruction.Opcode) &&
                    !TryCopyKnownRegisters(
                        dataflowState,
                        image.ScalarSampler,
                        SamplerDescriptorDwords,
                        out samplerDescriptor))
                {
                    var reason = HasConflictingRegisters(
                        dataflowState,
                        image.ScalarSampler,
                        SamplerDescriptorDwords)
                        ? "conflicting"
                        : "path-dependent";
                    error =
                        $"{reason} image binding pc=0x{instruction.Pc:X} " +
                        $"sampler=s{image.ScalarSampler}";
                    return false;
                }

                uint? mipLevel = null;
                if (instruction.Opcode == "ImageStoreMip")
                {
                    var coordinateComponentCount = image.Dimension switch
                    {
                        1 => 2,
                        2 or 3 or 5 => 3,
                        _ => 0,
                    };
                    if (coordinateComponentCount == 0)
                    {
                        error =
                            $"unsupported storage image dimension pc=0x{instruction.Pc:X} " +
                            $"dim={image.Dimension}";
                        return false;
                    }

                    var mipRegister = image.GetAddressRegister(
                        coordinateComponentCount);
                    if (!TryReadKnownVectorConstant(
                            dataflowState,
                            mipRegister,
                            out var constantMipLevel))
                    {
                        var reason = mipRegister < VectorRegisterCount &&
                            dataflowState.ConflictingVectorConstants[mipRegister]
                                ? "conflicting"
                                : "path-dependent";
                        error =
                            $"{reason} storage mip pc=0x{instruction.Pc:X} " +
                            $"v{mipRegister}";
                        return false;
                    }

                    mipLevel = constantMipLevel;
                }

                var candidate = new Gen5ImageBinding(
                    instruction.Pc,
                    instruction.Opcode,
                    image,
                    resourceDescriptor,
                    samplerDescriptor,
                    mipLevel);
                if (resolvedByPc.TryGetValue(instruction.Pc, out var existing) &&
                    !ImageBindingsEqual(existing, candidate))
                {
                    error =
                        $"conflicting image binding pc=0x{instruction.Pc:X} " +
                        $"resource={FormatWords(existing.ResourceDescriptor)}|{FormatWords(candidate.ResourceDescriptor)} " +
                        $"sampler={FormatWords(existing.SamplerDescriptor)}|{FormatWords(candidate.SamplerDescriptor)}";
                    return false;
                }

                resolvedByPc.TryAdd(instruction.Pc, candidate);
            }

            TrackVectorConstantWrites(instruction, dataflowState);

            EnqueueFallthrough(
                entryStates,
                worklist,
                instructionIndex,
                dataflowState,
                ref exitState);
        }

        var finalState = exitState ?? initialState;
        var normalizedGlobalBindings = globalMemoryBindings
            .Select(binding => binding with
            {
                InstructionPcs = binding.InstructionPcs
                    .Distinct()
                    .OrderBy(pc => pc)
                    .ToArray(),
            })
            .ToArray();
        if (vertexInputBindings.Count != 0)
        {
            if (!TryCaptureVertexInputData(
                    ctx,
                    vertexInputBindings,
                    out var capturedVertexInputs,
                    out error))
            {
                return false;
            }

            vertexInputBindings = capturedVertexInputs;
        }

        evaluation = new Gen5ShaderEvaluation(
            initialScalarRegisters,
            (uint[])finalState.Registers.Clone(),
            resolvedByPc.Values.OrderBy(binding => binding.Pc).ToArray(),
            normalizedGlobalBindings,
            state.ComputeSystemRegisters,
            runtimeScalarRegisters,
            vertexInputBindings.OrderBy(binding => binding.Pc).ToArray(),
            ScalarRegistersByPc: scalarRegisterSnapshots);
        return true;
    }

    private static bool WritesGlobalMemory(string opcode) =>
        opcode.StartsWith("BufferStore", StringComparison.Ordinal) ||
        opcode.StartsWith("TBufferStore", StringComparison.Ordinal) ||
        opcode.StartsWith("BufferAtomic", StringComparison.Ordinal) ||
        opcode.StartsWith("TBufferAtomic", StringComparison.Ordinal) ||
        opcode.StartsWith("GlobalStore", StringComparison.Ordinal) ||
        opcode.StartsWith("GlobalAtomic", StringComparison.Ordinal) ||
        opcode.StartsWith("FlatStore", StringComparison.Ordinal) ||
        opcode.StartsWith("FlatAtomic", StringComparison.Ordinal);

    private static void EnqueueDataflowState(
        ScalarDataflowState?[] entryStates,
        Queue<int> worklist,
        int instructionIndex,
        ScalarDataflowState incoming)
    {
        if (entryStates[instructionIndex] is not { } existing)
        {
            entryStates[instructionIndex] = incoming.Clone();
            worklist.Enqueue(instructionIndex);
            return;
        }

        if (existing.Join(incoming))
        {
            worklist.Enqueue(instructionIndex);
        }
    }

    private static void MarkRuntimeRegisterUnknown(
        ScalarDataflowState state,
        uint? scalarRegister)
    {
        if (scalarRegister is { } register)
        {
            state.MarkUnknown(register, 1);
        }
    }

    private static void EnqueueFallthrough(
        ScalarDataflowState?[] entryStates,
        Queue<int> worklist,
        int instructionIndex,
        ScalarDataflowState state,
        ref ScalarDataflowState? exitState)
    {
        var nextIndex = instructionIndex + 1;
        if (nextIndex < entryStates.Length)
        {
            EnqueueDataflowState(entryStates, worklist, nextIndex, state);
        }
        else
        {
            JoinExitState(ref exitState, state);
        }
    }

    private static bool TryEnqueueBranchTarget(
        IReadOnlyDictionary<uint, int> instructionIndexByPc,
        ScalarDataflowState?[] entryStates,
        Queue<int> worklist,
        uint targetPc,
        ScalarDataflowState state)
    {
        if (!instructionIndexByPc.TryGetValue(targetPc, out var targetIndex))
        {
            return false;
        }

        EnqueueDataflowState(entryStates, worklist, targetIndex, state);
        return true;
    }

    private static void JoinExitState(
        ref ScalarDataflowState? exitState,
        ScalarDataflowState incoming)
    {
        if (exitState is null)
        {
            exitState = incoming.Clone();
        }
        else
        {
            exitState.Join(incoming);
        }
    }

    private static bool TryResolveConditionalBranch(
        string opcode,
        ScalarDataflowState state,
        out ConditionalBranchResolution resolution)
    {
        resolution = ConditionalBranchResolution.Unknown;
        switch (opcode)
        {
            case "SCbranchScc0":
            case "SCbranchScc1":
                if (!state.ScalarConditionCodeKnown)
                {
                    return true;
                }

                var takeOnSet = opcode == "SCbranchScc1";
                resolution = state.ScalarConditionCode == takeOnSet
                    ? ConditionalBranchResolution.Taken
                    : ConditionalBranchResolution.Fallthrough;
                return true;
            case "SCbranchVccz":
            case "SCbranchVccnz":
                if (!TryReadKnownScalarPair(state, 106, out var vcc))
                {
                    return true;
                }

                var takeOnVccNonzero = opcode == "SCbranchVccnz";
                resolution = (vcc != 0) == takeOnVccNonzero
                    ? ConditionalBranchResolution.Taken
                    : ConditionalBranchResolution.Fallthrough;
                return true;
            case "SCbranchExecz":
            case "SCbranchExecnz":
                if (!state.ExecMaskKnown)
                {
                    return true;
                }

                var takeOnExecNonzero = opcode == "SCbranchExecnz";
                resolution = (state.ExecMask != 0) == takeOnExecNonzero
                    ? ConditionalBranchResolution.Taken
                    : ConditionalBranchResolution.Fallthrough;
                return true;
            default:
                return false;
        }
    }

    private static void MarkVectorMaskWrites(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state)
    {
        if (instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal))
        {
            state.MarkUnknown(126, 2);
        }
        else if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
        {
            var destination = instruction.Control switch
            {
                Gen5Vop3Control { ScalarDestination: { } register } => register,
                Gen5SdwaControl { ScalarDestination: { } register } => register,
                _ => 106u,
            };
            state.MarkUnknown(destination, 2);
        }

        if (instruction.Encoding == Gen5ShaderEncoding.Vop2 &&
            instruction.Opcode is "VAddcU32" or "VSubbU32" or "VSubbrevU32")
        {
            state.MarkUnknown(106, 1);
        }

        if (instruction.Control is Gen5Vop3Control
            {
                ScalarDestination: { } scalarDestination,
            } && !instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
        {
            state.MarkUnknown(scalarDestination, 1);
        }
    }

    private static bool AreScalarCompareInputsKnown(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state)
    {
        for (var index = 0; index < instruction.Sources.Count; index++)
        {
            var pair = index == 0 &&
                instruction.Opcode is "SBitcmp0B64" or "SBitcmp1B64";
            if (!IsScalarOperandKnown(instruction.Sources[index], state, pair))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreScalarCompareKInputsKnown(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state) =>
        instruction.Destinations.Count == 1 &&
        instruction.Destinations[0] is
        {
            Kind: Gen5OperandKind.ScalarRegister,
            Value: var destination,
        } &&
        AreRegistersKnown(state, destination, 1);

    private static bool AreScalarAluInputsKnown(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state)
    {
        if (instruction.Destinations.Count == 1 &&
            instruction.Destinations[0] is
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: var destination,
            } &&
            instruction.Opcode is "SAddkI32" or "SMulkI32" or "SBitset1B32" &&
            !AreRegistersKnown(state, destination, 1))
        {
            return false;
        }

        if (ScalarAluDependsOnScc(instruction.Opcode) &&
            !state.ScalarConditionCodeKnown)
        {
            return false;
        }

        if (instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) &&
            !state.ExecMaskKnown)
        {
            return false;
        }

        for (var index = 0; index < instruction.Sources.Count; index++)
        {
            if (!IsScalarOperandKnown(
                    instruction.Sources[index],
                    state,
                    ScalarAluSourceIsPair(instruction.Opcode, index)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreScalarLoadInputsKnown(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state)
    {
        if (instruction.Sources.Count == 0 ||
            instruction.Sources[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: var scalarBase,
            })
        {
            return false;
        }

        var baseDwords = instruction.Opcode.StartsWith(
            "SBufferLoad",
            StringComparison.Ordinal)
            ? 4u
            : 2u;
        if (!AreRegistersKnown(state, scalarBase, baseDwords))
        {
            return false;
        }

        return instruction.Sources.Count < 2 ||
            IsScalarOperandKnown(instruction.Sources[1], state, pair: false);
    }

    private static bool IsScalarOperandKnown(
        Gen5Operand operand,
        ScalarDataflowState state,
        bool pair)
    {
        if (operand.Kind != Gen5OperandKind.ScalarRegister)
        {
            return operand.Kind != Gen5OperandKind.VectorRegister;
        }

        if (pair && operand.Value == 126)
        {
            return state.ExecMaskKnown;
        }

        return AreRegistersKnown(state, operand.Value, pair ? 2u : 1u);
    }

    private static bool ScalarAluSourceIsPair(string opcode, int sourceIndex) =>
        opcode switch
        {
            "SMovB64" or "SWqmB64" or "SNotB64" => sourceIndex == 0,
            "SLshlB64" or "SLshrB64" or "SBfeU64" or "SBfeI64" => sourceIndex == 0,
            "SCselectB64" or
            "SAndB64" or
            "SOrB64" or
            "SXorB64" or
            "SAndn2B64" or
            "SOrn2B64" or
            "SNandB64" or
            "SNorB64" or
            "SXnorB64" => sourceIndex < 2,
            _ when opcode.Contains("Saveexec", StringComparison.Ordinal) => sourceIndex == 0,
            _ => false,
        };

    private static bool ScalarAluDependsOnScc(string opcode) =>
        opcode is
            "SCselectB32" or
            "SCselectB64" or
            "SAddcU32" or
            "SSubbU32";

    private static bool ScalarAluWritesScc(string opcode) =>
        opcode.Contains("Saveexec", StringComparison.Ordinal) ||
        opcode is
            "SAddkI32" or
            "SNotB64" or
            "SWqmB64" or
            "SLshlB64" or
            "SLshrB64" or
            "SBfeU64" or
            "SBfeI64" or
            "SAndB64" or
            "SOrB64" or
            "SXorB64" or
            "SAndn2B64" or
            "SOrn2B64" or
            "SNandB64" or
            "SNorB64" or
            "SXnorB64" or
            "SNotB32" or
            "SBcnt1I32B32" or
            "SAddU32" or
            "SSubU32" or
            "SAddI32" or
            "SSubI32" or
            "SAddcU32" or
            "SSubbU32" or
            "SMinI32" or
            "SMinU32" or
            "SMaxI32" or
            "SMaxU32" or
            "SAndB32" or
            "SOrB32" or
            "SXorB32" or
            "SAndn2B32" or
            "SOrn2B32" or
            "SNandB32" or
            "SNorB32" or
            "SXnorB32" or
            "SLshlB32" or
            "SLshrB32" or
            "SAshrI32" or
            "SBfeU32" or
            "SBfeI32" or
            "SAbsdiffI32" or
            "SLshl1AddU32" or
            "SLshl2AddU32" or
            "SLshl3AddU32" or
            "SLshl4AddU32";

    private static void MarkScalarAluOutputsKnown(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state)
    {
        if (instruction.Destinations.Count == 1 &&
            instruction.Destinations[0] is
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: var destination,
            })
        {
            var destinationDwords =
                ScalarAluDestinationIsPair(instruction.Opcode) ? 2u : 1u;
            state.MarkKnown(
                destination,
                destinationDwords);
            if (destination <= 127 && destination + destinationDwords > 126)
            {
                state.ExecMaskKnown = AreRegistersKnown(state, 126, 2);
                state.ExecMask = state.ExecMaskKnown
                    ? MaskWaveValue(
                        state.Registers[126] |
                        ((ulong)state.Registers[127] << 32))
                    : 0;
            }
        }

        if (ScalarAluWritesScc(instruction.Opcode))
        {
            state.ScalarConditionCodeKnown = true;
        }
    }

    private static void MarkScalarAluOutputsUnknown(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state)
    {
        if (instruction.Destinations.Count == 1 &&
            instruction.Destinations[0] is
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: var destination,
            })
        {
            state.MarkUnknown(
                destination,
                ScalarAluDestinationIsPair(instruction.Opcode) ? 2u : 1u);
        }

        if (instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal))
        {
            state.MarkUnknown(126, 2);
        }

        if (ScalarAluWritesScc(instruction.Opcode))
        {
            state.ScalarConditionCode = false;
            state.ScalarConditionCodeKnown = false;
        }
    }

    private static bool ScalarAluDestinationIsPair(string opcode) =>
        opcode.EndsWith("B64", StringComparison.Ordinal) ||
        opcode.Contains("Saveexec", StringComparison.Ordinal) ||
        opcode is "SGetpcB64" or "SBfeU64" or "SBfeI64";

    private static bool AreRegistersKnown(
        ScalarDataflowState state,
        uint firstRegister,
        uint count)
    {
        if ((ulong)firstRegister + count > (ulong)state.KnownRegisters.Length)
        {
            return false;
        }

        for (var index = firstRegister; index < firstRegister + count; index++)
        {
            if (index == NullScalarRegister)
            {
                continue;
            }

            if (!state.KnownRegisters[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasConflictingRegisters(
        ScalarDataflowState state,
        uint firstRegister,
        int count)
    {
        if ((ulong)firstRegister + (uint)count >
            (ulong)state.ConflictingRegisters.Length)
        {
            return false;
        }

        for (var index = firstRegister;
             index < firstRegister + (uint)count;
             index++)
        {
            if (index == NullScalarRegister)
            {
                continue;
            }

            if (state.ConflictingRegisters[index])
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadKnownScalarPair(
        ScalarDataflowState state,
        uint firstRegister,
        out ulong value)
    {
        value = 0;
        if (!AreRegistersKnown(state, firstRegister, 2))
        {
            return false;
        }

        value = ReadScalarRegister(state.Registers, firstRegister) |
            ((ulong)ReadScalarRegister(state.Registers, firstRegister + 1) << 32);
        return true;
    }

    private static bool TryCopyKnownRegisters(
        ScalarDataflowState state,
        uint start,
        int count,
        out IReadOnlyList<uint> values)
    {
        values = [];
        if (!AreRegistersKnown(state, start, checked((uint)count)))
        {
            return false;
        }

        return TryCopyRegisters(state.Registers, start, count, out values);
    }

    private static bool ImageBindingsEqual(
        Gen5ImageBinding left,
        Gen5ImageBinding right) =>
        left.Opcode == right.Opcode &&
        left.MipLevel == right.MipLevel &&
        left.ResourceDescriptor.SequenceEqual(right.ResourceDescriptor) &&
        left.SamplerDescriptor.SequenceEqual(right.SamplerDescriptor);

    private static string FormatWords(IReadOnlyList<uint> words) =>
        string.Join(',', words.Select(word => $"{word:X8}"));

    private static bool TryAddVertexInputBinding(
        List<Gen5VertexInputBinding> bindings,
        Gen5VertexInputBinding candidate,
        out string error)
    {
        error = string.Empty;
        var existing = bindings.FirstOrDefault(binding => binding.Pc == candidate.Pc);
        if (existing is null)
        {
            bindings.Add(candidate);
            return true;
        }

        if (existing.ComponentCount == candidate.ComponentCount &&
            existing.DataFormat == candidate.DataFormat &&
            existing.NumberFormat == candidate.NumberFormat &&
            existing.BaseAddress == candidate.BaseAddress &&
            existing.Stride == candidate.Stride &&
            existing.OffsetBytes == candidate.OffsetBytes &&
            existing.Data.AsSpan().SequenceEqual(candidate.Data))
        {
            return true;
        }

        error = $"conflicting vertex input binding pc=0x{candidate.Pc:X}";
        return false;
    }

    private static bool TryCreateVertexInputBinding(
        Gen5ShaderInstruction instruction,
        Gen5BufferMemoryControl control,
        BufferDescriptor descriptor,
        int desiredDataLength,
        uint location,
        uint scalarOffset,
        out Gen5VertexInputBinding binding)
    {
        binding = default!;
        if (!IsVertexFetchCandidate(instruction, control, descriptor))
        {
            return false;
        }

        var bindingStride = descriptor.Stride;
        var bindingOffset = unchecked((uint)control.OffsetBytes + scalarOffset);
        var bindingDataFormat = descriptor.DataFormat;
        var bindingNumberFormat = descriptor.NumberFormat;
        binding = new Gen5VertexInputBinding(
            instruction.Pc,
            location,
            control.DwordCount,
            bindingDataFormat,
            bindingNumberFormat,
            descriptor.BaseAddress,
            bindingStride,
            bindingOffset,
            Data: [],
            DataLength: desiredDataLength,
            DataPooled: false);
        return true;
    }

    private static bool TryCaptureVertexInputData(
        CpuContext ctx,
        IReadOnlyList<Gen5VertexInputBinding> pending,
        out List<Gen5VertexInputBinding> captured,
        out string error)
    {
        captured = new List<Gen5VertexInputBinding>(pending.Count);
        error = string.Empty;
        var ordered = pending
            .OrderBy(static binding => binding.Stride)
            .ThenBy(static binding => binding.BaseAddress)
            .ToArray();

        for (var first = 0; first < ordered.Length;)
        {
            var stride = ordered[first].Stride;
            var start = ordered[first].BaseAddress;
            var end = SaturatingAdd(start, (ulong)ordered[first].DataLength);
            var last = first + 1;
            while (last < ordered.Length && ordered[last].Stride == stride)
            {
                var candidate = ordered[last];
                // Attribute descriptors for an interleaved stream commonly
                // point a few bytes into the same record. Merge overlapping
                // spans (and one-record adjacency) so all attributes share one
                // captured array and one host vertex buffer.
                if (candidate.BaseAddress > SaturatingAdd(end, stride))
                {
                    break;
                }

                end = Math.Max(
                    end,
                    SaturatingAdd(candidate.BaseAddress, (ulong)candidate.DataLength));
                last++;
            }

            var byteCount = end > start
                ? Math.Min(end - start, (ulong)MaxGlobalMemoryBindingBytes)
                : 0;
            if (byteCount == 0 ||
                !TryReadGlobalMemory(ctx, start, byteCount, out var data, out var dataLength))
            {
                foreach (var binding in captured)
                {
                    if (binding.DataPooled)
                    {
                        GlobalMemoryPool.Return(binding.Data);
                    }
                }

                error =
                    $"vertex-buffer-read-failed address=0x{start:X16} " +
                    $"bytes={byteCount} stride={stride}";
                captured.Clear();
                return false;
            }

            for (var index = first; index < last; index++)
            {
                var binding = ordered[index];
                var delta = binding.BaseAddress - start;
                captured.Add(binding with
                {
                    BaseAddress = start,
                    OffsetBytes = checked((uint)(delta + binding.OffsetBytes)),
                    Data = data,
                    DataLength = dataLength,
                    DataPooled = index == first,
                });
            }

            first = last;
        }

        captured.Sort(static (left, right) => left.Location.CompareTo(right.Location));
        TraceTitleVertexInputs(captured);
        return true;
    }

    private static void TraceTitleVertexInputs(IReadOnlyList<Gen5VertexInputBinding> bindings)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_RAW"),
                "1",
                StringComparison.Ordinal) ||
            bindings.Count != 3 ||
            !bindings.Any(static binding =>
                binding.Pc == 0xF8 && binding.Stride == 16 &&
                binding.OffsetBytes == 12 && binding.DataFormat == 10) ||
            !bindings.Any(static binding =>
                binding.Pc == 0x204 && binding.Stride == 16 &&
                binding.OffsetBytes == 0 && binding.DataFormat == 12) ||
            !bindings.Any(static binding =>
                binding.Pc == 0x280 && binding.Stride == 16 &&
                binding.OffsetBytes == 8 && binding.DataFormat == 5))
        {
            return;
        }

        var shared = bindings[0];
        var records = new List<string>();
        foreach (var vertexIndex in new uint[] { 0, 1, 2, 3, 131, 4095 })
        {
            var recordOffset = (ulong)vertexIndex * shared.Stride;
            if (recordOffset + shared.Stride > (ulong)shared.DataLength)
            {
                continue;
            }

            records.Add(
                $"{vertexIndex}:" +
                Convert.ToHexString(
                    shared.Data,
                    checked((int)recordOffset),
                    checked((int)shared.Stride)));
        }

        Console.Error.WriteLine(
            $"[VERTEX-RAW] title base=0x{shared.BaseAddress:X16} " +
            $"length={shared.DataLength} records={string.Join(',', records)}");
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static bool IsVertexFetchCandidate(
        Gen5ShaderInstruction instruction,
        Gen5BufferMemoryControl control,
        BufferDescriptor descriptor) =>
        control.IndexEnabled &&
        !control.OffsetEnabled &&
        control.DwordCount is >= 1 and <= 4 &&
        descriptor.BaseAddress != 0 &&
        descriptor.Stride != 0 &&
        (instruction.Opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
         instruction.Opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal));

    private static HashSet<uint> CollectRuntimeScalarRegisters(Gen5ShaderProgram program)
    {
        var registers = new HashSet<uint>();
        foreach (var instruction in program.Instructions)
        {
            foreach (var operand in instruction.Sources.Concat(instruction.Destinations))
            {
                if (operand.Kind == Gen5OperandKind.ScalarRegister &&
                    operand.Value < ScalarRegisterCount &&
                    operand.Value != NullScalarRegister)
                {
                    registers.Add(operand.Value);
                }
            }

            if (instruction.Control is Gen5ScalarMemoryControl
                {
                    DynamicOffsetRegister: { } offsetRegister,
                } &&
                offsetRegister < ScalarRegisterCount)
            {
                registers.Add(offsetRegister);
            }
        }

        return registers;
    }

    private static bool TryGetSoppBranchTargetPc(
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
        var nextPc = (long)instruction.Pc + instruction.Words.Count * sizeof(uint);
        var target = nextPc + offset * sizeof(uint);
        if (target < 0 || target > uint.MaxValue)
        {
            return false;
        }

        targetPc = (uint)target;
        return true;
    }

    private static void TrackVectorConstantWrites(
        Gen5ShaderInstruction instruction,
        ScalarDataflowState state)
    {
        uint uniformConstant = 0;
        var hasUniformConstant =
            instruction.Opcode == "VMovB32" &&
            instruction.Control is null &&
            instruction.Sources.Count == 1 &&
            state.ExecMaskKnown &&
            state.ExecMask == RdnaWaveMask &&
            TryResolveUniformVectorSource(
                instruction.Sources[0],
                state,
                out uniformConstant);
        foreach (var destination in instruction.Destinations)
        {
            if (destination.Kind != Gen5OperandKind.VectorRegister)
            {
                continue;
            }

            if (hasUniformConstant)
            {
                state.MarkVectorKnown(destination.Value, uniformConstant);
            }
            else
            {
                state.MarkVectorUnknown(destination.Value);
            }
        }
    }

    private static bool TryResolveUniformVectorSource(
        Gen5Operand operand,
        ScalarDataflowState state,
        out uint value)
    {
        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            AreRegistersKnown(state, operand.Value, 1))
        {
            value = ReadScalarRegister(state.Registers, operand.Value);
            return true;
        }

        return TryResolveConstantOperand(operand, out value);
    }

    private static bool TryReadKnownVectorConstant(
        ScalarDataflowState state,
        uint vectorRegister,
        out uint value)
    {
        value = 0;
        if (vectorRegister >= state.VectorConstants.Length ||
            !state.KnownVectorConstants[vectorRegister])
        {
            return false;
        }

        value = state.VectorConstants[vectorRegister];
        return true;
    }

    private static bool TryResolveConstantOperand(Gen5Operand operand, out uint value)
    {
        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            value = operand.Value;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.EncodedConstant)
        {
            return TryDecodeInlineConstant(operand.Value, out value);
        }

        value = 0;
        return false;
    }

    // Both readers rent from ArrayPool: these run per bound buffer per draw,
    // and fresh multi-megabyte allocations here kept the background GC busy
    // full-time. The rented array (possibly oversized) is handed to the
    // presenter, which returns it to the pool after the host-buffer upload.
    public static void BeginGlobalMemoryReadScope()
    {
    }

    public static void EndGlobalMemoryReadScope()
    {
    }
    private static bool TryReadGlobalMemory(
        CpuContext ctx,
        ulong baseAddress,
        out byte[] data,
        out int dataLength)
    {
        var rented = GlobalMemoryPool.Rent(MaxGlobalMemoryBindingBytes);
        for (var size = MaxGlobalMemoryBindingBytes; size >= 4096; size >>= 1)
        {
            if (ctx.Memory.TryRead(baseAddress, rented.AsSpan(0, size)))
            {
                Interlocked.Increment(ref GlobalMemoryReadCount);
                Interlocked.Add(ref GlobalMemoryReadBytes, size);
                Interlocked.Add(ref GlobalMemoryReadPvmBytes, size);
                data = rented;
                dataLength = size;
                return true;
            }
        }

        GlobalMemoryPool.Return(rented);
        data = [];
        dataLength = 0;
        return false;
    }

    private static bool TryReadGlobalMemory(
        CpuContext ctx,
        ulong baseAddress,
        ulong sizeBytes,
        out byte[] data,
        out int dataLength)
    {
        data = [];
        dataLength = 0;
        if (sizeBytes == 0)
        {
            return false;
        }

        var cappedSize = Math.Min(sizeBytes, MaxGlobalMemoryBindingBytes);
        if (cappedSize > int.MaxValue)
        {
            return false;
        }

        var rented = GlobalMemoryPool.Rent(
            Math.Max((int)cappedSize, sizeof(uint)));
        if (cappedSize < sizeof(uint))
        {
            rented.AsSpan(0, sizeof(uint)).Clear();
            var exact = rented.AsSpan(0, (int)cappedSize);
            var readFromPvm = ctx.Memory.TryRead(baseAddress, exact);
            if (readFromPvm || FallbackMemoryReader?.Invoke(baseAddress, exact) == true)
            {
                Interlocked.Increment(ref GlobalMemoryReadCount);
                Interlocked.Add(ref GlobalMemoryReadBytes, sizeof(uint));
                if (readFromPvm)
                {
                    Interlocked.Add(ref GlobalMemoryReadPvmBytes, sizeof(uint));
                }
                else
                {
                    Interlocked.Add(ref GlobalMemoryReadLibcBytes, sizeof(uint));
                }
                data = rented;
                dataLength = sizeof(uint);
                return true;
            }

            GlobalMemoryPool.Return(rented);
            return false;
        }

        var candidateSize = (int)cappedSize;
        while (candidateSize >= sizeof(uint))
        {
            var span = rented.AsSpan(0, candidateSize);
            var readFromPvm = ctx.Memory.TryRead(baseAddress, span);
            if (readFromPvm || FallbackMemoryReader?.Invoke(baseAddress, span) == true)
            {
                Interlocked.Increment(ref GlobalMemoryReadCount);
                Interlocked.Add(ref GlobalMemoryReadBytes, candidateSize);
                if (readFromPvm)
                {
                    Interlocked.Add(ref GlobalMemoryReadPvmBytes, candidateSize);
                }
                else
                {
                    Interlocked.Add(ref GlobalMemoryReadLibcBytes, candidateSize);
                }
                data = rented;
                dataLength = candidateSize;
                return true;
            }

            if (candidateSize == sizeof(uint))
            {
                break;
            }

            candidateSize = Math.Max(candidateSize / 2, sizeof(uint));
        }

        GlobalMemoryPool.Return(rented);
        return false;
    }

    private static bool TryExecuteScalarAlu(
        Gen5ShaderInstruction instruction,
        ulong programAddress,
        uint[] registers,
        ref ulong execMask,
        ref bool scalarConditionCode,
        out string error)
    {
        error = string.Empty;
        if (instruction.Destinations.Count != 1 ||
            instruction.Destinations[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount,
            } destination)
        {
            error = $"unsupported-scalar-destination pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        if (instruction.Opcode == "SMovkI32")
        {
            WriteScalarRegister(
                registers,
                destination.Value,
                unchecked((uint)(short)instruction.Sources[0].Value));
            return true;
        }

        if (instruction.Opcode is "SAddkI32" or "SMulkI32")
        {
            var immediate = unchecked((uint)(short)instruction.Sources[0].Value);
            var oldValue = ReadScalarRegister(registers, destination.Value);
            var sopkResult = instruction.Opcode == "SAddkI32"
                ? oldValue + immediate
                : unchecked((uint)((int)oldValue * (int)immediate));
            WriteScalarRegister(
                registers,
                destination.Value,
                sopkResult);
            if (instruction.Opcode == "SAddkI32")
            {
                scalarConditionCode = SignedAddOverflow(
                    oldValue,
                    immediate,
                    sopkResult);
            }

            return true;
        }

        if (instruction.Opcode == "SGetpcB64")
        {
            var pc = programAddress + instruction.Pc + (ulong)(instruction.Words.Count * sizeof(uint));
            WriteScalarPair(registers, destination.Value, pc, ref execMask);
            return true;
        }

        if (TryExecuteSaveExecScalarAlu(
                instruction,
                registers,
                ref execMask,
                ref scalarConditionCode,
                out error))
        {
            return true;
        }

        if (instruction.Opcode is "SMovB64" or "SWqmB64" or "SNotB64")
        {
            if (destination.Value >= ScalarRegisterCount - 1 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var value))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode == "SNotB64")
            {
                value = ~value;
                scalarConditionCode = value != 0;
            }
            else if (instruction.Opcode == "SWqmB64")
            {
                value = ExpandWholeQuadMask(value);
                scalarConditionCode = value != 0;
            }

            WriteScalarPair(registers, destination.Value, value, ref execMask);
            return true;
        }

        if (instruction.Opcode is "SLshlB64" or "SLshrB64")
        {
            if (instruction.Sources.Count < 2 ||
                destination.Value >= ScalarRegisterCount - 1 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var value) ||
                !TryEvaluateScalarOperand(
                    instruction.Sources[1],
                    registers,
                    out var shift))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            value = instruction.Opcode == "SLshlB64"
                ? value << ((int)shift & 63)
                : value >> ((int)shift & 63);
            WriteScalarPair(registers, destination.Value, value, ref execMask);
            scalarConditionCode = value != 0;
            return true;
        }

        if (instruction.Opcode is "SBfeU64" or "SBfeI64")
        {
            if (instruction.Sources.Count < 2 ||
                destination.Value >= ScalarRegisterCount - 1 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var source) ||
                !TryEvaluateScalarOperand(
                    instruction.Sources[1],
                    registers,
                    out var control))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var offset = (int)control & 63;
            var width = Math.Min(((int)control >> 16) & 0x7F, 64 - offset);
            ulong value;
            if (width == 0)
            {
                value = 0;
            }
            else
            {
                value = source >> offset;
                if (width < 64)
                {
                    value &= ulong.MaxValue >> (64 - width);
                    if (instruction.Opcode == "SBfeI64")
                    {
                        value = unchecked((ulong)((long)(value << (64 - width)) >> (64 - width)));
                    }
                }
            }

            WriteScalarPair(registers, destination.Value, value, ref execMask);
            scalarConditionCode = value != 0;
            return true;
        }

        if (instruction.Opcode == "SBfmB64")
        {
            if (instruction.Sources.Count < 2 ||
                destination.Value >= ScalarRegisterCount - 1 ||
                !TryEvaluateScalarOperand(instruction.Sources[0], registers, out var widthSource) ||
                !TryEvaluateScalarOperand(instruction.Sources[1], registers, out var offsetSource))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var width = (int)widthSource & 63;
            var offset = (int)offsetSource & 63;
            var value = width == 0
                ? 0UL
                : (ulong.MaxValue >> (64 - width)) << offset;
            WriteScalarPair(registers, destination.Value, value, ref execMask);
            scalarConditionCode = value != 0;
            return true;
        }

        if (instruction.Opcode is
            "SCselectB64" or
            "SAndB64" or
            "SOrB64" or
            "SXorB64" or
            "SAndn2B64" or
            "SOrn2B64" or
            "SNandB64" or
            "SNorB64" or
            "SXnorB64")
        {
            if (instruction.Sources.Count < 2 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var maskLeft) ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[1],
                    registers,
                    execMask,
                    out var maskRight))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var value = instruction.Opcode switch
            {
                "SCselectB64" => scalarConditionCode ? maskLeft : maskRight,
                "SAndB64" => maskLeft & maskRight,
                "SOrB64" => maskLeft | maskRight,
                "SXorB64" => maskLeft ^ maskRight,
                "SAndn2B64" => maskLeft & ~maskRight,
                "SOrn2B64" => maskLeft | ~maskRight,
                "SNandB64" => ~(maskLeft & maskRight),
                "SNorB64" => ~(maskLeft | maskRight),
                _ => ~(maskLeft ^ maskRight),
            };
            WriteScalarPair(registers, destination.Value, value, ref execMask);
            if (instruction.Opcode != "SCselectB64")
            {
                scalarConditionCode = value != 0;
            }
            return true;
        }

        if (instruction.Sources.Count == 0 ||
            !TryEvaluateScalarOperand(
                instruction.Sources[0],
                registers,
                execMask,
                scalarConditionCode,
                out var left))
        {
            var source = instruction.Sources.Count == 0
                ? "<missing>"
                : instruction.Sources[0].ToString();
            error = $"scalar-source0 pc=0x{instruction.Pc:X} op={instruction.Opcode} source={source}";
            return false;
        }

        if (instruction.Opcode == "SMovB32")
        {
            WriteScalarRegister(registers, destination.Value, left);
            return true;
        }

        if (instruction.Opcode is
            "SNotB32" or
            "SBrevB32" or
            "SBcnt1I32B32" or
            "SFF1I32B32" or
            "SBitset1B32")
        {
            var unaryResult = instruction.Opcode switch
            {
                "SNotB32" => ~left,
                "SBrevB32" => ReverseBits(left),
                "SBcnt1I32B32" => (uint)BitOperations.PopCount(left),
                "SFF1I32B32" => left == 0 ? uint.MaxValue : (uint)BitOperations.TrailingZeroCount(left),
                _ => ReadScalarRegister(registers, destination.Value) |
                    (1u << ((int)left & 31)),
            };
            WriteScalarRegister(registers, destination.Value, unaryResult);
            if (instruction.Opcode is "SNotB32" or "SBcnt1I32B32")
            {
                scalarConditionCode = unaryResult != 0;
            }

            return true;
        }

        if (instruction.Sources.Count < 2 ||
            !TryEvaluateScalarOperand(
                instruction.Sources[1],
                registers,
                execMask,
                scalarConditionCode,
                out var right))
        {
            var source = instruction.Sources.Count < 2
                ? "<missing>"
                : instruction.Sources[1].ToString();
            error = $"scalar-source1 pc=0x{instruction.Pc:X} op={instruction.Opcode} source={source}";
            return false;
        }

        uint result;
        switch (instruction.Opcode)
        {
            case "SAddU32":
                {
                    var wide = (ulong)left + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SSubU32":
                result = left - right;
                scalarConditionCode = right > left;
                break;
            case "SAddI32":
                result = unchecked((uint)((int)left + (int)right));
                scalarConditionCode = SignedAddOverflow(left, right, result);
                break;
            case "SSubI32":
                result = unchecked((uint)((int)left - (int)right));
                scalarConditionCode = SignedSubOverflow(left, right, result);
                break;
            case "SAddcU32":
                {
                    var wide = (ulong)left + right + (scalarConditionCode ? 1UL : 0UL);
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SSubbU32":
                {
                    var borrow = scalarConditionCode ? 1UL : 0UL;
                    var subtrahend = (ulong)right + borrow;
                    result = unchecked(left - (uint)subtrahend);
                    scalarConditionCode = subtrahend > left;
                    break;
                }
            case "SMinI32":
                result = unchecked((uint)Math.Min((int)left, (int)right));
                scalarConditionCode = (int)left < (int)right;
                break;
            case "SMinU32":
                result = Math.Min(left, right);
                scalarConditionCode = left < right;
                break;
            case "SMaxI32":
                result = unchecked((uint)Math.Max((int)left, (int)right));
                scalarConditionCode = (int)left > (int)right;
                break;
            case "SMaxU32":
                result = Math.Max(left, right);
                scalarConditionCode = left > right;
                break;
            case "SCselectB32":
                result = scalarConditionCode ? left : right;
                break;
            case "SAndB32":
                result = left & right;
                scalarConditionCode = result != 0;
                break;
            case "SOrB32":
                result = left | right;
                scalarConditionCode = result != 0;
                break;
            case "SXorB32":
                result = left ^ right;
                scalarConditionCode = result != 0;
                break;
            case "SAndn2B32":
                result = left & ~right;
                scalarConditionCode = result != 0;
                break;
            case "SOrn2B32":
                result = left | ~right;
                scalarConditionCode = result != 0;
                break;
            case "SNandB32":
                result = ~(left & right);
                scalarConditionCode = result != 0;
                break;
            case "SNorB32":
                result = ~(left | right);
                scalarConditionCode = result != 0;
                break;
            case "SXnorB32":
                result = ~(left ^ right);
                scalarConditionCode = result != 0;
                break;
            case "SLshlB32":
                result = left << ((int)right & 31);
                scalarConditionCode = result != 0;
                break;
            case "SLshrB32":
                result = left >> ((int)right & 31);
                scalarConditionCode = result != 0;
                break;
            case "SAshrI32":
                result = unchecked((uint)((int)left >> ((int)right & 31)));
                scalarConditionCode = result != 0;
                break;
            case "SBfmB32":
                {
                    var width = (int)left & 31;
                    var offset = (int)right & 31;
                    result = width == 0 ? 0 : ((1u << width) - 1u) << offset;
                    break;
                }
            case "SMulI32":
                result = unchecked((uint)((int)left * (int)right));
                break;
            case "SBfeU32":
                {
                    var offset = (int)right & 31;
                    var width = Math.Min(((int)right >> 16) & 0x7F, 32 - offset);
                    result = width == 0 ? 0 : left >> offset & (uint.MaxValue >> (32 - width));
                    scalarConditionCode = result != 0;
                    break;
                }
            case "SBfeI32":
                {
                    var offset = (int)right & 31;
                    var width = Math.Min(((int)right >> 16) & 0x7F, 32 - offset);
                    result = width == 0
                        ? 0
                        : unchecked((uint)(((int)(left << (32 - width - offset))) >> (32 - width)));
                    scalarConditionCode = result != 0;
                    break;
                }
            case "SAbsdiffI32":
                result = unchecked((uint)Math.Abs((long)(int)left - (int)right));
                scalarConditionCode = result != 0;
                break;
            case "SLshl1AddU32":
                {
                    var wide = ((ulong)left << 1) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SLshl2AddU32":
                {
                    var wide = ((ulong)left << 2) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SLshl3AddU32":
                {
                    var wide = ((ulong)left << 3) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SLshl4AddU32":
                {
                    var wide = ((ulong)left << 4) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SPackLlB32B16":
                result = (left & 0xFFFFu) | (right << 16);
                break;
            case "SPackLhB32B16":
                result = (left & 0xFFFFu) | (right & 0xFFFF0000u);
                break;
            case "SPackHhB32B16":
                result = (left >> 16) | (right & 0xFFFF0000u);
                break;
            case "SMulHiU32":
                result = (uint)(((ulong)left * right) >> 32);
                break;
            case "SMulHiI32":
                result = unchecked((uint)(((long)(int)left * (int)right) >> 32));
                break;
            default:
                error = $"unsupported-scalar-op pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
        }

        WriteScalarRegister(registers, destination.Value, result);
        return true;
    }

    private static bool TryExecuteSaveExecScalarAlu(
        Gen5ShaderInstruction instruction,
        uint[] registers,
        ref ulong execMask,
        ref bool scalarConditionCode,
        out string error)
    {
        error = string.Empty;
        if (instruction.Opcode.EndsWith("SaveexecB32", StringComparison.Ordinal))
        {
            if (instruction.Destinations.Count != 1 ||
                instruction.Destinations[0] is not
                {
                    Kind: Gen5OperandKind.ScalarRegister,
                    Value: < ScalarRegisterCount,
                } destination32 ||
                instruction.Sources.Count == 0 ||
                !TryEvaluateScalarOperand(instruction.Sources[0], registers, out var source32))
            {
                error = $"scalar-source32 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var oldExec32 = (uint)execMask;
            var newExec32 = instruction.Opcode switch
            {
                "SAndSaveexecB32" => oldExec32 & source32,
                "SOrSaveexecB32" => oldExec32 | source32,
                "SXorSaveexecB32" => oldExec32 ^ source32,
                "SAndn1SaveexecB32" => ~source32 & oldExec32,
                "SAndn2SaveexecB32" => source32 & ~oldExec32,
                "SOrn1SaveexecB32" => ~source32 | oldExec32,
                "SOrn2SaveexecB32" => source32 | ~oldExec32,
                "SNandSaveexecB32" => ~(source32 & oldExec32),
                "SNorSaveexecB32" => ~(source32 | oldExec32),
                "SXnorSaveexecB32" => ~(oldExec32 ^ source32),
                _ => 0u,
            };
            registers[destination32.Value] = oldExec32;
            execMask = newExec32;
            registers[126] = newExec32;
            registers[127] = 0;
            scalarConditionCode = newExec32 != 0;
            return true;
        }

        if (instruction.Opcode is not (
            "SAndSaveexecB64" or
            "SOrSaveexecB64" or
            "SXorSaveexecB64" or
            "SAndn2SaveexecB64" or
            "SOrn2SaveexecB64" or
            "SNandSaveexecB64" or
            "SNorSaveexecB64" or
            "SXnorSaveexecB64" or
            "SAndn1SaveexecB64" or
            "SOrn1SaveexecB64"))
        {
            return false;
        }

        if (instruction.Destinations.Count != 1 ||
            instruction.Destinations[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount - 1,
            } destination ||
            instruction.Sources.Count == 0 ||
            !TryEvaluateScalarOperand64(
                instruction.Sources[0],
                registers,
                execMask,
                out var source))
        {
            error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        var oldExec = execMask;
        var newExec = instruction.Opcode switch
        {
            "SAndSaveexecB64" => oldExec & source,
            "SOrSaveexecB64" => oldExec | source,
            "SXorSaveexecB64" => oldExec ^ source,
            "SAndn1SaveexecB64" => ~source & oldExec,
            "SAndn2SaveexecB64" => source & ~oldExec,
            "SOrn1SaveexecB64" => ~source | oldExec,
            "SOrn2SaveexecB64" => source | ~oldExec,
            "SNandSaveexecB64" => ~(source & oldExec),
            "SNorSaveexecB64" => ~(source | oldExec),
            _ => ~(oldExec ^ source),
        };

        WriteScalarPair(registers, destination.Value, oldExec, ref execMask);
        execMask = MaskWaveValue(newExec);
        WriteScalarPair(registers, 126, execMask, ref execMask);
        scalarConditionCode = execMask != 0;
        return true;
    }

    private static bool TryEvaluateScalarOperand64(
        Gen5Operand operand,
        uint[] registers,
        ulong execMask,
        out ulong value)
    {
        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value == 126)
        {
            value = execMask;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value == NullScalarRegister)
        {
            value = 0;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value < ScalarRegisterCount - 1)
        {
            value = ReadScalarRegister(registers, operand.Value) |
                ((ulong)ReadScalarRegister(registers, operand.Value + 1) << 32);
            return true;
        }

        if (TryEvaluateScalarOperand(operand, registers, out var low))
        {
            value = operand.Kind == Gen5OperandKind.EncodedConstant &&
                    operand.Value is >= 193 and <= 208
                ? ulong.MaxValue << 32 | low
                : low;
            return true;
        }

        value = 0;
        return false;
    }

    private static void WriteScalarPair(
        uint[] registers,
        uint destination,
        ulong value,
        ref ulong execMask)
    {
        if (destination >= ScalarRegisterCount - 1 ||
            destination == NullScalarRegister)
        {
            return;
        }

        if (destination == 126)
        {
            value = MaskWaveValue(value);
        }

        WriteScalarRegister(registers, destination, (uint)value);
        WriteScalarRegister(registers, destination + 1, (uint)(value >> 32));
        if (destination == 126)
        {
            execMask = value;
        }
    }

    private static ulong MaskWaveValue(ulong value) => value & RdnaWaveMask;

    private static ulong ExpandWholeQuadMask(ulong value)
    {
        value = MaskWaveValue(value);
        var quadActive =
            (value | (value >> 1) | (value >> 2) | (value >> 3)) &
            0x1111_1111UL;
        return MaskWaveValue(
            quadActive |
            (quadActive << 1) |
            (quadActive << 2) |
            (quadActive << 3));
    }

    private static uint ReadScalarRegister(uint[] registers, uint register) =>
        register == NullScalarRegister ? 0 : registers[register];

    private static void WriteScalarRegister(
        uint[] registers,
        uint register,
        uint value)
    {
        if (register != NullScalarRegister)
        {
            registers[register] = value;
        }
    }

    private static bool SignedAddOverflow(uint left, uint right, uint result) =>
        ((left ^ result) & (right ^ result) & 0x80000000u) != 0;

    private static bool SignedSubOverflow(uint left, uint right, uint result) =>
        ((left ^ right) & (left ^ result) & 0x80000000u) != 0;

    private static bool TryExecuteScalarCompare(
        Gen5ShaderInstruction instruction,
        uint[] registers,
        out bool scalarConditionCode,
        out string error)
    {
        scalarConditionCode = false;
        error = string.Empty;
        if (instruction.Sources.Count != 2 ||
            !TryEvaluateScalarOperand(instruction.Sources[0], registers, out var left) ||
            !TryEvaluateScalarOperand(instruction.Sources[1], registers, out var right))
        {
            error = $"scalar-compare-source pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        if (instruction.Opcode is "SBitcmp0B32" or "SBitcmp1B32")
        {
            var bit = (int)(right & 31u);
            var isSet = ((left >> bit) & 1u) != 0;
            scalarConditionCode = instruction.Opcode == "SBitcmp1B32" ? isSet : !isSet;
            return true;
        }

        if (instruction.Opcode is "SBitcmp0B64" or "SBitcmp1B64")
        {
            if (!TryEvaluateScalarOperand64(instruction.Sources[0], registers, ulong.MaxValue, out var wide))
            {
                error = $"scalar-bitcmp-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var bit = (int)(right & 63u);
            var isSet = ((wide >> bit) & 1UL) != 0;
            scalarConditionCode = instruction.Opcode == "SBitcmp1B64" ? isSet : !isSet;
            return true;
        }

        scalarConditionCode = instruction.Opcode switch
        {
            "SCmpEqI32" => (int)left == (int)right,
            "SCmpLgI32" => (int)left != (int)right,
            "SCmpGtI32" => (int)left > (int)right,
            "SCmpGeI32" => (int)left >= (int)right,
            "SCmpLtI32" => (int)left < (int)right,
            "SCmpLeI32" => (int)left <= (int)right,
            "SCmpEqU32" => left == right,
            "SCmpLgU32" => left != right,
            "SCmpGtU32" => left > right,
            "SCmpGeU32" => left >= right,
            "SCmpLtU32" => left < right,
            "SCmpLeU32" => left <= right,
            _ => false,
        };
        if (!instruction.Opcode.StartsWith("SCmp", StringComparison.Ordinal))
        {
            error = $"unsupported-scalar-compare pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        return true;
    }

    private static bool TryExecuteScalarCompareK(
        Gen5ShaderInstruction instruction,
        uint[] registers,
        out bool scalarConditionCode,
        out string error)
    {
        scalarConditionCode = false;
        error = string.Empty;
        if (instruction.Destinations.Count != 1 ||
            instruction.Destinations[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount,
            } destination)
        {
            error = $"scalar-comparek-destination pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        var left = ReadScalarRegister(registers, destination.Value);
        var encodedImmediate = instruction.Sources[0].Value & 0xFFFFu;
        var right = instruction.Opcode.EndsWith("U32", StringComparison.Ordinal)
            ? encodedImmediate
            : unchecked((uint)(short)encodedImmediate);
        scalarConditionCode = instruction.Opcode switch
        {
            "SCmpkEqI32" => (int)left == (int)right,
            "SCmpkLgI32" => (int)left != (int)right,
            "SCmpkGtI32" => (int)left > (int)right,
            "SCmpkGeI32" => (int)left >= (int)right,
            "SCmpkLtI32" => (int)left < (int)right,
            "SCmpkLeI32" => (int)left <= (int)right,
            "SCmpkEqU32" => left == right,
            "SCmpkLgU32" => left != right,
            "SCmpkGtU32" => left > right,
            "SCmpkGeU32" => left >= right,
            "SCmpkLtU32" => left < right,
            "SCmpkLeU32" => left <= right,
            _ => false,
        };
        if (!instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
        {
            error = $"unsupported-scalar-comparek pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        return true;
    }

    private static bool TryExecuteScalarLoad(
        CpuContext ctx,
        Gen5ShaderState state,
        Gen5ShaderInstruction instruction,
        Gen5ScalarMemoryControl control,
        uint[] scalarRegisters,
        List<Gen5GlobalMemoryBinding> globalMemoryBindings,
        Dictionary<(uint ScalarAddress, ulong BaseAddress), Gen5GlobalMemoryBinding> globalMemoryByAddress,
        IReadOnlySet<uint> runtimeScalarRegisters,
        bool recordBinding,
        out string error)
    {
        error = string.Empty;
        if (instruction.Sources.Count == 0 ||
            instruction.Sources[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount - 1,
            } scalarBase)
        {
            error = $"invalid-scalar-base pc=0x{instruction.Pc:X}";
            return false;
        }

        var isBufferLoad =
            instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal);
        BufferDescriptor bufferDescriptor = default;
        var hasBufferDescriptor =
            isBufferLoad &&
            TryDecodeBufferDescriptor(
                scalarRegisters,
                scalarBase.Value,
                strictType: false,
                out bufferDescriptor);
        var baseAddress = hasBufferDescriptor
            ? bufferDescriptor.BaseAddress
            : scalarRegisters[scalarBase.Value] |
              ((ulong)scalarRegisters[scalarBase.Value + 1] << 32);
        var dynamicOffset = control.DynamicOffsetRegister is { } offsetRegister &&
                            offsetRegister < ScalarRegisterCount
            ? scalarRegisters[offsetRegister]
            : 0;
        var immediateOffset = (ulong)(long)control.ImmediateOffsetBytes;
        var byteOffset = unchecked(immediateOffset + dynamicOffset);
        var address = unchecked(
            baseAddress +
            byteOffset) & ~3UL;
        var bufferUnbound =
            isBufferLoad &&
            (!hasBufferDescriptor ||
             bufferDescriptor.SizeBytes == 0 ||
             (scalarRegisters[scalarBase.Value] == 0 &&
              scalarRegisters[scalarBase.Value + 1] == 0 &&
              scalarBase.Value + 3 < ScalarRegisterCount &&
              scalarRegisters[scalarBase.Value + 2] == 0 &&
              scalarRegisters[scalarBase.Value + 3] == 0));
        var scalarPointerUnbound = ShouldTreatScalarPointerAsUnbound(
            isBufferLoad,
            address,
            _strictScalarLoad);
        if (scalarPointerUnbound)
        {
            TraceScalarPointerFallback(
                state,
                instruction,
                scalarBase.Value,
                scalarRegisters,
                control,
                baseAddress,
                dynamicOffset);
        }
        var bufferSize = ulong.MaxValue;
        if (recordBinding && isBufferLoad)
        {
            bufferSize = hasBufferDescriptor ? bufferDescriptor.SizeBytes : ulong.MaxValue;

            var key = (scalarBase.Value, bufferDescriptor.BaseAddress);
            if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
            {
                if (existingBinding.InstructionPcs is List<uint> instructionPcs &&
                    !instructionPcs.Contains(instruction.Pc))
                {
                    instructionPcs.Add(instruction.Pc);
                }
            }
            else
            {
                var pooled = TryReadGlobalMemory(
                    ctx,
                    bufferDescriptor.BaseAddress,
                    bufferDescriptor.SizeBytes,
                    out var data,
                    out var dataLength);
                var binding = new Gen5GlobalMemoryBinding(
                    scalarBase.Value,
                    bufferDescriptor.BaseAddress,
                    new List<uint> { instruction.Pc },
                    data,
                    dataLength,
                    DataPooled: pooled)
                {
                    WriteBackToGuest = pooled,
                };
                globalMemoryByAddress.Add(key, binding);
                globalMemoryBindings.Add(binding);
            }
        }
        else if (recordBinding && baseAddress != 0)
        {
            var key = (scalarBase.Value, baseAddress);
            if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
            {
                if (existingBinding.InstructionPcs is List<uint> instructionPcs &&
                    !instructionPcs.Contains(instruction.Pc))
                {
                    instructionPcs.Add(instruction.Pc);
                }
            }
            else
            {
                var requiredBytes = Math.Max(
                    256UL * 1024UL,
                    checked(
                        byteOffset +
                        (ulong)Math.Max(instruction.Destinations.Count, 1) *
                        sizeof(uint)));
                requiredBytes = Math.Min(
                    (requiredBytes + 4095UL) & ~4095UL,
                    MaxGlobalMemoryBindingBytes);
                var pooled = TryReadGlobalMemory(
                    ctx,
                    baseAddress,
                    requiredBytes,
                    out var data,
                    out var dataLength);
                var binding = new Gen5GlobalMemoryBinding(
                    scalarBase.Value,
                    baseAddress,
                    new List<uint> { instruction.Pc },
                    data,
                    dataLength,
                    DataPooled: pooled)
                {
                    WriteBackToGuest = pooled,
                };
                globalMemoryByAddress.Add(key, binding);
                globalMemoryBindings.Add(binding);
            }
        }

        if (!bufferUnbound && !scalarPointerUnbound && address == 0)
        {
            error = FormatScalarLoadError(
                "invalid-load-address",
                instruction,
                scalarBase.Value,
                scalarRegisters,
                control,
                baseAddress,
                dynamicOffset,
                address);
            return false;
        }

        for (var index = 0; index < instruction.Destinations.Count; index++)
        {
            var destination = instruction.Destinations[index];
            if (destination.Kind != Gen5OperandKind.ScalarRegister ||
                destination.Value >= ScalarRegisterCount)
            {
                error = FormatScalarLoadError(
                    "invalid-scalar-destination",
                    instruction,
                    scalarBase.Value,
                    scalarRegisters,
                    control,
                    baseAddress,
                    dynamicOffset,
                    address);
                return false;
            }

            var componentOffset = unchecked(byteOffset + (ulong)(index * sizeof(uint)));
            if (bufferUnbound ||
                scalarPointerUnbound ||
                isBufferLoad &&
                (componentOffset >= bufferSize ||
                 bufferSize - componentOffset < sizeof(uint)))
            {
                WriteScalarRegister(scalarRegisters, destination.Value, 0);
                continue;
            }

            if (!TryReadUInt32(
                    ctx,
                    address + (ulong)(index * sizeof(uint)),
                    out var value))
            {
                if (isBufferLoad || !_strictScalarLoad)
                {
                    WriteScalarRegister(scalarRegisters, destination.Value, 0);
                    continue;
                }

                error = FormatScalarLoadError(
                    "scalar-load-failed",
                    instruction,
                    scalarBase.Value,
                    scalarRegisters,
                    control,
                    baseAddress,
                    dynamicOffset,
                    address);
                return false;
            }

            WriteScalarRegister(scalarRegisters, destination.Value, value);
        }

        return true;
    }

    private static bool ShouldTreatScalarPointerAsUnbound(
        bool isBufferLoad,
        ulong address,
        bool strictScalarLoad) =>
        !isBufferLoad && address == 0 && !strictScalarLoad;

    private static void TraceScalarPointerFallback(
        Gen5ShaderState state,
        Gen5ShaderInstruction instruction,
        uint scalarBase,
        IReadOnlyList<uint> scalarRegisters,
        Gen5ScalarMemoryControl control,
        ulong baseAddress,
        ulong dynamicOffset)
    {
        lock (_scalarFallbackTraceGate)
        {
            if (!_tracedScalarFallbacks.Add((state.Program.Address, instruction.Pc)))
            {
                return;
            }
        }

        var definitions = state.Program.Instructions
            .Where(candidate =>
                candidate.Pc < instruction.Pc &&
                candidate.Destinations.Any(destination =>
                    destination.Kind == Gen5OperandKind.ScalarRegister &&
                    destination.Value is var register &&
                    register >= scalarBase && register <= scalarBase + 1))
            .TakeLast(8)
            .Select(candidate =>
                $"0x{candidate.Pc:X}:{candidate.Opcode}[" +
                string.Join(',', candidate.Words.Select(word => $"{word:X8}")) + "]")
            .ToArray();
        var userData = string.Join(
            ',',
            state.UserData.Take(32).Select((value, index) => $"s{index}=0x{value:X8}"));
        var high = scalarBase + 1 < scalarRegisters.Count
            ? scalarRegisters[(int)scalarBase + 1]
            : 0;
        Console.Error.WriteLine(
            $"[LOADER][WARN] agc.scalar_pointer_fallback " +
            $"shader=0x{state.Program.Address:X16} pc=0x{instruction.Pc:X} " +
            $"op={instruction.Opcode} base=s{scalarBase}" +
            $"[0x{scalarRegisters[(int)scalarBase]:X8}:0x{high:X8}] " +
            $"base_addr=0x{baseAddress:X16} imm={control.ImmediateOffsetBytes} " +
            $"dynamic={dynamicOffset} definitions=[{string.Join(';', definitions)}] " +
            $"user_data=[{userData}] metadata=" +
            $"{(state.Metadata is null ? "missing" : $"srt={state.Metadata.ShaderResourceTableSizeDwords},eud={state.Metadata.ExtendedUserDataSizeDwords}")}");
    }

    [Conditional("DEBUG")]
    private static void RunScalarLoadSelfChecks()
    {
        Debug.Assert(
            ShouldTreatScalarPointerAsUnbound(
                isBufferLoad: false,
                address: 0,
                strictScalarLoad: false),
            "A null non-strict scalar pointer must read as zero instead of dropping the shader pass.");
        Debug.Assert(
            !ShouldTreatScalarPointerAsUnbound(
                isBufferLoad: false,
                address: 0,
                strictScalarLoad: true),
            "Strict scalar-load diagnostics must continue rejecting null pointers.");
        Debug.Assert(
            !ShouldTreatScalarPointerAsUnbound(
                isBufferLoad: true,
                address: 0,
                strictScalarLoad: false),
            "Buffer descriptor null handling must remain on the buffer-unbound path.");
        Debug.Assert(
            !ShouldTreatScalarPointerAsUnbound(
                isBufferLoad: false,
                address: 0x1000,
                strictScalarLoad: false),
            "A valid scalar pointer must not be treated as an unbound resource.");
    }

    private static bool TryDecodeBufferDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        uint scalarBase,
        bool strictType,
        out BufferDescriptor descriptor)
    {
        descriptor = default;
        if (scalarBase + 3 >= scalarRegisters.Count)
        {
            return false;
        }

        var word0 = scalarRegisters[(int)scalarBase];
        var word1 = scalarRegisters[(int)scalarBase + 1];
        var word2 = scalarRegisters[(int)scalarBase + 2];
        var word3 = scalarRegisters[(int)scalarBase + 3];
        if (word0 == 0 &&
            word1 == 0 &&
            word2 == 0 &&
            word3 == 0)
        {
            descriptor = new BufferDescriptor(0, 0, 0, 0, 0, 0);
            return true;
        }

        var type = word3 >> 30;
        if (type != 0)
        {
            if (strictType)
            {
                return false;
            }

            descriptor = new BufferDescriptor(0, 0, 0, 0, 0, 0);
            return true;
        }

        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var dataFormat = 0u;
        var numberFormat = 0u;
        if (unifiedFormat != 0)
        {
            if (!Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decodedFormat) ||
                decodedFormat.IsBlockCompressed)
            {
                return false;
            }

            dataFormat = decodedFormat.DataFormat;
            numberFormat = decodedFormat.NumberFormat;
        }

        var sizeBytes = stride == 0
            ? word2
            : (ulong)stride * word2;
        descriptor = new BufferDescriptor(
            baseAddress,
            stride,
            word2,
            sizeBytes,
            numberFormat,
            dataFormat);
        return true;
    }

    private static bool TryReadUserDataScalarLoad(
        Gen5ShaderState state,
        Gen5ShaderInstruction instruction,
        Gen5ScalarMemoryControl control,
        ulong byteOffset,
        int componentIndex,
        out uint value)
    {
        value = 0;
        if (!instruction.Opcode.StartsWith("SLoadDword", StringComparison.Ordinal) ||
            state.Metadata is not { } metadata ||
            (byteOffset & 3) != 0)
        {
            return false;
        }

        var baseDwordOffset = byteOffset >> 2;
        if (baseDwordOffset > int.MaxValue)
        {
            return false;
        }

        var dwordOffset = (long)baseDwordOffset + componentIndex;
        if (dwordOffset < 0 ||
            dwordOffset >= state.UserData.Count ||
            !IsShaderUserDataResourceOffset(metadata, (uint)dwordOffset))
        {
            return false;
        }

        value = state.UserData[(int)dwordOffset];
        return true;
    }

    private static bool IsShaderUserDataResourceOffset(
        Gen5ShaderMetadata metadata,
        uint dwordOffset)
    {
        if (dwordOffset < metadata.ShaderResourceTableSizeDwords)
        {
            return true;
        }

        foreach (var resource in metadata.Resources)
        {
            var dwordCount = resource.Kind switch
            {
                Gen5ShaderResourceKind.ReadOnlyTexture or
                    Gen5ShaderResourceKind.ReadWriteTexture => 8u,
                Gen5ShaderResourceKind.Sampler or
                    Gen5ShaderResourceKind.ConstantBuffer => 4u,
                _ => 1u,
            };
            if (dwordOffset >= resource.OffsetDwords &&
                dwordOffset < resource.OffsetDwords + dwordCount)
            {
                return true;
            }
        }

        return metadata.DirectResources.Values.Any(offset => offset == dwordOffset);
    }

    private static string FormatScalarLoadError(
        string reason,
        Gen5ShaderInstruction instruction,
        uint scalarBase,
        uint[] scalarRegisters,
        Gen5ScalarMemoryControl control,
        ulong baseAddress,
        uint dynamicOffset,
        ulong address)
    {
        var high = scalarBase + 1 < scalarRegisters.Length
            ? scalarRegisters[scalarBase + 1]
            : 0;
        var dynamic = control.DynamicOffsetRegister is { } register
            ? $" dyn=s{register}=0x{dynamicOffset:X8}"
            : " dyn=none";
        var descriptor = string.Join(
            ':',
            Enumerable.Range(0, 4).Select(index =>
                scalarBase + index < scalarRegisters.Length
                    ? $"{scalarRegisters[scalarBase + index]:X8}"
                    : "????????"));
        var words = string.Join(',', instruction.Words.Select(word => $"{word:X8}"));
        return
            $"{reason} pc=0x{instruction.Pc:X} op={instruction.Opcode} " +
            $"words=[{words}] base=s{scalarBase}[0x{scalarRegisters[scalarBase]:X8}:0x{high:X8}] " +
            $"desc=[{descriptor}] " +
            $"base_addr=0x{baseAddress:X16} imm={control.ImmediateOffsetBytes}" +
            $"{dynamic} address=0x{address:X16}";
    }

    private static bool TryEvaluateScalarOperand(
        Gen5Operand operand,
        uint[] scalarRegisters,
        out uint value)
    {
        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value < ScalarRegisterCount)
        {
            value = ReadScalarRegister(scalarRegisters, operand.Value);
            return true;
        }

        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            value = operand.Value;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.EncodedConstant)
        {
            return TryDecodeInlineConstant(operand.Value, out value);
        }

        value = 0;
        return false;
    }

    private static bool TryEvaluateScalarOperand(
        Gen5Operand operand,
        uint[] scalarRegisters,
        ulong execMask,
        bool scalarConditionCode,
        out uint value)
    {
        if (operand.Kind == Gen5OperandKind.EncodedConstant)
        {
            switch (operand.Value)
            {
                // RDNA scalar-source special registers. These encodings share
                // the inline-constant field but are evaluated from wave state.
                case 251: // VCCZ
                    value = (scalarRegisters[106] | scalarRegisters[107]) == 0 ? 1u : 0u;
                    return true;
                case 252: // EXECZ
                    value = execMask == 0 ? 1u : 0u;
                    return true;
                case 253: // SCC
                    value = scalarConditionCode ? 1u : 0u;
                    return true;
            }
        }

        return TryEvaluateScalarOperand(operand, scalarRegisters, out value);
    }

    private static bool TryDecodeInlineConstant(uint encoded, out uint value)
    {
        if (encoded == 125)
        {
            value = 0;
            return true;
        }

        if (encoded is >= 128 and <= 192)
        {
            value = encoded - 128;
            return true;
        }

        if (encoded is >= 193 and <= 208)
        {
            value = unchecked((uint)-(int)(encoded - 192));
            return true;
        }

        var floatingPoint = encoded switch
        {
            240 => 0.5f,
            241 => -0.5f,
            242 => 1.0f,
            243 => -1.0f,
            244 => 2.0f,
            245 => -2.0f,
            246 => 4.0f,
            247 => -4.0f,
            248 => 1.0f / (2.0f * MathF.PI),
            _ => float.NaN,
        };
        if (float.IsNaN(floatingPoint))
        {
            value = 0;
            return false;
        }

        value = BitConverter.SingleToUInt32Bits(floatingPoint);
        return true;
    }

    private static uint ReverseBits(uint value)
    {
        value = (value >> 1 & 0x55555555u) | ((value & 0x55555555u) << 1);
        value = (value >> 2 & 0x33333333u) | ((value & 0x33333333u) << 2);
        value = (value >> 4 & 0x0F0F0F0Fu) | ((value & 0x0F0F0F0Fu) << 4);
        value = (value >> 8 & 0x00FF00FFu) | ((value & 0x00FF00FFu) << 8);
        return value >> 16 | value << 16;
    }

    private static bool TryCopyRegisters(
        uint[] registers,
        uint start,
        int count,
        out IReadOnlyList<uint> values)
    {
        values = [];
        if (start > (uint)(registers.Length - count))
        {
            return false;
        }

        var copy = new uint[count];
        Array.Copy(registers, (int)start, copy, 0, count);
        values = copy;
        return true;
    }

    private static bool UsesSampler(string opcode) =>
        opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
        opcode.StartsWith("ImageGather", StringComparison.Ordinal);

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }
}
