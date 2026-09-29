// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class ShaderImageDescriptorDump
{
    private const int MaximumAccesses = 4096;
    private const int MaximumMemoryWords = 65536;

    internal sealed record ShaderImageAccessRecord(uint Pc, string Opcode, int HandleId, string ResourceClass,
        ImageDimension Dimension, bool DepthCompare, bool R128, bool DynamicStorageMip,
        uint?[] Words, uint?[] SamplerWords);
    internal sealed record MemoryWord(string Address, uint? Word);
    internal sealed record CaptureResult(IReadOnlyList<ShaderImageAccessRecord> Accesses, IReadOnlyList<MemoryWord> Memory,
        int ResolvedAccesses, int UniqueDescriptorWords, int UniqueImageBindings,
        bool AccessLimitReached, bool MemoryLimitReached);

    public static void Write(ShaderSource source, ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        if (!CompiledShaderDump.ShouldWrite(source.Address, source.Hash)) return;
        try
        {
            var capture = Capture(plan, inputs);
            var path = CompiledShaderDump.GetBasePath(source.Label, source.Address, source.Hash) + ".linked-images.json";
            File.WriteAllText(path, JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"[SHADER][INFO] Linked image evidence saved: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[SHADER][WARN] Cannot write linked image evidence: {exception.Message}");
        }
    }

    internal static CaptureResult Capture(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var memory = new Dictionary<ulong, uint?>();
        var memoryLimitReached = false;
        bool Read(ulong address, out uint word)
        {
            if (!memory.TryGetValue(address, out var cached))
            {
                if (memory.Count == MaximumMemoryWords)
                {
                    memoryLimitReached = true;
                    word = 0;
                    return false;
                }
                cached = inputs.ReadCleanMemory is { } reader && reader(address, out var value) ? value : null;
                memory.Add(address, cached);
            }
            word = cached ?? 0;
            return cached.HasValue;
        }
        var evaluator = new RuntimeValueEvaluator(plan, inputs.WithReader(Read));
        uint?[] Evaluate(ScalarValue? handle, int width)
        {
            var words = new uint?[width];
            if (handle is null || handle.Operands.Length != width) return words;
            for (var index = 0; index < width; index++)
                if (evaluator.Evaluate(handle.Operands[index], out var word)) words[index] = word;
            return words;
        }

        var accesses = new List<ShaderImageAccessRecord>();
        var descriptorKeys = new HashSet<string>();
        var bindingKeys = new HashSet<string>();
        var resolved = 0;
        var accessLimitReached = false;
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var access = plan.Memory[index];
            if (access.PlanningOnly || access.ImageClass == ImageResourceClass.None) continue;
            if (accesses.Count == MaximumAccesses) { accessLimitReached = true; break; }
            var binding = plan.Accesses[index];
            var words = Evaluate(binding?.Handle, 8);
            var depth = (access.ImageSampleFlags & ImageSampleFlags.Compare) != 0;
            var mip = access.ImageClass == ImageResourceClass.Storage && access.ImageHasMip;
            accesses.Add(new(access.Pc, access.Opcode, binding?.Handle?.Id ?? 0, access.ImageClass.ToString(),
                access.ImageDimension, depth, access.ImageR128, mip, words,
                access.NeedsSampler ? Evaluate(binding?.SamplerHandle, 4) : []));
            if (words.All(word => word.HasValue))
            {
                resolved++;
                var key = string.Join(',', words.Select(word => word!.Value.ToString("X8")));
                descriptorKeys.Add(key);
                bindingKeys.Add($"{key}:{access.ImageClass}:{access.ImageDimension}:{depth}:{access.ImageR128}:{mip}");
            }
        }
        return new(accesses, memory.Select(pair => new MemoryWord($"0x{pair.Key:X16}", pair.Value)).ToArray(),
            resolved, descriptorKeys.Count, bindingKeys.Count, accessLimitReached, memoryLimitReached);
    }
}
