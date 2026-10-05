// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private const int LookupSlots = 256;

    private static readonly bool LookupMemoEnabled = Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_LOOKUP_MEMO") != "0";

    private struct LookupMemo
    {
        public ImageRequest Request;
        public ImageViewDescription View;
        public ResourceSlotIdentifier Result;
        public ulong Generation;
        public bool ExactFormat;
    }

    private LookupMemo[]? _lookups;
    private ulong _lookupGeneration = 1;
    private long _lookupHits;
    private long _lookupMisses;
    private static long _totalLookupHits;
    private static long _totalLookupMisses;

    public (long Hits, long Misses) LookupMemoCounters => (_lookupHits, _lookupMisses);

    public static string TakeLookupReport() => FormattableString.Invariant(
        $"[PERF][IMAGE_LOOKUP] memo_hits={Interlocked.Exchange(ref _totalLookupHits, 0)} memo_misses={Interlocked.Exchange(ref _totalLookupMisses, 0)}");

    private void InvalidateLookups() => _lookupGeneration++;

    private static int LookupSlot(in ImageRequest request, bool exactFormat)
    {
        var hash = HashCode.Combine(request.Description.Data.Address, request.Description.Data.Size, request.Description.PixelFormat,
            request.Description.Extent.Width, request.View.Format, request.View.BaseLevel, request.View.BaseLayer,
            HashCode.Combine(request.Role, exactFormat, request.View.Type, request.Description.Resources));
        return hash & (LookupSlots - 1);
    }

    private static bool SameRequest(in ImageRequest left, in ImageRequest right) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<ImageRequest>(in left)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ImageRequest>(in right)));

    private bool TryReuseLookup(ref ImageRequest request, bool exactFormat, out ResourceSlotIdentifier result)
    {
        result = ResourceSlotIdentifier.Invalid;
        if (!LookupMemoEnabled || _lookups is not { } lookups || request.Role == ImageRole.DisplaySurface)
        {
            return false;
        }

        ref var memo = ref lookups[LookupSlot(request, exactFormat)];
        if (memo.Generation != _lookupGeneration || memo.ExactFormat != exactFormat || !SameRequest(memo.Request, request) ||
            _slots.TryGet(memo.Result) is not { Registered: true } image)
        {
            _lookupMisses++;
            Interlocked.Increment(ref _totalLookupMisses);
            return false;
        }

        _lookupHits++;
        Interlocked.Increment(ref _totalLookupHits);
        request.View = memo.View;
        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        result = memo.Result;
        return true;
    }

    private void RememberLookup(in ImageRequest original, bool exactFormat, in ImageViewDescription view, ResourceSlotIdentifier result)
    {
        if (!LookupMemoEnabled || original.Role == ImageRole.DisplaySurface)
        {
            return;
        }

        var lookups = _lookups ??= new LookupMemo[LookupSlots];
        ref var memo = ref lookups[LookupSlot(original, exactFormat)];
        memo.Request = original;
        memo.View = view;
        memo.Result = result;
        memo.ExactFormat = exactFormat;
        memo.Generation = _lookupGeneration;
    }
}
