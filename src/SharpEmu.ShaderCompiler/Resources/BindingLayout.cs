// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

// The fixed binding numbers of one stage: buffers, thirty-six image classes, samplers,
// and the shared buffers. A pixel stage adds Count to every number.
public enum DescriptorBindingKind : uint
{
    Buffers = 0,
    Samplers = BindingLayout.FirstImageBinding + BindingLayout.ImageBindingCount,
    GlobalDataShare,
    DeviceAddressPageTable,
    FaultBuffer,
    FlattenedResourceTable,
    ShaderData,
    Count,
}

// Thirty-two dwords of push data shared by the stages of one pipeline.
public static class PushData
{
    public const uint DwordCount = 32;
    public const uint ByteSize = DwordCount * sizeof(uint);
    public const uint NoStart = uint.MaxValue;

    public static bool CanFit(uint start, uint size) => size != 0 && start <= DwordCount && size <= DwordCount - start;

    public static uint StartFor(uint cursor, uint size) => CanFit(cursor, size) ? cursor : NoStart;
}

public sealed record DescriptorBinding(DescriptorBindingKind Kind, IReadOnlyList<uint> Resources);

// Which binding an image resource occupies: its class, numeric class and dimension.
public static class ImageDescriptorBinding
{
    private const uint SampledFloatBinding = 1;
    private const uint SampledUintBinding = 8;
    private const uint SampledSintBinding = 15;
    private const uint StorageFloatBinding = 22;
    private const uint StorageUintBinding = 27;
    private const uint AtomicUintBinding = 32;

    public static DescriptorBindingKind? ForImage(ImageResource image)
    {
        uint baseBinding;
        var sampled = false;
        if (image.ResourceClass == ImageResourceClass.Sampled)
        {
            if (image.Atomic)
            {
                return null;
            }

            sampled = true;
            switch (image.NumericClass)
            {
                case ImageNumericClass.Float:
                    baseBinding = SampledFloatBinding;
                    break;
                case ImageNumericClass.Uint:
                    baseBinding = SampledUintBinding;
                    break;
                case ImageNumericClass.Sint:
                    baseBinding = SampledSintBinding;
                    break;
                default:
                    return null;
            }
        }
        else if (image.ResourceClass == ImageResourceClass.Storage)
        {
            if (image.Atomic)
            {
                if (image.NumericClass != ImageNumericClass.Uint)
                {
                    return null;
                }

                baseBinding = AtomicUintBinding;
            }
            else
            {
                switch (image.NumericClass)
                {
                    case ImageNumericClass.Float:
                        baseBinding = StorageFloatBinding;
                        break;
                    case ImageNumericClass.Uint:
                        baseBinding = StorageUintBinding;
                        break;
                    default:
                        return null;
                }
            }
        }
        else
        {
            return null;
        }

        uint dimension;
        switch (image.Dimension)
        {
            case ImageDimension.Dim1D:
                dimension = 0;
                break;
            case ImageDimension.Dim1DArray:
                dimension = 1;
                break;
            case ImageDimension.Dim2D:
                dimension = 2;
                break;
            case ImageDimension.Dim2DArray:
                dimension = 3;
                break;
            case ImageDimension.Dim2DMsaa:
                if (!sampled)
                {
                    return null;
                }

                dimension = 4;
                break;
            case ImageDimension.Dim2DMsaaArray:
                if (!sampled)
                {
                    return null;
                }

                dimension = 5;
                break;
            case ImageDimension.Dim3D:
                dimension = sampled ? 6u : 4u;
                break;
            default:
                return null;
        }

        return (DescriptorBindingKind)(baseBinding + dimension);
    }

    public static ImageResourceClass ResourceClass(DescriptorBindingKind kind)
    {
        var value = (uint)kind;
        if (value >= BindingLayout.FirstImageBinding && value < BindingLayout.FirstStorageImageBinding)
        {
            return ImageResourceClass.Sampled;
        }

        if (value >= BindingLayout.FirstStorageImageBinding && value < (uint)DescriptorBindingKind.Samplers)
        {
            return ImageResourceClass.Storage;
        }

        return ImageResourceClass.None;
    }

