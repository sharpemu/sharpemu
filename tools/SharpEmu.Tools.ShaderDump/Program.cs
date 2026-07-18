// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Synthetic-shader conformance dumper.
//
// Feeds hand-assembled Gen5 (gfx10) instruction words through the real
// decode -> SPIR-V pipeline (SharpEmu.ShaderCompiler + SharpEmu.ShaderCompiler.Vulkan)
// and writes the resulting vertex, pixel, and compute SPIR-V blobs to disk. The blobs
// can then be checked with spirv-val / spirv-dis.
//
// Programs that contain buffer_store_dword automatically get a single
// global-memory binding covering every store, which the emitter exposes as
// guestBuffers[0] (descriptor set 0, binding 0).
//
// Each program carries an expectation: ExpectTranslate=true programs must
// decode and emit the requested stages; ExpectTranslate=false programs pin a decode
// failure that must stay loud. Any unexpected outcome makes the tool exit
// non-zero, so it can gate scripts/CI.
//
// Usage: SharpEmu.Tools.ShaderDump [output-directory]

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;

const ulong ProgramAddress = 0x100000;

(string Name, bool ExpectTranslate, uint[] Words)[] testPrograms =
[
    ("fmac", true, [
        0x560A0501,             // v_fmac_f32 v5, v1, v2
        0x580A0501, 0x42280000, // v_fmamk_f32 v5, v1, 42.0, v2
        0x5A0A0501, 0x42280000, // v_fmaak_f32 v5, v1, v2, 42.0
        0xD52B0005, 0x00020501, // v_fmac_f32_e64 v5, v1, v2
        0xBF810000,             // s_endpgm
    ]),
    ("muls", true, [
        0xD5690005, 0x00020501, // v_mul_lo_u32 v5, v1, v2
        0xD56A0005, 0x00020501, // v_mul_hi_u32 v5, v1, v2
        0xD56B0005, 0x00020501, // v_mul_lo_i32 v5, v1, v2
        0xD56C0005, 0x00020501, // v_mul_hi_i32 v5, v1, v2
        0xBF810000,             // s_endpgm
    ]),
    // Packed f16 (VOP3P) arithmetic, including the fused multiply-add. The
    // constants pin the double-rounding regression from the VOP3P first slice:
    // fma(0x4100, 0x7522, 0x04EA) must round once to 0x7A6B (an f32
    // multiply-add then pack yields 0x7A6A). The last fma exercises the src2
    // neg_lo/neg_hi modifier path.
    ("pk-f16", true, [
        0x7E0002FF, 0x41004100, // v_mov_b32 v0, 0x41004100 (2.5 packed)
        0x7E0202FF, 0x75227522, // v_mov_b32 v1, 0x75227522 (21024 packed)
        0x7E0402FF, 0x04EA04EA, // v_mov_b32 v2, 0x04EA04EA (~7.496e-5 packed)
        0xCC0E4003, 0x1C0A0300, // v_pk_fma_f16 v3, v0, v1, v2
        0xCC0F4004, 0x18020500, // v_pk_add_f16 v4, v0, v2
        0xCC104005, 0x18020300, // v_pk_mul_f16 v5, v0, v1
        0xCC114006, 0x18020300, // v_pk_min_f16 v6, v0, v1
        0xCC124007, 0x18020300, // v_pk_max_f16 v7, v0, v1
        0xCC0E4408, 0x9C0A0300, // v_pk_fma_f16 v8, v0, v1, neg_lo:[0,0,1] neg_hi:[0,0,1] v2
        0xBF810000,             // s_endpgm
    ]),
    ("mrt", true, [
        0x7E0002FF, 0x3F800000, // v_mov_b32 v0, 1.0f
        0x7E0202FF, 0x00000000, // v_mov_b32 v1, 0.0f
        0x7E0402FF, 0x00000000, // v_mov_b32 v2, 0.0f
        0x7E0602FF, 0x3F800000, // v_mov_b32 v3, 1.0f
        0x7E0802FF, 0x00000001, // v_mov_b32 v4, 1u
        0x7E0A02FF, 0x00000002, // v_mov_b32 v5, 2u
        0x7E0C02FF, 0x00000003, // v_mov_b32 v6, 3u
        0x7E0E02FF, 0x00000004, // v_mov_b32 v7, 4u
        0x7E1002FF, 0xFFFFFFFF, // v_mov_b32 v8, -1
        0x7E1202FF, 0x00000002, // v_mov_b32 v9, 2
        0x7E1402FF, 0xFFFFFFFD, // v_mov_b32 v10, -3
        0x7E1602FF, 0x00000004, // v_mov_b32 v11, 4
        0xF800000F, 0x03020100, // exp mrt0 v0, v1, v2, v3
        0xF800003F, 0x07060504, // exp mrt3 v4, v5, v6, v7
        0xF800086F, 0x0B0A0908, // exp mrt6 v8, v9, v10, v11 done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt-float2", true, [
        0x7E0002FF, 0x3F800000, // v_mov_b32 v0, 1.0f
        0x7E0202FF, 0x3E800000, // v_mov_b32 v1, 0.25f
        0x7E0402FF, 0x3E800000, // v_mov_b32 v2, 0.25f
        0x7E0602FF, 0x3F000000, // v_mov_b32 v3, 0.5f
        0xF800000F, 0x03020100, // exp mrt0 v0, v1, v2, v3
        0xF800081F, 0x03020100, // exp mrt1 v0, v1, v2, v3 done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt8", true, [
        0x7E0002FF, 0x3F800000, // v_mov_b32 v0, 1.0f
        0x7E0202FF, 0x00000000, // v_mov_b32 v1, 0.0f
        0x7E0402FF, 0x00000000, // v_mov_b32 v2, 0.0f
        0x7E0602FF, 0x3F800000, // v_mov_b32 v3, 1.0f
        0xF800000F, 0x03020100, // exp mrt0 v0, v1, v2, v3
        0xF800001F, 0x03020100, // exp mrt1 v0, v1, v2, v3
        0xF800002F, 0x03020100, // exp mrt2 v0, v1, v2, v3
        0xF800003F, 0x03020100, // exp mrt3 v0, v1, v2, v3
        0xF800004F, 0x03020100, // exp mrt4 v0, v1, v2, v3
        0xF800005F, 0x03020100, // exp mrt5 v0, v1, v2, v3
        0xF800006F, 0x03020100, // exp mrt6 v0, v1, v2, v3
        0xF800087F, 0x03020100, // exp mrt7 v0, v1, v2, v3 done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt-partial", true, [
        0x7E0002FF, 0x3F4CCCCD, // v_mov_b32 v0, 0.8f
        0x7E0202FF, 0x3F333333, // v_mov_b32 v1, 0.7f
        0xF8000803, 0x03020100, // exp mrt0 v0, v1, off, off done
        0xBF810000,             // s_endpgm
    ]),
    ("mrt-partial-merge", true, [
        0x7E0002FF, 0x3DCCCCCD, // v_mov_b32 v0, 0.1f
        0x7E0202FF, 0x3E4CCCCD, // v_mov_b32 v1, 0.2f
        0x7E0C02FF, 0x3E99999A, // v_mov_b32 v6, 0.3f
        0x7E0E02FF, 0x3ECCCCCD, // v_mov_b32 v7, 0.4f
        0xF8000003, 0x03020100, // exp mrt0 v0, v1, off, off
        0xF800080C, 0x07060504, // exp mrt0 off, off, v6, v7 done
        0xBF810000,             // s_endpgm
    ]),
    ("sopp-hints", true, [
        0xBFA10001,             // s_clause 0x1
        0xBFA30000,             // s_waitcnt_depctr 0x0
        0xBF810000,             // s_endpgm
    ]),
    // s_round_mode / s_denorm_mode write the FP MODE state and must keep
    // failing decode loudly until their semantics are modeled (see #108);
    // this program pins that behavior.
    ("sopp-mode", false, [
        0xBFA40000,             // s_round_mode 0x0
        0xBFA50000,             // s_denorm_mode 0x0
        0xBF810000,             // s_endpgm
    ]),
    // Executable end-to-end test: compute with real ALU instructions, then
    // buffer_store_dword results to guestBuffers[0] at offsets 0/4/8, prove
    // that a store with EXEC=0 does not land (offset 12 stays sentinel), and
    // that stores work again after EXEC is restored (offset 16). Offsets 20/24
    // hold the packed fused f16 FMA and its negated-addend twin, whose exact
    // results (0x7A6B7A6B / 0x7A6A7A6A) straddle an f16 midpoint and therefore
    // catch any double-rounding regression on real hardware.
    ("exec", true, [
        0xBFA10001,             // s_clause 0x1 (hint no-op in an executed program, needs #108)
        0x7E0002FF, 0x3FC00000, // v_mov_b32 v0, 1.5f
        0x7E0202FF, 0x40100000, // v_mov_b32 v1, 2.25f
        0x7E0402FF, 0x41200000, // v_mov_b32 v2, 10.0f
        0x56040300,             // v_fmac_f32 v2, v0, v1      -> v2 = fma(1.5, 2.25, 10.0)
        0x7E0602FF, 0x7FFFFFFF, // v_mov_b32 v3, 0x7FFFFFFF
        0x7E0802FF, 0x00010003, // v_mov_b32 v4, 0x00010003
        0xD56C0005, 0x00020903, // v_mul_hi_i32 v5, v3, v4
        0xD56B0006, 0x00020903, // v_mul_lo_i32 v6, v3, v4
        0xE0700000, 0x80020200, // buffer_store_dword v2, off, s[8:11], 0
        0xE0700004, 0x80020500, // buffer_store_dword v5, off, s[8:11], 0 offset:4
        0xE0700008, 0x80020600, // buffer_store_dword v6, off, s[8:11], 0 offset:8
        0xBEFE0380,             // s_mov_b32 exec_lo, 0       -> lane inactive
        0xE070000C, 0x80020200, // buffer_store_dword v2, off, s[8:11], 0 offset:12 (masked, must not land)
        0xBEFE03C1,             // s_mov_b32 exec_lo, -1      -> lane active again
        0xE0700010, 0x80020000, // buffer_store_dword v0, off, s[8:11], 0 offset:16
        0x7E0E02FF, 0x41004100, // v_mov_b32 v7, 0x41004100 (2.5 packed)
        0x7E1002FF, 0x75227522, // v_mov_b32 v8, 0x75227522 (21024 packed)
        0x7E1202FF, 0x04EA04EA, // v_mov_b32 v9, 0x04EA04EA (~7.496e-5 packed)
        0xCC0E400A, 0x1C261107, // v_pk_fma_f16 v10, v7, v8, v9
        0xCC0E440B, 0x9C261107, // v_pk_fma_f16 v11, v7, v8, neg_lo:[0,0,1] neg_hi:[0,0,1] v9
        0xE0700014, 0x80020A00, // buffer_store_dword v10, off, s[8:11], 0 offset:20
        0xE0700018, 0x80020B00, // buffer_store_dword v11, off, s[8:11], 0 offset:24
        0xBF810000,             // s_endpgm
    ]),
];

