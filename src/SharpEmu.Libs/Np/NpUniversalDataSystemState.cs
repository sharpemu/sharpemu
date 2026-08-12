// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.Libs.Np;

internal enum UdsValueKind
{
    String,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    Bool,
    Object,
    Array,
}

internal readonly record struct UdsValue(UdsValueKind Kind, object Value);

internal abstract class UdsNode;

internal sealed class UdsObjectNode : UdsNode
{
    public Dictionary<string, UdsValue> Values { get; } = new(StringComparer.Ordinal);
}

internal sealed class UdsArrayNode : UdsNode
{
    public List<UdsValue> Values { get; } = [];
}

internal sealed record UdsPostedEvent(string Name, UdsObjectNode Root);

internal readonly record struct UdsMemoryStat(ulong PoolSize, ulong MaximumInUseSize, ulong CurrentInUseSize);

internal static class NpUniversalDataSystemState
{
    private const int MaximumPostedEvents = 64;
    private const ulong EventHandleBase = 0xE000_0000_0000_0000;
    private const ulong ObjectHandleBase = 0xD000_0000_0000_0000;
    private const ulong ArrayHandleBase = 0xC000_0000_0000_0000;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, UdsContext> Contexts = [];
    private static readonly Dictionary<int, UdsServiceHandle> ServiceHandles = [];
    private static readonly Dictionary<ulong, UdsEvent> Events = [];
    private static readonly Dictionary<ulong, UdsObjectNode> Objects = [];
    private static readonly Dictionary<ulong, UdsArrayNode> Arrays = [];
    private static readonly Queue<UdsPostedEvent> PostedEvents = [];
    private static bool _initialized;
    private static ulong _poolSize;
    private static ulong _maximumInUseSize;
    private static int _nextContext;
    private static int _nextServiceHandle;
    private static ulong _nextEvent;
    private static ulong _nextObject;
    private static ulong _nextArray;

    internal static bool Initialize(ulong poolSize)
    {
        lock (Gate)
        {
            ResetLocked();
            _initialized = true;
            _poolSize = poolSize;
            return true;
        }
    }

    internal static void Terminate()
    {
        lock (Gate)
        {
            ResetLocked();
        }
    }