    public static uint ArrayIndex(DescriptorBindingKind kind) => (uint)kind - BindingLayout.FirstImageBinding;

    private static readonly ImageDimension[] SampledDimensions =
    [
        ImageDimension.Dim1D, ImageDimension.Dim1DArray, ImageDimension.Dim2D, ImageDimension.Dim2DArray,
        ImageDimension.Dim2DMsaa, ImageDimension.Dim2DMsaaArray, ImageDimension.Dim3D,
    ];

    private static readonly ImageDimension[] StorageDimensions =
    [
        ImageDimension.Dim1D, ImageDimension.Dim1DArray, ImageDimension.Dim2D, ImageDimension.Dim2DArray, ImageDimension.Dim3D,
    ];

    // The class, numeric class and dimension an image binding kind declares.
    public static (ImageResourceClass ResourceClass, ImageNumericClass NumericClass, ImageDimension Dimension, bool Atomic) Describe(DescriptorBindingKind kind)
    {
        var index = (uint)kind;
        if (index >= SampledFloatBinding && index < StorageFloatBinding)
        {
            var offset = index - SampledFloatBinding;
            var numericClass = (offset / 7) switch { 0 => ImageNumericClass.Float, 1 => ImageNumericClass.Uint, _ => ImageNumericClass.Sint };
            return (ImageResourceClass.Sampled, numericClass, SampledDimensions[offset % 7], false);
        }

        if (index >= StorageFloatBinding && index < AtomicUintBinding)
        {
            var offset = index - StorageFloatBinding;
            return (ImageResourceClass.Storage, offset / 5 == 0 ? ImageNumericClass.Float : ImageNumericClass.Uint, StorageDimensions[offset % 5], false);
        }

        if (index >= AtomicUintBinding && index < (uint)DescriptorBindingKind.Samplers)
        {
            return (ImageResourceClass.Storage, ImageNumericClass.Uint, StorageDimensions[index - AtomicUintBinding], true);
        }

        return (ImageResourceClass.None, ImageNumericClass.Unsupported, ImageDimension.Unknown, false);
    }
}

// The descriptor set layout of one compiled program and its push data: user-data
// registers, the shader base, packed memory offsets, and optional dispatch limits.
public sealed class BindingLayout : IEquatable<BindingLayout>
{
    public const uint FirstImageBinding = 1;
    public const uint FirstStorageImageBinding = 22;
    public const uint ImageBindingCount = 36;
    public const uint NoShaderBase = uint.MaxValue;
    public const uint ShaderBaseDwordCount = 2;
    private const int ScalarRegisterCount = 256;

    public uint PushDataStartDword { get; init; } = PushData.NoStart;
    public uint AllocationCursor { get; init; }
    public uint ShaderBaseDword { get; init; } = NoShaderBase;
    public uint MemoryOffsetDword { get; init; }
    public uint MemoryOffsetCount { get; init; }
    public bool UsesDispatchThreadLimits { get; init; }
    public IReadOnlyList<uint> UserDataRegisters { get; init; } = [];
    public IReadOnlyList<DescriptorBinding> Descriptors { get; init; } = [];

    public uint DispatchThreadLimitsDword => MemoryOffsetDword + (MemoryOffsetCount + 3) / 4;

    public uint ShaderDataDwordCount => DispatchThreadLimitsDword + (UsesDispatchThreadLimits ? 3u : 0u);

    public bool UsesPushData => PushDataStartDword != PushData.NoStart;

    public bool UsesShaderBase => ShaderBaseDword != NoShaderBase;

    public void AdvancePushData(ref uint cursor)
    {
        if (UsesPushData)
        {
            cursor = PushDataStartDword + ShaderDataDwordCount;
        }
    }

    public static uint NativeBindingIndex(ShaderStage stage, DescriptorBindingKind kind) =>
        (uint)kind + (stage == ShaderStage.Pixel ? (uint)DescriptorBindingKind.Count : 0);

    public DescriptorBinding? Find(DescriptorBindingKind kind) => Descriptors.FirstOrDefault(binding => binding.Kind == kind);

