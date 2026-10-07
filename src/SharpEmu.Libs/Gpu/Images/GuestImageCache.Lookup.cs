// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
