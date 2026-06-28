using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GamesLocalShare.Services;

/// <summary>
/// Resolves cover art from SteamGridDB — a community art database that covers titles the
/// official storefronts don't (console ports, EA/Konami catalogs, retro games). Requires a
/// free user API key. Used as a last-resort source for External (non-store) games, gated by
/// the same high-confidence title match as the other cover sources so a wrong cover is never
/// shown. Resolved URLs are cached on disk by title.
/// </summary>
public class SteamGridDbCoverService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamesLocalShare", "SgdbCovers");

    /// <summary>
    /// Returns a tall cover URL for <paramref name="title"/>, or null when there is no
    /// confident match, the key is missing, or the API call fails.
    /// </summary>
    public async Task<string?> ResolveCoverAsync(string title, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var cachePath = Path.Combine(CacheDir, SafeFileName(title) + ".url");
            if (File.Exists(cachePath))
            {
                var cached = (await File.ReadAllTextAsync(cachePath)).Trim();
                return string.IsNullOrEmpty(cached) ? null : cached;
            }

            // 1) Find the game id via autocomplete, accepting only a confident name match.
            var searchUrl = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(title)}";
            int? gameId = null;
            using (var doc = await GetJsonAsync(searchUrl, apiKey))
            {
                if (doc == null) return null;
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return null;
                foreach (var g in data.EnumerateArray())
                {
                    if (!g.TryGetProperty("id", out var idEl) || !g.TryGetProperty("name", out var nm)) continue;
                    if (TitleCoverArtService.TitlesMatch(nm.GetString() ?? "", title))
                    {
                        gameId = idEl.GetInt32();
                        break;
                    }
                }
            }
            if (gameId == null) return null;

            // 2) Fetch a tall (600x900) static cover for that game.
            var gridUrl = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}?dimensions=600x900&types=static&limit=1";
            string? coverUrl = null;
            using (var doc = await GetJsonAsync(gridUrl, apiKey))
            {
                if (doc != null
                    && doc.RootElement.TryGetProperty("data", out var grids)
                    && grids.GetArrayLength() > 0
                    && grids[0].TryGetProperty("url", out var u))
                {
                    coverUrl = u.GetString();
                }
            }

            // Cache the result (including an empty marker so we don't re-query a known miss).
            try { await File.WriteAllTextAsync(cachePath, coverUrl ?? string.Empty); } catch { }
            return coverUrl;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, string apiKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    }

    private static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