    internal static bool TryCreateContext(int userId, uint serviceLabel, ulong options, out int context)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                context = 0;
                return false;
            }

            context = NextPositiveId(ref _nextContext);
            Contexts.Add(context, new UdsContext(userId, serviceLabel, options));
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool DestroyContext(int context)
    {
        lock (Gate)
        {
            return _initialized && Contexts.Remove(context);
        }
    }

    internal static bool TryCreateServiceHandle(out int handle)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                handle = 0;
                return false;
            }

            handle = NextPositiveId(ref _nextServiceHandle);
            ServiceHandles.Add(handle, new UdsServiceHandle());
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool DestroyServiceHandle(int handle)
    {
        lock (Gate)
        {
            return _initialized && ServiceHandles.Remove(handle);
        }
    }

    internal static bool AbortServiceHandle(int handle)
    {
        lock (Gate)
        {
            if (!_initialized || !ServiceHandles.TryGetValue(handle, out var state))
            {
                return false;
            }

            state.Aborted = true;
            return true;
        }
    }

    internal static bool RegisterContext(int context, int handle, ulong options)
    {
        lock (Gate)
        {
            if (!_initialized ||
                !Contexts.TryGetValue(context, out var contextState) ||
                !ServiceHandles.TryGetValue(handle, out var handleState) ||
                handleState.Aborted)
            {
                return false;
            }

            contextState.IsRegistered = true;
            contextState.RegistrationOptions = options;
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool TryCreateObject(out ulong handle)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                handle = 0;
                return false;
            }

            handle = CreateObjectLocked(new UdsObjectNode());
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool DestroyObject(ulong handle)
    {
        lock (Gate)
        {
            return _initialized && Objects.Remove(handle);
        }
    }

    internal static bool TryCreateArray(out ulong handle)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                handle = 0;
                return false;
            }

            handle = CreateArrayLocked(new UdsArrayNode());
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool DestroyArray(ulong handle)
    {
        lock (Gate)
        {
            return _initialized && Arrays.Remove(handle);
        }
    }

    internal static bool SetObjectValue(ulong objectHandle, string key, UdsValue value)
    {
        lock (Gate)
        {
            if (!_initialized || !Objects.TryGetValue(objectHandle, out var node))
            {
                return false;
            }

            node.Values[key] = value;
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool AddArrayValue(ulong arrayHandle, UdsValue value)
    {
        lock (Gate)
        {
            if (!_initialized || !Arrays.TryGetValue(arrayHandle, out var node))
            {
                return false;
            }

            node.Values.Add(value);
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool TrySetObjectNode(
        ulong objectHandle,
        string key,
        ulong childHandle,
        bool childIsArray,
        bool createHandle,
        out ulong resolvedHandle)
    {
        lock (Gate)
        {
            resolvedHandle = 0;
            if (!_initialized || !Objects.TryGetValue(objectHandle, out var parent))
            {
                return false;
            }

            UdsNode child;
            if (childHandle != 0)
            {
                if (childIsArray)
                {
                    if (!Arrays.TryGetValue(childHandle, out var array))
                    {
                        return false;
                    }

                    child = array;
                }
                else
                {
                    if (!Objects.TryGetValue(childHandle, out var obj))
                    {
                        return false;
                    }

                    child = obj;
                }

                resolvedHandle = childHandle;
            }
            else if (childIsArray)
            {
                var array = new UdsArrayNode();
                child = array;
                if (createHandle)
                {
                    resolvedHandle = CreateArrayLocked(array);
                }
            }
            else
            {
                var obj = new UdsObjectNode();
                child = obj;
                if (createHandle)
                {
                    resolvedHandle = CreateObjectLocked(obj);
                }
            }

            parent.Values[key] = new UdsValue(
                childIsArray ? UdsValueKind.Array : UdsValueKind.Object,
                child);
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool TryAddArrayNode(
        ulong arrayHandle,
        ulong childHandle,
        bool childIsArray,
        bool createHandle,
        out ulong resolvedHandle)
    {
        lock (Gate)
        {
            resolvedHandle = 0;
            if (!_initialized || !Arrays.TryGetValue(arrayHandle, out var parent))
            {
                return false;
            }

            UdsNode child;
            if (childHandle != 0)
            {
                if (childIsArray)
                {
                    if (!Arrays.TryGetValue(childHandle, out var array))
                    {
                        return false;
                    }

                    child = array;
                }
                else
                {
                    if (!Objects.TryGetValue(childHandle, out var obj))
                    {
                        return false;
                    }

                    child = obj;
                }

                resolvedHandle = childHandle;
            }
            else if (childIsArray)
            {
                var array = new UdsArrayNode();
                child = array;
                if (createHandle)
                {
                    resolvedHandle = CreateArrayLocked(array);
                }
            }
            else
            {
                var obj = new UdsObjectNode();
                child = obj;
                if (createHandle)
                {
                    resolvedHandle = CreateObjectLocked(obj);
                }
            }

            parent.Values.Add(new UdsValue(
                childIsArray ? UdsValueKind.Array : UdsValueKind.Object,
                child));
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool TryCreateEvent(
        string name,
        ulong propertyHandle,
        bool createPropertyHandle,
        out ulong eventHandle,
        out ulong resolvedPropertyHandle)
    {
        lock (Gate)
        {
            eventHandle = 0;
            resolvedPropertyHandle = 0;
            if (!_initialized)
            {
                return false;
            }

            UdsObjectNode root;
            if (propertyHandle != 0)
            {
                if (!Objects.TryGetValue(propertyHandle, out root!))
                {
                    return false;
                }

                resolvedPropertyHandle = propertyHandle;
            }
            else
            {
                root = new UdsObjectNode();
                if (createPropertyHandle)
                {
                    resolvedPropertyHandle = CreateObjectLocked(root);
                }
            }

            eventHandle = EventHandleBase | ++_nextEvent;
            Events.Add(eventHandle, new UdsEvent(name, root));
            UpdateMaximumInUseLocked();
            return true;
        }
    }

    internal static bool DestroyEvent(ulong eventHandle)
    {
        lock (Gate)
        {
            return _initialized && Events.Remove(eventHandle);
        }
    }

    internal static bool PostEvent(int context, int handle, ulong eventHandle, ulong options)
    {
        lock (Gate)
        {
            if (!_initialized ||
                !Contexts.TryGetValue(context, out var contextState) ||
                !contextState.IsRegistered ||
                !ServiceHandles.TryGetValue(handle, out var serviceHandle) ||
                serviceHandle.Aborted ||
                !Events.TryGetValue(eventHandle, out var udsEvent))
            {
                return false;
            }

            var clone = CloneObject(udsEvent.Root, new Dictionary<UdsNode, UdsNode>(ReferenceEqualityComparer.Instance));
            if (PostedEvents.Count == MaximumPostedEvents)
            {
                PostedEvents.Dequeue();
            }

            PostedEvents.Enqueue(new UdsPostedEvent(udsEvent.Name, clone));
            _ = options;
            return true;
        }
    }

    internal static UdsMemoryStat GetMemoryStat()
    {
        lock (Gate)
        {
            var current = CalculateInUseLocked();
            return new UdsMemoryStat(_poolSize, _maximumInUseSize, current);
        }
    }

    internal static int PostedEventCountForTests
    {
        get
        {
            lock (Gate)
            {
                return PostedEvents.Count;
            }
        }
    }

    internal static UdsPostedEvent? LastPostedEventForTests
    {
        get
        {
            lock (Gate)
            {
                return PostedEvents.Count == 0 ? null : PostedEvents.Last();
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            ResetLocked();
        }
    }

    private static ulong CreateObjectLocked(UdsObjectNode node)
    {
        var handle = ObjectHandleBase | ++_nextObject;
        Objects.Add(handle, node);
        return handle;
    }

    private static ulong CreateArrayLocked(UdsArrayNode node)
    {
        var handle = ArrayHandleBase | ++_nextArray;
        Arrays.Add(handle, node);
        return handle;
    }

    private static int NextPositiveId(ref int value)
    {
        value = value == int.MaxValue ? 1 : value + 1;
        return value;
    }

    private static void UpdateMaximumInUseLocked()
    {
        _maximumInUseSize = Math.Max(_maximumInUseSize, CalculateInUseLocked());
    }

    private static ulong CalculateInUseLocked()
    {
        ulong size = checked((ulong)(Contexts.Count * 24 + ServiceHandles.Count * 24 + Events.Count * 32));
        var visited = new HashSet<UdsNode>(ReferenceEqualityComparer.Instance);
        foreach (var node in Objects.Values)
        {
            size = checked(size + MeasureNode(node, visited));
        }

        foreach (var node in Arrays.Values)
        {
            size = checked(size + MeasureNode(node, visited));
        }

        foreach (var udsEvent in Events.Values)
        {
            size = checked(size + (ulong)Encoding.UTF8.GetByteCount(udsEvent.Name) + 1);
            size = checked(size + MeasureNode(udsEvent.Root, visited));
        }

        size = checked(size + (ulong)(Contexts.Values.Count(context => context.IsRegistered) * 16));

        return size;
    }

    private static ulong MeasureNode(UdsNode node, HashSet<UdsNode> visited)
    {
        if (!visited.Add(node))
        {
            return 0;
        }

        if (node is UdsObjectNode obj)
        {
            ulong size = 24;
            foreach (var (key, value) in obj.Values)
            {
                size = checked(size + (ulong)Encoding.UTF8.GetByteCount(key) + 1 + MeasureValue(value, visited));
            }

            return size;
        }

        var array = (UdsArrayNode)node;
        ulong arraySize = 24;
        foreach (var value in array.Values)
        {
            arraySize = checked(arraySize + MeasureValue(value, visited));
        }

        return arraySize;
    }

    private static ulong MeasureValue(UdsValue value, HashSet<UdsNode> visited)
    {
        return value.Kind switch
        {
            UdsValueKind.String => checked((ulong)Encoding.UTF8.GetByteCount((string)value.Value) + 1),
            UdsValueKind.Int32 or UdsValueKind.UInt32 or UdsValueKind.Float32 or UdsValueKind.Bool => 4,
            UdsValueKind.Int64 or UdsValueKind.UInt64 or UdsValueKind.Float64 => 8,
            UdsValueKind.Object or UdsValueKind.Array => MeasureNode((UdsNode)value.Value, visited),
            _ => 0,
        };
    }

    private static UdsObjectNode CloneObject(UdsObjectNode source, Dictionary<UdsNode, UdsNode> clones)
    {
        if (clones.TryGetValue(source, out var existing))
        {
            return (UdsObjectNode)existing;
        }

        var clone = new UdsObjectNode();
        clones.Add(source, clone);
        foreach (var (key, value) in source.Values)
        {
            clone.Values.Add(key, CloneValue(value, clones));
        }

        return clone;
    }

    private static UdsArrayNode CloneArray(UdsArrayNode source, Dictionary<UdsNode, UdsNode> clones)
    {
        if (clones.TryGetValue(source, out var existing))
        {
            return (UdsArrayNode)existing;
        }

        var clone = new UdsArrayNode();
        clones.Add(source, clone);
        foreach (var value in source.Values)
        {
            clone.Values.Add(CloneValue(value, clones));
        }

        return clone;
    }

    private static UdsValue CloneValue(UdsValue value, Dictionary<UdsNode, UdsNode> clones)
    {
        return value.Kind switch
        {
            UdsValueKind.Object => value with { Value = CloneObject((UdsObjectNode)value.Value, clones) },
            UdsValueKind.Array => value with { Value = CloneArray((UdsArrayNode)value.Value, clones) },
            _ => value,
        };
    }

    private static void ResetLocked()
    {
        Contexts.Clear();
        ServiceHandles.Clear();
        Events.Clear();
        Objects.Clear();
        Arrays.Clear();
        PostedEvents.Clear();
        _initialized = false;
        _poolSize = 0;
        _maximumInUseSize = 0;
        _nextContext = 0;
        _nextServiceHandle = 0;
        _nextEvent = 0;
        _nextObject = 0;
        _nextArray = 0;
    }

    private sealed record UdsContext(int UserId, uint ServiceLabel, ulong Options)
    {
        public bool IsRegistered { get; set; }

        public ulong RegistrationOptions { get; set; }
    }

    private sealed class UdsServiceHandle
    {
        public bool Aborted { get; set; }
    }

    private sealed record UdsEvent(string Name, UdsObjectNode Root);
}
