// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

public enum FillWordSource
{
    UserData,
    BufferResource,
    Pointer,
    IndirectPointer,
}

public readonly record struct FillWord(FillWordSource Source, uint Register, int PointerOffset, int Offset);

public sealed record BoundedFill(
    uint GroupScalarRegister,
    FillWord Count,
    FillWord? Start,
    uint? ConstantValue,
    FillWord? Value,
    FillWord[] Destination,
    bool Formatted,
    FillWord[]? Pattern = null,
    FillWord? PatternLength = null);

public sealed record BoundedCopy(
    uint GroupScalarRegister,
    FillWord Count,
    FillWord Modulus,
    FillWord[] Source,
    FillWord[] Destination);

public static class BoundedFillDetector
{
    private static readonly string[] ModuloCopyOpcodes =
    [
        "VLshlAddU32", "SBufferLoadDword", "VCmpxGtU32", "SCbranchExecz", "SBufferLoadDword",
        "VCvtF32U32", "SCmpLgU32", "SCselectB64", "VRcpIflagF32", "VMulF32", "VCvtU32F32", "VMadU64U32", "VCmpNeU32", "VSubI32",
        "VCndmaskB32", "VMulHiU32", "VSubI32", "VAddI32", "VCndmaskB32", "VMulHiU32", "VMulLoU32", "VSubI32", "VCmpGeU32",
        "VCmpLeU32", "SAndB64", "VAddcU32", "VAddCoCiU32", "VCndmaskB32", "VMulLoU32", "VSubI32",
        "BufferLoadFormatX", "BufferStoreFormatX", "SEndpgm",
    ];