    // The user-data registers live at program entry: those some path reads before it
    // writes them, found by liveness over the control-flow graph, in ascending order.
    public static IReadOnlyList<uint> CollectUserDataRegisters(Gen5ShaderProgram program, uint userDataBase, uint userDataCount)
    {
        var controlFlow = IrControlFlowGraph.Build(program.Instructions, Gen5IrBranchResolver.Instance);
        var blockCount = controlFlow.Blocks.Count;
        var uses = new bool[blockCount][];
        var definitions = new bool[blockCount][];
        for (var block = 0; block < blockCount; block++)
        {
            uses[block] = new bool[ScalarRegisterCount];
            definitions[block] = new bool[ScalarRegisterCount];
            var range = controlFlow.Blocks[block];
            foreach (var instruction in program.Instructions)
            {
                if (instruction.Pc >= range.StartPc && instruction.Pc < range.EndPc)
                {
                    RecordUsesAndDefinitions(instruction, uses[block], definitions[block]);
                }
            }
        }

        var liveIn = new bool[blockCount][];
        for (var block = 0; block < blockCount; block++)
        {
            liveIn[block] = new bool[ScalarRegisterCount];
        }

        var changed = blockCount != 0;
        while (changed)
        {
            changed = false;
            for (var block = blockCount - 1; block >= 0; block--)
            {
                for (var register = 0; register < ScalarRegisterCount; register++)
                {
                    var liveOut = false;
                    foreach (var successor in controlFlow.Successors[block])
                    {
                        liveOut |= liveIn[successor][register];
                    }

                    var live = uses[block][register] || (liveOut && !definitions[block][register]);
                    if (live != liveIn[block][register])
                    {
                        liveIn[block][register] = live;
                        changed = true;
                    }
                }
            }
        }

        var registers = new List<uint>();
        for (uint index = 0; index < userDataCount && blockCount != 0; index++)
        {
            var register = userDataBase + index;
            if (register < ScalarRegisterCount && liveIn[0][register])
            {
                registers.Add(register);
            }
        }

        return registers;
    }

