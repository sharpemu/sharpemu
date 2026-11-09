// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Numerics;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

public sealed record GcnStorageImageControl(uint Address, uint Data, uint Resource, bool Array, uint Dmask) : Gen5InstructionControl;

// Unfiltered accesses use the shared typed-buffer backend and GCN physical addressing.
public static class GcnStorageImageLowering
{
    internal const uint ProjectionMarker = 0xF000A000;
    internal const uint ProjectionMask = 0xF000E000;
    private const uint MaxThinLevels = 15;
    private const uint NativeTile = 14;
    private const uint NativeDisplayTile = 10;
    private const int DescriptorScalars = 8;
    private const int ExpandedScalars = 28;
    private static readonly uint[] PairScalars = [20, 22, 26];
    private static readonly uint[] SingleScalars = [8, 9, 10, 11, 12, 13, 16, 17, 19, 24, 25];
    private static readonly ConcurrentDictionary<uint, byte> UnsupportedImageLoads = new();

    public static uint[] NormalizeProjection(ReadOnlySpan<uint> words)
    {
        var layout = words[3];
        var type = (layout >> 16) & 15;
        var tile = (layout >> 20) & 31;
        var format = layout & 63;
        var number = (layout >> 6) & 15;
        if (tile is NativeTile or NativeDisplayTile)
            return EmptyProjection();
        if (tile == 0 && format == 4 && number == 7 && (layout & (1u << 26)) == 0 &&
            GcnImageDescriptor.IsGpuDepthAddress(words[0] | ((ulong)(words[1] & 0xFF) << 32)))
            return EmptyProjection();
        if (type is not (8 or 9 or 10 or 11 or 12 or 13) || tile is not (8 or 13 or 19 or 31) ||
            format is < 1 or > 14 or 13 || (tile == 19 && type != 10) || (layout & (1u << 27)) != 0)
            return UnsupportedProjection(layout, type, tile, format, number);
        // sRGB decoding belongs to reads. Storage writes use the normalized backing format.
        if (number == 9 && (layout & (1u << 26)) != 0 && format is 1 or 10)
            number = 0;
        if (number > 7)
            return UnsupportedProjection(layout, type, tile, format, number);
        var typed = GcnBufferDescriptor.Normalize([0, 0, 0, (format << 15) | (number << 12) | 0xFAC]);
        return [words[0], words[1], words[2], typed[3]];
    }

    private static uint[] UnsupportedProjection(uint layout, uint type, uint tile, uint format, uint number)
    {
        if ((layout & (1u << 26)) != 0)
            throw new InvalidDataException($"Unsupported GCN storage-image store: type={type} tile={tile} format={format} number={number} flags=0x{layout:X8}.");

        if (UnsupportedImageLoads.TryAdd(layout, 0))
            Console.Error.WriteLine($"[GNMMODE][WARN] Unsupported GCN image load: type={type} tile={tile} format={format} number={number} flags=0x{layout:X8} " +
                $"mip={(layout >> 10) & 1} atc={(layout >> 11) & 1} compressed={(layout >> 12) & 1}; returning zero texels.");

        // shortcut: unsupported tile/mip image loads return zeros until their physical address layouts are implemented.
        return EmptyProjection();
    }

    private static uint[] EmptyProjection()
    {
        var empty = GcnBufferDescriptor.Normalize([0, 0, 0, (10u << 15) | 0xFAC]);
        return [0, 0, 0, empty[3]];
    }