    public static BoundedCopy? DetectCopy(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions
            .Where(instruction => instruction.Opcode is not ("SWaitcnt" or "STtraceData" or "SInstPrefetch") &&
                !(instruction.Opcode == "SMovB32" && instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: MarkerRegister }]))
            .ToArray();
        if (!instructions.Select(instruction => instruction.Opcode).SequenceEqual(ModuloCopyOpcodes))
        {
            return null;
        }

        var countLoad = instructions[1];
        var modulusLoad = instructions[4];
        if (instructions[0].Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } index] ||
            instructions[0].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } group, { Kind: Gen5OperandKind.EncodedConstant, Value: Shift64 },
                { Kind: Gen5OperandKind.VectorRegister, Value: ThreadIndexRegister }] ||
            countLoad.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister } count] ||
            modulusLoad.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister } modulus] ||
            countLoad.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } constants, _] ||
            modulusLoad.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } modulusConstants, _] ||
            modulusConstants.Value != constants.Value ||
            instructions[2].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } limit, { Kind: Gen5OperandKind.VectorRegister } compared] ||
            limit.Value != count.Value || compared.Value != index.Value ||
            instructions[5].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } converted] || converted.Value != modulus.Value ||
            instructions[6].Sources is not [{ Kind: Gen5OperandKind.EncodedConstant, Value: ZeroConstant }, { Kind: Gen5OperandKind.ScalarRegister } tested] ||
            tested.Value != modulus.Value)
        {
            return null;
        }

        foreach (var at in new[] { 11, 20, 23, 28 })
        {
            if (instructions[at].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } multiplier, ..] || multiplier.Value != modulus.Value)
            {
                return null;
            }
        }

        var load = instructions[30];
        var store = instructions[31];
        if (instructions[29].Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } remainder] ||
            instructions[29].Sources is not [{ Kind: Gen5OperandKind.VectorRegister } dividend, { Kind: Gen5OperandKind.VectorRegister }] ||
            dividend.Value != index.Value ||
            load.Control is not Gen5BufferMemoryControl { DwordCount: 1, OffsetBytes: 0, IndexEnabled: true, OffsetEnabled: false, Typed: false } loadControl ||
            store.Control is not Gen5BufferMemoryControl { DwordCount: 1, OffsetBytes: 0, IndexEnabled: true, OffsetEnabled: false, Typed: false } storeControl ||
            load.Sources is not [_, _, { Kind: Gen5OperandKind.EncodedConstant, Value: ZeroConstant }] ||
            store.Sources is not [_, _, { Kind: Gen5OperandKind.EncodedConstant, Value: ZeroConstant }] ||
            loadControl.VectorAddress != remainder.Value || storeControl.VectorAddress != index.Value ||
            storeControl.VectorData != loadControl.VectorData)
        {
            return null;
        }

        var branch = instructions[3];
        if (branch.Pc + sizeof(uint) + (uint)((short)(branch.Words[0] & 0xFFFF) * sizeof(uint)) != instructions[^1].Pc)
        {
            return null;
        }

        var resourceRegisters = Enumerable.Range(0, 4)
            .SelectMany(word => new[] { loadControl.ScalarResource + (uint)word, storeControl.ScalarResource + (uint)word });
        var constantRegisters = Enumerable.Range(0, 4).Select(word => constants.Value + (uint)word);
        if (resourceRegisters.Any(WrittenScalars(instructions, instructions.Length).Contains) ||
            constantRegisters.Any(WrittenScalars(instructions, 4).Contains) ||
            WrittenScalars(instructions[5..29], 24).Contains(modulus.Value) ||
            countLoad.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: null, ImmediateOffsetBytes: >= 0 } countControl ||
            modulusLoad.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: null, ImmediateOffsetBytes: >= 0 } modulusControl)
        {
            return null;
        }

        FillWord User(uint register) => new(FillWordSource.UserData, register, 0, 0);
        return new BoundedCopy(
            group.Value,
            new FillWord(FillWordSource.BufferResource, constants.Value, 0, countControl.ImmediateOffsetBytes),
            new FillWord(FillWordSource.BufferResource, constants.Value, 0, modulusControl.ImmediateOffsetBytes),
            [.. Enumerable.Range(0, 4).Select(word => User(loadControl.ScalarResource + (uint)word))],
            [.. Enumerable.Range(0, 4).Select(word => User(storeControl.ScalarResource + (uint)word))]);
    }

    private const uint Shift64 = 134;
    private const uint ZeroConstant = 128;
    private const uint NullScalar = 125;
    private const uint MarkerRegister = 124;
    private const uint ThreadIndexRegister = 0;

    private enum VectorKind { GlobalIndex, OffsetIndex, Constant, Word }

    private readonly record struct VectorValue(VectorKind Kind, uint Constant = 0, FillWord Word = default);

    private static readonly string[] PatternFillOpcodes =
    [
        "VLshlAddU32", "VCmpxGtU32", "SCbranchExecz",
        "VCvtF32U32", "SCmpLgU32", "SCselectB64", "VRcpIflagF32", "VMulF32", "VCvtU32F32", "VMadU64U32", "VCmpNeU32", "VSubI32",
        "VCndmaskB32", "VMulHiU32", "VSubI32", "VAddI32", "VCndmaskB32", "VMulHiU32", "VMulLoU32", "VSubI32", "VCmpLeU32", "VCmpGeU32",
        "SAndB64", "VAddcU32", "VAddCoCiU32", "VCndmaskB32", "VMulLoU32", "VSubI32",
        "VCmpNeI32", "SAndSaveexecB64", "SCbranchExecz", "VCmpNeI32", "SAndSaveexecB64", "SCbranchExecz", "VCmpNeI32", "SAndSaveexecB64", "SCbranchExecz",
        "VMovB32", "BufferStoreFormatX", "SAndn2B64", "SCbranchExecz",
        "VMovB32", "BufferStoreFormatX", "SMovB64", "SAndn2B64", "SCbranchExecz",
        "VMovB32", "BufferStoreFormatX", "SMovB64", "SAndn2B64", "SCbranchExecz",
        "VMovB32", "BufferStoreFormatX", "SEndpgm",
    ];

    public static BoundedFill? Detect(Gen5ShaderProgram program) => DetectRange(program) ?? DetectPattern(program);

    private static BoundedFill? DetectPattern(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions
            .Where(instruction => instruction.Opcode is not ("SWaitcnt" or "STtraceData" or "SInstPrefetch") &&
                !(instruction.Opcode == "SMovB32" && instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: MarkerRegister }]))
            .ToArray();
        if (!instructions.Select(instruction => instruction.Opcode).SequenceEqual(PatternFillOpcodes))
        {
            return null;
        }

        if (instructions[0].Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } index] ||
            instructions[0].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } group, { Kind: Gen5OperandKind.EncodedConstant, Value: Shift64 },
                { Kind: Gen5OperandKind.VectorRegister, Value: ThreadIndexRegister }] ||
            instructions[1].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } count, { Kind: Gen5OperandKind.VectorRegister } compared] ||
            compared.Value != index.Value ||
            instructions[3].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } length] ||
            instructions[4].Sources is not [{ Kind: Gen5OperandKind.EncodedConstant, Value: ZeroConstant }, { Kind: Gen5OperandKind.ScalarRegister } lengthTest] ||
            lengthTest.Value != length.Value)
        {
            return null;
        }

        foreach (var at in new[] { 9, 18, 26 })
        {
            if (instructions[at].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } multiplier, ..] || multiplier.Value != length.Value)
            {
                return null;
            }
        }

        if (instructions[20].Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } bound, _] || bound.Value != length.Value)
        {
            return null;
        }

        var stores = new[] { 38, 42, 47, 52 };
        var patternRegisters = new uint[stores.Length];
        uint? resource = null;
        for (var position = 0; position < stores.Length; position++)
        {
            var move = instructions[stores[position] - 1];
            var store = instructions[stores[position]];
            if (move.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } data] ||
                move.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } pattern] ||
                store.Control is not Gen5BufferMemoryControl { DwordCount: 1, OffsetBytes: 0, IndexEnabled: true, OffsetEnabled: false, Typed: false } control ||
                store.Sources is not [_, _, { Kind: Gen5OperandKind.EncodedConstant, Value: ZeroConstant }] ||
                control.VectorAddress != index.Value || control.VectorData != data.Value || (resource is { } previous && previous != control.ScalarResource))
            {
                return null;
            }

            resource = control.ScalarResource;
            patternRegisters[stores.Length - 1 - position] = pattern.Value;
        }

        foreach (var (branch, target) in PatternFillBranches)
        {
            var instruction = instructions[branch];
            if (instruction.Pc + sizeof(uint) + (uint)((short)(instruction.Words[0] & 0xFFFF) * sizeof(uint)) != instructions[target].Pc)
            {
                return null;
            }
        }

        const int LastLengthUse = 26;
        var resourceRegisters = Enumerable.Range(0, 4).Select(word => resource!.Value + (uint)word).ToArray();
        if (WrittenScalars(instructions, LastLengthUse).Contains(length.Value) ||
            patternRegisters.Concat(resourceRegisters).Any(WrittenScalars(instructions, instructions.Length).Contains))
        {
            return null;
        }

        FillWord User(uint register) => new(FillWordSource.UserData, register, 0, 0);
        return new BoundedFill(
            group.Value,
            User(count.Value),
            null,
            null,
            User(patternRegisters[0]),
            [.. Enumerable.Range(0, 4).Select(word => User(resource!.Value + (uint)word))],
            true,
            [.. patternRegisters.Select(User)],
            User(length.Value));
    }

    private static readonly (int Branch, int Target)[] PatternFillBranches =
        [(2, 53), (30, 49), (33, 44), (36, 39), (40, 43), (45, 48), (50, 53)];

    private static HashSet<uint> WrittenScalars(Gen5ShaderInstruction[] instructions, int count)
    {
        var written = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            var instruction = instructions[index];
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    continue;
                }

                written.Add(destination.Value);
                if (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) || instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
                {
                    written.Add(destination.Value + 1);
                }
            }
        }

        return written;
    }

    private static BoundedFill? DetectRange(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions;
        if (instructions.Count == 0 || instructions[^1].Opcode != "SEndpgm")
        {
            return null;
        }

        var scalars = new Dictionary<uint, FillWord?>();
        var vectors = new Dictionary<uint, VectorValue>();
        uint? group = null;
        FillWord? count = null;
        BoundedFill? fill = null;
        var endPc = instructions[^1].Pc;
        for (var index = 0; index < instructions.Count - 1; index++)
        {
            var instruction = instructions[index];
            switch (instruction.Opcode)
            {
                case "SWaitcnt" or "STtraceData" or "SInstPrefetch":
                    break;
                case "SMovB32" when instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: MarkerRegister }]:
                    break;
                case "VLshlAddU32":
                    if (fill is not null || group is not null ||
                        instruction.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } shifted] ||
                        instruction.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } groupRegister,
                            { Kind: Gen5OperandKind.EncodedConstant, Value: Shift64 }, { Kind: Gen5OperandKind.VectorRegister, Value: ThreadIndexRegister }] ||
                        scalars.ContainsKey(groupRegister.Value) || vectors.ContainsKey(ThreadIndexRegister))
                    {
                        return null;
                    }

                    group = groupRegister.Value;
                    vectors[shifted.Value] = new VectorValue(VectorKind.GlobalIndex);
                    break;
                case "SBufferLoadDword" or "SLoadDword" or "SLoadDwordx2" or "SLoadDwordx4":
                    if (!TryLoad(instruction, scalars))
                    {
                        return null;
                    }

                    break;
                case "VCmpxGtU32":
                    if (count is not null || fill is not null ||
                        instruction.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } limit, { Kind: Gen5OperandKind.VectorRegister } compared] ||
                        !vectors.TryGetValue(compared.Value, out var comparedValue) || comparedValue.Kind != VectorKind.GlobalIndex ||
                        ReadScalar(scalars, limit.Value) is not { } limitWord || limitWord.Source == FillWordSource.UserData)
                    {
                        return null;
                    }

                    count = limitWord;
                    if (index + 1 < instructions.Count && instructions[index + 1].Opcode == "SCbranchExecz")
                    {
                        var branch = instructions[index + 1];
                        var target = branch.Pc + sizeof(uint) + (uint)((short)(branch.Words[0] & 0xFFFF) * sizeof(uint));
                        if (target != endPc)
                        {
                            return null;
                        }

                        index++;
                    }

                    break;
                case "VAddI32" or "VAddU32" or "VAddNcU32":
                    if (instruction.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } sum] ||
                        instruction.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } addend, { Kind: Gen5OperandKind.VectorRegister } indexed] ||
                        !vectors.TryGetValue(indexed.Value, out var indexedValue) || indexedValue.Kind != VectorKind.GlobalIndex ||
                        ReadScalar(scalars, addend.Value) is not { } start || start.Source == FillWordSource.UserData)
                    {
                        return null;
                    }

                    vectors[sum.Value] = new VectorValue(VectorKind.OffsetIndex, Word: start);
                    break;
                case "VMovB32":
                    if (instruction.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } moved] || instruction.Sources is not [var source])
                    {
                        return null;
                    }

                    if (source.Kind == Gen5OperandKind.EncodedConstant && source.Value is >= ZeroConstant and <= 192)
                    {
                        vectors[moved.Value] = new VectorValue(VectorKind.Constant, source.Value - ZeroConstant);
                    }
                    else if (source.Kind == Gen5OperandKind.LiteralConstant)
                    {
                        vectors[moved.Value] = new VectorValue(VectorKind.Constant, source.Value);
                    }
                    else if (source.Kind == Gen5OperandKind.ScalarRegister && ReadScalar(scalars, source.Value) is { } word && word.Source != FillWordSource.UserData)
                    {
                        vectors[moved.Value] = new VectorValue(VectorKind.Word, Word: word);
                    }
                    else
                    {
                        return null;
                    }

                    break;
                case "BufferStoreFormatX" or "BufferStoreDword":
                    if (fill is not null || count is not { } limitCount || group is not { } groupValue ||
                        instruction.Control is not Gen5BufferMemoryControl { DwordCount: 1, OffsetBytes: 0, IndexEnabled: true, OffsetEnabled: false, Typed: false } store ||
                        instruction.Sources is not [_, _, { Kind: Gen5OperandKind.EncodedConstant, Value: ZeroConstant }] ||
                        !vectors.TryGetValue(store.VectorAddress, out var address) || address.Kind is not (VectorKind.GlobalIndex or VectorKind.OffsetIndex) ||
                        !vectors.TryGetValue(store.VectorData, out var data) || data.Kind is not (VectorKind.Constant or VectorKind.Word) ||
                        !TryReadDescriptor(scalars, store.ScalarResource, out var destination))
                    {
                        return null;
                    }

                    fill = new BoundedFill(
                        groupValue,
                        limitCount,
                        address.Kind == VectorKind.OffsetIndex ? address.Word : null,
                        data.Kind == VectorKind.Constant ? data.Constant : null,
                        data.Kind == VectorKind.Word ? data.Word : null,
                        destination,
                        instruction.Opcode == "BufferStoreFormatX");
                    break;
                default:
                    return null;
            }
        }

        return fill;
    }

    private static FillWord? ReadScalar(Dictionary<uint, FillWord?> scalars, uint register) =>
        scalars.TryGetValue(register, out var word) ? word : new FillWord(FillWordSource.UserData, register, 0, 0);

    private static bool TryLoad(Gen5ShaderInstruction instruction, Dictionary<uint, FillWord?> scalars)
    {
        if (instruction.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: null } control || control.ImmediateOffsetBytes < 0 ||
            instruction.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } resource, { Kind: Gen5OperandKind.EncodedConstant, Value: NullScalar or ZeroConstant }] ||
            instruction.Destinations.Count != (int)control.DestinationCount || instruction.Destinations.Count == 0)
        {
            return false;
        }

        FillWord first;
        if (instruction.Opcode == "SBufferLoadDword")
        {
            for (var word = 0u; word < 4; word++)
            {
                if (scalars.ContainsKey(resource.Value + word))
                {
                    return false;
                }
            }

            first = new FillWord(FillWordSource.BufferResource, resource.Value, 0, control.ImmediateOffsetBytes);
        }
        else if (!scalars.ContainsKey(resource.Value) && !scalars.ContainsKey(resource.Value + 1))
        {
            first = new FillWord(FillWordSource.Pointer, resource.Value, 0, control.ImmediateOffsetBytes);
        }
        else if (ReadScalar(scalars, resource.Value) is { Source: FillWordSource.Pointer } low &&
                 ReadScalar(scalars, resource.Value + 1) is { Source: FillWordSource.Pointer } high &&
                 high.Register == low.Register && high.Offset == low.Offset + sizeof(uint))
        {
            first = new FillWord(FillWordSource.IndirectPointer, low.Register, low.Offset, control.ImmediateOffsetBytes);
        }
        else
        {
            return false;
        }

        for (var word = 0; word < instruction.Destinations.Count; word++)
        {
            if (instruction.Destinations[word] is not { Kind: Gen5OperandKind.ScalarRegister } destination)
            {
                return false;
            }

            scalars[destination.Value] = first with { Offset = first.Offset + word * sizeof(uint) };
        }

        return true;
    }

    private static bool TryReadDescriptor(Dictionary<uint, FillWord?> scalars, uint register, out FillWord[] words)
    {
        words = new FillWord[4];
        for (var word = 0u; word < 4; word++)
        {
            if (ReadScalar(scalars, register + word) is not { } value)
            {
                return false;
            }

            words[word] = value;
        }

        var first = words[0];
        for (var word = 1; word < 4; word++)
        {
            var expected = first.Source == FillWordSource.UserData
                ? first with { Register = first.Register + (uint)word }
                : first with { Offset = first.Offset + word * sizeof(uint) };
            if (words[word] != expected)
            {
                return false;
            }
        }

        return true;
    }
}
