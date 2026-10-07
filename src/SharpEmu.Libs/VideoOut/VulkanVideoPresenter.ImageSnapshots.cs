// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private static readonly ulong _snapshotPixelHash = ulong.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_SNAPSHOT_PIXEL_HASH")?.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash) ? hash : 0;
        private readonly string _imageSnapshotSession = Guid.NewGuid().ToString("N");
        private int _imageSnapshotCount;
        private static readonly ulong _snapshotComputeHash = ulong.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_SNAPSHOT_COMPUTE_HASH")?.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var computeHash) ? computeHash : 0;
        private int _computeSnapshotCount;

        int IRenderHost.BeginComputeImageSnapshot(IPreparedBindings bindings)
        {
            if (_snapshotComputeHash == 0 || _computeSnapshotCount >= 2 ||
                bindings is not PreparedStageBindings prepared || prepared.Program.Hash != _snapshotComputeHash) return 0;
            var sequence = ++_computeSnapshotCount;
            SaveComputeImages(sequence, prepared, after: false);
            return sequence;
        }

        void IRenderHost.EndComputeImageSnapshot(int sequence, IPreparedBindings bindings)
        {
            if (sequence != 0 && bindings is PreparedStageBindings prepared)
                SaveComputeImages(sequence, prepared, after: true);
        }

        private void SaveComputeImages(int sequence, PreparedStageBindings prepared, bool after)
        {
            for (var index = 0; index < prepared.Textures.Length; index++)
            {
                var texture = prepared.Textures[index];
                if (after && !texture.IsStorage) continue;
                var phase = after ? "after" : "before";
                var path = Path.Combine(AppContext.BaseDirectory, "user", "logs", "image-snapshots",
                    _imageSnapshotSession, $"{_snapshotComputeHash:X16}-compute-{sequence}-{phase}-image-{index}");
                if (texture.CachedImage is { } image)
                    _imageCache.SaveDiagnosticImage(image, path);
                else Console.Error.WriteLine($"[GPU][WARN] ImageSnapshot compute image={index} has no cached image.");
            }
        }

        int IRenderHost.BeginImageSnapshot(IPreparedBindings? bindings)
        {
            if (_snapshotPixelHash == 0 || _imageSnapshotCount >= 2 ||
                bindings is not PreparedStageBindings prepared || prepared.Program.Hash != _snapshotPixelHash) return 0;
            var sequence = ++_imageSnapshotCount;
            for (var index = 0; index < prepared.Textures.Length; index++)
            {
                var texture = prepared.Textures[index];
                if (texture.CachedImage is { } image)
                    _imageCache.SaveDiagnosticImage(image, SnapshotPath(sequence, $"input-{index}"));
                else Console.Error.WriteLine($"[GPU][WARN] ImageSnapshot input={index} has no cached image.");
            }
            return sequence;
        }

        void IRenderHost.EndImageSnapshot(int sequence, ReadOnlySpan<ColorTargetState> targets)
        {
            if (sequence == 0) return;
            foreach (var target in targets)
                _imageCache.SaveDiagnosticImage(_imageCache.GetImage(target.Image), SnapshotPath(sequence, $"output-{target.Slot}"));
        }

        private string SnapshotPath(int sequence, string label) => Path.Combine(AppContext.BaseDirectory,
            "user", "logs", "image-snapshots", _imageSnapshotSession, $"{_snapshotPixelHash:X16}-{sequence}-{label}");
    }
}
