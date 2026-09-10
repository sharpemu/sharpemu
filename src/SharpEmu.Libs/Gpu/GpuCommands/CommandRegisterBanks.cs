// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

public enum ContextStateOperation : uint
{
    Clear = 0,
    Push = 1,
    Pop = 2,
    PushClear = 3,
}

// Draw translation reads the register state stored for each queue.
public sealed class CommandRegisterBanks
{
    private uint? _savedCompositeDepthSizeXy;

    public Dictionary<uint, uint> Context { get; } = new();

    public Dictionary<uint, uint> Shader { get; } = new();

    public Dictionary<uint, uint> UserConfig { get; } = new();

    public Dictionary<uint, uint> SavedContext { get; } = new();

    public bool ContextPushed { get; private set; }

    // A depth extent carried by a composite binding packet; a direct write clears it.
    public uint? CompositeDepthSizeXy { get; set; }

    public void Reset()
    {
        Context.Clear();
        Shader.Clear();
        UserConfig.Clear();
        SavedContext.Clear();
        ContextPushed = false;
        CompositeDepthSizeXy = null;
        _savedCompositeDepthSizeXy = null;
    }

    public void ApplyContextState(ContextStateOperation operation, Func<string, Exception> fatal)
    {
        switch (operation)
        {
            case ContextStateOperation.Clear:
                Context.Clear();
                CompositeDepthSizeXy = null;
                break;
            case ContextStateOperation.Push:
                if (ContextPushed)
                {
                    throw fatal("The context state is already pushed.");
                }

                Copy(Context, SavedContext);
                _savedCompositeDepthSizeXy = CompositeDepthSizeXy;
                ContextPushed = true;
                break;
            case ContextStateOperation.Pop:
                if (!ContextPushed)
                {
                    throw fatal("The context state is not pushed.");
                }

                Copy(SavedContext, Context);
                CompositeDepthSizeXy = _savedCompositeDepthSizeXy;
                _savedCompositeDepthSizeXy = null;
                SavedContext.Clear();
                ContextPushed = false;
                break;
            case ContextStateOperation.PushClear:
                if (ContextPushed)
                {
                    throw fatal("The context state is already pushed.");
                }

                Copy(Context, SavedContext);
                _savedCompositeDepthSizeXy = CompositeDepthSizeXy;
                ContextPushed = true;
                Context.Clear();
                CompositeDepthSizeXy = null;
                break;
            default:
                throw fatal($"The context state operation is unknown: operation={(uint)operation}.");
        }
    }

    private static void Copy(Dictionary<uint, uint> source, Dictionary<uint, uint> destination)
    {
        destination.Clear();
        foreach (var (register, value) in source)
        {
            destination[register] = value;
        }
    }
}
