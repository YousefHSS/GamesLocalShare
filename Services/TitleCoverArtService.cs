using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using GamesLocalShare.Models;

namespace GamesLocalShare.Services;

/// <summary>
/// Fetches cover art for Epic Games titles. Primary source is Epic's public
/// storefront GraphQL endpoint (anonymous). Results are cached on disk so we
/// only hit the network once per title.
/// </summary>
public class TitleCoverArtService
{
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 GamesLocalShare/1.0");
        c.DefaultRequestHeaders.Add("Origin", "https://store.epicgames.com");
        c.DefaultRequestHeaders.Add("Referer", "https://store.epicgames.com/");
        return c;
    }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamesLocalShare", "TitleCovers");

    // Epic's own storefront GraphQL is behind Cloudflare and rejects anonymous
    // requests (HTTP 403). egdata.app mirrors the public Epic catalog and is
    // queryable without auth, returning the same keyImages structure.
    private const string EgDataSearchEndpoint = "https://api.egdata.app/multisearch/offers";

    public async Task LoadAsync(GameInfo game)
    {
        if (!string.IsNullOrEmpty(game.CoverUrl)) return;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var urlCachePath = Path.Combine(CacheDir, SafeFileName(game.AppId) + ".url");

            string? url = null;
            if (File.Exists(urlCachePath))
            {
                try { url = (await File.ReadAllTextAsync(urlCachePath)).Trim(); } catch { }
            }

            if (string.IsNullOrEmpty(url))
            {
                // Clean messy install/folder names (camelCase, dots, version/site tags)
                // into a searchable title. For already-clean store titles this is a no-op.
                var searchTitle = CleanSearchTitle(game.Name);
                url = await ResolveImageUrlAsync(NormalizeTitle(searchTitle));
                if (string.IsNullOrEmpty(url))
                {
                    // Try with trailing tokens trimmed (e.g. "Rocket League" instead of "Rocket League: Ultimate Edition")
                    var firstSegment = searchTitle.Split(new[] { ':', '-', '–', '—' }, 2)[0].Trim();
                    if (!string.IsNullOrEmpty(firstSegment) && !string.Equals(firstSegment, searchTitle, StringComparison.OrdinalIgnoreCase))
                        url = await ResolveImageUrlAsync(NormalizeTitle(firstSegment));
                }
                if (!string.IsNullOrEmpty(url))
                {
                    try { await File.WriteAllTextAsync(urlCachePath, url); } catch { }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[EpicCover] No cover resolved for '{game.Name}'");
                }
            }

            if (string.IsNullOrEmpty(url)) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                game.CoverUrl = url;
            });
        }
        catch
        {
            // best-effort; covers are optional
        }
    }

    private static string NormalizeTitle(string title)
    {
        // Strip trademark / registered symbols and collapse whitespace.
        var cleaned = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (ch == '®' || ch == '™' || ch == '©') continue;
            cleaned.Append(ch);
        }
        return System.Text.RegularExpressions.Regex.Replace(cleaned.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Derives a clean store-search query from a possibly-messy install/folder name —
    /// the kind external (non-store) games have. Strips bracketed site tags and trailing
    /// version/build noise (e.g. "[Game3rb.com]", "v1.2.122"), turns dot/underscore
    /// separators into spaces, and splits camelCase joins ("AWayOut" -> "A Way Out").
    /// For already-clean store titles it is effectively a no-op. The high-confidence
    /// <see cref="TitlesMatch"/> guard still vets every result, so over-cleaning can
    /// only cost a cover, never produce a wrong one.
    /// </summary>
    internal static string CleanSearchTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        var rx = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        var s = raw;
        // [bracketed] (parenthesized) {tags}: site names, "(2)" dup suffixes, region codes.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\[\(\{][^\]\)\}]*[\]\)\}]", " ");
        // Trailing version / build noise, while the dots are still intact.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bv?\d+(\.\d+)+[a-z0-9\-]*", " ", rx);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(build|update|patch)\s*\d+\b", " ", rx);
        // Folder-style separators used in place of spaces.
        s = s.Replace('.', ' ').Replace('_', ' ');
        // Split squashed camelCase / letter→capital joins into words.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? raw : s;
    }

    /// <summary>
    /// Lowercases and reduces a title to space-separated alphanumeric tokens so two
    /// titles can be compared without punctuation / edition noise getting in the way.
    /// </summary>
    private static string NormalizeForMatch(string s)
    {
        var lowered = NormalizeTitle(s).ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// True when a search-result title is close enough to the requested title to trust
    /// its art. Accepts collapsed-string equality / strong prefixes and good token
    /// overlap, but rejects loosely-related hits (so we leave the cover blank instead
    /// of showing the wrong game). Conservative on purpose.
    /// </summary>
    internal static bool TitlesMatch(string candidate, string query)
    {
        var a = NormalizeForMatch(candidate);
        var b = NormalizeForMatch(query);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;

        // Collapsed (spaceless) equality / substantial prefix, e.g. "AWayOut" vs "A Way Out".
        var ca = a.Replace(" ", "");
        var cb = b.Replace(" ", "");
        if (ca == cb) return true;
        if (ca.StartsWith(cb, StringComparison.Ordinal) || cb.StartsWith(ca, StringComparison.Ordinal))
        {
            var min = Math.Min(ca.Length, cb.Length);
            var max = Math.Max(ca.Length, cb.Length);
            if (min >= 4 && (double)min / max >= 0.6) return true;
        }

        // Token-set overlap: Jaccard or containment of the shorter title in the longer.
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return false;
        int inter = ta.Intersect(tb).Count();
        if (inter == 0) return false;
        int union = ta.Union(tb).Count();
        double jaccard = (double)inter / union;
        double containment = (double)inter / Math.Min(ta.Count, tb.Count);
        return jaccard >= 0.6 || containment >= 0.8;
    }

    private static async Task<string?> ResolveImageUrlAsync(string title)
    {
        // 1. Try Steam storefront first as it has standardized library images
        var steamEndpoint = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(title)}&l=english&cc=US";
        try
        {
            using var steamResp = await _http.GetAsync(steamEndpoint);
            if (steamResp.IsSuccessStatusCode)
            {
                using var steamStream = await steamResp.Content.ReadAsStreamAsync();
                using var steamDoc = await JsonDocument.ParseAsync(steamStream);
                if (steamDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    // Accept an exact title match outright; otherwise the first result that
                    // is *similar enough* to the requested title. Never blindly take the
                    // first hit — that's how unrelated art (e.g. a Spider-Man cover for
                    // "Hollow Knight: Silksong") used to slip in. No confident match here
                    // just falls through to the Epic source below.
                    int? matchedId = null;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("name", out var n) || !item.TryGetProperty("id", out var id)) continue;
                        var name = n.GetString() ?? "";
                        if (string.Equals(name, title, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedId = id.GetInt32();
                            break; // exact match wins
                        }
                        if (matchedId == null && TitlesMatch(name, title))
                            matchedId = id.GetInt32();
                    }
                    if (matchedId != null)
                        return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{matchedId.Value}/library_600x900.jpg";
                }
            }
        }
        catch { }

        // 2. Fallback to Epic Games data
        var endpoint = $"{EgDataSearchEndpoint}?query={Uri.EscapeDataString(title)}";
        using var resp = await _http.GetAsync(endpoint);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine($"[EpicCover] egdata HTTP {(int)resp.StatusCode} for '{title}'");
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("hits", out var elements)) return null;
        if (elements.GetArrayLength() == 0) return null;

        // Prefer the element whose title best matches; require it to be similar
        // enough so we leave the cover blank rather than show unrelated art.
        JsonElement? best = null;
        foreach (var el in elements.EnumerateArray())
        {
            if (!el.TryGetProperty("title", out var t)) continue;
            var tStr = t.GetString() ?? "";
            if (string.Equals(NormalizeTitle(tStr), title, StringComparison.OrdinalIgnoreCase))
            {
                best = el;
                break;
            }
            if (best == null && TitlesMatch(tStr, title))
                best = el;
        }
        if (best == null) return null; // no confident match → leave blank
        var picked = best.Value;

        if (!picked.TryGetProperty("keyImages", out var images)) return null;

        // Image type preference, tall covers first to match the Steam library_600x900 look.
        string[] preferred =
        {
            "OfferImageTall", "DieselStoreFrontTall", "Thumbnail",
            "DieselGameBoxTall", "OfferImageWide", "DieselStoreFrontWide", "VaultClosed"
        };

        foreach (var pref in preferred)
        {
            foreach (var img in images.EnumerateArray())
            {
                if (img.TryGetProperty("type", out var ty) &&
                    string.Equals(ty.GetString(), pref, StringComparison.OrdinalIgnoreCase) &&
                    img.TryGetProperty("url", out var u))
                {
                    var url = u.GetString();
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            }
        }

        // Anything with a url
        foreach (var img in images.EnumerateArray())
        {
            if (img.TryGetProperty("url", out var u))
            {
                var url = u.GetString();
                if (!string.IsNullOrEmpty(url)) return url;
            }
        }

        return null;
    }

    private static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
