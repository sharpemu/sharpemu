#nullable disable
using System;

namespace LibAtrac9
{
    internal static class BitAllocation
    {
        public static void CreateGradient(Block block)
        {
            int valueCount = block.GradientEndValue - block.GradientStartValue;
            int unitCount = block.GradientEndUnit - block.GradientStartUnit;

            for (int i = 0; i < block.GradientEndUnit; i++)
            {
                block.Gradient[i] = block.GradientStartValue;
            }

            for (int i = block.GradientEndUnit; i <= block.QuantizationUnitCount; i++)
            {
                block.Gradient[i] = block.GradientEndValue;
            }
            if (unitCount <= 0) return;
            if (valueCount == 0) return;

            byte[] curve = Tables.GradientCurves[unitCount - 1];
            if (valueCount <= 0)
            {
                double scale = (-valueCount - 1) / 31.0;
                int baseVal = block.GradientStartValue - 1;
                for (int i = block.GradientStartUnit; i < block.GradientEndUnit; i++)
                {
                    block.Gradient[i] = baseVal - (int)(curve[i - block.GradientStartUnit] * scale);
                }
            }
            else
            {
                double scale = (valueCount - 1) / 31.0;
                int baseVal = block.GradientStartValue + 1;
                for (int i = block.GradientStartUnit; i < block.GradientEndUnit; i++)
                {
                    block.Gradient[i] = baseVal + (int)(curve[i - block.GradientStartUnit] * scale);
                }
            }
        }

        public static void CalculateMask(Channel channel)
        {
            Array.Clear(channel.PrecisionMask, 0, channel.PrecisionMask.Length);
            for (int i = 1; i < channel.Block.QuantizationUnitCount; i++)
            {
                int delta = channel.ScaleFactors[i] - channel.ScaleFactors[i - 1];
                if (delta > 1)
                {
                    channel.PrecisionMask[i] += Math.Min(delta - 1, 5);
                }
                else if (delta < -1)
                {
                    channel.PrecisionMask[i - 1] += Math.Min(delta * -1 - 1, 5);
                }
            }
        }

        public static void CalculatePrecisions(Channel channel)
        {
            Block block = channel.Block;

            if (block.GradientMode != 0)
            {
                for (int i = 0; i < block.QuantizationUnitCount; i++)
                {
                    channel.Precisions[i] = channel.ScaleFactors[i] + channel.PrecisionMask[i] - block.Gradient[i];
                    if (channel.Precisions[i] > 0)
                    {
                        switch (block.GradientMode)
                        {
                            case 1:
                                channel.Precisions[i] /= 2;
                                break;
                            case 2:
                                channel.Precisions[i] = 3 * channel.Precisions[i] / 8;
                                break;
                            case 3:
                                channel.Precisions[i] /= 4;
                                break;
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < block.QuantizationUnitCount; i++)
                {
                    channel.Precisions[i] = channel.ScaleFactors[i] - block.Gradient[i];
                }
            }

            for (int i = 0; i < block.QuantizationUnitCount; i++)
            {
                if (channel.Precisions[i] < 1)
                {
                    channel.Precisions[i] = 1;
                }
            }

            for (int i = 0; i < block.GradientBoundary; i++)
            {
                channel.Precisions[i]++;
            }

            for (int i = 0; i < block.QuantizationUnitCount; i++)
            {
                channel.PrecisionsFine[i] = 0;
                if (channel.Precisions[i] > 15)
                {
                    channel.PrecisionsFine[i] = channel.Precisions[i] - 15;
                    channel.Precisions[i] = 15;
                }
            }
        }

        public static byte[][] GenerateGradientCurves()
        {
            byte[] main =
            {
                01, 01, 01, 01, 02, 02, 02, 02, 03, 03, 03, 04, 04, 05, 05, 06, 07, 08, 09, 10, 11, 12, 13, 15,
                16, 18, 19, 20, 21, 22, 23, 24, 25, 26, 26, 27, 27, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30
            };
            var curves = new byte[main.Length][];

            for (int length = 1; length <= main.Length; length++)
            {
                curves[length - 1] = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    curves[length - 1][i] = main[i * main.Length / length];
                }
            }
            return curves;
        }
    }
}
