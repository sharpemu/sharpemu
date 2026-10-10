// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal sealed class RecordDifference
{
    private const int MaxDepth = 8;
    private const int MaxDifferences = 3;

    private readonly List<string> _differences = [];

    public bool Full => _differences.Count >= MaxDifferences;

    public bool Any => _differences.Count != 0;

    public override string ToString() => string.Join("; ", _differences);

    public RecordDifference Compare(string path, object? recorded, object? derived)
    {
        Compare(path, recorded, derived, 0);
        return this;
    }

    public static string Category(string difference)
    {
        var end = difference.IndexOf(' ');
        var path = end < 0 ? difference : difference[..end];
        var builder = new StringBuilder(path.Length);
        var skipping = false;
        foreach (var character in path)
        {
            if (character == '[')
            {
                builder.Append("[*]");
                skipping = true;
            }
            else if (character == ']')
            {
                skipping = false;
            }
            else if (!skipping)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private void Compare(string path, object? recorded, object? derived, int depth)
    {
        if (Full)
        {
            return;
        }

        if (recorded is null || derived is null)
        {
            if (recorded is not null || derived is not null)
            {
                Add(path, recorded, derived);
            }

            return;
        }

        var type = recorded.GetType();
        if (type != derived.GetType())
        {
            Add(path, type.Name, derived.GetType().Name);
            return;
        }

        if (IsScalar(type) || depth >= MaxDepth)
        {
            if (!Equals(recorded, derived))
            {
                Add(path, recorded, derived);
            }

            return;
        }

        if (recorded is IEnumerable recordedItems && derived is IEnumerable derivedItems)
        {
            var left = recordedItems.Cast<object?>().ToArray();
            var right = derivedItems.Cast<object?>().ToArray();
            if (left.Length != right.Length)
            {
                Add($"{path}.Count", left.Length, right.Length);
            }

            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                Compare($"{path}[{index}]", left[index], right[index], depth + 1);
            }

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(static property => property.GetIndexParameters().Length == 0 && !IsRuntimeOnly(property))
                     .OrderBy(static property => property.MetadataToken))
        {
            object? left, right;
            try
            {
                left = property.GetValue(recorded);
                right = property.GetValue(derived);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            Compare($"{path}.{property.Name}", left, right, depth + 1);
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(static field => field.MetadataToken))
        {
            Compare($"{path}.{field.Name}", field.GetValue(recorded), field.GetValue(derived), depth + 1);
        }
    }

    private static bool IsRuntimeOnly(PropertyInfo property) =>
        property.PropertyType == typeof(ShaderStageResources) ||
        property.DeclaringType == typeof(ComputeInputInfo) && property.Name.StartsWith("DispatchThreads", StringComparison.Ordinal);

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(UInt128);

    private void Add(string path, object? recorded, object? derived) =>
        _differences.Add($"{path} {Format(recorded)} -> {Format(derived)}");

    private static string Format(object? value) => value switch
    {
        null => "none",
        uint number when number > 9 => $"0x{number:X}",
        ulong number when number > 9 => $"0x{number:X}",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
