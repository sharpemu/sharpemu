// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.ShaderCompiler;

public static class Gen5ShaderTranslator
{
    private static int _dppVectorsValidated;
    /// <summary>
    /// Bitmask (256 bits) of scalar registers whose values the program can
    /// observe: scalar source operands (widened for 64-bit pairs), the
    /// descriptor/sampler/address ranges named by instruction controls, and
    /// the implicit state registers. Deterministic per instruction stream, so
    /// the SPIR-V that loads exactly these registers from the per-draw
    /// initial-state buffer is byte-stable across draws.
    /// </summary>
    public static ulong[] ComputeConsumedScalarMask(Gen5ShaderProgram program)
    {
        var mask = new ulong[4];
        AddConsumedScalar(mask, 106, 2);
        AddConsumedScalar(mask, 124, 2);
        AddConsumedScalar(mask, 126, 2);
        foreach (var instruction in program.Instructions)
        {
            foreach (var source in instruction.Sources)
            {
                if (source.Kind == Gen5OperandKind.ScalarRegister)
                {
                    AddConsumedScalar(mask, source.Value, 2);
                }
            }

            // Scalar memory bases can be a 4-dword buffer descriptor.
            if (instruction.Encoding is Gen5ShaderEncoding.Smem or Gen5ShaderEncoding.Smrd &&
                instruction.Sources.Count > 0 &&
                instruction.Sources[0].Kind == Gen5OperandKind.ScalarRegister)
            {
                AddConsumedScalar(mask, instruction.Sources[0].Value, 4);
            }

            switch (instruction.Control)
            {
                case Gen5ImageControl image:
                    AddConsumedScalar(mask, image.ScalarResource, 8);
                    AddConsumedScalar(mask, image.ScalarSampler, 4);
                    break;
                case Gen5ScalarMemoryControl { DynamicOffsetRegister: { } offsetRegister }:
                    AddConsumedScalar(mask, offsetRegister, 2);
                    break;
                case Gen5GlobalMemoryControl global:
                    AddConsumedScalar(mask, global.ScalarAddress, 2);
                    break;
                case Gen5BufferMemoryControl buffer:
                    AddConsumedScalar(mask, buffer.ScalarResource, 4);
                    break;
            }
        }

        return mask;
    }

    private static void AddConsumedScalar(ulong[] mask, uint register, uint count)
    {
        for (uint index = 0; index < count; index++)
        {
            var target = register + index;
            if (target < 256)
            {
                mask[target >> 6] |= 1UL << (int)(target & 63);
            }
        }
    }

    public static bool IsScalarConsumed(ulong[] mask, uint register) =>
        register < 256 && (mask[register >> 6] & (1UL << (int)(register & 63))) != 0;

    private const int MaxInstructions = 16384;
    private const uint PsUserDataRegister = 0x0C;
    private const uint VsUserDataRegister = 0x4C;
    private const uint GsUserDataRegister = 0x8C;
    private const uint EsUserDataRegister = 0xCC;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint ComputePgmRsrc2Register = 0x213;
    private const int MaximumHardwareUserSgprs = 64;
    private static readonly ConditionalWeakTable<object, ShaderDecodeCache> _decodeCaches = new();

    private sealed class ShaderDecodeCache
    {
        public object Gate { get; } = new();
        public Dictionary<ulong, Gen5ShaderProgram> Programs { get; } = new();
        public Dictionary<ulong, Gen5ShaderMetadata?> Metadata { get; } = new();
    }

    private static readonly uint[] FullscreenBarycentricEs =
    [
        0xBFA00001, 0x7E000000, 0x7E000000, 0x7E000000,
        0x93EBFF03, 0x00080008, 0x8F6A8C6B, 0x8700FF03,
        0x000000FF, 0x887C6A00, 0xBF900009, 0x81EA6BC0,
        0x90FE6AC1, 0xF8000941, 0x00000000, 0x81EA00C0,
        0xBF8CFF0F, 0x90FE6AC1, 0x36040A81, 0x2C060A81,
        0x7E000280, 0x7E0202F2, 0xD7460002, 0x03050302,
        0xD7460003, 0x03050303, 0x7E040B02, 0x7E060B03,
        0xF80008CF, 0x01000302, 0xBF810000,
    ];

    private static readonly uint[] FullscreenBarycentricPs =
    [
        0xD52F0000, 0x00000200,
        0xD52F0001, 0x00000602,
        0xF8001C0F, 0x00000100,
        0xBF810000,
    ];

    private static readonly uint[] Gen5RectListExportEs =
    [
        0xBFA00001, 0x7E000000, 0x7E000000, 0x7E000000,
        0x9380FF03, 0x00080008, 0x8F6A8C00, 0x876BFF03,
        0x000000FF, 0x887C6A6B, 0xBF900009, 0x81EA6BC0,
        0x90FE6AC1, 0x36060A81, 0x2C080A81, 0x7E020280,
        0x7E0402F2, 0xD7460003, 0x03050303, 0xD7460004,
        0x03050304, 0x7E060B03, 0x7E080B04, 0xF80008CF,
        0x02010403, 0x81EA00C0, 0xBF8CFF0F, 0x90FE6AC1,
        0xF8000941, 0x00000000, 0xBF810000,
    ];

    private static readonly uint[] Gen5QuadExportEs =
    [
        0xBEFC03FF, 0x61937B18, 0xBF960000, 0xBFA00002,
        0x93EBFF03, 0x00080008, 0x8F6A8C6B, 0x8700FF03,
        0x000000FF, 0x887C6A00, 0xBF900009, 0x81EA6BC0,
        0x90FE6AC1, 0xF8000941, 0x00000000, 0x81EA00C0,
        0xBF8CFF0F, 0x90FE6AC1, 0x2C080A81, 0x36040A81,
        0x7E0002F2, 0x7E020280, 0xD5690005, 0x000208C2,
        0xD7460003, 0x03050302, 0x7E040B02, 0x7E080B04,
        0x4A0A0A81, 0x7E060B03, 0x7E0A0B05, 0xF80008CF,
        0x00000503, 0xF800020F, 0x01010402, 0xBF810000,
    ];

    public static bool TryTranslate(
        CpuContext ctx,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        uint psInputEna,
        uint psInputAddr,
        out GuestDrawKind drawKind)
    {
        drawKind = GuestDrawKind.None;
        if (exportShaderAddress == 0 ||
            pixelShaderAddress == 0 ||
            psInputEna != 0x00000002 ||
            psInputAddr != 0x00000002 ||
            !MatchesProgram(ctx, exportShaderAddress, FullscreenBarycentricEs) ||
            !MatchesProgram(ctx, pixelShaderAddress, FullscreenBarycentricPs))
        {
            return false;
        }

        drawKind = GuestDrawKind.FullscreenBarycentric;
        return true;
    }

    public static bool IsFullscreenExportShader(CpuContext ctx, ulong exportShaderAddress) =>
        exportShaderAddress != 0 &&
        (MatchesProgram(ctx, exportShaderAddress, FullscreenBarycentricEs) ||
         MatchesProgram(ctx, exportShaderAddress, Gen5RectListExportEs) ||
         MatchesProgram(ctx, exportShaderAddress, Gen5QuadExportEs));

    public static string Describe(CpuContext ctx, ulong exportShaderAddress, ulong pixelShaderAddress)
    {
        var es = TryDecodeProgram(ctx, exportShaderAddress, out var esProgram, out var esError)
            ? ShaderDecodeInfo.Create(esProgram).ToString()
            : $"error={esError}";
        var ps = TryDecodeProgram(ctx, pixelShaderAddress, out var psProgram, out var psError)
            ? ShaderDecodeInfo.Create(psProgram).ToString()
            : $"error={psError}";
        return $"es[{es}] ps[{ps}]";
    }

    public static string DescribeWords(CpuContext ctx, ulong shaderAddress) =>
        TryDecodeProgram(ctx, shaderAddress, out var program, out var error)
            ? string.Join(',', program.Instructions.SelectMany(instruction => instruction.Words)
                .Select(word => $"{word:X8}"))
            : $"error={error}";

    public static bool TryCreateState(
        CpuContext ctx,
        ulong shaderAddress,
        ulong shaderHeaderAddress,
        IReadOnlyDictionary<uint, uint> shaderRegisters,
        uint userDataBaseRegister,
        out Gen5ShaderState state,
        out string error,
        Gen5ComputeSystemRegisters? computeSystemRegisters = null,
        uint userDataScalarRegisterBase = 0)
    {
        ValidateUserSgprCountDecoding();
        state = default!;
        error = string.Empty;
        var cache = _decodeCaches.GetValue(ctx.Memory, static _ => new ShaderDecodeCache());
        Gen5ShaderProgram? program;
        lock (cache.Gate)
        {
            cache.Programs.TryGetValue(shaderAddress, out program);
        }

        if (program is null)
        {
            if (!TryDecodeProgram(ctx, shaderAddress, out program, out error))
            {
                return false;
            }

            lock (cache.Gate)
            {
                cache.Programs.TryAdd(shaderAddress, program);
            }
        }

        Gen5ShaderMetadata? metadata = null;
        if (shaderHeaderAddress != 0)
        {
            var metadataCached = false;
            lock (cache.Gate)
            {
                metadataCached = cache.Metadata.TryGetValue(shaderHeaderAddress, out metadata);
            }

            if (!metadataCached)
            {
                if (Gen5ShaderMetadataReader.TryRead(
                        ctx,
                        shaderHeaderAddress,
                        out var decodedMetadata))
                {
                    metadata = decodedMetadata;
                }

                lock (cache.Gate)
                {
                    cache.Metadata.TryAdd(shaderHeaderAddress, metadata);
                }
            }
        }

        if (!TryGetUserSgprCount(
                shaderRegisters,
                userDataBaseRegister,
                out var userSgprCount,
                out var rsrc2Register,
                out _))
        {
            error =
                $"missing-user-sgpr-count ud_reg=0x{userDataBaseRegister:X} " +
                $"rsrc2_reg=0x{rsrc2Register:X}";
            return false;
        }

        var userData = new uint[userSgprCount];
        for (uint index = 0; index < userData.Length; index++)
        {
            shaderRegisters.TryGetValue(userDataBaseRegister + index, out userData[index]);
        }

        state = new Gen5ShaderState(
            program,
            userData,
            metadata,
            computeSystemRegisters,
            userDataScalarRegisterBase);
        return true;
    }

