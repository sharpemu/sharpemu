// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

internal static class GcnProgramRewriter
{
    internal delegate bool Expander(Gen5ShaderInstruction instruction, Action<Gen5ShaderInstruction> add, out string error);

    internal static (bool[] Scalar, bool[] Vector) UsedRegisters(IEnumerable<Gen5ShaderInstruction> instructions,
        bool spareHighScalars = false)
    {
        var scalarUsed = new bool[128];
        var vectorUsed = new bool[256];
        if (spareHighScalars)
        {
            Array.Fill(scalarUsed, true, 106, 2);
            Array.Fill(scalarUsed, true, 124, 4);
        }
        else
            Array.Fill(scalarUsed, true, 104, 24);
        for (var i = 0; i < 16; i++) scalarUsed[i] = true;
        foreach (var instruction in instructions)
        {
            foreach (var operand in instruction.Sources.Concat(instruction.Destinations))
            {
                var bank = operand.Kind == Gen5OperandKind.ScalarRegister ? scalarUsed
                    : operand.Kind == Gen5OperandKind.VectorRegister ? vectorUsed : null;
                if (bank is null) continue;
                for (var index = operand.Value; index < Math.Min((uint)bank.Length, operand.Value + instruction.DestinationWidth); index++) bank[index] = true;
            }
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } mask })
                for (var i = mask; i < Math.Min(mask + 2, 128u); i++) scalarUsed[i] = true;
            if (instruction.Opcode == "VCndmaskB32" && instruction.Sources.Count == 3 &&
                instruction.Sources[2] is { Kind: Gen5OperandKind.ScalarRegister } selector && selector.Value < 127)
                scalarUsed[selector.Value + 1] = true;
            if (instruction.Control is GcnStorageImageControl image)
            {
                for (var i = 0u; i < 8 && image.Resource + i < 128; i++) scalarUsed[image.Resource + i] = true;
                var addressCount = instruction.Opcode == "ImageLoadMip" ? 4u : 3u;
                for (var i = 0u; i < addressCount && image.Address + i < 256; i++) vectorUsed[image.Address + i] = true;
                for (var i = 0u; i < 4 && image.Data + i < 256; i++) vectorUsed[image.Data + i] = true;
            }
            if (instruction.Control is Gen5ImageControl sampled)
            {
                for (var i = 0u; i < 8 && sampled.ScalarResource + i < 128; i++) scalarUsed[sampled.ScalarResource + i] = true;
                for (var i = 0u; i < 4 && sampled.ScalarSampler + i < 128; i++) scalarUsed[sampled.ScalarSampler + i] = true;
                for (var i = 0u; i < 4 && sampled.VectorAddress + i < 256; i++) vectorUsed[sampled.VectorAddress + i] = true;
                foreach (var register in sampled.AddressRegisters)
                    if (register < 256) vectorUsed[register] = true;
                for (var i = 0u; i < 4 && sampled.VectorData + i < 256; i++) vectorUsed[sampled.VectorData + i] = true;
            }
            if (instruction.Control is Gen5BufferMemoryControl buffer)
                for (var i = 0u; i < 4 && buffer.ScalarResource + i < 128; i++) scalarUsed[buffer.ScalarResource + i] = true;
            if (instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal) && instruction.Sources.Count != 0)
                for (var i = 0u; i < 4 && instruction.Sources[0].Value + i < 128; i++) scalarUsed[instruction.Sources[0].Value + i] = true;
        }
        return (scalarUsed, vectorUsed);
    }

    internal static int FindFree(bool[] used, int count, int alignment = 4)
    {
        for (var start = 0; start <= used.Length - count; start += alignment)
            if (!used.AsSpan(start, count).Contains(true)) return start;
        return -1;
    }

    private const uint IslandSpacing = 0x10000;
    private const uint FarBranchBytes = 0x18000;
    private const uint BranchWord = 0xBF820000;

    internal static bool TryRewrite(Gen5ShaderProgram original, Expander expand, string overflowError,
        out Gen5ShaderProgram program, out string error)
    {
        program = original;
        error = "";
        var chunks = new List<List<Gen5ShaderInstruction>>();
        var chunkOf = new Dictionary<uint, int>();
        var branchTargets = new List<(int Chunk, uint Target)>();
        foreach (var instruction in original.Instructions)
        {
            chunkOf.Add(instruction.Pc, chunks.Count);
            var code = new List<Gen5ShaderInstruction>();
            if (expand(instruction, code.Add, out error))
            {
                if (error.Length != 0) return false;
            }
            else
            {
                if (instruction.Encoding == Gen5ShaderEncoding.Sopp &&
                    (instruction.Opcode == "SBranch" || instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal)))
                    branchTargets.Add((chunks.Count, (uint)((long)instruction.Pc + 4 + unchecked((short)instruction.Words[0]) * 4)));
                code.Add(instruction with { AddressOffset = instruction.ProgramOffset });
            }
            chunks.Add(code);
        }
        var branches = branchTargets.Select(branch => (branch.Chunk, Target: chunkOf[branch.Target])).ToArray();
        var sizes = chunks.Select(code => (uint)code.Sum(instruction => instruction.Words.Count * 4)).ToArray();
        var plain = new uint[chunks.Count + 1];
        for (var index = 0; index < chunks.Count; index++)
            plain[index + 1] = plain[index] + sizes[index];

        var islands = new List<int>();
        for (var index = 1; index < chunks.Count; index++)
            if (plain[index] / IslandSpacing != plain[index - 1] / IslandSpacing)
                islands.Add(index);
        var routes = new int[branches.Length][];
        var pads = new Dictionary<int, List<int>>();
        for (var index = 0; index < branches.Length; index++)
        {
            var (from, to) = branches[index];
            routes[index] = [];
            if (Math.Abs((long)plain[to] - plain[from] - 4) <= FarBranchBytes) continue;
            routes[index] = to > from
                ? islands.Where(island => island > from && island <= to).ToArray()
                : islands.Where(island => island > to && island <= from).Reverse().ToArray();
            foreach (var island in routes[index])
            {
                if (!pads.TryGetValue(island, out var list)) pads[island] = list = [];
                list.Add(index);
            }
        }

        var starts = new uint[chunks.Count];
        var padPcs = new Dictionary<(int Island, int Branch), uint>();
        uint pc = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            if (pads.TryGetValue(index, out var list))
            {
                pc += 4;
                foreach (var branch in list)
                {
                    padPcs[(index, branch)] = pc;
                    pc += 4;
                }
            }
            starts[index] = pc;
            pc += sizes[index];
        }

        uint Hop(int branch, int step) => step < routes[branch].Length
            ? padPcs[(routes[branch][step], branch)]
            : starts[branches[branch].Target];
        bool TryBranch(Gen5ShaderInstruction instruction, uint from, uint target, out Gen5ShaderInstruction patched)
        {
            var delta = ((long)target - from - instruction.Words.Count * 4) / 4;
            patched = instruction with
            {
                Pc = from,
                Words = [(instruction.Words[0] & 0xFFFF0000) | unchecked((ushort)(short)delta)],
            };
            return delta is >= short.MinValue and <= short.MaxValue;
        }

        var result = new List<Gen5ShaderInstruction>();
        var branchOfChunk = Enumerable.Range(0, branches.Length).ToDictionary(index => branches[index].Chunk);
        var jump = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBranch", [BranchWord], [], [], null);
        for (var index = 0; index < chunks.Count; index++)
        {
            if (pads.TryGetValue(index, out var list))
            {
                var guardPc = padPcs[(index, list[0])] - 4;
                if (!TryBranch(jump, guardPc, starts[index], out var guard)) { error = overflowError; return false; }
                result.Add(guard);
                foreach (var branch in list)
                {
                    var step = Array.IndexOf(routes[branch], index);
                    if (!TryBranch(jump, padPcs[(index, branch)], Hop(branch, step + 1), out var pad)) { error = overflowError; return false; }
                    result.Add(pad);
                }
            }
            pc = starts[index];
            foreach (var instruction in chunks[index])
            {
                if (branchOfChunk.TryGetValue(index, out var branch))
                {
                    if (!TryBranch(instruction, pc, Hop(branch, 0), out var patched)) { error = overflowError; return false; }
                    result.Add(patched);
                }
                else
                    result.Add(instruction with { Pc = pc });
                pc += (uint)instruction.Words.Count * 4;
            }
        }
        program = original with { Instructions = result };
        return true;
    }
}
