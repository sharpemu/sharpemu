// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

// Exact trims on the guest threads' host calls, each read at startup and on unless its variable is 0.
// - SHARPEMU_MARK_CLOSURE_FREE: a managed write that is not inside one GPU span walks its overlaps with its state passed
//   by value, without allocating a closure for each mark.
public static class HostCallTrims
{
    private static readonly bool MarkClosureFreeConfigured =
        Environment.GetEnvironmentVariable("SHARPEMU_MARK_CLOSURE_FREE") != "0";

    // Tests: override the switches on this thread.
    [ThreadStatic]
    internal static bool? MarkClosureFreeOverrideForTests;

    public static bool MarkClosureFree => MarkClosureFreeOverrideForTests ?? MarkClosureFreeConfigured;
}
