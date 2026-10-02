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
    public ulong RangeSize;
    public bool NativeColorClear;
    public bool Invalidated;
}
