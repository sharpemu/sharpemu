// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// Interns descriptor sources, builds the dense buffer, image, sampler and pair tables
// and patches each access with its index. A failure names hash, stage and pc.
public sealed partial class ResourceTracker
{
    private const uint SamplerBorderClampMask = (1u << 2) | (1u << 5) | (1u << 8);
    private const uint SamplerDword3ReservedMask = 0x3FFF_F000u;

    private readonly ShaderResourcePlan _plan;
    private readonly ScalarValueGraph _graph;
    private readonly ShaderResourceInfo _info = new();
    private readonly List<DescriptorSource> _sources = [];
    private readonly List<(int Index, uint Resource, uint Sampler, bool HasSampler)> _memoryPatches = [];
    private readonly List<IndirectImagePlan> _indirectImages = [];
    private readonly List<BufferCandidateTablePlan> _bufferCandidateTables = [];
    private readonly Dictionary<ScalarValue, List<ScalarValue>> _uses;
    private readonly Dictionary<int, List<ScalarValue>> _readsByMemory = [];

    private sealed class IndirectImagePlan
    {
        public ScalarValue Handle = null!;
        public uint Source;
        public ScalarValue Key = null!;
        public uint HeapSource;
        public bool KeyIsAddressOffset;
        public bool SuppressMemoryReads = true;
        public int[] Memory = new int[8];
        public ScalarValue[] Reads = new ScalarValue[8];
    }

    public sealed record Result(
        IReadOnlyList<DescriptorSource> Sources,
        ShaderResourceInfo Info,
        IReadOnlyList<IndirectImageAccess> IndirectImages,
        IReadOnlySet<ScalarValue> IndirectReads,
        IReadOnlyList<BufferCandidateTablePlan> BufferCandidateTables);

