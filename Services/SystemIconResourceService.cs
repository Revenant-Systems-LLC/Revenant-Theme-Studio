using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Services
{
    public class SystemIconResourceService
    {
        private const int MaxFallbackScanCount = 1500;
        private const int MaxConsecutiveMisses = 64;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(
            string lpszFile,
            int nIconIndex,
            IntPtr[]? phiconLarge,
            IntPtr[]? phiconSmall,
            uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public IReadOnlyList<IconChoice> LoadIcons(string resourcePath)
        {
            if (string.IsNullOrWhiteSpace(resourcePath))
                throw new ArgumentException("Resource file path is required.", nameof(resourcePath));

            if (!File.Exists(resourcePath))
                throw new FileNotFoundException("Resource file was not found.", resourcePath);

            var icons = new List<IconChoice>();
            int iconCount = GetIconCount(resourcePath);

            if (iconCount > 0)
            {
                for (int index = 0; index < iconCount; index++)
                {
                    if (TryLoadIcon(resourcePath, index, out var iconChoice))
                        icons.Add(iconChoice);
                }

                return icons;
            }

            int misses = 0;
            for (int index = 0; index < MaxFallbackScanCount; index++)
            {
                if (TryLoadIcon(resourcePath, index, out var iconChoice))
                {
                    icons.Add(iconChoice);
                    misses = 0;
                }
                else
                {
                    misses++;
                    if (misses >= MaxConsecutiveMisses)
                        break;
                }
            }

            return icons;
        }

        private static int GetIconCount(string resourcePath)
        {
            uint count = ExtractIconEx(resourcePath, -1, null, null, 0);
            return checked((int)count);
        }

        /// <summary>
        /// Extracts a single icon from a PE/DLL resource and writes it as a valid ICO file
        /// to <paramref name="destinationPath"/>.  The ICO container uses PNG image data
        /// (Vista+ format), so Windows 7 and later will load it correctly.
        /// Must be called on the WPF UI thread because HICON→BitmapSource requires a Dispatcher.
        /// </summary>
        public bool TryExtractIconToFile(string resourcePath, int index, string destinationPath)
        {
            if (!File.Exists(resourcePath))
                return false;

            var large = new IntPtr[1];
            var small = new IntPtr[1];
            uint extracted = ExtractIconEx(resourcePath, index, large, small, 1);
            if (extracted == 0)
                return false;

            IntPtr chosen = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (chosen == IntPtr.Zero)
                return false;

            try
            {
                var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                    chosen, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze();

                // Encode the BitmapSource to raw PNG bytes.
                using var pngStream = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(pngStream);
                byte[] pngBytes = pngStream.ToArray();

                int w = bitmapSource.PixelWidth;
                int h = bitmapSource.PixelHeight;

                // Write a minimal ICO file that wraps the PNG image.
                // ICO+PNG (Vista+ format): ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes) + PNG data.
                using var fs = File.Create(destinationPath);
                using var bw = new BinaryWriter(fs);

                // ICONDIR
                bw.Write((short)0);     // idReserved — must be 0
                bw.Write((short)1);     // idType — 1 = ICO
                bw.Write((short)1);     // idCount — one image

                // ICONDIRENTRY
                bw.Write((byte)(w >= 256 ? 0 : w));    // bWidth  (0 means 256)
                bw.Write((byte)(h >= 256 ? 0 : h));    // bHeight (0 means 256)
                bw.Write((byte)0);      // bColorCount — 0 for 32-bpp/truecolor
                bw.Write((byte)0);      // bReserved
                bw.Write((short)1);     // wPlanes
                bw.Write((short)32);    // wBitCount
                bw.Write(pngBytes.Length);  // dwBytesInRes
                bw.Write(22);               // dwImageOffset = 6 (ICONDIR) + 16 (ICONDIRENTRY)

                // Image data — raw PNG
                bw.Write(pngBytes);

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }

        private static bool TryLoadIcon(string resourcePath, int index, out IconChoice iconChoice)
        {
            iconChoice = null!;

            var large = new IntPtr[1];
            var small = new IntPtr[1];

            uint extracted = ExtractIconEx(resourcePath, index, large, small, 1);
            if (extracted == 0)
                return false;

            IntPtr chosen = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (chosen == IntPtr.Zero)
                return false;

            try
            {
                BitmapSource preview = Imaging.CreateBitmapSourceFromHIcon(
                    chosen,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(64, 64));

                preview.Freeze();

                iconChoice = new IconChoice
                {
                    ResourcePath = resourcePath,
                    ResourceIndex = index,
                    DisplayName = $"#{index}",
                    PreviewImage = preview
                };

                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
            finally
            {
                if (large[0] != IntPtr.Zero)
                    DestroyIcon(large[0]);

                if (small[0] != IntPtr.Zero)
                    DestroyIcon(small[0]);
            }
        }
    }
}
