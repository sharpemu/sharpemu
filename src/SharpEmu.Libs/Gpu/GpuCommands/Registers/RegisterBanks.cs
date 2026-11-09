// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The three typed register banks of one queue, with the state the register writers carry between packets.
public sealed class RegisterBanks
{
    private ContextRegisters? _savedContext;
    private uint? _savedCompositeDepthSizeXy;
    private static long _writeSerial;
    private RegisterStampSet _stamps = InitialStamps();

    public RegisterBanks(Func<string, Exception> fatal)
        : this(fatal, new ContextRegisters(), new ShaderProgramRegisters(), new UserConfigRegisters())
    {
    }

    // Banks over given parts, so a copy or a snapshot does not first build empty ones.
    private RegisterBanks(
        Func<string, Exception> fatal,
        ContextRegisters context,
        ShaderProgramRegisters shader,
        UserConfigRegisters userConfig)
    {
        Fatal = fatal;
        Context = context;
        Shader = shader;
        UserConfig = userConfig;
    }

    public Func<string, Exception> Fatal { get; private set; }

    public ContextRegisters Context { get; private set; }

    public ShaderProgramRegisters Shader { get; private set; }

    public UserConfigRegisters UserConfig { get; private set; }

    public bool ContextPushed => _savedContext is not null;

    public ulong Stamp(RegisterStamp stamp) => _stamps[(int)stamp];

    public ulong Stamp(RegisterStamp first, RegisterStamp second) => Math.Max(_stamps[(int)first], _stamps[(int)second]);

    public ulong Stamp(RegisterStamp first, RegisterStamp second, RegisterStamp third) =>
        Math.Max(Math.Max(_stamps[(int)first], _stamps[(int)second]), _stamps[(int)third]);

    public ulong ContextStamp =>
        Math.Max(
            Math.Max(_stamps[(int)RegisterStamp.ColorTargets], _stamps[(int)RegisterStamp.DepthTargets]),
            Math.Max(_stamps[(int)RegisterStamp.ShaderInterface], _stamps[(int)RegisterStamp.PipelineState]));

    public void MarkWritten(uint stampBits)
    {
        while (stampBits != 0)
        {
            _stamps[System.Numerics.BitOperations.TrailingZeroCount(stampBits)] = NextSerial();
            stampBits &= stampBits - 1;
        }
    }

    private static ulong NextSerial() => (ulong)Interlocked.Increment(ref _writeSerial);

    private static RegisterStampSet InitialStamps()
    {
        var stamps = new RegisterStampSet();
        for (var index = 0; index < RegisterStamps.Count; index++)
        {
            stamps[index] = NextSerial();
        }

        return stamps;
    }

    // A depth extent carried by a composite binding packet; a direct write clears it.
    public uint? CompositeDepthSizeXy { get; set; }

    public UserScalarKind UserDataMarker { get; set; }

    public uint IndexTypeAndSize { get; set; }

    // A copy of the live banks; the saved context and the draw-time markers are not part of it.
    public RegisterBanks Clone()
    {
        var clone = new RegisterBanks(Fatal, Context.Copy(), Shader.Copy(), UserConfig.Copy())
        {
            CompositeDepthSizeXy = CompositeDepthSizeXy,
            UserDataMarker = UserDataMarker,
            IndexTypeAndSize = IndexTypeAndSize,
            _stamps = _stamps,
        };
        return clone;
    }

    // The parts a snapshot shares with these banks; each is copied before its next write here.
    private bool _contextShared;
    private bool _vertexShared;
    private bool _pixelShared;
    private bool _computeShared;
    private bool _userConfigShared;

    // A read-only copy that shares every part with the live banks until they next write it: a
    // deferred draw takes one per draw, and between draws only a few parts change.
    public RegisterBanks Snapshot() => Snapshot(null);

    public RegisterBanks Snapshot(RegisterBanks? shell)
    {
        RegisterBanks snapshot;
        if (shell is null)
        {
            snapshot = new RegisterBanks(Fatal, Context, Shader.ShareStages(), UserConfig);
        }
        else
        {
            snapshot = shell;
            snapshot.Fatal = Fatal;
            snapshot.Shader.ShareStagesOf(Shader);
            snapshot.UserConfig = UserConfig;
            snapshot._savedContext = null;
            snapshot._savedCompositeDepthSizeXy = null;
            snapshot._userConfigShared = snapshot._vertexShared = snapshot._pixelShared = snapshot._computeShared = false;
            snapshot._pixelScalarsShared = snapshot._hullScalarsShared = snapshot._geometryScalarsShared = false;
            snapshot._legacyVertexScalarsShared = snapshot._exportScalarsShared = snapshot._computeScalarsShared = false;
            snapshot.OwnContext(Context);
        }

        snapshot.CompositeDepthSizeXy = CompositeDepthSizeXy;
        snapshot.UserDataMarker = UserDataMarker;
        snapshot.IndexTypeAndSize = IndexTypeAndSize;
        snapshot._stamps = _stamps;
        SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteSnapshot();
        _contextShared = _vertexShared = _pixelShared = _computeShared = _userConfigShared = true;
        _pixelScalarsShared = _hullScalarsShared = _geometryScalarsShared = _legacyVertexScalarsShared = _exportScalarsShared = _computeScalarsShared = true;
        return snapshot;
    }

