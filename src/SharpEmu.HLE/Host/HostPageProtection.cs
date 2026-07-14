// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Platform-neutral page protection. Values intentionally enumerate the exact
/// combinations the emulator uses today so each maps 1:1 onto a single native
/// protection constant (PAGE_* on Windows, PROT_* elsewhere).
/// </summary>
public enum HostPageProtection
{
    NoAccess,
    ReadOnly,
    ReadWrite,
    Execute,
    ReadExecute,
    ReadWriteExecute,
    ExecuteWriteCopy,
}
