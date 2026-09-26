// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Occurrence limiter for repeated import diagnostics.
///
/// A title that polls an unresolved or failing import retries as fast as the CPU
/// allows, with no delay between attempts. An unsampled warning on that path turns
/// every retry into a formatted string plus a synchronous stderr write, so the
/// diagnostic itself becomes the dominant cost of the loop and buries the rest of
/// the log.
///
/// Sampling keeps the occurrences that carry information — the first few, which
/// identify what failed and with which arguments — plus a periodic heartbeat that
/// still shows the loop is running and how far it has got.
/// </summary>
internal sealed class ImportLogSampler
{
    /// <summary>Occurrences always logged, before sampling begins.</summary>
    private const long HeadOccurrences = 8;

    /// <summary>After the head, log one occurrence every this many.</summary>
    private const long SampleInterval = 10_000;

    private readonly object _gate = new();

    // Keyed by a value tuple rather than a composed string: the caller hits this
    // on every retry, so building a key must not allocate.
    private readonly Dictionary<(string Nid, long Discriminator), long> _occurrences = new();

    /// <summary>
    /// Records one occurrence and reports whether it should be logged.
    /// <paramref name="discriminator"/> separates distinct outcomes for the same
    /// NID (a result code, for example) so one noisy failure cannot mask another.
    /// </summary>
    internal bool ShouldLog(string nid, long discriminator)
    {
        // long, not int: the loop in #619 was observed past 73 million dispatches,
        // which is within range of overflowing a 32-bit counter into negatives and
        // silently disabling the head check.
        long count;
        lock (_gate)
        {
            var key = (nid, discriminator);
            _occurrences.TryGetValue(key, out count);
            count++;
            _occurrences[key] = count;
        }

        return count <= HeadOccurrences || count % SampleInterval == 0;
    }

    /// <summary>Forgets every counter, so a fresh run logs its head occurrences again.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _occurrences.Clear();
        }
    }
}
