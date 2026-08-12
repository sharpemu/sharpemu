// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

internal enum NpCommerceDialogStatus
{
    None = 0,
    Initialized = 1,
    Running = 2,
    Finished = 3,
}

internal sealed record NpCommerceDialogRequest(
    bool IsOpen2,
    int UserId,
    int Mode,
    uint ServiceLabel,
    ulong Features,
    ulong UserData,
    IReadOnlyList<string> Targets);

internal static class NpCommerceDialogState
{
    internal const int ErrorOk = 0;
    internal const int ErrorNotInitialized = unchecked((int)0x80B80003);
    internal const int ErrorAlreadyInitialized = unchecked((int)0x80B80004);
    internal const int ErrorNotFinished = unchecked((int)0x80B80005);
    internal const int ErrorBusy = unchecked((int)0x80B80008);
    internal const int ErrorParameterInvalid = unchecked((int)0x80B8000A);
    internal const int ErrorArgumentNull = unchecked((int)0x80B8000D);
    internal const int ResultUserCanceled = 1;

    private static readonly object Gate = new();
    private static NpCommerceDialogStatus _status;
    private static NpCommerceDialogRequest? _request;

    internal static int Initialize()
    {
        lock (Gate)
        {
            if (_status != NpCommerceDialogStatus.None)
            {
                return ErrorAlreadyInitialized;
            }

            _status = NpCommerceDialogStatus.Initialized;
            _request = null;
            return ErrorOk;
        }
    }

    internal static int Terminate()
    {
        lock (Gate)
        {
            if (_status == NpCommerceDialogStatus.None)
            {
                return ErrorNotInitialized;
            }

            _status = NpCommerceDialogStatus.None;
            _request = null;
            return ErrorOk;
        }
    }

    internal static int Open(NpCommerceDialogRequest request)
    {
        lock (Gate)
        {
            if (_status == NpCommerceDialogStatus.None)
            {
                return ErrorNotInitialized;
            }

            if (_status == NpCommerceDialogStatus.Running)
            {
                return ErrorBusy;
            }

            if (request.Mode is < 0 or > 5)
            {
                _status = NpCommerceDialogStatus.Finished;
                _request = request;
                return ErrorParameterInvalid;
            }

            _request = request;
            _status = NpCommerceDialogStatus.Running;
            return ErrorOk;
        }
    }

    internal static int UpdateStatus()
    {
        lock (Gate)
        {
            if (_status == NpCommerceDialogStatus.Running)
            {
                _status = NpCommerceDialogStatus.Finished;
            }

            return (int)_status;
        }
    }

    internal static int TryGetResult(out ulong userData)
    {
        lock (Gate)
        {
            if (_status != NpCommerceDialogStatus.Finished || _request is null)
            {
                userData = 0;
                return ErrorNotFinished;
            }

            userData = _request.UserData;
            return ErrorOk;
        }
    }

    internal static NpCommerceDialogStatus Status
    {
        get
        {
            lock (Gate)
            {
                return _status;
            }
        }
    }

    internal static NpCommerceDialogRequest? RequestForTests
    {
        get
        {
            lock (Gate)
            {
                return _request;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _status = NpCommerceDialogStatus.None;
            _request = null;
        }
    }
}
