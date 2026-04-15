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
        /// Returns true if ExtractIconEx can pull a handle for the given index.
        /// Does not create a BitmapSource, so it is safe to call from a background thread.
        /// </summary>
        public bool CanExtractIcon(string resourcePath, int index)
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            uint extracted = ExtractIconEx(resourcePath, index, large, small, 1);
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            return extracted > 0;
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
