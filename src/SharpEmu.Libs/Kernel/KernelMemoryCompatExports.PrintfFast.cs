// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private const int PrintfFastFormatCapacity = 512;
    private const int PrintfFastOutputCapacity = 1024;
    private const int PrintfFastStringCapacity = 512;
    private const int PrintfFastMaxWidth = 255;

    private static readonly bool PrintfFastPathTraceActive =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_WIDE_PRINTF_ARGS"), "1", StringComparison.Ordinal);

    // Test hook: forces every printf-family call through the general formatter.
    internal static bool DisablePrintfFastPath;

    private enum FastLength
    {
        None,
        Char,
        Short,
        Long,
    }

    // Formats and writes a snprintf-style result without allocating when the format
    // only uses literal ASCII text plus %s %c %d %i %u %x %X %p %%, flags '-' and
    // '0', a width, a %s precision and the hh/h/l/ll/z length modifiers, and every
    // %s argument is a short ASCII string. Returns false (having written nothing)
    // so the caller can run the general formatter with an untouched argument source.
    private static bool TryFastSnprintf<TArgumentSource>(
        CpuContext ctx,
        ulong destination,
        ulong bufferSize,
        ulong formatAddress,
        TArgumentSource arguments,
        out int result)
        where TArgumentSource : struct, IPrintfArgumentSource
    {
        result = 0;
        if (DisablePrintfFastPath || PrintfFastPathTraceActive || formatAddress == 0)
        {
            return false;
        }

        Span<byte> format = stackalloc byte[PrintfFastFormatCapacity];
        var firstPageRemaining = 4096 - (int)(formatAddress & 4095);
        format = format[..Math.Min(format.Length, firstPageRemaining)];
        if (!TryReadCompat(ctx, formatAddress, format))
        {
            return false;
        }

        var formatLength = format.IndexOf((byte)0);
        if (formatLength < 0)
        {
            return false;
        }

        format = format[..formatLength];
        Span<byte> output = stackalloc byte[PrintfFastOutputCapacity + 1];
        Span<byte> scratch = stackalloc byte[PrintfFastStringCapacity];
        Span<byte> single = stackalloc byte[1];
        var length = 0;

        for (var i = 0; i < format.Length; i++)
        {
            var current = format[i];
            if (current >= 0x80)
            {
                return false;
            }

            if (current != (byte)'%')
            {
                if (length >= PrintfFastOutputCapacity)
                {
                    return false;
                }

                output[length++] = current;
                continue;
            }

            i++;
            var leftAlign = false;
            var zeroPad = false;
            for (; i < format.Length; i++)
            {
                if (format[i] == (byte)'-')
                {
                    leftAlign = true;
                }
                else if (format[i] == (byte)'0')
                {
                    zeroPad = true;
                }
                else
                {
                    break;
                }
            }

            var width = 0;
            for (; i < format.Length && format[i] is >= (byte)'0' and <= (byte)'9'; i++)
            {
                width = (width * 10) + (format[i] - '0');
                if (width > PrintfFastMaxWidth)
                {
                    return false;
                }
            }

            var precision = -1;
            if (i < format.Length && format[i] == (byte)'.')
            {
                i++;
                precision = 0;
                for (; i < format.Length && format[i] is >= (byte)'0' and <= (byte)'9'; i++)
                {
                    precision = (precision * 10) + (format[i] - '0');
                    if (precision > PrintfFastMaxWidth)
                    {
                        return false;
                    }
                }
            }

            var lengthMod = FastLength.None;
            if (i + 1 < format.Length && format[i] == (byte)'h' && format[i + 1] == (byte)'h')
            {
                lengthMod = FastLength.Char;
                i += 2;
            }
            else if (i + 1 < format.Length && format[i] == (byte)'l' && format[i + 1] == (byte)'l')
            {
                lengthMod = FastLength.Long;
                i += 2;
            }
            else if (i < format.Length && format[i] == (byte)'h')
            {
                lengthMod = FastLength.Short;
                i++;
            }
            else if (i < format.Length && (format[i] == (byte)'l' || format[i] == (byte)'z'))
            {
                lengthMod = FastLength.Long;
                i++;
            }

            if (i >= format.Length)
            {
                return false;
            }

            var specifier = (char)format[i];
            if (precision >= 0 && specifier != 's')
            {
                return false;
            }

            if (length + width + 32 > PrintfFastOutputCapacity)
            {
                return false;
            }

            switch (specifier)
            {
                case '%':
                    output[length++] = (byte)'%';
                    break;

                case 'd':
                case 'i':
                    {
                        var raw = arguments.NextGpArg();
                        long value = lengthMod switch
                        {
                            FastLength.Char => unchecked((sbyte)raw),
                            FastLength.Short => unchecked((short)raw),
                            FastLength.Long => unchecked((long)raw),
                            _ => unchecked((int)raw),
                        };

                        value.TryFormat(output[(length + width)..], out var written, default, CultureInfo.InvariantCulture);
                        length = PadFormattedNumber(output, length, written, width, leftAlign, zeroPad, signed: true);
                    }
                    break;

                case 'u':
                case 'x':
                case 'X':
                    {
                        var raw = arguments.NextGpArg();
                        ulong value = lengthMod switch
                        {
                            FastLength.Char => (byte)raw,
                            FastLength.Short => (ushort)raw,
                            FastLength.Long => raw,
                            _ => (uint)raw,
                        };

                        var numberFormat = specifier == 'u' ? "D" : specifier == 'x' ? "x" : "X";
                        value.TryFormat(output[(length + width)..], out var written, numberFormat, CultureInfo.InvariantCulture);
                        length = PadFormattedNumber(output, length, written, width, leftAlign, zeroPad);
                    }
                    break;

                case 'p':
                    {
                        var value = arguments.NextGpArg();
                        if (value == 0)
                        {
                            "(nil)"u8.CopyTo(output[length..]);
                            length += 5;
                        }
                        else
                        {
                            output[length++] = (byte)'0';
                            output[length++] = (byte)'x';
                            value.TryFormat(output[length..], out var written, "X", CultureInfo.InvariantCulture);
                            length += written;
                        }
                    }
                    break;

                case 'c':
                    {
                        if (lengthMod == FastLength.Long)
                        {
                            return false;
                        }

                        var value = (byte)arguments.NextGpArg();
                        if (value >= 0x80)
                        {
                            return false;
                        }

                        single[0] = value;
                        length = AppendPadded(output, length, single, width, leftAlign);
                    }
                    break;

                case 's':
                    {
                        if (lengthMod == FastLength.Long)
                        {
                            return false;
                        }

                        var address = arguments.NextGpArg();
                        if (address == 0)
                        {
                            "(null)"u8.CopyTo(output[length..]);
                            length += 6;
                            break;
                        }

                        var pageRemaining = 4096 - (int)(address & 4095);
                        var chunk = scratch[..Math.Min(scratch.Length, pageRemaining)];
                        if (!TryReadCompat(ctx, address, chunk))
                        {
                            return false;
                        }

                        var stringLength = chunk.IndexOf((byte)0);
                        if (stringLength < 0)
                        {
                            return false;
                        }

                        ReadOnlySpan<byte> text = chunk[..stringLength];
                        if (!Ascii.IsValid(text))
                        {
                            return false;
                        }

                        if (precision >= 0 && text.Length > precision)
                        {
                            text = text[..precision];
                        }

                        if (length + Math.Max(width, text.Length) > PrintfFastOutputCapacity)
                        {
                            return false;
                        }

                        length = AppendPadded(output, length, text, width, leftAlign);
                    }
                    break;

                default:
                    return false;
            }
        }

        result = WriteFastSnprintfOutput(ctx, destination, bufferSize, output, length);
        return true;
    }

    // Numbers are formatted at output[start + width ..]; move them into place and pad.
    // Zero padding goes in front of the whole rendering, sign included, matching the
    // general formatter.
    private static int PadFormattedNumber(Span<byte> output, int start, int written, int width, bool leftAlign, bool zeroPad, bool signed = false)
    {
        var padding = Math.Max(0, width - written);
        var number = output.Slice(start + width, written);
        if (leftAlign)
        {
            number.CopyTo(output[start..]);
            output.Slice(start + written, padding).Fill((byte)' ');
        }
        else
        {
            number.CopyTo(output[(start + padding)..]);
            if (zeroPad && signed && padding > 0 && output[start + padding] == (byte)'-')
            {
                // C99: zero padding goes between the sign and the digits.
                output[start] = (byte)'-';
                output.Slice(start + 1, padding).Fill((byte)'0');
            }
            else
            {
                output.Slice(start, padding).Fill(zeroPad ? (byte)'0' : (byte)' ');
            }
        }

        return start + written + padding;
    }

    private static int AppendPadded(Span<byte> output, int length, ReadOnlySpan<byte> text, int width, bool leftAlign)
    {
        var padding = Math.Max(0, width - text.Length);
        if (!leftAlign)
        {
            output.Slice(length, padding).Fill((byte)' ');
            length += padding;
        }

        text.CopyTo(output[length..]);
        length += text.Length;
        if (leftAlign)
        {
            output.Slice(length, padding).Fill((byte)' ');
            length += padding;
        }

        return length;
    }

    private static int WriteFastSnprintfOutput(
        CpuContext ctx,
        ulong destination,
        ulong bufferSize,
        Span<byte> output,
        int length)
    {
        if (bufferSize != 0 && destination != 0)
        {
            var copyLength = (int)Math.Min((ulong)length, bufferSize - 1);
            output[copyLength] = 0;
            if (!ctx.Memory.TryWrite(destination, output[..(copyLength + 1)]))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)length);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
