// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Json;

public static class JsonExports
{
    private const int ValueObjectSize = 0x20;
    private const int StringObjectSize = 0x08;
    private const ulong MaximumJsonBufferSize = 16 * 1024 * 1024;
    private const int SceJsonParserErrorInvalidToken = unchecked((int)0x80920101);
    private const int SceJsonParserErrorEmptyBuffer = unchecked((int)0x80920105);

    private sealed record JsonValueState(JsonElement Element);

    private sealed record JsonStringState(
        string Value,
        ulong GuestBufferAddress = 0,
        int GuestBufferCapacity = 0);

    private static readonly ConcurrentDictionary<ulong, JsonValueState> _values = new();
    private static readonly ConcurrentDictionary<ulong, JsonStringState> _strings = new();
    private static readonly JsonElement _nullElement = CreateNullElement();

    [SysAbiExport(
        Nid = "-hJRce8wn1U",
        ExportName = "_ZN3sce4Json12MemAllocatorC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("MemAllocator.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "OcAgPxcq5Vk",
        ExportName = "_ZN3sce4Json12MemAllocatorD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorDestructor(CpuContext ctx)
    {
        TraceJson("MemAllocator.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cK6bYHf-Q5E",
        ExportName = "_ZN3sce4Json11InitializerC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("Initializer.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RujUxbr3haM",
        ExportName = "_ZN3sce4Json11InitializerD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerDestructor(CpuContext ctx)
    {
        TraceJson("Initializer.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "PR5k1penBLM",
        ExportName = "_ZN3sce4Json11Initializer9terminateEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerTerminate(CpuContext ctx)
    {
        TraceJson("Initializer.terminate", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Cxwy7wHq4J0",
        ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_13InitParameterE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerInitialize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        var initParameterAddress = ctx[CpuRegister.Rsi];
        if (thisAddress == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        TraceJson("Initializer.initialize", thisAddress, initParameterAddress);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "+drDFyAS6u4",
        ExportName = "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerSetGlobalNullAccessCallback(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        JsonObjectHeap.GlobalNullAccessCallback = ctx[CpuRegister.Rsi];
        JsonObjectHeap.GlobalNullAccessCallbackContext = ctx[CpuRegister.Rdx];
        TraceJson("Initializer.setGlobalNullAccessCallback", thisAddress, ctx[CpuRegister.Rsi]);
        return SetReturn(ctx, 0);
    }

    // Catalog alias NID for the same callback setter.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "00oCq0RwSAY",
        ExportName = "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerSetGlobalNullAccessCallbackAlt(CpuContext ctx) =>
        InitializerSetGlobalNullAccessCallback(ctx);
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "WSOuge5IsCg",
        ExportName = "_ZN3sce4Json14InitParameter2C1Ev",
        Target = Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitParameter2Constructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The PS5 ABI object occupies 0x28 bytes in the caller's frame. Its
        // setters below replace the allocator and file-buffer fields.
        Span<byte> parameter = stackalloc byte[0x28];
        parameter.Clear();
        if (!ctx.Memory.TryWrite(thisAddress, parameter))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceJson("InitParameter2.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "I2QC8PYhJWY",
        ExportName = "_ZN3sce4Json14InitParameter212setAllocatorEPNS0_12MemAllocatorEPv",
        Target = Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitParameter2SetAllocator(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> fields = stackalloc byte[sizeof(ulong) * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(fields, ctx[CpuRegister.Rsi]);
        BinaryPrimitives.WriteUInt64LittleEndian(fields[sizeof(ulong)..], ctx[CpuRegister.Rdx]);
        if (!ctx.Memory.TryWrite(thisAddress, fields))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Eu95jmqn5Rw",
        ExportName = "_ZN3sce4Json14InitParameter217setFileBufferSizeEm",
        Target = Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitParameter2SetFileBufferSize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0 || !ctx.TryWriteUInt64(thisAddress + 0x10, ctx[CpuRegister.Rsi]))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "IXW-z8pggfg",
        ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_14InitParameter2E",
        Target = Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerInitialize2(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        var initParameterAddress = ctx[CpuRegister.Rsi];
        if (thisAddress == 0 || initParameterAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceJson("Initializer.initialize2", thisAddress, initParameterAddress);
        return SetReturn(ctx, 0);
    }
    public static int ValueConstructor(CpuContext ctx)
    {
        _ = ConstructValue(ctx);
        return JsonValueExports.ValueDefaultConstructor(ctx);
    }

    [SysAbiExport(
        Nid = "-wa17B7TGnw",
        ExportName = "_ZN3sce4Json5ValueC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueBaseConstructor(CpuContext ctx) => ConstructValue(ctx);
    public static int ValueDestructor(CpuContext ctx)
    {
        _ = DestroyValue(ctx);
        return JsonValueExports.ValueDestructor(ctx);
    }

    [SysAbiExport(
        Nid = "0eUrW9JAxM0",
        ExportName = "_ZN3sce4Json5ValueD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueBaseDestructor(CpuContext ctx) => DestroyValue(ctx);

    [SysAbiExport(
        Nid = "S5JxQnoGF3E",
        ExportName = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPKcm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ParserParseBuffer(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = ctx[CpuRegister.Rdx];
        if (valueAddress == 0 || bufferAddress == 0 || bufferSize == 0)
        {
            return SetReturn(ctx, SceJsonParserErrorEmptyBuffer);
        }

        if (bufferSize > MaximumJsonBufferSize || bufferSize > int.MaxValue)
        {
            return SetReturn(ctx, SceJsonParserErrorInvalidToken);
        }

        var buffer = new byte[(int)bufferSize];
        if (!ctx.Memory.TryRead(bufferAddress, buffer))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer);
            var element = document.RootElement.Clone();
            StoreValue(ctx, valueAddress, element);
            TraceJsonText("Parser.parse", valueAddress, Encoding.UTF8.GetString(buffer));
            return SetReturn(ctx, 0);
        }
        catch (JsonException)
        {
            return SetReturn(ctx, SceJsonParserErrorInvalidToken);
        }
    }

    [SysAbiExport(
        Nid = "SHtAad20YYM",
        ExportName = "_ZNK3sce4Json5Value7getTypeEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetType(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var element = GetValue(valueAddress);
        ctx[CpuRegister.Rax] = (ulong)GetValueType(element);
        return 0;
    }

    [SysAbiExport(
        Nid = "RBw+4NukeGQ",
        ExportName = "_ZNK3sce4Json5Value5countEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueCount(CpuContext ctx)
    {
        var element = GetValue(ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Array => (ulong)element.GetArrayLength(),
            System.Text.Json.JsonValueKind.Object => (ulong)element.EnumerateObject().Count(),
            _ => 0,
        };
        return 0;
    }

    [SysAbiExport(
        Nid = "zTwZdI8AZ5Y",
        ExportName = "_ZNK3sce4Json5Value10getBooleanEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetBoolean(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "DIxvoy7Ngvk",
        ExportName = "_ZNK3sce4Json5Value10getIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetInteger(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "sn4HNCtNRzY",
        ExportName = "_ZNK3sce4Json5Value11getUIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetUnsignedInteger(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "3qrge7L-AU4",
        ExportName = "_ZNK3sce4Json5Value7getRealEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetReal(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "HwDt5lD9Bfo",
        ExportName = "_ZNK3sce4Json5ValueixEPKc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueIndexCString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var keyAddress = ctx[CpuRegister.Rsi];
        if (!TryReadUtf8CString(ctx, keyAddress, 4096, out var key) ||
            !TryAllocateGuestObject(ctx, ValueObjectSize, out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var parent = GetValue(valueAddress);
        var child = parent.ValueKind == System.Text.Json.JsonValueKind.Object &&
            parent.TryGetProperty(key, out var property)
            ? property.Clone()
            : _nullElement;
        StoreValue(ctx, childAddress, child);
        ctx[CpuRegister.Rax] = childAddress;
        TraceJsonText("Value.index", valueAddress, key);
        return 0;
    }

    [SysAbiExport(
        Nid = "XlWbvieLj2M",
        ExportName = "_ZNK3sce4Json5ValueixEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueIndexPosition(CpuContext ctx) => ReturnIndexedValue(ctx);

    [SysAbiExport(
        Nid = "0YqYAoO-+Uo",
        ExportName = "_ZNK3sce4Json5Value8getValueEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetPosition(CpuContext ctx) => ReturnIndexedValue(ctx);

    [SysAbiExport(
        Nid = "4zrm6VrgIAw",
        ExportName = "_ZN3sce4Json5ValueaSERKS1_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueAssignment(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        if (destinationAddress != 0)
        {
            StoreValue(ctx, destinationAddress, GetValue(sourceAddress));
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }
    public static int StringConstructor(CpuContext ctx)
    {
        _ = ConstructString(ctx);
        return JsonValueExports.StringDefaultConstructor(ctx);
    }

    [SysAbiExport(
        Nid = "eG9E9M6XvTM",
        ExportName = "_ZN3sce4Json6StringC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringBaseConstructor(CpuContext ctx) => ConstructString(ctx);
    public static int StringDestructor(CpuContext ctx)
    {
        _ = DestroyString(ctx);
        return JsonValueExports.StringDestructor(ctx);
    }

    [SysAbiExport(
        Nid = "Ui7YFnSTCBw",
        ExportName = "_ZN3sce4Json6StringD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringBaseDestructor(CpuContext ctx) => DestroyString(ctx);

    [SysAbiExport(
        Nid = "Ncel8t2Rrpc",
        ExportName = "_ZNK3sce4Json5Value8toStringERNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueToString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var stringAddress = ctx[CpuRegister.Rsi];
        if (stringAddress != 0)
        {
            var element = GetValue(valueAddress);
            var value = element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
            _strings[stringAddress] = new JsonStringState(value);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "L1KAkYWml-M",
        ExportName = "_ZNK3sce4Json6String5c_strEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringCStr(CpuContext ctx)
    {
        var stringAddress = ctx[CpuRegister.Rdi];
        if (!_strings.TryGetValue(stringAddress, out var state))
        {
            state = new JsonStringState(string.Empty);
        }

        var bytes = Encoding.UTF8.GetBytes(state.Value + '\0');
        var guestBufferAddress = state.GuestBufferAddress;
        if (guestBufferAddress == 0 || state.GuestBufferCapacity < bytes.Length)
        {
            if (!TryAllocateGuestObject(ctx, bytes.Length, out guestBufferAddress))
            {
                ctx[CpuRegister.Rax] = 0;
                return 0;
            }
        }

        if (!ctx.Memory.TryWrite(guestBufferAddress, bytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        _strings[stringAddress] = state with
        {
            GuestBufferAddress = guestBufferAddress,
            GuestBufferCapacity = bytes.Length,
        };
        ctx.TryWriteUInt64(stringAddress, guestBufferAddress);
        ctx[CpuRegister.Rax] = guestBufferAddress;
        TraceJsonText("String.c_str", stringAddress, state.Value);
        return 0;
    }

    private static int ConstructValue(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        StoreValue(ctx, thisAddress, _nullElement);
        ctx[CpuRegister.Rax] = thisAddress;
        return 0;
    }

    private static int ReturnIndexedValue(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var position = ctx[CpuRegister.Rsi];
        if (!TryAllocateGuestObject(ctx, ValueObjectSize, out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var parent = GetValue(valueAddress);
        var child = parent.ValueKind == System.Text.Json.JsonValueKind.Array &&
            position < (ulong)parent.GetArrayLength()
            ? parent[(int)position].Clone()
            : _nullElement;
        StoreValue(ctx, childAddress, child);
        ctx[CpuRegister.Rax] = childAddress;
        return 0;
    }

    private static int ReturnValueStorage(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = thisAddress == 0 ? 0 : thisAddress + 0x10;
        return 0;
    }

    private static int DestroyValue(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        _values.TryRemove(thisAddress, out _);
        if (thisAddress != 0)
        {
            Span<byte> empty = stackalloc byte[ValueObjectSize];
            empty.Clear();
            ctx.Memory.TryWrite(thisAddress, empty);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int ConstructString(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        _strings[thisAddress] = new JsonStringState(string.Empty);
        ctx.TryWriteUInt64(thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return 0;
    }

    private static int DestroyString(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        _strings.TryRemove(thisAddress, out _);
        if (thisAddress != 0)
        {
            ctx.TryWriteUInt64(thisAddress, 0);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static JsonElement CreateNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private static JsonElement GetValue(ulong address) =>
        address != 0 && _values.TryGetValue(address, out var state)
            ? state.Element
            : _nullElement;

    private static void StoreValue(CpuContext ctx, ulong address, JsonElement element)
    {
        if (address == 0)
        {
            return;
        }

        var clone = element.Clone();
        _values[address] = new JsonValueState(clone);

        Span<byte> mirror = stackalloc byte[ValueObjectSize];
        mirror.Clear();
        var type = GetValueType(clone);
        BinaryPrimitives.WriteInt32LittleEndian(mirror[0x1C..], type);
        switch (clone.ValueKind)
        {
            case System.Text.Json.JsonValueKind.True:
                mirror[0x10] = 1;
                break;
            case System.Text.Json.JsonValueKind.Number when clone.TryGetInt64(out var integer):
                BinaryPrimitives.WriteInt64LittleEndian(mirror[0x10..], integer);
                break;
            case System.Text.Json.JsonValueKind.Number when clone.TryGetUInt64(out var unsignedInteger):
                BinaryPrimitives.WriteUInt64LittleEndian(mirror[0x10..], unsignedInteger);
                break;
            case System.Text.Json.JsonValueKind.Number:
                BinaryPrimitives.WriteInt64LittleEndian(
                    mirror[0x10..],
                    BitConverter.DoubleToInt64Bits(clone.GetDouble()));
                break;
        }

        ctx.Memory.TryWrite(address, mirror);
    }

    private static int GetValueType(JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => 1,
        System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out _) => 2,
        System.Text.Json.JsonValueKind.Number when element.TryGetUInt64(out _) => 3,
        System.Text.Json.JsonValueKind.Number => 4,
        System.Text.Json.JsonValueKind.String => 5,
        System.Text.Json.JsonValueKind.Array => 6,
        System.Text.Json.JsonValueKind.Object => 7,
        _ => 0,
    };

    private static bool TryAllocateGuestObject(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        return size > 0 &&
            ctx.Memory is IGuestMemoryAllocator allocator &&
            allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address);
    }

    private static bool TryReadUtf8CString(
        CpuContext ctx,
        ulong address,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (address == 0 || maximumLength <= 0)
        {
            return false;
        }

        var bytes = new byte[maximumLength];
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, current))
            {
                return false;
            }

            if (current[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, index);
                return true;
            }

            bytes[index] = current[0];
        }

        return false;
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceJson(string operation, ulong thisAddress, ulong argument)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.{operation} this=0x{thisAddress:X16} arg=0x{argument:X16}");
    }

    private static void TraceJsonText(string operation, ulong thisAddress, string value)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var preview = value.Length <= 128 ? value : value[..128];
        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.{operation} this=0x{thisAddress:X16} value={preview}");
    }
}
