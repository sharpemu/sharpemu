// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

/// <summary>
/// Per-game environment switches as overrides of the global list, one variable at a time. A game
/// stores an entry only for a variable whose state it changes, so later global changes to every
/// other variable still reach the game.
/// </summary>
public static class EnvironmentOverrides
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The variable an entry ("NAME" or "NAME=value") sets.</summary>
    public static string Name(string entry) => entry.Split('=', 2, StringSplitOptions.TrimEntries)[0];

    /// <summary>
    /// The normalized entry for <paramref name="variable"/> after every entry in the list applies,
    /// or null while the variable is in its default state (unset or off).
    /// </summary>
    public static string? StateOf(IEnumerable<string> entries, string variable)
    {
        var materialized = entries as IReadOnlyCollection<string> ?? entries.ToList();
        if (EnvironmentSettings.Find(variable) is { } setting)
        {
            return EnvironmentSettings.Canonical(materialized, setting);
        }

        if (NameComparer.Equals(variable, StrictComputeSettings.VariableName))
        {
            return StrictComputeSettings.IsEnabled(materialized) ? null : variable + "=0";
        }

        string? last = null;
        foreach (var entry in materialized)
        {
            if (NameComparer.Equals(Name(entry), variable))
            {
                last = entry;
            }
        }

        if (last is null)
        {
            return null;
        }

        var parts = last.Split('=', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 1 || parts[1] == "1" ? variable
            : parts[1] == "0" || parts[1].Length == 0 ? null
            : $"{variable}={parts[1]}";
    }

    /// <summary>Whether a plain switch is on: its last entry is bare or not "0".</summary>
    public static bool IsSet(IEnumerable<string> entries, string variable) => StateOf(entries, variable) is not null;

    /// <summary>
    /// The global list with the game's entries laid over it: a variable the game names takes the
    /// game's state, every other variable keeps the global one. Default states drop out.
    /// </summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<string> global, IReadOnlyList<string>? perGame)
    {
        if (perGame is null)
        {
            return global;
        }

        var overridden = new HashSet<string>(perGame.Select(Name), NameComparer);
        var merged = global.Where(entry => !overridden.Contains(Name(entry))).Concat(perGame).ToList();
        return Normalize(merged);
    }

    /// <summary>
    /// The game's overrides after an edit: variables whose state changed from
    /// <paramref name="baseline"/> to <paramref name="current"/> take the current state, and
    /// <paramref name="stored"/> keeps every other variable it already overrode.
    /// </summary>
    public static List<string>? PickChanged(
        IReadOnlyList<string> current,
        IReadOnlyList<string> baseline,
        IReadOnlyList<string>? stored)
    {
        var changed = new HashSet<string>(
            current.Concat(baseline).Select(Name)
                .Where(variable => !string.Equals(
                    StateOf(current, variable),
                    StateOf(baseline, variable),
                    StringComparison.OrdinalIgnoreCase)),
            NameComparer);

        var result = stored?.Where(entry => !changed.Contains(Name(entry))).ToList() ?? [];
        foreach (var variable in changed)
        {
            result.Add(Explicit(variable, StateOf(current, variable)));
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// The overrides that turn <paramref name="global"/> into <paramref name="target"/>: one explicit
    /// entry for every variable whose state differs; null when none does.
    /// </summary>
    public static List<string>? Between(IReadOnlyList<string> global, IReadOnlyList<string> target)
    {
        var result = global.Concat(target).Select(Name)
            .Where(name => name.Length != 0)
            .Distinct(NameComparer)
            .Where(variable => !string.Equals(
                StateOf(target, variable),
                StateOf(global, variable),
                StringComparison.OrdinalIgnoreCase))
            .Select(variable => Explicit(variable, StateOf(target, variable)))
            .ToList();
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Keeps only the variables whose state differs from the global list, one explicit entry each;
    /// null when nothing differs.
    /// </summary>
    public static List<string>? WithoutInherited(IReadOnlyList<string> perGame, IReadOnlyList<string> global)
    {
        var result = new List<string>();
        foreach (var variable in perGame.Select(Name).Where(name => name.Length != 0).Distinct(NameComparer))
        {
            var state = StateOf(perGame, variable);
            if (!string.Equals(state, StateOf(global, variable), StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Explicit(variable, state));
            }
        }

        return result.Count == 0 ? null : result;
    }

    // One entry per variable in its effective state; default states are left out so the
    // emulator's own default applies.
    private static List<string> Normalize(IReadOnlyList<string> entries) =>
        entries.Select(Name)
            .Where(name => name.Length != 0)
            .Distinct(NameComparer)
            .Select(variable => StateOf(entries, variable))
            .OfType<string>()
            .ToList();

    // A state written so that it still overrides the global list when it is the default one.
    private static string Explicit(string variable, string? state)
    {
        if (state is not null)
        {
            return state;
        }

        if (EnvironmentSettings.Find(variable) is { } setting)
        {
            return setting.Kind == EnvironmentSettingKind.Toggle
                ? $"{setting.Variable}={(setting.DefaultEnabled ? "1" : setting.OffValue)}"
                : $"{setting.Variable}={setting.DefaultChoice}";
        }

        return NameComparer.Equals(variable, StrictComputeSettings.VariableName) ? variable : variable + "=0";
    }
}