var outputDirectory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "spv");
Directory.CreateDirectory(outputDirectory);

var failures = 0;
foreach (var (name, expectTranslate, words) in testPrograms)
{
    var memory = new FakeMemory();
    memory.AddRegion(ProgramAddress, words);
    var ctx = new CpuContext(memory, Generation.Gen5);

    Console.WriteLine(
        $"[{name}] decode: " +
        Gen5ShaderTranslator.Describe(ctx, ProgramAddress, ProgramAddress));

    if (!Gen5ShaderTranslator.TryDecodeProgram(ctx, ProgramAddress, out var program, out var decodeError))
    {
        if (expectTranslate)
        {
            failures++;
            Console.WriteLine($"[{name}] FAILED: decode error ({decodeError})");
        }
        else
        {
            Console.WriteLine($"[{name}] decode failed as expected ({decodeError})");
        }

        continue;
    }

    if (!expectTranslate)
    {
        failures++;
        Console.WriteLine(
            $"[{name}] FAILED: decoded successfully but is pinned as a decode failure — " +
            "if the new decode support is intentional, its semantics need verifying here first");
        continue;
    }

    // Buffer stores need a global-memory binding; the emitter resolves them by
    // instruction PC, so collect store PCs from the decoded program itself.
    var storePcs = new List<uint>();
    foreach (var instruction in program!.Instructions)
    {
        if (instruction.Opcode.StartsWith("BufferStore", StringComparison.Ordinal))
        {
            storePcs.Add(instruction.Pc);
        }
    }

    // The binding's scalar base (8 -> s[8:11]) must match the srsrc field of
    // the hand-assembled buffer_store words, and the 64-byte backing store
    // must cover every hand-assembled store offset.
    var globalBindings = storePcs.Count > 0
        ? new[]
        {
            new Gen5GlobalMemoryBinding(
                8u,
                0UL,
                storePcs,
                new byte[64],
                64,
                false),
        }
        : Array.Empty<Gen5GlobalMemoryBinding>();

    var state = new Gen5ShaderState(program, new uint[16], Metadata: null);
    var evaluation = new Gen5ShaderEvaluation(
        new uint[256],
        new uint[256],
        Array.Empty<Gen5ImageBinding>(),
        globalBindings);

    if (Gen5SpirvTranslator.TryCompileVertexShader(state, evaluation, out var vertexShader, out var vertexError))
    {
        var path = Path.Combine(outputDirectory, $"{name}.spv");
        File.WriteAllBytes(path, vertexShader.Spirv);
        Console.WriteLine($"[{name}] emit: success, {vertexShader.Spirv.Length} bytes -> {path}");
    }
    else
    {
        failures++;
        Console.WriteLine($"[{name}] emit: FAILED ({vertexError})");
    }

    if (Gen5SpirvTranslator.TryCompileComputeShader(state, evaluation, 1, 1, 1, out var computeShader, out var computeError))
    {
        var path = Path.Combine(outputDirectory, $"{name}-cs.spv");
        File.WriteAllBytes(path, computeShader.Spirv);
        Console.WriteLine($"[{name}] compute emit: success, {computeShader.Spirv.Length} bytes -> {path}");
    }
    else
    {
        failures++;
        Console.WriteLine($"[{name}] compute emit: FAILED ({computeError})");
    }

    if (name.StartsWith("mrt", StringComparison.Ordinal))
    {
        Gen5PixelOutputBinding[] pixelOutputs = name switch
        {
            "mrt" =>
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(3, 1, Gen5PixelOutputKind.Uint),
                new Gen5PixelOutputBinding(6, 2, Gen5PixelOutputKind.Sint),
            ],
            "mrt-float2" =>
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(1, 1, Gen5PixelOutputKind.Float),
            ],
            "mrt8" =>
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(1, 1, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(2, 2, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(3, 3, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(4, 4, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(5, 5, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(6, 6, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(7, 7, Gen5PixelOutputKind.Float),
            ],
            _ => [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };

        if (Gen5SpirvTranslator.TryCompilePixelShader(state, evaluation, pixelOutputs, out var pixelShader, out var pixelError))
        {
            var path = Path.Combine(outputDirectory, $"{name}-ps.spv");
            File.WriteAllBytes(path, pixelShader.Spirv);
            Console.WriteLine($"[{name}] pixel emit: success, {pixelShader.Spirv.Length} bytes -> {path}");
        }
        else
        {
            failures++;
            Console.WriteLine($"[{name}] pixel emit: FAILED ({pixelError})");
        }

        if (name == "mrt")
        {
            Gen5PixelOutputBinding[] invalidOutputs =
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(3, 7, Gen5PixelOutputKind.Float),
            ];
            if (Gen5SpirvTranslator.TryCompilePixelShader(state, evaluation, invalidOutputs, out _, out var invalidError))
            {
                failures++;
                Console.WriteLine("[mrt] FAILED: sparse host locations were accepted");
            }
            else
            {
                Console.WriteLine($"[mrt] sparse host locations rejected as expected ({invalidError})");
            }
        }
    }
}

Console.WriteLine(failures == 0
    ? "RESULT: all programs behaved as expected"
    : $"RESULT: {failures} unexpected outcome(s)");
Environment.ExitCode = failures == 0 ? 0 : 1;

internal sealed class FakeMemory : ICpuMemory
{
    private readonly List<(ulong Base, byte[] Data)> _regions = [];

    public void AddRegion(ulong baseAddress, uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        _regions.Add((baseAddress, bytes));
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        foreach (var (baseAddress, data) in _regions)
        {
            if (virtualAddress >= baseAddress &&
                virtualAddress + (ulong)destination.Length <= baseAddress + (ulong)data.Length)
            {
                data.AsSpan(
                    (int)(virtualAddress - baseAddress),
                    destination.Length).CopyTo(destination);
                return true;
            }
        }

        return false;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
}
