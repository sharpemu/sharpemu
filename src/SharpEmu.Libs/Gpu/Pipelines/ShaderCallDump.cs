// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.Json;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class ShaderCallDump
{
    private const int MaximumTableBytes = 65536;
    private const int MaximumBuffers = 16;
    private const int MaximumTargets = 16;

    public static void Write(string? basePath, Gen5ShaderProgram program,
        ResourceSnapshot snapshot, MemoryAccessTable memory, GuestWordReader readCleanWord,
        ShaderFunctionReadException? functionFailure = null)
    {
        if (basePath is null || !program.Instructions.Any(instruction => instruction.Opcode == "SSwappcB64")) return;
        try
        {
            var buffers = new List<object>();
            var targets = new List<object>();
            var visited = new HashSet<ulong>();
            if (functionFailure is not null)
            {
                var failedPath = $"{basePath}.call-failed.bin";
                File.WriteAllBytes(failedPath, functionFailure.Result.Bytes);
                targets.Add(new { Buffer = -1, Offset = -1, Address = $"0x{functionFailure.Address:X16}",
                    CapturedBytes = functionFailure.Result.Bytes.Length, Path = failedPath,
                    Complete = false, Error = functionFailure.Result.Error });
                visited.Add(functionFailure.Address);
            }
            var callBuffers = FindCallBuffers(program, memory);
            var orderedBuffers = Enumerable.Range(0, snapshot.Buffers.Length)
                .OrderByDescending(index => callBuffers.Contains((uint)index)).Take(MaximumBuffers);
            foreach (var index in orderedBuffers)
            {
                var descriptor = snapshot.Buffers[index];
                if (descriptor.Length < 4) continue;
                var address = descriptor[0] | ((ulong)(descriptor[1] & 0xFFFF) << 32);
                var stride = (descriptor[1] >> 16) & 0x3FFF;
                var size = (ulong)descriptor[2] * Math.Max(stride, 1u);
                var bytes = ReadPrefix(address, (int)Math.Min(size, MaximumTableBytes), readCleanWord);
                var path = $"{basePath}.call-buffer-{index:D2}.bin";
                File.WriteAllBytes(path, bytes);
                buffers.Add(new { Index = index, Address = $"0x{address:X16}", Descriptor = descriptor,
                    Size = size, CapturedBytes = bytes.Length, Complete = (ulong)bytes.Length == size, Path = path });

                // These records are candidates only. Their selection can depend on GPU results.
                for (var offset = 0; callBuffers.Contains((uint)index) && offset + 16 <= bytes.Length && targets.Count < MaximumTargets; offset += 16)
                {
                    var target = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
                    if (target < 65536 || target >= (1ul << 48) || (target & 3) != 0 || !visited.Add(target)) continue;
                    var function = ShaderFunctionReader.Read(target, readCleanWord);
                    var code = function.Bytes;
                    if (code.Length == 0) continue;
                    var codePath = $"{basePath}.call-candidate-{targets.Count:D2}.bin";
                    File.WriteAllBytes(codePath, code);
                    targets.Add(new { Buffer = index, Offset = offset, Address = $"0x{target:X16}",
                        CapturedBytes = code.Length, Path = codePath, Complete = function.Complete, function.Error });
                }
            }

            var evidence = new
            {
                Phase = "shader_call_compile_failure",
                TimestampUtc = DateTime.UtcNow,
                CallPcs = program.Instructions.Where(instruction => instruction.Opcode == "SSwappcB64")
                    .Select(instruction => $"0x{instruction.Pc:X}").ToArray(),
                snapshot.UserData,
                CallBufferIndices = callBuffers.Order().ToArray(),
                Buffers = buffers,
                CandidateTargets = targets,
                BufferCount = snapshot.Buffers.Length,
                MaximumBuffers, MaximumTableBytes, MaximumTargets,
                Note = "Clean-memory snapshots only. Function captures stop at decoder termination or failure. Candidate targets are not confirmed calls. Each capture reports its decode status.",
            };
            var manifestPath = $"{basePath}.calls.json";
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"[SHADER][INFO] Shader call evidence saved: {manifestPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[SHADER][WARN] Cannot write shader call evidence: {exception.Message}");
        }
    }

    internal static HashSet<uint> FindCallBuffers(Gen5ShaderProgram program, MemoryAccessTable memory)
    {
        var result = new HashSet<uint>();
        for (var index = 0; index < program.Instructions.Count; index++)
        {
            var call = program.Instructions[index];
            if (call.Opcode != "SSwappcB64" || call.Sources.Count != 1 ||
                call.Sources[0].Kind != Gen5OperandKind.ScalarRegister) continue;
            var low = FindLoad(index, call.Sources[0].Value);
            var high = FindLoad(index, call.Sources[0].Value + 1);
            if (low is not { ComponentIndex: 0, Kind: MemoryResourceKind.ScalarBuffer } ||
                high is null || high.Pc != low.Pc || high.ComponentIndex != 1 ||
                low.Resource == MemoryAccessInfo.NoResource) continue;
            result.Add(low.Resource);
        }
        return result;

        MemoryAccessInfo? FindLoad(int before, uint register)
        {
            for (var index = before - 1; index >= 0; index--)
            {
                var instruction = program.Instructions[index];
                if (instruction.Opcode is "SSwappcB64" or "SSetpcB64" or "SBranch" or "SEndpgm" ||
                    instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal)) return null;
                for (var component = 0; component < instruction.Destinations.Count; component++)
                {
                    var destination = instruction.Destinations[component];
                    if (destination.Kind != Gen5OperandKind.ScalarRegister || register < destination.Value ||
                        register - destination.Value >= instruction.DestinationWidth) continue;
                    if (instruction.Opcode is "SMovB32" or "SMovB64" && instruction.Sources.Count == 1 &&
                        instruction.Sources[0].Kind == Gen5OperandKind.ScalarRegister)
                    {
                        register = instruction.Sources[0].Value + register - destination.Value;
                        break;
                    }
                    return instruction.Control is Gen5ScalarMemoryControl
                        ? memory.Find(instruction.Pc, (uint)component) : null;
                }
            }
            return null;
        }
    }

    internal static byte[] ReadPrefix(ulong address, int maximumBytes, GuestWordReader readWord)
    {
        var bytes = new byte[maximumBytes & ~3];
        var offset = 0;
        while (offset < bytes.Length && address <= ulong.MaxValue - (ulong)offset - 3 &&
            readWord(address + (ulong)offset, out var word))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), word);
            offset += 4;
        }
        return bytes.AsSpan(0, offset).ToArray();
    }
}
