using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Revenant_Theme_Studio.Features.Wallpaper.Services;

namespace Revenant_Theme_Studio.Features.Wallpaper
{
    public abstract class WallpaperSource
    {
        public string Name { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public SourceType Type { get; set; }
        public List<WallpaperImage> Images { get; set; } = new List<WallpaperImage>();

        public abstract Task<List<WallpaperImage>> GetAvailableImages();
    }

    public class LocalFolderSource : WallpaperSource
    {
        public override Task<List<WallpaperImage>> GetAvailableImages()
        {
            var images = new List<WallpaperImage>();

            if (Directory.Exists(SourcePath))
            {
                var imageExtensions = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp" };
                foreach (var extension in imageExtensions)
                {
                    try
                    {
                        var files = Directory.GetFiles(SourcePath, extension, SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            images.Add(new WallpaperImage
                            {
                                Path = file,
                                Name = Path.GetFileName(file),
                                Weight = 1
                            });
                        }
                    }
                    catch { }
                }
            }

            return Task.FromResult(images);
        }
    }

    public class WebImageSource : WallpaperSource
    {
        private static readonly HttpClient _httpClient;
        public ExtractionRule Rule { get; set; } = new ExtractionRule();

        static WebImageSource()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RevenantThemeStudio/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public override async Task<List<WallpaperImage>> GetAvailableImages()
        {
            var images = new List<WallpaperImage>();

            try
            {
                List<string> imageUrls;

                if (ImageBoardExtractor.IsKnownBoard(SourcePath))
                {
                    imageUrls = await ImageBoardExtractor.ExtractViaApi(SourcePath);
                }
                else
                {
                    var html = await _httpClient.GetStringAsync(SourcePath);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);
                    imageUrls = ExtractImageUrls(doc);
                }

                foreach (var url in imageUrls)
                {
                    images.Add(new WallpaperImage
                    {
                        Path = url,
                        Name = Path.GetFileName(new Uri(url).AbsolutePath),
                        Weight = 1
                    });
                }
            }
            catch (Exception) { }

            return images;
        }

        public async Task<WallpaperImage?> GetSelectedImage()
        {
            var images = await GetAvailableImages();
            if (images.Count == 0) return null;
            return Rule.SelectFrom(images);
        }

        private List<string> ExtractImageUrls(HtmlDocument doc)
        {
            var urls = new List<string>();
            var baseUri = new Uri(SourcePath);

            var imgNodes = doc.DocumentNode.SelectNodes("//img[@src]");
            if (imgNodes != null)
            {
                foreach (var img in imgNodes)
                {
                    var src = img.GetAttributeValue("src", string.Empty);
                    if (string.IsNullOrWhiteSpace(src)) continue;

                    var resolved = ResolveUrl(baseUri, src);
                    if (resolved != null && IsImageUrl(resolved))
                        urls.Add(resolved);

                    var dataSrc = img.GetAttributeValue("data-src", string.Empty);
                    if (!string.IsNullOrWhiteSpace(dataSrc))
                    {
                        var resolvedData = ResolveUrl(baseUri, dataSrc);
                        if (resolvedData != null && IsImageUrl(resolvedData))
                            urls.Add(resolvedData);
                    }
                }
            }

            var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
            if (linkNodes != null)
            {
                foreach (var link in linkNodes)
                {
                    var href = link.GetAttributeValue("href", string.Empty);
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    var resolved = ResolveUrl(baseUri, href);
                    if (resolved != null && IsDirectImageLink(resolved))
                        urls.Add(resolved);
                }
            }

            return urls.Distinct().ToList();
        }

        private static string? ResolveUrl(Uri baseUri, string raw)
        {
            try
            {
                if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
                    return absolute.ToString();
                if (Uri.TryCreate(baseUri, raw, out var resolved))
                    return resolved.ToString();
            }
            catch { }
            return null;
        }

        private static bool IsImageUrl(string url)
        {
            var lower = url.ToLowerInvariant();
            return lower.Contains(".jpg") || lower.Contains(".jpeg") ||
                   lower.Contains(".png") || lower.Contains(".webp") ||
                   lower.Contains(".bmp") || lower.Contains(".gif");
        }

        private static bool IsDirectImageLink(string url)
        {
            var lower = url.ToLowerInvariant();
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
            return extensions.Any(ext => lower.EndsWith(ext) ||
                   lower.Contains(ext + "?"));
        }
    }

    public class ExtractionRule
    {
        public SelectionMode Mode { get; set; } = SelectionMode.First;
        public int Position { get; set; } = 0;
        public int MinWidth { get; set; } = 0;
        public int MinHeight { get; set; } = 0;

        public WallpaperImage? SelectFrom(List<WallpaperImage> images)
        {
            if (images.Count == 0) return null;

            return Mode switch
            {
                SelectionMode.First => images[0],
                SelectionMode.Last => images[^1],
                SelectionMode.Random => images[Random.Shared.Next(images.Count)],
                SelectionMode.Position => Position < images.Count ? images[Position] : images[0],
                _ => images[0]
            };
        }
    }

    public enum SelectionMode
    {
        First,
        Last,
        Random,
        Position
    }

    public class WallpaperImage
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime LastUsed { get; set; }
        public int Weight { get; set; } = 1;
    }

    public enum SourceType
    {
        LocalFolder,
        WebSource,
        ImageBoard,
        Gallery
    }
}
