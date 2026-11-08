// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const double ShaderCacheSplashRefreshMilliseconds = 100;

        private Func<ShaderCacheProgressView>? _shaderCacheProgress;
        private long _shaderCacheSplashLastDraw;
        private bool _shaderCacheSplashVisible;
        private byte[]? _shaderCacheSplashSource;
        private byte[]? _shaderCacheSplashBackground;
        private byte[]? _shaderCacheSplashPixels;
        private uint _shaderCacheSplashWidth;
        private uint _shaderCacheSplashHeight;

        private void ShowShaderCacheProgress(Func<ShaderCacheProgressView> progress)
        {
            Volatile.Write(ref _shaderCacheProgress, progress);
            WakeRenderThread();
        }

        private void HideShaderCacheProgress(Func<ShaderCacheProgressView> progress)
        {
            Interlocked.CompareExchange(ref _shaderCacheProgress, null, progress);
            WakeRenderThread();
        }

        private bool ShaderCacheSplashNeedsRefresh =>
            Volatile.Read(ref _shaderCacheProgress) is not null
                ? Stopwatch.GetElapsedTime(_shaderCacheSplashLastDraw).TotalMilliseconds >= ShaderCacheSplashRefreshMilliseconds
                : _shaderCacheSplashVisible;

        private byte[] DrawShaderCacheSplash(in Presentation presentation, Func<ShaderCacheProgressView> progress)
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
            ShaderCacheProgressOverlay.Draw(_shaderCacheSplashPixels!, (int)_extent.Width, (int)_extent.Height, progress());
            return _shaderCacheSplashPixels!;
        }

        private void NoteShaderCacheSplashPresented(bool drawn)
        {
            if (drawn)
            {
                _shaderCacheSplashLastDraw = Stopwatch.GetTimestamp();
                _shaderCacheSplashVisible = true;
            }
            else if (_shaderCacheSplashVisible)
            {
                _shaderCacheSplashSource = _shaderCacheSplashBackground = _shaderCacheSplashPixels = null;
                _shaderCacheSplashVisible = false;
            }
        }
    }
}
