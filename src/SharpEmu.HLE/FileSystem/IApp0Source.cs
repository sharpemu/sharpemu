// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem;

// A read-only game folder served without being extracted: a .zar archive or a PS5 package. Nodes
// are opaque ints; lookup ignores case and takes '/' or '\' separators. Every member is safe to
// call from several threads at once.
public interface IApp0Source : IDisposable
{
    public const int InvalidNode = -1;

    // The file the source reads (the archive or the package).
    string Path { get; }

    // The node at the path; the root for an empty path; InvalidNode when it is missing.
    int LookUp(string path);

    bool IsFile(int node);

    bool IsDirectory(int node);

    ulong GetFileSize(int node);

    IReadOnlyList<ZArchive.Entry> GetEntries(int directory);

    // Copies file bytes from offset into destination; returns the count read, short at the end of
    // the file.
    int Read(int file, ulong offset, Span<byte> destination);
}
