// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

// Fewer guest memory reads in the AGC emitters (SHARPEMU_AGC_PACKET_WRITES=0 turns them off):
// - Marker names are read in chunks within a page.
// - A release reads its six stack words in one read.
// - The submitted command scan uses a copy loaded a page at a time.
// The switch leaves packet emission alone. A register range allocates its marker and packet together, with one write
// for the marker and header, only when both fit without the buffer-full callback, and otherwise apart; the payload
// copy follows either way. Push markers retain ordinary allocation/header/name-word writes after name resolution.
// Neither emitter rolls the cursor back or writes speculatively and replays.
// The retained read optimizations have separate input-stability and read-effect contracts.
internal static class AgcPacketWrites
{
    internal static readonly bool Configured =
        Environment.GetEnvironmentVariable("SHARPEMU_AGC_PACKET_WRITES") != "0";

    // Tests: overrides the switch on this thread.
    [ThreadStatic]
    internal static bool? OverrideForTests;

    internal static bool Active => OverrideForTests ?? Configured;

    // The largest submitted command buffer the scan reads at once.
    internal const uint MaximumScanDwords = 1u << 18;
}
