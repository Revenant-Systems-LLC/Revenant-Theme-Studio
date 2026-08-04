using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
                                ThumbUrl = file,
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
                if (ImageBoardExtractor.IsKnownBoard(SourcePath))
                {
                    foreach (var remote in await ImageBoardExtractor.ExtractViaApi(SourcePath))
                        images.Add(ToWallpaperImage(remote));
                }
                else
                {
                    var html = await ImageBoardExtractor.GetStringAsBrowser(_httpClient, SourcePath);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    foreach (var url in ExtractImageUrls(doc))
                        images.Add(ToWallpaperImage(new RemoteImage { FullUrl = url, ThumbUrl = url }));
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

        private static WallpaperImage ToWallpaperImage(RemoteImage remote)
        {
            string name;
            try { name = Path.GetFileName(new Uri(remote.FullUrl).AbsolutePath); }
            catch { name = remote.FullUrl; }

            return new WallpaperImage
            {
                Path = remote.FullUrl,
                FallbackPath = remote.FallbackUrl,
                ThumbUrl = remote.ThumbUrl ?? remote.FullUrl,
                Name = name,
                Weight = 1
            };
        }

        // ── Generic gallery extraction ────────────────────────────────────────
        // Pulls candidates from every place galleries actually put images:
        // <img> src + the lazy-load attribute zoo, srcset (largest candidate),
        // <picture><source>, direct <a href> image links, og:/twitter: meta,
        // inline background-image styles, and JSON-LD blocks.

        private List<string> ExtractImageUrls(HtmlDocument doc)
        {
            var urls = new List<string>();
            var baseUri = new Uri(SourcePath);

            void Add(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                var resolved = ResolveUrl(baseUri, raw.Trim());
                if (resolved != null && IsImageUrl(resolved) && !IsLikelyJunk(resolved))
                    urls.Add(resolved);
            }

            void AddSrcset(string? srcset)
            {
                var best = PickLargestFromSrcset(srcset);
                if (best != null) Add(best);
            }

            var imgNodes = doc.DocumentNode.SelectNodes("//img");
            if (imgNodes != null)
            {
                foreach (var img in imgNodes)
                {
                    Add(img.GetAttributeValue("src", string.Empty));
                    Add(img.GetAttributeValue("data-src", string.Empty));
                    Add(img.GetAttributeValue("data-original", string.Empty));
                    Add(img.GetAttributeValue("data-lazy", string.Empty));
                    Add(img.GetAttributeValue("data-lazy-src", string.Empty));
                    AddSrcset(img.GetAttributeValue("srcset", string.Empty));
                    AddSrcset(img.GetAttributeValue("data-srcset", string.Empty));
                }
            }

            var sourceNodes = doc.DocumentNode.SelectNodes("//picture/source[@srcset]");
            if (sourceNodes != null)
            {
                foreach (var source in sourceNodes)
                    AddSrcset(source.GetAttributeValue("srcset", string.Empty));
            }

            var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
            if (linkNodes != null)
            {
                foreach (var link in linkNodes)
                {
                    var href = link.GetAttributeValue("href", string.Empty);
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    var resolved = ResolveUrl(baseUri, href);
                    if (resolved != null && IsDirectImageLink(resolved) && !IsLikelyJunk(resolved))
                        urls.Add(resolved);
                }
            }

            var metaNodes = doc.DocumentNode.SelectNodes(
                "//meta[@property='og:image' or @property='og:image:secure_url' or @name='twitter:image']");
            if (metaNodes != null)
            {
                foreach (var meta in metaNodes)
                    Add(meta.GetAttributeValue("content", string.Empty));
            }

            var styledNodes = doc.DocumentNode.SelectNodes("//*[contains(@style, 'background')]");
            if (styledNodes != null)
            {
                foreach (var node in styledNodes)
                {
                    var style = node.GetAttributeValue("style", string.Empty);
                    foreach (Match m in Regex.Matches(style, @"url\(\s*['""]?([^'"")\s]+)['""]?\s*\)"))
                        Add(m.Groups[1].Value);
                }
            }

            var jsonLdNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (jsonLdNodes != null)
            {
                foreach (var script in jsonLdNodes)
                {
                    var text = script.InnerText.Replace("\\/", "/");
                    foreach (Match m in Regex.Matches(text,
                        @"https?://[^\s""'\\]+\.(?:jpg|jpeg|png|webp|bmp)(?:\?[^\s""'\\]*)?",
                        RegexOptions.IgnoreCase))
                        Add(m.Value);
                }
            }

            return urls.Distinct().ToList();
        }

        /// <summary>Parse "url1 640w, url2 1280w" (or 1x/2x descriptors) and return the
        /// largest candidate — galleries put the full-quality rendition last/biggest.</summary>
        private static string? PickLargestFromSrcset(string? srcset)
        {
            if (string.IsNullOrWhiteSpace(srcset)) return null;

            string? bestUrl = null;
            double bestScore = -1;

            foreach (var part in srcset.Split(','))
            {
                var pieces = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length == 0) continue;

                double score = 0;
                if (pieces.Length > 1)
                {
                    var desc = pieces[^1].ToLowerInvariant();
                    if (desc.EndsWith("w") && double.TryParse(desc[..^1], out var w)) score = w;
                    else if (desc.EndsWith("x") && double.TryParse(desc[..^1], out var x)) score = x;
                }

                if (score >= bestScore)
                {
                    bestScore = score;
                    bestUrl = pieces[0];
                }
            }
            return bestUrl;
        }

        private static string? ResolveUrl(Uri baseUri, string raw)
        {
            try
            {
                if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
                    return absolute.Scheme is "http" or "https" ? absolute.ToString() : null;
                if (Uri.TryCreate(baseUri, raw, out var resolved))
                    return resolved.Scheme is "http" or "https" ? resolved.ToString() : null;
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

        /// <summary>Filter out UI chrome that technically is an image but never a wallpaper.</summary>
        private static bool IsLikelyJunk(string url)
        {
            var lower = url.ToLowerInvariant();
            return lower.Contains("favicon") || lower.Contains("sprite") ||
                   lower.Contains("logo") || lower.Contains("/icons/") ||
                   lower.Contains("icon-") || lower.Contains("emoji") ||
                   lower.Contains("avatar") || lower.Contains("badge") ||
                   lower.Contains("spacer") || lower.Contains("1x1") ||
                   lower.Contains("blank.");
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

        /// <summary>Secondary URL tried when Path fails to download (e.g. a synthesized
        /// Pinterest /originals/ link whose real extension differs).</summary>
        public string? FallbackPath { get; set; }

        /// <summary>Small rendition for the results grid; falls back to Path when the
        /// source has no separate thumbnails.</summary>
        public string ThumbUrl { get; set; } = string.Empty;

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
