// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Hand-built instruction records for the resource-plan tests, one helper per shape.
internal static class ResourceTestProgram
{
    public const uint NullOperand = 125;
    public const ulong Hash = 0x1234_5678_9ABC_DEF0;

    private static Gen5Operand Literal(uint value) => new(Gen5OperandKind.LiteralConstant, value);

    private static Gen5Operand Immediate(uint value) => Gen5Operand.Source(128 + value);

    public static Gen5Operand Operand(uint value) =>
        value <= 64 ? Immediate(value) : Literal(value);

    public static Gen5ShaderInstruction EndProgram(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);

    public static Gen5ShaderInstruction Nop(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sopp, "SNop", [0xBF800000], [], [], null);

    public static Gen5ShaderInstruction Branch(uint pc, string opcode, short wordOffset) =>
        new(pc, Gen5ShaderEncoding.Sopp, opcode, [unchecked((uint)(ushort)wordOffset)], [], [], null);

    public static Gen5ShaderInstruction MoveScalar(uint pc, uint destination, uint value) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SMovB32", [0u], [Operand(value)], [Gen5Operand.Scalar(destination)], null);

    public static Gen5ShaderInstruction MoveScalarRegister(uint pc, uint destination, uint source) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SMovB32", [0u], [Gen5Operand.Scalar(source)], [Gen5Operand.Scalar(destination)], null);

    public static Gen5ShaderInstruction Sop1(uint pc, string opcode, uint destination, Gen5Operand source) =>
        new(pc, Gen5ShaderEncoding.Sop1, opcode, [0u], [source], [Gen5Operand.Scalar(destination)], null);

    public static Gen5ShaderInstruction Sop2(uint pc, string opcode, uint destination, Gen5Operand source0, Gen5Operand source1) =>
        new(pc, Gen5ShaderEncoding.Sop2, opcode, [0u], [source0, source1], [Gen5Operand.Scalar(destination)], null);

    public static Gen5ShaderInstruction Sopc(uint pc, string opcode, Gen5Operand source0, Gen5Operand source1) =>
        new(pc, Gen5ShaderEncoding.Sopc, opcode, [0u], [source0, source1], [], null);

    public static Gen5ShaderInstruction Vop1(uint pc, string opcode, uint destination, Gen5Operand source) =>
        new(pc, Gen5ShaderEncoding.Vop1, opcode, [0u], [source], [Gen5Operand.Vector(destination)], null);

    public static Gen5ShaderInstruction Vop2(uint pc, string opcode, uint destination, Gen5Operand source0, Gen5Operand source1) =>
        new(pc, Gen5ShaderEncoding.Vop2, opcode, [0u], [source0, source1], [Gen5Operand.Vector(destination)], null);

    public static Gen5ShaderInstruction Vopc(uint pc, string opcode, Gen5Operand source0, uint vectorSource1) =>
        new(pc, Gen5ShaderEncoding.Vopc, opcode, [0u], [source0, Gen5Operand.Vector(vectorSource1)], [], null);

    public static Gen5ShaderInstruction Vop3(uint pc, string opcode, uint destination, params Gen5Operand[] sources) =>
        new(pc, Gen5ShaderEncoding.Vop3, opcode, [0u, 0u], sources, [Gen5Operand.Vector(destination)],
            new Gen5Vop3Control(0, 0, 0, false, 0, null));

    public static Gen5ShaderInstruction ReadFirstLane(uint pc, uint scalarDestination, uint vectorSource) =>
        new(pc, Gen5ShaderEncoding.Vop1, "VReadfirstlaneB32", [0u], [Gen5Operand.Vector(vectorSource)], [Gen5Operand.Scalar(scalarDestination)], null);

    public static Gen5ShaderInstruction WriteLane(uint pc, uint vectorRegister, uint scalarRegister, uint lane) =>
        new(pc, Gen5ShaderEncoding.Vop3, "VWritelaneB32", [0u, 0u],
            [Gen5Operand.Scalar(scalarRegister), Immediate(lane), Gen5Operand.Scalar(0)], [Gen5Operand.Vector(vectorRegister)], null);

    public static Gen5ShaderInstruction ReadLane(uint pc, uint scalarRegister, uint vectorRegister, uint lane) =>
        new(pc, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
            [Gen5Operand.Vector(vectorRegister), Immediate(lane), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(scalarRegister)], null);

    // s_load_dwordxN through the address pair at baseRegister.
    public static Gen5ShaderInstruction ScalarLoad(uint pc, uint baseRegister, uint destination, uint count = 1, int immediateOffset = 0, uint? dynamicOffsetRegister = null) =>
        new(pc, Gen5ShaderEncoding.Smem, ScalarLoadName("SLoadDword", count), [0u, 0u],
            [Gen5Operand.Scalar(baseRegister), dynamicOffsetRegister is { } register ? Gen5Operand.Scalar(register) : Gen5Operand.Source(NullOperand)],
            Enumerable.Range((int)destination, (int)count).Select(index => Gen5Operand.Scalar((uint)index)).ToArray(),
            new Gen5ScalarMemoryControl(count, immediateOffset, dynamicOffsetRegister));

