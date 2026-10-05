// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe class CpuPatcher : IDisposable
{
    private readonly nint _tlsBaseAddress;

    public CpuPatcher(nint tlsBaseAddress)
    {
        _tlsBaseAddress = tlsBaseAddress;
    }

    public bool TryPatchInstruction(nint address)
    {
        return false;
    }

    public void Dispose()
    {
    }

    internal unsafe class UnsafeCodeReader : Iced.Intel.CodeReader
    {
        private readonly byte* _start;
        private readonly ulong _length;
        public ulong Position { get; private set; }

        public UnsafeCodeReader(byte* start, ulong length)
        {
            _start = start;
            _length = length;
        }

        public override int ReadByte()
        {
            if (Position >= _length)
                return -1;
            return _start[Position++];
        }
    }
}
