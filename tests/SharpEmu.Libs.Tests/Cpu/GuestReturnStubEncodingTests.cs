// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// The guest return stub is hand-emitted, so nothing type-checks which register holds what across
/// the TLS lookup it performs. A guest reaches this stub by returning, so rax carries its exit
/// code — and <c>TlsGetValue</c> returns in rax too. Calling it without parking the guest value
/// first replaced a clean exit with the address of the host-RSP slot.
///
/// These decode the bytes the emitter actually produces, so the guarantee survives the emission
/// code being re-ordered.
/// </summary>
public sealed unsafe class GuestReturnStubEncodingTests
{
    private const int StubSize = 256;

    [Fact]
    public void GuestExitCodeInRaxSurvivesTheTlsLookup()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        var decoded = DecodeGuestReturnStub();

        var lastCall = LastIndexOf(decoded, i => i.Mnemonic == Mnemonic.Call);
        Assert.True(lastCall >= 0, "the stub is expected to call TlsGetValue");

        // rax is volatile and holds TlsGetValue's result the moment that call returns, so
        // something after it has to put the guest's value back.
        var raxRestore = LastIndexOf(decoded, i =>
            i.Mnemonic == Mnemonic.Pop && i.Op0Register == Register.RAX);
        Assert.True(
            raxRestore > lastCall,
            "rax must be restored after the last call, otherwise the guest exit code is lost");
    }

    /// <summary>
    /// The stack switch has to read the slot through a scratch register rather than rax, matching
    /// what the entry-stub epilogue already does with r10 for the same switch.
    /// </summary>
    [Fact]
    public void StackSwitchDoesNotDereferenceRax()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        var switches = DecodeGuestReturnStub()
            .Where(i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP)
            .ToArray();

        Assert.NotEmpty(switches);
        Assert.All(switches, i => Assert.NotEqual(Register.RAX, i.MemoryBase));
    }

    /// <summary>
    /// Win64 requires RSP ≡ 0 (mod 16) at every call instruction. The stub is entered by a guest
    /// <c>ret</c>, so entry RSP is 16-aligned; walking the stack deltas keeps the added push/pop
    /// from silently changing the phase the original frame established.
    /// </summary>
    [Fact]
    public void EveryCallIsMadeOnASixteenByteAlignedStack()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        long delta = 0;
        foreach (var instruction in DecodeGuestReturnStub())
        {
            switch (instruction.Mnemonic)
            {
                case Mnemonic.Call:
                    Assert.True(
                        ((delta % 16) + 16) % 16 == 0,
                        $"stack is misaligned by {((delta % 16) + 16) % 16} bytes at the call");
                    break;
                case Mnemonic.Push:
                    delta -= 8;
                    break;
                case Mnemonic.Pop:
                    delta += 8;
                    break;
                case Mnemonic.Sub when instruction.Op0Register == Register.RSP:
                    delta -= (long)instruction.GetImmediate(1);
                    break;
                case Mnemonic.Add when instruction.Op0Register == Register.RSP:
                    delta += (long)instruction.GetImmediate(1);
                    break;
                case Mnemonic.Ret:
                    return;
            }
        }
    }

    private static bool IsSupportedHost =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static int LastIndexOf(Instruction[] instructions, Func<Instruction, bool> predicate)
    {
        for (var i = instructions.Length - 1; i >= 0; i--)
        {
            if (predicate(instructions[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Builds the stub the way the backend initializer does. The constructor is skipped — it
    /// installs process-wide handlers — and only the two fields the emitter reads are supplied.
    /// </summary>
    private static Instruction[] DecodeGuestReturnStub()
    {
        var backend = RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
        SetField(backend, "_hostRspSlotTlsIndex", 7u);
        SetField(backend, "_tlsGetValueAddress", (nint)0x1234_5678);

        var create = typeof(DirectExecutionBackend).GetMethod(
            "CreateGuestReturnStub",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stub = (nint)create.Invoke(backend, null)!;
        Assert.NotEqual((nint)0, stub);

        var code = new byte[StubSize];
        Marshal.Copy(stub, code, 0, code.Length);

        var decoder = Decoder.Create(64, code);
        decoder.IP = (ulong)stub;
        var decoded = new List<Instruction>();
        while (decoder.IP - (ulong)stub < (ulong)code.Length)
        {
            var instruction = decoder.Decode();
            decoded.Add(instruction);
            if (instruction.Mnemonic == Mnemonic.Ret)
            {
                break;
            }
        }

        return [.. decoded];
    }

    private static void SetField(object target, string name, object value) =>
        typeof(DirectExecutionBackend)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
}