    // s_buffer_load_dwordxN through the descriptor at resourceRegister.
    public static Gen5ShaderInstruction ScalarBufferLoad(uint pc, uint resourceRegister, uint destination, uint count = 1, int immediateOffset = 0, uint? dynamicOffsetRegister = null) =>
        new(pc, Gen5ShaderEncoding.Smem, ScalarLoadName("SBufferLoadDword", count), [0u, 0u],
            [Gen5Operand.Scalar(resourceRegister), dynamicOffsetRegister is { } register ? Gen5Operand.Scalar(register) : Gen5Operand.Source(NullOperand)],
            Enumerable.Range((int)destination, (int)count).Select(index => Gen5Operand.Scalar((uint)index)).ToArray(),
            new Gen5ScalarMemoryControl(count, immediateOffset, dynamicOffsetRegister));

    private static string ScalarLoadName(string prefix, uint count) => count == 1 ? prefix : $"{prefix}x{count}";

    public static Gen5ShaderInstruction BufferAccess(uint pc, string opcode, uint resourceRegister, int offset = 0, uint dwords = 1, uint vectorData = 4, bool indexEnabled = false, bool offsetEnabled = false, uint vectorAddress = 0)
    {
        var isStore = opcode.Contains("Store", StringComparison.Ordinal) || opcode.Contains("Atomic", StringComparison.Ordinal);
        var data = Enumerable.Range((int)vectorData, (int)dwords).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
        return new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Mubuf, opcode, [0u, 0u],
            isStore ? [Gen5Operand.Vector(vectorAddress), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), .. data]
                : [Gen5Operand.Vector(vectorAddress), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand)],
            isStore ? [] : data,
            new Gen5BufferMemoryControl(dwords, vectorAddress, vectorData, resourceRegister, offset, indexEnabled, offsetEnabled, Glc: false, Slc: false));
    }

    public static Gen5ShaderInstruction BufferLoad(uint pc, uint resourceRegister, int offset = 0, uint dwords = 1, bool formatted = false) =>
        BufferAccess(pc, formatted ? FormatName("BufferLoadFormat", dwords) : DwordName("BufferLoadDword", dwords), resourceRegister, offset, dwords);

    public static Gen5ShaderInstruction BufferStore(uint pc, uint resourceRegister, int offset = 0, uint dwords = 1, bool formatted = false) =>
        BufferAccess(pc, formatted ? FormatName("BufferStoreFormat", dwords) : DwordName("BufferStoreDword", dwords), resourceRegister, offset, dwords);

    public static Gen5ShaderInstruction BufferAtomicAdd(uint pc, uint resourceRegister, int offset = 0) =>
        BufferAccess(pc, "BufferAtomicAdd", resourceRegister, offset);

    private static string FormatName(string prefix, uint dwords) => prefix + dwords switch { 1 => "X", 2 => "Xy", 3 => "Xyz", _ => "Xyzw" };

    private static string DwordName(string prefix, uint dwords) => dwords == 1 ? prefix : $"{prefix}x{dwords}";

    // global_load/store through the scalar address pair, or a flat access through v[address:address+1].
    public static Gen5ShaderInstruction GlobalAccess(uint pc, string opcode, uint scalarAddress, int offset = 0, uint vectorAddress = 0, uint dwords = 1)
    {
        var flat = opcode.StartsWith("Flat", StringComparison.Ordinal);
        var isStore = opcode.Contains("Store", StringComparison.Ordinal);
        Gen5Operand[] addresses = flat
            ? [Gen5Operand.Vector(vectorAddress), Gen5Operand.Vector(vectorAddress + 1)]
            : [Gen5Operand.Vector(vectorAddress), Gen5Operand.Scalar(scalarAddress)];
        var data = Enumerable.Range(8, (int)dwords).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
        return new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Flat, opcode, [0u, 0u],
            isStore ? [.. addresses, .. data] : addresses,
            isStore ? [] : data,
            new Gen5GlobalMemoryControl(dwords, vectorAddress, 8, 8, flat ? uint.MaxValue : scalarAddress, offset, Glc: false, Slc: false, flat));
    }

    public static Gen5ShaderInstruction Image(uint pc, string opcode, uint resourceRegister, uint samplerRegister = 0, uint dimension = 1, uint dmask = 0xF, uint vectorAddress = 0, bool r128 = false)
    {
        var addressRegisters = new uint[] { vectorAddress, vectorAddress + 1, vectorAddress + 2 };
        var control = new Gen5ImageControl(dmask, vectorAddress, addressRegisters, 4, resourceRegister, samplerRegister, dimension,
            dimension is 4 or 5 or 7, Glc: false, Slc: false, A16: false, D16: false);
        var sources = new List<Gen5Operand> { Gen5Operand.Vector(vectorAddress), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Scalar(samplerRegister) };
        var destinations = opcode.StartsWith("ImageStore", StringComparison.Ordinal) ? Array.Empty<Gen5Operand>() : [Gen5Operand.Vector(4)];
        return new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Mimg, opcode, [r128 ? 1u << 15 : 0u, 0u], sources, destinations, control);
    }

    public static Gen5ShaderInstruction DataShareWrite(uint pc, bool gds) =>
        new(pc, Gen5ShaderEncoding.Ds, "DsWriteB32", [gds ? 1u << 17 : 0u, 0u], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [],
            new Gen5DataShareControl(0, 0, gds));

    // Any DS instruction: sources first (address, data...), then the vector destinations.
    public static Gen5ShaderInstruction DataShare(uint pc, string opcode, bool gds, Gen5Operand[] sources, uint[] destinations, uint offset0 = 0, uint offset1 = 0) =>
        new(pc, Gen5ShaderEncoding.Ds, opcode, [gds ? 1u << 17 : 0u, 0u], sources, destinations.Select(Gen5Operand.Vector).ToArray(),
            new Gen5DataShareControl(offset0, offset1, gds));

    // A global or flat access with explicit data registers: a load writes destination,
    // a store or atomic reads source (an atomic with glc also writes destination).
    public static Gen5ShaderInstruction GlobalMemory(uint pc, string opcode, uint scalarAddress, uint vectorAddress, uint destination, uint source, int offset = 0, uint dwords = 1, bool glc = false)
    {
        var flat = opcode.StartsWith("Flat", StringComparison.Ordinal);
        var noScalarAddress = flat || scalarAddress >= NullOperand;
        var isStore = opcode.Contains("Store", StringComparison.Ordinal) || opcode.Contains("Atomic", StringComparison.Ordinal);
        Gen5Operand[] addresses = noScalarAddress
            ? [Gen5Operand.Vector(vectorAddress), Gen5Operand.Vector(vectorAddress + 1)]
            : [Gen5Operand.Vector(vectorAddress), Gen5Operand.Scalar(scalarAddress)];
        var data = Enumerable.Range((int)source, (int)dwords).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
        var results = Enumerable.Range((int)destination, (int)dwords).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
        return new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Flat, opcode, [0u, 0u],
            isStore ? [.. addresses, .. data] : addresses,
            isStore && !glc ? [] : results,
            new Gen5GlobalMemoryControl(dwords, vectorAddress, source, destination, noScalarAddress ? NullOperand : scalarAddress, offset, Glc: glc, Slc: false, flat));
    }

    public static Gen5ShaderInstruction MoveVector(uint pc, uint destination, uint value) =>
        Vop1(pc, "VMovB32", destination, Operand(value));

    public static Gen5ShaderInstruction MoveVectorFromScalar(uint pc, uint destination, uint scalarRegister) =>
        Vop1(pc, "VMovB32", destination, Gen5Operand.Scalar(scalarRegister));

    public static Gen5ShaderProgram Program(params Gen5ShaderInstruction[] instructions) => new(0, instructions);

    public static ShaderResourcePlan Extract(Gen5ShaderProgram program, uint userDataBase = 0, uint userDataCount = 64, ShaderStage stage = ShaderStage.Compute) =>
        ShaderResourcePlan.Extract(program, stage, Hash, userDataBase, userDataCount);

    // The plan, its default specialization applied, and the layout of one program.
    public static (ShaderResourcePlan Plan, SpecializedResourceInfo Resources, BindingLayout Layout) Prepare(
        Gen5ShaderProgram program, ShaderStage stage = ShaderStage.Compute, uint userDataBase = 0, uint userDataCount = 64, uint pushDataStartDword = 0)
    {
        var plan = ShaderResourcePlan.Extract(program, stage, Hash, userDataBase, userDataCount);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, userDataBase, userDataCount),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            pushDataStartDword);
        return (plan, resources, layout);
    }

    public static ShaderCompileRequest Request(Gen5ShaderProgram program, ShaderStage stage = ShaderStage.Compute, uint userDataBase = 0, uint userDataCount = 64, uint pushDataStartDword = 0)
    {
        var (plan, resources, layout) = Prepare(program, stage, userDataBase, userDataCount, pushDataStartDword);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : [],
        };
    }

    public static ResourceRuntimeInputs Inputs(uint[] userData, GuestWordReader? readMemory = null, GuestWordReader? readCleanMemory = null, ulong shaderBase = 0) =>
        new() { UserData = userData, ReadMemory = readMemory, ReadCleanMemory = readCleanMemory, ShaderBase = shaderBase };
}

// A small dword-addressed memory image whose reads can be counted and refused.
internal sealed class TestWordMemory
{
    public ulong Base { get; init; } = 0x1000;
    public uint[] Words { get; init; } = new uint[8];
    public uint Reads { get; set; }
    public uint FailAfter { get; set; } = uint.MaxValue;
    public ulong FailAddress { get; set; } = ulong.MaxValue;
    public bool RequireAlignment { get; init; }

    public bool Read(ulong address, out uint value)
    {
        value = 0;
        if (address < Base || address - Base >= (ulong)Words.Length * sizeof(uint) || Reads >= FailAfter ||
            address == FailAddress || (RequireAlignment && (address & 3) != 0))
        {
            return false;
        }

        value = Words[(address - Base) / sizeof(uint)];
        Reads++;
        return true;
    }

    public ref uint At(ulong address) => ref Words[(address - Base) / sizeof(uint)];
}
