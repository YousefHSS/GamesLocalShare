using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace GamesLocalShare.Services;

/// <summary>
/// Resolves authoritative title + cover art for an installed Xbox (MSIXVC) game.
///
/// Every Game Pass / MSIXVC install carries a <c>Content\MicrosoftGame.Config</c> with a
/// <c>&lt;StoreId&gt;</c> element — the title's Microsoft Store product id. We read that id
/// (no flaky ACL/PFN parsing) and query the public, anonymous Store DisplayCatalog for the
/// real product title and poster image. Results are cached on disk by StoreId so we hit the
/// network at most once per title.
/// </summary>
public class XboxStoreMetadataService
{
    /// <summary>Authoritative title + cover for an Xbox title (either may be null).</summary>
    public sealed class StoreInfo
    {
        public string? Title { get; set; }
        public string? CoverUrl { get; set; }
    }

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GamesLocalShare/1.0");
        return c;
    }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamesLocalShare", "XboxStore");

    /// <summary>
    /// Returns the Store title + cover for the game installed at <paramref name="installPath"/>,
    /// or null when there is no readable StoreId or the lookup fails (caller should fall back).
    /// </summary>
    public async Task<StoreInfo?> ResolveAsync(string installPath)
    {
        try
        {
            var storeId = ReadStoreId(installPath);
            if (string.IsNullOrEmpty(storeId)) return null;

            Directory.CreateDirectory(CacheDir);
            var cachePath = Path.Combine(CacheDir, storeId + ".json");
            if (File.Exists(cachePath))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<StoreInfo>(await File.ReadAllTextAsync(cachePath));
                    if (cached != null) return cached;
                }
                catch { }
            }

            var url = $"https://displaycatalog.mp.microsoft.com/v7.0/products/{storeId}?market=US&languages=en-us";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            // The direct products/{id} endpoint returns a singular "Product" object; the
            // products/lookup endpoint returns a "Products" array. Accept either shape.
            JsonElement product;
            if (doc.RootElement.TryGetProperty("Product", out var single))
                product = single;
            else if (doc.RootElement.TryGetProperty("Products", out var products) && products.GetArrayLength() > 0)
                product = products[0];
            else
                return null;

            if (!product.TryGetProperty("LocalizedProperties", out var locs) || locs.GetArrayLength() == 0)
                return null;
            var loc = locs[0];

            var info = new StoreInfo
            {
                Title = loc.TryGetProperty("ProductTitle", out var t) ? t.GetString() : null,
                CoverUrl = PickCover(loc),
            };

            try { await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(info)); } catch { }
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>&lt;StoreId&gt;</c> from <c>Content\MicrosoftGame.Config</c>. Returns null if
    /// the file/folder is missing, unreadable (restrictive ACLs), or has only the commented-out
    /// placeholder id.
    /// </summary>
    private static string? ReadStoreId(string installPath)
    {
        try
        {
            var contentDir = Path.Combine(installPath, "Content");
            if (!Directory.Exists(contentDir)) return null;

            var cfg = Directory.GetFiles(contentDir, "*.config", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => Path.GetFileName(f).Equals("MicrosoftGame.Config", StringComparison.OrdinalIgnoreCase));
            if (cfg == null) return null;

            var xml = File.ReadAllText(cfg);
            // Match a real <StoreId>ABCDEF1234</StoreId> element (the template ships a commented
            // placeholder "000000000000"; require a plausible alphanumeric id and reject all-zero).
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(xml, @"<StoreId>\s*([0-9A-Za-z]{8,})\s*</StoreId>"))
            {
                var id = m.Groups[1].Value.Trim();
                if (id.Trim('0').Length > 0) return id;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the best cover image, preferring a tall poster (library look) then box art.
    /// Store image URIs are protocol-relative (<c>//store-images...</c>); normalize to https.
    /// </summary>
    private static string? PickCover(JsonElement loc)
    {
        if (!loc.TryGetProperty("Images", out var images)) return null;

        string[] preferred = { "Poster", "BoxArt", "FeaturePromotionalSquareArt", "TitledHeroArt", "SuperHeroArt", "Logo" };
        foreach (var pref in preferred)
        {
            foreach (var img in images.EnumerateArray())
            {
                if (img.TryGetProperty("ImagePurpose", out var p) &&
                    string.Equals(p.GetString(), pref, StringComparison.OrdinalIgnoreCase) &&
                    img.TryGetProperty("Uri", out var u))
                {
                    var uri = u.GetString();
                    if (string.IsNullOrEmpty(uri)) continue;
                    if (uri.StartsWith("//")) return "https:" + uri;
                    if (uri.StartsWith("http")) return uri;
                    return "https://" + uri.TrimStart('/');
                }
            }
        }
        return null;
    }
}
