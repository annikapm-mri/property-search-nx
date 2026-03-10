using Microsoft.Playwright;
using PropertySearch.Api.Models;

namespace PropertySearch.Api.Services;

public class CraigslistScraperService : IScraperService
{
    private readonly ILogger<CraigslistScraperService> _logger;
    private readonly string _csvPath;
    

    // ── Craigslist city subdomain map ──
    private static readonly Dictionary<string, string> CityMap = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["austin"]        = "austin",
        ["houston"]       = "houston",
        ["dallas"]        = "dallas",
        ["miami"]         = "miami",
        ["orlando"]       = "orlando",
        ["denver"]        = "denver",
        ["seattle"]       = "seattle",
        ["chicago"]       = "chicago",
        ["atlanta"]       = "atlanta",
        ["phoenix"]       = "phoenix",
        ["reno"]          = "reno",
        ["nashville"]     = "nashville",
        ["portland"]      = "portland",
        ["boston"]        = "boston",
        ["losangeles"]    = "losangeles",
        ["sandiego"]      = "sandiego",
        ["lasvegas"]      = "lasvegas",
        ["newyork"]       = "newyork",
        ["sanfrancisco"]  = "sfbay",
        ["tampa"]         = "tampa",
        ["charlotte"]     = "charlotte",
        ["sacramento"]    = "sacramento",
        ["minneapolis"]   = "minneapolis",
        ["detroit"]       = "detroit",
        ["memphis"]       = "memphis",
        ["baltimore"]     = "baltimore",
        ["washington"]    = "washingtondc",
        ["dc"]            = "washingtondc",
        ["tx"]            = "austin",
        ["ca"]            = "losangeles",
        ["fl"]            = "miami",
        ["wa"]            = "seattle",
        ["co"]            = "denver",
        ["ga"]            = "atlanta",
        ["il"]            = "chicago",
        ["az"]            = "phoenix",
        ["tn"]            = "nashville",
        ["nv"]            = "lasvegas",
    };

    public CraigslistScraperService(
        ILogger<CraigslistScraperService> logger,
        IConfiguration config)
    {
        _logger  = logger;
        _csvPath = config["CsvPath"] ?? "Data/properties.csv";
    }

    public async Task<List<Property>> ScrapePropertiesAsync(string keyword)
    {
        var headers  = ReadCsvHeaders();
        var city     = ExtractCity(keyword);
        var citySlug = GetCraigslistSlug(city);

        _logger.LogInformation(
            "[Craigslist] Scraping {City} ({Slug}) for: {Keyword}",
            city, citySlug, keyword);

        return await ScrapeCraigslistAsync(citySlug, city, keyword, headers);
    }

    private async Task<List<Property>> ScrapeCraigslistAsync(
        string citySlug, string cityName, string keyword, List<string> headers)
    {
        var results = new List<Property>();

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,  // invisible browser
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
            });

            var page = await browser.NewPageAsync();

            // ── Set realistic browser headers ──
            await page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                 "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                 "Chrome/120.0.0.0 Safari/537.36"
            });

            // ── Build Craigslist URL ──
            // apartments/housing for rent section
            var url = $"https://{citySlug}.craigslist.org/search/apa";
            _logger.LogInformation("[Craigslist] Fetching: {Url}", url);

            await page.GotoAsync(url, new() {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout   = 15000
            });

            // ── Wait for listings to load ──
            await page.WaitForSelectorAsync(".cl-search-result", new() {
                Timeout = 10000
            });

            // ── Scrape listing cards ──
            var listings = await page.QuerySelectorAllAsync(".cl-search-result");
            _logger.LogInformation("[Craigslist] Found {Count} listings", listings.Count);

            foreach (var listing in listings.Take(12))
            {
                try
                {
                    // Price
                    var priceEl = await listing.QuerySelectorAsync(".priceinfo");
                    var priceText = await priceEl?.InnerTextAsync() ?? "$0";
                    var price = ParsePrice(priceText);
                    if (price <= 0) continue;

                    // Title
                    var titleEl = await listing.QuerySelectorAsync(".titlestring");
                    var title = await titleEl?.InnerTextAsync() ?? "";

                    // URL
                    var linkEl = await listing.QuerySelectorAsync("a.cl-app-anchor");
                    var href   = await linkEl?.GetAttributeAsync("href") ?? "";

                    // Beds/baths from meta
                    var metaEl  = await listing.QuerySelectorAsync(".housing");
                    var metaText = await metaEl?.InnerTextAsync() ?? "";
                    var (beds, baths, sqft) = ParseMeta(metaText);

                    // Location
                    var locEl  = await listing.QuerySelectorAsync(".location");
                    var location = await locEl?.InnerTextAsync() ?? cityName;

                    // Image
                    var imgEl  = await listing.QuerySelectorAsync("img");
                    var imgSrc = await imgEl?.GetAttributeAsync("src") ?? "";

                    var property = BuildProperty(headers, new Dictionary<string, object?>
                    {
                        ["id"]          = Guid.NewGuid().ToString(),
                        ["url"]         = href.StartsWith("http") ? href : $"https://{citySlug}.craigslist.org{href}",
                        ["region"]      = string.IsNullOrEmpty(location) ? cityName : location.Trim(),
                        ["region_url"]  = "",
                        ["price"]       = price,
                        ["type"]        = DetectType(title),
                        ["sqfeet"]      = sqft,
                        ["beds"]        = beds,
                        ["baths"]       = baths,
                        ["cats_allowed"]            = title.ToLower().Contains("cat") ? 1 : 0,
                        ["dogs_allowed"]            = title.ToLower().Contains("dog") ? 1 : 0,
                        ["smoking_allowed"]         = 0,
                        ["wheelchair_access"]       = 0,
                        ["electric_vehicle_charge"] = 0,
                        ["comes_furnished"]         = title.ToLower().Contains("furnished") ? 1 : 0,
                        ["laundry_options"]         = title.ToLower().Contains("laundry") || title.ToLower().Contains("w/d") ? "w/d in unit" : "",
                        ["parking_options"]         = title.ToLower().Contains("parking") || title.ToLower().Contains("garage") ? "off-street parking" : "",
                        ["image_url"]               = imgSrc,
                        ["description"]             = $"{title}. Located in {location}.",
                        ["lat"]                     = (object?)"",
                        ["long"]                    = (object?)"",
                        ["state"]                   = GetStateFromSlug(citySlug)
                    });

                    results.Add(property);
                    _logger.LogInformation(
                        "[Craigslist] ✅ {Beds}bd/{Baths}ba — ${Price} — {Location}",
                        beds, baths, price, location);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Craigslist] Skipped listing: {Msg}", ex.Message);
                }
            }

            _logger.LogInformation(
                "[Craigslist] Scraped {Count} real listings from {City}",
                results.Count, cityName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Craigslist] Scrape failed for {City}", cityName);
        }

        return results;
    }

    // ── Parse "$1,500/mo" → 1500 ──
    private static decimal ParsePrice(string text)
    {
        var digits = System.Text.RegularExpressions.Regex.Replace(text, @"[^\d]", "");
        return decimal.TryParse(digits, out var p) ? p : 0;
    }

    // ── Parse "2br - 850ft²" → (beds:2, baths:1, sqft:850) ──
    private static (int beds, double baths, int sqft) ParseMeta(string meta)
    {
        var beds  = 0;
        var baths = 1.0;
        var sqft  = 0;

        var brMatch = System.Text.RegularExpressions.Regex.Match(meta, @"(\d+)br");
        if (brMatch.Success) beds = int.Parse(brMatch.Groups[1].Value);

        var baMatch = System.Text.RegularExpressions.Regex.Match(meta, @"(\d+\.?\d*)ba");
        if (baMatch.Success) baths = double.Parse(baMatch.Groups[1].Value);

        var sqMatch = System.Text.RegularExpressions.Regex.Match(meta, @"(\d+)ft");
        if (sqMatch.Success) sqft = int.Parse(sqMatch.Groups[1].Value);

        return (beds, baths, sqft);
    }

    // ── Detect property type from title ──
    private static string DetectType(string title)
    {
        var t = title.ToLower();
        if (t.Contains("house") || t.Contains("home")) return "house";
        if (t.Contains("condo"))                        return "condo";
        if (t.Contains("townhouse") || t.Contains("townhome")) return "townhouse";
        if (t.Contains("studio"))                       return "studio";
        return "apartment";
    }

    // ── Extract city from keyword ──
    private static string ExtractCity(string keyword)
    {
        var words = keyword.ToLower()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
            if (CityMap.ContainsKey(word)) return word;

        return words.LastOrDefault() ?? "austin";
    }

    // ── Get Craigslist subdomain ──
    private static string GetCraigslistSlug(string city)
    {
        return CityMap.TryGetValue(city.ToLower(), out var slug) ? slug : city.ToLower();
    }

    // ── Get state code from Craigslist slug ──
    private static string GetStateFromSlug(string slug) => slug switch
    {
        "austin" or "houston" or "dallas"    => "tx",
        "losangeles" or "sfbay" or "sacramento" => "ca",
        "miami" or "orlando" or "tampa"      => "fl",
        "seattle"                            => "wa",
        "denver"                             => "co",
        "atlanta"                            => "ga",
        "chicago"                            => "il",
        "phoenix"                            => "az",
        "nashville" or "memphis"             => "tn",
        "lasvegas" or "reno"                 => "nv",
        "portland"                           => "or",
        "boston"                             => "ma",
        "washingtondc" or "baltimore"        => "md",
        "newyork"                            => "ny",
        "charlotte"                          => "nc",
        "minneapolis"                        => "mn",
        "detroit"                            => "mi",
        _                                    => "us"
    };

    // ── CSV helpers ──
    private List<string> ReadCsvHeaders()
    {
        try
        {
            if (!File.Exists(_csvPath)) return new List<string>();
            using var reader = new StreamReader(_csvPath);
            var line = reader.ReadLine() ?? "";
            return line.Split(',')
                .Select(h => h.Trim().Trim('"').ToLower())
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static Property BuildProperty(
        List<string> headers, Dictionary<string, object?> data)
    {
        var p = new Property();
        foreach (var header in headers)
        {
            if (!data.TryGetValue(header, out var value)) continue;
            switch (header)
            {
                case "id":          p.Id          = value?.ToString() ?? ""; break;
                case "url":         p.Url         = value?.ToString() ?? ""; break;
                case "region":      p.Region      = value?.ToString() ?? ""; break;
                case "price":       p.Price       = Convert.ToDecimal(value ?? 0); break;
                case "type":        p.Type        = value?.ToString() ?? ""; break;
                case "sqfeet":      p.SqFeet      = Convert.ToDouble(value ?? 0); break;
                case "beds":        p.Beds        = Convert.ToInt32(value ?? 0); break;
                case "baths":       p.Baths       = Convert.ToDouble(value ?? 0); break;
                case "image_url":   p.ImageUrl    = value?.ToString() ?? ""; break;
                case "description": p.Description = value?.ToString() ?? ""; break;
                case "state":       p.State       = value?.ToString() ?? ""; break;
                case "cats_allowed":  p.CatsAllowed  = Convert.ToInt32(value ?? 0); break;
                case "dogs_allowed":  p.DogsAllowed  = Convert.ToInt32(value ?? 0); break;
                case "comes_furnished": p.ComesFurnished = Convert.ToInt32(value ?? 0); break;
                case "laundry_options": p.LaundryOptions = value?.ToString() ?? ""; break;
                case "parking_options": p.ParkingOptions = value?.ToString() ?? ""; break;
                case "lat":
                    p.Lat = string.IsNullOrEmpty(value?.ToString())
                        ? null : Convert.ToDouble(value); break;
                case "long":
                    p.Long = string.IsNullOrEmpty(value?.ToString())
                        ? null : Convert.ToDouble(value); break;
            }
        }
        return p;
    }
}