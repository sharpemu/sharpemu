// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private const int LookupSlots = 4096;

    private static readonly bool LookupMemoEnabled = Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_LOOKUP_MEMO") != "0";

    private struct LookupMemo
    {
        public ImageRequest Request;
        public ImageViewDescription View;
        public ResourceSlotIdentifier Result;
        public ulong Generation;
        public ulong Begin;
        public ulong End;
        public bool ExactFormat;
    }

    private const int LookupEventSlots = 64;

    private readonly record struct LookupEvent(ulong Generation, ulong Begin, ulong End);

    private readonly LookupEvent[] _lookupEvents = new LookupEvent[LookupEventSlots];
    private int _lookupEventCursor;
    private static readonly ThreadCounter _totalLookupRevalidations = new();

    private LookupMemo[]? _lookups;
    private ulong _lookupGeneration = 1;
    private long _lookupHits;
    private long _lookupMisses;
    private static readonly ThreadCounter _totalLookupHits = new();
    private static readonly ThreadCounter _totalLookupMisses = new();
    private static readonly ThreadCounter _totalGenerationBumps = new();
    private static readonly ThreadCounter _totalBindingHits = new();
    private static readonly ThreadCounter _totalBindingMisses = new();

    public static void CountBindingMemo(bool hit) => (hit ? _totalBindingHits : _totalBindingMisses).Increment();

    public (long Hits, long Misses) LookupMemoCounters => (_lookupHits, _lookupMisses);

    public static string TakeLookupReport() =>
        FormattableString.Invariant($"[PERF][IMAGE_LOOKUP] memo_hits={_totalLookupHits.Take()} memo_misses={_totalLookupMisses.Take()} ") +
        FormattableString.Invariant($"generation_bumps={_totalGenerationBumps.Take()} revalidated={_totalLookupRevalidations.Take()} ") +
        FormattableString.Invariant($"binding_hits={_totalBindingHits.Take()} binding_misses={_totalBindingMisses.Take()}");

    private void InvalidateLookups() => InvalidateLookups(0, ulong.MaxValue);

    private void InvalidateLookups(ulong begin, ulong end)
    {
        _lookupGeneration++;
        _lookupEvents[_lookupEventCursor++ & (LookupEventSlots - 1)] = new LookupEvent(_lookupGeneration, begin, end);
        _totalGenerationBumps.Increment();
    }

    // Changes whenever an image a lookup could find is created, removed or replaced, so a caller
    // that remembers a lookup's result can tell when it may have changed.
    public ulong LookupGeneration => _lookupGeneration;

    // What a remembered lookup still does on reuse: the image counts as used now. False when the
    // image is gone.
    public bool TryReuseAcquired(ResourceSlotIdentifier imageIdentifier, ulong backing)
    {
        using var held = _lock.Hold();
        if (_slots.TryGet(imageIdentifier) is not { Registered: true, Parked: false } image || image.DepthOwner.IsValid ||
            image.Binding.NeedsRebind || image.IsCpuDirty || image.IsBufferModified || image.Backing.Handle.Handle != backing)
        {
            return false;
        }

        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        if (!ImageDescription.IsEmptyRange(image.Description.Data))
        {
            MergeMipTailBlock(image);
        }

        return true;
    }

    public bool TryTouchRemembered(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (_slots.TryGet(imageIdentifier) is not { Registered: true, Parked: false } image)
        {
            return false;
        }

        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        return true;
    }

    public bool TryTouchRemembered(ResourceSlotIdentifier imageIdentifier, ref ulong generation, ulong begin, ulong end)
    {
        using var held = _lock.Hold();
        if (!LookupUnchangedSince(generation, begin, end) || _slots.TryGet(imageIdentifier) is not { Registered: true, Parked: false } image)
        {
            return false;
        }

        generation = _lookupGeneration;
        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        return true;
    }

    public GuestSpan RememberedRange(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var held = _lock.Read();
        return UnionRange(request.Description.Data, _slots.TryGet(imageIdentifier)?.Description.Data ?? request.Description.Data);
    }

    private static GuestSpan UnionRange(GuestSpan left, GuestSpan right)
    {
        var begin = Math.Min(left.Address, right.Address);
        var end = Math.Max(left.End, right.End);
        return new GuestSpan(begin, end - begin);
    }

    private bool LookupUnchangedSince(ulong generation, ulong begin, ulong end)
    {
        if (generation == _lookupGeneration)
        {
            return true;
        }

        if (_lookupGeneration - generation > LookupEventSlots)
        {
            return false;
        }

        for (var index = _lookupEventCursor - 1; index >= 0 && index > _lookupEventCursor - 1 - LookupEventSlots; index--)
        {
            ref readonly var lookupEvent = ref _lookupEvents[index & (LookupEventSlots - 1)];
            if (lookupEvent.Generation <= generation)
            {
                break;
            }

            if (lookupEvent.Begin < end && begin < lookupEvent.End)
            {
                return false;
            }
        }

        _totalLookupRevalidations.Increment();
        return true;
    }

    private static int LookupSlot(in ImageRequest request, bool exactFormat)
    {
        var hash = HashCode.Combine(request.Description.Data.Address, request.Description.Data.Size, request.Description.PixelFormat,
            request.Description.Extent.Width, request.View.Format, request.View.BaseLevel, request.View.BaseLayer,
            HashCode.Combine(request.Role, exactFormat, request.View.Type, request.Description.Resources));
        return hash & (LookupSlots - 1);
    }

    private static bool SameRequest(in ImageRequest left, in ImageRequest right) =>
        SameDescription(left.Description, right.Description) && left.View == right.View && left.Role == right.Role &&
        left.TraceTextureMetadataAddress == right.TraceTextureMetadataAddress &&
        string.Equals(left.TraceTextureDescriptor, right.TraceTextureDescriptor, StringComparison.Ordinal) &&
        left.TraceMetadataCompress == right.TraceMetadataCompress && left.TraceWriteCompress == right.TraceWriteCompress;

    private static bool SameDescription(in ImageDescription left, in ImageDescription right)
    {
        if (left.Data != right.Data || left.Stencil != right.Stencil ||
            left.HtileClearMask != right.HtileClearMask || left.PixelFormat != right.PixelFormat ||
            left.GuestFormat != right.GuestFormat || left.Type != right.Type ||
            left.Extent.Width != right.Extent.Width || left.Extent.Height != right.Extent.Height ||
            left.Extent.Depth != right.Extent.Depth || left.Resources != right.Resources ||
            left.Pitch != right.Pitch || left.BytesPerBlock != right.BytesPerBlock ||
            left.Samples != right.Samples || left.TileMode != right.TileMode || left.Bgra16 != right.Bgra16)
        {
            return false;
        }

        ref readonly var a = ref left.Metadata;
        ref readonly var b = ref right.Metadata;
        if (a.Range != b.Range || a.Kind != b.Kind || a.Control != b.Control || a.Compression != b.Compression ||
            a.StencilCompressed != b.StencilCompressed || a.NativeColorClear != b.NativeColorClear ||
            a.ColorAlphaOnLeastSignificantBits != b.ColorAlphaOnLeastSignificantBits ||
            a.ColorMetadataBaseLayer != b.ColorMetadataBaseLayer || a.PackedColorClearSupported != b.PackedColorClearSupported ||
            a.PackedColorClear.Uint32_0 != b.PackedColorClear.Uint32_0 ||
            a.PackedColorClear.Uint32_1 != b.PackedColorClear.Uint32_1 ||
            a.PackedColorClear.Uint32_2 != b.PackedColorClear.Uint32_2 ||
            a.PackedColorClear.Uint32_3 != b.PackedColorClear.Uint32_3)
        {
            return false;
        }

        for (var level = 0; level < ImageDescription.MaxLevels; level++)
        {
            ref readonly var first = ref left.MipLayout[level];
            ref readonly var second = ref right.MipLayout[level];
            if (first.Offset != second.Offset || first.Size != second.Size ||
                first.Pitch != second.Pitch || first.Height != second.Height)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReuseLookup(ref ImageRequest request, bool exactFormat, out ResourceSlotIdentifier result)
    {
        result = ResourceSlotIdentifier.Invalid;
        if (!LookupMemoEnabled || _lookups is not { } lookups || request.Role == ImageRole.DisplaySurface)
        {
            return false;
        }

        ref var memo = ref lookups[LookupSlot(request, exactFormat)];
        if (memo.ExactFormat != exactFormat || !SameRequest(memo.Request, request) || !LookupUnchangedSince(memo.Generation, memo.Begin, memo.End) ||
            _slots.TryGet(memo.Result) is not { Registered: true, Parked: false } image)
        {
            _lookupMisses++;
            _totalLookupMisses.Increment();
            return false;
        }

        memo.Generation = _lookupGeneration;
        _lookupHits++;
        _totalLookupHits.Increment();
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
        var range = UnionRange(original.Description.Data, _slots[result].Description.Data);
        memo.Begin = range.Address;
        memo.End = range.End;
    }
}
