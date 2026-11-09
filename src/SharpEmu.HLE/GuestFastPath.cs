// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Per-host-thread state that emitted native x86-64 import stubs can read
/// without entering managed code.
///
/// The hottest guest imports are trivial queries about the calling thread
/// (pthread_self, pthread_getspecific). Routing them through the managed
/// import gateway costs a guest-to-host stack switch, a full guest register
/// pack, a GCHandle dereference and the whole dispatch bookkeeping - roughly
/// three orders of magnitude more than the answer itself. This class publishes
/// the few values those imports need into a flat native block whose address
/// lives in a native TLS slot, so a stub can answer them in a handful of
/// instructions and fall back to the managed gateway whenever the fast answer
/// is not available.
///
/// The block is owned by the host thread; the values in it describe the guest
/// thread currently bound to that host thread, and are republished by
/// <see cref="GuestThreadExecution.EnterGuestThread"/> /
/// <see cref="GuestThreadExecution.RestoreGuestThread"/>. A block whose
/// ThreadHandle is zero means "no guest thread bound here": every stub then
/// takes the managed path, which is also what happens before the fast path is
/// enabled at all.
/// </summary>
public static unsafe class GuestFastPath
{
    /// <summary>
    /// Byte offset of the guest thread handle pthread_self must answer with.
    /// Only ever a real guest thread handle: for a host thread with no guest
    /// thread bound, pthread_self has to reach the managed export (it registers
    /// the thread's CPU context with the scheduler), so the slot stays zero.
    /// </summary>
    public const int BlockSelfHandleOffset = 0;

    /// <summary>
    /// Byte offset of the pointer to the pthread-specific value table of the
    /// thread handle KernelPthreadState would report - the bound guest thread,
    /// or the host thread's own synthetic handle when none is bound.
    /// </summary>
    public const int BlockTlsValuesOffset = 8;

    /// <summary>Byte offset of the pthread_self fast-path hit counter.</summary>
    public const int BlockSelfHitsOffset = 16;

    /// <summary>Byte offset of the pthread_getspecific fast-path hit counter.</summary>
    public const int BlockGetspecificHitsOffset = 32;

    /// <summary>Size of the per-host-thread block. One cache line, room for future fast-path state.</summary>
    public const int BlockSize = 64;

    /// <summary>
    /// pthread TLS keys the native table can answer. Keys are handed out
    /// densely from 1 upwards by pthread_key_create, so a small dense table
    /// covers every key a real title creates; anything at or above this limit
    /// is answered by the managed dictionary through the slow path.
    /// </summary>
    public const int TlsSlotCount = 1024;

    private static readonly ConcurrentDictionary<ulong, nint> TlsTables = new();

    /// <summary>Values for keys at or above <see cref="TlsSlotCount"/>; effectively never used.</summary>
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<int, ulong>> TlsOverflow = new();

    private static readonly List<nint> Blocks = new();
    private static readonly object BlocksGate = new();

    private static Action<nint>? _publishBlock;
    private static volatile bool _enabled;

    [ThreadStatic]
    private static nint _block;

    [ThreadStatic]
    private static ulong _hostThreadHandle;

    [ThreadStatic]
    private static ulong _cachedTableHandle;

    [ThreadStatic]
    private static nint _cachedTable;

    /// <summary>True once a backend has installed native stubs that read the block.</summary>
    public static bool Enabled => _enabled;

    /// <summary>
    /// Arms the fast path. <paramref name="publishBlock"/> stores a block
    /// pointer in the native TLS slot the emitted stubs read, for the host
    /// thread that calls it.
    /// </summary>
    public static void Enable(Action<nint> publishBlock)
    {
        _publishBlock = publishBlock ?? throw new ArgumentNullException(nameof(publishBlock));
        _enabled = true;

        // A block is published once per host thread, so a second backend (and
        // therefore a second TLS slot) must be handed the block this thread
        // already owns or its stubs would never see one.
        if (_block != 0)
        {
            _publishBlock(_block);
        }
    }

    /// <summary>Disarms every stub by clearing the calling thread's block; used by tests.</summary>
    public static void Disable()
    {
        _enabled = false;
        var block = _block;
        if (block != 0)
        {
            *(ulong*)(block + BlockSelfHandleOffset) = 0;
            *(nint*)(block + BlockTlsValuesOffset) = 0;
        }
    }

