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
    public uint ClearMask;
    public uint FillValue = 0xffffffff;
    public ulong FillSize;
}
