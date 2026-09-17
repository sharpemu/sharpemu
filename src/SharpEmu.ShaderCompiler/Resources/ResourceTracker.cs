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
    private readonly Dictionary<ScalarValue, List<ScalarValue>> _uses;
    private readonly Dictionary<int, List<ScalarValue>> _readsByMemory = [];

    private sealed class IndirectImagePlan
    {
        public ScalarValue Handle = null!;
        public uint Source;
        public ScalarValue Key = null!;
        public uint HeapSource;
        public bool KeyIsAddressOffset;
        public int[] Memory = new int[8];
        public ScalarValue[] Reads = new ScalarValue[8];
    }

    public sealed record Result(
        IReadOnlyList<DescriptorSource> Sources,
        ShaderResourceInfo Info,
        IReadOnlyList<IndirectImageAccess> IndirectImages,
        IReadOnlySet<ScalarValue> IndirectReads);

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
            foreach (var index in plan.Memory)
            {
                _plan.Memory[index].PlanningOnly = true;
            }

            foreach (var read in plan.Reads)
            {
                indirectReads.Add(read);
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

        return new Result(_sources, _info, indirectAccesses, indirectReads);
    }

    private ResourcePlanException Failure(uint pc, string reason) =>
        new($"shader resource tracking: hash=0x{_plan.Hash:X16} stage={_plan.Stage} pc=0x{pc:X8} {reason}");

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

    private bool ValidateSource(DescriptorSource source, out uint badDword)
    {
        for (badDword = 0; badDword < source.DwordCount; badDword++)
        {
            var dword = source.Dwords[badDword];
            if (dword.Type != ScalarValueType.U32 || !_plan.ValidateRuntimeValue(dword))
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

    private uint GetHandleSource(ScalarValue? handle, ScalarValueKind expected, uint width, uint pc, bool sampler = false, bool sampleAdjust = false)
    {
        if (handle is null || handle.Kind != expected)
        {
            throw Failure(pc, $"memory operation requires {expected}");
        }

        var source = MakeSource(handle, width, sampler, sampleAdjust, pc);
        if (expected == ScalarValueKind.ImageHandle)
        {
            for (uint dword = 0; dword < source.DwordCount; dword++)
            {
                if (source.Dwords[dword].Kind == ScalarValueKind.ScalarBufferWord)
                {
                    throw Failure(pc, $"{expected} dword {dword} is not a valid runtime value");
                }
            }
        }

        if (!ValidateSource(source, out var badDword))
        {
            throw Failure(pc, $"{expected} dword {badDword} is not a valid runtime value");
        }

        return InternSource(source);
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
            throw Failure(memory.Pc, "memory operation has no resource handle");
        }

        if (memory.PlanningOnly || IsIndirectPlanningMemory(index))
        {
            return;
        }

        if (isBuffer)
        {
            var source = GetHandleSource(access.Handle, ScalarValueKind.BufferHandle, 4, memory.Pc);
            var resource = AddBuffer(source, memory, memory.Pc);
            if (resource == DescriptorConstants.NoIndex)
            {
                throw Failure(memory.Pc, "buffer resource limit exceeded");
            }

            AddMemoryPatch(index, resource, 0, false, memory.Pc);
            return;
        }

        if (isAddress)
        {
            if (memory.Kind == MemoryResourceKind.Scratch)
            {
                throw Failure(memory.Pc, "scratch operations are not supported");
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
        _indirectImages.Any(plan => plan.Memory.Contains(index));

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

            if (TryMakeIndirectImage(handle, memory.Pc, out var plan) || TryMakeDirectImage(handle, out plan))
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
        return memory.Kind == MemoryResourceKind.ScalarBuffer && memory.DataBits == 32 && memory.DataDwords == 1 ? memory : null;
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
        if (materialMemory is null || materialMemory.Offset != 0 || !MemoryIndexBelongsTo(materialMemoryIndex, materialRead))
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
