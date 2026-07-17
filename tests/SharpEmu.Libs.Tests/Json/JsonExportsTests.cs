// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

[Collection("JsonObjectHeap")]
public sealed class JsonExportsTests
{
    private const ulong BaseAddress = 0x2_0000_0000;
    private const ulong ValueAddress = BaseAddress + 0x1000;
    private const ulong StringAddress = BaseAddress + 0x2000;
    private const ulong TextAddress = BaseAddress + 0x3000;
    private const ulong BufferAddress = BaseAddress + 0x4000;

    private readonly AllocatingCpuMemory _memory = new(BaseAddress, 0x20000, BaseAddress + 0x10000);
    private readonly CpuContext _ctx;

    public JsonExportsTests()
    {
        JsonObjectHeap.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ReferValueByString_ReturnsParsedBooleanChild()
    {
        var json = Encoding.UTF8.GetBytes("{\"enabled\":true}");
        Assert.True(_memory.TryWrite(BufferAddress, json));
        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = (ulong)json.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(_ctx));

        _memory.WriteCString(TextAddress, "enabled");
        _ctx[CpuRegister.Rdi] = StringAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.StringCStringConstructor(_ctx);

        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, JsonExports.ValueReferValueByString(_ctx));

        var childAddress = _ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, childAddress);
        _ctx[CpuRegister.Rdi] = StringAddress;
        JsonValueExports.StringDestructor(_ctx);

        _memory.WriteCString(TextAddress, "enabled");
        _ctx[CpuRegister.Rdi] = StringAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.StringCStringConstructor(_ctx);
        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        JsonExports.ValueReferValueByString(_ctx);
        Assert.Equal(childAddress, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = childAddress;
        JsonExports.ValueGetType(_ctx);
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
        JsonExports.ValueGetBoolean(_ctx);
        Assert.Equal(childAddress + 0x10, _ctx[CpuRegister.Rax]);
        Span<byte> boolean = stackalloc byte[1];
        Assert.True(_memory.TryRead(childAddress + 0x10, boolean));
        Assert.Equal(1, boolean[0]);
    }

    [Fact]
    public void ReferValueByString_ReturnsNullForMissingMember()
    {
        var json = Encoding.UTF8.GetBytes("{}");
        Assert.True(_memory.TryWrite(BufferAddress, json));
        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = (ulong)json.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(_ctx));

        _memory.WriteCString(TextAddress, "missing");
        _ctx[CpuRegister.Rdi] = StringAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.StringCStringConstructor(_ctx);

        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, JsonExports.ValueReferValueByString(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly FakeCpuMemory _memory;
        private readonly ulong _endAddress;
        private ulong _nextAddress;

        public AllocatingCpuMemory(ulong baseAddress, int size, ulong allocationStart)
        {
            _memory = new FakeCpuMemory(baseAddress, size);
            _nextAddress = allocationStart;
            _endAddress = baseAddress + (ulong)size;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            _memory.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            _memory.TryWrite(virtualAddress, source);

        public ulong WriteCString(ulong virtualAddress, string text) =>
            _memory.WriteCString(virtualAddress, text);

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            var aligned = (_nextAddress + alignment - 1) & ~(alignment - 1);
            if (aligned > _endAddress || size > _endAddress - aligned)
            {
                return false;
            }

            address = aligned;
            _nextAddress = aligned + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) => false;
    }
}
