// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>Input snapshots produced by the active host window.</summary>
public interface IHostWindowInputSource
{
    bool HasKeyboardFocus { get; }

    bool IsKeyDown(int virtualKey);

    int GetGamepadStates(Span<HostGamepadState> destination);

    string? DescribeConnectedGamepad();

    void SetRumble(byte largeMotor, byte smallMotor);

    void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger);

    void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger);

    void SetLightbar(byte red, byte green, byte blue);

    void ResetLightbar();
}

/// <summary>Process-wide bridge between the window layer and host input.</summary>
public static class HostWindowInputSource
{
    private static IHostWindowInputSource? _current;

    public static IHostWindowInputSource? Current => Volatile.Read(ref _current);

    public static void Set(IHostWindowInputSource source) =>
        Volatile.Write(ref _current, source);

    public static void Clear(IHostWindowInputSource source) =>
        Interlocked.CompareExchange(ref _current, null, source);
}
