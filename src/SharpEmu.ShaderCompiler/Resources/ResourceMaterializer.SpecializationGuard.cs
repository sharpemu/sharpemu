// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public static partial class ResourceMaterializer
{
    // A published guard owns its data. Callers receive a separate specialization.
    internal sealed class SpecializationGuard
    {
        internal uint[][] Buffers = [];
        internal uint[][] Images = [];
        internal uint[][] Samplers = [];
        internal int[] SamplerSources = [];
        internal ResourceSpecialization Specialization = new();
        internal bool PortableBuffers;
    }

    private static uint BufferGuardFlags(ShaderResourcePlan plan, int index) =>
        0xC0E00000u | (plan.Info.Buffers[index].Formatted ? 0x7FFFFu : 0u);

    private static SpecializationGuard? CaptureGuard(ShaderResourcePlan plan, MaterializedSnapshot snapshot)
    {
        if (plan.RequiresSpecializationMemory || plan.IndirectImages.Count != 0 || plan.BufferCandidateTables.Count != 0 ||
            snapshot.IndirectImages.Count != 0 || snapshot.BufferCandidateTables.Count != 0 ||
            plan.DescriptorSources.Any(source => source.IndirectImage is not null) ||
            plan.Info.Images.Any(image => image.IndirectRoot != DescriptorConstants.NoIndex) ||
            snapshot.Buffers.Length != plan.Info.Buffers.Count || snapshot.Images.Length != plan.Info.Images.Count ||
            snapshot.Samplers.Length != plan.Info.Samplers.Count ||
            snapshot.Buffers.Any(words => words.Length != 4 || words[3] >> 30 != 0)) return null;
        return new SpecializationGuard
        {
            Buffers = snapshot.Buffers.Select((words, index) => new[] { words[1] & 0xBFFF0000u, words[3] & BufferGuardFlags(plan, index) }).ToArray(),
            Images = snapshot.Images.Select(words => words.ToArray()).ToArray(),
            Samplers = snapshot.Samplers.Select(words => words.ToArray()).ToArray(),
        };
    }

    private static bool MatchesGuard(ShaderResourcePlan plan, MaterializedSnapshot snapshot, SpecializationGuard guard,
        bool checkSamplers = true)
    {
        if (snapshot.Buffers.Length != guard.Buffers.Length || snapshot.Images.Length != guard.Images.Length ||
            snapshot.IndirectImages.Count != 0 || snapshot.BufferCandidateTables.Count != 0) return false;
        for (var index = 0; index < guard.Buffers.Length; index++)
        {
            var words = snapshot.Buffers[index];
            if (words.Length != 4 || (words[1] & 0xBFFF0000u) != guard.Buffers[index][0] ||
                (words[3] & BufferGuardFlags(plan, index)) != guard.Buffers[index][1]) return false;
        }
        for (var index = 0; index < guard.Images.Length; index++)
            if (!snapshot.Images[index].AsSpan().SequenceEqual(guard.Images[index])) return false;
        if (!checkSamplers) return true;
        if (snapshot.Samplers.Length != guard.Samplers.Length) return false;
        for (var index = 0; index < guard.Samplers.Length; index++)
            if (!snapshot.Samplers[index].AsSpan().SequenceEqual(guard.Samplers[index])) return false;
        return true;
    }

    private static void ApplyGuard(MaterializedSnapshot snapshot, SpecializationGuard guard)
    {
        if (guard.SamplerSources.Length == snapshot.Samplers.Length) return;
        var samplers = new uint[guard.SamplerSources.Length][];
        for (var index = 0; index < samplers.Length; index++)
            samplers[index] = snapshot.Samplers[guard.SamplerSources[index]];
        snapshot.Samplers = samplers;
    }
}
