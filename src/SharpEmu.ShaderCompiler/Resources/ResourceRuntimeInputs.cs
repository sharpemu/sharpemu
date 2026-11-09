// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// Reads one dword of guest memory; false when the address cannot be read.
public delegate bool GuestWordReader(ulong address, out uint word);

// What one draw supplies to materialise a plan: its user data, the shader base and
// the two memory readers. The clean reader refuses memory the GPU may still own.
public sealed class ResourceRuntimeInputs
{
    public IReadOnlyList<uint> UserData { get; init; } = [];
    public ulong ShaderBase { get; init; }
    public GuestWordReader? ReadMemory { get; init; }
    public GuestWordReader? ReadCleanMemory { get; init; }
    public ComputeSelectorState? ComputeState { get; init; }
    public Action<uint, uint, ulong, ulong, ReadOnlyMemory<uint>>? TraceImageCandidate { get; init; }

    // Told true before the flattened table's words are evaluated and false after, so a reader
    // wrapper can tell the words only the table reads from those the descriptors depend on.
    public Action<bool>? TablePhase { get; init; }

    public ResidentGuestBytesReader? ReadResidentMemory { get; init; }

    public bool ReadsClean { get; init; }

    public ResourceRuntimeInputs WithReader(GuestWordReader? reader) => new()
    {
        UserData = UserData,
        ShaderBase = ShaderBase,
        ReadMemory = reader,
        ReadCleanMemory = ReadCleanMemory,
        ComputeState = ComputeState,
        TraceImageCandidate = TraceImageCandidate,
        TablePhase = TablePhase,
        ReadResidentMemory = ReadResidentMemory,
        ReadsClean = ReadsClean || ReferenceEquals(reader, ReadCleanMemory),
    };

    // The inputs of a linked plan whose call entries read their arguments from user data
    // (ScalarValueGraph.CallArgumentRegisterBase): this draw's user data, then two registers per argument.
    public ResourceRuntimeInputs WithCallArguments(uint userDataBase, IReadOnlyList<ulong> arguments)
    {
        if (userDataBase > ScalarValueGraph.CallArgumentRegisterBase ||
            UserData.Count > ScalarValueGraph.CallArgumentRegisterBase - userDataBase)
        {
            throw new ArgumentOutOfRangeException(nameof(userDataBase), "The user data reaches the call argument registers.");
        }

        var first = (int)(ScalarValueGraph.CallArgumentRegisterBase - userDataBase);
        var userData = new uint[first + arguments.Count * 2];
        for (var index = 0; index < UserData.Count; index++)
        {
            userData[index] = UserData[index];
        }

        var offset = first;
        foreach (var argument in arguments)
        {
            userData[offset++] = (uint)argument;
            userData[offset++] = (uint)(argument >> 32);
        }

        return new()
        {
            UserData = userData,
            ShaderBase = ShaderBase,
            ReadMemory = ReadMemory,
            ReadCleanMemory = ReadCleanMemory,
            ComputeState = ComputeState,
            TraceImageCandidate = TraceImageCandidate,
            TablePhase = TablePhase,
            ReadResidentMemory = ReadResidentMemory,
            ReadsClean = ReadsClean,
        };
    }
}

public readonly record struct ComputeSelectorState(uint WaveSize, uint ThreadsX, uint ThreadsY, uint ThreadsZ,
    bool HasPartialWorkgroups, uint LocalDataShareDwords, int LocalInvocationIdComponents);
