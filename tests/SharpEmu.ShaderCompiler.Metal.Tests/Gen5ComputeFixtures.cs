// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

internal sealed record Gen5ComputeFixture(
    string Name,
    uint[] Words,
    uint StoreScalarResourceBase,
    int StoreBackingBytes);

/// <summary>
/// Hand-assembled Gen5 (gfx10) programs plus the decode -> (state, evaluation) -> MSL
/// pipeline the tests drive. Programs are synthetic by construction — shader binaries
/// captured from games are copyrighted content and must never land in fixtures.
/// </summary>
internal static class Gen5ComputeFixtures
{
    public const ulong ProgramAddress = 0x100000;

    // Straight-line ALU: VOP2 fmac + fmamk/fmaak literals + the VOP3 fmac form.
    public static readonly Gen5ComputeFixture Fmac = new(
        "fmac",
        [
            0x560A0501,             // v_fmac_f32 v5, v1, v2
            0x580A0501, 0x42280000, // v_fmamk_f32 v5, v1, 42.0, v2
            0x5A0A0501, 0x42280000, // v_fmaak_f32 v5, v1, v2, 42.0
            0xD52B0005, 0x00020501, // v_fmac_f32_e64 v5, v1, v2
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 0,
        StoreBackingBytes: 0);

    // VOP3 integer multiplies, low and high halves, signed and unsigned.
    public static readonly Gen5ComputeFixture Muls = new(
        "muls",
        [
            0xD5690005, 0x00020501, // v_mul_lo_u32 v5, v1, v2
            0xD56A0005, 0x00020501, // v_mul_hi_u32 v5, v1, v2
            0xD56B0005, 0x00020501, // v_mul_lo_i32 v5, v1, v2
            0xD56C0005, 0x00020501, // v_mul_hi_i32 v5, v1, v2
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 0,
        StoreBackingBytes: 0);

    // End-to-end executable program: real ALU results stored to buffer 0 at dword
    // offsets 0/4/8, then proof that a store with EXEC=0 does not land (offset 12
    // keeps its sentinel) and that stores work again once EXEC is restored (16).
    public static readonly Gen5ComputeFixture ExecStore = new(
        "exec-store",
        [
            0xBFA10001,             // s_clause 0x1 (scheduling hint)
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
            0xE070000C, 0x80020200, // buffer_store_dword v2 offset:12 (masked, must not land)
            0xBEFE03C1,             // s_mov_b32 exec_lo, -1      -> lane active again
            0xE0700010, 0x80020000, // buffer_store_dword v0 offset:16
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 64);

    // Control flow through the PC dispatcher: a scalar loop counting down from 5,
    // accumulating into v1, with the result stored to buffer 0.
    public static readonly Gen5ComputeFixture Loop = new(
        "loop",
        [
            0xBE800385,             // s_mov_b32 s0, 5
            0x7E020280,             // v_mov_b32 v1, 0
            // loop: (pc=0x8)
            0x4A020200,             // v_add_nc_u32 v1, s0, v1
            0x81808100,             // s_sub_i32 s0, s0, 1 (inline +1 = 0x81)
            0xBF85FFFD,             // s_cbranch_scc1 loop
            0xE0700000, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 16);

    public static readonly Gen5ComputeFixture[] All = [Fmac, Muls, ExecStore, Loop];

    /// <summary>Drives the real decoder and the MSL emitter for one fixture.</summary>
    public static Gen5MslShader CompileOrThrow(Gen5ComputeFixture fixture)
    {
        var program = DecodeOrThrow(fixture);

        // Buffer stores need a global-memory binding; the emitter resolves them
        // by instruction PC, so collect memory-access PCs from the decoded
        // program itself.
        var accessPcs = new List<uint>();
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is Gen5BufferMemoryControl or Gen5GlobalMemoryControl)
            {
                accessPcs.Add(instruction.Pc);
            }
        }

        var globalBindings = accessPcs.Count != 0
            ? new[]
            {
                new Gen5GlobalMemoryBinding(
                    fixture.StoreScalarResourceBase,
                    0UL,
                    accessPcs,
                    new byte[Math.Max(fixture.StoreBackingBytes, 4)],
                    Math.Max(fixture.StoreBackingBytes, 4),
                    DataPooled: false)
                {
                    Writable = true,
                },
            }
            : Array.Empty<Gen5GlobalMemoryBinding>();

        var state = new Gen5ShaderState(program, new uint[16], Metadata: null);
        var evaluation = new Gen5ShaderEvaluation(
            new uint[128],
            new uint[128],
            Array.Empty<Gen5ImageBinding>(),
            globalBindings);

        if (!Gen5MslTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out var shader,
                out var compileError))
        {
            throw new InvalidOperationException($"[{fixture.Name}] MSL emit failed: {compileError}");
        }

        return shader;
    }

    public static Gen5ShaderProgram DecodeOrThrow(Gen5ComputeFixture fixture)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ProgramAddress, fixture.Words);
        var ctx = new CpuContext(memory, Generation.Gen5);

        if (!Gen5ShaderTranslator.TryDecodeProgram(ctx, ProgramAddress, out var program, out var decodeError))
        {
            throw new InvalidOperationException($"[{fixture.Name}] decode failed: {decodeError}");
        }

        return program!;
    }
}
