// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SharpEmu.GUI;

// One launcher per data folder: two would race on the same settings, library cache and
// saves. The first launcher holds an exclusive lock on a file in the data folder (the OS
// drops it if the process dies, so a crash never leaves the launcher locked out) and
// listens on a named pipe; a second launcher finds the lock taken, asks the first over the
// pipe to bring its window forward, and exits.
internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        _pipeName = pipeName;
    }

    public static SingleInstance? Current { get; private set; }

    // The lock, or null when another launcher already holds it (it has been asked to come
    // forward). Any failure to set up the lock lets this launcher run rather than block it.
    public static SingleInstance? TryAcquire(out bool anotherIsRunning)
    {
        anotherIsRunning = false;
        string lockPath;
        try
        {
            lockPath = Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "gui.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var pipeName = "sharpemu-gui-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(lockPath))))[..16].ToLowerInvariant();
        try
        {
            var lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return Current = new SingleInstance(lockFile, pipeName);
        }
        catch (IOException)
        {
            anotherIsRunning = true;
            SignalRunningInstance(pipeName);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Calls activate on the UI thread's behalf each time a second launcher starts.
    public void Listen(Action activate) => _ = ListenAsync(activate, _stop.Token);

    public void Dispose()
    {
        _stop.Cancel();
        _lock.Dispose();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                activate();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that hung up mid-connect; wait for the next one.
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static void SignalRunningInstance(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The running launcher could not be reached; it stays where it is.
        }
    }
}
