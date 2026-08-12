// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[CollectionDefinition("NpUniversalDataSystem", DisableParallelization = true)]
public sealed class NpUniversalDataSystemCollection;

[Collection("NpUniversalDataSystem")]
public sealed class NpUniversalDataSystemExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int ErrorInvalidArgument = unchecked((int)0x80553102);
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x2_0000);
    private readonly CpuContext _context;

    public NpUniversalDataSystemExportsTests()
    {
        NpUniversalDataSystemState.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => NpUniversalDataSystemState.ResetForTests();

    [Fact]
    public void FullLifecycle_PreservesAttachedChildAfterItsHandleIsDestroyed()
    {
        Initialize(poolSize: 0x20_0000);
        var context = CreateContext(userId: 7, serviceLabel: 3, options: 0x11);
        var serviceHandle = CreateServiceHandle();
        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, 0x22);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemRegisterContext(_context));

        var eventName = _memory.WriteCString(MemoryBase + 0x1000, "level_complete");
        var eventOutput = MemoryBase + 0x1100;
        var rootOutput = MemoryBase + 0x1110;
        SetRegisters(eventName, 0, eventOutput, rootOutput);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateEvent(_context));
        var eventHandle = ReadUInt64(eventOutput);
        var rootHandle = ReadUInt64(rootOutput);

        var childHandle = CreateObject(MemoryBase + 0x1120);
        SetObjectString(childHandle, "name", "tower");
        SetObjectInt32(childHandle, "score", 321);
        var arrayHandle = CreateArray(MemoryBase + 0x1130);
        AddArrayString(arrayHandle, "first");
        AddArrayBool(arrayHandle, true);
        AddArrayFloat32(arrayHandle, 1.25f);
        SetObjectNode(childHandle, "values", arrayHandle, childIsArray: true);

        SetRegisters(arrayHandle);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyArray(_context));
        SetObjectNode(rootHandle, "payload", childHandle, childIsArray: false);
        SetRegisters(childHandle);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyObject(_context));

        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, eventHandle, 0x44);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemPostEvent(_context));
        var posted = Assert.IsType<UdsPostedEvent>(NpUniversalDataSystemState.LastPostedEventForTests);
        Assert.Equal("level_complete", posted.Name);
        var payload = Assert.IsType<UdsObjectNode>(posted.Root.Values["payload"].Value);
        Assert.Equal("tower", payload.Values["name"].Value);
        Assert.Equal(321, payload.Values["score"].Value);
        var values = Assert.IsType<UdsArrayNode>(payload.Values["values"].Value);
        Assert.Equal("first", values.Values[0].Value);
        Assert.Equal(true, values.Values[1].Value);
        Assert.Equal(1.25f, values.Values[2].Value);

        SetRegisters(eventHandle);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEvent(_context));
        SetRegisters((ulong)(uint)serviceHandle);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemDestroyHandle(_context));
        SetRegisters((ulong)(uint)context);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemDestroyContext(_context));
    }

    [Fact]
    public void NodeSetter_WithNullValue_CreatesAttachedBuilderHandle()
    {
        Initialize();
        var parent = CreateObject(MemoryBase + 0x1200);
        var key = _memory.WriteCString(MemoryBase + 0x1210, "child");
        var childOutput = MemoryBase + 0x1220;
        SetRegisters(parent, key, 0, childOutput);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetObject(_context));
        var child = ReadUInt64(childOutput);
        SetObjectString(child, "status", "ready");

        var eventName = _memory.WriteCString(MemoryBase + 0x1230, "builder");
        var eventOutput = MemoryBase + 0x1240;
        SetRegisters(eventName, parent, eventOutput, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateEvent(_context));
        var context = CreateContext();
        var serviceHandle = CreateServiceHandle();
        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemRegisterContext(_context));
        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, ReadUInt64(eventOutput), 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemPostEvent(_context));

        var posted = Assert.IsType<UdsPostedEvent>(NpUniversalDataSystemState.LastPostedEventForTests);
        var childNode = Assert.IsType<UdsObjectNode>(posted.Root.Values["child"].Value);
        Assert.Equal("ready", childNode.Values["status"].Value);
    }

    [Fact]
    public void TypedSetters_ReadIntegerAndFloatingPointAbiRegisters()
    {
        Initialize();
        var root = CreateObject(MemoryBase + 0x1300);
        SetObjectUInt32(root, "u32", 0xF123_4567);
        SetObjectInt64(root, "i64", -0x1234_5678_7654_321);
        SetObjectUInt64(root, "u64", 0xFEDC_BA98_7654_3210);
        SetObjectFloat64(root, "f64", 3.5);
        SetObjectBool(root, "bool", true);

        var name = _memory.WriteCString(MemoryBase + 0x1310, "typed");
        var eventOutput = MemoryBase + 0x1320;
        SetRegisters(name, root, eventOutput, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateEvent(_context));
        var context = CreateContext();
        var serviceHandle = CreateServiceHandle();
        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemRegisterContext(_context));
        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, ReadUInt64(eventOutput), 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemPostEvent(_context));

        var snapshot = Assert.IsType<UdsPostedEvent>(NpUniversalDataSystemState.LastPostedEventForTests).Root;
        Assert.Equal(0xF123_4567U, snapshot.Values["u32"].Value);
        Assert.Equal(-0x1234_5678_7654_321L, snapshot.Values["i64"].Value);
        Assert.Equal(0xFEDC_BA98_7654_3210UL, snapshot.Values["u64"].Value);
        Assert.Equal(3.5, snapshot.Values["f64"].Value);
        Assert.Equal(true, snapshot.Values["bool"].Value);
    }

    [Fact]
    public void GetMemoryStat_ReportsPoolCurrentAndHighWaterValues()
    {
        const ulong poolSize = 0x40_0000;
        Initialize(poolSize);
        var root = CreateObject(MemoryBase + 0x1400);
        SetObjectString(root, "message", "memory-stat-payload");
        var statOutput = MemoryBase + 0x1500;
        SetRegisters(statOutput);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemGetMemoryStat(_context));
        var first = ReadMemoryStat(statOutput);
        Assert.Equal(poolSize, first.PoolSize);
        Assert.True(first.CurrentInUseSize > 0);
        Assert.True(first.MaximumInUseSize >= first.CurrentInUseSize);

        SetRegisters(root);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyObject(_context));
        SetRegisters(statOutput);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemGetMemoryStat(_context));
        var second = ReadMemoryStat(statOutput);
        Assert.True(second.CurrentInUseSize < first.CurrentInUseSize);
        Assert.Equal(first.MaximumInUseSize, second.MaximumInUseSize);
    }

    [Fact]
    public void PostEvent_RejectsUnregisteredAndAbortedHandles()
    {
        Initialize();
        var context = CreateContext();
        var serviceHandle = CreateServiceHandle();
        var eventName = _memory.WriteCString(MemoryBase + 0x1600, "invalid-lifecycle");
        var eventOutput = MemoryBase + 0x1610;
        SetRegisters(eventName, 0, eventOutput, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateEvent(_context));
        var eventHandle = ReadUInt64(eventOutput);

        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, eventHandle, 0);
        Assert.Equal(ErrorInvalidArgument, NpUniversalDataSystemExports.NpUniversalDataSystemPostEvent(_context));
        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemRegisterContext(_context));
        SetRegisters((ulong)(uint)serviceHandle);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemAbortHandle(_context));
        SetRegisters((ulong)(uint)context, (ulong)(uint)serviceHandle, eventHandle, 0);
        Assert.Equal(ErrorInvalidArgument, NpUniversalDataSystemExports.NpUniversalDataSystemPostEvent(_context));
        Assert.Equal(0, NpUniversalDataSystemState.PostedEventCountForTests);
    }

    [Fact]
    public void PostEvent_AcceptsNewRequestHandleAfterRegistrationHandleIsDestroyed()
    {
        Initialize();
        var context = CreateContext();
        var registrationHandle = CreateServiceHandle();
        SetRegisters((ulong)(uint)context, (ulong)(uint)registrationHandle, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemRegisterContext(_context));
        SetRegisters((ulong)(uint)registrationHandle);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemDestroyHandle(_context));

        var postHandle = CreateServiceHandle();
        var eventName = _memory.WriteCString(MemoryBase + 0x1680, "transient-request-handle");
        var eventOutput = MemoryBase + 0x16A0;
        SetRegisters(eventName, 0, eventOutput, 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateEvent(_context));

        SetRegisters(
            (ulong)(uint)context,
            (ulong)(uint)postHandle,
            ReadUInt64(eventOutput),
            0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemPostEvent(_context));
        Assert.Equal(1, NpUniversalDataSystemState.PostedEventCountForTests);
    }

    [Fact]
    public void InvalidPointersAndUninitializedCalls_ReturnErrors()
    {
        SetRegisters(MemoryBase + 0x1700);
        Assert.Equal(ErrorInvalidArgument, NpUniversalDataSystemExports.NpUniversalDataSystemCreateHandle(_context));
        SetRegisters(0);
        Assert.Equal(ErrorInvalidArgument, NpUniversalDataSystemExports.NpUniversalDataSystemInitialize(_context));
        SetRegisters(0xDEAD_BEEF);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpUniversalDataSystemExports.NpUniversalDataSystemInitialize(_context));
    }

    [Fact]
    public void ConcurrentPropertyWrites_AreRetained()
    {
        Assert.True(NpUniversalDataSystemState.Initialize(0x10_0000));
        Assert.True(NpUniversalDataSystemState.TryCreateObject(out var root));
        Parallel.For(0, 256, index => Assert.True(NpUniversalDataSystemState.SetObjectValue(
            root, $"key-{index}", new UdsValue(UdsValueKind.Int32, index))));
        Assert.True(NpUniversalDataSystemState.TryCreateContext(1, 0, 0, out var context));
        Assert.True(NpUniversalDataSystemState.TryCreateServiceHandle(out var serviceHandle));
        Assert.True(NpUniversalDataSystemState.RegisterContext(context, serviceHandle, 0));
        Assert.True(NpUniversalDataSystemState.TryCreateEvent("concurrent", root, false, out var udsEvent, out _));
        Assert.True(NpUniversalDataSystemState.PostEvent(context, serviceHandle, udsEvent, 0));
        Assert.Equal(256, NpUniversalDataSystemState.LastPostedEventForTests!.Root.Values.Count);
    }

    private void Initialize(ulong poolSize = 0x10_0000)
    {
        var address = MemoryBase + 0x100;
        Span<byte> parameters = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(parameters, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(parameters[8..], poolSize);
        Assert.True(_memory.TryWrite(address, parameters));
        SetRegisters(address);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemInitialize(_context));
    }

    private int CreateContext(int userId = 1, uint serviceLabel = 0, ulong options = 0)
    {
        var address = MemoryBase + 0x200;
        SetRegisters(address, unchecked((ulong)(uint)userId), serviceLabel, options);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateContext(_context));
        Assert.True(_context.TryReadInt32(address, out var context));
        return context;
    }

    private int CreateServiceHandle()
    {
        var address = MemoryBase + 0x210;
        SetRegisters(address);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateHandle(_context));
        Assert.True(_context.TryReadInt32(address, out var handle));
        return handle;
    }

    private ulong CreateObject(ulong address)
    {
        SetRegisters(address);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyObject(_context));
        return ReadUInt64(address);
    }

    private ulong CreateArray(ulong address)
    {
        SetRegisters(address);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyArray(_context));
        return ReadUInt64(address);
    }

    private void SetObjectString(ulong handle, string keyText, string valueText)
    {
        SetRegisters(handle, WriteString(0x2000, keyText), WriteString(0x3000, valueText));
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetString(_context));
    }

    private void SetObjectInt32(ulong handle, string keyText, int value)
    {
        SetRegisters(handle, WriteString(0x2000, keyText), unchecked((ulong)(uint)value));
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetInt32(_context));
    }

    private void SetObjectUInt32(ulong handle, string keyText, uint value)
    {
        SetRegisters(handle, WriteString(0x2000, keyText), value);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetUInt32(_context));
    }

    private void SetObjectInt64(ulong handle, string keyText, long value)
    {
        SetRegisters(handle, WriteString(0x2000, keyText), unchecked((ulong)value));
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetInt64(_context));
    }

    private void SetObjectUInt64(ulong handle, string keyText, ulong value)
    {
        SetRegisters(handle, WriteString(0x2000, keyText), value);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetUInt64(_context));
    }

    private void SetObjectFloat64(ulong handle, string keyText, double value)
    {
        SetRegisters(handle, WriteString(0x2000, keyText));
        _context.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetFloat64(_context));
    }

    private void SetObjectBool(ulong handle, string keyText, bool value)
    {
        SetRegisters(handle, WriteString(0x2000, keyText), value ? 1UL : 0UL);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetBool(_context));
    }

    private void SetObjectNode(ulong parent, string keyText, ulong child, bool childIsArray)
    {
        SetRegisters(parent, WriteString(0x2000, keyText), child, 0);
        AssertSuccess(childIsArray
            ? NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetArray(_context)
            : NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetObject(_context));
    }

    private void AddArrayString(ulong handle, string valueText)
    {
        SetRegisters(handle, WriteString(0x3000, valueText));
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_context));
    }

    private void AddArrayBool(ulong handle, bool value)
    {
        SetRegisters(handle, value ? 1UL : 0UL);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetBool(_context));
    }

    private void AddArrayFloat32(ulong handle, float value)
    {
        SetRegisters(handle);
        _context.SetXmmRegister(0, unchecked((uint)BitConverter.SingleToInt32Bits(value)), 0);
        AssertSuccess(NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetFloat32(_context));
    }

    private ulong WriteString(ulong offset, string value) => _memory.WriteCString(MemoryBase + offset, value);

    private void SetRegisters(ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0)
    {
        _context[CpuRegister.Rdi] = rdi;
        _context[CpuRegister.Rsi] = rsi;
        _context[CpuRegister.Rdx] = rdx;
        _context[CpuRegister.Rcx] = rcx;
    }

    private ulong ReadUInt64(ulong address)
    {
        Assert.True(_context.TryReadUInt64(address, out var value));
        return value;
    }

    private (ulong PoolSize, ulong MaximumInUseSize, ulong CurrentInUseSize) ReadMemoryStat(ulong address)
    {
        Span<byte> bytes = stackalloc byte[24];
        Assert.True(_memory.TryRead(address, bytes));
        return (BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
    }

    private static void AssertSuccess(int result) => Assert.Equal(0, result);
}
