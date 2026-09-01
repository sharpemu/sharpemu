// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Collections.Concurrent;

namespace SharpEmu.ShaderCompiler;

public enum Gen5ShaderEvaluationStage
{
    Unknown,
    Vertex,
    Pixel,
    Compute,
}

internal enum Gen5GlobalMemoryReadKind
{
    UnknownSize,
    DescriptorSized,
}

internal enum Gen5GlobalMemoryReadSource
{
    None,
    PhysicalMemory,
    Fallback,
}

internal readonly record struct Gen5EvaluationInputSignature(
    Gen5ShaderEvaluationStage Stage,
    ulong ProgramAddress,
    uint ShaderChecksum,
    uint UserDataScalarRegisterBase,
    ulong UserDataHash,
    ulong ComputeRegisterHash,
    bool ResolveVertexInputs,
    ulong RequiredVertexRecordCount,
    bool CaptureVertexInputsOnly);

internal readonly record struct Gen5EvaluationPlanSignature(
    Gen5EvaluationInputSignature Input,
    ulong PlanHash);

internal static class Gen5ShaderEvaluationProfile
{
    private const int ReportIntervalSeconds = 5;
    private const double BytesPerMebibyte = 1024.0 * 1024.0;

    private static readonly bool _detailedEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SHADER_EVALUATION"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _enabled =
        _detailedEnabled ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);
    private static readonly long[] _evaluationCalls = new long[4];
    private static readonly long[] _evaluationFailures = new long[4];
    private static readonly long[] _evaluationTicks = new long[4];
    private static readonly long[] _readCalls = new long[2];
    private static readonly long[] _readFailures = new long[2];
    private static readonly long[] _readBytes = new long[2];
    private static readonly long[] _readTicks = new long[2];
    private static readonly long[] _sourceCalls = new long[3];
    private static readonly long[] _sourceBytes = new long[3];
    private static readonly long[] _sizeBucketCalls = new long[5];
    private static ConcurrentDictionary<Gen5EvaluationInputSignature, long> _inputSignatures = [];
    private static ConcurrentDictionary<Gen5EvaluationPlanSignature, long> _planSignatures = [];
    private static long _maximumReadBytes;
    private static long _lastReportTimestamp = Stopwatch.GetTimestamp();
    private static long _nextReportTimestamp =
        Stopwatch.GetTimestamp() + (Stopwatch.Frequency * ReportIntervalSeconds);

    public static bool Enabled => _enabled;

    // The unified profile records timings and memory traffic. Full input and
    // plan hashes are opt-in because hashing every binding can perturb a draw-
    // heavy workload enough to obscure the timing being measured.
    public static bool DetailedEnabled => _detailedEnabled;

    public static Gen5EvaluationInputSignature CreateInputSignature(
        Gen5ShaderEvaluationStage stage,
        Gen5ShaderState state,
        bool resolveVertexInputs,
        uint? requiredVertexRecordCount,
        bool captureVertexInputsOnly)
    {
        var userDataHash = StartHash();
        userDataHash = AddHash(userDataHash, (uint)state.UserData.Count);
        foreach (var value in state.UserData)
        {
            userDataHash = AddHash(userDataHash, value);
        }

        var computeRegisterHash = StartHash();
        if (state.ComputeSystemRegisters is { } registers)
        {
            computeRegisterHash = AddNullableHash(computeRegisterHash, registers.WorkGroupXRegister);
            computeRegisterHash = AddNullableHash(computeRegisterHash, registers.WorkGroupYRegister);
            computeRegisterHash = AddNullableHash(computeRegisterHash, registers.WorkGroupZRegister);
            computeRegisterHash = AddNullableHash(computeRegisterHash, registers.ThreadGroupSizeRegister);
        }
        else
        {
            computeRegisterHash = AddHash(computeRegisterHash, uint.MaxValue);
        }

        return new Gen5EvaluationInputSignature(
            stage,
            state.Program.Address,
            state.ShaderChecksum,
            state.UserDataScalarRegisterBase,
            userDataHash,
            computeRegisterHash,
            resolveVertexInputs,
            requiredVertexRecordCount ?? ulong.MaxValue,
            captureVertexInputsOnly);
    }

    public static void RecordSignature(
        Gen5EvaluationInputSignature input,
        Gen5ShaderEvaluation evaluation)
    {
        if (!_detailedEnabled)
        {
            return;
        }

        _inputSignatures.AddOrUpdate(input, 1, static (_, count) => count + 1);
        var plan = new Gen5EvaluationPlanSignature(input, ComputePlanHash(evaluation));
        _planSignatures.AddOrUpdate(plan, 1, static (_, count) => count + 1);
    }

    public static void RecordEvaluation(
        Gen5ShaderEvaluationStage stage,
        long elapsedTicks,
        bool succeeded)
    {
        if (!_enabled)
        {
            return;
        }

        var index = (int)stage;
        Interlocked.Increment(ref _evaluationCalls[index]);
        Interlocked.Add(ref _evaluationTicks[index], elapsedTicks);
        if (!succeeded)
        {
            Interlocked.Increment(ref _evaluationFailures[index]);
        }

        TryReport();
    }

    public static void RecordMemoryRead(
        Gen5GlobalMemoryReadKind kind,
        Gen5GlobalMemoryReadSource source,
        int bytes,
        long elapsedTicks,
        bool succeeded)
    {
        if (!_enabled)
        {
            return;
        }

        var kindIndex = (int)kind;
        Interlocked.Increment(ref _readCalls[kindIndex]);
        Interlocked.Add(ref _readTicks[kindIndex], elapsedTicks);
        if (!succeeded)
        {
            Interlocked.Increment(ref _readFailures[kindIndex]);
            return;
        }

        Interlocked.Add(ref _readBytes[kindIndex], bytes);
        Interlocked.Increment(ref _sourceCalls[(int)source]);
        Interlocked.Add(ref _sourceBytes[(int)source], bytes);
        Interlocked.Increment(ref _sizeBucketCalls[GetSizeBucket(bytes)]);
        UpdateMaximum(ref _maximumReadBytes, bytes);
    }

    private static int GetSizeBucket(int bytes) => bytes switch
    {
        <= 4 * 1024 => 0,
        <= 64 * 1024 => 1,
        <= 1024 * 1024 => 2,
        <= 4 * 1024 * 1024 => 3,
        _ => 4,
    };

    private static void UpdateMaximum(ref long location, long value)
    {
        var observed = Volatile.Read(ref location);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref location, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private static void TryReport()
    {
        var now = Stopwatch.GetTimestamp();
        var deadline = Volatile.Read(ref _nextReportTimestamp);
        if (now < deadline ||
            Interlocked.CompareExchange(
                ref _nextReportTimestamp,
                now + (Stopwatch.Frequency * ReportIntervalSeconds),
                deadline) != deadline)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _lastReportTimestamp, now);
        var seconds = Math.Max(
            (now - previous) / (double)Stopwatch.Frequency,
            double.Epsilon);

        var calls = ExchangeAll(_evaluationCalls);
        var failures = ExchangeAll(_evaluationFailures);
        var ticks = ExchangeAll(_evaluationTicks);
        var readCalls = ExchangeAll(_readCalls);
        var readFailures = ExchangeAll(_readFailures);
        var readBytes = ExchangeAll(_readBytes);
        var readTicks = ExchangeAll(_readTicks);
        var sourceCalls = ExchangeAll(_sourceCalls);
        var sourceBytes = ExchangeAll(_sourceBytes);
        var buckets = ExchangeAll(_sizeBucketCalls);
        var maximum = Interlocked.Exchange(ref _maximumReadBytes, 0);
        var inputSignatures = Interlocked.Exchange(
            ref _inputSignatures,
            new ConcurrentDictionary<Gen5EvaluationInputSignature, long>());
        var planSignatures = Interlocked.Exchange(
            ref _planSignatures,
            new ConcurrentDictionary<Gen5EvaluationPlanSignature, long>());

        Console.Error.WriteLine(
            $"[PERF][SHADER_EVAL] {seconds:F1}s " +
            $"vs={FormatStage(calls, failures, ticks, Gen5ShaderEvaluationStage.Vertex)} " +
            $"ps={FormatStage(calls, failures, ticks, Gen5ShaderEvaluationStage.Pixel)} " +
            $"cs={FormatStage(calls, failures, ticks, Gen5ShaderEvaluationStage.Compute)} " +
            $"other={FormatStage(calls, failures, ticks, Gen5ShaderEvaluationStage.Unknown)}");
        Console.Error.WriteLine(
            $"[PERF][SHADER_MEMORY] " +
            $"unknown={FormatReads(readCalls, readFailures, readBytes, readTicks, Gen5GlobalMemoryReadKind.UnknownSize)} " +
            $"sized={FormatReads(readCalls, readFailures, readBytes, readTicks, Gen5GlobalMemoryReadKind.DescriptorSized)} " +
            $"rate_mib_s={(readBytes.Sum() / BytesPerMebibyte) / seconds:F1} " +
            $"source=pvm:{sourceCalls[(int)Gen5GlobalMemoryReadSource.PhysicalMemory]}/" +
            $"{sourceBytes[(int)Gen5GlobalMemoryReadSource.PhysicalMemory] / BytesPerMebibyte:F1}MiB," +
            $"fallback:{sourceCalls[(int)Gen5GlobalMemoryReadSource.Fallback]}/" +
            $"{sourceBytes[(int)Gen5GlobalMemoryReadSource.Fallback] / BytesPerMebibyte:F1}MiB " +
            $"buckets=4K:{buckets[0]},64K:{buckets[1]},1M:{buckets[2]},4M:{buckets[3]},large:{buckets[4]} " +
            $"max_mib={maximum / BytesPerMebibyte:F1}");
        if (_detailedEnabled)
        {
            Console.Error.WriteLine(FormatSignatureReport(inputSignatures, planSignatures));
        }
    }

    private static string FormatSignatureReport(
        IReadOnlyDictionary<Gen5EvaluationInputSignature, long> inputSignatures,
        IReadOnlyDictionary<Gen5EvaluationPlanSignature, long> planSignatures)
    {
        var calls = inputSignatures.Values.Sum();
        var repeatedInputs = Math.Max(0, calls - inputSignatures.Count);
        var reusablePlans = Math.Max(0, calls - planSignatures.Count);
        var planCountsByInput = planSignatures.Keys
            .GroupBy(static signature => signature.Input)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var variantInputs = planCountsByInput.Count(static pair => pair.Value > 1);
        var top = inputSignatures
            .GroupBy(static pair => (pair.Key.Stage, pair.Key.ProgramAddress))
            .Select(group =>
            {
                var shaderCalls = group.Sum(static pair => pair.Value);
                var shaderInputs = group.Count();
                var shaderPlans = planSignatures.Keys.Count(plan =>
                    plan.Input.Stage == group.Key.Stage &&
                    plan.Input.ProgramAddress == group.Key.ProgramAddress);
                var shaderReusable = Math.Max(0, shaderCalls - shaderPlans);
                return new
                {
                    group.Key.Stage,
                    group.Key.ProgramAddress,
                    Calls = shaderCalls,
                    Inputs = shaderInputs,
                    Plans = shaderPlans,
                    Reusable = shaderReusable,
                };
            })
            .OrderByDescending(static value => value.Reusable)
            .ThenByDescending(static value => value.Calls)
            .Take(6)
            .Select(value =>
                $"{FormatStageName(value.Stage)}@0x{value.ProgramAddress:X}:" +
                $"n{value.Calls}/i{value.Inputs}/p{value.Plans}/" +
                $"reuse{FormatPercent(value.Reusable, value.Calls)}")
            .ToArray();

        return
            $"[PERF][SHADER_REPEAT] calls={calls} inputs={inputSignatures.Count} " +
            $"input_repeat={FormatPercent(repeatedInputs, calls)} " +
            $"plans={planSignatures.Count} plan_reuse={FormatPercent(reusablePlans, calls)} " +
            $"variant_inputs={variantInputs} top=[{string.Join(',', top)}]";
    }

    private static string FormatStageName(Gen5ShaderEvaluationStage stage) => stage switch
    {
        Gen5ShaderEvaluationStage.Vertex => "vs",
        Gen5ShaderEvaluationStage.Pixel => "ps",
        Gen5ShaderEvaluationStage.Compute => "cs",
        _ => "other",
    };

    private static string FormatPercent(long numerator, long denominator) =>
        denominator == 0 ? "0.0%" : $"{numerator * 100.0 / denominator:F1}%";

    // This intentionally excludes captured buffer bytes. It measures whether the
    // evaluator's descriptor/register plan can be reused while snapshots continue
    // to refresh through the existing memory path.
    private static ulong ComputePlanHash(Gen5ShaderEvaluation evaluation)
    {
        var hash = StartHash();
        hash = AddValues(hash, evaluation.InitialScalarRegisters);
        hash = AddValues(hash, evaluation.ScalarRegisters);

        hash = AddHash(hash, (uint)evaluation.ImageBindings.Count);
        foreach (var binding in evaluation.ImageBindings)
        {
            hash = AddHash(hash, binding.Pc);
            hash = AddStringHash(hash, binding.Opcode);
            hash = AddHash(hash, binding.Control.Dmask);
            hash = AddHash(hash, binding.Control.VectorAddress);
            hash = AddValues(hash, binding.Control.AddressRegisters);
            hash = AddHash(hash, binding.Control.VectorData);
            hash = AddHash(hash, binding.Control.ScalarResource);
            hash = AddHash(hash, binding.Control.ScalarSampler);
            hash = AddHash(hash, binding.Control.Dimension);
            hash = AddHash(hash, binding.Control.IsArray);
            hash = AddHash(hash, binding.Control.Glc);
            hash = AddHash(hash, binding.Control.Slc);
            hash = AddHash(hash, binding.Control.A16);
            hash = AddHash(hash, binding.Control.D16);
            hash = AddValues(hash, binding.ResourceDescriptor);
            hash = AddValues(hash, binding.SamplerDescriptor);
            hash = AddNullableHash(hash, binding.MipLevel);
        }

        hash = AddHash(hash, (uint)evaluation.GlobalMemoryBindings.Count);
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            hash = AddHash(hash, binding.ScalarAddress);
            hash = AddHash(hash, binding.BaseAddress);
            hash = AddValues(hash, binding.InstructionPcs);
            hash = AddHash(hash, (uint)binding.DataLength);
            hash = AddHash(hash, binding.Writable);
            hash = AddHash(hash, binding.WriteBackToGuest);
        }

        var vertexInputs = evaluation.VertexInputs ?? [];
        hash = AddHash(hash, (uint)vertexInputs.Count);
        foreach (var binding in vertexInputs)
        {
            hash = AddHash(hash, binding.Pc);
            hash = AddHash(hash, binding.Location);
            hash = AddHash(hash, binding.ComponentCount);
            hash = AddHash(hash, binding.DataFormat);
            hash = AddHash(hash, binding.NumberFormat);
            hash = AddHash(hash, binding.BaseAddress);
            hash = AddHash(hash, binding.Stride);
            hash = AddHash(hash, binding.OffsetBytes);
            hash = AddHash(hash, (uint)binding.DataLength);
            hash = AddHash(hash, binding.PerInstance);
            hash = AddValues(hash, binding.AliasPcs ?? []);
        }

        if (evaluation.RuntimeScalarRegisters is { } runtimeRegisters)
        {
            foreach (var register in runtimeRegisters.Order())
            {
                hash = AddHash(hash, register);
            }
        }

        return hash;
    }

    private static ulong AddValues(ulong hash, IEnumerable<uint> values)
    {
        foreach (var value in values)
        {
            hash = AddHash(hash, value);
        }

        return hash;
    }

    private static ulong AddStringHash(ulong hash, string value)
    {
        foreach (var character in value)
        {
            hash = AddHash(hash, character);
        }

        return hash;
    }

    private static ulong AddNullableHash(ulong hash, uint? value) =>
        AddHash(AddHash(hash, value.HasValue), value.GetValueOrDefault());

    private static ulong StartHash() => 14695981039346656037UL;

    private static ulong AddHash(ulong hash, bool value) =>
        AddHash(hash, value ? 1u : 0u);

    private static ulong AddHash(ulong hash, uint value) =>
        (hash ^ value) * 1099511628211UL;

    private static ulong AddHash(ulong hash, ulong value) =>
        AddHash(AddHash(hash, (uint)value), (uint)(value >> 32));

    private static string FormatStage(
        IReadOnlyList<long> calls,
        IReadOnlyList<long> failures,
        IReadOnlyList<long> ticks,
        Gen5ShaderEvaluationStage stage)
    {
        var index = (int)stage;
        return $"{calls[index]}/{ToMilliseconds(ticks[index]):F1}ms/{failures[index]}fail";
    }

    private static string FormatReads(
        IReadOnlyList<long> calls,
        IReadOnlyList<long> failures,
        IReadOnlyList<long> bytes,
        IReadOnlyList<long> ticks,
        Gen5GlobalMemoryReadKind kind)
    {
        var index = (int)kind;
        return $"{calls[index]}/{bytes[index] / BytesPerMebibyte:F1}MiB/" +
               $"{ToMilliseconds(ticks[index]):F1}ms/{failures[index]}fail";
    }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private static long[] ExchangeAll(long[] counters)
    {
        var values = new long[counters.Length];
        for (var index = 0; index < counters.Length; index++)
        {
            values[index] = Interlocked.Exchange(ref counters[index], 0);
        }

        return values;
    }
}
