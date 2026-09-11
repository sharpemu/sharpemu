// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The three typed register banks of one queue, with the state the register writers carry between packets.
public sealed class RegisterBanks
{
    private ContextRegisters? _savedContext;
    private uint? _savedCompositeDepthSizeXy;

    public RegisterBanks(Func<string, Exception> fatal) => Fatal = fatal;

    public Func<string, Exception> Fatal { get; }

    public ContextRegisters Context { get; private set; } = new();

    public ShaderProgramRegisters Shader { get; private set; } = new();

    public UserConfigRegisters UserConfig { get; private set; } = new();

    public bool ContextPushed => _savedContext is not null;

    // A depth extent carried by a composite binding packet; a direct write clears it.
    public uint? CompositeDepthSizeXy { get; set; }

    public UserScalarKind UserDataMarker { get; set; }

    public uint IndexTypeAndSize { get; set; }

    // A copy of the live banks; the saved context and the draw-time markers are not part of it.
    public RegisterBanks Clone()
    {
        var clone = new RegisterBanks(Fatal)
        {
            Context = Context.Copy(),
            Shader = Shader.Copy(),
            UserConfig = UserConfig.Copy(),
            CompositeDepthSizeXy = CompositeDepthSizeXy,
            UserDataMarker = UserDataMarker,
            IndexTypeAndSize = IndexTypeAndSize,
        };
        return clone;
    }

    public void Reset()
    {
        Context = new ContextRegisters();
        Shader = new ShaderProgramRegisters();
        UserConfig = new UserConfigRegisters();
        _savedContext = null;
        _savedCompositeDepthSizeXy = null;
        CompositeDepthSizeXy = null;
        UserDataMarker = UserScalarKind.Unknown;
        IndexTypeAndSize = 0;
    }

    public void ApplyContextState(ContextStateOperation operation)
    {
        switch (operation)
        {
            case ContextStateOperation.Clear:
                Context = new ContextRegisters();
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

                Context = _savedContext;
                CompositeDepthSizeXy = _savedCompositeDepthSizeXy;
                _savedContext = null;
                _savedCompositeDepthSizeXy = null;
                break;
            case ContextStateOperation.PushClear:
                Push();
                Context = new ContextRegisters();
                CompositeDepthSizeXy = null;
                break;
            default:
                throw Fatal($"The context state operation is unknown: operation={(uint)operation}.");
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
