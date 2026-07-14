// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Synthetic-shader conformance dumper.
//
// Feeds hand-assembled Gen5 (gfx10) instruction words through the real
// decode -> SPIR-V pipeline (Gen5ShaderTranslator / Gen5SpirvTranslator, via
// reflection so no emulator source changes are required) and writes the
// resulting vertex and compute SPIR-V blobs to disk. The blobs can then be
// checked with spirv-val / spirv-dis.
//
// Programs that contain buffer_store_dword automatically get a single
// global-memory binding covering every store, which the emitter exposes as
// guestBuffers[0] (descriptor set 0, binding 0).
//
// Each program carries an expectation: ExpectTranslate=true programs must
// decode and emit both stages; ExpectTranslate=false programs pin a decode
// failure that must stay loud. Any unexpected outcome makes the tool exit
// non-zero, so it can gate scripts/CI.
//
// Usage: SharpEmu.Tools.ShaderDump [output-directory]

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.CxxAbi;

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
    // that stores work again after EXEC is restored (offset 16).
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
        0xBF810000,             // s_endpgm
    ]),
];

var assembly = typeof(CxaGuardExports).Assembly;
var shaderTranslator = assembly.GetType("SharpEmu.Libs.Agc.Gen5ShaderTranslator")
    ?? throw new InvalidOperationException("Gen5ShaderTranslator not found");
var spirvTranslator = assembly.GetType("SharpEmu.Libs.Agc.Gen5SpirvTranslator")
    ?? throw new InvalidOperationException("Gen5SpirvTranslator not found");
var describe = shaderTranslator.GetMethod(
    "Describe",
    BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("Gen5ShaderTranslator.Describe not found");
var tryDecode = shaderTranslator.GetMethod(
    "TryDecodeProgram",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Gen5ShaderTranslator.TryDecodeProgram not found");
var stateType = assembly.GetType("SharpEmu.Libs.Agc.Gen5ShaderState")
    ?? throw new InvalidOperationException("Gen5ShaderState not found");
var evaluationType = assembly.GetType("SharpEmu.Libs.Agc.Gen5ShaderEvaluation")
    ?? throw new InvalidOperationException("Gen5ShaderEvaluation not found");
var imageBindingType = assembly.GetType("SharpEmu.Libs.Agc.Gen5ImageBinding")
    ?? throw new InvalidOperationException("Gen5ImageBinding not found");
var globalBindingType = assembly.GetType("SharpEmu.Libs.Agc.Gen5GlobalMemoryBinding")
    ?? throw new InvalidOperationException("Gen5GlobalMemoryBinding not found");
var tryCompile = spirvTranslator.GetMethod(
    "TryCompileVertexShader",
    BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("Gen5SpirvTranslator.TryCompileVertexShader not found");
var tryCompileCompute = spirvTranslator.GetMethod(
    "TryCompileComputeShader",
    BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("Gen5SpirvTranslator.TryCompileComputeShader not found");

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
        (string)describe.Invoke(null, [ctx, ProgramAddress, ProgramAddress])!);

    object?[] decodeArgs = [ctx, ProgramAddress, null, null];
    if (!(bool)tryDecode.Invoke(null, decodeArgs)!)
    {
        if (expectTranslate)
        {
            failures++;
            Console.WriteLine($"[{name}] FAILED: decode error ({decodeArgs[3]})");
        }
        else
        {
            Console.WriteLine($"[{name}] decode failed as expected ({decodeArgs[3]})");
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
    var programObj = decodeArgs[2]!;
    var instructions = (System.Collections.IEnumerable)programObj
        .GetType().GetProperty("Instructions")!.GetValue(programObj)!;
    var storePcs = new List<uint>();
    foreach (var instruction in instructions)
    {
        var op = (string)instruction.GetType().GetProperty("Opcode")!.GetValue(instruction)!;
        if (op.StartsWith("BufferStore", StringComparison.Ordinal))
        {
            storePcs.Add((uint)instruction.GetType().GetProperty("Pc")!.GetValue(instruction)!);
        }
    }

    // The binding's scalar base (8 -> s[8:11]) must match the srsrc field of
    // the hand-assembled buffer_store words, and the 64-byte backing store
    // must cover every hand-assembled store offset.
    var globalBindings = Array.CreateInstance(globalBindingType, storePcs.Count > 0 ? 1 : 0);
    if (storePcs.Count > 0)
    {
        globalBindings.SetValue(
            Activator.CreateInstance(
                globalBindingType,
                8u,
                0UL,
                (IReadOnlyList<uint>)storePcs,
                new byte[64]),
            0);
    }

    var state = Activator.CreateInstance(
        stateType,
        programObj,
        new uint[16],
        null,
        null,
        0u)!;
    var evaluation = Activator.CreateInstance(
        evaluationType,
        new uint[256],
        new uint[256],
        new Dictionary<uint, IReadOnlyList<uint>>(),
        Array.CreateInstance(imageBindingType, 0),
        globalBindings,
        null,
        null,
        null)!;

    object?[] compileArgs = [state, evaluation, null, null, 0, -1, 0];
    if ((bool)tryCompile.Invoke(null, compileArgs)!)
    {
        var shader = compileArgs[2]!;
        var spirv = (byte[])shader.GetType().GetProperty("Spirv")!.GetValue(shader)!;
        var path = Path.Combine(outputDirectory, $"{name}.spv");
        File.WriteAllBytes(path, spirv);
        Console.WriteLine($"[{name}] emit: success, {spirv.Length} bytes -> {path}");
    }
    else
    {
        failures++;
        Console.WriteLine($"[{name}] emit: FAILED ({compileArgs[3]})");
    }

    object?[] computeArgs = [state, evaluation, 1u, 1u, 1u, null, null];
    if ((bool)tryCompileCompute.Invoke(null, computeArgs)!)
    {
        var shader = computeArgs[5]!;
        var spirv = (byte[])shader.GetType().GetProperty("Spirv")!.GetValue(shader)!;
        var path = Path.Combine(outputDirectory, $"{name}-cs.spv");
        File.WriteAllBytes(path, spirv);
        Console.WriteLine($"[{name}] compute emit: success, {spirv.Length} bytes -> {path}");
    }
    else
    {
        failures++;
        Console.WriteLine($"[{name}] compute emit: FAILED ({computeArgs[6]})");
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
