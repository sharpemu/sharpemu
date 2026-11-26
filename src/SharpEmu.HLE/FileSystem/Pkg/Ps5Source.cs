// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32.SafeHandles;

namespace SharpEmu.HLE.FileSystem.Pkg;

// Positional reads over the package file; every read is range checked.
internal sealed class Ps5Source : IDisposable
{
    private readonly SafeFileHandle _handle;

    public Ps5Source(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    public void ReadExactAt(Span<byte> buffer, long offset)
    {
        if (offset < 0 || offset > Length || buffer.Length > Length - offset)
        {
            throw new InvalidDataException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Read of {buffer.Length:N0} bytes at 0x{offset:X} is past the end of the {Length:N0}-byte package."));
        }

        var done = 0;
        while (done < buffer.Length)
        {
            var read = RandomAccess.Read(_handle, buffer[done..], offset + done);
            if (read == 0)
            {
                throw new InvalidDataException("The package file ended unexpectedly.");
            }

            done += read;
        }
    }

    public byte[] ReadBytes(long length, long offset)
    {
        if (length < 0 || length > int.MaxValue || offset < 0 || offset > Length || length > Length - offset)
        {
            throw new InvalidDataException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Read of {length:N0} bytes at 0x{offset:X} is past the end of the {Length:N0}-byte package."));
        }

        var bytes = new byte[length];
        ReadExactAt(bytes, offset);
        return bytes;
    }

    public void Dispose() => _handle.Dispose();
}
