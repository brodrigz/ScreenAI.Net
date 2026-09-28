using System;

namespace ScreenAI.Imaging
{
    internal sealed class PreparedImage
    {
        // A bound on each admitted image, separate from the native downsampling threshold.
        internal const int MaximumBytes = 128 * 1024 * 1024;
        internal byte[] Pixels { get; }
        internal int Width { get; }
        internal int Height { get; }
        private PreparedImage(byte[] pixels, int width, int height) { Pixels = pixels; Width = width; Height = height; }

        internal static int Validate(int length, int width, int height, int stride)
        {
            if (width <= 0 || width > 32768) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || height > 32768) throw new ArgumentOutOfRangeException(nameof(height));
            if (stride == 0) stride = checked(width * 4);
            if (stride < width * 4 || stride % 4 != 0) throw new ArgumentOutOfRangeException(nameof(stride));
            var required = (long)stride * height;
            if (required > MaximumBytes) throw new ArgumentException("An input image cannot exceed 128 MiB.");
            if (required > length) throw new ArgumentException("Pixel buffer is shorter than stride * height.");
            return stride;
        }

        internal static PreparedImage Create(ReadOnlySpan<byte> source, int width, int height, int stride,
            int maximum, OversizedImageBehavior behavior)
        {
            stride = Validate(source.Length, width, height, stride);
            if (behavior != OversizedImageBehavior.Reject && behavior != OversizedImageBehavior.ResizeToFit)
                throw new ArgumentOutOfRangeException(nameof(behavior));
            if (width <= maximum && height <= maximum)
            {
                var packed = new byte[checked(width * height * 4)];
                for (int y = 0; y < height; y++) source.Slice(y * stride, width * 4).CopyTo(packed.AsSpan(y * width * 4));
                return new PreparedImage(packed, width, height);
            }
            if (behavior == OversizedImageBehavior.Reject)
                throw new ArgumentOutOfRangeException(nameof(width), "Image exceeds the native downsampling threshold of " + maximum + ". Select ResizeToFit explicitly.");
            double scale = (double)maximum / Math.Max(width, height);
            int targetWidth = Math.Max(1, (int)Math.Floor(width * scale));
            int targetHeight = Math.Max(1, (int)Math.Floor(height * scale));
            var resized = new byte[checked(targetWidth * targetHeight * 4)];
            // Bilinear interpolation in premultiplied BGRA, preserving aspect ratio to integer-pixel precision.
            for (int y = 0; y < targetHeight; y++)
            {
                double sy = Math.Max(0, (y + 0.5) * height / targetHeight - 0.5);
                int y0 = (int)sy, y1 = Math.Min(height - 1, y0 + 1);
                double fy = sy - y0;
                for (int x = 0; x < targetWidth; x++)
                {
                    double sx = Math.Max(0, (x + 0.5) * width / targetWidth - 0.5);
                    int x0 = (int)sx, x1 = Math.Min(width - 1, x0 + 1);
                    double fx = sx - x0;
                    for (int c = 0; c < 4; c++)
                    {
                        double top = source[y0 * stride + x0 * 4 + c] * (1 - fx) + source[y0 * stride + x1 * 4 + c] * fx;
                        double bottom = source[y1 * stride + x0 * 4 + c] * (1 - fx) + source[y1 * stride + x1 * 4 + c] * fx;
                        resized[(y * targetWidth + x) * 4 + c] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                    }
                }
            }
            return new PreparedImage(resized, targetWidth, targetHeight);
        }
    }
}
