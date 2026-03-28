using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Services;

public class LocalFolderSourceProvider : ISourceProvider
{
    public SourceType SupportedType => SourceType.Local;

    public IReadOnlyList<string> GetCandidates(WallpaperSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Location))
            return new List<string>();

        if (!Directory.Exists(source.Location))
            return new List<string>();

        var files = Directory.GetFiles(source.Location, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f =>
                f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"[Provider] {source.Location} → {files.Count} images found");

        return files;
    }
}