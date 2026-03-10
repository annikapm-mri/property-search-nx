using System.Text.Json;
using System.Web;
using HtmlAgilityPack;
using PropertySearch.Api.Models;
using PropertySearch.Api.Services;

namespace PropertySearch.Api.Services;

public interface IScraperService
{
    Task<List<Property>> ScrapePropertiesAsync(string keyword);
}

public class FreePropertyScraperService : IScraperService
{
    private readonly HttpClient _http;
    private readonly ILogger<FreePropertyScraperService> _logger;
    private readonly string _csvPath;

    public FreePropertyScraperService(
        HttpClient http,
        ILogger<FreePropertyScraperService> logger,
        IConfiguration config)
    {
        _http    = http;
        _logger  = logger;
        _csvPath = config["CsvPath"] ?? "Data/properties.csv";
    }

    public async Task<List<Property>> ScrapePropertiesAsync(string keyword)
    {
        var headers = ReadCsvHeaders();
        
        // Try HTML scraping first
        _logger.LogInformation("[Scraper] Attempting HTML scrape for: {Keyword}", keyword);
        var htmlResults = await ScrapeCraigslistAsync(keyword, headers);
        if (htmlResults.Count > 0)
        {
            _logger.LogInformation("[Scraper] Got {Count} results from HTML scraping", htmlResults.Count);
            return htmlResults;
        }

        // Fallback to HUD API
        _logger.LogInformation("[Scraper] HTML scraping returned 0 results, trying HUD API");
        var hudResults = await FetchHudDataAsync(keyword, headers);
        if (hudResults.Count > 0)
        {
            _logger.LogInformation("[Scraper] Got {Count} results from HUD API", hudResults.Count);
            return hudResults;
        }

        _logger.LogInformation("[Scraper] No results found from any source");
        return new List<Property>();
    }

    // ── Scrape actual Craigslist listings from live web ──
    private async Task<List<Property>> ScrapeCraigslistAsync(string keyword, List<string> headers)
    {
        var results = new List<Property>();

        try
        {
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Extract location from keyword
            var location = ExtractLocationFromKeyword(keyword);
            if (string.IsNullOrEmpty(location))
                return results;

            // Map to Craigslist city code
            var cityCode = GetCraigslistCityCode(location);
            if (string.IsNullOrEmpty(cityCode))
                return results;

            // Search Craigslist
            var searchUrl = $"https://{cityCode}.craigslist.org/search/apa?query={HttpUtility.UrlEncode(keyword)}&sort=rel";
            _logger.LogInformation("[Scraper] Fetching Craigslist: {Url}", searchUrl);

            var response = await _http.GetAsync(searchUrl, HttpCompletionOption.ResponseContentRead);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Scraper] Craigslist returned {Status}", response.StatusCode);
                return results;
            }

            var html = await response.Content.ReadAsStringAsync();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Parse listings using XPath
            var listings = doc.DocumentNode.SelectNodes("//li[@data-pid]");
            _logger.LogInformation("[Scraper] Found {Count} listings on Craigslist", listings?.Count ?? 0);

            if (listings == null || listings.Count == 0)
                return results;

            foreach (var listing in listings.Take(5))
            {
                try
                {
                    // Extract listing URL
                    var linkNode = listing.SelectSingleNode(".//a[@href]");
                    if (linkNode == null) continue;

                    var url = linkNode.GetAttributeValue("href", "");
                    if (!url.Contains("craigslist"))
                        url = $"https://{cityCode}.craigslist.org{url}";

                    // Extract price
                    var priceNode = listing.SelectSingleNode(".//span[@class='priceinfo']");
                    if (priceNode == null) continue;

                    var priceText = priceNode.InnerText.Replace("$", "").Replace(",", "").Trim();
                    if (!decimal.TryParse(priceText, out var price))
                        continue;

                    // Extract title
                    var titleNode = listing.SelectSingleNode(".//a[@class='posting-title']");
                    var title = titleNode?.InnerText?.Trim() ?? "";
                    if (string.IsNullOrEmpty(title)) continue;

                    // Parse beds/baths from title
                    var bedsMatch = System.Text.RegularExpressions.Regex.Match(
                        title, @"(\d+)\s*(?:br|bed)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var bathsMatch = System.Text.RegularExpressions.Regex.Match(
                        title, @"(\d+)\s*(?:ba|bath)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    var beds = bedsMatch.Success ? int.Parse(bedsMatch.Groups[1].Value) : 1;
                    var baths = bathsMatch.Success ? double.Parse(bathsMatch.Groups[1].Value) : 1.0;

                    var property = BuildProperty(headers, new Dictionary<string, object?>
                    {
                        ["id"]           = Guid.NewGuid().ToString(),
                        ["url"]          = url,
                        ["region"]       = cityCode.Replace("craigslist.", "").Split('.')[0],
                        ["price"]        = price,
                        ["type"]         = title.Contains("house", StringComparison.OrdinalIgnoreCase) ? "house" : "apartment",
                        ["beds"]         = beds,
                        ["baths"]        = baths,
                        ["sqfeet"]       = 0,
                        ["cats_allowed"] = 0,
                        ["dogs_allowed"] = 0,
                        ["smoking_allowed"] = 0,
                        ["wheelchair_access"] = 0,
                        ["comes_furnished"] = 0,
                        ["laundry_options"] = "",
                        ["parking_options"] = "",
                        ["image_url"]    = "",
                        ["description"]  = title,
                        ["state"]        = location,
                    });

                    results.Add(property);
                    _logger.LogInformation("[Scraper] Scraped: {Price}/mo - {Title}", price, title);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Scraper] Error parsing listing");
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scraper] Craigslist scrape failed");
        }