    public static bool TryLower(Gen5ShaderProgram original, out Gen5ShaderProgram program, out string error,
        bool sampledLoads = false)
    {
        if (sampledLoads)
        {
            original = original with
            {
                Instructions = original.Instructions.Select(instruction =>
                    instruction.Control is GcnStorageImageControl image && instruction.Opcode is "ImageLoad" or "ImageLoadMip"
                        ? instruction with
                        {
                            Control = new Gen5ImageControl(image.Dmask, image.Address, [], image.Data,
                                image.Resource, 0, image.Array ? 3u : 1u, image.Array,
                                (instruction.Words[0] & (1u << 13)) != 0,
                                (instruction.Words[0] & (1u << 25)) != 0, false, false),
                        }
                        : instruction).ToArray(),
            };
        }
        program = original;
        error = "";
        var hasImages = original.Instructions.Any(instruction => instruction.Control is GcnStorageImageControl);
        var hasAddress64 = original.Instructions.Any(IsAddress64);
        static bool IsBuffer(Gen5ShaderInstruction instruction) => instruction.Control is Gen5BufferMemoryControl ||
            instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal);
        if (!hasImages && !original.Instructions.Any(IsBuffer)) return true;
        var (scalarUsed, vectorUsed) = GcnProgramRewriter.UsedRegisters(original.Instructions, spareHighScalars: true);
        var scalarFree = scalarUsed.Count(used => !used);
        var scalars = AllocateScalars(scalarUsed, hasImages);
        var vector = GcnProgramRewriter.FindFree(vectorUsed, hasImages ? 16 : hasAddress64 ? 2 : 0);
        if (scalars is null || vector < 0)
        {
            error = $"gcn-storage-image-lowering-has-no-free-registers scalarFree={scalarFree} vector={vector}";
            return false;
        }
        var scalar = (int)scalars[0];
        return GcnProgramRewriter.TryRewrite(original,
            (Gen5ShaderInstruction instruction, Action<Gen5ShaderInstruction> Add, out string failure) =>
            {
                failure = "";
                if (instruction.Control is GcnStorageImageControl image)
                {
                    var components = (uint)BitOperations.PopCount(image.Dmask);
                    if (image.Resource > 120 || image.Address > (instruction.Opcode == "ImageLoadMip" ? 252u : 253u) ||
                        components == 0 || image.Data + components > 256 ||
                        instruction.Opcode == "ImageStore" && image.Dmask is not (1 or 3 or 7 or 15))
                    {
                        failure = $"unsupported-gcn-storage-image-operands pc=0x{instruction.Pc:X} op={instruction.Opcode} " +
                            $"dmask=0x{image.Dmask:X} resource=s{image.Resource} address=v{image.Address} data=v{image.Data}";
                        return true;
                    }
                    Expand(instruction, image, scalars, (uint)vector, Add);
                    return true;
                }
                if (IsAddress64(instruction))
                {
                    ExpandAddress64(instruction, (uint)scalar, (uint)vector, Add);
                    return true;
                }
                if (!IsBuffer(instruction)) return false;
                var source = instruction.Control is Gen5BufferMemoryControl buffer ? buffer.ScalarResource : instruction.Sources[0].Value;
                var temporary = (uint)scalar;
                Gen5Operand S(uint register) => Gen5Operand.Scalar(register);
                Gen5Operand C(uint value) => new(Gen5OperandKind.LiteralConstant, value);
                void Emit(Gen5ShaderEncoding encoding, string name, Gen5Operand[] inputs, uint destination) =>
                    Add(new Gen5ShaderInstruction(0, encoding, name, [0], inputs, [S(destination)], null) { AddressOffset = instruction.ProgramOffset });
                Emit(Gen5ShaderEncoding.Sop2, "SCselectB32", [C(1), C(0)], temporary + 4);
                for (var index = 0u; index < 4; index++)
                    if (index == 1) Emit(Gen5ShaderEncoding.Sop2, "SAndB32", [S(source + index), C(0xFFFF00FF)], temporary + index);
                    else Emit(Gen5ShaderEncoding.Sop1, "SMovB32", [S(source + index)], temporary + index);
                var sources = instruction.Sources.ToArray();
                sources[instruction.Control is Gen5BufferMemoryControl ? 1 : 0] = S(temporary);
                var normalizedControl = instruction.Control;
                if (instruction.Control is Gen5BufferMemoryControl bufferControl)
                {
                    normalizedControl = bufferControl with { ScalarResource = temporary, SplitDescriptorFormat = true };
                    if (bufferControl.Typed)
                    {
                        if (!GcnBufferDescriptor.TryNormalizeFormat(bufferControl.TypedFormat, out var unified))
                        {
                            failure = $"unsupported-gcn-typed-buffer-format:{bufferControl.TypedFormat}";
                            return true;
                        }
                        normalizedControl = bufferControl with { ScalarResource = temporary, TypedFormat = unified, SplitDescriptorFormat = true };
                    }
                }
                Add(instruction with
                {
                    Sources = sources,
                    Control = normalizedControl,
                    AddressOffset = instruction.ProgramOffset,
                });
                Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopc, "SCmpEqU32", [0], [S(temporary + 4), C(1)], [], null)
                    { AddressOffset = instruction.ProgramOffset });
                return true;
            }, "gcn-storage-image-lowering-branch-overflow", out program, out error);
    }

    private static bool IsAddress64(Gen5ShaderInstruction instruction) =>
        instruction.Control is Gen5BufferMemoryControl && instruction.Encoding == Gen5ShaderEncoding.Mubuf &&
        (instruction.Words[0] & (1u << 15)) != 0;

    private static void ExpandAddress64(Gen5ShaderInstruction original, uint scalar, uint vector,
        Action<Gen5ShaderInstruction> add)
    {
        var buffer = (Gen5BufferMemoryControl)original.Control!;
        Gen5Operand S(uint index) => Gen5Operand.Scalar(scalar + index);
        Gen5Operand V(uint index) => Gen5Operand.Vector(vector + index);
        static Gen5Operand C(uint value) => new(Gen5OperandKind.LiteralConstant, value);
        void Emit(Gen5ShaderEncoding encoding, string opcode, Gen5Operand[] sources, Gen5Operand[] destinations,
            Gen5InstructionControl? control = null) =>
            add(new Gen5ShaderInstruction(0, encoding, opcode, [0], sources, destinations, control)
                { AddressOffset = original.ProgramOffset });
        Emit(Gen5ShaderEncoding.Sop2, "SCselectB32", [C(1), C(0)], [S(4)]);
        Emit(Gen5ShaderEncoding.Sop2, "SAndB32", [Gen5Operand.Scalar(buffer.ScalarResource + 1), C(255)], [S(1)]);
        Emit(Gen5ShaderEncoding.Sop2, "SAddU32", [Gen5Operand.Scalar(buffer.ScalarResource), original.Sources[2]], [S(0)]);
        Emit(Gen5ShaderEncoding.Sop2, "SAddcU32", [S(1), C(0)], [S(1)]);
        var carry = new Gen5Vop3Control(0, 0, 0, false, 0, scalar + 6);
        Emit(Gen5ShaderEncoding.Vop3, "VAddCoU32", [S(0), Gen5Operand.Vector(buffer.VectorAddress)], [V(0)], carry);
        Emit(Gen5ShaderEncoding.Vop3, "VAddCoCiU32", [S(1), Gen5Operand.Vector(buffer.VectorAddress + 1), S(6)], [V(1)], carry);
        var load = original.Opcode.StartsWith("BufferLoad", StringComparison.Ordinal);
        Gen5Operand[] sources = [V(0), V(1)];
        if (!load)
            sources = [.. sources, .. Enumerable.Range((int)buffer.VectorData, (int)buffer.DwordCount)
                .Select(index => Gen5Operand.Vector((uint)index))];
        add(original with
        {
            Encoding = Gen5ShaderEncoding.Flat,
            Opcode = "Global" + original.Opcode["Buffer".Length..],
            Sources = sources,
            Destinations = load ? original.Destinations : [],
            Control = new Gen5GlobalMemoryControl(buffer.DwordCount, vector, buffer.VectorData, buffer.VectorData,
                125, buffer.OffsetBytes, buffer.Glc, buffer.Slc),
            AddressOffset = original.ProgramOffset,
        });
        Emit(Gen5ShaderEncoding.Sopc, "SCmpEqU32", [S(4), C(1)], []);
    }

    private static uint[]? AllocateScalars(bool[] used, bool hasImages)
    {
        var scalars = new uint[hasImages ? ExpandedScalars : DescriptorScalars];
        var descriptor = -1;
        for (var start = 0; start + DescriptorScalars <= used.Length && descriptor < 0; start += 4)
            if (!used.AsSpan(start, DescriptorScalars).Contains(true))
                descriptor = start;
        if (descriptor < 0)
            return null;
        for (var index = 0; index < DescriptorScalars; index++)
        {
            scalars[index] = (uint)(descriptor + index);
            used[descriptor + index] = true;
        }
        if (!hasImages)
            return scalars;
        foreach (var pair in PairScalars)
        {
            if (Take(used, 2) is not { } register)
                return null;
            scalars[pair] = register;
            scalars[pair + 1] = register + 1;
        }
        foreach (var single in SingleScalars)
        {
            if (Take(used, 1) is not { } register)
                return null;
            scalars[single] = register;
        }
        return scalars;
    }

    private static uint? Take(bool[] used, int width)
    {
        for (var register = 0; register + width <= used.Length; register += width)
        {
            if (used.AsSpan(register, width).Contains(true))
                continue;
            used.AsSpan(register, width).Fill(true);
            return (uint)register;
        }
        return null;
    }

    private static void Expand(Gen5ShaderInstruction original, GcnStorageImageControl image, uint[] scalars, uint vector,
        Action<Gen5ShaderInstruction> add)
    {
        Gen5Operand S(uint index) => Gen5Operand.Scalar(scalars[index]);
        Gen5Operand V(uint index) => Gen5Operand.Vector(vector + index);
        static Gen5Operand C(uint value) => new(Gen5OperandKind.LiteralConstant, value);
        Gen5Operand T(uint index) => Gen5Operand.Scalar(image.Resource + index);
        Gen5ShaderInstruction Op(Gen5ShaderEncoding encoding, string name, Gen5Operand[] src, Gen5Operand[] dst) =>
            new(0, encoding, name, [0], src, dst, null) { AddressOffset = original.ProgramOffset };
        void S2(string name, uint dst, Gen5Operand a, Gen5Operand b) => add(Op(Gen5ShaderEncoding.Sop2, name, [a, b], [S(dst)]));
        void V2(string name, uint dst, Gen5Operand a, Gen5Operand b) => add(Op(Gen5ShaderEncoding.Vop2, name, [a, b], [V(dst)]));
        void Mov(uint dst, Gen5Operand value) => add(Op(Gen5ShaderEncoding.Vop1, "VMovB32", [value], [V(dst)]));
        void Compare(string name, Gen5Operand a, Gen5Operand b) => add(Op(Gen5ShaderEncoding.Vopc, name, [a, b], []));
        void ScalarEq(Gen5Operand a, Gen5Operand b) => add(Op(Gen5ShaderEncoding.Sopc, "SCmpEqU32", [a, b], []));
        void Select(uint dst, Gen5Operand yes, Gen5Operand no) => S2("SCselectB32", dst, yes, no);
        void Mul(uint dst, Gen5Operand a, Gen5Operand b) => add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3,
            "VMulLoU32", [0, 0], [a, b], [V(dst)], new Gen5Vop3Control(0, 0, 0, false, 0, null)) { AddressOffset = original.ProgramOffset });
        void SavePair(uint dst, uint src) => add(Op(Gen5ShaderEncoding.Sop1, "SMovB64", [Gen5Operand.Scalar(src)], [S(dst)]));
        void Move(uint dst, Gen5Operand value) => add(Op(Gen5ShaderEncoding.Sop1, "SMovB32", [value], [S(dst)]));
        void RoundPow2(uint register)
        {
            S2("SSubU32", 19, S(register), C(1));
            for (var shift = 1u; shift < 16; shift <<= 1)
            {
                S2("SLshrB32", 13, S(19), C(shift));
                S2("SOrB32", 19, S(19), S(13));
            }
            S2("SAddU32", 19, S(19), C(1));
            S2("SAndB32", 13, T(3), C(1u << 25));
            ScalarEq(S(13), C(0));
            Select(register, S(register), S(19));
        }
        // The address expansion must leave the guest's SCC, VCC and EXEC exactly as it found them.
        Select(24, C(1), C(0));
        SavePair(20, 126);
        SavePair(22, 106);
        S2("SLshlB32", 0, T(0), C(8));
        S2("SLshrB32", 1, T(0), C(24));
        S2("SAndB32", 4, T(1), C(63));
        S2("SLshlB32", 4, S(4), C(8));
        S2("SOrB32", 1, S(1), S(4));
        S2("SBfeU32", 4, T(4), C(13 | (14u << 16))); // explicit texel pitch
        S2("SAddU32", 4, S(4), C(1));
        S2("SBfeU32", 5, T(2), C(14 | (14u << 16)));
        S2("SAddU32", 5, S(5), C(1));
        S2("SAndB32", 6, T(4), C(8191));
        S2("SAddU32", 6, S(6), C(1));
        S2("SAndB32", 7, T(2), C(16383));
        S2("SAddU32", 7, S(7), C(1));
        S2("SLshrB32", 8, T(3), C(28));
        S2("SMulI32", 11, S(6), C(6));
        ScalarEq(S(8), C(11));
        Select(6, S(11), S(6)); // cube DEPTH counts cubes; array coordinates address faces
        S2("SBfeU32", 9, T(3), C(20 | (5u << 16)));
        // POW2_PAD changes the physical slice height, not the logical y bounds.
        S2("SSubU32", 10, S(5), C(1));
        for (var shift = 1u; shift < 16; shift <<= 1)
        {
            S2("SLshrB32", 11, S(10), C(shift));
            S2("SOrB32", 10, S(10), S(11));
        }
        S2("SAddU32", 10, S(10), C(1));
        S2("SAndB32", 11, T(3), C(1u << 25));
        ScalarEq(S(11), C(0));
        Select(10, S(5), S(10));
        S2("SAddU32", 10, S(10), C(7));
        S2("SAndB32", 10, S(10), C(0xFFFFFFF8));
        ScalarEq(S(9), C(13));
        Select(11, S(10), S(5));
        ScalarEq(S(9), C(19));
        Select(10, S(10), S(11));
        S2("SMulI32", 10, S(10), S(4)); // slice size in texels
        S2("SAddU32", 11, S(6), C(3));
        S2("SAndB32", 11, S(11), C(0xFFFFFFFC));
        ScalarEq(S(9), C(19));
        Select(11, S(11), S(6));
        S2("SMulI32", 2, S(10), S(11));
        S2("SBfeU32", 19, T(1), C(20 | (6u << 16)));
        add(Op(Gen5ShaderEncoding.Sopc, "SCmpGeU32", [S(19), C(2)], []));
        Select(16, C(2), C(1));
        add(Op(Gen5ShaderEncoding.Sopc, "SCmpGeU32", [S(19), C(4)], []));
        Select(16, C(4), S(16));
        add(Op(Gen5ShaderEncoding.Sopc, "SCmpGeU32", [S(19), C(11)], []));
        Select(16, C(8), S(16));
        ScalarEq(S(19), C(14));
        Select(16, C(16), S(16)); // bytes per texel, not bytes per channel
        S2("SMulI32", 2, S(2), S(16));
        S2("SBfeU32", 17, T(3), C(12 | (4u << 16)));
        ScalarEq(S(9), C(13));
        Select(17, S(17), C(0));
        Move(25, C(0));
        Move(26, S(10));
        Move(27, S(4));
        for (var level = 0u; level < MaxThinLevels; level++)
        {
            S2("SLshrB32", 11, S(4), C(level));
            S2("SMaxU32", 11, S(11), C(1));
            S2("SLshrB32", 12, S(5), C(level));
            S2("SMaxU32", 12, S(12), C(1));
            RoundPow2(11);
            RoundPow2(12);
            S2("SAddU32", 11, S(11), C(7));
            S2("SAndB32", 11, S(11), C(0xFFFFFFF8));
            S2("SAddU32", 12, S(12), C(7));
            S2("SAndB32", 12, S(12), C(0xFFFFFFF8));
            for (var step = 0; step < 3; step++)
            {
                S2("SAddU32", 19, S(11), C(8));
                S2("SMulI32", 13, S(11), S(12));
                S2("SMulI32", 13, S(13), S(16));
                S2("SAndB32", 13, S(13), C(255));
                ScalarEq(S(13), C(0));
                Select(11, S(11), S(19));
            }
            S2("SMulI32", 13, S(11), S(12));
            ScalarEq(S(17), C(level));
            Select(26, S(13), S(26));
            Select(27, S(11), S(27));
            S2("SMulI32", 13, S(13), S(16));
            S2("SMulI32", 13, S(13), S(6));
            add(Op(Gen5ShaderEncoding.Sopc, "SCmpGtU32", [S(17), C(level)], []));
            Select(13, S(13), C(0));
            S2("SAddU32", 25, S(25), S(13));
        }
        ScalarEq(S(9), C(13));
        Select(4, S(27), S(4));
        Select(10, S(26), S(10));
        S2("SLshrB32", 7, S(7), S(17));
        S2("SMaxU32", 7, S(7), C(1));
        S2("SLshrB32", 5, S(5), S(17));
        S2("SMaxU32", 5, S(5), C(1));
        S2("SMulI32", 13, S(10), S(16));
        S2("SMulI32", 13, S(13), S(6));
        ScalarEq(S(9), C(13));
        Select(2, S(13), S(2));
        S2("SAddU32", 0, S(0), S(25));
        S2("SAddcU32", 1, S(1), C(0));
        S2("SAndB32", 3, T(3), C(0x01F00000));
        S2("SLshrB32", 11, T(3), C(28));
        S2("SLshlB32", 11, S(11), C(16));
        S2("SOrB32", 3, S(3), S(11));
        S2("SOrB32", 3, S(3), C(ProjectionMarker));
        S2("SLshrB32", 11, T(1), C(20));
        S2("SAndB32", 11, S(11), C(1023));
        S2("SOrB32", 3, S(3), S(11));
        if (original.Opcode == "ImageStore")
            S2("SOrB32", 3, S(3), C(1u << 26));
        S2("SAndB32", 11, T(3), C(0x000FF000));
        ScalarEq(S(9), C(13));
        Select(11, C(0), S(11));
        S2("SAndB32", 12, T(3), C(1u << 27));
        S2("SOrB32", 11, S(11), S(12));
        S2("SAndB32", 12, T(6), C(1u << 21)); // compressed Neo images
        S2("SOrB32", 11, S(11), S(12));
        ScalarEq(S(11), C(0));
        Select(11, C(0), C(1u << 27));
        S2("SOrB32", 3, S(3), S(11));
        (Gen5Operand Word, uint Mask, uint Reason)[] reasons = [(T(3), 0x000FF000, 1u << 10), (T(3), 1u << 27, 1u << 11), (T(6), 1u << 21, 1u << 12)];
        foreach (var (word, mask, reason) in reasons)
        {
            S2("SAndB32", 12, word, C(mask));
            ScalarEq(S(12), C(0));
            Select(12, C(0), C(reason));
            S2("SOrB32", 3, S(3), S(12));
        }
        S2("SAndB32", 12, T(5), C(8191)); // base array
        S2("SBfeU32", 13, T(5), C(13 | (13u << 16)));
        S2("SAddU32", 13, S(13), C(1));
        S2("SSubU32", 13, S(13), S(12)); // view layer count
        ScalarEq(S(8), C(10));
        Select(13, S(6), S(13));
        Mov(0, Gen5Operand.Vector(image.Address));
        Mov(1, Gen5Operand.Vector(image.Address + 1));
        // Loads may overwrite their own address tuple before the native-image fallback.
        if (image.Array || original.Opcode == "ImageLoadMip")
            Mov(14, Gen5Operand.Vector(image.Address + 2));
        if (image.Array && original.Opcode == "ImageLoadMip")
            Mov(15, Gen5Operand.Vector(image.Address + 3));
        ScalarEq(S(8), C(8));
        Select(11, C(0), C(uint.MaxValue));
        ScalarEq(S(8), C(12));
        Select(11, C(0), S(11)); // only 1D and 1D-array omit y
        V2("VAndB32", 1, S(11), V(1));
        if (image.Array)
        {
            Mov(2, Gen5Operand.Vector(image.Address + 2));
            Mov(3, S(8));
            Compare("VCmpEqU32", C(12), V(3));
            V2("VCndmaskB32", 2, V(2), Gen5Operand.Vector(image.Address + 1));
        }
        else Mov(2, C(0));
        Mov(3, S(8));
        Compare("VCmpEqU32", C(10), V(3));
        V2("VCndmaskB32", 2, V(2), Gen5Operand.Vector(image.Address + 2));
        Compare("VCmpEqU32", C(11), V(3));
        V2("VCndmaskB32", 2, V(2), Gen5Operand.Vector(image.Address + 2));
        if (original.Opcode == "ImageLoadMip")
        {
            // shortcut: only single-mip image layouts are supported; a nonzero LOD reads no texels.
            Mov(9, Gen5Operand.Vector(image.Address + 3));
            Mov(8, S(8));
            Compare("VCmpEqU32", C(8), V(8));
            V2("VCndmaskB32", 9, V(9), Gen5Operand.Vector(image.Address + 1));
            Compare("VCmpEqU32", C(9), V(8));
            V2("VCndmaskB32", 9, V(9), Gen5Operand.Vector(image.Address + 2));
            Compare("VCmpEqU32", C(12), V(8));
            V2("VCndmaskB32", 9, V(9), Gen5Operand.Vector(image.Address + 2));
        }
        var load = original.Opcode is "ImageLoad" or "ImageLoadMip";
        var packed = image.Dmask is 1 or 3 or 7 or 15;
        if (load)
        {
            var components = (uint)BitOperations.PopCount(image.Dmask);
            for (var component = 0u; component < components; component++)
                add(Op(Gen5ShaderEncoding.Vop1, "VMovB32", [C(0)], [Gen5Operand.Vector(image.Data + component)]));
        }
        if (original.Opcode == "ImageLoadMip")
            Compare("VCmpxEqU32", C(0), V(9));
        Compare("VCmpxGtU32", S(7), V(0));
        Compare("VCmpxGtU32", S(5), V(1));
        Compare("VCmpxGtU32", S(13), V(2));
        V2("VAddU32", 2, S(12), V(2));
        Mul(3, S(4), V(1));
        V2("VAddU32", 3, V(3), V(0)); // linear texel offset
        V2("VLshrrevB32", 4, C(3), V(1));
        Mul(4, S(4), V(4));
        V2("VLshrrevB32", 5, C(3), V(0));
        V2("VLshlrevB32", 5, C(3), V(5));
        V2("VAddU32", 4, V(4), V(5));
        V2("VLshlrevB32", 4, C(3), V(4)); // microtile base in texels
        V2("VLshlrevB32", 6, C(2), V(4)); // 8x8x4 thick microtile base
        for (var bit = 0u; bit < 3; bit++)
        {
            V2("VAndB32", 5, C(1u << (int)bit), V(0));
            V2("VLshlrevB32", 5, C(bit), V(5));
            V2("VOrB32", 4, V(4), V(5));
            V2("VAndB32", 5, C(1u << (int)bit), V(1));
            V2("VLshlrevB32", 5, C(bit + 1), V(5));
            V2("VOrB32", 4, V(4), V(5));
        }
        // Thick 8/16-bit tiles interleave x0,y0,x1,y1,z0,z1,x2,y2.
        // At 32 bits per texel, y1 and z0 exchange positions.
        void Bit(uint destination, uint coordinate, uint sourceBit, uint targetBit)
        {
            V2("VAndB32", 5, C(1u << (int)sourceBit), V(coordinate));
            V2("VLshlrevB32", 5, C(targetBit - sourceBit), V(5));
            V2("VOrB32", destination, V(destination), V(5));
        }
        Mov(7, C(0));
        Bit(7, 0, 0, 0); Bit(7, 1, 0, 1);
        Bit(7, 2, 1, 5); Bit(7, 0, 2, 6); Bit(7, 1, 2, 7);
        Mov(8, V(7));
        Mov(9, V(7));
        Bit(7, 0, 1, 2); Bit(7, 1, 1, 3); Bit(7, 2, 0, 4);
        Bit(8, 0, 1, 2); Bit(8, 1, 1, 4); Bit(8, 2, 0, 3);
        Bit(9, 2, 0, 2); Bit(9, 0, 1, 3); Bit(9, 1, 1, 4);
        Mov(5, S(16));
        Compare("VCmpEqU32", C(4), V(5));
        V2("VCndmaskB32", 7, V(7), V(8));
        Compare("VCmpLeU32", C(8), V(5));
        V2("VCndmaskB32", 7, V(7), V(9));
        V2("VOrB32", 6, V(6), V(7));
        V2("VLshrrevB32", 8, C(2), V(2));
        Mul(8, S(10), V(8));
        V2("VLshlrevB32", 8, C(2), V(8));
        V2("VAddU32", 6, V(6), V(8));
        Mul(5, S(10), V(2));
        V2("VAddU32", 3, V(3), V(5));
        V2("VAddU32", 4, V(4), V(5));
        Mov(5, S(9));
        Compare("VCmpEqU32", C(13), V(5));
        V2("VCndmaskB32", 3, V(3), V(4));
        Compare("VCmpEqU32", C(19), V(5));
        V2("VCndmaskB32", 3, V(3), V(6));
        Mul(3, S(16), V(3));
        var count = image.Dmask switch { 3 => 2u, 7 => 3u, 15 => 4u, _ => 1u };
        var dataRegister = load && packed ? image.Data : vector + 10;
        if (!load)
            for (var component = 0u; component < 4; component++)
                Mov(10 + component, component < count ? Gen5Operand.Vector(image.Data + component) : C(0));
        var data = Gen5Operand.Vector(dataRegister);
        if (!load || !packed) count = 4;
        SavePair(26, 126);
        void CompareNativeTile()
        {
            ScalarEq(S(9), C(NativeTile));
            Select(19, C(1), C(0));
            ScalarEq(S(9), C(NativeDisplayTile));
            Select(19, C(1), S(19));
            if (load)
            {
                ScalarEq(S(9), C(0));
                Select(19, C(1), S(19));
            }
            ScalarEq(S(19), C(1));
        }
        CompareNativeTile();
        add(Op(Gen5ShaderEncoding.Sop2, "SCselectB64", [C(0), S(26)], [Gen5Operand.Scalar(106)]));
        add(Op(Gen5ShaderEncoding.Sop1, "SMovB64", [Gen5Operand.Scalar(106)], [Gen5Operand.Scalar(126)]));
        var suffix = count switch { 2 => "Xy", 3 => "Xyz", 4 => "Xyzw", _ => "X" };
        add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mubuf, (load ? "BufferLoadFormat" : "BufferStoreFormat") + suffix, [0, 0],
            load ? [V(3), S(0), C(0)] : [V(3), S(0), C(0), data], load ? [data] : [],
            new Gen5BufferMemoryControl(count, vector + 3, dataRegister, scalars[0], 0, false, true, false, false)) { AddressOffset = original.ProgramOffset });
        if (load && !packed)
            for (uint channel = 0, component = 0; channel < 4; channel++)
                if ((image.Dmask & (1u << (int)channel)) != 0)
                    add(Op(Gen5ShaderEncoding.Vop1, "VMovB32", [V(10 + channel)], [Gen5Operand.Vector(image.Data + component++)]));
        for (var word = 0u; word < DescriptorScalars; word++)
            Move(word, T(word));
        S2("SOrB32", 5, S(5), C(GcnImageDescriptor.NativeStorageMarker));
        CompareNativeTile();
        add(Op(Gen5ShaderEncoding.Sop2, "SCselectB64", [S(26), C(0)], [Gen5Operand.Scalar(106)]));
        add(Op(Gen5ShaderEncoding.Sop1, "SMovB64", [Gen5Operand.Scalar(106)], [Gen5Operand.Scalar(126)]));
        var coordinates = Enumerable.Range(0,
                (image.Array ? 3 : 2) + (original.Opcode == "ImageLoadMip" ? 1 : 0))
            .Select(index => vector + (uint)(index < 2 ? index : index + 12)).ToArray();
        add(original with
        {
            Sources = [V(0), S(0)],
            Control = new Gen5ImageControl(image.Dmask, vector, coordinates, image.Data, scalars[0], 0,
                image.Array ? 3u : 1u, image.Array, (original.Words[0] & (1u << 13)) != 0,
                (original.Words[0] & (1u << 25)) != 0, false, false),
            AddressOffset = original.ProgramOffset,
        });
        add(Op(Gen5ShaderEncoding.Sop1, "SMovB64", [S(20)], [Gen5Operand.Scalar(126)]));
        add(Op(Gen5ShaderEncoding.Sop1, "SMovB64", [S(22)], [Gen5Operand.Scalar(106)]));
        ScalarEq(S(24), C(1));
    }
}
