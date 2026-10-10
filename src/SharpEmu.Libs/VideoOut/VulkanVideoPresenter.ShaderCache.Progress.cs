// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private ShaderCacheProgress? _shaderCacheProgress;
        private int _precompileBatches;
        private int _precompileMerged;
        private int _precompileWorkersActive;
        private int _precompilePass;
        private long _shaderCacheSplashLastDraw;
        private bool _shaderCacheSplashVisible;
        private byte[]? _shaderCacheSplashSource;
        private byte[]? _shaderCacheSplashBackground;
        private byte[]? _shaderCacheSplashPixels;
        private uint _shaderCacheSplashWidth;
        private uint _shaderCacheSplashHeight;

        private void UpdateShaderCacheProgress(ShaderCachePhase phase, long completed = 0, long total = 0)
        {
            ShaderCacheProgress? previous;
            ShaderCacheProgress next;
            do
            {
                previous = Volatile.Read(ref _shaderCacheProgress);
                // Parallel scan callbacks can arrive out of byte-count order.
                if (previous is { Phase: ShaderCachePhase.Scanning } && phase == previous.Phase && total == previous.Total)
                    completed = Math.Max(completed, previous.Completed);
                next = new ShaderCacheProgress(phase, completed, total);
            }
            while (!ReferenceEquals(Interlocked.CompareExchange(ref _shaderCacheProgress, next, previous), previous));
            WakeRenderThread();
        }

        private bool ShaderCacheSplashNeedsRefresh =>
            Volatile.Read(ref _shaderCacheProgress) is not null
                ? Stopwatch.GetElapsedTime(_shaderCacheSplashLastDraw).TotalMilliseconds >= 100
                : _shaderCacheSplashVisible;

        private byte[] DrawShaderCacheSplash(in Presentation presentation, ShaderCacheProgress stage)
        {
            if (_shaderCacheSplashSource != presentation.Pixels ||
                _shaderCacheSplashWidth != _extent.Width || _shaderCacheSplashHeight != _extent.Height)
            {
                _shaderCacheSplashSource = presentation.Pixels;
                _shaderCacheSplashWidth = _extent.Width;
                _shaderCacheSplashHeight = _extent.Height;
                _shaderCacheSplashBackground = presentation.Width == _extent.Width && presentation.Height == _extent.Height
                    ? presentation.Pixels!
                    : ScaleBgra(presentation.Pixels!, presentation.Width, presentation.Height, _extent.Width, _extent.Height);
                _shaderCacheSplashPixels = new byte[_shaderCacheSplashBackground.Length];
            }

            _shaderCacheSplashBackground!.CopyTo(_shaderCacheSplashPixels!, 0);
            var snapshot = new ShaderCacheProgressSnapshot(stage,
                Volatile.Read(ref _precompileComputeDone), Volatile.Read(ref _precompileComputeTotal),
                Volatile.Read(ref _precompileGraphicsDone), Volatile.Read(ref _precompileGraphicsTotal),
                Volatile.Read(ref _precompileMerged), Volatile.Read(ref _precompileBatches),
                Volatile.Read(ref _precompileCompiled), Volatile.Read(ref _precompileFromStore), Volatile.Read(ref _precompileFailed),
                Volatile.Read(ref _precompileWorkersActive), Volatile.Read(ref _precompilePass),
                _precompileStarted == 0 ? 0 : Stopwatch.GetElapsedTime(_precompileStarted).TotalSeconds);
            ShaderCacheProgressOverlay.Draw(_shaderCacheSplashPixels!, (int)_extent.Width, (int)_extent.Height, snapshot);
            return _shaderCacheSplashPixels!;
        }

        private void ClearShaderCacheSplash()
        {
            _shaderCacheSplashSource = _shaderCacheSplashBackground = _shaderCacheSplashPixels = null;
            _shaderCacheSplashVisible = false;
        }
    }
}
