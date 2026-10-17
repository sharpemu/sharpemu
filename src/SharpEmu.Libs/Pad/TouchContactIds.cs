// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

// The pad gives each new finger contact its own touch id, 1 to 127 and then back to 1,
// and keeps it while the finger stays down. Games tell strokes apart by that id: with a
// fixed id per finger slot, lifting the finger and touching elsewhere reads as one
// contact jumping across the pad (Ghost of Yōtei's name drawing never saw a new stroke).
internal sealed class TouchContactIds
{
    private const byte FirstId = 1;
    private const byte LastId = 127;

    private readonly bool[] _down = new bool[2];
    private readonly byte[] _ids = new byte[2];
    private byte _next = FirstId;

    // The id to report for this finger slot; a contact that just started gets a new one.
    public byte Track(int finger, bool down)
    {
        if ((uint)finger >= (uint)_down.Length)
        {
            return 0;
        }

        if (down && !_down[finger])
        {
            _ids[finger] = _next;
            _next = _next == LastId ? FirstId : (byte)(_next + 1);
        }

        _down[finger] = down;
        return _ids[finger];
    }
}
