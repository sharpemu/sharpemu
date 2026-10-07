// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

public sealed class HostBackingObject : IDisposable
{
    private readonly Action<HostBackingObject> _release;

    internal readonly object Gate = new();
    internal nint Handle;
    internal int Descriptor = -1;

    internal HostBackingObject(ulong aliasBase, ulong size, Action<HostBackingObject> release)
    {
        AliasBase = aliasBase;
        Size = size;
        _release = release;
    }

    public ulong AliasBase { get; }

    public ulong Size { get; }

    internal bool IsDisposed { get; private set; }

    // Wait for an active mapping operation. The owner must remove guest views before disposal.
    public void Dispose()
    {
        lock (Gate)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _release(this);
        }
    }
}
