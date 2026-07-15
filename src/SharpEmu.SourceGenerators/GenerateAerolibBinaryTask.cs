// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Microsoft.Build.Framework;

namespace SharpEmu.SourceGenerators;

/// <summary>
/// Builds aerolib.bin (the runtime NID -> name catalog) from scripts/ps5_names.txt at
/// build time, replacing scripts/generate_aerolib_binary.py and the committed binary.
/// Format matches the python script exactly: uint32 entry count, then per entry a
/// byte-length-prefixed NID and a ushort-length-prefixed name, little-endian UTF-8.
///
/// Implements ITask directly (Framework-only reference) and is an MSBuild task, not an
/// analyzer — the file-IO ban that RS1035 enforces for compiler-loaded code does not
/// apply to build tasks, hence the targeted suppressions.
/// </summary>
public sealed class GenerateAerolibBinaryTask : ITask
{
    public IBuildEngine? BuildEngine { get; set; }

    public ITaskHost? HostObject { get; set; }

    [Required]
    public string NamesFile { get; set; } = string.Empty;

    [Required]
    public string OutputFile { get; set; } = string.Empty;

#pragma warning disable RS1035 // File IO is the entire point of this build task.
    public bool Execute()
    {
        try
        {
            var names = new List<string>();
            foreach (var line in File.ReadAllLines(NamesFile))
            {
                var name = line.Trim();
                if (name.Length != 0)
                {
                    names.Add(name);
                }
            }

            var outputDirectory = Path.GetDirectoryName(OutputFile);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            using (var stream = File.Create(OutputFile))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((uint)names.Count);
                foreach (var name in names)
                {
                    var nidBytes = Encoding.UTF8.GetBytes(Ps5Nid.Compute(name, sha1));
                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    if (nameBytes.Length > ushort.MaxValue)
                    {
                        // A silent (ushort) truncation would corrupt the catalog.
                        throw new InvalidDataException(
                            $"Symbol name exceeds the format's ushort length prefix ({nameBytes.Length} bytes): '{name.Substring(0, 64)}...'");
                    }

                    writer.Write((byte)nidBytes.Length);
                    writer.Write(nidBytes);
                    writer.Write((ushort)nameBytes.Length);
                    writer.Write(nameBytes);
                }
            }

            BuildEngine?.LogMessageEvent(new BuildMessageEventArgs(
                $"aerolib: {names.Count} symbols -> {OutputFile}",
                helpKeyword: null,
                senderName: nameof(GenerateAerolibBinaryTask),
                MessageImportance.Normal));
            return true;
        }
        catch (Exception exception)
        {
            BuildEngine?.LogErrorEvent(new BuildErrorEventArgs(
                subcategory: null,
                code: "SHEMAERO",
                file: NamesFile,
                lineNumber: 0,
                columnNumber: 0,
                endLineNumber: 0,
                endColumnNumber: 0,
                message: exception.ToString(),
                helpKeyword: null,
                senderName: nameof(GenerateAerolibBinaryTask)));
            return false;
        }
    }
#pragma warning restore RS1035
}