    public ContextRegisters ShareContext()
    {
        _contextShared = true;
        return Context;
    }

    // Called by the register writers before they store into a part.
    public void PrepareContextWrite()
    {
        if (_contextShared)
        {
            Context = Context.ShallowCopy();
            SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContext();
            _contextShared = false;
            SetContextPartsShared(true);
        }
    }

    // The context is copied without its nested parts, and a part only when a writer asks for it
    // through these accessors: a draw usually rewrites a few plain fields.
    private bool _screenViewportShared;
    private bool _secondaryViewportsShared;
    private bool _sampleLocationsShared;
    private bool _shaderInterfaceShared;
    private bool _blendControlsShared;
    private bool _colorTargetsShared;
    private bool _colorClearWord1Shared;
    private bool _unmodeledShared;

    private void SetContextPartsShared(bool shared) =>
        _screenViewportShared = _secondaryViewportsShared = _sampleLocationsShared = _shaderInterfaceShared =
            _blendControlsShared = _colorTargetsShared = _colorClearWord1Shared = _unmodeledShared = shared;

    private void OwnContext(ContextRegisters context)
    {
        Context = context;
        _contextShared = false;
        SetContextPartsShared(false);
    }

    public ScreenViewportRegisters ScreenViewportForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_screenViewportShared)
            {
                Context.ScreenViewport = Context.ScreenViewport.CopySharingSecondary();
                _screenViewportShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ScreenViewport;
        }
    }

    public ref ViewportRegisters ViewportForWrite(int slot)
    {
        var screenViewport = ScreenViewportForWrite;
        if (slot == 0)
        {
            return ref screenViewport.Primary;
        }

        if (_secondaryViewportsShared)
        {
            screenViewport.Secondary = (ViewportRegisters[])screenViewport.Secondary.Clone();
            _secondaryViewportsShared = false;
            SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
        }

        return ref screenViewport.Secondary[slot - 1];
    }

    public SampleLocationRegisters SampleLocationsForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_sampleLocationsShared)
            {
                Context.SampleLocations = Context.SampleLocations.Copy();
                _sampleLocationsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.SampleLocations;
        }
    }

    public ShaderInterfaceRegisters ShaderInterfaceForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_shaderInterfaceShared)
            {
                Context.ShaderInterface = Context.ShaderInterface.Copy();
                _shaderInterfaceShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ShaderInterface;
        }
    }

    public BlendRegisters[] BlendControlsForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_blendControlsShared)
            {
                Context.BlendControls = (BlendRegisters[])Context.BlendControls.Clone();
                _blendControlsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.BlendControls;
        }
    }

    public ColorTargetWords[] ColorTargetsForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_colorTargetsShared)
            {
                Context.ColorTargets = (ColorTargetWords[])Context.ColorTargets.Clone();
                _colorTargetsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ColorTargets;
        }
    }

    public uint[] ColorClearWord1ForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_colorClearWord1Shared)
            {
                Context.ColorClearWord1 = (uint[])Context.ColorClearWord1.Clone();
                _colorClearWord1Shared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ColorClearWord1;
        }
    }

    public Dictionary<uint, uint> UnmodeledTableRegistersForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_unmodeledShared)
            {
                Context.UnmodeledTableRegisters = new Dictionary<uint, uint>(Context.UnmodeledTableRegisters);
                _unmodeledShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteUnmodeledTable();
            }

            return Context.UnmodeledTableRegisters;
        }
    }

    public void PrepareUserConfigWrite()
    {
        if (_userConfigShared)
        {
            UserConfig = UserConfig.Copy();
            _userConfigShared = false;
            SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteUserConfig();
        }
    }

    // COMPUTE_DISPATCH_INITIATOR, the first register of the compute range.
    private const uint ComputeRangeStart = 0x200;

    // A stage is copied without its user scalar sets, and a set only when its window is written:
    // a draw usually rewrites one set of one or two stages.
    private bool _pixelScalarsShared;
    private bool _hullScalarsShared;
    private bool _geometryScalarsShared;
    private bool _legacyVertexScalarsShared;
    private bool _exportScalarsShared;
    private bool _computeScalarsShared;
    private const uint UserScalarWindow = UserScalarRegisters.Capacity;

    private static bool Overlaps(uint first, uint last, uint windowStart) =>
        first < windowStart + UserScalarWindow && last >= windowStart;

    // The shader bank's offsets: pixel below SPI_SHADER_PGM_LO_VS, the geometry stages up to the
    // compute range, compute from there.
    public void PrepareShaderWrite(uint offset, uint count)
    {
        var last = offset + Math.Max(count, 1) - 1;
        if (offset < ShaderRegisterOffset.SpiShaderPgmLoVs)
        {
            if (_pixelShared)
            {
                Shader.Pixel = Shader.Pixel.ShallowCopy();
                _pixelShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteStage();
            }

            if (_pixelScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataPs0))
            {
                Shader.Pixel.UserScalars = Shader.Pixel.UserScalars.Copy();
                _pixelScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }
        }

        if (offset < ComputeRangeStart && last >= ShaderRegisterOffset.SpiShaderPgmLoVs)
        {
            if (_vertexShared)
            {
                Shader.Vertex = Shader.Vertex.ShallowCopy();
                _vertexShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteStage();
            }

            var vertex = Shader.Vertex;
            if (_hullScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataHs0))
            {
                vertex.HullUserScalars = vertex.HullUserScalars.Copy();
                _hullScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }

            if (_geometryScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataGs0))
            {
                vertex.GeometryUserScalars = vertex.GeometryUserScalars.Copy();
                _geometryScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }

            if (_legacyVertexScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataVs0))
            {
                vertex.LegacyVertexUserScalars = vertex.LegacyVertexUserScalars.Copy();
                _legacyVertexScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }

            if (_exportScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataEs0))
            {
                vertex.ExportUserScalars = vertex.ExportUserScalars.Copy();
                _exportScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }
        }

        if (last >= ComputeRangeStart)
        {
            if (_computeShared)
            {
                Shader.Compute = Shader.Compute.ShallowCopy();
                _computeShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteStage();
            }

            if (_computeScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.ComputeUserData0))
            {
                Shader.Compute.UserScalars = Shader.Compute.UserScalars.Copy();
                _computeScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }
        }
    }

    public void Reset()
    {
        _contextShared = _vertexShared = _pixelShared = _computeShared = _userConfigShared = false;
        _pixelScalarsShared = _hullScalarsShared = _geometryScalarsShared = _legacyVertexScalarsShared = _exportScalarsShared = _computeScalarsShared = false;
        OwnContext(new ContextRegisters());
        Shader = new ShaderProgramRegisters();
        UserConfig = new UserConfigRegisters();
        _savedContext = null;
        _savedCompositeDepthSizeXy = null;
        CompositeDepthSizeXy = null;
        UserDataMarker = UserScalarKind.Unknown;
        IndexTypeAndSize = 0;
        MarkWritten(RegisterStamps.AllBits);
    }

    public void ApplyContextState(ContextStateOperation operation)
    {
        var previousViewport = Context.ScreenViewport.Primary;
        switch (operation)
        {
            case ContextStateOperation.Clear:
                OwnContext(new ContextRegisters());
                CompositeDepthSizeXy = null;
                break;
            case ContextStateOperation.Push:
                Push();
                break;
            case ContextStateOperation.Pop:
                if (_savedContext is null)
                {
                    throw Fatal("The context state is not pushed.");
                }

                OwnContext(_savedContext);
                CompositeDepthSizeXy = _savedCompositeDepthSizeXy;
                _savedContext = null;
                _savedCompositeDepthSizeXy = null;
                break;
            case ContextStateOperation.PushClear:
                Push();
                OwnContext(new ContextRegisters());
                CompositeDepthSizeXy = null;
                break;
            default:
                throw Fatal($"The context state operation is unknown: operation={(uint)operation}.");
        }

        MarkWritten(RegisterStamps.ContextBits);
        if (Rendering.RenderTrace.Enabled)
        {
            var currentViewport = Context.ScreenViewport.Primary;
            Rendering.RenderTrace.Write(
                $"ContextState operation={operation} previousZScale={previousViewport.ZScale} previousZOffset={previousViewport.ZOffset} " +
                $"zScale={currentViewport.ZScale} zOffset={currentViewport.ZOffset}");
        }
    }

    private void Push()
    {
        if (_savedContext is not null)
        {
            throw Fatal("The context state is already pushed.");
        }

        _savedContext = Context.Copy();
        _savedCompositeDepthSizeXy = CompositeDepthSizeXy;
    }
}
