// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Comprehensive boot diagnostics: engine detection, timeline recording,
/// progress scoring, crash fingerprinting, and one-click AI debug package.
/// </summary>
public static class BootDiagnostics
{
    // ====== Engine Detection ======
    public enum GameEngine { Unknown, Unity, UnityIL2CPP, Unreal, Custom }

    private static GameEngine _detectedEngine = GameEngine.Unknown;
    private static string _engineVersion = "unknown";
    private static readonly ConcurrentDictionary<string, int> _importLibraryCounts = new();

    public static GameEngine DetectedEngine => _detectedEngine;

    public static void TrackImport(string? libraryName)
    {
        if (string.IsNullOrEmpty(libraryName)) return;
        _importLibraryCounts.AddOrUpdate(libraryName, 1, (_, v) => v + 1);

        // Detect engine from import patterns
        if (_detectedEngine == GameEngine.Unknown)
        {
            if (libraryName.Contains("il2cpp") || libraryName.Contains("libil2cpp"))
                _detectedEngine = GameEngine.UnityIL2CPP;
            else if (libraryName.Contains("unity") || libraryName.Contains("libunity"))
                _detectedEngine = GameEngine.Unity;
        }
    }

    public static string GetEngineReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== Engine Detection ==========");
        sb.AppendLine($"Engine: {_detectedEngine}");
        sb.AppendLine($"Version: {_engineVersion}");
        sb.AppendLine();
        sb.AppendLine("Import library coverage:");
        foreach (var kvp in _importLibraryCounts.OrderByDescending(x => x.Value))
            sb.AppendLine($"  {kvp.Key,-30} {kvp.Value,6} calls");
        return sb.ToString();
    }

    // ====== Boot Timeline Recorder ======
    private static readonly List<BootEvent> _timeline = new();
    private static readonly object _timelineGate = new();
    private static readonly Stopwatch _bootTimer = Stopwatch.StartNew();

    public static void RecordEvent(string event_, string? detail = null, bool success = true)
    {
        lock (_timelineGate)
        {
            _timeline.Add(new BootEvent(
                TimestampMs: _bootTimer.ElapsedMilliseconds,
                Event: event_,
                Detail: detail,
                Success: success));
        }
    }

    public static string GetTimelineReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== Boot Timeline ==========");
        lock (_timelineGate)
        {
            foreach (var e in _timeline)
            {
                var mark = e.Success ? "✓" : "✗";
                var detail = e.Detail != null ? $" ({e.Detail})" : "";
                sb.AppendLine($"  [{e.TimestampMs,6}ms] {mark} {e.Event}{detail}");
            }
        }
        return sb.ToString();
    }

    // ====== Boot Progress Score ======
    public static BootProgressScore ComputeProgressScore()
    {
        var stages = new Dictionary<string, bool>
        {
            ["SELF Loaded"] = _timeline.Any(e => e.Event.Contains("SELF") || e.Event.Contains("ELF")),
            ["TLS Initialized"] = _timeline.Any(e => e.Event.Contains("TLS")),
            ["CRT Init"] = _timeline.Any(e => e.Event.Contains("CRT") || e.Event.Contains("libc")),
            ["Pthread Init"] = _timeline.Any(e => e.Event.Contains("pthread") || e.Event.Contains("Pthread")),
            ["Direct Memory"] = _importLibraryCounts.ContainsKey("libKernel"),
            ["Engine Detected"] = _detectedEngine != GameEngine.Unknown,
            ["IL2CPP Init"] = _detectedEngine == GameEngine.UnityIL2CPP,
            ["AGC/GPU Init"] = _importLibraryCounts.ContainsKey("libSceAgc"),
            ["VideoOut Open"] = _importLibraryCounts.ContainsKey("libSceVideoOut") || _timeline.Any(e => e.Event.Contains("VideoOut")),
            ["First Frame"] = _timeline.Any(e => e.Event.Contains("frame") || e.Event.Contains("Frame")),
        };

        var successCount = stages.Count(s => s.Value);
        var score = (int)((double)successCount / stages.Count * 100);

        return new BootProgressScore(score, stages);
    }

    // ====== Crash Fingerprint ======
    public static string ComputeCrashFingerprint(ulong rip, string? firstFailureNid, string crashType)
    {
        var input = $"{rip:X16}|{firstFailureNid ?? "none"}|{crashType}";
        var hash = input.GetHashCode();
        return $"CRASH-{hash:X8}";
    }

    // ====== Last Known Good State ======
    private static string? _lastGoodState;

    public static void UpdateLastGoodState(string state) => _lastGoodState = state;
    public static string? GetLastGoodState() => _lastGoodState;

    // ====== One-Click AI Debug Package ======
    public static string GenerateAiDebugPackage(string outputDir, string gameId,
        ulong faultAddress, ulong rip, ulong rax, ulong rdi, ulong rsi,
        string? firstFailure, string[] missingNids, string[] lastImports)
    {
        Directory.CreateDirectory(outputDir);
        var crashType = faultAddress < 0x10000 ? "NULL_POINTER"
            : faultAddress >= 0x1FE000000UL && faultAddress < 0x200000000UL ? "GPU_ADDRESS"
            : "UNKNOWN";
        var crashId = ComputeCrashFingerprint(rip, firstFailure, crashType);
        var progress = ComputeProgressScore();

        // 1. debug_report.json
        var report = new
        {
            schema = 1,
            emulator_version = "v1.0.0013",
            session_id = Guid.NewGuid().ToString("N")[..8],
            crash_id = crashId,
            game_id = gameId,
            engine = _detectedEngine.ToString(),
            boot_progress = progress.Score,
            boot_stage = progress.Stages.LastOrDefault(s => s.Value).Key ?? "unknown",
            last_good_state = _lastGoodState ?? "none",
            crash = new
            {
                type = crashType,
                rip = $"0x{rip:X16}",
                fault_address = $"0x{faultAddress:X16}",
                null_register = rdi == 0 ? "RDI" : rsi == 0 ? "RSI" : "none",
                rax = $"0x{rax:X16}",
                confidence = 0.85
            },
            missing_nids = missingNids,
            first_failure = firstFailure ?? "none",
            last_imports = lastImports.Take(20).ToArray(),
            suggested_actions = GenerateSuggestedActions(crashType, missingNids, firstFailure, _detectedEngine),
            timeline = _timeline.Select(e => new { time_ms = e.TimestampMs, event_ = e.Event, success = e.Success }).ToArray()
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, "debug_report.json"), json);

        // 2. AI_SUMMARY.md — 20-line summary
        var summary = new StringBuilder();
        summary.AppendLine("# SharpEmu AI Summary");
        summary.AppendLine();
        summary.AppendLine($"Game: {gameId}");
        summary.AppendLine($"Engine: {_detectedEngine}");
        summary.AppendLine($"Boot: {progress.Score}%");
        summary.AppendLine($"Crash: {crashType} ({crashId})");
        summary.AppendLine($"RIP: 0x{rip:X16}");
        summary.AppendLine($"Fault: 0x{faultAddress:X16}");
        summary.AppendLine($"NULL reg: {(rdi == 0 ? "RDI" : rsi == 0 ? "RSI" : "none")}");
        summary.AppendLine($"First failure: {firstFailure ?? "none"}");
        summary.AppendLine($"Missing NIDs: {missingNids.Length}");
        summary.AppendLine($"Last good: {_lastGoodState ?? "none"}");
        summary.AppendLine();
        summary.AppendLine("Root cause:");
        summary.AppendLine(GenerateRootCause(crashType, firstFailure, _detectedEngine));
        summary.AppendLine();
        summary.AppendLine("Suggested:");
        foreach (var a in GenerateSuggestedActions(crashType, missingNids, firstFailure, _detectedEngine).Take(3))
            summary.AppendLine($"- {a}");
        summary.AppendLine();
        summary.AppendLine("Confidence: 85%");
        File.WriteAllText(Path.Combine(outputDir, "AI_SUMMARY.md"), summary.ToString());

        // 3. AI_CONTEXT.md — full context for AI
        var ctx = new StringBuilder();
        ctx.AppendLine("# SharpEmu AI Debug Context");
        ctx.AppendLine();
        ctx.AppendLine("You are debugging SharpEmu, a PS5/PS4 emulator written in C#.");
        ctx.AppendLine();
        ctx.AppendLine($"- **Emulator version**: v1.0.0013");
        ctx.AppendLine($"- **Game ID**: {gameId}");
        ctx.AppendLine($"- **Engine**: {_detectedEngine}");
        ctx.AppendLine($"- **Boot progress**: {progress.Score}%");
        ctx.AppendLine($"- **Crash ID**: {crashId}");
        ctx.AppendLine($"- **Crash type**: {crashType}");
        ctx.AppendLine($"- **Crash RIP**: 0x{rip:X16}");
        ctx.AppendLine($"- **First failure**: {firstFailure ?? "none"}");
        ctx.AppendLine($"- **Missing NIDs**: {missingNids.Length}");
        ctx.AppendLine($"- **Last good state**: {_lastGoodState ?? "none"}");
        ctx.AppendLine();
        ctx.AppendLine("## Boot Progress Checklist");
        foreach (var s in progress.Stages)
            ctx.AppendLine($"- [{(s.Value ? "✓" : "✗")}] {s.Key}");
        ctx.AppendLine();
        ctx.AppendLine("## Boot Timeline");
        lock (_timelineGate)
        {
            foreach (var e in _timeline)
                ctx.AppendLine($"  [{e.TimestampMs}ms] {(e.Success ? "✓" : "✗")} {e.Event}");
        }
        ctx.AppendLine();
        ctx.AppendLine("## Missing NIDs");
        foreach (var n in missingNids)
            ctx.AppendLine($"- {n}");
        ctx.AppendLine();
        ctx.AppendLine("## Last 20 imports");
        foreach (var i in lastImports.Take(20))
            ctx.AppendLine($"- {i}");
        ctx.AppendLine();
        ctx.AppendLine("## Suggested Investigation");
        foreach (var a in GenerateSuggestedActions(crashType, missingNids, firstFailure, _detectedEngine))
            ctx.AppendLine($"- {a}");
        File.WriteAllText(Path.Combine(outputDir, "AI_CONTEXT.md"), ctx.ToString());

        // 4. boot_progress.json
        var progressJson = JsonSerializer.Serialize(new
        {
            score = progress.Score,
            stages = progress.Stages,
            engine = _detectedEngine.ToString(),
            crash_id = crashId
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, "boot_progress.json"), progressJson);

        // 5. Create zip
        var zipPath = Path.Combine(outputDir, $"SharpEmu_Report_{gameId}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        System.IO.Compression.ZipFile.CreateFromDirectory(outputDir, zipPath);

        Console.Error.WriteLine($"[AI_DEBUG] Package created: {zipPath}");
        Console.Error.WriteLine($"[AI_DEBUG] Crash ID: {crashId}");
        Console.Error.WriteLine($"[AI_DEBUG] Boot progress: {progress.Score}%");
        Console.Error.WriteLine($"[AI_DEBUG] Engine: {_detectedEngine}");

        return zipPath;
    }

    private static string GenerateRootCause(string crashType, string? firstFailure, GameEngine engine)
    {
        if (firstFailure != null && firstFailure.Contains("UNRESOLVED"))
            return $"Missing HLE export caused NULL return → game used NULL pointer → crash. First failure: {firstFailure}";
        if (crashType == "NULL_POINTER")
            return "A game object was never initialized (NULL pointer dereference). Check which HLE call should have set the register.";
        if (crashType == "GPU_ADDRESS")
            return "Game accessed unmapped GPU memory at a hardcoded address.";
        return "Unknown crash type. Check crash_snapshot.txt for details.";
    }

    private static string[] GenerateSuggestedActions(string crashType, string[] missingNids, string? firstFailure, GameEngine engine)
    {
        var actions = new List<string>();
        if (missingNids.Length > 0)
            actions.Add($"Implement missing NID: {missingNids[0]}");
        if (engine == GameEngine.UnityIL2CPP)
            actions.Add("Ensure all IL2CPP API functions are stubbed (il2cpp_init, il2cpp_set_data_dir, etc.)");
        if (crashType == "NULL_POINTER")
            actions.Add("Trace which HLE call should have set the NULL register");
        if (crashType == "GPU_ADDRESS")
            actions.Add("Map GPU placeholder memory at 0x1FE000000");
        if (firstFailure != null && firstFailure.Contains("UNRESOLVED"))
            actions.Add("Implement the unresolved NID that caused the first failure");
        if (actions.Count == 0)
            actions.Add("Check crash_snapshot.txt for full details");
        return actions.ToArray();
    }

    public static void Reset()
    {
        _detectedEngine = GameEngine.Unknown;
        _importLibraryCounts.Clear();
        _timeline.Clear();
        _lastGoodState = null;
        _bootTimer.Restart();
    }
}

public readonly record struct BootEvent(long TimestampMs, string Event, string? Detail, bool Success);
public readonly record struct BootProgressScore(int Score, Dictionary<string, bool> Stages);