    private static bool TryGetUserSgprCount(
        IReadOnlyDictionary<uint, uint> shaderRegisters,
        uint userDataBaseRegister,
        out int count,
        out uint rsrc2Register,
        out uint rsrc2)
    {
        rsrc2Register = userDataBaseRegister == ComputeUserDataRegister
            ? ComputePgmRsrc2Register
            : userDataBaseRegister - 1;
        if (!shaderRegisters.TryGetValue(rsrc2Register, out rsrc2))
        {
            count = 0;
            return false;
        }

        count = checked((int)((rsrc2 >> 1) & 0x1Fu));
        // GFX10 PS/VS/GS expose a sixth USER_SGPR bit. ES and compute do not.
        // AGC's logical user-data layout (including its SRT pointer and back
        // user data) describes memory reached through these SGPRs; it does not
        // increase the hardware register window.
        var hasUserSgprMsb = userDataBaseRegister is
            PsUserDataRegister or VsUserDataRegister or GsUserDataRegister;
        if (hasUserSgprMsb &&
            (rsrc2 & (1u << 27)) != 0)
        {
            count |= 0x20;
        }

        // Primary SH defaults leave SPI_SHADER_PGM_RSRC2_PS at 0. Draws that
        // still wrote USER_DATA_n via SetShReg would otherwise translate with
        // an empty SRT window (Astro title PS → Address-0 descriptors →
        // device lost). Recover the window from contiguous live registers.
        if (count == 0 &&
            userDataBaseRegister is not ComputeUserDataRegister)
        {
            var probed = 0;
            while (probed < MaximumHardwareUserSgprs &&
                   shaderRegisters.ContainsKey(userDataBaseRegister + (uint)probed))
            {
                probed++;
            }

            count = probed;
        }

        if (userDataBaseRegister is not (PsUserDataRegister or
                VsUserDataRegister or
                GsUserDataRegister or
                EsUserDataRegister or
                ComputeUserDataRegister) ||
            count > MaximumHardwareUserSgprs)
        {
            count = 0;
            return false;
        }

        return true;
    }

    [Conditional("DEBUG")]
    private static void ValidateUserSgprCountDecoding()
    {
        static int Decode(uint baseRegister, uint rsrc2)
        {
            var registers = new Dictionary<uint, uint>
            {
                [baseRegister == ComputeUserDataRegister
                    ? ComputePgmRsrc2Register
                    : baseRegister - 1] = rsrc2,
            };
            var decoded =
                TryGetUserSgprCount(registers, baseRegister, out var count, out _, out _);
            Debug.Assert(decoded);
            return count;
        }

        Debug.Assert(Decode(PsUserDataRegister, 2u << 1) == 2);
        Debug.Assert(Decode(PsUserDataRegister, (3u << 1) | (1u << 27)) == 35);
        Debug.Assert(Decode(VsUserDataRegister, (1u << 1) | (1u << 27)) == 33);
        Debug.Assert(Decode(GsUserDataRegister, (4u << 1) | (1u << 27)) == 36);
        Debug.Assert(Decode(EsUserDataRegister, (7u << 1) | (1u << 27)) == 7);
        Debug.Assert(Decode(ComputeUserDataRegister, (11u << 1) | (1u << 27)) == 11);
    }

    public static string DescribeState(Gen5ShaderState state)
    {
        var userData = string.Join(
            ',',
            state.UserData.Select((value, index) => $"s{index}=0x{value:X8}"));
        var systemRegisters = state.ComputeSystemRegisters is { } compute
            ? $" compute[{DescribeComputeSystemRegisters(compute)}]"
            : string.Empty;
        if (state.Metadata is not { } metadata)
        {
            return
                $"ud_base=s{state.UserDataScalarRegisterBase} hw_ud={state.UserData.Count} " +
                $"ud[{userData}]" +
                $"{systemRegisters} metadata=missing";
        }

        var direct = string.Join(
            ',',
            metadata.DirectResources.Select(resource => $"{resource.Key}:{resource.Value}"));
        var resources = string.Join(
            ',',
            metadata.Resources.Select(resource =>
                $"{resource.Kind}[{resource.Slot}]@{resource.OffsetDwords}" +
                (resource.SizeFlag ? "+" : string.Empty)));
        return
            $"ud_base=s{state.UserDataScalarRegisterBase} hw_ud={state.UserData.Count} " +
            $"ud[{userData}]" +
            $"{systemRegisters} metadata[eud={metadata.ExtendedUserDataSizeDwords}," +
            $"srt={metadata.ShaderResourceTableSizeDwords},direct={direct},resources={resources}]";
    }

    private static string DescribeComputeSystemRegisters(Gen5ComputeSystemRegisters registers) =>
        $"x={DescribeRegister(registers.WorkGroupXRegister)}," +
        $"y={DescribeRegister(registers.WorkGroupYRegister)}," +
        $"z={DescribeRegister(registers.WorkGroupZRegister)}," +
        $"size={DescribeRegister(registers.ThreadGroupSizeRegister)}";

    private static string DescribeRegister(uint? register) =>
        register.HasValue ? $"s{register.Value}" : "-";

