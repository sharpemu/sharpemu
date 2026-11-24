// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SkiaSharp;

namespace SharpEmu.Libs.Overlay;

public sealed class OverlayImages : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SKImage?> _images = new(StringComparer.OrdinalIgnoreCase);

    public SKImage? FromFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_images.TryGetValue(path, out var image))
            {
                try
                {
                    using var data = SKData.Create(path);
                    image = data is null ? null : SKImage.FromEncodedData(data);
                }
                catch (IOException)
                {
                    image = null;
                }

                _images[path] = image;
            }

            return image;
        }
    }

    public SKImage? FromBytes(string key, byte[]? encoded)
    {
        if (encoded is null || encoded.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_images.TryGetValue(key, out var image))
            {
                using var data = SKData.CreateCopy(encoded);
                image = SKImage.FromEncodedData(data);
                _images[key] = image;
            }

            return image;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var image in _images.Values)
            {
                image?.Dispose();
            }

            _images.Clear();
        }
    }

    public void Dispose() => Clear();
}
