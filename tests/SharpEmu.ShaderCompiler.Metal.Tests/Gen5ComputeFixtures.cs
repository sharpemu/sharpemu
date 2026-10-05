// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

internal sealed record Gen5ComputeFixture(
    string Name,
    uint[] Words,
    uint StoreScalarResourceBase,
    int StoreBackingBytes);

// Synthetic instruction programs for decoder and compile-request tests.
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
    // accumulating 5+4+3+2+1 = 15 into v1, with the result stored to buffer 0.
    // (s_sub_i32's SCC is signed overflow, so the loop condition needs an
    // explicit s_cmp_lg_u32 — which also exercises the scalar-compare path.)
    public static readonly Gen5ComputeFixture Loop = new(
        "loop",
        [
            0xBE800385,             // s_mov_b32 s0, 5
            0x7E020280,             // v_mov_b32 v1, 0
            // loop: (pc=0x8)
            0x4A020200,             // v_add_nc_u32 v1, s0, v1
            0x81808100,             // s_sub_i32 s0, s0, 1 (inline +1 = 0x81)
            0xBF078000,             // s_cmp_lg_u32 s0, 0
            0xBF85FFFC,             // s_cbranch_scc1 loop
            0xE0700000, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 16);

    // LDS round trip: write a literal through workgroup-shared memory, barrier,
    // read it back, and store it to buffer 0.
    public static readonly Gen5ComputeFixture Lds = new(
        "lds",
        [
            0x7E000280,             // v_mov_b32 v0, 0            (LDS byte address)
            0x7E0402FF, 0x00001234, // v_mov_b32 v2, 0x1234
            0xD8340000, 0x00000200, // ds_write_b32 v0, v2
            0xBF8A0000,             // s_barrier
            0xD8D80000, 0x03000000, // ds_read_b32 v3, v0
            0xE0700000, 0x80020300, // buffer_store_dword v3, off, s[8:11], 0
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 16);

    // Wave64 cross-lane: v_readfirstlane routes through the translator's
    // two-half threadgroup-scratch broadcast bridge. All lanes hold the same
    // value (42), so the broadcast result is 42 on every lane regardless of
    // which lane is "first" — the fixture's job is to prove the emitted wave64
    // MSL compiles and that a full 64-lane dispatch runs through the bridge
    // barriers without deadlocking, returning the broadcast value.
    public static readonly Gen5ComputeFixture Wave64Broadcast = new(
        "wave64-broadcast",
        [
            0x7E0002FF, 0x0000002A, // v_mov_b32 v0, 42
            0x7E000500,             // v_readfirstlane_b32 s0, v0
            0x7E020200,             // v_mov_b32 v1, s0
            0xE0700000, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 16);

    // Typed load: the instruction's two 32-bit floats win over the descriptor's four
    // unorm bytes in s11, so the two raw dwords at offset 0 land at offsets 16 and 20.
    public static readonly Gen5ComputeFixture TypedLoad = new(
        "typed-load",
        [
            0xBE8B03FF, 0x00038FAC, // s_mov_b32 s11, 0x38FAC (descriptor format 56, swizzle xyzw)
            0xEA010000, 0x80020200, // tbuffer_load_format_xy v[2:3], off, s[8:11], 0 format:64
            0xE0700010, 0x80020200, // buffer_store_dword v2, off, s[8:11], 0 offset:16
            0xE0700014, 0x80020300, // buffer_store_dword v3, off, s[8:11], 0 offset:20
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 32);

    // Typed store with format 8_8 unorm: 1.0 in v2 stores 0xFF at offset 16 and 0.5 in v3
    // stores 0x80 at offset 17; the other two bytes of that dword keep their value.
    public static readonly Gen5ComputeFixture TypedStore = new(
        "typed-store",
        [
            0xBE8B03FF, 0x00038FAC, // s_mov_b32 s11, 0x38FAC (descriptor format 56, swizzle xyzw)
            0x7E0402FF, 0x3F800000, // v_mov_b32 v2, 1.0
            0x7E0602FF, 0x3F000000, // v_mov_b32 v3, 0.5
            0xE8750010, 0x80020200, // tbuffer_store_format_xy v[2:3], off, s[8:11], 0 format:14 offset:16
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 32);

    public static readonly Gen5ComputeFixture[] All = [Fmac, Muls, ExecStore, Loop, Lds, TypedLoad, TypedStore];

    // Minimal pixel program: two interpolated attribute channels plus two
    // inline constants exported to MRT0 (done+vm).
    public static readonly uint[] PixelWords =
    [
        0xC8020002,             // v_interp_mov_f32 v0, p0, attr0.x
        0xC8060102,             // v_interp_mov_f32 v1, p0, attr0.y
        0x7E0402F2,             // v_mov_b32 v2, 1.0
        0x7E0602F0,             // v_mov_b32 v3, 0.5
        0xF800180F, 0x03020100, // exp mrt0 v0, v1, v2, v3 done vm
        0xBF810000,             // s_endpgm
    ];

    // Minimal vertex program: constant position export plus one param output.
    public static readonly uint[] VertexWords =
    [
        0x7E000280,             // v_mov_b32 v0, 0
        0x7E0202F2,             // v_mov_b32 v1, 1.0
        0xF80008CF, 0x01000000, // exp pos0 v0, v0, v0, v1 done
        0xF800020F, 0x01000101, // exp param0 v1, v1, v0, v1
        0xBF810000,             // s_endpgm
    ];

    public static Gen5MslShader CompileVertexOrThrow(int requiredVertexOutputCount = 0) =>
        CompileStageRequestOrThrow(VertexWords, ShaderStage.Vertex, requiredVertexOutputCount);

    public static Gen5MslShader CompilePixelOrThrow(
        Gen5PixelOutputKind outputKind = Gen5PixelOutputKind.Float,
        Gen5ColorComponentMapping componentMapping = default)
    {
        var program = DecodeOrThrow(new Gen5ComputeFixture("pixel", PixelWords, 0, 0));
        var request = RequestOrThrow(program, ShaderStage.Pixel,
            pixelOutputs: [new Gen5PixelOutputBinding(0, 0, outputKind, componentMapping)]);
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"[pixel] MSL request emit failed: {error}");
        }
        return shader;
    }

    // The request path: the fixture's own resource plan, default specialization and layout.
    private const ulong RequestHash = 0x5348_4152_5045_4D55;
    private const uint UserDataCount = 16;

    public static ShaderCompileRequest RequestOrThrow(Gen5ShaderProgram program, ShaderStage stage, uint waveLaneCount = 32, uint localSizeX = 32, int requiredVertexOutputCount = 0,
        IReadOnlyList<Gen5PixelOutputBinding>? pixelOutputs = null, ResourceRuntimeInputs? runtimeInputs = null)
    {
        var plan = ShaderResourcePlan.Extract(program, stage, RequestHash, 0, UserDataCount);
        var specialization = ResourceSpecialization.Default(plan.Info);
        if (runtimeInputs is not null)
        {
            var snapshot = new ResourceSnapshot();
            if (!ResourceMaterializer.Materialize(plan, runtimeInputs, ref snapshot, ref specialization, out var failure))
                throw new InvalidOperationException($"Fixture resource resolution failed: {failure}");
        }
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, UserDataCount),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        return new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveLaneCount,
            LocalSizeX = stage == ShaderStage.Compute ? localSizeX : 1,
            PixelOutputs = pixelOutputs ?? (stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : []),
            RequiredVertexOutputCount = requiredVertexOutputCount,
        };
    }

    public static Gen5MslShader CompileRequestOrThrow(Gen5ComputeFixture fixture, uint waveLaneCount = 32, uint localSizeX = 32)
    {
        var request = CreateComputeRequest(fixture, waveLaneCount, localSizeX);
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"[{fixture.Name}] MSL request emit failed: {error}");
        }

        return shader;
    }

    public static ShaderCompileRequest CreateComputeRequest(Gen5ComputeFixture fixture, uint waveLaneCount = 32, uint localSizeX = 32)
    {
        var program = DecodeOrThrow(fixture);
        var hasBufferDescriptor = program.Instructions.Any(instruction => instruction.Control is Gen5BufferMemoryControl);
        var userData = new uint[UserDataCount];
        if (hasBufferDescriptor && fixture.StoreBackingBytes != 0)
        {
            userData[fixture.StoreScalarResourceBase] = 0x200000;
            userData[fixture.StoreScalarResourceBase + 2] = (uint)fixture.StoreBackingBytes;
            userData[fixture.StoreScalarResourceBase + 3] = DescriptorConstants.IdentityDestinationSelect;
        }
        return RequestOrThrow(program, ShaderStage.Compute, waveLaneCount, localSizeX,
            runtimeInputs: hasBufferDescriptor ? new ResourceRuntimeInputs { UserData = userData } : null);
    }

    public static Gen5MslShader CompileStageRequestOrThrow(uint[] words, ShaderStage stage, int requiredVertexOutputCount = 0)
    {
        var memory = new FakeGuestMemory();
        memory.AddRegion(ProgramAddress, words);
        var ctx = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(ctx, ProgramAddress, out var program, out var decodeError))
        {
            throw new InvalidOperationException($"[{stage}] decode failed: {decodeError}");
        }

        var request = RequestOrThrow(program!, stage, requiredVertexOutputCount: requiredVertexOutputCount);
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"[{stage}] MSL request emit failed: {error}");
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