    /// <summary>
    /// Publishes <paramref name="guestThreadHandle"/> (and its pthread TLS
    /// table) as the guest thread bound to the calling host thread. Pass 0 to
    /// unbind, which leaves pthread_self on the managed gateway and falls the
    /// value table back to the host thread's own pthread storage.
    /// </summary>
    public static void BindGuestThread(ulong guestThreadHandle)
    {
        if (!_enabled)
        {
            return;
        }

        var block = EnsureBlock();
        if (block == 0)
        {
            return;
        }

        // Clear the self handle first so a stub can never answer with a handle
        // that no longer owns this host thread.
        *(ulong*)(block + BlockSelfHandleOffset) = 0;
        var tlsHandle = guestThreadHandle != 0 ? guestThreadHandle : _hostThreadHandle;
        *(nint*)(block + BlockTlsValuesOffset) = tlsHandle == 0 ? 0 : GetOrCreateTable(tlsHandle);
        *(ulong*)(block + BlockSelfHandleOffset) = guestThreadHandle;
    }

    /// <summary>
    /// Records the calling host thread's own pthread handle - the one
    /// KernelPthreadState reports when no guest thread is bound - so
    /// pthread_getspecific can be answered on threads the guest scheduler does
    /// not own (the primary execution thread among them).
    /// </summary>
    public static void BindHostThread(ulong hostThreadHandle)
    {
        if (!_enabled || hostThreadHandle == 0)
        {
            return;
        }

        _hostThreadHandle = hostThreadHandle;
        var block = EnsureBlock();
        if (block == 0 || *(ulong*)(block + BlockSelfHandleOffset) != 0)
        {
            // A bound guest thread owns the value table while it runs.
            return;
        }

        *(nint*)(block + BlockTlsValuesOffset) = GetOrCreateTable(hostThreadHandle);
    }

    /// <summary>
    /// Drops every fast-path binding for the calling host thread, so each stub
    /// falls back to the managed gateway until the thread is bound again.
    /// </summary>
    public static void UnbindCurrentThread()
    {
        _hostThreadHandle = 0;
        var block = _block;
        if (block != 0)
        {
            *(ulong*)(block + BlockSelfHandleOffset) = 0;
            *(nint*)(block + BlockTlsValuesOffset) = 0;
        }
    }

    /// <summary>Fast-path hit counts summed over every host thread, for diagnostics.</summary>
    public static (long SelfHits, long GetspecificHits, int Blocks) SnapshotCounters()
    {
        long selfHits = 0;
        long getspecificHits = 0;
        int blocks;
        lock (BlocksGate)
        {
            blocks = Blocks.Count;
            foreach (var block in Blocks)
            {
                selfHits += *(long*)(block + BlockSelfHitsOffset);
                getspecificHits += *(long*)(block + BlockGetspecificHitsOffset);
            }
        }

        return (selfHits, getspecificHits, blocks);
    }

    /// <summary>Address of the calling host thread's block; allocates it on first use.</summary>
    public static nint EnsureBlock()
    {
        var block = _block;
        if (block != 0)
        {
            return block;
        }

        block = (nint)NativeMemory.AllocZeroed(BlockSize);
        _block = block;
        lock (BlocksGate)
        {
            Blocks.Add(block);
        }

        _publishBlock?.Invoke(block);
        return block;
    }

    /// <summary>The pthread-specific value table for a guest (or host) thread handle.</summary>
    public static nint GetOrCreateTable(ulong threadHandle)
    {
        if (threadHandle == 0)
        {
            return 0;
        }

        if (_cachedTableHandle == threadHandle && _cachedTable != 0)
        {
            return _cachedTable;
        }

        var table = TlsTables.GetOrAdd(
            threadHandle,
            static _ => (nint)NativeMemory.AllocZeroed((nuint)TlsSlotCount, sizeof(ulong)));
        _cachedTableHandle = threadHandle;
        _cachedTable = table;
        return table;
    }

    /// <summary>pthread_getspecific, for the managed (slow) path and for keys the table cannot hold.</summary>
    public static ulong GetSpecific(ulong threadHandle, int key)
    {
        if (threadHandle == 0 || key < 0)
        {
            return 0;
        }

        if ((uint)key < TlsSlotCount)
        {
            return TlsTables.TryGetValue(threadHandle, out var table) && table != 0
                ? ((ulong*)table)[key]
                : 0;
        }

        return TlsOverflow.TryGetValue(threadHandle, out var overflow) && overflow.TryGetValue(key, out var value)
            ? value
            : 0;
    }

