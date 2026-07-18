// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.CxxAbi;
using Xunit;

namespace SharpEmu.Libs.Tests.Libc;

public sealed class CxxAbiExportsTests
{
    private const ulong MemoryBase = 0x0000_7FFF_1100_0000;
    private const ulong GuardAddress = MemoryBase + 0x1000;
    private const ulong GuestThreadHandle = 0x0000_0200_1234_5000;

    [Fact]
    public void GuardRelease_AfterHostThreadMigration_UsesGuestThreadIdentity()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);

        var acquire = RunOnNewHostThread(memory, CxaGuardExports.CxaGuardAcquire);
        var release = RunOnNewHostThread(memory, CxaGuardExports.CxaGuardRelease);

        Assert.NotEqual(acquire.ManagedThreadId, release.ManagedThreadId);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, acquire.Result);
        Assert.Equal(1UL, acquire.Rax);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, release.Result);
        Assert.Equal(0UL, release.Rax);
        var verifyContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(verifyContext.TryReadUInt64(GuardAddress, out var guardWord));
        Assert.Equal(1UL, guardWord & 0xFFFFUL);

        var completedAcquire = RunOnNewHostThread(memory, CxaGuardExports.CxaGuardAcquire);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, completedAcquire.Result);
        Assert.Equal(0UL, completedAcquire.Rax);
    }

    private static GuardCallResult RunOnNewHostThread(
        FakeCpuMemory memory,
        Func<CpuContext, int> operation)
    {
        GuardCallResult result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            var previousGuestThread = GuestThreadExecution.EnterGuestThread(GuestThreadHandle);
            try
            {
                var context = new CpuContext(memory, Generation.Gen5);
                context[CpuRegister.Rdi] = GuardAddress;
                result = new GuardCallResult(
                    operation(context),
                    context[CpuRegister.Rax],
                    Environment.CurrentManagedThreadId);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previousGuestThread);
            }
        });

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(exception);
        return result;
    }

    private readonly record struct GuardCallResult(int Result, ulong Rax, int ManagedThreadId);
}
