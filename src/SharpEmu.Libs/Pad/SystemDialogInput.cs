// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

internal static class SystemDialogInput
{
    internal const uint InterceptedButton = 0x80000000;

    private static readonly System.Collections.Concurrent.ConcurrentQueue<int> Keys = new();
    private static int _capturing;
    private static uint _residualButtons;

    internal static bool Capturing => Volatile.Read(ref _capturing) != 0;

    internal static void Capture()
    {
        Keys.Clear();
        Volatile.Write(ref _capturing, 1);
    }

    internal static void OnKeyDown(int virtualKey)
    {
        if (Capturing)
        {
            Keys.Enqueue(virtualKey);
        }
    }

    internal static void TakeKeys(List<int> keys)
    {
        while (Keys.TryDequeue(out var key))
        {
            keys.Add(key);
        }
    }

    internal static void Release()
    {
        Volatile.Write(ref _residualButtons, PadExports.ReadSystemDialogState().Buttons);
        PadExports.DiscardUnreadStates();
        Volatile.Write(ref _capturing, 0);
    }

    internal static PadState Filter(in PadState state)
    {
        if (Capturing)
        {
            return state with
            {
                Buttons = InterceptedButton,
                LeftX = 128,
                LeftY = 128,
                RightX = 128,
                RightY = 128,
                L2 = 0,
                R2 = 0,
                Motion = default,
                Touch = default,
            };
        }

        var residual = Volatile.Read(ref _residualButtons);
        if (residual == 0)
        {
            return state;
        }

        var held = residual & state.Buttons;
        Interlocked.CompareExchange(ref _residualButtons, held, residual);
        return held == 0 ? state : state with { Buttons = state.Buttons & ~held };
    }
}
