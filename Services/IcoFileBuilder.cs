using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Revenant_Theme_Studio.Services
{
    public record NormalizeResult(byte[] IcoBytes, string? Warning);

    public static class IcoFileBuilder
    {
        private static readonly int[] StandardSizes = { 256, 128, 96, 64, 48, 40, 32, 24, 20, 16 };

        // Build a valid .ico byte array from raw (width, height, bitCount, frameData) tuples.
        // width/height follow the ICO convention: 0 encodes 256.
        // frameData must already be either a PNG blob or a DIB (BMP without file header).
        public static byte[] WriteIcoBytes(
            IReadOnlyList<(byte width, byte height, ushort bitCount, byte[] frameData)> frames)
        {
            int count      = frames.Count;
            int dataOffset = 6 + count * 16; // ICONDIR(6) + N × ICONDIRENTRY(16)

            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            // ICONDIR
            w.Write((ushort)0);
            w.Write((ushort)1);      // type = 1 (icon)
            w.Write((ushort)count);

            // ICONDIRENTRY array
            int offset = dataOffset;
            foreach (var (width, height, bitCount, frameData) in frames)
            {
                w.Write(width);
                w.Write(height);
                w.Write((byte)0);            // bColorCount
                w.Write((byte)0);            // bReserved
                w.Write((ushort)1);          // wPlanes
                w.Write(bitCount);
                w.Write((uint)frameData.Length);
                w.Write((uint)offset);
                offset += frameData.Length;
            }

            foreach (var (_, _, _, frameData) in frames)
                w.Write(frameData);

            return ms.ToArray();
        }

        // Validate and normalise a .ico or .png source into a full-size-set .ico.
        // Throws InvalidDataException for hard errors (not square, no 256 frame, etc.).
        // Returns a Warning string for soft issues (oversized PNG downscaled).
        public static NormalizeResult ValidateAndNormalize(string sourcePath)
        {
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            return ext switch
            {
                ".png" => NormalizeFromPng(sourcePath),
                ".ico" => NormalizeFromIco(sourcePath),
                _      => throw new InvalidDataException(
                              $"Unsupported file type '{ext}'. Use .ico or .png.")
            };
        }

        private static NormalizeResult NormalizeFromPng(string path)
        {
            BitmapSource src;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource   = new Uri(path, UriKind.Absolute);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                src = img;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Cannot load PNG: {ex.Message}", ex);
            }

            if (src.PixelWidth != src.PixelHeight)
                throw new InvalidDataException(
                    $"PNG must be square — got {src.PixelWidth}×{src.PixelHeight}.");

            if (src.PixelWidth < 256)
                throw new InvalidDataException(
                    $"PNG must be at least 256×256 — got {src.PixelWidth}×{src.PixelHeight}.");

            string? warning  = null;
            BitmapSource s256 = src;

            if (src.PixelWidth > 256)
            {
                warning = $"PNG is {src.PixelWidth}×{src.PixelHeight} — downscaled to 256×256.";
                s256    = ScaleTo(src, 256);
            }

            return new NormalizeResult(BuildNormalizedIco(s256), warning);
        }

        private static NormalizeResult NormalizeFromIco(string path)
        {
            IconBitmapDecoder decoder;
            try
            {
                using var fs = File.OpenRead(path);
                decoder = new IconBitmapDecoder(fs,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Cannot load ICO: {ex.Message}", ex);
            }

            BitmapFrame? frame256 = null;
            foreach (var frame in decoder.Frames)
            {
                if (frame.PixelWidth == 256)
                {
                    frame256 = frame;
                    break;
                }
            }

            if (frame256 == null)
                throw new InvalidDataException(
                    "ICO must contain a 256×256 frame — this file does not have one.");

            return new NormalizeResult(BuildNormalizedIco(frame256), null);
        }

        private static byte[] BuildNormalizedIco(BitmapSource src256)
        {
            var frames = new List<(byte, byte, ushort, byte[])>(StandardSizes.Length);

            foreach (int size in StandardSizes)
            {
                BitmapSource scaled = size == 256 ? src256 : ScaleTo(src256, size);

                if (size == 256)
                    frames.Add((0, 0, 32, ToPngBytes(scaled)));
                else
                    frames.Add(((byte)size, (byte)size, 32, ToBmpDib(scaled, size)));
            }

            return WriteIcoBytes(frames);
        }

        // High-quality off-screen downscale using WPF's rendering pipeline.
        private static BitmapSource ScaleTo(BitmapSource source, int size)
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var ctx = visual.RenderOpen())
                ctx.DrawImage(source, new Rect(0, 0, size, size));

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private static byte[] ToPngBytes(BitmapSource source)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        // Encode a BitmapSource as a 32bpp BGRA DIB (BMP without file header),
        // stored bottom-up with a zeroed AND mask, as required by the ICO format.
        private static byte[] ToBmpDib(BitmapSource source, int size)
        {
            var conv = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int stride = size * 4;
            byte[] pixels = new byte[size * stride];
            conv.CopyPixels(pixels, stride, 0);

            // Flip to bottom-up row order
            byte[] flipped = new byte[pixels.Length];
            for (int row = 0; row < size; row++)
                Buffer.BlockCopy(pixels, (size - 1 - row) * stride,
                                 flipped, row * stride, stride);

            // AND mask — all zeros (alpha channel carries transparency)
            int andStride = ((size + 31) / 32) * 4;
            byte[] andMask = new byte[size * andStride];

            using var ms = new MemoryStream();
            using var w  = new BinaryWriter(ms);

            // BITMAPINFOHEADER
            w.Write(40);
            w.Write(size);
            w.Write(size * 2);     // height ×2: XOR mask + AND mask
            w.Write((ushort)1);    // planes
            w.Write((ushort)32);   // bit count
            w.Write(0);            // compression BI_RGB
            w.Write(flipped.Length + andMask.Length);
            w.Write(0);            // xPelsPerMeter
            w.Write(0);            // yPelsPerMeter
            w.Write(0);            // clrUsed
            w.Write(0);            // clrImportant
            w.Write(flipped);
            w.Write(andMask);

            return ms.ToArray();
        }
    }
}
