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
                url = await ResolveImageUrlAsync(NormalizeTitle(game.Name));
                if (string.IsNullOrEmpty(url))
                {
                    // Try with trailing tokens trimmed (e.g. "Rocket League" instead of "Rocket League: Ultimate Edition")
                    var firstSegment = game.Name.Split(new[] { ':', '-', '–', '—' }, 2)[0].Trim();
                    if (!string.IsNullOrEmpty(firstSegment) && !string.Equals(firstSegment, game.Name, StringComparison.OrdinalIgnoreCase))
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
                    // Look for exact match first
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var n) && string.Equals(n.GetString(), title, StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.TryGetProperty("id", out var id))
                            {
                                return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id.GetInt32()}/library_600x900.jpg";
                            }
                        }
                    }

                    // Fall back to first match
                    if (items[0].TryGetProperty("id", out var firstId))
                    {
                        return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{firstId.GetInt32()}/library_600x900.jpg";
                    }
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

        // Prefer the element whose normalized title best matches.
        JsonElement? best = null;
        foreach (var el in elements.EnumerateArray())
        {
            if (!el.TryGetProperty("title", out var t)) continue;
            var tStr = NormalizeTitle(t.GetString() ?? "");
            if (string.Equals(tStr, title, StringComparison.OrdinalIgnoreCase))
            {
                best = el;
                break;
            }
            if (best == null && tStr.StartsWith(title, StringComparison.OrdinalIgnoreCase))
                best = el;
        }
        var picked = best ?? elements[0];

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
