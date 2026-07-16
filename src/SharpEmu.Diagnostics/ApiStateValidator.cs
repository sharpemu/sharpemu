// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Lightweight API state machine for PS5 subsystems (AGC, VideoOut, GNM, Kernel).
/// Each subsystem has a defined state order; calling a function in the wrong state
/// produces an immediate diagnostic instead of letting the game crash thousands of
/// instructions later.
///
/// Example: sceAgcDriverRegisterOwner requires sceAgcDriverInitResourceRegistration
/// to have been called first. Without this validator, the game gets INVALID_ARGUMENT
/// and continues into an error path that eventually writes to an unmapped GPU address.
/// With the validator, we report the ordering violation immediately.
/// </summary>
public sealed class ApiStateValidator
{
    public enum Subsystem
    {
        Agc,
        VideoOut,
        Gnm,
        Kernel,
        Pad,
        AudioOut,
        SaveData,
        Network
    }

    public enum AgcState
    {
        Uninitialized,
        ResourceRegistrationInitialized,
        OwnerRegistered,
        ContextCreated,
        Ready,
        SubmitDone
    }

    public enum VideoOutState
    {
        Uninitialized,
        Opened,
        BuffersRegistered,
        FlipRateSet,
        Flipping
    }

    private static readonly ConcurrentDictionary<Subsystem, object> _states = new();
    private static readonly ConcurrentQueue<StateViolation> _violations = new();

    static ApiStateValidator()
    {
        _states[Subsystem.Agc] = AgcState.Uninitialized;
        _states[Subsystem.VideoOut] = VideoOutState.Uninitialized;
    }

    // -----------------------------------------------------------------------
    // AGC state machine
    // -----------------------------------------------------------------------

    public static void Agc_InitResourceRegistration()
    {
        var current = (AgcState)_states[Subsystem.Agc];
        if (current != AgcState.Uninitialized && current != AgcState.ResourceRegistrationInitialized)
        {
            ReportViolation(Subsystem.Agc, "sceAgcDriverInitResourceRegistration",
                expected: AgcState.Uninitialized,
                current: current);
        }
        _states[Subsystem.Agc] = AgcState.ResourceRegistrationInitialized;
    }

    public static bool Agc_RequireResourceRegistration(string callerFunction)
    {
        var current = (AgcState)_states[Subsystem.Agc];
        if (current == AgcState.Uninitialized)
        {
            ReportViolation(Subsystem.Agc, callerFunction,
                expected: AgcState.ResourceRegistrationInitialized,
                current: current);
            return false;
        }
        return true;
    }

    public static void Agc_RegisterOwner()
    {
        if (Agc_RequireResourceRegistration("sceAgcDriverRegisterOwner"))
        {
            _states[Subsystem.Agc] = AgcState.OwnerRegistered;
        }
    }

    public static void Agc_CreateContext()
    {
        var current = (AgcState)_states[Subsystem.Agc];
        if (current < AgcState.OwnerRegistered)
        {
            ReportViolation(Subsystem.Agc, "sceAgcDriverCreateContext",
                expected: AgcState.OwnerRegistered,
                current: current);
        }
        _states[Subsystem.Agc] = AgcState.ContextCreated;
    }

    public static void Agc_Submit()
    {
        var current = (AgcState)_states[Subsystem.Agc];
        if (current < AgcState.ContextCreated)
        {
            ReportViolation(Subsystem.Agc, "sceAgcDriverSubmit",
                expected: AgcState.ContextCreated,
                current: current);
        }
        _states[Subsystem.Agc] = AgcState.SubmitDone;
    }

    // -----------------------------------------------------------------------
    // VideoOut state machine
    // -----------------------------------------------------------------------

    public static void VideoOut_Open()
    {
        _states[Subsystem.VideoOut] = VideoOutState.Opened;
    }

    public static bool VideoOut_RequireOpen(string callerFunction)
    {
        var current = (VideoOutState)_states[Subsystem.VideoOut];
        if (current == VideoOutState.Uninitialized)
        {
            ReportViolation(Subsystem.VideoOut, callerFunction,
                expected: VideoOutState.Opened,
                current: current);
            return false;
        }
        return true;
    }

    public static void VideoOut_RegisterBuffers()
    {
        if (VideoOut_RequireOpen("sceVideoOutRegisterBuffers"))
        {
            _states[Subsystem.VideoOut] = VideoOutState.BuffersRegistered;
        }
    }

    // -----------------------------------------------------------------------
    // Violation reporting
    // -----------------------------------------------------------------------

    private static void ReportViolation(Subsystem subsystem, string function,
        object expected, object current)
    {
        var violation = new StateViolation(
            Subsystem: subsystem,
            Function: function,
            ExpectedState: expected?.ToString() ?? "?",
            CurrentState: current?.ToString() ?? "?",
            Timestamp: DateTimeOffset.UtcNow);
        _violations.Enqueue(violation);

        Console.Error.WriteLine(
            $"[API_STATE][VIOLATION] {subsystem}.{function}: " +
            $"expected={violation.ExpectedState} current={violation.CurrentState}");
    }

    public static IReadOnlyList<StateViolation> GetViolations() => _violations.ToArray();

    public static string RenderReport()
    {
        var sb = new System.Text.StringBuilder();
        var violations = _violations.ToArray();
        sb.AppendLine($"API State Validator Report ({violations.Length} violations)");
        sb.AppendLine(new string('-', 80));
        if (violations.Length == 0)
        {
            sb.AppendLine("  No state violations detected.");
        }
        else
        {
            sb.AppendLine($"  {"Subsystem",-12} {"Function",-40} {"Expected",-30} {"Current",-30}");
            sb.AppendLine(new string('-', 80));
            foreach (var v in violations)
            {
                sb.AppendLine($"  {v.Subsystem,-12} {v.Function,-40} {v.ExpectedState,-30} {v.CurrentState,-30}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Current subsystem states:");
        foreach (var kvp in _states)
        {
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
        }
        return sb.ToString();
    }

    public static void Reset()
    {
        _states[Subsystem.Agc] = AgcState.Uninitialized;
        _states[Subsystem.VideoOut] = VideoOutState.Uninitialized;
        while (_violations.TryDequeue(out _)) { }
    }
}

public readonly record struct StateViolation(
    ApiStateValidator.Subsystem Subsystem,
    string Function,
    string ExpectedState,
    string CurrentState,
    DateTimeOffset Timestamp);
