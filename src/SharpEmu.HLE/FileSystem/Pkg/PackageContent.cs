// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem.Pkg;

// A file a folder install of the package writes. Path is relative to the game folder, with '/'
// separators, and passed the same name checks Extract applies. Bytes holds sce_sys metadata read
// while listing (it touches the package no further); Native is the format's handle for the rest.
internal sealed record PackageContentFile(string Path, long Size, byte[]? Bytes, object? Native);

// The files of a package as a stream of byte ranges with their physical position in the .pkg, so
// a converter can write them anywhere without an extracted folder. Both package formats implement
// it next to Extract and list exactly what Extract writes.
internal interface IPackageContent
{
    PackageSource Source { get; }

    // In Extract's write order: sce_sys metadata first, then the game files. Later entries win when
    // two map to one path, as the later write does on disk.
    IReadOnlyList<PackageContentFile> ListContentFiles();

    // Package offset just past the last byte read to decode [offset, offset+length) of `file`;
    // 0 when nothing is read (metadata, empty ranges, sparse data).
    long SourceEnd(PackageContentFile file, long offset, long length);

    // A reader with its own decode state, for one thread.
    IPackageContentReader CreateContentReader();
}

internal interface IPackageContentReader : IDisposable
{
    // Fills destination with the file bytes at offset (the range lies inside the file).
    void Read(PackageContentFile file, long offset, Memory<byte> destination);
}
