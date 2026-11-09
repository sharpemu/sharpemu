// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private PendingLaunch? _activeLaunch;
    private GnmLoadExecSession? _loadExecSession;

    private bool TryRestartGnmSession(int exitCode)
    {
        if (_isClosing || _isStopping || _activeLaunch is not { } launch || _loadExecSession is not { } session ||
            !session.TryTakeRequest(exitCode, out var request))
            return false;
        _emulator?.Dispose();
        _emulator = null;
        DisposeLoadExecSession();
        _pendingLaunch = launch with { LoadExec = request };
        AppendConsoleLine($"[GNMMODE] Loading /app0/{request.Executable}", DimLineBrush);
        StartPendingSession();
        return true;
    }

    private void DisposeLoadExecSession()
    {
        _loadExecSession?.Dispose();
        _loadExecSession = null;
        _activeLaunch = null;
    }
}
