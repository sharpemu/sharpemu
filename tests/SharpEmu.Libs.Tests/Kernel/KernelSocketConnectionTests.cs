// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSocketConnectionTests
{
    private const string WorkerVariable = "SHARPEMU_SOCKET_CONNECTION_TEST_WORKER";

    [Fact]
    public async Task ConnectDoesNotRequireAnAvailableThreadPoolWorker()
    {
        if (Environment.GetEnvironmentVariable(WorkerVariable) != "1")
        {
            await RunIsolatedWorker();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumCompletionThreads);
            ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumCompletionThreads);
            using var workerEntered = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            using var workerExited = new ManualResetEventSlim();
            var queued = false;
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                queued = ThreadPool.QueueUserWorkItem(_ =>
                {
                    workerEntered.Set();
                    releaseWorker.Wait();
                    workerExited.Set();
                });
                Assert.True(queued);
                Assert.True(workerEntered.Wait(TimeSpan.FromSeconds(10)), "The blocking worker did not start.");
                Assert.True(ThreadPool.SetMinThreads(1, minimumCompletionThreads));
                Assert.True(ThreadPool.SetMaxThreads(1, maximumCompletionThreads));
                ConnectToPort(((IPEndPoint)listener.LocalEndpoint).Port, succeeds: true);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                releaseWorker.Set();
                ThreadPool.SetMaxThreads(maximumWorkers, maximumCompletionThreads);
                ThreadPool.SetMinThreads(minimumWorkers, minimumCompletionThreads);
                if (queued) workerExited.Wait();
            }
        }) { IsBackground = true };
        thread.Start();
        await completion.Task;
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The connection worker did not exit.");
    }

    [Fact]
    public void RefusedConnectionReturnsGuestFailureAndKeepsDescriptorOpen()
    {
        // Reserve an endpoint without listening so no other process can take the port.
        using var endpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        endpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        ConnectToPort(((IPEndPoint)endpoint.LocalEndPoint!).Port, succeeds: false);
    }

    private static void ConnectToPort(int port, bool succeeds)
    {
        const ulong memoryBase = 0x10000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 6;
        Assert.Equal(0, KernelSocketCompatExports.Socket(context));
        var descriptor = checked((int)context[CpuRegister.Rax]);
        try
        {
            Span<byte> address = stackalloc byte[16];
            address.Clear();
            address[0] = 16;
            address[1] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(address[2..], checked((ushort)port));
            IPAddress.Loopback.GetAddressBytes().CopyTo(address[4..]);
            Assert.True(memory.TryWrite(memoryBase, address));
            context[CpuRegister.Rdi] = (ulong)descriptor;
            context[CpuRegister.Rsi] = memoryBase;
            context[CpuRegister.Rdx] = 16;
            Assert.Equal(0, KernelSocketCompatExports.Connect(context));
            Assert.Equal(succeeds ? 0UL : ulong.MaxValue, context[CpuRegister.Rax]);
            Assert.True(KernelSocketCompatExports.IsEmulatedSocketFd(descriptor));
        }
        finally
        {
            Assert.True(KernelSocketCompatExports.TryCloseSocketFd(descriptor));
        }
    }

    private static async Task RunIsolatedWorker()
    {
        var processPath = Environment.ProcessPath;
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            (Path.GetFileNameWithoutExtension(processPath) == "dotnet" ? processPath : "dotnet");
        var start = new ProcessStartInfo(dotnet!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(typeof(KernelSocketConnectionTests).Assembly.Location);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add($"FullyQualifiedName={typeof(KernelSocketConnectionTests).FullName}.{nameof(ConnectDoesNotRequireAnAvailableThreadPoolWorker)}");
        start.Environment[WorkerVariable] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail($"The isolated connection test stalled.\n{await output}\n{await errors}");
        }
        Assert.True(process.ExitCode == 0, $"{await output}\n{await errors}");
    }
}