    /// <summary>pthread_setspecific. Writes the same storage the native stubs read.</summary>
    public static void SetSpecific(ulong threadHandle, int key, ulong value)
    {
        if (threadHandle == 0 || key < 0)
        {
            return;
        }

        if ((uint)key < TlsSlotCount)
        {
            ((ulong*)GetOrCreateTable(threadHandle))[key] = value;
            return;
        }

        TlsOverflow
            .GetOrAdd(threadHandle, static _ => new ConcurrentDictionary<int, ulong>())[key] = value;
    }

    /// <summary>
    /// Compare-and-set used by the destructor loop: clears <paramref name="key"/>
    /// only when it still holds <paramref name="expected"/>, so a destructor that
    /// re-sets the key is handled on the next iteration instead of being lost.
    /// </summary>
    public static bool TryClearSpecific(ulong threadHandle, int key, ulong expected)
    {
        if (threadHandle == 0 || key < 0)
        {
            return false;
        }

        if ((uint)key < TlsSlotCount)
        {
            if (!TlsTables.TryGetValue(threadHandle, out var table) || table == 0)
            {
                return false;
            }

            return Interlocked.CompareExchange(ref ((ulong*)table)[key], 0, expected) == expected;
        }

        return TlsOverflow.TryGetValue(threadHandle, out var overflow) &&
            overflow.TryUpdate(key, 0, expected);
    }

    /// <summary>pthread_key_delete: the key reads back as unset on every thread.</summary>
    public static void ClearKeyEverywhere(int key)
    {
        if (key < 0)
        {
            return;
        }

        if ((uint)key < TlsSlotCount)
        {
            foreach (var table in TlsTables.Values)
            {
                if (table != 0)
                {
                    ((ulong*)table)[key] = 0;
                }
            }

            return;
        }

        foreach (var overflow in TlsOverflow.Values)
        {
            overflow.TryRemove(key, out _);
        }
    }

    /// <summary>Every non-zero (key, value) pair a thread currently holds, for the destructor loop.</summary>
    public static List<KeyValuePair<int, ulong>> SnapshotThreadValues(ulong threadHandle)
    {
        var result = new List<KeyValuePair<int, ulong>>();
        if (threadHandle == 0)
        {
            return result;
        }

        if (TlsTables.TryGetValue(threadHandle, out var table) && table != 0)
        {
            var values = (ulong*)table;
            for (var key = 0; key < TlsSlotCount; key++)
            {
                if (values[key] != 0)
                {
                    result.Add(new KeyValuePair<int, ulong>(key, values[key]));
                }
            }
        }

        if (TlsOverflow.TryGetValue(threadHandle, out var overflow))
        {
            foreach (var entry in overflow)
            {
                if (entry.Value != 0)
                {
                    result.Add(entry);
                }
            }
        }

        return result;
    }

    /// <summary>True when the thread has ever stored a pthread-specific value.</summary>
    public static bool HasThreadValues(ulong threadHandle) =>
        threadHandle != 0 &&
        (TlsTables.ContainsKey(threadHandle) || TlsOverflow.ContainsKey(threadHandle));

    /// <summary>
    /// Drops a thread's pthread-specific storage on thread exit. Called from
    /// the exiting thread after its destructors have run, so no stub can still
    /// be reading the table; the calling thread's block is disarmed first.
    /// </summary>
    public static void ReleaseThread(ulong threadHandle)
    {
        if (threadHandle == 0)
        {
            return;
        }

        var block = _block;
        if (block != 0 && *(ulong*)(block + BlockSelfHandleOffset) == threadHandle)
        {
            *(ulong*)(block + BlockSelfHandleOffset) = 0;
            *(nint*)(block + BlockTlsValuesOffset) = 0;
        }
        else if (block != 0 && _hostThreadHandle == threadHandle)
        {
            *(nint*)(block + BlockTlsValuesOffset) = 0;
        }

        if (_cachedTableHandle == threadHandle)
        {
            _cachedTableHandle = 0;
            _cachedTable = 0;
        }

        TlsOverflow.TryRemove(threadHandle, out _);
        if (TlsTables.TryRemove(threadHandle, out var table) && table != 0)
        {
            NativeMemory.Free((void*)table);
        }
    }
}
