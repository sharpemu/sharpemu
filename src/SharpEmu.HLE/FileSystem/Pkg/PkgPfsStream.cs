// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;

namespace SharpEmu.HLE.FileSystem.Pkg;

// A read-only, seekable view of a PKG's PFS image with the XTS sector encryption removed. The
// tweak of a sector is its absolute number in the image; sectors before
// PkgCrypto.FirstEncryptedSector are plain.
public sealed class PkgPfsStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly Aes? _dataCipher;
    private readonly Aes? _tweakCipher;
    private long _position;
    private byte[] _scratch = new byte[PkgCrypto.SectorSize];

    // Pass null keys for an image that is not encrypted.
    public PkgPfsStream(Stream inner, long imageOffset, long imageSize, byte[]? dataKey, byte[]? tweakKey)
    {
        _inner = inner;
        _start = imageOffset;
        _length = imageSize;
        if (dataKey is not null && tweakKey is not null)
        {
            _dataCipher = Aes.Create();
            _dataCipher.Key = dataKey;
            _tweakCipher = Aes.Create();
            _tweakCipher.Key = tweakKey;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    // Package offset just past the last byte a read of [position, position+count) of the image
    // touches: reads cover whole sectors, clipped to the image and to the package file.
    internal long SourceEnd(long position, long count)
    {
        if (count <= 0 || position >= _length)
        {
            return 0;
        }

        var end = position + Math.Min(count, _length - position);
        var alignedEnd = (end + PkgCrypto.SectorSize - 1) & ~(long)(PkgCrypto.SectorSize - 1);
        return Math.Min(_start + alignedEnd, _inner.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        var remaining = _length - _position;
        if (remaining <= 0 || destination.IsEmpty)
        {
            return 0;
        }

        if (destination.Length > remaining)
        {
            destination = destination[..(int)remaining];
        }

        var alignedStart = _position & ~(long)(PkgCrypto.SectorSize - 1);
        var alignedEnd = (_position + destination.Length + PkgCrypto.SectorSize - 1) & ~(long)(PkgCrypto.SectorSize - 1);
        var span = (int)(alignedEnd - alignedStart);
        if (_scratch.Length < span)
        {
            _scratch = new byte[span];
        }

        var window = _scratch.AsSpan(0, span);
        _inner.Position = _start + alignedStart;
        var filled = 0;
        while (filled < span)
        {
            var read = _inner.Read(window[filled..]);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        // Sectors cut short by the end of the file are zero padded: PFS images are sector sized.
        window[filled..].Clear();

        if (_dataCipher is not null && _tweakCipher is not null)
        {
            for (var offset = 0; offset < span; offset += PkgCrypto.SectorSize)
            {
                var sector = (alignedStart + offset) / PkgCrypto.SectorSize;
                if (sector >= PkgCrypto.FirstEncryptedSector)
                {
                    PkgCrypto.XtsDecrypt(_dataCipher, _tweakCipher,
                        window.Slice(offset, PkgCrypto.SectorSize), (ulong)sector);
                }
            }
        }

        window.Slice((int)(_position - alignedStart), destination.Length).CopyTo(destination);
        _position += destination.Length;
        return destination.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dataCipher?.Dispose();
            _tweakCipher?.Dispose();
        }

        base.Dispose(disposing);
    }
}
