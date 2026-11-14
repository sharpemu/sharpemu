// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

// Exact trims on the guest threads' host calls, each read at startup and on unless its variable is 0.
// - SHARPEMU_MARK_CLOSURE_FREE: a managed write that is not inside one GPU span walks its overlaps with its state passed
//   by value, without allocating a closure for each mark.
// - SHARPEMU_MIXED_COPY_DIRECT: a guest copy between the backing and private memory that needs no commit or protection
//   change copies in one call under the read lock instead of through 64 KiB staging pieces.
public static class HostCallTrims
{
    private static readonly bool MarkClosureFreeConfigured =
        Environment.GetEnvironmentVariable("SHARPEMU_MARK_CLOSURE_FREE") != "0";

    private static readonly bool MixedCopyConfigured =
        Environment.GetEnvironmentVariable("SHARPEMU_MIXED_COPY_DIRECT") != "0";

    // Tests: override the switches on this thread.
    [ThreadStatic]
    internal static bool? MarkClosureFreeOverrideForTests;
    [ThreadStatic]
    internal static bool? MixedCopyOverrideForTests;

    public static bool MarkClosureFree => MarkClosureFreeOverrideForTests ?? MarkClosureFreeConfigured;

    public static bool MixedCopyDirect => MixedCopyOverrideForTests ?? MixedCopyConfigured;
}
