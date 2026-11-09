// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using TestProgram = SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class KeptStageInputsTests : IDisposable
{
    private const ulong VertexAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong VertexHeader = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong UserDataBlock = PipelineTestGuest.MemoryBase + 0x9000;
    private const ulong DirectOffsets = PipelineTestGuest.MemoryBase + 0x9100;
    private const ulong Semantics = PipelineTestGuest.MemoryBase + 0x9200;
    private const ulong AttributeTable = PipelineTestGuest.MemoryBase + 0xA000;
    private const ulong BufferTable = PipelineTestGuest.MemoryBase + 0xA100;
    private const ulong MovedAttributeTable = PipelineTestGuest.MemoryBase + 0xB000;
    private const ulong MovedBufferTable = PipelineTestGuest.MemoryBase + 0xB100;
    private const ulong StreamBase = PipelineTestGuest.MemoryBase + 0x20000;
    private const ulong PixelAddress = PipelineTestGuest.MemoryBase + 0x2000;
    private const ulong PixelHeader = PipelineTestGuest.MemoryBase + 0x8100;
    private const ulong PixelUserDataBlock = PipelineTestGuest.MemoryBase + 0xC000;
    private const uint DirectResourceCount = 11;
    private const uint VertexBufferTableType = 8;
    private const uint VertexAttributeTableType = 10;
    private const uint AttributeRegister = 0;
    private const uint BufferRegister = 2;
    private const uint Format32x4Float = 77;
    private const ulong ShaderSizeOffset = 0x44;

    private readonly FatalScope _fatal = new();
    private readonly PipelineTestGuest _guest = new();
    private readonly ShaderPipelineCache _cache;
    private readonly RegisterBanks _banks = new(static message => new InvalidOperationException(message));

    public KeptStageInputsTests()
    {
        _cache = new ShaderPipelineCache(_guest.Context, _guest.Host, _guest.Compiler, _guest.Registry);
        WriteQword(UserDataBlock, DirectOffsets);
        WriteHalf(UserDataBlock + 0x2C, (ushort)DirectResourceCount);
        for (uint type = 0; type < DirectResourceCount; type++)
        {
            WriteHalf(DirectOffsets + type * 2, 0xFFFF);
        }

        WriteHalf(DirectOffsets + VertexBufferTableType * 2, (ushort)BufferRegister);
        WriteHalf(DirectOffsets + VertexAttributeTableType * 2, (ushort)AttributeRegister);
        _guest.WriteWords(Semantics, 0 | (4u << 16), 1 | (4u << 8) | (2u << 16));
        _guest.RegisterProgram(VertexAddress, VertexHeader, PipelineTestGuest.EndProgram, UserDataBlock, Semantics, 2);
        WriteTables(AttributeTable, BufferTable, stride: 24);
        _guest.RegisterProgram(PixelAddress, PixelHeader, PipelineTestGuest.EndProgram, PixelUserDataBlock);
        _banks.Shader.Vertex.ExportAddress = VertexAddress;
        _banks.Shader.Pixel.Address = PixelAddress;
        PointAtTables(AttributeTable, BufferTable);
    }

    public void Dispose() => _fatal.Dispose();

    private void WriteQword(ulong address, ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        _guest.Write(address, bytes);
    }

    private void WriteHalf(ulong address, ushort value)
    {
        var bytes = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        _guest.Write(address, bytes);
    }

    private void WriteTables(ulong attributeTable, ulong bufferTable, uint stride)
    {
        _guest.WriteWords(attributeTable, 0, 16u << 14);
        _guest.WriteWords(bufferTable, PipelineTestGuest.BufferDescriptor(StreamBase, stride, 3, Format32x4Float));
    }

    private void PointAtTables(ulong attributeTable, ulong bufferTable)
    {
        var registers = _banks.Shader.Vertex.GeometryUserScalars;
        registers.Set(AttributeRegister, (uint)attributeTable, UserScalarKind.Unknown);
        registers.Set(AttributeRegister + 1, (uint)(attributeTable >> 32), UserScalarKind.Unknown);
        registers.Set(BufferRegister, (uint)bufferTable, UserScalarKind.Unknown);
        registers.Set(BufferRegister + 1, (uint)(bufferTable >> 32), UserScalarKind.Unknown);
        _banks.MarkWritten(1u << (int)RegisterStamp.VertexUserData);
    }

    private VertexInputInfo Lookup()
    {
        var programs = _cache.GetGraphicsPrograms(_banks, [], pixelActive: false, depthBound: false);
        Assert.True(programs.Available);
        return programs.VertexInput;
    }

    private PixelInputInfo PixelLookup()
    {
        var programs = _cache.GetGraphicsPrograms(_banks, [], pixelActive: true, depthBound: false);
        Assert.True(programs.Available);
        return programs.PixelInput;
    }

    private void SetKillEnable(bool enabled)
    {
        var shaderInterface = _banks.Context.ShaderInterface;
        var control = shaderInterface.DepthShaderControl;
        control.KillEnable = enabled;
        shaderInterface.DepthShaderControl = control;
        _banks.MarkWritten(1u << (int)RegisterStamp.ShaderInterface);
    }

    private sealed class HeaderlessFrontend : IShaderFrontend
    {
        public ulong CacheDomain => 0x484541444552;
        public uint VertexUserDataBase => 0;
        public bool HasVertexTables => false;
        public uint ScratchDwords;
        public uint SemanticsCount;
        public int Reads;

        public RegisteredShader ReadShader(CpuContext context, ulong codeAddress, string label, ReadOnlySpan<uint> userData = default)
        {
            Reads++;
            return new RegisteredShader(codeAddress, 0, 4, ScratchDwords, 0, Semantics, SemanticsCount, 0, 0);
        }

        public bool TryDecode(CpuContext context, RegisteredShader shader, out Gen5ShaderProgram program, out string error)
        {
            program = TestProgram.Program(TestProgram.EndProgram(0));
            error = string.Empty;
            return true;
        }
    }

    [Fact]
    public void HeaderlessPixelInputsReuseStaticArraysWhileEachDrawStillValidatesItsShader()
    {
        var frontend = new HeaderlessFrontend();
        var registry = new ShaderHeaderRegistry(_guest.Context, _ => 0, _ => null, frontend);
        var cache = new ShaderPipelineCache(_guest.Context, _guest.Host, _guest.Compiler, registry);
        PixelInputInfo Read() => cache.GetGraphicsPrograms(_banks, [], pixelActive: true, depthBound: false).PixelInput;
        var first = Read();
        var second = Read();
        Assert.Equal(4, frontend.Reads);
        Assert.Same(first.InterpolatorSettings, second.InterpolatorSettings);
        Assert.Same(first.TargetOutputModes, second.TargetOutputModes);

        frontend.ScratchDwords = 8;
        var changedScratch = Read();
        Assert.Equal(8u, changedScratch.ScratchDwords);
        Assert.NotSame(second.InterpolatorSettings, changedScratch.InterpolatorSettings);
        _guest.WriteWords(PixelAddress, 0xBF800000);
        var changedCode = Read();
        Assert.NotSame(changedScratch.InterpolatorSettings, changedCode.InterpolatorSettings);

        var otherRegistry = new ShaderHeaderRegistry(_guest.Context, _ => 0, _ => null, frontend);
        cache = new ShaderPipelineCache(_guest.Context, _guest.Host, _guest.Compiler, otherRegistry);
        var otherSession = Read();
        Assert.NotSame(changedCode.InterpolatorSettings, otherSession.InterpolatorSettings);

        frontend.SemanticsCount = 1;
        var withSemantics = Read();
        Assert.NotSame(withSemantics.InterpolatorSettings, Read().InterpolatorSettings);
    }

    [Fact]
    public void PixelInput_IsKeptUntilAContextRegisterIsWritten()
    {
        var first = PixelLookup();
        _banks.MarkWritten(1u << (int)RegisterStamp.PixelUserData);
        var second = PixelLookup();
        SetKillEnable(true);
        var changed = PixelLookup();

        Assert.NotSame(first, second);
        Assert.Same(first.InterpolatorSettings, second.InterpolatorSettings);
        Assert.True(second.Stage.IsValid);
        Assert.False(second.KillEnable);
        Assert.NotSame(first.InterpolatorSettings, changed.InterpolatorSettings);
        Assert.True(changed.KillEnable);
    }

    [Fact]
    public void VertexInput_IsKeptWhileItsTablesHoldTheSameWords()
    {
        var first = Lookup();
        _banks.MarkWritten(1u << (int)RegisterStamp.VertexUserData);
        var second = Lookup();

        Assert.Equal(2, first.Attributes.Length);
        Assert.Equal(24u, Assert.Single(first.Buffers).Stride);
        Assert.NotSame(first, second);
        Assert.Same(first.Attributes, second.Attributes);
        Assert.Same(first.Buffers, second.Buffers);
        Assert.True(second.Stage.IsValid);
    }

    [Fact]
    public void VertexInput_IsResolvedAgainWhenATableWordChanges()
    {
        var first = Lookup();
        WriteTables(AttributeTable, BufferTable, stride: 32);
        var changedBuffer = Lookup();
        _guest.WriteWords(AttributeTable + 4, 8u << 14);
        var changedAttribute = Lookup();

        Assert.NotSame(first.Buffers, changedBuffer.Buffers);
        Assert.Equal(32u, Assert.Single(changedBuffer.Buffers).Stride);
        Assert.NotSame(changedBuffer.Attributes, changedAttribute.Attributes);
        Assert.Equal(8u, changedAttribute.Attributes[1].OffsetBytes);
    }

    [Fact]
    public void VertexInput_IsKeptWhenTheSameTablesMove()
    {
        var first = Lookup();
        WriteTables(MovedAttributeTable, MovedBufferTable, stride: 24);
        PointAtTables(MovedAttributeTable, MovedBufferTable);
        var moved = Lookup();

        Assert.Same(first.Attributes, moved.Attributes);
        Assert.Same(first.Buffers, moved.Buffers);
    }

    [Fact]
    public void VertexInput_IsResolvedAgainWhenTheClipTransformChanges()
    {
        var first = Lookup();
        _banks.Context.Clip.ClipDisable = true;
        _banks.Context.ScreenViewport.Primary = new ViewportRegisters { XScale = 32, XOffset = 32, YScale = 32, YOffset = 32 };
        _banks.MarkWritten(1u << (int)RegisterStamp.ShaderInterface);
        var clipped = Lookup();

        Assert.NotSame(first.Attributes, clipped.Attributes);
        Assert.True(clipped.ClipSpace.Enabled);
        Assert.Equal(32f, clipped.ClipSpace.ScaleX);
    }

    [Fact]
    public void UserData_IsKeptUntilARegisterHoldsAnotherWord()
    {
        Lookup();
        var kept = (KeptUserData)typeof(ShaderPipelineCache).GetField("_vertexUserData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var first = kept.Words;
        Lookup();
        Assert.Same(first, kept.Words);

        _banks.MarkWritten(1u << (int)RegisterStamp.VertexUserData);
        Lookup();
        Assert.Same(first, kept.Words);

        _banks.Shader.Vertex.GeometryUserScalars.Set(4, 0x1234, UserScalarKind.Unknown);
        _banks.MarkWritten(1u << (int)RegisterStamp.VertexUserData);
        Lookup();
        Assert.NotSame(first, kept.Words);
        Assert.Equal(5, kept.Words!.Length);
        Assert.Equal(0x1234u, kept.Words[4]);
    }

    [Fact]
    public void Header_IsReadAgainOnlyAfterARegistration()
    {
        var first = _guest.Registry.Require(VertexAddress, "vertex");
        var size = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(size, 8);
        _guest.Write(VertexHeader + ShaderSizeOffset, size);

        Assert.Same(first, _guest.Registry.Require(VertexAddress, "vertex"));
        ShaderHeaderRegistry.NoteRegistration();
        Assert.Equal(8u, _guest.Registry.Require(VertexAddress, "vertex").CodeSizeBytes);
    }

    [Fact]
    public void ShaderIdentity_IsRehashedAfterARegistrationOrEverySixtyFourthUse()
    {
        var entry = _guest.Registry.RequireEntry(VertexAddress, "vertex");
        var first = _cache.IdentityOf(entry, VertexAddress, "vertex");
        _guest.WriteWords(VertexAddress, 0xBF800001);

        for (var use = 1; use < 64; use++)
        {
            Assert.Equal(first, _cache.IdentityOf(entry, VertexAddress, "vertex"));
        }

        var rehashed = _cache.IdentityOf(entry, VertexAddress, "vertex");
        Assert.NotEqual(first, rehashed);
        Assert.Equal(rehashed, _cache.IdentityOf(entry, VertexAddress, "vertex"));

        _guest.WriteWords(VertexAddress, 0xBF800002);
        ShaderHeaderRegistry.NoteRegistration();
        var registered = _guest.Registry.RequireEntry(VertexAddress, "vertex");
        Assert.NotSame(entry, registered);
        Assert.NotEqual(rehashed, _cache.IdentityOf(registered, VertexAddress, "vertex"));
    }

    private GraphicsPrograms Programs(bool pixelActive) =>
        _cache.GetGraphicsPrograms(_banks, [], pixelActive, depthBound: false);

    [Fact]
    public void GraphicsPrograms_AreKeptOnceTheirResourcesAreCachedAndNothingIsWritten()
    {
        _ = Programs(pixelActive: true);
        var cached = Programs(pixelActive: true);
        Assert.Same(cached, Programs(pixelActive: true));
        Assert.Same(cached, Programs(pixelActive: true));
        _banks.MarkWritten(1u << (int)RegisterStamp.PixelUserData);
        var resolved = Programs(pixelActive: true);
        Assert.NotSame(cached, resolved);
        Assert.Same(resolved, Programs(pixelActive: true));
    }

    [Fact]
    public void GraphicsPrograms_ResolveAgainWhenTheTargetsOrTheRegistryChange()
    {
        _ = Programs(pixelActive: false);
        var cached = Programs(pixelActive: false);
        Assert.Same(cached, Programs(pixelActive: false));
        Assert.NotSame(cached, Programs(pixelActive: true));
        _ = Programs(pixelActive: false);
        var again = Programs(pixelActive: false);
        Assert.Same(again, Programs(pixelActive: false));
        ShaderHeaderRegistry.NoteRegistration();
        Assert.NotSame(again, Programs(pixelActive: false));
    }

    private void WriteContext(uint offset, params uint[] values) =>
        RegisterWriteTable.WriteContextPacket(
            _banks,
            new SharpEmu.Libs.Gpu.GpuCommands.PacketContext(
                SharpEmu.Libs.Gpu.GpuCommands.PacketHeader.Make((uint)values.Length + 2, SharpEmu.Libs.Gpu.GpuCommands.PacketOpcode.SetContextRegister),
                0x1_0000_1000, 0, 100, 100),
            offset,
            values);

    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void GraphicsPrograms_ResolveAgainWhenTheViewportMovesWithClippingOff()
    {
        WriteContext(ContextRegisterOffset.PaClClipCntl, 1u << 16);
        WriteContext(ContextRegisterOffset.PaClViewportXScale, Float(32f), Float(32f), Float(32f), Float(32f));
        _ = Programs(pixelActive: false);
        var cached = Programs(pixelActive: false);
        Assert.Same(cached, Programs(pixelActive: false));

        WriteContext(ContextRegisterOffset.PaClViewportXScale, Float(64f));
        var moved = Programs(pixelActive: false);

        Assert.NotSame(cached, moved);
        Assert.True(moved.VertexInput.ClipSpace.Enabled);
        Assert.Equal(64f, moved.VertexInput.ClipSpace.ScaleX);
    }

    [Fact]
    public void GraphicsPrograms_AreKeptWhenTheViewportMovesWithClippingOn()
    {
        WriteContext(ContextRegisterOffset.PaClViewportXScale, Float(32f), Float(32f), Float(32f), Float(32f));
        _ = Programs(pixelActive: false);
        var cached = Programs(pixelActive: false);

        WriteContext(ContextRegisterOffset.PaClViewportXScale, Float(64f));

        Assert.Same(cached, Programs(pixelActive: false));
    }

    [Fact]
    public void GraphicsPrograms_AreKeptOverDepthAndBlendWrites()
    {
        _ = Programs(pixelActive: true);
        var cached = Programs(pixelActive: true);

        WriteContext(ContextRegisterOffset.DbDepthControl, 0x0000_0006);
        WriteContext(ContextRegisterOffset.CbBlend0Control, 0x2000_0000);
        WriteContext(ContextRegisterOffset.PaSuScModeCntl, 0x0000_0002);

        Assert.Same(cached, Programs(pixelActive: true));
    }

    [Fact]
    public void GraphicsPrograms_ResolveAgainWhenTheShaderInterfaceOrTheColorTargetsChange()
    {
        _ = Programs(pixelActive: true);
        var cached = Programs(pixelActive: true);
        WriteContext(ContextRegisterOffset.CbShaderMask, 0x0000_000F);
        var interfaceChanged = Programs(pixelActive: true);
        Assert.NotSame(cached, interfaceChanged);
        Assert.Same(interfaceChanged, Programs(pixelActive: true));

        WriteContext(ContextRegisterOffset.CbTargetMask, 0x0000_000F);
        Assert.NotSame(interfaceChanged, Programs(pixelActive: true));
    }

    [Fact]
    public void GraphicsPrograms_ThatReadTheBlendStateResolveAgainOnABlendWrite()
    {
        _ = Programs(pixelActive: true);
        var cached = Programs(pixelActive: true);
        var kept = typeof(ShaderPipelineCache).GetField("_keptGraphics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        kept.GetType().GetField("PipelineStateStamp")!.SetValue(kept, _banks.Stamp(RegisterStamp.PipelineState));
        Assert.Same(cached, Programs(pixelActive: true));

        WriteContext(ContextRegisterOffset.CbBlend0Control, 0x2000_0000);

        Assert.NotSame(cached, Programs(pixelActive: true));
    }

    [Fact]
    public void GraphicsPrograms_ResolveAgainWhenTheVertexTableWordsChange()
    {
        _ = Programs(pixelActive: false);
        var cached = Programs(pixelActive: false);
        Assert.Same(cached, Programs(pixelActive: false));
        WriteTables(AttributeTable, BufferTable, stride: 32);
        var changed = Programs(pixelActive: false);
        Assert.NotSame(cached, changed);
        Assert.Equal(32u, changed.VertexInput.Buffers[0].Stride);
    }
}
