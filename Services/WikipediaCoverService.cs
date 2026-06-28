using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace GamesLocalShare.Services;

/// <summary>
/// Keyless cover lookup via Wikipedia's public MediaWiki API. Covers famous titles the
/// storefronts don't carry (console ports, EA/Konami catalogs, retro games) by using the
/// article's lead image — which for a game article is its box/cover art. No API key or
/// hosting is required. Results are cached on disk by title. Gated by the shared
/// high-confidence title match so a wrong article's image is never used.
/// </summary>
public class WikipediaCoverService
{
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Wikimedia asks API clients to send a descriptive User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GamesLocalShare/1.0 (LAN game library cover lookup)");
        return c;
    }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamesLocalShare", "WikiCovers");

    /// <summary>
    /// Returns the lead-image URL of the best-matching Wikipedia game article for
    /// <paramref name="title"/>, or null when there's no confident match.
    /// </summary>
    public async Task<string?> ResolveCoverAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var cachePath = Path.Combine(CacheDir, SafeFileName(title) + ".url");
            if (File.Exists(cachePath))
            {
                var cached = (await File.ReadAllTextAsync(cachePath)).Trim();
                return string.IsNullOrEmpty(cached) ? null : cached;
            }

            // Search (biased toward games) and pull the lead image of each candidate page in
            // one round-trip via a generator.
            var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&prop=pageimages"
                    + "&piprop=original&pilicense=any&generator=search&gsrlimit=5&gsrsearch="
                    + Uri.EscapeDataString(title + " video game");

            string? cover = null;
            using (var resp = await _http.GetAsync(url))
            {
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                    if (doc.RootElement.TryGetProperty("query", out var q) && q.TryGetProperty("pages", out var pages))
                    {
                        // "pages" is keyed by page id; order by the search "index" and take the
                        // first confident title match that has a lead image.
                        var ordered = pages.EnumerateObject()
                            .Select(p => p.Value)
                            .OrderBy(v => v.TryGetProperty("index", out var i) ? i.GetInt32() : int.MaxValue);

                        foreach (var page in ordered)
                        {
                            if (!page.TryGetProperty("original", out var orig) || !orig.TryGetProperty("source", out var src))
                                continue;
                            var pageTitle = page.TryGetProperty("title", out var pt) ? pt.GetString() ?? "" : "";
                            // Drop disambiguators like "(video game)" / "(2018 video game)".
                            var cleaned = System.Text.RegularExpressions.Regex.Replace(pageTitle, @"\s*\([^)]*\)\s*$", "");
                            if (TitleCoverArtService.TitlesMatch(cleaned, title))
                            {
                                cover = src.GetString();
                                break;
                            }
                        }
                    }
                }
            }

            try { await File.WriteAllTextAsync(cachePath, cover ?? string.Empty); } catch { }
            return cover;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
