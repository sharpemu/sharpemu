// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

public enum MemoryResourceKind : byte
{
    None,
    ScalarBuffer,
    ScalarAddress,
    Buffer,
    Flat,
    Global,
    Scratch,
    LocalDataShare,
    GlobalDataShare,
    Image,
    Sampler,
}

public enum MemoryAccess : byte
{
    None,
    Read,
    Write,
    Atomic,
}

public enum ImageDimension : byte
{
    Unknown,
    Dim1D,
    Dim1DArray,
    Dim2D,
    Dim3D,
    Dim2DArray,
    Dim2DMsaa,
    Dim2DMsaaArray,
}

public enum ImageResourceClass : byte
{
    None,
    Sampled,
    Storage,
}

[Flags]
public enum ImageSampleFlags : uint
{
    None = 0,
    Compare = 1u << 3,
    Adjust = 1u << 10,
}

// The decoded facts of one memory access, one entry per accessed dword for scalar
// loads. The tracker fills Resource and Sampler; the planner sets PlanningOnly.
public sealed class MemoryAccessInfo
{
    public const uint NoResource = uint.MaxValue;

    public uint Pc { get; init; }
    public string Opcode { get; init; } = string.Empty;
    public MemoryResourceKind Kind { get; init; }
    public MemoryAccess Access { get; init; }
    public uint Resource { get; set; } = NoResource;
    public uint Sampler { get; set; } = NoResource;
    public uint Offset { get; init; }
    public uint Dmask { get; init; }
    public uint DataDwords { get; init; } = 1;
    public uint DataBits { get; init; } = 32;
    public uint ComponentIndex { get; init; }
    public uint ComponentCount { get; init; } = 1;
    public uint DataFormat { get; init; }
    public uint NumberFormat { get; init; }
    public ImageSampleFlags ImageSampleFlags { get; init; }
    public ImageDimension ImageDimension { get; init; }
    public ImageResourceClass ImageClass { get; init; }
    public bool NeedsSampler { get; init; }
    public bool AddressIsFull { get; init; }
    public bool DataSigned { get; init; }
    public bool Typed { get; init; }
    public bool Formatted { get; init; }
    public bool ImageHasMip { get; init; }
    public bool ImageR128 { get; init; }
    public bool Glc { get; init; }
    public bool Slc { get; init; }
    public bool IndexEnabled { get; init; }
    public bool OffsetEnabled { get; init; }
    public bool PlanningOnly { get; set; }
    // A scalar buffer load whose descriptor only exists on the device: the shader reads
    // through the descriptor in its registers instead of a host-bound buffer.
    public bool DeviceDescriptor { get; set; }

    // The guest V# behind a buffer access, filled in by the resource tracker.
    // Null until then. Its provenance tells the backend which lowering
    // strategy to pick; the access shape (Typed/Formatted/Access) completes
    // the choice via GuestBufferDescriptor.ChooseStrategy.
    public GuestBufferDescriptor? BufferDescriptor { get; set; }

    public bool IsAddressKind =>
        Kind is MemoryResourceKind.ScalarAddress or MemoryResourceKind.Flat or
            MemoryResourceKind.Global or MemoryResourceKind.Scratch;

    // The decoded facts of two accesses are the same when every field but the program
    // counter and the tracker's patches agree.
    public bool SameAccess(MemoryAccessInfo other) =>
        Kind == other.Kind && Access == other.Access && Offset == other.Offset && Dmask == other.Dmask &&
        DataDwords == other.DataDwords && DataBits == other.DataBits && ComponentIndex == other.ComponentIndex &&
        ComponentCount == other.ComponentCount && DataFormat == other.DataFormat && NumberFormat == other.NumberFormat &&
        ImageSampleFlags == other.ImageSampleFlags && ImageDimension == other.ImageDimension &&
        ImageClass == other.ImageClass && NeedsSampler == other.NeedsSampler && AddressIsFull == other.AddressIsFull &&
        DataSigned == other.DataSigned && Typed == other.Typed && Formatted == other.Formatted &&
        ImageHasMip == other.ImageHasMip && ImageR128 == other.ImageR128 && Glc == other.Glc && Slc == other.Slc &&
        IndexEnabled == other.IndexEnabled && OffsetEnabled == other.OffsetEnabled &&
        Resource == other.Resource && Sampler == other.Sampler && PlanningOnly == other.PlanningOnly &&
        DeviceDescriptor == other.DeviceDescriptor;
}

// Every memory access of a program, indexed by program counter and component.
public sealed class MemoryAccessTable
{
    private readonly List<MemoryAccessInfo> _entries = [];
    private readonly Dictionary<(uint Pc, uint Component), int> _indexByPc = [];

    public IReadOnlyList<MemoryAccessInfo> Entries => _entries;

    public int Count => _entries.Count;

