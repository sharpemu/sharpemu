// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed record ShaderCallLibrary(string Identity, IReadOnlyList<ShaderCallSite> Calls)
{
    public static ShaderCallLibrary Read(Gen5ShaderProgram caller, MemoryAccessTable memory,
        ResourceSnapshot snapshot, GuestWordReader read)
    {
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var calls = new List<ShaderCallSite>();
        var functions = new Dictionary<ulong, Gen5ShaderProgram>();
        foreach (var (call, index) in caller.Instructions.Select((instruction, index) => (instruction, index)))
        {
            if (call.Opcode != "SSwappcB64") continue;
            if (index < 6 || call.Sources.Count != 1 || call.Destinations.Count != 1 ||
                call.Sources[0].Kind != Gen5OperandKind.ScalarRegister || call.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                throw new InvalidOperationException("The shader call does not use a supported table record.");
            var loadIndex = index - 6;
            while (loadIndex >= 0 && caller.Instructions[loadIndex] is { Opcode: "VMovB32", Destinations.Count: 1 } vectorMove &&
                vectorMove.Destinations[0].Kind == Gen5OperandKind.VectorRegister) loadIndex--;
            if (loadIndex < 0) throw new InvalidOperationException("The shader call table load is missing.");
            var load = caller.Instructions[loadIndex];
            if (load.Opcode != "SBufferLoadDwordx4" || load.Control is not Gen5ScalarMemoryControl loadControl ||
                load.Destinations.Count != 4 || caller.Instructions[index - 5].Opcode != "SWaitcnt")
                throw new InvalidOperationException("The shader call table load is not supported.");
            if (loadControl.DynamicOffsetRegister is { } selector)
            {
                var scale = caller.Instructions.Take(loadIndex).Reverse().FirstOrDefault(instruction =>
                    instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal) || instruction.Opcode is "SBranch" or "SSwappcB64" ||
                    instruction.Destinations.Any(destination => destination.Kind == Gen5OperandKind.ScalarRegister &&
                        selector >= destination.Value && selector - destination.Value < instruction.DestinationWidth));
                if (scale is null || scale.Opcode != "SLshlB32" || scale.Destinations.Count != 1 || scale.Destinations[0] != Gen5Operand.Scalar(selector) ||
                    scale.Sources.Count != 2 || scale.Sources[1] != new Gen5Operand(Gen5OperandKind.EncodedConstant, 132))
                    throw new InvalidOperationException("The shader call table does not use 16-byte records.");
            }
            uint argumentRegister = 0;
            for (var component = 0; component < 4; component++)
            {
                var move = caller.Instructions[index - 4 + component];
                if (move.Opcode != "SMovB32" || move.Sources.Count != 1 || move.Destinations.Count != 1 ||
                    move.Sources[0] != load.Destinations[component] || move.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                    throw new InvalidOperationException("The shader call record has an unsupported register transfer.");
                if (component == 2) argumentRegister = move.Destinations[0].Value;
                var expected = component < 2 ? call.Sources[0].Value + (uint)component : argumentRegister + (uint)component - 2;
                if (move.Destinations[0].Value != expected)
                    throw new InvalidOperationException("The shader call record does not use register pairs.");
            }
            var access = memory.Find(load.Pc);
            if (access is null || access.Resource >= snapshot.Buffers.Length)
                throw new InvalidOperationException("The shader call table has no materialized buffer.");
            var descriptor = snapshot.Buffers[access.Resource];
            var address = descriptor[0] | ((ulong)(descriptor[1] & 0xFFFF) << 32);
            var stride = (descriptor[1] >> 16) & 0x3FFF;
            var size = (ulong)descriptor[2] * Math.Max(stride, 1u);
            if (size == 0 || size > 65536 || (size & 15) != 0 || (descriptor[1] & 0x80000000) != 0)
                throw new InvalidOperationException("The shader call table extent or layout is not supported.");
            var table = ShaderCallDump.ReadPrefix(address, (int)size, read);
            if ((ulong)table.Length != size) throw new InvalidOperationException("The shader call table is not CPU-readable at this submission.");
            identity.AppendData(table);
            var targets = new List<ShaderCallTarget>();
            var records = new HashSet<(ulong, ulong)>();
            var firstRecord = checked((int)loadControl.ImmediateOffsetBytes);
            if (firstRecord < 0 || (firstRecord & 15) != 0 || firstRecord > table.Length - 16)
                throw new InvalidOperationException("The shader call record is outside the table or is not aligned.");
            var recordEnd = loadControl.DynamicOffsetRegister.HasValue ? table.Length : firstRecord + 16;
            for (var offset = firstRecord; offset < recordEnd; offset += 16)
            {
                var target = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(offset));
                var argument = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(offset + 8));
                if (target == 0 || !records.Add((target, argument))) continue;
                if (!functions.TryGetValue(target, out var function))
                {
                    var result = ShaderFunctionReader.Read(target, read);
                    if (!result.Complete) throw new ShaderFunctionReadException(target, result);
                    function = result.Program;
                    identity.AppendData(result.Bytes);
                    functions.Add(target, function);
                }
                targets.Add(new(target, argument, function));
            }
            calls.Add(new(call.Pc, call.Sources[0].Value, argumentRegister, call.Destinations[0].Value,
                unchecked(caller.Address + call.ProgramOffset + (ulong)call.Words.Count * sizeof(uint)), targets));
        }
        identity.AppendData(BitConverter.GetBytes(caller.Address));
        return new(Convert.ToHexString(identity.GetHashAndReset()), calls);
    }
}