    // Reads count as uses only before the block defines the register: scalar sources at
    // their width plus the descriptor, address and offset registers of memory instructions.
    private static void RecordUsesAndDefinitions(Gen5ShaderInstruction instruction, bool[] uses, bool[] definitions)
    {
        void Use(uint register, uint count)
        {
            for (uint index = 0; index < count; index++)
            {
                if (register + index < ScalarRegisterCount && !definitions[register + index])
                {
                    uses[register + index] = true;
                }
            }
        }

        var width = instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u;
        foreach (var source in instruction.Sources)
        {
            if (source.Kind == Gen5OperandKind.ScalarRegister)
            {
                Use(source.Value, width);
            }
        }

        if (instruction.Opcode is "SBitset0B32" or "SBitset1B32")
        {
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind == Gen5OperandKind.ScalarRegister)
                {
                    Use(destination.Value, 1);
                }
            }
        }

        switch (instruction.Control)
        {
            case Gen5ImageControl image:
                Use(image.ScalarResource, 8);
                if (instruction.Opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
                    instruction.Opcode.StartsWith("ImageGather", StringComparison.Ordinal))
                {
                    Use(image.ScalarSampler, 4);
                }

                break;
            case Gen5ScalarMemoryControl scalar:
                if (instruction.Sources.Count != 0 && instruction.Sources[0].Kind == Gen5OperandKind.ScalarRegister)
                {
                    Use(instruction.Sources[0].Value, instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal) ? 4u : 2u);
                }

                if (scalar.DynamicOffsetRegister is { } offsetRegister)
                {
                    Use(offsetRegister, 1);
                }

                break;
            case Gen5GlobalMemoryControl global when global.ScalarAddress < 255:
                Use(global.ScalarAddress, 2);
                break;
            case Gen5BufferMemoryControl buffer:
                Use(buffer.ScalarResource, 4);
                break;
        }

        // A record that lists every written register defines each once; a single listed
        // register of a multiword instruction defines the whole width.
        var scalarDestinations = instruction.Destinations.Where(destination => destination.Kind == Gen5OperandKind.ScalarRegister).ToList();
        if (scalarDestinations.Count == 0)
        {
            return;
        }

        if (instruction.Control is Gen5ScalarMemoryControl memory)
        {
            var first = scalarDestinations.Min(destination => destination.Value);
            var count = Math.Max(memory.DestinationCount, (uint)scalarDestinations.Count);
            for (uint index = 0; index < count && first + index < ScalarRegisterCount; index++)
            {
                definitions[first + index] = true;
            }

            return;
        }

        var perDestination = scalarDestinations.Count > 1 ? 1u : width;
        foreach (var destination in scalarDestinations)
        {
            for (uint index = 0; index < perDestination && destination.Value + index < ScalarRegisterCount; index++)
            {
                definitions[destination.Value + index] = true;
            }
        }
    }

    public static bool UsesGlobalDataShare(Gen5ShaderProgram program) =>
        program.Instructions.Any(instruction => instruction.Control is Gen5DataShareControl { Gds: true });

    public static bool ReadsShaderBase(Gen5ShaderProgram program) =>
        program.Instructions.Any(instruction => instruction.Opcode == "SGetpcB64");

    // Allocates one program's bindings: buffers, image groups (one element per mip for a
    // dynamic-mip storage image), samplers, shared buffers, push data or its fallback.
    public static BindingLayout Allocate(
        ShaderResourceInfo info,
        IReadOnlyList<uint> userDataRegisters,
        bool usesGlobalDataShare,
        bool usesFlattenedTable,
        bool usesShaderBase,
        uint pushDataStartDword = 0,
        bool usesDispatchThreadLimits = false)
    {
        var shaderBaseDword = usesShaderBase ? (uint)userDataRegisters.Count : NoShaderBase;
        var memoryOffsetDword = (uint)userDataRegisters.Count + (usesShaderBase ? ShaderBaseDwordCount : 0);
        var memoryOffsetCount = (uint)info.Buffers.Count;
        var shaderDataDwords = memoryOffsetDword + (memoryOffsetCount + 3) / 4 + (usesDispatchThreadLimits ? 3u : 0u);
        var pushStart = PushData.StartFor(pushDataStartDword, shaderDataDwords);
        var descriptors = new List<DescriptorBinding>();
        if (info.Buffers.Count != 0)
        {
            descriptors.Add(new DescriptorBinding(DescriptorBindingKind.Buffers, Enumerable.Range(0, info.Buffers.Count).Select(index => (uint)index).ToArray()));
        }

        var imageGroups = new List<uint>[ImageBindingCount];
        for (var index = 0; index < info.Images.Count; index++)
        {
            var image = info.Images[index];
            var kind = ImageDescriptorBinding.ForImage(image);
            if (kind is null)
            {
                throw new ResourcePlanException($"shader binding layout failed: image {index} has an invalid binding class");
            }

            var group = ImageDescriptorBinding.ArrayIndex(kind.Value);
            if (group >= ImageBindingCount)
            {
                throw new ResourcePlanException($"shader binding layout failed: image {index} has an unmapped binding class");
            }

            var dynamic = image.MipMode == ImageMipMode.DynamicStorage;
            var count = dynamic ? image.MipCount : 1;
            if (count == 0 || (!dynamic && image.MipCount != 1))
            {
                throw new ResourcePlanException($"shader binding layout failed: image {index} has invalid specialized mip count {image.MipCount}");
            }

            imageGroups[group] ??= [];
            imageGroups[group].AddRange(Enumerable.Repeat((uint)index, (int)count));
        }

        for (var group = 0; group < imageGroups.Length; group++)
        {
            if (imageGroups[group] is { Count: > 0 } resources)
            {
                descriptors.Add(new DescriptorBinding((DescriptorBindingKind)(FirstImageBinding + group), resources));
            }
        }

        if (info.Samplers.Count != 0)
        {
            descriptors.Add(new DescriptorBinding(DescriptorBindingKind.Samplers, Enumerable.Range(0, info.Samplers.Count).Select(index => (uint)index).ToArray()));
        }

        if (usesGlobalDataShare)
        {
            descriptors.Add(new DescriptorBinding(DescriptorBindingKind.GlobalDataShare, []));
        }

        if (info.UsesDeviceAddresses)
        {
            descriptors.Add(new DescriptorBinding(DescriptorBindingKind.DeviceAddressPageTable, []));
            descriptors.Add(new DescriptorBinding(DescriptorBindingKind.FaultBuffer, []));
        }

        var flattened = usesFlattenedTable || info.Images.Any(image => image.IndirectSearchIterations != 0);
        if (flattened)
        {
            descriptors.Add(new DescriptorBinding(DescriptorBindingKind.FlattenedResourceTable, []));
        }

        if (shaderDataDwords != 0 && pushStart == PushData.NoStart)
        {
            descriptors.Add(new DescriptorBinding(DescriptorBindingKind.ShaderData, []));
        }

        return new BindingLayout
        {
            PushDataStartDword = pushStart,
            AllocationCursor = pushDataStartDword,
            ShaderBaseDword = shaderBaseDword,
            MemoryOffsetDword = memoryOffsetDword,
            MemoryOffsetCount = memoryOffsetCount,
            UsesDispatchThreadLimits = usesDispatchThreadLimits,
            UserDataRegisters = userDataRegisters,
            Descriptors = descriptors,
        };
    }

    // The allocation cursor is context, not layout: two spills from different cursors are equal.
    public bool Equals(BindingLayout? other) =>
        other is not null &&
        PushDataStartDword == other.PushDataStartDword &&
        ShaderBaseDword == other.ShaderBaseDword &&
        MemoryOffsetDword == other.MemoryOffsetDword &&
        MemoryOffsetCount == other.MemoryOffsetCount &&
        UsesDispatchThreadLimits == other.UsesDispatchThreadLimits &&
        UserDataRegisters.SequenceEqual(other.UserDataRegisters) &&
        Descriptors.Count == other.Descriptors.Count &&
        Descriptors.Zip(other.Descriptors).All(pair => pair.First.Kind == pair.Second.Kind && pair.First.Resources.SequenceEqual(pair.Second.Resources));

    public override bool Equals(object? obj) => Equals(obj as BindingLayout);

    public override int GetHashCode() => HashCode.Combine(PushDataStartDword, MemoryOffsetDword, MemoryOffsetCount, Descriptors.Count, UsesDispatchThreadLimits);
}

