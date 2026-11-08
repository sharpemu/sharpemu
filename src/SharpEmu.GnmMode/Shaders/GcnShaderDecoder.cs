// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

// GFX6/7 opcode and width decoding. Only the normalized IR is shared with AGC.
public static partial class GcnShaderDecoder
{
    // GCN vertex fetch shaders are pointed to by user SGPRs and have no OrbShdr
    // locator. Decode only through their terminal S_SETPC_B64 return.
    public static bool TryDecodeFunction(ICpuMemory memory, ulong address,
        out Gen5ShaderProgram program, out uint size, out string error)
    {
        program = new Gen5ShaderProgram(address, []);
        size = 0;
        error = string.Empty;
        const uint maximumSize = 64 * 1024;
        var instructions = new List<Gen5ShaderInstruction>();
        var furthestBranchTarget = 0u;
        Span<byte> bytes = stackalloc byte[16];
        for (var pc = 0u; pc < maximumSize;)
        {
            // Four dwords cover all currently supported GCN instruction forms.
            var readable = address > ulong.MaxValue - pc ? 0 : ReadInstructionWindow(memory, address + pc, bytes);
            if (readable == 0)
            {
                error = $"gcn-fetch-code-unreadable pc=0x{pc:X} address=0x{address:X}";
                return false;
            }
            if (!TryDecodeInstruction(bytes, 0, out var instruction, out error))
            {
                error = $"gcn-fetch {error} at offset=0x{pc:X}";
                return false;
            }
            if (instruction.Words.Count * 4 > readable)
            {
                error = $"gcn-fetch-code-unreadable pc=0x{pc + (uint)readable:X} address=0x{address:X}";
                return false;
            }
            instruction = instruction with { Pc = pc };
            instructions.Add(instruction);
            if (instruction.Opcode is "SBranch" || instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                var target = (long)pc + 4 + unchecked((short)instruction.Words[0]) * 4L;
                if (target < 0 || target >= maximumSize)
                {
                    error = $"gcn-fetch-branch-outside-code pc=0x{pc:X} target=0x{target:X}";
                    return false;
                }
                furthestBranchTarget = Math.Max(furthestBranchTarget, (uint)target);
            }
            pc += checked((uint)instruction.Words.Count * 4);
            if (instruction.Opcode is "SSetpcB64" or "SEndpgm" && pc > furthestBranchTarget)
            {
                size = pc;
                program = new Gen5ShaderProgram(address, instructions);
                return true;
            }
        }
        error = "unterminated-gcn-fetch-program";
        return false;
    }

    private static int ReadInstructionWindow(ICpuMemory memory, ulong address, Span<byte> bytes)
    {
        for (var length = bytes.Length; length >= sizeof(uint); length -= sizeof(uint))
        {
            if (memory.TryRead(address, bytes[..length]))
            {
                bytes[length..].Clear();
                return length;
            }
        }

        return 0;
    }