        return results;
    }

    // ── Extract location code from keyword ──
    private static string? ExtractLocationFromKeyword(string keyword)
    {
        var stateAbbreviations = new Dictionary<string, string>
        {
            { "OH", "OH" }, { "OHIO", "OH" },
            { "TX", "TX" }, { "TEXAS", "TX" },
            { "CA", "CA" }, { "CALIFORNIA", "CA" },
            { "NY", "NY" }, { "NEW YORK", "NY" },
            { "FL", "FL" }, { "FLORIDA", "FL" },
            { "AUSTIN", "TX" }, { "HOUSTON", "TX" }, { "DALLAS", "TX" },
            { "CHICAGO", "IL" }, { "LOS ANGELES", "CA" }, { "NYC", "NY" }
        };

        var words = keyword.ToUpper()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (stateAbbreviations.TryGetValue(word, out var state))
                return state;
        }

        return null;
    }

    // ── Map state to Craigslist city code ──
    private static string? GetCraigslistCityCode(string state)
    {
        var cityMap = new Dictionary<string, string>
        {
            { "OH", "columbus" },
            { "TX", "austin" },
            { "CA", "orangecounty" },
            { "NY", "newyork" },
            { "FL", "miami" },
            { "IL", "chicago" }
        };

        return cityMap.TryGetValue(state.ToUpper(), out var city) ? city : null;
    }

    // ── Read CSV headers at runtime ──
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

    // ── Fetch REAL HUD Fair Market Rent data ──
    private async Task<List<Property>> FetchHudDataAsync(
        string keyword, List<string> headers)
    {
        var results = new List<Property>();

        try
        {
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("User-Agent", "PropertySearchApp/1.0");

            // Step 1 — Get all metro areas from HUD
            _logger.LogInformation("[Scraper] Fetching HUD metro areas for: {Keyword}", keyword);

            var metroUrl  = "https://www.huduser.gov/hudapi/public/fmr/listMetroAreas";
            var metroResp = await _http.GetAsync(metroUrl);

            if (!metroResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Scraper] HUD metro API returned {Status}", metroResp.StatusCode);
                return results;
            }

            var metroJson = await metroResp.Content.ReadAsStringAsync();
            var metroDoc  = JsonDocument.Parse(metroJson);

            if (metroDoc.RootElement.ValueKind != JsonValueKind.Array)
                return results;

            // Extract state code and city from keyword
            var stateCode = ExtractStateCode(keyword);
            var searchWords = keyword.ToLower()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && w != "house" && w != "apartment")
                .ToList();

            _logger.LogInformation("[Scraper] Extracted state: {State}, search words: {Words}", 
                stateCode ?? "none", string.Join(",", searchWords));

            // Step 2 — Find metros matching the keyword or state
            var matchedMetros = metroDoc.RootElement
                .EnumerateArray()
                .Where(m =>
                {
                    var name = GetString(m, "areaname") ?? "";
                    
                    // Match by state code if found
                    if (!string.IsNullOrEmpty(stateCode) && 
                        name.Contains($", {stateCode.ToUpper()}", StringComparison.OrdinalIgnoreCase))
                        return true;

                    // Match if ANY word in keyword appears in metro name
                    return searchWords.Any(w =>
                        name.Contains(w, StringComparison.OrdinalIgnoreCase));
                })
                .Take(5) // max 5 metros
                .ToList();

            _logger.LogInformation("[Scraper] Found {Count} matching HUD metros", matchedMetros.Count);

            // Step 3 — For each metro, get real FMR data
            foreach (var metro in matchedMetros)
            {
                var entityId  = GetString(metro, "cbsasub") ?? GetString(metro, "fips") ?? "";
                var areaName  = GetString(metro, "areaname") ?? keyword;
                var metroStateCode = ExtractStateFromAreaName(areaName);

                if (string.IsNullOrEmpty(entityId)) continue;

                var fmrUrl  = $"https://www.huduser.gov/hudapi/public/fmr/data/{entityId}";
                var fmrResp = await _http.GetAsync(fmrUrl);

                if (!fmrResp.IsSuccessStatusCode) continue;

                var fmrJson = await fmrResp.Content.ReadAsStringAsync();
                var fmrDoc  = JsonDocument.Parse(fmrJson);

                if (!fmrDoc.RootElement.TryGetProperty("data", out var data)) continue;
                if (!data.TryGetProperty("basicdata", out var basicData)) continue;

                // Step 4 — Extract real rent prices per bedroom type
                var rentData = new[]
                {
                    new { Label = "Studio",     Beds = 0, Baths = 1.0, RentKey = "Efficiency"    },
                    new { Label = "1 Bedroom",  Beds = 1, Baths = 1.0, RentKey = "One-Bedroom"   },
                    new { Label = "2 Bedroom",  Beds = 2, Baths = 1.0, RentKey = "Two-Bedroom"   },
                    new { Label = "3 Bedroom",  Beds = 3, Baths = 2.0, RentKey = "Three-Bedroom" },
                    new { Label = "4 Bedroom",  Beds = 4, Baths = 2.0, RentKey = "Four-Bedroom"  },
                };

                foreach (var rent in rentData)
                {
                    var price = GetDecimal(basicData, rent.RentKey);
                    if (price <= 0) continue;

                    // Clean city name from metro area name
                    var cityName = CleanCityName(areaName);

                    var property = BuildProperty(headers, new Dictionary<string, object?>
                    {
                        ["id"]          = Guid.NewGuid().ToString(),
                        ["url"]         = "https://www.huduser.gov/portal/datasets/fmr.html",
                        ["region"]      = cityName,
                        ["region_url"]  = "",
                        ["price"]       = price,
                        ["type"]        = rent.Beds == 0 ? "apartment" : "apartment",
                        ["sqfeet"]      = EstimateSqft(rent.Beds),
                        ["beds"]        = rent.Beds,
                        ["baths"]       = rent.Baths,
                        ["cats_allowed"]            = 0,
                        ["dogs_allowed"]            = 0,
                        ["smoking_allowed"]         = 0,
                        ["wheelchair_access"]       = 0,
                        ["electric_vehicle_charge"] = 0,
                        ["comes_furnished"]         = 0,
                        ["laundry_options"]         = "",
                        ["parking_options"]         = "",
                        ["image_url"]               = "",
                        ["description"]             = BuildDescription(rent.Label, cityName, price, areaName),
                        ["lat"]                     = (object?)"",
                        ["long"]                    = (object?)"",
                        ["state"]                   = metroStateCode
                    });

                    results.Add(property);
                }

                _logger.LogInformation(
                    "[Scraper] Got {Count} real HUD rent prices for {Area}",
                    results.Count, areaName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scraper] HUD fetch failed");
        }

        return results;
    }

    // ── Clean metro area name to just city ──
    // "Austin-Round Rock, TX Metro Area" → "Austin"
    private static string CleanCityName(string areaName)
    {
        // Remove "Metro Area", "HUD Metro FMR Area" etc
        var clean = areaName
            .Replace("HUD Metro FMR Area", "")
            .Replace("Metro Area", "")
            .Replace("Metro Div.", "")
            .Trim();

        // Take just the first city (before hyphen or comma)
        var firstCity = clean.Split(new[] { '-', ',' }, 2)[0].Trim();

        return firstCity;
    }

    // ── Extract state code from keyword (e.g., "oh" from "3 bed house in oh") ──
    private static string? ExtractStateCode(string keyword)
    {
        // List of all US state abbreviations
        var stateAbbreviations = new[] {
            "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
            "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
            "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
            "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
            "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY"
        };

        var words = keyword.ToLower()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Look for 2-letter state code in keyword
        foreach (var word in words)
        {
            if (word.Length == 2 && 
                stateAbbreviations.Contains(word.ToUpper()))
                return word.ToUpper();
        }

        return null;
    }

    // ── Extract state code from "Austin-Round Rock, TX Metro Area" ──
    private static string ExtractStateFromAreaName(string areaName)
    {
        // Look for 2-letter state code pattern ", TX"
        var match = System.Text.RegularExpressions.Regex.Match(
            areaName, @",\s*([A-Z]{2})\b");

        if (match.Success)
            return match.Groups[1].Value.ToLower();

        return "";
    }

    // ── Estimate sqft based on bedroom count ──
    private static int EstimateSqft(int beds) => beds switch
    {
        0 => 500,
        1 => 750,
        2 => 1000,
        3 => 1300,
        4 => 1700,
        _ => 1000
    };

    // ── Build honest description using real HUD data ──
    private static string BuildDescription(
        string label, string city, decimal price, string fullAreaName)
    {
        return $"HUD Fair Market Rent for a {label} in the {fullAreaName}. " +
               $"The official FMR price of ${price:N0}/month is set by the U.S. Department " +
               $"of Housing and Urban Development for the {city} area. " +
               $"FMR represents the 40th percentile of gross rents for standard quality units. " +
               $"Source: HUD FMR Data {DateTime.Now.Year}.";
    }

    // ── Build property using only CSV headers ──
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

    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static decimal GetDecimal(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;
    }
}