// Recomputes the layout an emitter was given from the same inputs and the same push
// cursor and fails when they differ, so declarations and descriptor writes never drift.
public static class BindingLayoutValidator
{
    public static void Validate(
        BindingLayout layout,
        ShaderResourceInfo info,
        IReadOnlyList<uint> userDataRegisters,
        bool usesGlobalDataShare,
        bool usesFlattenedTable,
        bool usesShaderBase,
        ulong hash,
        ShaderStage stage)
    {
        if (layout.UsesDispatchThreadLimits && stage != ShaderStage.Compute)
        {
            throw new ResourcePlanException("Only a compute shader can use dispatch thread limits.");
        }

        var expected = BindingLayout.Allocate(info, userDataRegisters, usesGlobalDataShare, usesFlattenedTable, usesShaderBase, layout.AllocationCursor, layout.UsesDispatchThreadLimits);
        if (!expected.Equals(layout))
        {
            throw new ResourcePlanException(
                $"shader binding layout does not match its resources: hash=0x{hash:X16} stage={stage} " +
                $"expected=[{Describe(expected)}] actual=[{Describe(layout)}]");
        }
    }

    private static string Describe(BindingLayout layout) =>
        $"push={layout.PushDataStartDword} base={layout.ShaderBaseDword} offsets={layout.MemoryOffsetDword}+{layout.MemoryOffsetCount} " +
        $"user=[{string.Join(",", layout.UserDataRegisters)}] " +
        string.Join(" ", layout.Descriptors.Select(binding => $"{binding.Kind}:{string.Join(",", binding.Resources)}"));
}
