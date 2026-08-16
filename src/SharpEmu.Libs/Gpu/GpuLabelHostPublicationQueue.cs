// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// Publishes compatibility host mirrors after their GPU dependencies complete.
/// Callers use this from one backend-consumer thread.
/// </summary>
internal sealed class GpuLabelHostPublicationQueue
{
    private readonly List<(GuestGpuLabelDependency Dependency, Action Publish)> _pending = [];
    private ulong _completedGraphicsTimeline;
    private ulong _completedComputeTimeline;

    internal int Count => _pending.Count;

    internal void Register(GuestGpuLabelDependency dependency, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (IsComplete(dependency))
        {
            publish();
            return;
        }

        _pending.Add((dependency, publish));
    }

    internal void Complete(GuestGpuLabelDependency dependency)
    {
        _completedGraphicsTimeline = Math.Max(
            _completedGraphicsTimeline,
            dependency.GraphicsTimeline);
        _completedComputeTimeline = Math.Max(
            _completedComputeTimeline,
            dependency.ComputeTimeline);

        // Preserve packet order when several writes become visible together.
        for (var index = 0; index < _pending.Count;)
        {
            var pending = _pending[index];
            if (!IsComplete(pending.Dependency))
            {
                index++;
                continue;
            }

            _pending.RemoveAt(index);
            pending.Publish();
        }
    }

    internal void Cancel() => _pending.Clear();

    private bool IsComplete(GuestGpuLabelDependency dependency) =>
        dependency.GraphicsTimeline <= _completedGraphicsTimeline &&
        dependency.ComputeTimeline <= _completedComputeTimeline;
}
