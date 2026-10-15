// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Opt-in per-guest-thread host QoS, driven by <c>SHARPEMU_GUEST_THREAD_QOS</c>:
/// semicolon-separated <c>PATTERN=CLASS</c> rules, first match wins. PATTERN is a
/// case-sensitive glob (<c>*</c>, <c>?</c>) over the full guest thread name and
/// CLASS is a <see cref="HostThreadQos"/> member name. A value with no <c>=</c> is
/// the older single-class diagnostic switch and is ignored here.
/// </summary>
/// <remarks>
/// A matched guest thread is created at its class through <see cref="HostQosThread"/>
/// (natively on macOS, where CoreCLR threads refuse a later class change), so the
/// rules apply on every host with that host's mapping of the class.
/// </remarks>
public static class GuestThreadQosPolicy
{
    private static readonly IReadOnlyList<Rule>? s_rules = LoadFromEnvironment();
    private static readonly ConcurrentDictionary<ulong, HostThreadQos> s_pending = new();
    private static int s_pendingCount;
    private static int s_deferredLogged;

    public readonly record struct Rule(string Pattern, HostThreadQos Class);

    public static bool IsEnabled => s_rules is not null;

    private static IReadOnlyList<Rule>? LoadFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_THREAD_QOS");
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('='))
        {
            return null;
        }

        if (!TryParseRules(value, out var rules, out var error))
        {
            Console.Error.WriteLine($"[SCHED][QOS] ignoring SHARPEMU_GUEST_THREAD_QOS: {error}");
            return null;
        }

        return rules;
    }

    /// <summary>Parses <c>PATTERN=CLASS;PATTERN=CLASS</c>. Empty segments are skipped.</summary>
    public static bool TryParseRules(string? text, out IReadOnlyList<Rule> rules, out string? error)
    {
        var parsed = new List<Rule>();
        rules = parsed;
        error = null;
        foreach (var segment in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.LastIndexOf('=');
            if (separator <= 0)
            {
                error = $"rule '{segment}' is not PATTERN=CLASS";
                return false;
            }

            var pattern = segment[..separator];
            var className = segment[(separator + 1)..].Trim();
            if (!Enum.TryParse<HostThreadQos>(className, ignoreCase: false, out var qos) ||
                !Enum.IsDefined(qos))
            {
                error = $"rule '{segment}' has unknown class '{className}' (expected one of " +
                    $"{string.Join(", ", Enum.GetNames<HostThreadQos>())})";
                return false;
            }

            parsed.Add(new Rule(pattern, qos));
        }

        return true;
    }

    /// <summary>First matching rule wins.</summary>
    public static bool TryResolve(IReadOnlyList<Rule> rules, string name, out HostThreadQos qos)
    {
        foreach (var rule in rules)
        {
            if (GlobMatch(rule.Pattern, name))
            {
                qos = rule.Class;
                return true;
            }
        }

        qos = default;
        return false;
    }

    /// <summary>Case-sensitive glob: <c>*</c> matches any run, <c>?</c> exactly one char.</summary>
    public static bool GlobMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]) && pattern[p] != '*')
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    /// Applies the matching class to the calling host thread, which must be the host
    /// thread running the guest thread named <paramref name="name"/>. No-op when the
    /// env var is unset or no rule matches.
    /// </summary>
    public static void ApplyToCurrentThread(string? name)
    {
        var rules = s_rules;
        if (rules is null || string.IsNullOrEmpty(name) || !TryResolve(rules, name, out var qos))
        {
            return;
        }

        LogApply(name, qos, HostThreadQualityOfService.TryApplyToCurrentThread(qos, out var errno), errno);
    }

    private static void LogApply(string name, HostThreadQos qos, bool ok, int errno, string suffix = "")
    {
        Console.Error.WriteLine(
            $"[SCHED][QOS] thread={name} class={qos} ok={ok}{(ok ? string.Empty : $" errno={errno}")}{suffix}");
    }

    /// <summary>The class a thread with this name should be created at, if any rule matches.</summary>
    public static bool TryGetClassForNewThread(string? name, out HostThreadQos qos)
    {
        qos = default;
        var rules = s_rules;
        return rules is not null && !string.IsNullOrEmpty(name) && TryResolve(rules, name, out qos);
    }

    /// <summary>Verifies and logs the class of a thread created with <see cref="HostQosThread"/>;
    /// call from that thread.</summary>
    public static void LogQosThreadStart(string name, HostThreadQos qos)
    {
        LogApply(
            name,
            qos,
            HostThreadQualityOfService.IsCurrentThreadAt(qos),
            0,
            $" (created at class, now {HostThreadQualityOfService.DescribeCurrentThread()})");
    }

    /// <summary>
    /// A rename aimed at <paramref name="targetHandle"/>. Self-renames apply now;
    /// renames of another thread are recorded and applied by
    /// <see cref="ApplyPendingForCurrentThread"/> the next time that thread runs.
    /// </summary>
    public static void OnRename(ulong targetHandle, ulong currentGuestHandle, string? newName)
    {
        var rules = s_rules;
        if (rules is null || string.IsNullOrEmpty(newName) || !TryResolve(rules, newName, out var qos))
        {
            return;
        }

        if (targetHandle == currentGuestHandle)
        {
            ApplyToCurrentThread(newName);
            return;
        }

        s_pending[targetHandle] = qos;
        Interlocked.Increment(ref s_pendingCount);
        if (Interlocked.Exchange(ref s_deferredLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[SCHED][QOS] rename of another thread ({newName}) deferred until it next runs");
        }
    }

    /// <summary>Hook run on a guest thread's own host thread before each slice.</summary>
    public static void ApplyPendingForCurrentThread(ulong guestHandle)
    {
        if (s_rules is null || Volatile.Read(ref s_pendingCount) == 0)
        {
            return;
        }

        if (!s_pending.TryRemove(guestHandle, out var qos))
        {
            return;
        }

        Interlocked.Decrement(ref s_pendingCount);
        var ok = HostThreadQualityOfService.TryApplyToCurrentThread(qos, out var errno);
        LogApply($"0x{guestHandle:X}", qos, ok, errno, " (deferred rename)");
    }
}