    public static bool TryDecode(ICpuMemory memory, ulong address, uint size,
        out Gen5ShaderProgram program, out string error)
    {
        program = new Gen5ShaderProgram(address, []);
        error = string.Empty;
        if (size is < 4 or > 1024 * 1024 || size % 4 != 0 || address > ulong.MaxValue - size)
        {
            error = "invalid-gcn-code-range";
            return false;
        }
        var bytes = new byte[size];
        if (!memory.TryRead(address, bytes))
        {
            error = "gcn-code-unreadable";
            return false;
        }
        var instructions = new List<Gen5ShaderInstruction>();
        var branchTargets = new HashSet<uint>();
        var furthestTarget = 0u;
        for (var pc = 0u; pc < size;)
        {
            if (!TryDecodeInstruction(bytes, pc, out var instruction, out error))
            {
                error += FurtherUnsupported(bytes, pc + EncodedWidth(bytes, pc) * 4);
                return false;
            }
            instructions.Add(instruction);
            if (instruction.Encoding == Gen5ShaderEncoding.Sopp &&
                (instruction.Opcode == "SBranch" || instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal)))
            {
                var signedTarget = (long)pc + 4 + unchecked((short)instruction.Words[0]) * 4L;
                if (signedTarget < 0 || signedTarget >= size)
                {
                    error = $"gcn-branch-outside-code pc=0x{pc:X} target=0x{signedTarget:X}";
                    return false;
                }
                var target = (uint)signedTarget;
                branchTargets.Add(target);
                furthestTarget = Math.Max(furthestTarget, target);
            }
            pc += (uint)instruction.Words.Count * 4;
            if (instruction.Opcode is "SEndpgm" or "SBranch" && pc > furthestTarget)
            {
                if (branchTargets.Any(target => !instructions.Any(candidate => candidate.Pc == target)))
                {
                    error = "gcn-branch-target-inside-instruction";
                    return false;
                }
                program = new Gen5ShaderProgram(address, instructions);
                return true;
            }
        }
        error = "unterminated-gcn-program";
        return false;
    }

    private static string FurtherUnsupported(ReadOnlySpan<byte> bytes, uint pc)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        while (pc + 4 <= (uint)bytes.Length && missing.Count < 32)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)pc..]);
            if (word == 0xBF810000)
                break;
            if (!TryDecodeInstruction(bytes, pc, out _, out _))
                missing.Add(EncodedOpcode(word));
            pc += EncodedWidth(bytes, pc) * 4;
        }
        return missing.Count == 0 ? string.Empty : $" further-unsupported=[{string.Join(" ", missing)}]";
    }

    private static string EncodedOpcode(uint word) => (word >> 26) switch
    {
        0x30 or 0x31 => $"SMRD:{(word >> 22) & 31}",
        0x32 => $"VINTRP:{(word >> 16) & 3}",
        0x34 => $"VOP3:0x{(word >> 17) & 511:X}",
        0x36 => $"DS:{(word >> 18) & 255}",
        0x37 => $"FLAT:{(word >> 18) & 127}",
        0x38 => $"MUBUF:{(word >> 18) & 127}",
        0x3A => $"MTBUF:{(word >> 16) & 7}",
        0x3C => $"MIMG:0x{(word >> 18) & 127:X}",
        0x3E => "EXP",
        _ when (word & 0x80000000) == 0 => (word >> 25) switch
        {
            0x3F => $"VOP1:{(word >> 9) & 255}",
            0x3E => $"VOPC:0x{(word >> 17) & 255:X}",
            _ => $"VOP2:{(word >> 25) & 63}",
        },
        _ => ((word >> 23) & 511) switch
        {
            0x17D => $"SOP1:{(word >> 8) & 255}",
            0x17E => $"SOPC:{(word >> 16) & 127}",
            0x17F => $"SOPP:{(word >> 16) & 127}",
            _ when (word >> 28) == 0xB => $"SOPK:{(word >> 23) & 31}",
            _ => $"SOP2:{(word >> 23) & 127}",
        },
    };

    private static uint EncodedWidth(ReadOnlySpan<byte> bytes, uint pc)
    {
        var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)pc..]);
        if ((word >> 26) is 0x34 or 0x36 or 0x37 or 0x38 or 0x3A or 0x3C or 0x3E)
            return 2;
        if ((word >> 26) is 0x30 or 0x31)
            return (word & 0x1FF) == 0xFF ? 2u : 1u;
        if ((word >> 26) == 0x32)
            return 1;
        if ((word & 0x80000000) == 0)
        {
            var vop2 = (word >> 25) is not (0x3E or 0x3F);
            return (word & 511) == 255 || vop2 && ((word >> 25) & 63) is 32 or 33 ? 2u : 1u;
        }
        var major = (word >> 23) & 127;
        return major switch
        {
            127 => 1,
            125 => (word & 255) == 255 ? 2u : 1u,
            126 => (word & 255) == 255 || ((word >> 8) & 255) == 255 ? 2u : 1u,
            >= 96 => ((word >> 23) & 31) == 21 ? 2u : 1u,
            _ => (word & 255) == 255 || ((word >> 8) & 255) == 255 ? 2u : 1u,
        };
    }

    private static bool TryDecodeInstruction(ReadOnlySpan<byte> bytes, uint pc,
        out Gen5ShaderInstruction instruction, out string error)
    {
        instruction = default!;
        var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)pc..]);
        if (word >> 26 == 0x34)
            return TryDecodeVector3(bytes, pc, word, out instruction, out error);
        if (word >> 26 == 0x36)
            return TryDecodeDataShare(bytes, pc, word, out instruction, out error);
        var encoding = Gen5ShaderEncoding.Sopp;
        var name = string.Empty;
        var width = 1;
        var hasLiteral = false;
        var source = word & 255;
        if ((word & 0xC0000000) == 0x80000000)
        {
            var major = (word >> 23) & 127;
            encoding = major switch
            {
                125 => Gen5ShaderEncoding.Sop1, 126 => Gen5ShaderEncoding.Sopc,
                127 => Gen5ShaderEncoding.Sopp, >= 96 => Gen5ShaderEncoding.Sopk,
                _ => Gen5ShaderEncoding.Sop2,
            };
            name = ScalarOpcode(encoding, word);
            hasLiteral = encoding is Gen5ShaderEncoding.Sop1 or Gen5ShaderEncoding.Sop2 or Gen5ShaderEncoding.Sopc && source == 255 ||
                encoding is Gen5ShaderEncoding.Sop2 or Gen5ShaderEncoding.Sopc && ((word >> 8) & 255) == 255;
        }
        else if ((word & 0xF8000000) == 0xC0000000)
        {
            encoding = Gen5ShaderEncoding.Smrd;
            var opcode = (word >> 22) & 31;
            var count = opcode & 7;
            if (opcode <= 4 || opcode is >= 8 and <= 12)
                name = (opcode < 8 ? "SLoadDword" : "SBufferLoadDword") + (count == 0 ? "" : $"x{1 << (int)count}");
            hasLiteral = (word & 0x100) == 0 && source == 255;
            if ((word & 0x100) == 0 && source > 103 && !hasLiteral)
                name = string.Empty;
        }
        else if ((word & 0x80000000) == 0)
        {
            var major = (word >> 25) & 63;
            encoding = major switch { 62 => Gen5ShaderEncoding.Vopc, 63 => Gen5ShaderEncoding.Vop1, _ => Gen5ShaderEncoding.Vop2 };
            var opcode = encoding switch
            {
                Gen5ShaderEncoding.Vop1 => (word >> 9) & 255,
                Gen5ShaderEncoding.Vopc => (word >> 17) & 255,
                _ => major,
            };
            name = VectorOpcode(encoding, opcode);
            source = word & 511;
            hasLiteral = source == 255 || encoding == Gen5ShaderEncoding.Vop2 && opcode is 32 or 33;
            // These are not RDNA DPP/SDWA/literal-extension forms in GFX6/7.
            if (source is >= 209 and <= 239 or >= 249 and <= 250 or >= 252 and <= 254)
                name = string.Empty;
        }
        else if (word >> 26 == 0x32)
        {
            encoding = Gen5ShaderEncoding.Vintrp;
            name = ((word >> 16) & 3) switch
            {
                0 => "VInterpP1F32", 1 => "VInterpP2F32", 2 => "VInterpMovF32", _ => string.Empty,
            };
        }
        else if (word >> 26 == 0x38)
        {
            encoding = Gen5ShaderEncoding.Mubuf;
            width = 2;
            name = ((word >> 18) & 127) switch
            {
                0 => "BufferLoadFormatX", 1 => "BufferLoadFormatXy", 2 => "BufferLoadFormatXyz", 3 => "BufferLoadFormatXyzw",
                4 => "BufferStoreFormatX", 5 => "BufferStoreFormatXy", 6 => "BufferStoreFormatXyz", 7 => "BufferStoreFormatXyzw",
                8 => "BufferLoadUbyte", 9 => "BufferLoadSbyte", 10 => "BufferLoadUshort", 11 => "BufferLoadSshort",
                12 => "BufferLoadDword", 13 => "BufferLoadDwordx2", 14 => "BufferLoadDwordx4", 15 => "BufferLoadDwordx3",
                24 => "BufferStoreByte", 26 => "BufferStoreShort", 28 => "BufferStoreDword",
                29 => "BufferStoreDwordx2", 30 => "BufferStoreDwordx4", 31 => "BufferStoreDwordx3",
                >= 0x30 and <= 0x3D and not 0x34 and var atomic => Gen5ShaderTranslator.MubufOpcodeName(atomic),
                _ => string.Empty,
            };
            if ((word & (1u << 15)) != 0 &&
                ((word & 0x13000u) != 0 || ((word >> 18) & 127) is not (>= 8 and <= 15 or 24 or 26 or >= 28 and <= 31)))
                name = string.Empty;
        }
        else if (word >> 26 == 0x3A)
        {
            encoding = Gen5ShaderEncoding.Mtbuf;
            width = 2;
            name = ((word >> 16) & 7) switch
            {
                0 => "TBufferLoadFormatX", 1 => "TBufferLoadFormatXy",
                2 => "TBufferLoadFormatXyz", 3 => "TBufferLoadFormatXyzw",
                4 => "TBufferStoreFormatX", 5 => "TBufferStoreFormatXy",
                6 => "TBufferStoreFormatXyz", 7 => "TBufferStoreFormatXyzw",
                _ => string.Empty,
            };
            // GCN ADDR64 uses a separate addressing form from indexed buffers.
            if ((word & (1u << 15)) != 0) name = string.Empty;
        }
        else if (word >> 26 == 0x3C)
        {
            encoding = Gen5ShaderEncoding.Mimg;
            width = 2;
            var imageOpcode = (word >> 18) & 127;
            name = imageOpcode switch
            {
                0 => "ImageLoad", 1 => "ImageLoadMip", 8 => "ImageStore",
                0x0E or (>= 0x20 and <= 0x60) or (>= 0x68 and <= 0x6F) => Gen5ShaderTranslator.MimgOpcodeName(imageOpcode),
                _ => "",
            };
            if ((word & ((1u << 15) | (1u << 16) | (1u << 17))) != 0 ||
                ((word >> 8) & 15) == 0 ||
                IsSampler(name) && (word & (1u << 12)) != 0)
                name = "";
        }
        else if (word >> 26 == 0x3E)
        {
            encoding = Gen5ShaderEncoding.Exp;
            width = 2;
            name = "Exp";
        }
        if (hasLiteral) width++;
        if (name.Length == 0)
        {
            error = word >> 26 == 0x3C
                ? $"unsupported-gcn-instruction MIMG pc=0x{pc:X} word=0x{word:X8} op=0x{(word >> 18) & 127:X}; GCN image descriptor/tiling normalization is required"
                : $"unsupported-gcn-instruction pc=0x{pc:X} word=0x{word:X8} encoding=0x{word >> 26:X}; no Gen5 opcode fallback";
            return false;
        }
        if ((uint)(width * 4) > (uint)bytes.Length - pc)
        {
            error = $"truncated-gcn-instruction pc=0x{pc:X} dwords={width}";
            return false;
        }
        var words = new uint[width];
        for (var i = 0; i < width; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)pc + i * 4)..]);
        if (encoding is Gen5ShaderEncoding.Mubuf or Gen5ShaderEncoding.Mtbuf && (words[1] & (1u << 23)) != 0)
        {
            error = $"unsupported-gcn-buffer-tfe pc=0x{pc:X}";
            return false;
        }
        uint? literal = hasLiteral ? words[^1] : null;
        Gen5Operand Src(uint encoded) => Gen5Operand.Source(encoded, literal);
        Gen5Operand[] sources = [];
        Gen5Operand[] destinations = [];
        Gen5InstructionControl? control = null;
        var dst = (word >> 16) & 127;
        switch (encoding)
        {
            case Gen5ShaderEncoding.Sop1:
                sources = [Src(word & 255)];
                destinations = [Gen5Operand.Scalar(dst)];
                if (name is "SCmovB32" or "SCmovB64")
                {
                    name = name == "SCmovB32" ? "SCselectB32" : "SCselectB64";
                    sources = [sources[0], destinations[0]];
                    encoding = Gen5ShaderEncoding.Sop2;
                }
                break;
            case Gen5ShaderEncoding.Sop2:
            case Gen5ShaderEncoding.Sopc:
                sources = [Src(word & 255), Src((word >> 8) & 255)];
                destinations = encoding == Gen5ShaderEncoding.Sop2 ? [Gen5Operand.Scalar(dst)] : [];
                break;
            case Gen5ShaderEncoding.Sopk:
                sources = [new Gen5Operand(Gen5OperandKind.EncodedConstant, word & 65535)];
                destinations = [Gen5Operand.Scalar(dst)];
                break;
            case Gen5ShaderEncoding.Smrd:
                var count = 1u << (int)((word >> 22) & 7);
                if (literal > int.MaxValue / 4)
                {
                    error = $"unsupported-gcn-smrd-literal-offset pc=0x{pc:X} offset=0x{literal:X}";
                    return false;
                }
                var dynamicOffset = (word & 256) == 0 && literal is null ? word & 255 : (uint?)null;
                var scalarBase = ((word >> 9) & 63) * 2;
                sources = dynamicOffset is { } offset ? [Gen5Operand.Scalar(scalarBase), Gen5Operand.Scalar(offset)] : [Gen5Operand.Scalar(scalarBase)];
                destinations = Enumerable.Range((int)((word >> 15) & 127), (int)count).Select(index => Gen5Operand.Scalar((uint)index)).ToArray();
                var offsetBytes = literal is { } dwordOffset ? (int)dwordOffset * 4
                    : dynamicOffset.HasValue ? 0 : (int)(word & 255) * 4;
                control = new Gen5ScalarMemoryControl(count, offsetBytes, dynamicOffset);
                break;
            case Gen5ShaderEncoding.Vop1:
                sources = [Src(word & 511)];
                destinations = name == "VReadfirstlaneB32" ? [Gen5Operand.Scalar((word >> 17) & 127)] : [Gen5Operand.Vector((word >> 17) & 255)];
                break;
            case Gen5ShaderEncoding.Vop2:
            case Gen5ShaderEncoding.Vopc:
                sources = [Src(word & 511), Gen5Operand.Vector((word >> 9) & 255)];
                destinations = encoding == Gen5ShaderEncoding.Vop2 ? [Gen5Operand.Vector((word >> 17) & 255)] : [];
                if (name is "VReadlaneB32" or "VWritelaneB32")
                {
                    sources = [sources[0], Src((word >> 9) & 255)];
                    if (name == "VReadlaneB32") destinations = [Gen5Operand.Scalar((word >> 17) & 127)];
                }
                else if (name == "VMadMkF32") sources = [sources[0], new Gen5Operand(Gen5OperandKind.LiteralConstant, literal!.Value), sources[1]];
                else if (name == "VMadAkF32") sources = [.. sources, new Gen5Operand(Gen5OperandKind.LiteralConstant, literal!.Value)];
                break;
            case Gen5ShaderEncoding.Vintrp:
                sources = [Gen5Operand.Vector(word & 255)];
                destinations = [Gen5Operand.Vector((word >> 18) & 255)];
                control = new Gen5InterpolationControl((word >> 10) & 63, (word >> 8) & 3);
                break;
            case Gen5ShaderEncoding.Mubuf:
                var extra = words[1];
                var vaddr = extra & 255;
                var vdata = (extra >> 8) & 255;
                var resource = ((extra >> 16) & 31) * 4;
                var dwords = name == "BufferAtomicCmpswap" || name.EndsWith("x2", StringComparison.Ordinal) || name.EndsWith("Xy", StringComparison.Ordinal) ? 2u
                    : name.EndsWith("x3", StringComparison.Ordinal) || name.EndsWith("Xyz", StringComparison.Ordinal) ? 3u
                    : name.EndsWith("x4", StringComparison.Ordinal) || name.EndsWith("Xyzw", StringComparison.Ordinal) ? 4u : 1u;
                sources = [Gen5Operand.Vector(vaddr), Gen5Operand.Scalar(resource), Src(extra >> 24)];
                if ((word & (1u << 15)) != 0)
                {
                    if (vaddr == 255 || vdata + dwords > 256)
                    {
                        error = $"unsupported-gcn-addr64-registers pc=0x{pc:X} address=v{vaddr} data=v{vdata} count={dwords}";
                        return false;
                    }
                    sources = [.. sources, Gen5Operand.Vector(vaddr + 1)];
                }
                destinations = Enumerable.Range((int)vdata, (int)dwords).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
                control = new Gen5BufferMemoryControl(dwords, vaddr, vdata, resource, (int)(word & 4095),
                    ((word >> 13) & 1) != 0, ((word >> 12) & 1) != 0, ((word >> 14) & 1) != 0, ((extra >> 22) & 1) != 0);
                break;
            case Gen5ShaderEncoding.Mtbuf:
                var typedExtra = words[1];
                var typedAddress = typedExtra & 255;
                var typedData = (typedExtra >> 8) & 255;
                var typedResource = ((typedExtra >> 16) & 31) * 4;
                var typedComponents = ((word >> 16) & 3) + 1;
                sources = [Gen5Operand.Vector(typedAddress), Gen5Operand.Scalar(typedResource), Src(typedExtra >> 24)];
                destinations = Enumerable.Range((int)typedData, (int)typedComponents)
                    .Select(index => Gen5Operand.Vector((uint)index)).ToArray();
                control = new Gen5BufferMemoryControl(typedComponents, typedAddress, typedData, typedResource,
                    (int)(word & 4095), ((word >> 13) & 1) != 0, ((word >> 12) & 1) != 0,
                    ((word >> 14) & 1) != 0, ((typedExtra >> 22) & 1) != 0,
                    Typed: true, TypedFormat: (((word >> 19) & 15) << 3) | ((word >> 23) & 7));
                break;
            case Gen5ShaderEncoding.Mimg:
                var imageExtra = words[1];
                var imageAddress = imageExtra & 255;
                var imageData = (imageExtra >> 8) & 255;
                var imageResource = ((imageExtra >> 16) & 31) * 4;
                var imageArray = ((word >> 14) & 1) != 0;
                if (name is not ("ImageLoad" or "ImageLoadMip" or "ImageStore"))
                {
                    var sampler = ((imageExtra >> 21) & 31) * 4;
                    var coordinates = Enumerable.Range((int)imageAddress, ImageAddressCount(name, imageArray)).Select(index => (uint)index).ToArray();
                    sources = [.. coordinates.Select(Gen5Operand.Vector), Gen5Operand.Scalar(imageResource), Gen5Operand.Scalar(sampler)];
                    destinations = [Gen5Operand.Vector(imageData)];
                    control = new Gen5ImageControl((word >> 8) & 15, imageAddress, coordinates, imageData,
                        imageResource, sampler, imageArray ? 3u : 1u, imageArray,
                        (word & (1u << 13)) != 0, (word & (1u << 25)) != 0, false, false);
                }
                else
                {
                    sources = [Gen5Operand.Vector(imageAddress), Gen5Operand.Scalar(imageResource)];
                    destinations = name is "ImageLoad" or "ImageLoadMip" ? [Gen5Operand.Vector(imageData)] : [];
                    control = new GcnStorageImageControl(imageAddress, imageData, imageResource, imageArray, (word >> 8) & 15);
                }
                break;
            case Gen5ShaderEncoding.Exp:
                sources = Enumerable.Range(0, 4).Select(index => Gen5Operand.Vector((words[1] >> (index * 8)) & 255)).ToArray();
                control = new Gen5ExportControl((word >> 4) & 63, word & 15,
                    (word & 1024) != 0, (word & 2048) != 0, (word & 4096) != 0);
                break;
        }
        instruction = new Gen5ShaderInstruction(pc, encoding, name, words, sources, destinations, control);
        error = string.Empty;
        return true;
    }

    private static bool TryDecodeDataShare(ReadOnlySpan<byte> bytes, uint pc, uint word,
        out Gen5ShaderInstruction instruction, out string error)
    {
        instruction = default!;
        if (8 > (uint)bytes.Length - pc)
        {
            error = $"truncated-gcn-instruction pc=0x{pc:X} dwords=2";
            return false;
        }
        var extra = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)pc + 4)..]);
        if (Gen5ShaderTranslator.TryCreateDataShareInstruction(pc, word, extra, out instruction))
        {
            error = string.Empty;
            return true;
        }
        error = $"unsupported-gcn-instruction pc=0x{pc:X} word=0x{word:X8} encoding=0x36; no Gen5 opcode fallback";
        return false;
    }

    private const uint VolumeDimension = 2;
    private const int DescriptorTypeWord = 3;

    internal static Gen5ShaderProgram WithVolumeImages(Gen5ShaderProgram program, ICpuMemory memory, ReadOnlySpan<uint> userData)
    {
        var instructions = program.Instructions.ToArray();
        var changed = false;
        for (var index = 0; index < instructions.Length; index++)
        {
            var instruction = instructions[index];
            if (instruction.Control is not Gen5ImageControl { IsArray: false, Dimension: 1 } image ||
                !instruction.Opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
                ResourceWord(instructions, index, image.ScalarResource + DescriptorTypeWord, memory, userData) is not { } word ||
                word >> 28 != GcnImageDescriptor.VolumeImageType)
                continue;
            var coordinates = Enumerable.Range((int)image.VectorAddress, ImageAddressCount(instruction.Opcode, true))
                .Select(register => (uint)register).ToArray();
            instructions[index] = instruction with
            {
                Sources = [.. coordinates.Select(Gen5Operand.Vector), Gen5Operand.Scalar(image.ScalarResource), Gen5Operand.Scalar(image.ScalarSampler)],
                Control = image with { AddressRegisters = coordinates, Dimension = VolumeDimension },
            };
            changed = true;
        }
        return changed ? program with { Instructions = instructions } : program;
    }

    private static uint? ResourceWord(Gen5ShaderInstruction[] instructions, int index, uint register,
        ICpuMemory memory, ReadOnlySpan<uint> userData)
    {
        var writer = LastScalarWriter(instructions, index, register);
        if (writer < 0)
            return register < userData.Length ? userData[(int)register] : null;
        var load = instructions[writer];
        if (!load.Opcode.StartsWith("SLoadDword", StringComparison.Ordinal) ||
            load.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: null } control ||
            load.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } pointer] ||
            LastScalarWriter(instructions, writer, pointer.Value) >= 0 ||
            LastScalarWriter(instructions, writer, pointer.Value + 1) >= 0 ||
            pointer.Value + 1 >= userData.Length)
            return null;
        var address = (userData[(int)pointer.Value] | ((ulong)(userData[(int)pointer.Value + 1] & 0xFFFF) << 32)) +
            (ulong)control.ImmediateOffsetBytes + (register - load.Destinations[0].Value) * 4UL;
        Span<byte> bytes = stackalloc byte[4];
        return memory.TryRead(address, bytes) ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
    }

    private static int LastScalarWriter(Gen5ShaderInstruction[] instructions, int index, uint register)
    {
        for (var cursor = index - 1; cursor >= 0; cursor--)
            if (instructions[cursor].Destinations.Any(destination =>
                    destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value <= register &&
                    register < destination.Value + Math.Max(1u, (uint)instructions[cursor].DestinationWidth)))
                return cursor;
        return -1;
    }

    private static bool IsSampler(string name) =>
        name.StartsWith("ImageSample", StringComparison.Ordinal) ||
        name.StartsWith("ImageGather", StringComparison.Ordinal) ||
        name == "ImageGetLod";

    private static int ImageAddressCount(string name, bool array)
    {
        if (name == "ImageGetResinfo")
            return 1;
        var start = name.StartsWith("ImageGather4", StringComparison.Ordinal) ? "ImageGather4".Length
            : name.StartsWith("ImageSample", StringComparison.Ordinal) ? "ImageSample".Length : name.Length;
        var count = array ? 3 : 2;
        for (var index = start; index < name.Length; index++)
        {
            if (!char.IsUpper(name[index]))
                continue;
            char next = index + 1 < name.Length ? name[index + 1] : default;
            count += name[index] switch
            {
                'D' => 4,
                'C' when next == 'd' => 4,
                'L' when next == 'z' => 0,
                _ => 1,
            };
        }
        return count;
    }

    private static string ScalarOpcode(Gen5ShaderEncoding encoding, uint word) => encoding switch
    {
        Gen5ShaderEncoding.Sop1 => ((word >> 8) & 255) switch
        {
            3 => "SMovB32", 4 => "SMovB64", 5 => "SCmovB32", 6 => "SCmovB64", 7 => "SNotB32", 8 => "SNotB64",
            9 => "SWqmB32", 10 => "SWqmB64", 11 => "SBrevB32", 15 => "SBcnt1I32B32", 16 => "SBcnt1I32B64",
            19 => "SFF1I32B32", 20 => "SFF1I32B64", 21 => "SFlbitI32B32", 22 => "SFlbitI32B64",
            31 => "SGetpcB64", 32 => "SSetpcB64", 33 => "SSwappcB64", 36 => "SAndSaveexecB64", 37 => "SOrSaveexecB64", 38 => "SXorSaveexecB64",
            39 => "SAndn2SaveexecB64", 40 => "SOrn2SaveexecB64", 41 => "SNandSaveexecB64",
            42 => "SNorSaveexecB64", 43 => "SXnorSaveexecB64", 52 => "SAbsI32", _ => "",
        },
        Gen5ShaderEncoding.Sop2 => ((word >> 23) & 127) switch
        {
            0 => "SAddU32", 1 => "SSubU32", 2 => "SAddI32", 3 => "SSubI32", 4 => "SAddcU32", 5 => "SSubbU32",
            6 => "SMinI32", 7 => "SMinU32", 8 => "SMaxI32", 9 => "SMaxU32", 10 => "SCselectB32", 11 => "SCselectB64",
            14 => "SAndB32", 15 => "SAndB64", 16 => "SOrB32", 17 => "SOrB64", 18 => "SXorB32", 19 => "SXorB64",
            20 => "SAndn2B32", 21 => "SAndn2B64", 22 => "SOrn2B32", 23 => "SOrn2B64", 24 => "SNandB32", 25 => "SNandB64",
            26 => "SNorB32", 27 => "SNorB64", 28 => "SXnorB32", 29 => "SXnorB64", 30 => "SLshlB32", 31 => "SLshlB64",
            32 => "SLshrB32", 33 => "SLshrB64", 34 => "SAshrI32", 35 => "SAshrI64", 36 => "SBfmB32", 37 => "SBfmB64",
            38 => "SMulI32", 39 => "SBfeU32", 40 => "SBfeI32", 41 => "SBfeU64", 42 => "SBfeI64", _ => "",
        },
        Gen5ShaderEncoding.Sopc => ((word >> 16) & 127) switch
        {
            0 => "SCmpEqI32", 1 => "SCmpLgI32", 2 => "SCmpGtI32", 3 => "SCmpGeI32", 4 => "SCmpLtI32", 5 => "SCmpLeI32",
            6 => "SCmpEqU32", 7 => "SCmpLgU32", 8 => "SCmpGtU32", 9 => "SCmpGeU32", 10 => "SCmpLtU32", 11 => "SCmpLeU32",
            12 => "SBitcmp0B32", 13 => "SBitcmp1B32", 14 => "SBitcmp0B64", 15 => "SBitcmp1B64", _ => "",
        },
        Gen5ShaderEncoding.Sopk => (((word >> 23) & 127) - 96) switch
        {
            0 => "SMovkI32", 3 => "SCmpkEqI32", 4 => "SCmpkLgI32", 5 => "SCmpkGtI32", 6 => "SCmpkGeI32",
            7 => "SCmpkLtI32", 8 => "SCmpkLeI32", 9 => "SCmpkEqU32", 10 => "SCmpkLgU32", 11 => "SCmpkGtU32",
            12 => "SCmpkGeU32", 13 => "SCmpkLtU32", 14 => "SCmpkLeU32", 15 => "SAddkI32", 16 => "SMulkI32", _ => "",
        },
        _ => ((word >> 16) & 127) switch
        {
            0 => "SNop", 1 => "SEndpgm", 2 => "SBranch", 4 => "SCbranchScc0", 5 => "SCbranchScc1",
            6 => "SCbranchVccz", 7 => "SCbranchVccnz", 8 => "SCbranchExecz", 9 => "SCbranchExecnz",
            10 => "SBarrier", 12 => "SWaitcnt", 15 => "SSetprio", _ => "",
        },
    };

    private static string VectorOpcode(Gen5ShaderEncoding encoding, uint opcode) => encoding switch
    {
        Gen5ShaderEncoding.Vop1 => opcode switch
        {
            0 => "VNop", 1 => "VMovB32", 2 => "VReadfirstlaneB32", 3 => "VCvtI32F64", 4 => "VCvtF64I32",
            5 => "VCvtF32I32", 6 => "VCvtF32U32", 7 => "VCvtU32F32", 8 => "VCvtI32F32", 10 => "VCvtF16F32", 11 => "VCvtF32F16",
            12 => "VCvtRpiI32F32", 13 => "VCvtFlrI32F32", 14 => "VCvtOffF32I4", 15 => "VCvtF32F64", 16 => "VCvtF64F32",
            17 => "VCvtF32Ubyte0", 18 => "VCvtF32Ubyte1", 19 => "VCvtF32Ubyte2", 20 => "VCvtF32Ubyte3",
            21 => "VCvtU32F64", 22 => "VCvtF64U32", 23 => "VTruncF64", 24 => "VCeilF64", 25 => "VRndneF64", 26 => "VFloorF64",
            32 => "VFractF32", 33 => "VTruncF32", 34 => "VCeilF32", 35 => "VRndneF32", 36 => "VFloorF32",
            37 => "VExpF32", 38 => GcnAluLowering.Prefix + "LogClampF32", 39 => "VLogF32",
            40 => GcnAluLowering.Prefix + "RcpClampF32", 41 => GcnAluLowering.Prefix + "RcpLegacyF32",
            42 => "VRcpF32", 43 => "VRcpIflagF32", 44 => GcnAluLowering.Prefix + "RsqClampF32",
            45 => GcnAluLowering.Prefix + "RsqLegacyF32", 46 => "VRsqF32", 47 => "VRcpF64", 49 => "VRsqF64",
            51 => "VSqrtF32", 52 => "VSqrtF64", 53 => "VSinF32", 54 => "VCosF32", 55 => "VNotB32", 56 => "VBfrevB32",
            57 => "VFfbhU32", 58 => "VFfblB32", 59 => "VFfbhI32", 62 => "VFractF64", 65 => "VNop",
            66 => "VMovreldB32", 67 => "VMovrelsB32", 68 => "VMovrelsdB32", 69 => "VLogF32", 70 => "VExpF32", _ => "",
        },
        Gen5ShaderEncoding.Vop2 => opcode switch
        {
            0 => "VCndmaskB32", 1 => "VReadlaneB32", 2 => "VWritelaneB32", 3 => "VAddF32", 4 => "VSubF32", 5 => "VSubrevF32",
            6 => GcnAluLowering.Prefix + "MacLegacyF32", 7 => GcnAluLowering.Prefix + "MulLegacyF32",
            8 => "VMulF32", 9 => "VMulI32I24", 10 => GcnAluLowering.Prefix + "MulHiI32I24", 11 => "VMulU32U24", 12 => "VMulHiU32U24",
            13 => GcnAluLowering.Prefix + "MinLegacyF32", 14 => GcnAluLowering.Prefix + "MaxLegacyF32",
            15 => "VMinF32", 16 => "VMaxF32", 17 => "VMinI32", 18 => "VMaxI32", 19 => "VMinU32", 20 => "VMaxU32",
            21 => "VLshrB32", 22 => "VLshrrevB32", 23 => "VAshrI32", 24 => "VAshrrevI32", 25 => "VLshlB32", 26 => "VLshlrevB32",
            27 => "VAndB32", 28 => "VOrB32", 29 => "VXorB32", 30 => "VBfmB32", 31 => "VMacF32", 32 => "VMadMkF32", 33 => "VMadAkF32",
            34 => "VBcntU32B32", 35 => "VMbcntLoU32B32", 36 => "VMbcntHiU32B32", 37 => "VAddI32", 38 => "VSubI32", 39 => "VSubrevI32",
            40 => "VAddcU32", 41 => "VSubbU32", 42 => "VSubbrevU32", 43 => "VLdexpF32",
            45 => "VCvtPknormI16F32", 46 => "VCvtPknormU16F32", 47 => "VCvtPkrtzF16F32", 48 => "VCvtPkU16U32", 49 => "VCvtPkI16I32",
            _ => "",
        },
        _ => CompareOpcode(opcode),
    };

    private static readonly string[] FloatConditions =
        ["F", "Lt", "Eq", "Le", "Gt", "Lg", "Ge", "O", "U", "Nge", "Nlg", "Ngt", "Nle", "Neq", "Nlt", "Tru"];
    private static readonly string[] IntegerConditions = ["F", "Lt", "Eq", "Le", "Gt", "Ne", "Ge", "T"];
    private static readonly string[] IntegerTypes = ["I32", "I64", "U32", "U64"];

    private static string CompareOpcode(uint opcode)
    {
        if (opcode > 0xFF) return "";
        var prefix = (opcode & 16) != 0 ? GcnAluLowering.CompareExec : "VCmp";
        if (opcode < 0x80)
            return prefix + FloatConditions[opcode & 15] + ((opcode & 32) != 0 ? "F64" : "F32");
        var type = IntegerTypes[(opcode >> 5) & 3];
        var condition = opcode & 15;
        if (condition < 8) return prefix + IntegerConditions[condition] + type;
        return condition == 8 && type == "I32" ? prefix + "ClassF32" : "";
    }
}
