// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GuestMemory;

internal static class RangeSearch
{
    // Return the last key's index at or below the address, or -1 if no key qualifies.
    public static int FindLastIndexAtOrBelow<T>(SortedList<ulong, T> list, ulong address)
    {
        var keys = list.Keys;
        var low = 0;
        var high = keys.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (keys[mid] <= address)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low - 1;
    }
}
