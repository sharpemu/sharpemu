// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

// JsonObjectHeap is shared static state; both Json test classes join one collection so xUnit
// does not run them in parallel against it.
[Collection("JsonObjectHeap")]
public sealed class JsonValueExportsTests
{
    private const ulong ThisAddress = 0x1_0000_0000;
    private const ulong StringAddress = 0x1_0000_1000;
    private const ulong TextAddress = 0x1_0000_2000;

    private readonly FakeCpuMemory _memory = new(0x1_0000_0000, 0x10000);
    private readonly CpuContext _ctx;

    public JsonValueExportsTests()
    {
        JsonObjectHeap.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ValueDefaultConstructor_RegistersNullAndReturnsThis()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;

        JsonValueExports.ValueDefaultConstructor(_ctx);

        Assert.Equal(ThisAddress, _ctx[CpuRegister.Rax]);
        Assert.Equal(JsonValueKind.Null, JsonObjectHeap.Values[ThisAddress].Kind);
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(1UL, true)]
    [InlineData(0xFFFF_FF00UL, false)] // only the low byte is the bool; 0x00 low byte => false
    public void ValueSetBoolean_StoresLowByte(ulong raw, bool expected)
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = raw;

        JsonValueExports.ValueSetBoolean(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.Boolean, state.Kind);
        Assert.Equal(expected, state.Boolean);
        Assert.Equal(ThisAddress, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ValueSetInteger_RoundTripsSignedValue()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-42L);

        JsonValueExports.ValueSetInteger(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.Integer, state.Kind);
        Assert.Equal(-42L, state.Integer);
    }

    [Fact]
    public void ValueSetUnsigned_RoundTripsFullWidth()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = ulong.MaxValue;

        JsonValueExports.ValueSetUnsigned(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.UInteger, state.Kind);
        Assert.Equal(ulong.MaxValue, state.UnsignedInteger);
    }

    [Fact]
    public void ValueSetReal_ReadsDoubleFromXmm0()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(3.14159)), 0);

        JsonValueExports.ValueSetReal(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.Real, state.Kind);
        Assert.Equal(3.14159, state.Real, precision: 10);
    }

    [Fact]
    public void ValueSetCString_ReadsGuestString()
    {
        _memory.WriteCString(TextAddress, "hello json");
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;

        JsonValueExports.ValueSetCString(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.String, state.Kind);
        Assert.Equal("hello json", state.Text);
    }

    [Fact]
    public void ValueSetType_KeepsRawGuestEnumValue()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = 7;

        JsonValueExports.ValueSetType(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.ExplicitType, state.Kind);
        Assert.Equal(7u, state.ExplicitType);
    }

    [Fact]
    public void StringConstructThenValueSetString_CopiesText()
    {
        _memory.WriteCString(TextAddress, "from string object");
        _ctx[CpuRegister.Rdi] = StringAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.StringCStringConstructor(_ctx);

        Assert.Equal("from string object", JsonObjectHeap.Strings[StringAddress]);
        Assert.Equal(StringAddress, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        JsonValueExports.ValueSetString(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.String, state.Kind);
        Assert.Equal("from string object", state.Text);
    }

    [Fact]
    public void ValueSetString_MissingStringShadow_DegradesToEmpty()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = StringAddress; // never constructed

        JsonValueExports.ValueSetString(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.String, state.Kind);
        Assert.Equal(string.Empty, state.Text);
    }

    [Fact]
    public void Destructors_RemoveShadowState()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        JsonValueExports.ValueDefaultConstructor(_ctx);
        _ctx[CpuRegister.Rdi] = StringAddress;
        JsonValueExports.StringDefaultConstructor(_ctx);

        Assert.True(JsonObjectHeap.Values.ContainsKey(ThisAddress));
        Assert.True(JsonObjectHeap.Strings.ContainsKey(StringAddress));

        _ctx[CpuRegister.Rdi] = ThisAddress;
        JsonValueExports.ValueDestructor(_ctx);
        _ctx[CpuRegister.Rdi] = StringAddress;
        JsonValueExports.StringDestructor(_ctx);

        Assert.False(JsonObjectHeap.Values.ContainsKey(ThisAddress));
        Assert.False(JsonObjectHeap.Strings.ContainsKey(StringAddress));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ValueSetCString_FaultingPointer_DegradesToEmptyString()
    {
        _ctx[CpuRegister.Rdi] = ThisAddress;
        _ctx[CpuRegister.Rsi] = 0xDEAD_0000_0000; // outside the mapped region

        JsonValueExports.ValueSetCString(_ctx);

        var state = JsonObjectHeap.Values[ThisAddress];
        Assert.Equal(JsonValueKind.String, state.Kind);
        Assert.Equal(string.Empty, state.Text);
    }
}