    private static bool MatchesProgram(CpuContext ctx, ulong address, ReadOnlySpan<uint> expected)
    {
        var bytes = new byte[expected.Length * sizeof(uint)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint))) != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    // Public contract entry: emitter test suites and tools drive the decoder directly
    // from raw instruction words.
    public static bool TryDecodeProgram(
        CpuContext ctx,
        ulong address,
        out Gen5ShaderProgram program,
        out string error)
    {
        ValidateDppControlVectors();
        program = new Gen5ShaderProgram(address, []);
        error = string.Empty;
        if (address == 0)
        {
            error = "missing";
            return false;
        }

        var instructions = new List<Gen5ShaderInstruction>();
        var instructionCount = 0;
        for (uint pc = 0; instructionCount < MaxInstructions;)
        {
            if (!TryReadUInt32(ctx, address + pc, out var word))
            {
                error = $"read-failed pc=0x{pc:X}";
                return false;
            }

            if (!TryDecodeInstruction(
                    ctx,
                    address,
                    pc,
                    word,
                    out var encoding,
                    out var name,
                    out var sizeDwords,
                    out error))
            {
                return false;
            }

            var words = new uint[sizeDwords];
            words[0] = word;
            for (uint wordIndex = 1; wordIndex < sizeDwords; wordIndex++)
            {
                if (!TryReadUInt32(ctx, address + pc + wordIndex * sizeof(uint), out words[wordIndex]))
                {
                    error = $"read-failed pc=0x{pc + wordIndex * sizeof(uint):X}";
                    return false;
                }
            }

            instructions.Add(CreateInstruction(pc, encoding, name, words));
            instructionCount++;

            pc += sizeDwords * sizeof(uint);
            if (string.Equals(name, "SEndpgm", StringComparison.Ordinal))
            {
                program = new Gen5ShaderProgram(address, instructions);
                return true;
            }
        }

        error = "unterminated";
        return false;
    }

    [Conditional("DEBUG")]
    private static void ValidateDppControlVectors()
    {
        if (System.Threading.Interlocked.Exchange(ref _dppVectorsValidated, 1) != 0)
        {
            return;
        }

        static (uint Lane, bool InRange) Resolve(uint control, uint lane)
        {
            var rowBase = lane & ~15u;
            var rowLane = lane & 15u;
            return control switch
            {
                >= 0x101 and <= 0x10F => (
                    rowBase + ((rowLane + (control & 15)) & 15),
                    rowLane + (control & 15) < 16),
                >= 0x111 and <= 0x11F => (
                    rowBase + ((rowLane - (control & 15)) & 15),
                    rowLane >= (control & 15)),
                >= 0x121 and <= 0x12F => (
                    rowBase + ((rowLane - (control & 15)) & 15),
                    true),
                0x140 => (rowBase + 15 - rowLane, true),
                0x141 => ((lane & ~7u) + 7 - (lane & 7), true),
                >= 0x150 and <= 0x15F => (rowBase + (control & 15), true),
                >= 0x160 and <= 0x16F => (rowBase + (rowLane ^ (control & 15)), true),
                _ => (lane, false),
            };
        }

        Debug.Assert(Resolve(0x101, 0) == (1u, true));
        Debug.Assert(Resolve(0x101, 15) == (0u, false));
        Debug.Assert(Resolve(0x112, 1) == (15u, false));
        Debug.Assert(Resolve(0x123, 0) == (13u, true));
        Debug.Assert(Resolve(0x140, 18) == (29u, true));
        Debug.Assert(Resolve(0x141, 9) == (14u, true));
        Debug.Assert(Resolve(0x153, 20) == (19u, true));
        Debug.Assert(Resolve(0x163, 22) == (21u, true));
        const uint dpp8 =
            (7u << 0) | (6u << 3) | (5u << 6) | (4u << 9) |
            (3u << 12) | (2u << 15) | (1u << 18) | (0u << 21);
        Debug.Assert(((dpp8 >> (0 * 3)) & 7) == 7);
        Debug.Assert(((dpp8 >> (7 * 3)) & 7) == 0);
    }

    private static bool TryDecodeInstruction(
        CpuContext ctx,
        ulong baseAddress,
        uint pc,
        uint word,
        out Gen5ShaderEncoding encoding,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        encoding = Gen5ShaderEncoding.Vop2;
        name = string.Empty;
        sizeDwords = 1;
        error = string.Empty;

        if ((word & 0x80000000u) == 0)
        {
            var vopOpcode = (word >> 25) & 0x3F;
            encoding = vopOpcode switch
            {
                0x3E => Gen5ShaderEncoding.Vopc,
                0x3F => Gen5ShaderEncoding.Vop1,
                _ => Gen5ShaderEncoding.Vop2,
            };
            return DecodeVop2(word, out name, out sizeDwords, out error);
        }

        if ((word & 0xF8000000u) == 0xC0000000u)
        {
            encoding = Gen5ShaderEncoding.Smrd;
            return DecodeSmrd(word, out name, out sizeDwords, out error);
        }

        if ((word & 0xC0000000u) == 0x80000000u)
        {
            var sopOpcode = (word >> 23) & 0x7F;
            encoding = sopOpcode switch
            {
                0x7D => Gen5ShaderEncoding.Sop1,
                0x7E => Gen5ShaderEncoding.Sopc,
                0x7F => Gen5ShaderEncoding.Sopp,
                >= 0x60 => Gen5ShaderEncoding.Sopk,
                _ => Gen5ShaderEncoding.Sop2,
            };
            return DecodeSop(word, out name, out sizeDwords, out error);
        }

        // gfx10 moved VOP3P (packed 16-bit math) to its own 0b110011000 prefix
        // (word0 top byte 0xCC), separate from the VOP3 block. Match the full
        // 9-bit prefix here, before the coarse major-opcode switch, so packed
        // instructions are not misread as one of the neighbouring encodings.
        if ((word & 0xFF800000u) == 0xCC000000u)
        {
            encoding = Gen5ShaderEncoding.Vop3p;
            if (!ctx.TryReadUInt32(baseAddress + pc + sizeof(uint), out var vop3pExtra))
            {
                error = $"vop3p-extra-read-failed pc=0x{pc:X}";
                return false;
            }

            return DecodeVop3p(word, vop3pExtra, out name, out sizeDwords, out error);
        }

        switch (word >> 26)
        {
            case 0x33:
                encoding = Gen5ShaderEncoding.Smem;
                return DecodeSmem(word, out name, out sizeDwords, out error);
            case 0x32:
                encoding = Gen5ShaderEncoding.Vintrp;
                return DecodeVintrp(word, out name, out sizeDwords, out error);
            case 0x34:
            case 0x35:
                encoding = Gen5ShaderEncoding.Vop3;
                if (!TryReadUInt32(ctx, baseAddress + pc + sizeof(uint), out var vop3Extra))
                {
                    error = $"vop3-extra-read-failed pc=0x{pc:X}";
                    return false;
                }

                return DecodeVop3(
                    word,
                    vop3Extra,
                    IsVop3BOpcode((word >> 16) & 0x3FF),
                    out name,
                    out sizeDwords,
                    out error);
            case 0x36:
                encoding = Gen5ShaderEncoding.Ds;
                return DecodeDs(word, out name, out sizeDwords, out error);
            case 0x37:
                encoding = Gen5ShaderEncoding.Flat;
                return DecodeFlat(word, out name, out sizeDwords, out error);
            case 0x38:
                encoding = Gen5ShaderEncoding.Mubuf;
                if (!TryReadUInt32(ctx, baseAddress + pc + sizeof(uint), out var mubufExtra))
                {
                    error = $"mubuf-extra-read-failed pc=0x{pc:X}";
                    return false;
                }

                return DecodeMubuf(word, mubufExtra, out name, out sizeDwords, out error);
            case 0x3A:
                encoding = Gen5ShaderEncoding.Mtbuf;
                if (!TryReadUInt32(ctx, baseAddress + pc + sizeof(uint), out var mtbufExtra))
                {
                    error = $"mtbuf-extra-read-failed pc=0x{pc:X}";
                    return false;
                }

                return DecodeMtbuf(word, mtbufExtra, out name, out sizeDwords, out error);
            case 0x3C:
                encoding = Gen5ShaderEncoding.Mimg;
                return DecodeMimg(word, out name, out sizeDwords, out error);
            case 0x3D:
                encoding = Gen5ShaderEncoding.Smem;
                return DecodeSmem(word, out name, out sizeDwords, out error);
            case 0x3E:
                encoding = Gen5ShaderEncoding.Exp;
                name = "Exp";
                sizeDwords = 2;
                return true;
            case 0x3F:
                encoding = Gen5ShaderEncoding.Vop3p;
                return DecodeRaw2(word, "Vop3p", out name, out sizeDwords, out error);
            default:
                error = $"unknown-top pc=0x{pc:X} word=0x{word:X8}";
                return false;
        }
    }

    // Kept beside the production decoder so offline compatibility tools use
    // precisely the same opcode tables and instruction-width rules as runtime
    // shader translation.
    internal static bool TryDecodeInstructionForPreflight(
        CpuContext ctx,
        uint pc,
        uint word,
        out string name,
        out uint sizeDwords,
        out string error) =>
        TryDecodeInstruction(
            ctx,
            0,
            pc,
            word,
            out _,
            out name,
            out sizeDwords,
            out error);

    private static bool DecodeSop(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 23) & 0x7F;
        return opcode switch
        {
            0x7D => DecodeSop1(word, out name, out sizeDwords, out error),
            0x7E => DecodeSopc(word, out name, out sizeDwords, out error),
            0x7F => DecodeSopp(word, out name, out sizeDwords, out error),
            >= 0x60 => DecodeSopk(word, out name, out sizeDwords, out error),
            _ => DecodeSop2(word, out name, out sizeDwords, out error),
        };
    }

    private static bool DecodeSop1(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 8) & 0xFF;
        var src0 = word & 0xFF;
        sizeDwords = 1 + (src0 == 0xFF ? 1u : 0u);
        error = string.Empty;
        name = opcode switch
        {
            0x03 => "SMovB32",
            0x04 => "SMovB64",
            0x07 => "SNotB32",
            0x08 => "SNotB64",
            0x0A => "SWqmB64",
            0x0B => "SBrevB32",
            0x0F => "SBcnt1I32B32",
            0x13 => "SFF1I32B32",
            0x1D => "SBitset1B32",
            0x1F => "SGetpcB64",
            0x20 => "SSetpcB64",
            0x21 => "SSwappcB64",
            0x24 => "SAndSaveexecB64",
            0x25 => "SOrSaveexecB64",
            0x26 => "SXorSaveexecB64",
            0x27 => "SAndn2SaveexecB64",
            0x28 => "SOrn2SaveexecB64",
            0x29 => "SNandSaveexecB64",
            0x2A => "SNorSaveexecB64",
            0x2B => "SXnorSaveexecB64",
            0x37 => "SAndn1SaveexecB64",
            0x38 => "SOrn1SaveexecB64",
            0x3C => "SAndSaveexecB32",
            0x3D => "SOrSaveexecB32",
            0x3E => "SXorSaveexecB32",
            0x3F => "SAndn2SaveexecB32",
            0x40 => "SOrn2SaveexecB32",
            0x41 => "SNandSaveexecB32",
            0x42 => "SNorSaveexecB32",
            0x43 => "SXnorSaveexecB32",
            0x44 => "SAndn1SaveexecB32",
            0x45 => "SOrn1SaveexecB32",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-sop1 op=0x{opcode:X2} word=0x{word:X8}", out error);
    }

    private static bool DecodeSop2(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 23) & 0x7F;
        var src0 = word & 0xFF;
        var src1 = (word >> 8) & 0xFF;
        sizeDwords = src0 == 0xFF || src1 == 0xFF ? 2u : 1u;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "SAddU32",
            0x01 => "SSubU32",
            0x02 => "SAddI32",
            0x03 => "SSubI32",
            0x04 => "SAddcU32",
            0x05 => "SSubbU32",
            0x06 => "SMinI32",
            0x07 => "SMinU32",
            0x08 => "SMaxI32",
            0x09 => "SMaxU32",
            0x0A => "SCselectB32",
            0x0B => "SCselectB64",
            0x0E => "SAndB32",
            0x0F => "SAndB64",
            0x10 => "SOrB32",
            0x11 => "SOrB64",
            0x12 => "SXorB32",
            0x13 => "SXorB64",
            0x14 => "SAndn2B32",
            0x15 => "SAndn2B64",
            0x16 => "SOrn2B32",
            0x17 => "SOrn2B64",
            0x18 => "SNandB32",
            0x19 => "SNandB64",
            0x1A => "SNorB32",
            0x1B => "SNorB64",
            0x1C => "SXnorB32",
            0x1D => "SXnorB64",
            0x1E => "SLshlB32",
            0x1F => "SLshlB64",
            0x20 => "SLshrB32",
            0x21 => "SLshrB64",
            0x22 => "SAshrI32",
            0x23 => "SAshrI64",
            0x24 => "SBfmB32",
            0x25 => "SBfmB64",
            0x26 => "SMulI32",
            0x27 => "SBfeU32",
            0x28 => "SBfeI32",
            0x29 => "SBfeU64",
            0x2A => "SBfeI64",
            0x2D => "SAbsdiffI32",
            0x2E => "SLshl1AddU32",
            0x2F => "SLshl2AddU32",
            0x30 => "SLshl3AddU32",
            0x31 => "SLshl4AddU32",
            0x32 => "SPackLlB32B16",
            0x33 => "SPackLhB32B16",
            0x34 => "SPackHhB32B16",
            0x35 => "SMulHiU32",
            0x36 => "SMulHiI32",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-sop2 op=0x{opcode:X2}", out error);
    }

    private static bool DecodeSopc(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 16) & 0x7F;
        var src0 = word & 0xFF;
        var src1 = (word >> 8) & 0xFF;
        sizeDwords = src0 == 0xFF || src1 == 0xFF ? 2u : 1u;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "SCmpEqI32",
            0x01 => "SCmpLgI32",
            0x02 => "SCmpGtI32",
            0x03 => "SCmpGeI32",
            0x04 => "SCmpLtI32",
            0x05 => "SCmpLeI32",
            0x06 => "SCmpEqU32",
            0x07 => "SCmpLgU32",
            0x08 => "SCmpGtU32",
            0x09 => "SCmpGeU32",
            0x0A => "SCmpLtU32",
            0x0B => "SCmpLeU32",
            0x0C => "SBitcmp0B32",
            0x0D => "SBitcmp1B32",
            0x0E => "SBitcmp0B64",
            0x0F => "SBitcmp1B64",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-sopc op=0x{opcode:X2}", out error);
    }

    private static bool DecodeSopp(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 16) & 0x7F;
        sizeDwords = 1;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "SNop",
            0x01 => "SEndpgm",
            0x02 => "SBranch",
            0x04 => "SCbranchScc0",
            0x05 => "SCbranchScc1",
            0x06 => "SCbranchVccz",
            0x07 => "SCbranchVccnz",
            0x08 => "SCbranchExecz",
            0x09 => "SCbranchExecnz",
            0x0A => "SBarrier",
            0x0C => "SWaitcnt",
            0x10 => "SSendmsg",
            0x16 => "STtraceData",
            0x20 => "SInstPrefetch",
            0x21 => "SClause",
            0x23 => "SWaitcntDepctr",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-sopp op=0x{opcode:X2}", out error);
    }

    private static bool DecodeSopk(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = ((word >> 23) & 0x7F) - 0x60;
        sizeDwords = 1;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "SMovkI32",
            0x03 => "SCmpkEqI32",
            0x04 => "SCmpkLgI32",
            0x05 => "SCmpkGtI32",
            0x06 => "SCmpkGeI32",
            0x07 => "SCmpkLtI32",
            0x08 => "SCmpkLeI32",
            0x09 => "SCmpkEqU32",
            0x0A => "SCmpkLgU32",
            0x0B => "SCmpkGtU32",
            0x0C => "SCmpkGeU32",
            0x0D => "SCmpkLtU32",
            0x0E => "SCmpkLeU32",
            0x0F => "SAddkI32",
            0x10 => "SMulkI32",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-sopk op=0x{opcode:X2}", out error);
    }

    private static bool DecodeVop1(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 9) & 0xFF;
        var src0 = word & 0x1FF;
        sizeDwords = src0 is 0xE9 or 0xEA or 0xF9 or 0xFA or 0xFF ? 2u : 1u;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "VNop",
            0x01 => "VMovB32",
            0x02 => "VReadfirstlaneB32",
            0x05 => "VCvtF32I32",
            0x06 => "VCvtF32U32",
            0x07 => "VCvtU32F32",
            0x08 => "VCvtI32F32",
            0x0A => "VCvtF16F32",
            0x0B => "VCvtF32F16",
            0x0C => "VCvtRpiI32F32",
            0x0D => "VCvtFlrI32F32",
            0x0E => "VCvtOffF32I4",
            0x11 => "VCvtF32Ubyte0",
            0x12 => "VCvtF32Ubyte1",
            0x13 => "VCvtF32Ubyte2",
            0x14 => "VCvtF32Ubyte3",
            0x20 => "VFractF32",
            0x21 => "VTruncF32",
            0x22 => "VCeilF32",
            0x23 => "VRndneF32",
            0x24 => "VFloorF32",
            0x25 => "VExpF32",
            0x27 => "VLogF32",
            0x2A => "VRcpF32",
            0x2B => "VRcpIflagF32",
            0x2E => "VRsqF32",
            0x33 => "VSqrtF32",
            0x35 => "VSinF32",
            0x36 => "VCosF32",
            0x37 => "VNotB32",
            0x38 => "VBfrevB32",
            0x3A => "VFfblB32",
            0x42 => "VMovreldB32",
            0x43 => "VMovrelsB32",
            0x44 => "VMovrelsdB32",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-vop1 op=0x{opcode:X2}", out error);
    }

    private static bool DecodeVop2(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 25) & 0x3F;
        if (opcode == 0x3E)
        {
            return DecodeVopc(word, out name, out sizeDwords, out error);
        }

        if (opcode == 0x3F)
        {
            return DecodeVop1(word, out name, out sizeDwords, out error);
        }

        var src0 = word & 0x1FF;
        sizeDwords = opcode is 0x20 or 0x21 or 0x2C or 0x2D ||
            src0 is 0xE9 or 0xEA or 0xF9 or 0xFA or 0xFF ? 2u : 1u;
        error = string.Empty;
        name = opcode switch
        {
            0x01 => "VCndmaskB32",
            0x02 => "VDot2cF32F16",
            0x03 => "VAddF32",
            0x04 => "VSubF32",
            0x05 => "VSubrevF32",
            0x08 => "VMulF32",
            0x0B => "VMulU32U24",
            0x0C => "VMulHiU32U24",
            0x0F => "VMinF32",
            0x10 => "VMaxF32",
            0x11 => "VMinI32",
            0x12 => "VMaxI32",
            0x13 => "VMinU32",
            0x14 => "VMaxU32",
            0x15 => "VLshrB32",
            0x16 => "VLshrrevB32",
            0x17 => "VAshrI32",
            0x18 => "VAshrrevI32",
            0x19 => "VLshlB32",
            0x1A => "VLshlrevB32",
            0x1B => "VAndB32",
            0x1C => "VOrB32",
            0x1D => "VXorB32",
            0x1E => "VXnorB32",
            0x1F => "VMacF32",
            0x20 => "VMadMkF32",
            0x21 => "VMadAkF32",
            0x22 => "VBcntU32B32",
            0x23 => "VMbcntLoU32B32",
            0x24 => "VMbcntHiU32B32",
            0x25 => "VAddI32",
            0x26 => "VSubI32",
            0x27 => "VSubrevI32",
            0x28 => "VAddcU32",
            0x29 => "VSubbU32",
            0x2A => "VSubbrevU32",
            0x2B => "VFmacF32",
            0x2C => "VFmaMkF32",
            0x2D => "VFmaAkF32",
            0x2F => "VCvtPkrtzF16F32",
            0x30 => "VCvtPkU16U32",
            0x31 => "VCvtPkI16I32",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-vop2 op=0x{opcode:X2}", out error);
    }

    private static bool DecodeVopc(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 17) & 0xFF;
        var src0 = word & 0x1FF;
        sizeDwords = src0 is 0xE9 or 0xEA or 0xF9 or 0xFA or 0xFF ? 2u : 1u;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "VCmpFF32",
            0x01 => "VCmpLtF32",
            0x02 => "VCmpEqF32",
            0x03 => "VCmpLeF32",
            0x04 => "VCmpGtF32",
            0x05 => "VCmpLgF32",
            0x06 => "VCmpGeF32",
            0x07 => "VCmpOF32",
            0x08 => "VCmpUF32",
            0x09 => "VCmpNgeF32",
            0x0A => "VCmpNlgF32",
            0x0B => "VCmpNgtF32",
            0x0C => "VCmpNleF32",
            0x0D => "VCmpNeqF32",
            0x0E => "VCmpNltF32",
            0x0F => "VCmpTruF32",
            0x10 => "VCmpxFF32",
            0x11 => "VCmpxLtF32",
            0x12 => "VCmpxEqF32",
            0x13 => "VCmpxLeF32",
            0x14 => "VCmpxGtF32",
            0x15 => "VCmpxLgF32",
            0x16 => "VCmpxGeF32",
            0x17 => "VCmpxOF32",
            0x18 => "VCmpxUF32",
            0x19 => "VCmpxNgeF32",
            0x1A => "VCmpxNlgF32",
            0x1B => "VCmpxNgtF32",
            0x1C => "VCmpxNleF32",
            0x1D => "VCmpxNeqF32",
            0x1E => "VCmpxNltF32",
            0x1F => "VCmpxTruF32",
            0x80 => "VCmpFI32",
            0x81 => "VCmpLtI32",
            0x82 => "VCmpEqI32",
            0x83 => "VCmpLeI32",
            0x84 => "VCmpGtI32",
            0x85 => "VCmpNeI32",
            0x86 => "VCmpGeI32",
            0x87 => "VCmpTI32",
            0x88 => "VCmpClassF32",
            0x90 => "VCmpxFI32",
            0x91 => "VCmpxLtI32",
            0x92 => "VCmpxEqI32",
            0x93 => "VCmpxLeI32",
            0x94 => "VCmpxGtI32",
            0x95 => "VCmpxNeI32",
            0x96 => "VCmpxGeI32",
            0x97 => "VCmpxTI32",
            0xC0 => "VCmpFU32",
            0xC1 => "VCmpLtU32",
            0xC2 => "VCmpEqU32",
            0xC3 => "VCmpLeU32",
            0xC4 => "VCmpGtU32",
            0xC5 => "VCmpNeU32",
            0xC6 => "VCmpGeU32",
            0xC7 => "VCmpTU32",
            0xD0 => "VCmpxFU32",
            0xD1 => "VCmpxLtU32",
            0xD2 => "VCmpxEqU32",
            0xD3 => "VCmpxLeU32",
            0xD4 => "VCmpxGtU32",
            0xD5 => "VCmpxNeU32",
            0xD6 => "VCmpxGeU32",
            0xD7 => "VCmpxTU32",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-vopc op=0x{opcode:X2}", out error);
    }

    private static bool DecodeVop3(
        uint word,
        uint extra,
        bool isVop3B,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        var opcode = (word >> 16) & 0x3FF;
        var src0 = extra & 0x1FF;
        var src1 = (extra >> 9) & 0x1FF;
        var src2 = (extra >> 18) & 0x1FF;
        sizeDwords = src0 == 0xFF || src1 == 0xFF || src2 == 0xFF ? 3u : 2u;
        error = string.Empty;
        name = isVop3B
            ? opcode switch
            {
                0x128 => "VAddCoCiU32",
                0x30F => "VAddCoU32",
                0x310 => "VSubCoU32",
                0x319 => "VSubrevCoU32",
                0x176 => "VMadU64U32",
                _ => $"Vop3bRaw{opcode:X3}",
            }
            : opcode switch
        {
            0x101 => "VCndmaskB32",
            0x103 => "VAddF32",
            0x104 => "VSubF32",
            0x108 => "VMulF32",
            0x10F => "VMinF32",
            0x110 => "VMaxF32",
            0x11F => "VMacF32",
            0x12B => "VFmacF32",
            0x12F => "VCvtPkrtzF16F32",
            0x141 => "VMadF32",
            0x143 => "VMadU32U24",
            0x144 => "VCubeidF32",
            0x145 => "VCubescF32",
            0x146 => "VCubetcF32",
            0x147 => "VCubemaF32",
            0x14A => "VBfiB32",
            0x14B => "VFmaF32",
            0x151 => "VMin3F32",
            0x152 => "VMin3I32",
            0x153 => "VMin3U32",
            0x154 => "VMax3F32",
            0x155 => "VMax3I32",
            0x156 => "VMax3U32",
            0x157 => "VMed3F32",
            0x158 => "VMed3I32",
            0x159 => "VMed3U32",
            0x15A => "VSadU8",
            0x15B => "VSadHiU8",
            0x15C => "VSadU16",
            0x15D => "VSadU32",
            0x15E => "VCvtPkU8F32",
            0x148 => "VBfeU32",
            0x169 => "VMulLoU32",
            0x16A => "VMulHiU32",
            0x16B => "VMulLoI32",
            0x16C => "VMulHiI32",
            0x360 => "VReadlaneB32",
            0x361 => "VWritelaneB32",
            0x362 => "VLdexpF32",
            0x363 => "VBfmB32",
            0x364 => "VBcntU32B32",
            0x365 => "VMbcntLoU32B32",
            0x366 => "VMbcntHiU32B32",
            0x368 => "VCvtPknormI16F32",
            0x369 => "VCvtPknormU16F32",
            0x373 => "VMadU32U16",
            0x346 => "VLshlAddU32",
            0x347 => "VAddLshlU32",
            0x36D => "VAdd3U32",
            0x36F => "VLshlOrU32",
            0x371 => "VAndOrB32",
            0x372 => "VOr3U32",
            0x377 => "VPermlane16B32",
            0x378 => "VPermlanex16B32",
            _ => $"Vop3Raw{opcode:X3}",
        };

        return FinishDecode(name, $"unknown-vop3 op=0x{opcode:X3}", out error);
    }

    private static bool IsVop3BOpcode(uint opcode) =>
        opcode is 0x128 or 0x16D or 0x16E or 0x176 or 0x177 or 0x30F or 0x310 or 0x319;

    private static bool DecodeRaw2(
        uint word,
        string prefix,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        name = $"{prefix}Raw{word >> 24:X2}";
        sizeDwords = 2;
        error = string.Empty;
        return true;
    }

    private static bool DecodeVop3p(
        uint word,
        uint extra,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        var opcode = (word >> 16) & 0x7F;
        var src0 = extra & 0x1FF;
        var src1 = (extra >> 9) & 0x1FF;
        var src2 = (extra >> 18) & 0x1FF;
        sizeDwords = src0 == 0xFF || src1 == 0xFF || src2 == 0xFF ? 3u : 2u;
        error = string.Empty;

        // Opcode numbers taken from LLVM's AMDGPU VOP3PInstructions.td and the
        // gfx9/gfx10 MC test encodings; they are unchanged across gfx9 and gfx10.
        // The mix ops (0x20/0x21/0x22) are V_MAD_MIX_* on gfx9 and V_FMA_MIX_*
        // (fused) on the gfx10 the PS5 targets; both share these opcodes. Any
        // remaining packed opcode (integer, ...) stays opaque here and fails
        // loudly at emission rather than being silently mis-emitted.
        name = opcode switch
        {
            0x0E => "VPkFmaF16",
            0x0F => "VPkAddF16",
            0x10 => "VPkMulF16",
            0x11 => "VPkMinF16",
            0x12 => "VPkMaxF16",
            0x20 => "VFmaMixF32",
            0x21 => "VFmaMixloF16",
            0x22 => "VFmaMixhiF16",
            _ => $"Vop3pRaw{opcode:X2}",
        };

        return true;
    }

    private static bool DecodeDs(
        uint word,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        var opcode = (word >> 18) & 0xFF;
        sizeDwords = 2;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "DsAddU32",
            0x01 => "DsSubU32",
            0x03 => "DsIncU32",
            0x04 => "DsDecU32",
            0x05 => "DsMinI32",
            0x06 => "DsMaxI32",
            0x07 => "DsMinU32",
            0x08 => "DsMaxU32",
            0x09 => "DsAndB32",
            0x0A => "DsOrB32",
            0x0B => "DsXorB32",
            0x0D => "DsWriteB32",
            0x0E => "DsWrite2B32",
            0x0F => "DsWrite2St64B32",
            0x10 => "DsCmpstB32",
            0x20 => "DsAddRtnU32",
            0x21 => "DsSubRtnU32",
            0x23 => "DsIncRtnU32",
            0x24 => "DsDecRtnU32",
            0x25 => "DsMinRtnI32",
            0x26 => "DsMaxRtnI32",
            0x27 => "DsMinRtnU32",
            0x28 => "DsMaxRtnU32",
            0x29 => "DsAndRtnB32",
            0x2A => "DsOrRtnB32",
            0x2B => "DsXorRtnB32",
            0x2D => "DsWrxchgRtnB32",
            0x30 => "DsCmpstRtnB32",
            0x35 => "DsSwizzleB32",
            0x36 => "DsReadB32",
            0x37 => "DsRead2B32",
            0x38 => "DsRead2St64B32",
            0x4D => "DsWriteB64",
            0xDE => "DsWriteB96",
            0xDF => "DsWriteB128",
            0xFE => "DsReadB96",
            0xFF => "DsReadB128",
            _ => string.Empty,
        };

        return FinishDecode(
            name,
            $"unknown-ds op=0x{opcode:X2} word=0x{word:X8}",
            out error);
    }

    private static bool DecodeBuffer(
        uint word,
        string prefix,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        var opcode = (word >> 18) & 0x7F;
        name = $"{prefix}Raw{opcode:X2}";
        sizeDwords = 2;
        error = string.Empty;
        return true;
    }

    private static bool DecodeMtbuf(
        uint word,
        uint extra,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        var opcode = (word >> 16) & 0x7;
        name = opcode switch
        {
            0x00 => "TBufferLoadFormatX",
            0x01 => "TBufferLoadFormatXy",
            0x02 => "TBufferLoadFormatXyz",
            0x03 => "TBufferLoadFormatXyzw",
            0x04 => "TBufferStoreFormatX",
            0x05 => "TBufferStoreFormatXy",
            0x06 => "TBufferStoreFormatXyz",
            0x07 => "TBufferStoreFormatXyzw",
            _ => string.Empty,
        };
        sizeDwords = (extra >> 24) == 0xFF ? 3u : 2u;
        error = string.Empty;
        return true;
    }

    private static bool DecodeMubuf(
        uint word,
        uint extra,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        var opcode = (word >> 18) & 0x7F;
        name = opcode switch
        {
            0x00 => "BufferLoadFormatX",
            0x01 => "BufferLoadFormatXy",
            0x02 => "BufferLoadFormatXyz",
            0x03 => "BufferLoadFormatXyzw",
            0x04 => "BufferStoreFormatX",
            0x05 => "BufferStoreFormatXy",
            0x06 => "BufferStoreFormatXyz",
            0x07 => "BufferStoreFormatXyzw",
            0x08 => "BufferLoadUbyte",
            0x09 => "BufferLoadSbyte",
            0x0A => "BufferLoadUshort",
            0x0B => "BufferLoadSshort",
            0x0C => "BufferLoadDword",
            0x0D => "BufferLoadDwordx2",
            0x0E => "BufferLoadDwordx4",
            0x0F => "BufferLoadDwordx3",
            0x18 => "BufferStoreByte",
            0x19 => "BufferStoreByteD16Hi",
            0x1A => "BufferStoreShort",
            0x1B => "BufferStoreShortD16Hi",
            0x1C => "BufferStoreDword",
            0x1D => "BufferStoreDwordx2",
            0x1E => "BufferStoreDwordx4",
            0x1F => "BufferStoreDwordx3",
            0x20 => "BufferLoadUbyteD16",
            0x21 => "BufferLoadUbyteD16Hi",
            0x22 => "BufferLoadSbyteD16",
            0x23 => "BufferLoadSbyteD16Hi",
            0x24 => "BufferLoadShortD16",
            0x25 => "BufferLoadShortD16Hi",
            0x30 => "BufferAtomicSwap",
            0x31 => "BufferAtomicCmpswap",
            0x32 => "BufferAtomicAdd",
            0x33 => "BufferAtomicSub",
            0x35 => "BufferAtomicSmin",
            0x36 => "BufferAtomicUmin",
            0x37 => "BufferAtomicSmax",
            0x38 => "BufferAtomicUmax",
            0x39 => "BufferAtomicAnd",
            0x3A => "BufferAtomicOr",
            0x3B => "BufferAtomicXor",
            0x3C => "BufferAtomicInc",
            0x3D => "BufferAtomicDec",
            _ => $"MubufRaw{opcode:X2}",
        };
        sizeDwords = (extra >> 24) == 0xFF ? 3u : 2u;
        error = string.Empty;
        return true;
    }

    private static bool DecodeFlat(
        uint word,
        out string name,
        out uint sizeDwords,
        out string error)
    {
        var segment = (word >> 14) & 0x3;
        var opcode = (word >> 18) & 0x7F;
        sizeDwords = 2;
        error = string.Empty;
        name = segment == 0x2
            ? opcode switch
            {
                0x08 => "GlobalLoadUbyte",
                0x09 => "GlobalLoadSbyte",
                0x0A => "GlobalLoadUshort",
                0x0B => "GlobalLoadSshort",
                0x0C => "GlobalLoadDword",
                0x0D => "GlobalLoadDwordx2",
                0x0E => "GlobalLoadDwordx4",
                0x0F => "GlobalLoadDwordx3",
                0x18 => "GlobalStoreByte",
                0x19 => "GlobalStoreByteD16Hi",
                0x1A => "GlobalStoreShort",
                0x1B => "GlobalStoreShortD16Hi",
                0x1C => "GlobalStoreDword",
                0x1D => "GlobalStoreDwordx2",
                0x1E => "GlobalStoreDwordx4",
                0x1F => "GlobalStoreDwordx3",
                0x20 => "GlobalLoadUbyteD16",
                0x21 => "GlobalLoadUbyteD16Hi",
                0x22 => "GlobalLoadSbyteD16",
                0x23 => "GlobalLoadSbyteD16Hi",
                0x24 => "GlobalLoadShortD16",
                0x25 => "GlobalLoadShortD16Hi",
                0x32 => "GlobalAtomicAdd",
                0x38 => "GlobalAtomicUMax",
                _ => string.Empty,
            }
            : string.Empty;

        return FinishDecode(
            name,
            $"unknown-flat segment=0x{segment:X1} op=0x{opcode:X2} word=0x{word:X8}",
            out error);
    }

    private static bool DecodeSmrd(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 22) & 0x1F;
        var offset = word & 0xFF;
        var immediateOffset = ((word >> 8) & 1) != 0;
        sizeDwords = !immediateOffset && offset == 0xFF ? 2u : 1u;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "SLoadDword",
            0x01 => "SLoadDwordx2",
            0x02 => "SLoadDwordx4",
            0x03 => "SLoadDwordx8",
            0x04 => "SLoadDwordx16",
            0x08 => "SBufferLoadDword",
            0x09 => "SBufferLoadDwordx2",
            0x0A => "SBufferLoadDwordx4",
            0x0B => "SBufferLoadDwordx8",
            0x0C => "SBufferLoadDwordx16",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-smrd op=0x{opcode:X2}", out error);
    }

    private static bool DecodeSmem(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 18) & 0xFF;
        sizeDwords = 2;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "SLoadDword",
            0x01 => "SLoadDwordx2",
            0x02 => "SLoadDwordx4",
            0x03 => "SLoadDwordx8",
            0x04 => "SLoadDwordx16",
            0x08 => "SBufferLoadDword",
            0x09 => "SBufferLoadDwordx2",
            0x0A => "SBufferLoadDwordx4",
            0x0B => "SBufferLoadDwordx8",
            0x0C => "SBufferLoadDwordx16",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-smem op=0x{opcode:X2}", out error);
    }

    private static bool DecodeMimg(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 18) & 0x7F;
        sizeDwords = 2 + ((word >> 1) & 0x3);
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "ImageLoad",
            0x01 => "ImageLoadMip",
            0x08 => "ImageStore",
            0x09 => "ImageStoreMip",
            0x0E => "ImageGetResinfo",
            0x0F => "ImageAtomicSwap",
            0x10 => "ImageAtomicCmpswap",
            0x11 => "ImageAtomicAdd",
            0x12 => "ImageAtomicSub",
            0x14 => "ImageAtomicSmin",
            0x15 => "ImageAtomicUmin",
            0x16 => "ImageAtomicSmax",
            0x17 => "ImageAtomicUmax",
            0x18 => "ImageAtomicAnd",
            0x19 => "ImageAtomicOr",
            0x1A => "ImageAtomicXor",
            0x1B => "ImageAtomicInc",
            0x1C => "ImageAtomicDec",
            0x20 => "ImageSample",
            0x22 => "ImageSampleD",
            0x24 => "ImageSampleL",
            0x25 => "ImageSampleB",
            0x27 => "ImageSampleLz",
            0x2F => "ImageSampleCLz",
            0x30 => "ImageSampleO",
            0x34 => "ImageSampleLO",
            0x37 => "ImageSampleLzO",
            0x40 => "ImageGather4",
            0x47 => "ImageGather4Lz",
            0x48 => "ImageGather4C",
            0x4E => "ImageGather4CBCl",
            0x57 => "ImageGather4LzO",
            0x5F => "ImageGather4CLzO",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-mimg op=0x{opcode:X2}", out error);
    }

    private static bool DecodeVintrp(uint word, out string name, out uint sizeDwords, out string error)
    {
        var opcode = (word >> 16) & 0x3;
        sizeDwords = 1;
        error = string.Empty;
        name = opcode switch
        {
            0x00 => "VInterpP1F32",
            0x01 => "VInterpP2F32",
            0x02 => "VInterpMovF32",
            _ => string.Empty,
        };

        return FinishDecode(name, $"unknown-vintrp op=0x{opcode:X1}", out error);
    }

    private static bool FinishDecode(string name, string decodeError, out string error)
    {
        error = string.Empty;
        if (name.Length != 0)
        {
            return true;
        }

        error = decodeError;
        return false;
    }

    private static string ClassifyInstruction(string name)
    {
        if (name.StartsWith("Image", StringComparison.Ordinal))
        {
            return "image";
        }

        if (name.StartsWith("Global", StringComparison.Ordinal))
        {
            return "global_memory";
        }

        if (name.StartsWith("VInterp", StringComparison.Ordinal))
        {
            return "interp";
        }

        if (string.Equals(name, "Exp", StringComparison.Ordinal))
        {
            return "export";
        }

        if (name.StartsWith("SLoad", StringComparison.Ordinal) ||
            name.StartsWith("SBufferLoad", StringComparison.Ordinal))
        {
            return "scalar_load";
        }

        if (name.StartsWith('V'))
        {
            return "valu";
        }

        if (name.StartsWith('S'))
        {
            return "salu";
        }

        return "other";
    }

    private static bool IsMimgInstruction(string name) =>
        name.StartsWith("Image", StringComparison.Ordinal);

    public static bool IsImageLoadOperation(string name) =>
        name.StartsWith("ImageLoad", StringComparison.Ordinal);

    public static bool IsStorageImageOperation(string name) =>
        name.StartsWith("ImageStore", StringComparison.Ordinal) ||
        name.StartsWith("ImageAtomic", StringComparison.Ordinal);

    public static bool RequiresStorageImage(
        Gen5ImageBinding binding,
        IReadOnlyList<Gen5ImageBinding> stageBindings)
    {
        if (IsStorageImageOperation(binding.Opcode))
        {
            return true;
        }

        if (!IsImageLoadOperation(binding.Opcode))
        {
            return false;
        }

        // IMAGE_LOAD itself is read-only and maps naturally to OpImageFetch,
        // including for block-compressed textures which Vulkan cannot expose
        // as storage images. Keep it as storage only when the same resolved
        // descriptor is also written in this shader stage, preserving coherent
        // read/write access through one storage-image representation.
        return stageBindings.Any(candidate =>
            IsStorageImageOperation(candidate.Opcode) &&
            binding.ResourceDescriptor.SequenceEqual(candidate.ResourceDescriptor));
    }

    public static bool IsArrayedImageBinding(Gen5ImageBinding binding) =>
        binding.Control.IsArray &&
        (binding.Opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
         binding.Opcode.StartsWith("ImageGather4", StringComparison.Ordinal));

    public static bool IsDataShareAtomic(string name) => name switch
    {
        "DsAddU32" or "DsSubU32" or "DsIncU32" or "DsDecU32" or
        "DsMinI32" or "DsMaxI32" or "DsMinU32" or "DsMaxU32" or
        "DsAndB32" or "DsOrB32" or "DsXorB32" or "DsCmpstB32" or
        "DsAddRtnU32" or "DsSubRtnU32" or "DsIncRtnU32" or "DsDecRtnU32" or
        "DsMinRtnI32" or "DsMaxRtnI32" or "DsMinRtnU32" or "DsMaxRtnU32" or
        "DsAndRtnB32" or "DsOrRtnB32" or "DsXorRtnB32" or
        "DsWrxchgRtnB32" or "DsCmpstRtnB32" => true,
        _ => false,
    };

    private static Gen5ShaderInstruction CreateInstruction(
        uint pc,
        Gen5ShaderEncoding encoding,
        string opcode,
        uint[] words)
    {
        var word = words[0];
        var isSdwa =
            encoding is Gen5ShaderEncoding.Vop1 or Gen5ShaderEncoding.Vop2 or Gen5ShaderEncoding.Vopc &&
            (word & 0x1FF) == 0xF9;
        var isDpp =
            encoding is Gen5ShaderEncoding.Vop1 or Gen5ShaderEncoding.Vop2 or Gen5ShaderEncoding.Vopc &&
            (word & 0x1FF) == 0xFA;
        var isDpp8 =
            encoding is Gen5ShaderEncoding.Vop1 or Gen5ShaderEncoding.Vop2 or Gen5ShaderEncoding.Vopc &&
            (word & 0x1FF) is 0xE9 or 0xEA;
        var literal = !isSdwa && !isDpp && !isDpp8 &&
            words.Length > MinimumEncodingDwords(encoding)
            ? words[^1]
            : (uint?)null;
        IReadOnlyList<Gen5Operand> sources = [];
        IReadOnlyList<Gen5Operand> destinations = [];
        Gen5InstructionControl? control = null;

        switch (encoding)
        {
            case Gen5ShaderEncoding.Sop1:
                sources = [Gen5Operand.Source(word & 0xFF, literal)];
                destinations = [Gen5Operand.Scalar((word >> 16) & 0x7F)];
                break;
            case Gen5ShaderEncoding.Sop2:
                sources =
                [
                    Gen5Operand.Source(word & 0xFF, literal),
                    Gen5Operand.Source((word >> 8) & 0xFF, literal),
                ];
                destinations = [Gen5Operand.Scalar((word >> 16) & 0x7F)];
                break;
            case Gen5ShaderEncoding.Sopc:
                sources =
                [
                    Gen5Operand.Source(word & 0xFF, literal),
                    Gen5Operand.Source((word >> 8) & 0xFF, literal),
                ];
                break;
            case Gen5ShaderEncoding.Sopk:
                sources = [new Gen5Operand(Gen5OperandKind.EncodedConstant, word & 0xFFFF)];
                destinations = [Gen5Operand.Scalar((word >> 16) & 0x7F)];
                break;
            case Gen5ShaderEncoding.Smrd:
            {
                var scalarBase = ((word >> 9) & 0x3F) * 2;
                var scalarDestination = (word >> 15) & 0x7F;
                var immediate = ((word >> 8) & 1) != 0;
                var offset = word & 0xFF;
                var count = ScalarLoadDwordCount(opcode);
                uint? dynamicOffsetRegister = null;
                var immediateOffsetBytes = 0;
                if (immediate)
                {
                    immediateOffsetBytes = checked((int)(offset * sizeof(uint)));
                }
                else if (offset == 0xFF && literal.HasValue)
                {
                    immediateOffsetBytes = unchecked((int)literal.Value);
                }
                else
                {
                    dynamicOffsetRegister = offset;
                }

                sources = dynamicOffsetRegister.HasValue
                    ? [Gen5Operand.Scalar(scalarBase), Gen5Operand.Scalar(dynamicOffsetRegister.Value)]
                    : [Gen5Operand.Scalar(scalarBase)];
                destinations = Enumerable
                    .Range((int)scalarDestination, checked((int)count))
                    .Select(index => Gen5Operand.Scalar((uint)index))
                    .ToArray();
                control = new Gen5ScalarMemoryControl(
                    count,
                    immediateOffsetBytes,
                    dynamicOffsetRegister);
                break;
            }
            case Gen5ShaderEncoding.Smem:
            {
                var extra = words[1];
                var scalarBase = (word & 0x3F) * 2;
                var scalarDestination = (word >> 6) & 0x7F;
                var scalarOffset = (extra >> 25) & 0x7F;
                var offset = SignExtend(extra & 0x1FFFFF, 21);
                var count = ScalarLoadDwordCount(opcode);
                var scalarOffsetOperand = Gen5Operand.Source(scalarOffset);
                var dynamicOffsetRegister = scalarOffsetOperand.Kind ==
                    Gen5OperandKind.ScalarRegister
                    ? scalarOffsetOperand.Value
                    : (uint?)null;
                sources =
                [
                    Gen5Operand.Scalar(scalarBase),
                    scalarOffsetOperand,
                ];
                destinations = Enumerable
                    .Range((int)scalarDestination, checked((int)count))
                    .Select(index => Gen5Operand.Scalar((uint)index))
                    .ToArray();
                control = new Gen5ScalarMemoryControl(
                    count,
                    offset,
                    dynamicOffsetRegister);
                break;
            }
            case Gen5ShaderEncoding.Vop1:
                if (isDpp8)
                {
                    var extra = words[1];
                    sources = [Gen5Operand.Vector(extra & 0xFF)];
                    control = new Gen5Dpp8Control(
                        extra >> 8,
                        (word & 0x1FF) == 0xEA);
                }
                else if (isDpp)
                {
                    var extra = words[1];
                    sources = [Gen5Operand.Vector(extra & 0xFF)];
                    control = CreateDppControl(extra);
                }
                else if (isSdwa)
                {
                    var extra = words[1];
                    var source0 = (extra & 0xFF) +
                        ((((extra >> 23) & 1) == 0) ? 256u : 0u);
                    sources = [Gen5Operand.Source(source0)];
                    control = CreateSdwaControl(extra, isCompare: false, hasSource1: false);
                }
                else
                {
                    sources = [Gen5Operand.Source(word & 0x1FF, literal)];
                }

                // V_READFIRSTLANE_B32 is encoded as VOP1, but its destination
                // field names an SGPR rather than a VGPR. Treating it like an
                // ordinary vector destination leaves every invocation with a
                // different value and corrupts scalar addresses derived from
                // lane data.
                destinations = opcode == "VReadfirstlaneB32"
                    ? [Gen5Operand.Scalar((word >> 17) & 0x7F)]
                    : [Gen5Operand.Vector((word >> 17) & 0xFF)];
                break;
            case Gen5ShaderEncoding.Vop2:
                if (isDpp8)
                {
                    var extra = words[1];
                    sources =
                    [
                        Gen5Operand.Vector(extra & 0xFF),
                        Gen5Operand.Vector((word >> 9) & 0xFF),
                    ];
                    control = new Gen5Dpp8Control(
                        extra >> 8,
                        (word & 0x1FF) == 0xEA);
                }
                else if (isDpp)
                {
                    var extra = words[1];
                    sources =
                    [
                        Gen5Operand.Vector(extra & 0xFF),
                        Gen5Operand.Vector((word >> 9) & 0xFF),
                    ];
                    control = CreateDppControl(extra);
                }
                else if (isSdwa)
                {
                    var extra = words[1];
                    var source0 = (extra & 0xFF) + ((((extra >> 23) & 1) == 0) ? 256u : 0u);
                    var source1 =
                        ((word >> 9) & 0xFF) +
                        ((((extra >> 31) & 1) == 0) ? 256u : 0u);
                    sources =
                    [
                        Gen5Operand.Source(source0),
                        Gen5Operand.Source(source1),
                    ];
                    control = CreateSdwaControl(extra, isCompare: false, hasSource1: true);
                }
                else
                {
                    sources =
                    [
                        Gen5Operand.Source(word & 0x1FF, literal),
                        Gen5Operand.Vector((word >> 9) & 0xFF),
                    ];
                    if ((opcode is "VMadMkF32" or "VFmaMkF32") && literal.HasValue)
                    {
                        sources =
                        [
                            sources[0],
                            new Gen5Operand(Gen5OperandKind.LiteralConstant, literal.Value),
                            sources[1],
                        ];
                    }
                    else if ((opcode is "VMadAkF32" or "VFmaAkF32") && literal.HasValue)
                    {
                        sources =
                        [
                            .. sources,
                            new Gen5Operand(Gen5OperandKind.LiteralConstant, literal.Value),
                        ];
                    }
                }

                destinations = [Gen5Operand.Vector((word >> 17) & 0xFF)];
                break;
            case Gen5ShaderEncoding.Vopc:
                if (isDpp8)
                {
                    var extra = words[1];
                    sources =
                    [
                        Gen5Operand.Vector(extra & 0xFF),
                        Gen5Operand.Vector((word >> 9) & 0xFF),
                    ];
                    control = new Gen5Dpp8Control(
                        extra >> 8,
                        (word & 0x1FF) == 0xEA);
                }
                else if (isDpp)
                {
                    var extra = words[1];
                    sources =
                    [
                        Gen5Operand.Vector(extra & 0xFF),
                        Gen5Operand.Vector((word >> 9) & 0xFF),
                    ];
                    control = CreateDppControl(extra);
                }
                else if (isSdwa)
                {
                    var extra = words[1];
                    var source0 = (extra & 0xFF) +
                        ((((extra >> 23) & 1) == 0) ? 256u : 0u);
                    var source1 =
                        ((word >> 9) & 0xFF) +
                        ((((extra >> 31) & 1) == 0) ? 256u : 0u);
                    sources =
                    [
                        Gen5Operand.Source(source0),
                        Gen5Operand.Source(source1),
                    ];
                    var sdwa = CreateSdwaControl(extra, isCompare: true, hasSource1: true);
                    control = sdwa;
                    if (sdwa.ScalarDestination is { } scalarDestination &&
                        scalarDestination != 106)
                    {
                        destinations = [Gen5Operand.Scalar(scalarDestination)];
                    }
                }
                else
                {
                    sources =
                    [
                        Gen5Operand.Source(word & 0x1FF, literal),
                        Gen5Operand.Vector((word >> 9) & 0xFF),
                    ];
                }
                break;
            case Gen5ShaderEncoding.Vop3:
            {
                var extra = words[1];
                sources =
                [
                    Gen5Operand.Source(extra & 0x1FF, literal),
                    Gen5Operand.Source((extra >> 9) & 0x1FF, literal),
                    Gen5Operand.Source((extra >> 18) & 0x1FF, literal),
                ];
                destinations = [Gen5Operand.Vector(word & 0xFF)];
                if (opcode == "VReadlaneB32")
                {
                    // V_READLANE uses the VOP3A vdst byte even though the
                    // destination register is scalar. Bits 8-14 are the
                    // distinct sdst field used by VOP3B encodings.
                    destinations = [Gen5Operand.Scalar(word & 0xFF)];
                }
                var isVop3B = IsVop3BOpcode((word >> 16) & 0x3FF);
                control = new Gen5Vop3Control(
                    isVop3B ? 0 : (word >> 8) & 0x7,
                    (extra >> 29) & 0x7,
                    (extra >> 27) & 0x3,
                    ((word >> 15) & 1) != 0,
                    isVop3B ? 0 : (word >> 11) & 0xF,
                    isVop3B ? (word >> 8) & 0x7F : null);
                break;
            }
            case Gen5ShaderEncoding.Vop3p:
            {
                var extra = words[1];
                sources =
                [
                    Gen5Operand.Source(extra & 0x1FF, literal),
                    Gen5Operand.Source((extra >> 9) & 0x1FF, literal),
                    Gen5Operand.Source((extra >> 18) & 0x1FF, literal),
                ];
                destinations = [Gen5Operand.Vector(word & 0xFF)];

                // op_sel_hi is split across both dwords: bits [1:0] live in word1
                // [28:27], bit [2] in word0 [14].
                var opSelHi = ((extra >> 27) & 0x3) | (((word >> 14) & 0x1) << 2);
                control = new Gen5Vop3pControl(
                    (word >> 11) & 0x7,
                    opSelHi,
                    (extra >> 29) & 0x7,
                    (word >> 8) & 0x7,
                    ((word >> 15) & 1) != 0);
                break;
            }
            case Gen5ShaderEncoding.Ds:
            {
                var extra = words[1];
                var vectorAddress = extra & 0xFF;
                var vectorData0 = (extra >> 8) & 0xFF;
                var vectorData1 = (extra >> 16) & 0xFF;
                var vectorDestination = (extra >> 24) & 0xFF;
                control = new Gen5DataShareControl(
                    word & 0xFF,
                    (word >> 8) & 0xFF,
                    ((word >> 17) & 1) != 0);
                sources = opcode switch
                {
                    "DsWriteB32" => [
                        Gen5Operand.Vector(vectorAddress),
                        Gen5Operand.Vector(vectorData0),
                    ],
                    "DsWriteB64" => [
                        Gen5Operand.Vector(vectorAddress),
                        Gen5Operand.Vector(vectorData0),
                        Gen5Operand.Vector(vectorData0 + 1),
                    ],
                    "DsWriteB96" => [
                        Gen5Operand.Vector(vectorAddress),
                        Gen5Operand.Vector(vectorData0),
                        Gen5Operand.Vector(vectorData0 + 1),
                        Gen5Operand.Vector(vectorData0 + 2),
                    ],
                    "DsWriteB128" => [
                        Gen5Operand.Vector(vectorAddress),
                        Gen5Operand.Vector(vectorData0),
                        Gen5Operand.Vector(vectorData0 + 1),
                        Gen5Operand.Vector(vectorData0 + 2),
                        Gen5Operand.Vector(vectorData0 + 3),
                    ],
                    "DsWrite2B32" or "DsWrite2St64B32" => [
                        Gen5Operand.Vector(vectorAddress),
                        Gen5Operand.Vector(vectorData0),
                        Gen5Operand.Vector(vectorData1),
                    ],
                    "DsSwizzleB32" => [Gen5Operand.Vector(vectorData0)],
                    // DS_CMPST operand order is reversed vs buffer/image cmpswap:
                    // DATA0 holds the comparator, DATA1 holds the new value.
                    "DsCmpstB32" or "DsCmpstRtnB32" => [
                        Gen5Operand.Vector(vectorAddress),
                        Gen5Operand.Vector(vectorData0),
                        Gen5Operand.Vector(vectorData1),
                    ],
                    _ when IsDataShareAtomic(opcode) => [
                        Gen5Operand.Vector(vectorAddress),
                        Gen5Operand.Vector(vectorData0),
                    ],
                    _ => [Gen5Operand.Vector(vectorAddress)],
                };
                destinations = opcode switch
                {
                    "DsReadB32" or "DsSwizzleB32" => [
                        Gen5Operand.Vector(vectorDestination),
                    ],
                    "DsRead2B32" or "DsRead2St64B32" => [
                        Gen5Operand.Vector(vectorDestination),
                        Gen5Operand.Vector(vectorDestination + 1),
                    ],
                    "DsReadB96" => [
                        Gen5Operand.Vector(vectorDestination),
                        Gen5Operand.Vector(vectorDestination + 1),
                        Gen5Operand.Vector(vectorDestination + 2),
                    ],
                    "DsReadB128" => [
                        Gen5Operand.Vector(vectorDestination),
                        Gen5Operand.Vector(vectorDestination + 1),
                        Gen5Operand.Vector(vectorDestination + 2),
                        Gen5Operand.Vector(vectorDestination + 3),
                    ],
                    _ when IsDataShareAtomic(opcode) &&
                        opcode.Contains("Rtn", StringComparison.Ordinal) => [
                        Gen5Operand.Vector(vectorDestination),
                    ],
                    _ => [],
                };
                break;
            }
            case Gen5ShaderEncoding.Vintrp:
                sources = [Gen5Operand.Vector(word & 0xFF)];
                destinations = [Gen5Operand.Vector((word >> 18) & 0xFF)];
                control = new Gen5InterpolationControl(
                    (word >> 10) & 0x3F,
                    (word >> 8) & 0x3);
                break;
            case Gen5ShaderEncoding.Flat:
            {
                var extra = words[1];
                var vectorAddress = extra & 0xFF;
                var vectorData = (extra >> 8) & 0xFF;
                var scalarAddress = (extra >> 16) & 0x7F;
                var dwordCount = opcode switch
                {
                    "GlobalLoadUbyte" or
                    "GlobalLoadSbyte" or
                    "GlobalLoadUshort" or
                    "GlobalLoadSshort" or
                    "GlobalLoadUbyteD16" or
                    "GlobalLoadUbyteD16Hi" or
                    "GlobalLoadSbyteD16" or
                    "GlobalLoadSbyteD16Hi" or
                    "GlobalLoadShortD16" or
                    "GlobalLoadShortD16Hi" or
                    "GlobalStoreByte" or
                    "GlobalStoreByteD16Hi" or
                    "GlobalStoreShort" or
                    "GlobalStoreShortD16Hi" or
                    "GlobalStoreDword" or
                    "GlobalAtomicAdd" or
                    "GlobalAtomicUMax" => 1u,
                    "GlobalLoadDword" => 1u,
                    "GlobalLoadDwordx2" => 2u,
                    "GlobalLoadDwordx3" => 3u,
                    "GlobalLoadDwordx4" => 4u,
                    "GlobalStoreDwordx2" => 2u,
                    "GlobalStoreDwordx3" => 3u,
                    "GlobalStoreDwordx4" => 4u,
                    _ => 0u,
                };
                sources =
                [
                    Gen5Operand.Vector(vectorAddress),
                    Gen5Operand.Scalar(scalarAddress),
                ];
                destinations = opcode.StartsWith("GlobalLoad", StringComparison.Ordinal)
                    ? Enumerable
                        .Range((int)vectorData, checked((int)dwordCount))
                        .Select(index => Gen5Operand.Vector((uint)index))
                        .ToArray()
                    : [];
                control = new Gen5GlobalMemoryControl(
                    dwordCount,
                    vectorAddress,
                    vectorData,
                    scalarAddress,
                    SignExtend(word & 0x1FFF, 13),
                    ((word >> 16) & 1) != 0,
                    ((word >> 17) & 1) != 0);
                break;
            }
            case Gen5ShaderEncoding.Mubuf:
            {
                var extra = words[1];
                var vectorAddress = extra & 0xFF;
                var vectorData = (extra >> 8) & 0xFF;
                var scalarResource = ((extra >> 16) & 0x1F) * 4;
                var scalarOffset = (extra >> 24) & 0xFF;
                var dwordCount = opcode switch
                {
                    "BufferLoadFormatX" => 1u,
                    "BufferLoadFormatXy" => 2u,
                    "BufferLoadFormatXyz" => 3u,
                    "BufferLoadFormatXyzw" => 4u,
                    "BufferStoreFormatX" => 1u,
                    "BufferStoreFormatXy" => 2u,
                    "BufferStoreFormatXyz" => 3u,
                    "BufferStoreFormatXyzw" => 4u,
                    "BufferLoadUbyte" or
                    "BufferLoadSbyte" or
                    "BufferLoadUshort" or
                    "BufferLoadSshort" or
                    "BufferStoreByte" or
                    "BufferStoreByteD16Hi" or
                    "BufferStoreShort" or
                    "BufferStoreShortD16Hi" or
                    "BufferLoadUbyteD16" or
                    "BufferLoadUbyteD16Hi" or
                    "BufferLoadSbyteD16" or
                    "BufferLoadSbyteD16Hi" or
                    "BufferLoadShortD16" or
                    "BufferLoadShortD16Hi" => 1u,
                    "BufferLoadDword" => 1u,
                    "BufferLoadDwordx2" => 2u,
                    "BufferLoadDwordx3" => 3u,
                    "BufferLoadDwordx4" => 4u,
                    "BufferStoreDword" => 1u,
                    "BufferStoreDwordx2" => 2u,
                    "BufferStoreDwordx3" => 3u,
                    "BufferStoreDwordx4" => 4u,
                    "BufferAtomicCmpswap" => 2u,
                    _ when opcode.StartsWith("BufferAtomic", StringComparison.Ordinal) => 1u,
                    _ => 0u,
                };
                sources =
                [
                    Gen5Operand.Vector(vectorAddress),
                    Gen5Operand.Scalar(scalarResource),
                    Gen5Operand.Source(scalarOffset, literal),
                ];
                destinations = Enumerable
                    .Range((int)vectorData, checked((int)dwordCount))
                    .Select(index => Gen5Operand.Vector((uint)index))
                    .ToArray();
                control = new Gen5BufferMemoryControl(
                    dwordCount,
                    vectorAddress,
                    vectorData,
                    scalarResource,
                    (int)(word & 0xFFF),
                    ((word >> 13) & 1) != 0,
                    ((word >> 12) & 1) != 0,
                    ((word >> 14) & 1) != 0,
                    ((extra >> 22) & 1) != 0);
                break;
            }
            case Gen5ShaderEncoding.Mtbuf:
            {
                var extra = words[1];
                var vectorAddress = extra & 0xFF;
                var vectorData = (extra >> 8) & 0xFF;
                var scalarResource = ((extra >> 16) & 0x1F) * 4;
                var scalarOffset = (extra >> 24) & 0xFF;
                var dwordCount = opcode switch
                {
                    "TBufferLoadFormatX" => 1u,
                    "TBufferLoadFormatXy" => 2u,
                    "TBufferLoadFormatXyz" => 3u,
                    "TBufferLoadFormatXyzw" => 4u,
                    _ => 0u,
                };
                sources =
                [
                    Gen5Operand.Vector(vectorAddress),
                    Gen5Operand.Scalar(scalarResource),
                    Gen5Operand.Source(scalarOffset, literal),
                ];
                destinations = Enumerable
                    .Range((int)vectorData, checked((int)dwordCount))
                    .Select(index => Gen5Operand.Vector((uint)index))
                    .ToArray();
                control = new Gen5BufferMemoryControl(
                    dwordCount,
                    vectorAddress,
                    vectorData,
                    scalarResource,
                    (int)(word & 0xFFF),
                    ((word >> 13) & 1) != 0,
                    ((word >> 12) & 1) != 0,
                    ((word >> 14) & 1) != 0,
                    ((extra >> 22) & 1) != 0);
                break;
            }
            case Gen5ShaderEncoding.Mimg:
            {
                var extra = words[1];
                var vectorAddress = extra & 0xFF;
                var vectorData = (extra >> 8) & 0xFF;
                var scalarResource = ((extra >> 16) & 0x1F) * 4;
                var scalarSampler = ((extra >> 21) & 0x1F) * 4;
                var addressRegisters = new List<uint>(1 + Math.Max(0, words.Length - 2) * 4)
                {
                    vectorAddress,
                };
                for (var wordIndex = 2; wordIndex < words.Length; wordIndex++)
                {
                    for (var shift = 0; shift < 32; shift += 8)
                    {
                        addressRegisters.Add((words[wordIndex] >> shift) & 0xFF);
                    }
                }

                var imageSources = new List<Gen5Operand>(addressRegisters.Count + 2);
                foreach (var addressRegister in addressRegisters)
                {
                    imageSources.Add(Gen5Operand.Vector(addressRegister));
                }

                imageSources.Add(Gen5Operand.Scalar(scalarResource));
                imageSources.Add(Gen5Operand.Scalar(scalarSampler));
                sources = imageSources;
                destinations = opcode.StartsWith("ImageStore", StringComparison.Ordinal)
                    ? []
                    : [Gen5Operand.Vector(vectorData)];
                var dimension = (word >> 3) & 0x7;
                control = new Gen5ImageControl(
                    (word >> 8) & 0xF,
                    vectorAddress,
                    addressRegisters,
                    vectorData,
                    scalarResource,
                    scalarSampler,
                    dimension,
                    dimension is 4 or 5 or 7,
                    ((word >> 13) & 1) != 0,
                    ((word >> 25) & 1) != 0,
                    ((extra >> 30) & 1) != 0,
                    ((extra >> 31) & 1) != 0);
                break;
            }
            case Gen5ShaderEncoding.Exp:
            {
                var extra = words[1];
                sources =
                [
                    Gen5Operand.Vector(extra & 0xFF),
                    Gen5Operand.Vector((extra >> 8) & 0xFF),
                    Gen5Operand.Vector((extra >> 16) & 0xFF),
                    Gen5Operand.Vector((extra >> 24) & 0xFF),
                ];
                control = new Gen5ExportControl(
                    (word >> 4) & 0x3F,
                    word & 0xF,
                    ((word >> 10) & 1) != 0,
                    ((word >> 11) & 1) != 0,
                    ((word >> 12) & 1) != 0);
                break;
            }
        }

        return new Gen5ShaderInstruction(pc, encoding, opcode, words, sources, destinations, control);
    }

    private static Gen5DppControl CreateDppControl(uint word) =>
        new(
            (word >> 8) & 0x1FF,
            ((word >> 18) & 1) != 0,
            ((word >> 19) & 1) != 0,
            ((word >> 21) & 1) | (((word >> 23) & 1) << 1),
            ((word >> 20) & 1) | (((word >> 22) & 1) << 1),
            (word >> 24) & 0xF,
            (word >> 28) & 0xF);

    private static Gen5SdwaControl CreateSdwaControl(
        uint word,
        bool isCompare,
        bool hasSource1)
    {
        var scalarDestination = isCompare
            ? ((word >> 15) & 1) != 0
                ? (word >> 8) & 0x7Fu
                : 106u
            : (uint?)null;
        return new Gen5SdwaControl(
            isCompare ? 6u : (word >> 8) & 0x7u,
            isCompare ? 0u : (word >> 11) & 0x3u,
            (word >> 16) & 0x7u,
            hasSource1 ? (word >> 24) & 0x7u : 6u,
            ((word >> 19) & 1) != 0,
            hasSource1 && ((word >> 27) & 1) != 0,
            ((word >> 21) & 1) | (hasSource1 ? ((word >> 29) & 1) << 1 : 0),
            ((word >> 20) & 1) | (hasSource1 ? ((word >> 28) & 1) << 1 : 0),
            isCompare ? 0u : (word >> 14) & 0x3u,
            !isCompare && ((word >> 13) & 1) != 0,
            scalarDestination);
    }

    private static int MinimumEncodingDwords(Gen5ShaderEncoding encoding) => encoding switch
    {
        Gen5ShaderEncoding.Vop3 or
        Gen5ShaderEncoding.Smem or
        Gen5ShaderEncoding.Mubuf or
        Gen5ShaderEncoding.Mtbuf or
        Gen5ShaderEncoding.Ds or
        Gen5ShaderEncoding.Flat or
        Gen5ShaderEncoding.Vop3p or
        Gen5ShaderEncoding.Mimg or
        Gen5ShaderEncoding.Exp => 2,
        _ => 1,
    };

    private static uint ScalarLoadDwordCount(string opcode) => opcode switch
    {
        "SLoadDword" or "SBufferLoadDword" => 1,
        "SLoadDwordx2" or "SBufferLoadDwordx2" => 2,
        "SLoadDwordx4" or "SBufferLoadDwordx4" => 4,
        "SLoadDwordx8" or "SBufferLoadDwordx8" => 8,
        "SLoadDwordx16" or "SBufferLoadDwordx16" => 16,
        _ => 0,
    };

    private static int SignExtend(uint value, int bits)
    {
        var shift = 32 - bits;
        return (int)(value << shift) >> shift;
    }

    private static void AddFeatureCount(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var count);
        counts[key] = count + 1;
    }

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

    private readonly record struct ShaderDecodeInfo(
        int InstructionCount,
        Dictionary<string, int> Counts,
        Dictionary<string, int> FeatureCounts,
        Dictionary<string, int> MimgCounts,
        List<string> Details)
    {
        public static ShaderDecodeInfo Create(Gen5ShaderProgram program)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var featureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var mimgCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var details = new List<string>();
            foreach (var instruction in program.Instructions)
            {
                AddFeatureCount(counts, instruction.Opcode);
                AddFeatureCount(featureCounts, ClassifyInstruction(instruction.Opcode));
                if (instruction.Control is Gen5ImageControl image)
                {
                    AddFeatureCount(mimgCounts, $"{instruction.Opcode}/dmask=0x{image.Dmask:X}");
                }

                if (details.Count < 16 && DescribeInstruction(instruction) is { } detail)
                {
                    details.Add(detail);
                }
            }

            return new ShaderDecodeInfo(
                program.Instructions.Count,
                counts,
                featureCounts,
                mimgCounts,
                details);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append("ins=");
            builder.Append(InstructionCount);
            AppendCounts(builder, " features=", FeatureCounts, 16);
            builder.Append(" ops=");
            AppendCounts(builder, string.Empty, Counts, 128);
            AppendCounts(builder, " mimg=", MimgCounts, 12);
            AppendDetails(builder, Details, 10);

            return builder.ToString();
        }

        private static string? DescribeInstruction(Gen5ShaderInstruction instruction)
        {
            if (instruction.Control is Gen5ImageControl image)
            {
                var addressRegisters = string.Join(
                    '/',
                    image.AddressRegisters.Select(register => $"v{register}"));
                return
                    $"{instruction.Opcode}@0x{instruction.Pc:X}:dm=0x{image.Dmask:X}," +
                    $"va={addressRegisters},vd=v{image.VectorData}," +
                    $"sr=s{image.ScalarResource},ss=s{image.ScalarSampler}," +
                    $"dim={image.Dimension},da={(image.IsArray ? 1 : 0)}," +
                    $"a16={(image.A16 ? 1 : 0)},d16={(image.D16 ? 1 : 0)}," +
                    $"glc={(image.Glc ? 1 : 0)}," +
                    $"slc={(image.Slc ? 1 : 0)}";
            }

            if (instruction.Control is Gen5ExportControl export)
            {
                return
                    $"Exp@0x{instruction.Pc:X}:target=0x{export.Target:X}," +
                    $"en=0x{export.EnableMask:X},compr={(export.Compressed ? 1 : 0)}," +
                    $"done={(export.Done ? 1 : 0)},vm={(export.ValidMask ? 1 : 0)}," +
                    $"src={string.Join('/', instruction.Sources)}";
            }

            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                return
                    $"{instruction.Opcode}@0x{instruction.Pc:X}:" +
                    $"attr={interpolation.Attribute},chan={interpolation.Channel}," +
                    $"src={instruction.Sources[0]},dst={instruction.Destinations[0]}";
            }

            return null;
        }

        private static void AppendCounts(
            StringBuilder builder,
            string prefix,
            Dictionary<string, int> counts,
            int limit)
        {
            if (counts.Count == 0)
            {
                return;
            }

            builder.Append(prefix);
            var written = 0;
            foreach (var (name, count) in counts)
            {
                if (written != 0)
                {
                    builder.Append(',');
                }

                builder.Append(name);
                builder.Append(':');
                builder.Append(count);
                written++;
                if (written == limit && counts.Count > written)
                {
                    builder.Append(",...");
                    break;
                }
            }
        }

        private static void AppendDetails(StringBuilder builder, List<string> details, int limit)
        {
            if (details.Count == 0)
            {
                return;
            }

            builder.Append(" detail=");
            var written = 0;
            foreach (var detail in details)
            {
                if (written != 0)
                {
                    builder.Append(';');
                }

                builder.Append(detail);
                written++;
                if (written == limit && details.Count > written)
                {
                    builder.Append(";...");
                    break;
                }
            }
        }
    }
}
