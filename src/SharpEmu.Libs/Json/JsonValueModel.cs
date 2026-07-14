// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Json;

// sce::Json::Value is an opaque variant type (null / bool / signed / unsigned / real / string /
// array / object). Games build one, populate it through set()/ctors and later serialize it. We
// model the payload host-side keyed by the guest `this` pointer instead of writing into the guest
// object: the object is often stack-allocated and its real byte layout is unknown, so writing a
// guessed layout risks smashing an adjacent stack canary (the same failure the AudioOut2 context
// param sizing note in this project already ran into). The guest reaches the payload only through
// libSceJson methods, so shadowing by address is enough for the build path.
internal enum JsonValueKind : byte
{
    Null = 0,
    Boolean = 1,
    Integer = 2,
    UInteger = 3,
    Real = 4,
    String = 5,

    // set(ValueType) / Value(ValueType): the guest chose the type itself. We keep its raw enum
    // value verbatim rather than mapping it, because the canonical ValueType constants are not
    // known from clean-room evidence and round-tripping the guest's own value is what matters.
    ExplicitType = 6,
}

internal readonly struct JsonValueState
{
    private JsonValueState(
        JsonValueKind kind,
        bool boolean = false,
        long integer = 0,
        ulong unsignedInteger = 0,
        double real = 0,
        string? text = null,
        uint explicitType = 0)
    {
        Kind = kind;
        Boolean = boolean;
        Integer = integer;
        UnsignedInteger = unsignedInteger;
        Real = real;
        Text = text;
        ExplicitType = explicitType;
    }

    public JsonValueKind Kind { get; }
    public bool Boolean { get; }
    public long Integer { get; }
    public ulong UnsignedInteger { get; }
    public double Real { get; }
    public string? Text { get; }
    public uint ExplicitType { get; }

    public static JsonValueState Null { get; } = new(JsonValueKind.Null);

    public static JsonValueState FromBoolean(bool value) => new(JsonValueKind.Boolean, boolean: value);

    public static JsonValueState FromInteger(long value) => new(JsonValueKind.Integer, integer: value);

    public static JsonValueState FromUnsignedInteger(ulong value) =>
        new(JsonValueKind.UInteger, unsignedInteger: value);

    public static JsonValueState FromReal(double value) => new(JsonValueKind.Real, real: value);

    public static JsonValueState FromString(string value) => new(JsonValueKind.String, text: value);

    public static JsonValueState FromExplicitType(uint value) =>
        new(JsonValueKind.ExplicitType, explicitType: value);
}

// Shared host-side heap for the libSceJson object shadows. Keyed by the guest object address;
// constructors overwrite and destructors remove, so guest stack-address reuse stays correct.
internal static class JsonObjectHeap
{
    public static ConcurrentDictionary<ulong, JsonValueState> Values { get; } = new();

    public static ConcurrentDictionary<ulong, string> Strings { get; } = new();

    // Guest function the library should call when a Value is read as the wrong type. This HLE
    // never dereferences missing members (shadows degrade to defaults), so the hook is stored for
    // fidelity but not invoked.
    public static ulong GlobalNullAccessCallback;

    public static ulong GlobalNullAccessCallbackContext;

    public static void SetValue(ulong address, JsonValueState state) => Values[address] = state;

    public static void RemoveValue(ulong address) => Values.TryRemove(address, out _);

    public static void SetString(ulong address, string text) => Strings[address] = text;

    public static void RemoveString(ulong address) => Strings.TryRemove(address, out _);

    // A missing shadow (temporary the compiler built without an out-of-line ctor, or a copy we did
    // not track) degrades to the empty string rather than faulting.
    public static string GetStringOrEmpty(ulong address) =>
        Strings.TryGetValue(address, out var text) ? text : string.Empty;

    internal static void ResetForTests()
    {
        Values.Clear();
        Strings.Clear();
        GlobalNullAccessCallback = 0;
        GlobalNullAccessCallbackContext = 0;
    }
}
