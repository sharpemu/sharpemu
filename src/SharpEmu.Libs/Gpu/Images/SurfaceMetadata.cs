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
    public uint ClearMask;
    public uint FillValue = 0xffffffff;
    public ulong FillSize;
    private System.Collections.BitArray? _upperSlices;

    // Sets slices 0-31 from the mask; the rest follow an all-clear or all-uncleared mask.
    public void SetFromMask(uint mask)
    {
        ClearMask = mask;
        if (mask == uint.MaxValue)
            (_upperSlices ??= new System.Collections.BitArray((int)(MaxSlices - 32))).SetAll(true);
        else
            _upperSlices?.SetAll(false);
    }

    public bool IsSliceCleared(uint slice) =>
        slice < 32 ? (ClearMask & (1u << (int)slice)) != 0 : slice < MaxSlices && _upperSlices is { } upper && upper[(int)(slice - 32)];

    public bool SetSlice(uint slice, bool isClear)
    {
        if (slice >= MaxSlices)
            return false;
        if (slice < 32)
        {
            ClearMask = isClear ? ClearMask | (1u << (int)slice) : ClearMask & ~(1u << (int)slice);
            return true;
        }

        (_upperSlices ??= new System.Collections.BitArray((int)(MaxSlices - 32)))[(int)(slice - 32)] = isClear;
        return true;
    }
}
