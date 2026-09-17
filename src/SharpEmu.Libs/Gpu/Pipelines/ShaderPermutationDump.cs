// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Keep each cache miss and module together so later variants cannot replace the evidence.
internal static class ShaderPermutationDump
{
    private static readonly string SessionName = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static long _sequence;

    public static Action<IndirectImageFailure>? CreateFailureCapture(ShaderSource source) =>
        CompiledShaderDump.ShouldWrite(source.Address) ? CreateEnabledFailureCapture(source) : null;

    // Keep captured shader state out of the disabled per-draw path.
    private static Action<IndirectImageFailure> CreateEnabledFailureCapture(ShaderSource source) =>
        failure => WriteIndirectImageFailure(source, failure);

    public static string? WriteInputs(
        ShaderSource source,
        ProgramKey key,
        bool sourceWasCached,
        IEnumerable<ProgramKey> sourceKeys,
        IReadOnlyList<ProgramPermutation> candidates,
        ResourceSpecialization specialization,
        ShaderCompileRequest request,
        uint pushDataCursor,
        ulong programId)
    {
        if (!CompiledShaderDump.ShouldWrite(source.Address)) return null;
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var basePath = $"{CompiledShaderDump.GetBasePath(source.Label, source.Address, source.Hash)}.{SessionName}.variant-{sequence:D6}";
            var comparisons = candidates.Select(candidate => new
            {
                ProgramId = candidate.Handle.Id,
                SpecializationMatches = candidate.Specialization.Equals(specialization),
                PushDataMatches = candidate.Bindings.PushDataStartDword == PushData.StartFor(pushDataCursor, candidate.Bindings.ShaderDataDwordCount),
                candidate.Bindings.PushDataStartDword,
                candidate.Bindings.ShaderDataDwordCount,
            }).ToArray();
            var previousKeys = sourceKeys.Where(previous =>
                previous.Stage == key.Stage && previous.Hash == key.Hash && !previous.Equals(key)).ToArray();
            var evidence = new
            {
                Phase = "before_translation",
                TimestampUtc = DateTime.UtcNow,
                ProgramId = programId,
                Stage = source.Label,
                ShaderAddress = $"0x{source.Address:X16}",
                ShaderHash = $"0x{source.Hash:X16}",
                SourceWasCached = sourceWasCached,
                MissReason = !sourceWasCached ? (previousKeys.Length == 0 ? "new_source" : "source_key_changed") : "permutation_mismatch",
                key.UserDataCount,
                key.CodeSize,
                StaticStateWords = key.StaticState,
                PreviousSourceKeys = previousKeys,
                Candidates = comparisons,
                Specialization = specialization,
                PushDataCursor = pushDataCursor,
                request.Bindings.PushDataStartDword,
                request.Bindings.ShaderDataDwordCount,
                request.Bindings.UsesDispatchThreadLimits,
                request.Bindings.DispatchThreadLimitsDword,
                request.LocalSizeX,
                request.LocalSizeY,
                request.LocalSizeZ,
                request.ThreadCountX,
                request.ThreadCountY,
                request.ThreadCountZ,
                request.WaveSize,
                request.ScratchDwords,
                InstructionCount = request.Program.Instructions.Count,
            };
            File.WriteAllText($"{basePath}.json", JsonSerializer.Serialize(evidence, JsonOptions));
            return basePath;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            Console.Error.WriteLine($"[SHADER][WARN] Cannot write the shader variant inputs: {exception.Message}");
            return null;
        }
    }

    public static void WriteIndirectImageFailure(ShaderSource source, IndirectImageFailure failure)
    {
        if (!CompiledShaderDump.ShouldWrite(source.Address)) return;
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var path = $"{CompiledShaderDump.GetBasePath(source.Label, source.Address, source.Hash)}.{SessionName}.resource-failure-{sequence:D6}.json";
            var evidence = new
            {
                Phase = "resource_materialization_failed",
                TimestampUtc = DateTime.UtcNow,
                Stage = source.Label,
                ShaderAddress = $"0x{source.Address:X16}",
                ShaderHash = $"0x{source.Hash:X16}",
                Failure = failure,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, JsonOptions));
            Console.Error.WriteLine($"[SHADER][INFO] Resource failure saved: {path}");
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            Console.Error.WriteLine($"[SHADER][WARN] Cannot write the resource failure: {exception.Message}");
        }
    }

    // Write before module creation, which can enter the driver compiler.
    public static void WriteModule(string? basePath, byte[] payload, string extension)
    {
        if (basePath is null || payload.Length == 0) return;
        try
        {
            File.WriteAllBytes($"{basePath}.{extension}", payload);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            Console.Error.WriteLine($"[SHADER][WARN] Cannot write the shader variant module: {exception.Message}");
        }
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
