// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

public enum HostViewFailure
{
    None,
    BackingUnavailable,
    OffsetOutOfBounds,
    FixedMapFailed,
    PlaceholderMapFailed,
    ProtectFailed,
    WrongHostAddress,
    AddressUnavailable,
}