    private ResourceTracker(ShaderResourcePlan plan)
    {
        _plan = plan;
        _graph = plan.Graph;
        var roots = new List<ScalarValue>();
        foreach (var access in plan.Accesses)
        {
            if (access?.Handle is { } handle)
            {
                roots.Add(handle);
            }

            if (access?.SamplerHandle is { } sampler)
            {
                roots.Add(sampler);
            }

            if (access?.Read is { } read)
            {
                roots.Add(read);
            }
        }

        roots.AddRange(plan.TableReads.Select(read => read.Value));
        roots.AddRange(plan.DynamicReads);
        _uses = _graph.CollectUses(roots);
        foreach (var value in _uses.Keys.Concat(roots))
        {
            if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord)
            {
                if (!_readsByMemory.TryGetValue(value.MemoryIndex, out var list))
                {
                    list = [];
                    _readsByMemory[value.MemoryIndex] = list;
                }

                if (!list.Contains(value))
                {
                    list.Add(value);
                }
            }
        }
    }

    public static Result Track(ShaderResourcePlan plan) => new ResourceTracker(plan).Run();

    private Result Run()
    {
        PlanIndirectImages();
        for (var index = 0; index < _plan.Memory.Count; index++)
        {
            Collect(index);
        }

        LinkImageAliases();
        foreach (var (index, resource, sampler, hasSampler) in _memoryPatches)
        {
            var memory = _plan.Memory[index];
            memory.Resource = resource;
            if (hasSampler)
            {
                memory.Sampler = sampler;
            }
        }

        var indirectReads = new HashSet<ScalarValue>();
        var indirectAccesses = new List<IndirectImageAccess>();
        foreach (var plan in _indirectImages)
        {
            if (plan.SuppressMemoryReads)
            {
                foreach (var index in plan.Memory)
                {
                    _plan.Memory[index].PlanningOnly = true;
                }

                foreach (var read in plan.Reads)
                {
                    indirectReads.Add(read);
                }
            }

            for (var index = 0; index < _plan.Accesses.Length; index++)
            {
                if (_plan.Accesses[index]?.Handle is { } handle && ReferenceEquals(handle, plan.Handle))
                {
                    indirectAccesses.Add(new IndirectImageAccess(index, plan.Key, plan.HeapSource)
                    {
                        KeyIsAddressOffset = plan.KeyIsAddressOffset,
                    });
                }
            }
        }

        return new Result(_sources, _info, indirectAccesses, indirectReads, _bufferCandidateTables);
    }

    private ResourcePlanException Failure(uint pc, string reason) =>
        new($"shader resource tracking: hash=0x{_plan.Hash:X16} stage={_plan.Stage} pc=0x{pc:X8} {reason}");

    /// <summary>
    /// Names the instructions that produced the undefined leaves <paramref name="value"/>
    /// rests on, so a rejected descriptor dword points at an opcode.
    /// </summary>
    private string DescribeUndefinedLeaves(ScalarValue value)
    {
        var origins = new List<string>();
        var seen = new HashSet<ScalarValue>();
        var pending = new Stack<ScalarValue>();
        pending.Push(value);
        while (pending.Count != 0 && origins.Count < 8)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (current.Kind == ScalarValueKind.Undefined &&
                _graph.TryGetUndefinedOrigin(current, out var origin))
            {
                var described = $"{origin.Opcode}@0x{origin.Pc:X}";
                if (!origins.Contains(described))
                {
                    origins.Add(described);
                }

                continue;
            }

            foreach (var operand in current.Operands)
            {
                pending.Push(operand);
            }
        }

        return origins.Count == 0 ? string.Empty : $" (undefined from: {string.Join(", ", origins)})";
    }

    private bool HasUndefinedOrigin(ScalarValue value, string opcodePrefix)
    {
        var seen = new HashSet<ScalarValue>();
        var pending = new Stack<ScalarValue>();
        pending.Push(value);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (current.Kind == ScalarValueKind.Undefined &&
                _graph.TryGetUndefinedOrigin(current, out var origin) &&
                origin.Opcode.StartsWith(opcodePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var operand in current.Operands)
            {
                pending.Push(operand);
            }
        }

        return false;
    }

    // ---- descriptor sources ----

    // Copies a handle's dwords into a source. A sampler that no clamp axis sets to
    // border mode drops its border colour, which is unused then.
    private DescriptorSource MakeSource(ScalarValue handle, uint width, bool sampler, bool sampleAdjust, uint pc)
    {
        if (handle.Operands.Length != width)
        {
            throw Failure(pc, $"{handle.Kind} has {handle.Operands.Length} descriptor dwords, expected {width}");
        }

        var dwords = (ScalarValue[])handle.Operands.Clone();
        if (sampleAdjust)
        {
            dwords[3] = CanonicalizeSampleAdjustDword3(dwords[3]);
        }

        var dword0 = dwords[0];
        if (sampler && dword0.IsConstant && (dword0.ConstantU32 & SamplerBorderClampMask) == 0)
        {
            dwords[3] = _graph.Constant(0u);
        }

        return new DescriptorSource { Dwords = dwords };
    }

    private static uint PossibleBits(ScalarValue value)
    {
        if (value.IsConstant)
        {
            return value.Type == ScalarValueType.U32 ? value.ConstantU32 : uint.MaxValue;
        }

        if (value.Kind != ScalarValueKind.Operation)
        {
            return uint.MaxValue;
        }

        return value.Operation switch
        {
            ScalarOperation.And32 => PossibleBits(value.Operands[0]) & PossibleBits(value.Operands[1]),
            ScalarOperation.Or32 => PossibleBits(value.Operands[0]) | PossibleBits(value.Operands[1]),
            ScalarOperation.ShiftLeft32 when value.Operands[1].IsConstant =>
                PossibleBits(value.Operands[0]) << (int)(value.Operands[1].ConstantU32 & 31),
            _ => uint.MaxValue,
        };
    }

    private ScalarValue CanonicalizeSampleAdjustDword3(ScalarValue value)
    {
        for (;;)
        {
            if (value.Kind != ScalarValueKind.Operation || value.Operation != ScalarOperation.Or32)
            {
                return value;
            }

            var left = value.Operands[0];
            var right = value.Operands[1];
            var leftReserved = (PossibleBits(left) & ~SamplerDword3ReservedMask) == 0;
            var rightReserved = (PossibleBits(right) & ~SamplerDword3ReservedMask) == 0;
            if (leftReserved && rightReserved)
            {
                return _graph.Constant(0u);
            }

            if (leftReserved)
            {
                value = right;
            }
            else if (rightReserved)
            {
                value = left;
            }
            else
            {
                return value;
            }
        }
    }

    private bool ValidateSource(DescriptorSource source, out uint badDword) => ValidateSource(source, out badDword, out _);

    private bool ValidateSource(DescriptorSource source, out uint badDword, out bool controlDependent)
    {
        controlDependent = false;
        for (badDword = 0; badDword < source.DwordCount; badDword++)
        {
            var dword = source.Dwords[badDword];
            if (dword.Type != ScalarValueType.U32)
            {
                return false;
            }

            if (!_plan.ValidateRuntimeValue(dword, out controlDependent))
            {
                return false;
            }
        }

        return true;
    }

    private uint InternSource(DescriptorSource source)
    {
        for (var candidate = 0; candidate < _sources.Count; candidate++)
        {
            var current = _sources[candidate];
            if (current.DwordCount != source.DwordCount || !Equals(current.IndirectImage, source.IndirectImage))
            {
                continue;
            }

            var same = true;
            for (var index = 0; index < source.DwordCount && same; index++)
            {
                same = _graph.Equivalent(current.Dwords[index], source.Dwords[index]);
            }

            if (same)
            {
                return (uint)candidate;
            }
        }

        _sources.Add(source);
        return (uint)(_sources.Count - 1);
    }

    // Records a proven bounded SRT candidate table, reusing an equivalent table so several
    // accesses share one native candidate layout. Returns false when the SRT root itself is
    // not a valid runtime source, in which case the access stays on its old lowering.
    private bool InternBufferCandidateTable(BufferCandidateTablePlan table, uint pc, int memoryIndex)
    {
        table.SourceSrtResource = InternRuntimeSource(table.SrtHandle, pc);
        if (table.SourceSrtResource == DescriptorConstants.NoIndex)
        {
            return false;
        }

        for (var existing = 0; existing < _bufferCandidateTables.Count; existing++)
        {
            var current = _bufferCandidateTables[existing];
            if (!_graph.Equivalent(current.SrtHandle, table.SrtHandle) ||
                !_graph.Equivalent(current.OffsetExpression, table.OffsetExpression))
            {
                continue;
            }

            _bufferCandidateTables[existing] = current.WithMemoryIndex(memoryIndex);
            return true;
        }

        _bufferCandidateTables.Add(table);
        return true;
    }

    // A fully-resolvable descriptor source for the static SRT root a candidate table reads
    // through. Its dwords stay runtime values; this only records them for the materialiser.
    private uint InternRuntimeSource(ScalarValue handle, uint pc)
    {
        var source = MakeSource(handle, 4, false, false, pc);
        return ValidateSource(source, out _) ? InternSource(source) : DescriptorConstants.NoIndex;
    }

    private bool IsHostBufferHandle(ScalarValue? handle) =>
        handle is { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } &&
        handle.Operands.All(dword => dword.Type == ScalarValueType.U32 && _plan.ValidateRuntimeValue(dword));

    private bool IsDeviceLoadedBufferHandle(ScalarValue? handle) =>
        handle is { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } &&
        handle.Operands.All(dword =>
            dword.Type == ScalarValueType.U32 &&
            (_plan.ValidateRuntimeValue(dword) || DependsOnScalarBufferWord(dword))) &&
        handle.Operands.Any(DependsOnScalarBufferWord);

    private static bool DependsOnScalarBufferWord(ScalarValue value)
    {
        var pending = new Stack<ScalarValue>();
        var visited = new HashSet<ScalarValue>();
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (current.Kind == ScalarValueKind.ScalarBufferWord)
            {
                return true;
            }

            foreach (var operand in current.Operands)
            {
                pending.Push(operand);
            }
        }

        return false;
    }

    private uint GetHandleSource(
        ScalarValue? handle,
        ScalarValueKind expected,
        uint width,
        uint pc,
        bool sampler = false,
        bool sampleAdjust = false,
        string? memoryOpcode = null,
        MemoryAccess memoryAccess = MemoryAccess.Read)
    {
        if (handle is null || handle.Kind != expected)
        {
            throw Failure(pc, $"memory operation requires {expected}");
        }

        var source = MakeSource(handle, width, sampler, sampleAdjust, pc);
        var nonContiguousImage = expected == ScalarValueKind.ImageHandle && !IsContiguousScalarBufferRecord(source);

        // Image descriptors loaded straight from a scalar buffer at a dynamically-uniform
        // offset (e.g. a bindless material heap entry read via S_BUFFER_LOAD, without going
        // through the explicit TryMakeIndirectImage/TryMakeDirectImage heap-record shapes)
        // used to be rejected outright here. But RuntimeValueValidator/RuntimeValueEvaluator
        // already handle ScalarBufferWord dwords generically (the same mechanism buffer
        // descriptors rely on via MaterializationSources), so let ValidateSource below be the
        // single source of truth instead of a narrower, ImageHandle-specific blanket ban.
        var badDword = 0u;
        var controlDependent = false;
        if (nonContiguousImage || !ValidateSource(source, out badDword, out controlDependent))
        {
            // A bindless image/sampler descriptor whose dwords resolve through a
            // control-dependent phi (e.g. a hash-table/linear-probe material lookup, as seen
            // in Ghost of Yotei) has no single compile-time source: real support needs
            // GPU-side dynamic descriptor indexing, which this resource tracker doesn't
            // implement. Rather than fail shader recompilation outright, degrade to a null
            // descriptor for that one access and let it read as a null/black texture,
            // mirroring KytyPS5's fallback for the same case (feat/shader-control-dependent-
            // descriptor). Buffer/sampler-adjacent handles or any other validation failure
            // still hard-fail, since those aren't safe to silently zero.
            var dynamicImageFallback = expected is (ScalarValueKind.ImageHandle or ScalarValueKind.SamplerHandle) &&
                (controlDependent || HasUndefinedOrigin(source.Dwords[badDword], "BufferLoadFormat") ||
                 (nonContiguousImage && source.Dwords.Any(dword => HasUndefinedOrigin(dword, "SAndB32"))));
            if (dynamicImageFallback)
            {
                source = new DescriptorSource
                {
                    Dwords = Enumerable.Repeat(_graph.Constant(0u), (int)source.DwordCount).ToArray(),
                };
            }
            else
            {
                throw Failure(
                    pc,
                    $"{memoryOpcode ?? "memory"} ({memoryAccess}) {expected} dword {badDword} is not a valid runtime value" +
                        DescribeUndefinedLeaves(source.Dwords[badDword]) +
                        $" (value: {DescribeValueShape(source.Dwords[badDword])})");
            }
        }

        return InternSource(source);
    }

    private string DescribeValueShape(ScalarValue value, int depth = 0)
    {
        if (depth >= 4)
        {
            return $"{value.Kind}#{value.Id}";
        }

        if (value.IsConstant)
        {
            return value.Type == ScalarValueType.U64
                ? $"0x{value.ConstantU64:X}"
                : value.Type == ScalarValueType.Bool ? (value.ConstantBool ? "true" : "false") : $"0x{value.ConstantU32:X}";
        }

        if (value.Kind == ScalarValueKind.Operation)
        {
            return $"{value.Operation}#{value.Id}({string.Join(',', value.Operands.Select(operand => DescribeValueShape(operand, depth + 1)))})";
        }

        if (value.Kind == ScalarValueKind.Undefined && _graph.TryGetUndefinedOrigin(value, out var origin))
        {
            return $"Undefined#{value.Id}({origin.Opcode}@0x{origin.Pc:X})";
        }

        return $"{value.Kind}#{value.Id}";
    }

    // An image descriptor read from a scalar buffer must be one contiguous record.
    private bool IsContiguousScalarBufferRecord(DescriptorSource source)
    {
        if (!source.Dwords.Any(dword => dword.Kind == ScalarValueKind.ScalarBufferWord))
        {
            return true;
        }

        var first = ScalarReadMemory(source.Dwords[0], out _);
        for (var dword = 0; dword < source.Dwords.Length; dword++)
        {
            var read = source.Dwords[dword];
            var memory = ScalarReadMemory(read, out _);
            if (first is null || memory is null ||
                memory.Offset != first.Offset + (uint)dword * sizeof(uint) ||
                !_graph.Equivalent(read.Operands[0], source.Dwords[0].Operands[0]) ||
                !_graph.Equivalent(read.Operands[1], source.Dwords[0].Operands[1]))
            {
                return false;
            }
        }

        return true;
    }

    // A runtime V# is one whose four dwords cannot be resolved at plan time, but
    // whose non-constant leaves are all reads from guest memory. This covers a
    // descriptor read straight from a device address and one read from the SRT
    // buffer at a dynamic offset, including the phi that merges an SRT read
    // across a loop. A handle that is fully resolvable (constants and flattened
    // table words only) is not a runtime descriptor and keeps the native binding.
    private bool IsRuntimeDescriptorHandle(ScalarValue? handle, bool allowComputedWords = false)
    {
        if (handle is null || handle.Kind != ScalarValueKind.BufferHandle ||
            handle.Operands.Length != 4)
        {
            return false;
        }

        // Descriptor words loaded by the shader can point at a memory entry that
        // is intentionally omitted from the host-side table (for example a
        // vertex-fetch record created after fixed-function lowering).  The
        // device-address lowering does not need to evaluate that word on the
        // host, so keep the descriptor runtime when the word is still a real
        // scalar memory read.  Other values retain the stricter derivability
        // check, which keeps divergent first-lane handles rejected.
        if (!handle.Operands.All(IsRuntimeDescriptorDword))
        {
            // Some vertex-fetch shaders combine one scalar address load with
            // descriptor words produced in the vector path. Those words are
            // not host-evaluable, but the shader still owns the complete
            // descriptor in its registers. Once a scalar memory word is part
            // of the handle, lower the whole descriptor through device
            // addresses instead of trying to bind a partial host source.
            return allowComputedWords && handle.Operands.Any(operand => HasRuntimeReadKind(operand, ScalarValueKind.ScalarAddressWord));
        }

        return handle.Operands.Any(operand => HasRuntimeRead(operand));
    }

    private bool IsRuntimeDescriptorDword(ScalarValue value) => IsRuntimeDescriptorDword(value, []);

    private bool IsRuntimeDescriptorDword(ScalarValue value, HashSet<ScalarValue> visiting)
    {
        if (!visiting.Add(value))
        {
            return value.Kind == ScalarValueKind.Phi;
        }

        try
        {
            if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord)
            {
                // The backend consumes this as a device-address word.  It may
                // not have a corresponding host materialization record.
                return value.MemoryIndex >= 0;
            }

            return IsRuntimeDerivable(value, visiting);
        }
        finally
        {
            visiting.Remove(value);
        }
    }

    // True when a value is known at plan time (constant, user data, shader base,
    // a flattened resource-table word) or is ultimately a guest-memory read.
    // A phi, select or operation qualifies when every operand does, so a
    // loop-carried descriptor merge stays a runtime descriptor. A cycle through
    // a phi is accepted: its non-cyclic inputs are checked on the path that
    // reached it, the same way the read planner treats cyclic phis.
    private bool IsRuntimeDerivable(ScalarValue value) => IsRuntimeDerivable(value, []);

    private bool IsRuntimeDerivable(ScalarValue value, HashSet<ScalarValue> visiting)
    {
        if (!visiting.Add(value))
        {
            return value.Kind == ScalarValueKind.Phi;
        }

        try
        {
            switch (value.Kind)
            {
                case ScalarValueKind.Constant:
                case ScalarValueKind.ResourceTableWord:
                case ScalarValueKind.UserData:
                case ScalarValueKind.ShaderBase:
                    return true;
                case ScalarValueKind.ScalarAddressWord:
                case ScalarValueKind.ScalarBufferWord:
                    return value.MemoryIndex < _graph.Memory.Count;
                case ScalarValueKind.Phi:
                case ScalarValueKind.Select:
                case ScalarValueKind.Operation:
                case ScalarValueKind.FirstLane:
                    return value.Operands.All(operand => IsRuntimeDerivable(operand, visiting));
                default:
                    return false;
            }
        }
        finally
        {
            visiting.Remove(value);
        }
    }

    private static bool HasRuntimeRead(ScalarValue value) => HasRuntimeRead(value, []);

    private static bool HasRuntimeReadKind(ScalarValue value, ScalarValueKind kind) => HasRuntimeReadKind(value, kind, []);

    private static bool HasRuntimeReadKind(ScalarValue value, ScalarValueKind kind, HashSet<ScalarValue> visiting)
    {
        if (!visiting.Add(value))
        {
            return false;
        }

        try
        {
            return value.Kind == kind || value.Operands.Any(operand => HasRuntimeReadKind(operand, kind, visiting));
        }
        finally
        {
            visiting.Remove(value);
        }
    }

    private static bool HasRuntimeRead(ScalarValue value, HashSet<ScalarValue> visiting)
    {
        if (!visiting.Add(value))
        {
            return false;
        }

        try
        {
            if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord)
            {
                return true;
            }

            return value.Operands.Any(operand => HasRuntimeRead(operand, visiting));
        }
        finally
        {
            visiting.Remove(value);
        }
    }

    // ---- dense tables ----

    private static uint ByteExtent(MemoryAccessInfo memory)
    {
        var bytes = Math.Max((memory.DataBits + 7) / 8, 1u);
        var count = Math.Max(memory.DataDwords, 1u);
        var end = (ulong)memory.Offset + (ulong)bytes * count;
        return end > uint.MaxValue ? uint.MaxValue : (uint)end;
    }

    private uint AddBuffer(uint source, MemoryAccessInfo memory, uint pc)
    {
        for (var index = 0; index < _info.Buffers.Count; index++)
        {
            if (_info.Buffers[index].Source == source)
            {
                Merge(_info.Buffers[index], memory, pc);
                return (uint)index;
            }
        }

        if (_info.Buffers.Count >= ShaderResourceInfo.MaxBuffers)
        {
            return DescriptorConstants.NoIndex;
        }

        var resource = new BufferResource { Source = source, FirstUsePc = pc };
        Merge(resource, memory, pc);
        _info.Buffers.Add(resource);
        return (uint)(_info.Buffers.Count - 1);
    }

    private static void Merge(BufferResource resource, MemoryAccessInfo memory, uint pc)
    {
        var atomic = memory.Access == MemoryAccess.Atomic;
        var write = memory.Access == MemoryAccess.Write || atomic;
        resource.FirstUsePc = Math.Min(resource.FirstUsePc, pc);
        resource.MaxByteExtent = Math.Max(resource.MaxByteExtent, ByteExtent(memory));
        resource.Read |= !write || atomic;
        resource.Written |= write;
        resource.Atomic |= atomic;
        resource.Formatted |= memory.Formatted;
        resource.Scalar |= memory.Kind == MemoryResourceKind.ScalarBuffer;
    }

    private uint AddImage(uint source, MemoryAccessInfo memory, uint pc)
    {
        var resourceClass = memory.ImageClass;
        var mip = resourceClass == ImageResourceClass.Storage && memory.ImageHasMip ? ImageMipMode.DynamicStorage : ImageMipMode.None;
        var depth = (memory.ImageSampleFlags & ImageSampleFlags.Compare) != 0;
        for (var index = 0; index < _info.Images.Count; index++)
        {
            var image = _info.Images[index];
            if (image.Source == source && image.ResourceClass == resourceClass && image.Dimension == memory.ImageDimension &&
                image.MipMode == mip && image.DepthCompare == depth && image.R128 == memory.ImageR128)
            {
                Merge(image, memory, pc);
                return (uint)index;
            }
        }

        if (_info.Images.Count >= ShaderResourceInfo.MaxImages)
        {
            return DescriptorConstants.NoIndex;
        }

        var added = new ImageResource
        {
            Source = source,
            FirstUsePc = pc,
            ResourceClass = resourceClass,
            Dimension = memory.ImageDimension,
            MipMode = mip,
            DepthCompare = depth,
            R128 = memory.ImageR128,
        };
        Merge(added, memory, pc);
        _info.Images.Add(added);
        return (uint)(_info.Images.Count - 1);
    }

    private static void Merge(ImageResource image, MemoryAccessInfo memory, uint pc)
    {
        var atomic = memory.Access == MemoryAccess.Atomic;
        var write = memory.Access == MemoryAccess.Write || atomic;
        image.FirstUsePc = Math.Min(image.FirstUsePc, pc);
        image.Read |= !write || atomic;
        image.Written |= write;
        image.Atomic |= atomic;
    }

    private uint AddSampler(uint source, uint pc)
    {
        for (var index = 0; index < _info.Samplers.Count; index++)
        {
            if (_info.Samplers[index].Source == source)
            {
                _info.Samplers[index].FirstUsePc = Math.Min(_info.Samplers[index].FirstUsePc, pc);
                return (uint)index;
            }
        }

        if (_info.Samplers.Count >= ShaderResourceInfo.MaxSamplers)
        {
            return DescriptorConstants.NoIndex;
        }

        _info.Samplers.Add(new SamplerResource { Source = source, FirstUsePc = pc });
        return (uint)(_info.Samplers.Count - 1);
    }

    private void AddSampledPair(uint image, uint sampler, uint pc)
    {
        foreach (var pair in _info.SampledPairs)
        {
            if (pair.Image == image && pair.Sampler == sampler)
            {
                pair.FirstUsePc = Math.Min(pair.FirstUsePc, pc);
                return;
            }
        }

        if (_info.SampledPairs.Count >= ShaderResourceInfo.MaxSampledPairs)
        {
            throw Failure(pc, "sampled image/sampler pair limit exceeded");
        }

        _info.SampledPairs.Add(new SampledImagePair { Image = image, Sampler = sampler, FirstUsePc = pc });
    }

    private void AddMemoryPatch(int index, uint resource, uint sampler, bool hasSampler, uint pc)
    {
        for (var patch = 0; patch < _memoryPatches.Count; patch++)
        {
            var existing = _memoryPatches[patch];
            if (existing.Index != index)
            {
                continue;
            }

            if (existing.Resource != resource || (hasSampler && existing.HasSampler && existing.Sampler != sampler))
            {
                throw Failure(pc, "memory metadata is reused with incompatible resources");
            }

            if (hasSampler)
            {
                _memoryPatches[patch] = (index, resource, sampler, true);
            }

            return;
        }

        _memoryPatches.Add((index, resource, sampler, hasSampler));
    }

    // ---- collection ----

    private void Collect(int index)
    {
        var memory = _plan.Memory[index];
        var access = _plan.Accesses[index];
        var isBuffer = memory.Kind is MemoryResourceKind.Buffer or MemoryResourceKind.ScalarBuffer;
        var isAddress = memory.Kind is MemoryResourceKind.ScalarAddress or MemoryResourceKind.Flat or MemoryResourceKind.Global or MemoryResourceKind.Scratch;
        var isImage = memory.Kind == MemoryResourceKind.Image;
        if (!isBuffer && !isAddress && !isImage)
        {
            return;
        }

        if (access is null)
        {
            if (HasIndirectPcTransferBefore(memory.Pc))
            {
                memory.PlanningOnly = true;
                return;
            }

            throw Failure(memory.Pc, "memory operation has no resource handle");
        }

        if (memory.PlanningOnly || IsIndirectPlanningMemory(index))
        {
            return;
        }

        if (isBuffer)
        {
            // A front GS/HS shader can carry a second code object after
            // S_SETPC_B64. Until AGC supplies its continuation descriptor,
            // linear decoding sees that unreachable tail with undefined SGPRs.
            // Do not turn that dead instruction into a descriptor failure; the
            // live path has already transferred control through the PC pair.
            if (access.Handle is { Kind: ScalarValueKind.BufferHandle } unreachableHandle &&
                unreachableHandle.Operands.Any(value => value.IsUndefined) &&
                HasIndirectPcTransferBefore(memory.Pc))
            {
                memory.PlanningOnly = true;
                return;
            }

            // A buffer access can carry a descriptor assembled in vector lanes.  Its
            // record count/stride is not a host-evaluable scalar (the A6FE Little
            // Nightmares path is one example), but the Vulkan physical-storage-buffer
            // lowering can read the four descriptor words directly from shader
            // registers. Keep this narrow to vector-derived accesses; ordinary
            // malformed handles must still fail validation below.
            if (memory.Access is MemoryAccess.Read or MemoryAccess.Write &&
                access.Handle is { Kind: ScalarValueKind.BufferHandle } writeHandle)
            {
                var writeSource = MakeSource(writeHandle, 4, false, false, memory.Pc);
                if (writeSource.Dwords.Any(dword =>
                    HasUndefinedOrigin(dword, "VAdd3U32") ||
                    HasUndefinedOrigin(dword, "VCvtU32F32") ||
                    HasUndefinedOrigin(dword, "VRcpIflagF32")))
                {
                    memory.BufferDescriptor = new GuestBufferDescriptor
                    {
                        Provenance = BufferDescriptorProvenance.Runtime,
                    };
                    _info.UsesDeviceAddresses = true;
                    return;
                }
            }

            // A V# whose dwords are all read from a runtime scalar-memory address
            // (e.g. a descriptor-array entry indexed by a loop counter) cannot be
            // bound ahead of time. Record it as a runtime guest descriptor so the
            // backend can choose a lowering strategy for each access.
            // A vector access through a V# the draw can evaluate keeps the explicit binding.
            var allowComputedFormatDescriptor = memory.Formatted &&
                memory.Opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal);
            if (IsRuntimeDescriptorHandle(access.Handle, allowComputedFormatDescriptor) &&
                (memory.Kind != MemoryResourceKind.Buffer ||
                 !ValidateSource(MakeSource(access.Handle!, 4, false, false, memory.Pc), out _)))
            {
                // A formatted vector access can enumerate its bounded candidates; a scalar
                // buffer read only needs the descriptor's base, which its SGPRs already
                // hold, so it is addressed through the device-address page table.
                if (memory.Kind == MemoryResourceKind.Buffer &&
                    BufferCandidateTablePlanner.TryPlan(_plan, access.Handle!, index, out var table) &&
                    InternBufferCandidateTable(table, memory.Pc, index))
                {
                    memory.BufferDescriptor = new GuestBufferDescriptor
                    {
                        Provenance = BufferDescriptorProvenance.Runtime,
                    };
                    return;
                }

                // Without a bounded candidate table, a V# loaded from scalar-buffer
                // data is read through its descriptor registers on the device.
                if (memory.Kind == MemoryResourceKind.Buffer && IsDeviceLoadedBufferHandle(access.Handle))
                {
                    memory.DeviceDescriptor = true;
                    _info.UsesDeviceAddresses = true;
                    return;
                }

                memory.BufferDescriptor = new GuestBufferDescriptor
                {
                    Provenance = BufferDescriptorProvenance.Runtime,
                };
                _info.UsesDeviceAddresses = true;
                return;
            }

            // Scalar loads can address buffers that have no host descriptor binding, while
            // vector buffer descriptors loaded from scalar-buffer data must stay device-side.
            if ((memory.Kind == MemoryResourceKind.ScalarBuffer && !IsHostBufferHandle(access.Handle)) ||
                (memory.Kind == MemoryResourceKind.Buffer && IsDeviceLoadedBufferHandle(access.Handle)))
            {
                memory.DeviceDescriptor = true;
                _info.UsesDeviceAddresses = true;
                return;
            }

            var source = GetHandleSource(
                access.Handle,
                ScalarValueKind.BufferHandle,
                4,
                memory.Pc,
                memoryOpcode: memory.Opcode,
                memoryAccess: memory.Access);
            var resource = AddBuffer(source, memory, memory.Pc);
            if (resource == DescriptorConstants.NoIndex)
            {
                throw Failure(memory.Pc, "buffer resource limit exceeded");
            }

            memory.BufferDescriptor = new GuestBufferDescriptor
            {
                Provenance = BufferDescriptorProvenance.Static,
                StaticResource = resource,
            };
            AddMemoryPatch(index, resource, 0, false, memory.Pc);
            return;
        }

        if (isAddress)
        {
            if (memory.Kind == MemoryResourceKind.Scratch)
            {
                // Scratch is invocation-private shader storage. It has no host
                // resource handle or descriptor-table binding to materialize.
                return;
            }

            if (access.Handle is null || access.Handle.Kind != ScalarValueKind.AddressHandle)
            {
                throw Failure(memory.Pc, "address operation requires an address handle");
            }

            if (access.Handle.Operands.Length != 2)
            {
                throw Failure(memory.Pc, "an address handle must have two address dwords");
            }

            _info.UsesDeviceAddresses = true;
            return;
        }

        // A fused shader resumes through S_SETPC_B64.  The continuation can
        // legitimately consume the image descriptor carried in the incoming
        // SGPR state, while the standalone resource graph only sees the first
        // code object and therefore has no provenance for those dwords.  Do
        // not reject the whole program for that cross-object bookkeeping gap;
        // leave the access unplanned so the continuation path can supply its
        // descriptor at runtime.
        if (isImage &&
            access.Handle is { Kind: ScalarValueKind.ImageHandle } continuationImage &&
            continuationImage.Operands.Any(value => value.IsUndefined) &&
            HasIndirectPcTransferBefore(memory.Pc))
        {
            memory.PlanningOnly = true;
            return;
        }

        if (memory.ImageClass == ImageResourceClass.None)
        {
            throw Failure(memory.Pc, "image operation has invalid resource kind");
        }

        uint imageSource;
        var indirect = _indirectImages.FirstOrDefault(plan => ReferenceEquals(plan.Handle, access.Handle));
        if (indirect is not null)
        {
            imageSource = indirect.Source;
        }
        else
        {
            imageSource = GetHandleSource(access.Handle, ScalarValueKind.ImageHandle, 8, memory.Pc);
        }

        var image = AddImage(imageSource, memory, memory.Pc);
        if (image == DescriptorConstants.NoIndex)
        {
            throw Failure(memory.Pc, "image resource limit exceeded");
        }

        uint sampler = 0;
        if (memory.NeedsSampler)
        {
            if (access.SamplerHandle is null)
            {
                throw Failure(memory.Pc, "sampled image operation has no sampler handle");
            }

            var sampleAdjust = (memory.ImageSampleFlags & ImageSampleFlags.Adjust) != 0;
            var samplerSource = GetHandleSource(access.SamplerHandle, ScalarValueKind.SamplerHandle, 4, memory.Pc, sampler: true, sampleAdjust);
            sampler = AddSampler(samplerSource, memory.Pc);
            if (sampler == DescriptorConstants.NoIndex)
            {
                throw Failure(memory.Pc, "sampler resource limit exceeded");
            }

            AddSampledPair(image, sampler, memory.Pc);
        }

        AddMemoryPatch(index, image, sampler, memory.NeedsSampler, memory.Pc);
    }

    private void LinkImageAliases()
    {
        foreach (var buffer in _info.Buffers)
        {
            var bufferSource = _sources[(int)buffer.Source];
            if (bufferSource.DwordCount != 4)
            {
                continue;
            }

            for (var image = 0; image < _info.Images.Count; image++)
            {
                var imageSource = _sources[(int)_info.Images[image].Source];
                if (imageSource.DwordCount != 8 || imageSource.IndirectImage is not null)
                {
                    continue;
                }

                var alias = true;
                for (var dword = 0; dword < 4 && alias; dword++)
                {
                    alias = _graph.Equivalent(bufferSource.Dwords[dword], imageSource.Dwords[dword]);
                }

                if (alias)
                {
                    buffer.ImageAlias = (uint)image;
                    break;
                }
            }
        }
    }

    // ---- indirect images ----

    private bool IsIndirectPlanningMemory(int index) =>
        _indirectImages.Any(plan => plan.SuppressMemoryReads && plan.Memory.Contains(index));

    private bool HasIndirectPcTransferBefore(uint pc)
    {
        var previousEnd = 0u;
        foreach (var instruction in _graph.Program.Instructions)
        {
            if (instruction.Pc >= pc)
            {
                break;
            }

            if (instruction.Opcode is "SSetpcB64" or "SRfeB64")
            {
                return true;
            }

            // Fused shaders replace the S_SETPC marker with a NOP and append
            // the continuation at its real address.  The PC discontinuity is
            // the remaining reliable boundary when the marker is gone.
            if (previousEnd != 0 && instruction.Pc > previousEnd)
            {
                return true;
            }

            previousEnd = instruction.Pc + (uint)(instruction.Words.Count * sizeof(uint));
        }

        return false;
    }

    private void PlanIndirectImages()
    {
        for (var index = 0; index < _plan.Memory.Count; index++)
        {
            var memory = _plan.Memory[index];
            if (memory.Kind != MemoryResourceKind.Image || _plan.Accesses[index]?.Handle is not { } handle ||
                _indirectImages.Any(plan => ReferenceEquals(plan.Handle, handle)))
            {
                continue;
            }

            if (TryMakeIndirectImage(handle, memory.Pc, out var plan) ||
                TryMakeDenseIndirectImage(handle, memory.Pc, out plan) ||
                TryMakeDirectImage(handle, out plan))
            {
                _indirectImages.Add(plan);
            }
        }
    }

    private MemoryAccessInfo? ScalarReadMemory(ScalarValue read, out int index)
    {
        index = read.MemoryIndex;
        if (read.Kind != ScalarValueKind.ScalarBufferWord || index >= _plan.Memory.Count)
        {
            return null;
        }

        var memory = _plan.Memory[index];
        return memory.Kind is MemoryResourceKind.ScalarBuffer or MemoryResourceKind.Buffer &&
            memory.DataBits == 32 && memory.DataDwords == 1 ? memory : null;
    }

    private bool MemoryIndexBelongsTo(int index, ScalarValue owner) =>
        !_readsByMemory.TryGetValue(index, out var readers) || readers.All(reader => ReferenceEquals(reader, owner));

    private bool UsesOnly(ScalarValue value, IReadOnlyList<ScalarValue> users) =>
        _uses.TryGetValue(value, out var uses) && uses.Count != 0 && uses.All(user => users.Any(candidate => ReferenceEquals(candidate, user)));

    private bool MakeRuntimeBufferSource(ScalarValue handle, uint pc, out uint sourceIndex, out DescriptorSource source)
    {
        sourceIndex = 0;
        source = null!;
        if (handle.Kind != ScalarValueKind.BufferHandle)
        {
            return false;
        }

        source = MakeSource(handle, 4, false, false, pc);
        if (!ValidateSource(source, out _))
        {
            return false;
        }

        sourceIndex = InternSource(source);
        return true;
    }

    private bool MakeRuntimeAddressSource(ScalarValue handle, uint pc, out uint sourceIndex, out DescriptorSource source)
    {
        sourceIndex = 0;
        source = null!;
        if (handle.Kind != ScalarValueKind.AddressHandle)
        {
            return false;
        }

        source = MakeSource(handle, 2, false, false, pc);
        if (!ValidateSource(source, out _))
        {
            return false;
        }

        sourceIndex = InternSource(source);
        return true;
    }

    private static bool MatchMaterialOffset(ScalarValue value, out ScalarValue selector, out uint stride, out uint offset)
    {
        selector = null!;
        stride = 0;
        offset = 0;
        if (value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.IAdd32)
        {
            if (value.Operands[0].IsConstant)
            {
                offset = value.Operands[0].ConstantU32;
                value = value.Operands[1];
            }
            else if (value.Operands[1].IsConstant)
            {
                offset = value.Operands[1].ConstantU32;
                value = value.Operands[0];
            }
            else
            {
                return false;
            }
        }

        if (value.Kind != ScalarValueKind.Operation || value.Operation != ScalarOperation.IMul32)
        {
            return false;
        }

        if (value.Operands[0].IsConstant)
        {
            stride = value.Operands[0].ConstantU32;
            selector = value.Operands[1];
        }
        else if (value.Operands[1].IsConstant)
        {
            stride = value.Operands[1].ConstantU32;
            selector = value.Operands[0];
        }
        else
        {
            return false;
        }

        return stride != 0 && selector.Kind == ScalarValueKind.FirstLane;
    }

    // A material-table key selects a heap record whose eight dwords are the image
    // descriptor. The key read, the heap reads and their shift must feed nothing else.
    private bool TryMakeIndirectImage(ScalarValue handle, uint pc, out IndirectImagePlan plan)
    {
        plan = null!;
        if (handle.Kind != ScalarValueKind.ImageHandle || handle.Operands.Length != 8)
        {
            return false;
        }

        var heapReads = new ScalarValue[8];
        ScalarValue? heapHandle = null;
        ScalarValue? heapOffset = null;
        var memoryIndices = new int[8];
        for (var dword = 0; dword < 8; dword++)
        {
            var read = handle.Operands[dword];
            heapReads[dword] = read;
            var memory = ScalarReadMemory(read, out var memoryIndex);
            if (memory is null || memory.Offset != (uint)dword * sizeof(uint) || !MemoryIndexBelongsTo(memoryIndex, read))
            {
                return false;
            }

            var currentHandle = read.Operands[0];
            if (heapHandle is not null && !ReferenceEquals(currentHandle, heapHandle))
            {
                return false;
            }

            heapHandle = currentHandle;
            if (dword == 0)
            {
                heapOffset = read.Operands[1];
            }
            else if (!_graph.Equivalent(heapOffset!, read.Operands[1]))
            {
                return false;
            }

            memoryIndices[dword] = memoryIndex;
        }

        if (heapOffset!.Kind != ScalarValueKind.Operation || heapOffset.Operation != ScalarOperation.ShiftLeft32 ||
            !heapOffset.Operands[1].IsConstant || heapOffset.Operands[1].ConstantU32 != 5)
        {
            return false;
        }

        var materialRead = heapOffset.Operands[0];
        var materialMemory = ScalarReadMemory(materialRead, out var materialMemoryIndex);
        if (materialMemory is null || !MemoryIndexBelongsTo(materialMemoryIndex, materialRead))
        {
            return false;
        }

        var materialHandle = materialRead.Operands[0];
        if (!MatchMaterialOffset(materialRead.Operands[1], out var selector, out var selectorStride, out var selectorOffset))
        {
            return false;
        }

        if (!UsesOnly(materialRead, [heapOffset]) || !UsesOnly(heapOffset, heapReads))
        {
            return false;
        }

        foreach (var read in heapReads)
        {
            if (!UsesOnly(read, [handle]))
            {
                return false;
            }
        }

        if (!MakeRuntimeBufferSource(materialHandle, pc, out var materialSourceIndex, out var materialSource) ||
            !MakeRuntimeBufferSource(heapHandle!, pc, out var heapSourceIndex, out var heapSource))
        {
            return false;
        }

        var imageDwords = new ScalarValue[8];
        Array.Copy(materialSource.Dwords, 0, imageDwords, 0, 4);
        Array.Copy(heapSource.Dwords, 0, imageDwords, 4, 4);
        var imageSource = new DescriptorSource
        {
            Dwords = imageDwords,
            IndirectImage = new IndirectImageSelector(materialSourceIndex, heapSourceIndex, selectorStride, selectorOffset, 0)
            {
                SelectorValues = IndirectSelectorValues.Create(_plan, selector),
                MaterialImmediate = materialMemory.Offset,
            },
        };

        plan = new IndirectImagePlan
        {
            Handle = handle,
            Source = InternSource(imageSource),
            Key = materialRead,
            HeapSource = heapSourceIndex,
            Memory = memoryIndices,
            Reads = heapReads,
        };
        return true;
    }
}
