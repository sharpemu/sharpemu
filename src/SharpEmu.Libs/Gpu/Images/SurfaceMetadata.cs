// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

// PendingDcc holds a metadata fill seen before its color target was bound; it stays invisible
// to the metadata queries until the target classifies the address.
public enum SurfaceMetadataKind : byte
{
    PendingDcc,
    CMask,
    FMask,
    HTile,
    Dcc,
}

public sealed class SurfaceMetadata
{
    // Slices 0-31 live in ClearMask; an array target can hold up to 2048 slices.
    public const uint MaxSlices = 2048;

    public SurfaceMetadataKind Kind;
    private uint _clearMask;
    private bool _upperSlicesClear;
    private readonly HashSet<uint> _upperSliceExceptions = new();
    internal uint DccSliceCount = 32;

    public uint ClearMask
    {
        get => _clearMask;
        set
        {
            _clearMask = value;
            _upperSlicesClear = value == uint.MaxValue;
            _upperSliceExceptions.Clear();
        }
    }

    internal bool IsSliceClear(uint slice) => slice < 32
        ? (_clearMask & (1u << (int)slice)) != 0
        : _upperSlicesClear != _upperSliceExceptions.Contains(slice);

    internal void SetSliceClear(uint slice, bool clear)
    {
        if (slice < 32)
        {
            if (clear) _clearMask |= 1u << (int)slice;
            else _clearMask &= ~(1u << (int)slice);
        }
        else if (clear == _upperSlicesClear)
        {
            _upperSliceExceptions.Remove(slice);
        }
        else
        {
            _upperSliceExceptions.Add(slice);
        }
    }

    public uint FillValue = 0xffffffff;
    public ulong FillSize;
    public ulong Size;
    // Sets slices 0-31 from the mask; the rest follow an all-clear or all-uncleared mask.
    public void SetFromMask(uint mask) => ClearMask = mask;

    // Color targets track up to their layer count, HTile up to MaxSlices, the other kinds 32.
    internal bool TracksSlice(uint slice) => slice < Kind switch
    {
        SurfaceMetadataKind.Dcc => DccSliceCount,
        SurfaceMetadataKind.HTile => MaxSlices,
        _ => 32u,
    };

    public bool IsSliceCleared(uint slice) => slice < MaxSlices && IsSliceClear(slice);

    public bool SetSlice(uint slice, bool isClear)
    {
        if (slice >= MaxSlices)
            return false;
        SetSliceClear(slice, isClear);
        return true;
    }

    // HTile slices of the depth target, so a rewrite can be read back slice by slice.
    public uint Layers = 1;

    // A dispatch rewrote these HTile words in place; their clear state is read from the
    // words before the depth target is next bound.
    public bool RewrittenByGpu;

    public ulong RangeSize;
    public bool NativeColorClear;
    public bool Invalidated;
}