    public MemoryAccessInfo this[int index] => _entries[index];

    public int Add(MemoryAccessInfo entry)
    {
        var index = _entries.Count;
        _entries.Add(entry);
        _indexByPc[(entry.Pc, entry.ComponentIndex)] = index;
        return index;
    }

    public bool TryGetIndex(uint pc, uint component, out int index) =>
        _indexByPc.TryGetValue((pc, component), out index);

    public MemoryAccessInfo? Find(uint pc, uint component = 0) =>
        _indexByPc.TryGetValue((pc, component), out var index) ? _entries[index] : null;

    // One entry per memory instruction; scalar loads get one entry per loaded dword.
    public static MemoryAccessTable Build(Gen5ShaderProgram program, IReadOnlySet<uint>? fixedFunctionVertexLoads = null)
    {
        var table = new MemoryAccessTable();
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is Gen5BufferMemoryControl && fixedFunctionVertexLoads?.Contains(instruction.Pc) == true)
                continue;
            switch (instruction.Control)
            {
                case Gen5ScalarMemoryControl scalar:
                    AddScalarLoad(table, instruction, scalar);
                    break;
                case Gen5BufferMemoryControl buffer:
                    table.Add(FromBuffer(instruction, buffer));
                    break;
                case Gen5GlobalMemoryControl global:
                    table.Add(FromGlobal(instruction, global));
                    break;
                case Gen5ImageControl image:
                    table.Add(FromImage(instruction, image));
                    break;
                case Gen5DataShareControl share:
                    if (instruction.Opcode is "DsSwizzleB32" or "DsBpermuteB32")
                    {
                        break;
                    }
                    table.Add(new MemoryAccessInfo
                    {
                        Pc = instruction.Pc,
                        Opcode = instruction.Opcode,
                        Kind = share.Gds ? MemoryResourceKind.GlobalDataShare : MemoryResourceKind.LocalDataShare,
                        Access = DataShareAccess(instruction.Opcode),
                        Offset = share.SingleOffsetBytes,
                    });
                    break;
            }
        }

        return table;
    }

    private static void AddScalarLoad(MemoryAccessTable table, Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control)
    {
        var isBuffer = instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal);
        for (uint component = 0; component < control.DestinationCount; component++)
        {
            table.Add(new MemoryAccessInfo
            {
                Pc = instruction.Pc,
                Opcode = instruction.Opcode,
                Kind = isBuffer ? MemoryResourceKind.ScalarBuffer : MemoryResourceKind.ScalarAddress,
                Access = MemoryAccess.Read,
                Offset = unchecked((uint)control.ImmediateOffsetBytes + component * sizeof(uint)),
                DataDwords = 1,
                ComponentIndex = component,
                ComponentCount = control.DestinationCount,
            });
        }
    }

    private static MemoryAccessInfo FromBuffer(Gen5ShaderInstruction instruction, Gen5BufferMemoryControl control)
    {
        var opcode = instruction.Opcode;
        var access = opcode.Contains("Atomic", StringComparison.Ordinal) ? MemoryAccess.Atomic
            : opcode.Contains("Store", StringComparison.Ordinal) ? MemoryAccess.Write
            : MemoryAccess.Read;
        var (bits, signed) = SubwordBits(opcode);
        uint dataFormat = 0;
        uint numberFormat = 0;
        if (control.Typed)
        {
            Gfx10UnifiedFormat.TryDecode(control.TypedFormat, out dataFormat, out numberFormat);
        }

        return new MemoryAccessInfo
        {
            Pc = instruction.Pc,
            Opcode = opcode,
            Kind = MemoryResourceKind.Buffer,
            Access = access,
            Offset = unchecked((uint)control.OffsetBytes),
            DataDwords = Math.Max(control.DwordCount, 1u),
            DataBits = bits,
            ComponentCount = Math.Max(control.DwordCount, 1u),
            DataFormat = dataFormat,
            NumberFormat = numberFormat,
            DataSigned = signed,
            Typed = control.Typed,
            Formatted = opcode.Contains("Format", StringComparison.Ordinal),
            Glc = control.Glc,
            Slc = control.Slc,
            IndexEnabled = control.IndexEnabled,
            OffsetEnabled = control.OffsetEnabled,
        };
    }

    private const uint NullScalarRegister = 125;

    private static MemoryAccessInfo FromGlobal(Gen5ShaderInstruction instruction, Gen5GlobalMemoryControl control)
    {
        var opcode = instruction.Opcode;
        var access = opcode.Contains("Atomic", StringComparison.Ordinal) ? MemoryAccess.Atomic
            : opcode.Contains("Store", StringComparison.Ordinal) ? MemoryAccess.Write
            : MemoryAccess.Read;
        var (bits, signed) = SubwordBits(opcode);
        return new MemoryAccessInfo
        {
            Pc = instruction.Pc,
            Opcode = opcode,
            Kind = opcode.StartsWith("Scratch", StringComparison.Ordinal)
                ? MemoryResourceKind.Scratch
                : control.UsesFlatAddress
                    ? MemoryResourceKind.Flat
                    : MemoryResourceKind.Global,
            Access = access,
            Offset = unchecked((uint)control.OffsetBytes),
            DataDwords = Math.Max(control.DwordCount, 1u),
            DataBits = bits,
            ComponentCount = Math.Max(control.DwordCount, 1u),
            DataSigned = signed,
            AddressIsFull = control.UsesFlatAddress || control.ScalarAddress == NullScalarRegister,
            Glc = control.Glc,
            Slc = control.Slc,
        };
    }

    private static MemoryAccessInfo FromImage(Gen5ShaderInstruction instruction, Gen5ImageControl control)
    {
        var opcode = instruction.Opcode;
        if (opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay")
        {
            // The current backends return a deterministic miss for raw GFX10
            // BVH traversal. Do not materialize its descriptor as a texture;
            // BVH descriptors do not use the regular image descriptor layout.
            return new MemoryAccessInfo
            {
                Pc = instruction.Pc,
                Opcode = opcode,
                Kind = MemoryResourceKind.None,
                Access = MemoryAccess.None,
                DataDwords = 4,
                ComponentCount = 4,
            };
        }

        var atomic = opcode.StartsWith("ImageAtomic", StringComparison.Ordinal);
        var store = opcode.StartsWith("ImageStore", StringComparison.Ordinal);
        var sampled = opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
            opcode.StartsWith("ImageGather", StringComparison.Ordinal) ||
            opcode == "ImageGetLod";
        var compare = (opcode.StartsWith("ImageSampleC", StringComparison.Ordinal) && !opcode.StartsWith("ImageSampleCd", StringComparison.Ordinal)) ||
            opcode.StartsWith("ImageGather4C", StringComparison.Ordinal);
        var mimgOpcode = instruction.Words.Count != 0
            ? ((instruction.Words[0] >> 18) & 0x7F) | ((instruction.Words[0] & 1) << 7)
            : 0u;
        var adjust = mimgOpcode is
            0xA0 or 0xA1 or 0xA5 or 0xA6 or 0xA8 or 0xA9 or 0xAD or 0xAE or
            0xB0 or 0xB1 or 0xB5 or 0xB6 or 0xB8 or 0xB9 or 0xBD or 0xBE;
        return new MemoryAccessInfo
        {
            Pc = instruction.Pc,
            Opcode = opcode,
            Kind = MemoryResourceKind.Image,
            Access = atomic ? MemoryAccess.Atomic : store ? MemoryAccess.Write : MemoryAccess.Read,
            ImageClass = atomic || store ? ImageResourceClass.Storage : ImageResourceClass.Sampled,
            NeedsSampler = sampled,
            Dmask = control.Dmask,
            ImageDimension = DecodeImageDimension(control.Dimension),
            ImageSampleFlags =
                (compare ? ImageSampleFlags.Compare : ImageSampleFlags.None) |
                (adjust ? ImageSampleFlags.Adjust : ImageSampleFlags.None),
            ImageHasMip = opcode is "ImageLoadMip" or "ImageStoreMip",
            ImageR128 = instruction.Words.Count != 0 && ((instruction.Words[0] >> 15) & 1) != 0,
            Glc = control.Glc,
            Slc = control.Slc,
        };
    }

    public static ImageDimension DecodeImageDimension(uint dimension) => dimension switch
    {
        0 => ImageDimension.Dim1D,
        1 => ImageDimension.Dim2D,
        2 => ImageDimension.Dim3D,
        3 => ImageDimension.Dim2DArray,
        4 => ImageDimension.Dim1DArray,
        5 => ImageDimension.Dim2DArray,
        6 => ImageDimension.Dim2DMsaa,
        7 => ImageDimension.Dim2DMsaaArray,
        _ => ImageDimension.Unknown,
    };

    private static (uint Bits, bool Signed) SubwordBits(string opcode)
    {
        if (opcode.Contains("byte", StringComparison.OrdinalIgnoreCase))
        {
            return (8, opcode.Contains("Sbyte", StringComparison.Ordinal));
        }

        if (opcode.Contains("short", StringComparison.OrdinalIgnoreCase))
        {
            return (16, opcode.Contains("Sshort", StringComparison.Ordinal));
        }

        return (32, false);
    }

    private static MemoryAccess DataShareAccess(string opcode) =>
        Gen5ShaderTranslator.IsDataShareAtomic(opcode) ? MemoryAccess.Atomic
        : opcode.StartsWith("DsWrite", StringComparison.Ordinal) ? MemoryAccess.Write
        : MemoryAccess.Read;
}
