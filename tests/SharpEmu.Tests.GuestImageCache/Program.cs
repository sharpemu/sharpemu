// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;

var waits = 0;
var destroyed = new List<GuestImageResource>();
using var cache = new GuestImageCache(
    () => waits++,
    resource => destroyed.Add(resource));

var first = new GuestImageResource { Address = 0x1000, GuestSize = 0x100 };
cache.Add(first);
Assert(cache.TryGetValue(0x1000, out var found) && ReferenceEquals(found, first),
    "exact guest image lookup failed");

Assert(cache.InvalidateOverlaps(0x1100, 0x80).Count == 0,
    "adjacent ranges were treated as aliases");
Assert(waits == 0, "non-overlapping invalidation waited for the GPU");

var invalidated = cache.InvalidateOverlaps(0x1080, 0x100);
Assert(invalidated.Count == 1 && ReferenceEquals(invalidated[0], first),
    "overlapping alias was not invalidated");
Assert(waits == 1 && destroyed.SequenceEqual([first]),
    "overlap did not wait once before destroying the old image");

var second = new GuestImageResource { Address = 0x1080, GuestSize = 0x100 };
cache.Add(second);
Assert(cache.ContainsKey(0x1080), "replacement image was not cached");

cache.Dispose();
Assert(waits == 2 && destroyed.SequenceEqual([first, second]),
    "cache disposal did not safely destroy the remaining image");

Console.WriteLine("GuestImageCache lookup, alias invalidation, and safe lifetime tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
