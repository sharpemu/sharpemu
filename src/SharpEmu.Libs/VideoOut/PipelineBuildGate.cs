// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// One build of a driver pipeline key at a time (CreatePipelineWithBinaries). A creation whose key another thread is
// building waits for that build and then loads the binaries it stored, instead of compiling the same pipeline beside
// it: at boot the session can ask for compute pipelines the prewarm is still compiling. A waiter lends a lower-priority
// builder (a below-normal prewarm worker) its own priority until the build ends.
internal sealed class PipelineBuildGate
{
    private readonly Dictionary<string, Build> _builds = new(StringComparer.Ordinal);

    internal sealed class Build
    {
        public required string Key { get; init; }
        public required Thread Builder { get; init; }
        public required ThreadPriority Priority { get; init; }
        public bool Raised;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Registers the calling thread as the builder of key, after waiting for any other thread that builds it now.
    public Build Begin(string key)
    {
        var thread = Thread.CurrentThread;
        while (true)
        {
            Build? running;
            lock (_builds)
            {
                if (!_builds.TryGetValue(key, out running))
                {
                    var build = new Build { Key = key, Builder = thread, Priority = thread.Priority };
                    _builds.Add(key, build);
                    return build;
                }

                if (running.Builder.Priority < thread.Priority)
                {
                    running.Builder.Priority = thread.Priority;
                    running.Raised = true;
                }
            }

            running.Done.Task.Wait();
        }
    }

    // Ends the calling thread's build: restores a priority a waiter raised and wakes the waiters, also when the restore fails.
    public void Finish(Build build)
    {
        try
        {
            lock (_builds)
            {
                _builds.Remove(build.Key);
                if (build.Raised)
                {
                    build.Builder.Priority = build.Priority;
                }
            }
        }
        finally
        {
            build.Done.SetResult();
        }
    }
}
