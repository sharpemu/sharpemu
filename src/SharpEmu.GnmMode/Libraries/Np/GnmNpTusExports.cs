// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.GnmMode.Libraries.Np;

public static class GnmNpTusExports
{
    private const int InvalidArgument = unchecked((int)0x80550704);
    private const int NoLogin = unchecked((int)0x80550705);
    private const int TooManyObjects = unchecked((int)0x80550706);
    private const int InsufficientArgument = unchecked((int)0x8055070C);
    private const int InvalidId = unchecked((int)0x8055070E);
    private const int InvalidOnlineId = unchecked((int)0x8055070F);
    private static readonly object Gate = new();
    private static readonly HashSet<int> Contexts = [];
    private static readonly Dictionary<int, Request> Requests = [];
    private static int _nextId;

    private sealed class Request(int context)
    {
        public int Context { get; } = context;
        public bool Started { get; set; }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            Contexts.Clear();
            Requests.Clear();
            _nextId = 0;
        }
    }

    public static int CreateNpTitleCtx(CpuContext context)
    {
        if ((uint)context[CpuRegister.Rdi] == uint.MaxValue)
            return context.SetReturn(InvalidArgument);
        Span<byte> npId = stackalloc byte[36];
        if (context[CpuRegister.Rsi] == 0 || !context.Memory.TryRead(context[CpuRegister.Rsi], npId))
            return context.SetReturn(InsufficientArgument);
        if (npId[16] != 0 || npId[..16].IndexOfAnyExcept((byte)0) < 0)
            return context.SetReturn(InvalidOnlineId);
        lock (Gate)
        {
            if (_nextId == int.MaxValue)
                return context.SetReturn(TooManyObjects);
            var id = ++_nextId;
            Contexts.Add(id);
            return context.SetReturn(id);
        }
    }

    public static int DeleteNpTitleCtx(CpuContext context)
    {
        lock (Gate)
        {
            var id = (int)context[CpuRegister.Rdi];
            if (!Contexts.Remove(id))
                return context.SetReturn(InvalidId);
            foreach (var request in Requests.Where(pair => pair.Value.Context == id).Select(pair => pair.Key).ToArray())
                Requests.Remove(request);
            return context.SetReturn(0);
        }
    }

    public static int CreateRequest(CpuContext context)
    {
        lock (Gate)
        {
            var parent = (int)context[CpuRegister.Rdi];
            if (!Contexts.Contains(parent))
                return context.SetReturn(InvalidId);
            if (_nextId == int.MaxValue)
                return context.SetReturn(TooManyObjects);
            var id = ++_nextId;
            Requests.Add(id, new Request(parent));
            return context.SetReturn(id);
        }
    }

    public static int DeleteRequest(CpuContext context)
    {
        lock (Gate)
            return context.SetReturn(Requests.Remove((int)context[CpuRegister.Rdi]) ? 0 : InvalidId);
    }

    public static int BeginOfflineRequest(CpuContext context)
    {
        lock (Gate)
        {
            if (!Requests.TryGetValue((int)context[CpuRegister.Rdi], out var request))
                return context.SetReturn(InvalidId);
            request.Started = true;
            return context.SetReturn(0);
        }
    }

    public static int PollAsync(CpuContext context)
    {
        lock (Gate)
        {
            if (!Requests.TryGetValue((int)context[CpuRegister.Rdi], out var request))
                return context.SetReturn(InvalidId);
            if (!request.Started)
                return context.SetReturn(1);
            var output = context[CpuRegister.Rsi];
            return context.SetReturn(output == 0 || context.TryWriteUInt32(output, unchecked((uint)NoLogin)) ? 0 : InvalidArgument);
        }
    }
}
