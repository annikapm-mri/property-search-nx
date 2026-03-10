using Microsoft.Playwright;
using PropertySearch.Api.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PropertySearch.Api.Services;

public class PropertyFinderScraperService : IScraperService
{
    private readonly ILogger<PropertyFinderScraperService> _logger;
    private readonly string _csvPath;

    // ── UAE city/region mapping ──
    private static readonly Dictionary<string, string> CityMap = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["dubai"]         = "dubai",
        ["abudhabi"]      = "abu-dhabi",
        ["abu dhabi"]     = "abu-dhabi",
        ["sharjah"]       = "sharjah",
        ["ajman"]         = "ajman",
        ["rak"]           = "ras-al-khaimah",
        ["rasalkhaimah"]  = "ras-al-khaimah",
        ["fujairah"]      = "fujairah",
        ["ummalquwain"]   = "umm-al-quwain",
        ["uae"]           = "dubai",
        ["emirates"]      = "dubai",
    };

    public PropertyFinderScraperService(
        ILogger<PropertyFinderScraperService> logger,
        IConfiguration config)
    {
        _logger  = logger;
        _csvPath = config["CsvPath"] ?? "Data/properties.csv";
    }

    public async Task<List<Property>> ScrapePropertiesAsync(string keyword)
    {
        var headers = ReadCsvHeaders();
        var city    = ExtractCity(keyword);
        var citySlug = GetCitySlug(city);

        _logger.LogInformation(
            "[PropertyFinder] Scraping {City} ({Slug}) for: {Keyword}",
            city, citySlug, keyword);

        return await ScrapePropertyFinderAsync(citySlug, city, keyword, headers);
    }

    private async Task<List<Property>> ScrapePropertyFinderAsync(
        string citySlug, string cityName, string keyword, List<string> headers)
    {
        var results = new List<Property>();

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
            });

            var page = await browser.NewPageAsync();

            // ── Set realistic browser headers ──
            await page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                 "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                 "Chrome/120.0.0.0 Safari/537.36",
                ["Accept-Language"] = "en-US,en;q=0.9"
            });

            // ── Build PropertyFinder URL ──
            // Avoid hardcoded location ids and use keyword query directly
            var encodedKeyword = Uri.EscapeDataString(keyword);
            var url = $"https://www.propertyfinder.ae/en/search?c=1&fu=0&ob=mr&page=1&k={encodedKeyword}"; // c=1 is rent
            
            _logger.LogInformation("[PropertyFinder] Fetching: {Url}", url);

            await page.GotoAsync(url, new()
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout   = 30000
            });

            // ── Wait for page to load fully ──
            await Task.Delay(3000); // Give time for dynamic content to load

            // ── Save screenshot for debugging ──
            var screenshotPath = $"propertyfinder-debug-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            await page.ScreenshotAsync(new() { Path = screenshotPath, FullPage = true });
            _logger.LogInformation("[PropertyFinder] Screenshot saved: {Path}", screenshotPath);

            // ── Log page content for debugging ──
            var pageContent = await page.ContentAsync();
            _logger.LogInformation("[PropertyFinder] Page content length: {Length} chars", pageContent.Length);

            // ── Primary extraction path: parse embedded __NEXT_DATA__ JSON ──
            var jsonResults = ExtractPropertiesFromNextData(pageContent, headers, cityName, citySlug);
            if (jsonResults.Count > 0)
            {
                _logger.LogInformation("[PropertyFinder] Extracted {Count} listings from __NEXT_DATA__", jsonResults.Count);
                return jsonResults;
            }

            // ── Fallback: crawl multiple generic pages and filter to requested city ──
            var pagedCityResults = await CollectCityResultsByPaginationAsync(page, headers, cityName, citySlug);
            if (pagedCityResults.Count > 0)
            {
                _logger.LogInformation("[PropertyFinder] Extracted {Count} city-matched listings via pagination fallback", pagedCityResults.Count);
                return pagedCityResults;
            }
            
            // Check if we hit a CAPTCHA or block (DOM-based, not plain string match)
            var challengeElement = await page.QuerySelectorAsync(
                "iframe[src*='captcha'], iframe[src*='recaptcha'], [id*='captcha'], [class*='captcha'], [data-sitekey]");
            var bodyText = await page.InnerTextAsync("body");
            var hasChallengeText = bodyText.Contains("verify you are human", StringComparison.OrdinalIgnoreCase) ||
                                   bodyText.Contains("are you a robot", StringComparison.OrdinalIgnoreCase) ||
                                   bodyText.Contains("access denied", StringComparison.OrdinalIgnoreCase);

            if (challengeElement != null || hasChallengeText)
            {
                _logger.LogError("[PropertyFinder] Page appears to be blocked or showing CAPTCHA");
                return results;
            }

            // ── Try to find ANY cards on the page ──
            var possibleSelectors = new[]
            {
                "div[data-testid='property-card']",
                "[class*='PropertyCard']",
                "[class*='property-card']",
                "[data-testid*='property']",
                "article",
                ".card",
                "a[href*='/property-for-rent']",
                "a[href*='/en/rent/']"
            };

            IReadOnlyList<IElementHandle>? listings = null;
            string? usedSelector = null;
            
            foreach (var selector in possibleSelectors)
            {
                try
                {
                    listings = await page.QuerySelectorAllAsync(selector);
                    if (listings.Count > 0)
                    {
                        _logger.LogInformation("[PropertyFinder] Found {Count} elements using selector: {Selector}",
                            listings.Count, selector);
                        usedSelector = selector;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("[PropertyFinder] Selector {Selector} failed: {Error}",
                        selector, ex.Message);
                }
            }

            if (!string.IsNullOrEmpty(usedSelector))
            {
                _logger.LogInformation("[PropertyFinder] Using selector: {Selector}", usedSelector);
            }

            if (listings == null || listings.Count == 0)
            {
                _logger.LogWarning("[PropertyFinder] No listings found with any selector. Check screenshot: {Path}",
                    screenshotPath);
                return results;
            }

            // ── Track seen URLs to prevent duplicates ──
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var listing in listings.Take(20)) // Take more to account for duplicates
            {
                try
                {
                    // Extract all text from the listing for debugging
                    var listingText = await listing.InnerTextAsync();
                    _logger.LogDebug("[PropertyFinder] Processing listing text: {Text}", 
                        listingText.Length > 200 ? listingText.Substring(0, 200) + "..." : listingText);

                    // URL - try to find any link
                    var linkEl = await listing.QuerySelectorAsync("a[href]");
                    var href = linkEl != null ? await linkEl.GetAttributeAsync("href") ?? "" : "";
                    if (!string.IsNullOrEmpty(href) && !href.StartsWith("http"))
                        href = $"https://www.propertyfinder.ae{href}";

                    // ── Skip duplicates or empty URLs ──
                    if (string.IsNullOrEmpty(href) || !seenUrls.Add(href))
                    {
                        _logger.LogDebug("[PropertyFinder] Skipping duplicate or empty URL: {Url}", href);
                        continue;
                    }

                    // ── Stop if we have enough unique results ──
                    if (results.Count >= 12) break;

                    // Price - look for any text with AED or numbers
                    var priceEl = await listing.QuerySelectorAsync(
                        "[class*='price'], [data-testid*='price'], span:has-text('AED'), span:has-text('AED')");
                    var priceText = priceEl != null ? await priceEl.InnerTextAsync() : "";
                    
                    // If no specific price element, extract from listing text
                    if (string.IsNullOrEmpty(priceText))
                    {
                        var priceMatch = System.Text.RegularExpressions.Regex.Match(
                            listingText, @"AED\s*([\d,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (priceMatch.Success)
                            priceText = priceMatch.Groups[1].Value;
                    }
                    
                    var price = ParsePrice(priceText);
                    if (price <= 0)
                    {
                        _logger.LogDebug("[PropertyFinder] Skipping listing with no valid price");
                        continue;
                    }

                    // Title - try multiple selectors
                    var titleEl = await listing.QuerySelectorAsync(
                        "h2, h3, h4, [class*='title'], [data-testid*='title'], a");
                    var title = titleEl != null ? await titleEl.InnerTextAsync() : listingText.Split('\n')[0];
                    title = title.Trim();
                    if (title.Length > 200) title = title.Substring(0, 200);

                    // Beds/baths - use regex on the listing text
                    var beds = ExtractBedsFromText(listingText);
                    var baths = ExtractBathsFromText(listingText);

                    // Area (sqft) - use regex
                    var sqft = ExtractAreaFromText(listingText);

                    // Location - try to find location element or use city
                    var locEl = await listing.QuerySelectorAsync(
                        "[class*='location'], [data-testid*='location'], [class*='address']");
                    var location = locEl != null ? await locEl.InnerTextAsync() : cityName;
                    location = location.Trim();
                    if (location.Length > 100) location = location.Substring(0, 100);

                    // Keep only listings matching requested city/emirate
                    if (!IsLocationMatch(location, cityName, citySlug))
                    {
                        _logger.LogDebug("[PropertyFinder] Skipping non-matching location: {Location}", location);
                        continue;
                    }

                    // Image
                    var imgEl = await listing.QuerySelectorAsync("img");
                    var imgSrc = imgEl != null ? await imgEl.GetAttributeAsync("src") ?? "" : "";

                    // Property type
                    var propertyType = DetectType(title, listingText);

                    var property = BuildProperty(headers, new Dictionary<string, object?>
                    {
                        ["id"]          = Guid.NewGuid().ToString(),
                        ["url"]         = href,
                        ["region"]      = location.Trim(),
                        ["region_url"]  = "",
                        ["price"]       = price,
                        ["type"]        = propertyType,
                        ["sqfeet"]      = sqft,
                        ["beds"]        = beds,
                        ["baths"]       = baths,
                        ["cats_allowed"]            = 0,
                        ["dogs_allowed"]            = 0,
                        ["smoking_allowed"]         = 0,
                        ["wheelchair_access"]       = 0,
                        ["electric_vehicle_charge"] = 0,
                        ["comes_furnished"]         = title.ToLower().Contains("furnished") ? 1 : 0,
                        ["laundry_options"]         = "",
                        ["parking_options"]         = title.ToLower().Contains("parking") ? "parking available" : "",
                        ["image_url"]               = imgSrc,
                        ["description"]             = $"{title}. Located in {location}.",
                        ["lat"]                     = (object?)"",
                        ["long"]                    = (object?)"",
                        ["state"]                   = cityName
                    });

                    results.Add(property);
                    _logger.LogInformation(
                        "[PropertyFinder] ✅ {Beds}bd/{Baths}ba — AED {Price} — {Location}",
                        beds, baths, price, location);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[PropertyFinder] Skipped listing: {Msg}", ex.Message);
                }
            }

            _logger.LogInformation(
                "[PropertyFinder] Scraped {Count} listings from {City}",
                results.Count, cityName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PropertyFinder] Scrape failed for {City}", cityName);
        }

        return results;
    }

    private List<Property> ExtractPropertiesFromNextData(
        string pageContent,
        List<string> headers,
        string cityName,
        string citySlug)
    {
        var results = new List<Property>();

        try
        {
            var match = Regex.Match(
                pageContent,
                "<script id=\"__NEXT_DATA__\" type=\"application/json\">(?<json>.*?)</script>",
                RegexOptions.Singleline);

            if (!match.Success)
            {
                _logger.LogWarning("[PropertyFinder] __NEXT_DATA__ script not found");
                return results;
            }

            using var doc = JsonDocument.Parse(match.Groups["json"].Value);
            var root = doc.RootElement;

            if (!root.TryGetProperty("props", out var props) ||
                !props.TryGetProperty("pageProps", out var pageProps) ||
                !pageProps.TryGetProperty("searchResult", out var searchResult) ||
                !searchResult.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("[PropertyFinder] Unable to locate properties array in __NEXT_DATA__");
                return results;
            }

            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in properties.EnumerateArray())
            {
                try
                {
                    var detailsPath = item.TryGetProperty("details_path", out var detailsPathEl)
                        ? detailsPathEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(detailsPath)) continue;

                    var href = detailsPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? detailsPath
                        : $"https://www.propertyfinder.ae{detailsPath}";
                    if (!seenUrls.Add(href)) continue;

                    var title = item.TryGetProperty("title", out var titleEl)
                        ? titleEl.GetString() ?? string.Empty
                        : string.Empty;

                    var price = 0m;
                    if (item.TryGetProperty("price", out var priceObj) &&
                        priceObj.TryGetProperty("value", out var priceVal) &&
                        priceVal.ValueKind == JsonValueKind.Number)
                    {
                        price = priceVal.GetDecimal();
                    }
                    if (price <= 0) continue;

                    var beds = item.TryGetProperty("bedrooms", out var bedsEl) && bedsEl.ValueKind == JsonValueKind.Number
                        ? bedsEl.GetInt32()
                        : 0;

                    var baths = item.TryGetProperty("bathrooms", out var bathsEl) && bathsEl.ValueKind == JsonValueKind.Number
                        ? bathsEl.GetInt32()
                        : 0;

                    var sqft = 0;
                    if (item.TryGetProperty("size", out var sizeObj) && sizeObj.ValueKind == JsonValueKind.Object)
                    {
                        if (sizeObj.TryGetProperty("value", out var sizeVal) && sizeVal.ValueKind == JsonValueKind.Number)
                        {
                            sqft = (int)sizeVal.GetDecimal();
                        }

                        if (sizeObj.TryGetProperty("unit", out var unitEl))
                        {
                            var unit = unitEl.GetString() ?? string.Empty;
                            if (unit.Contains("sqm", StringComparison.OrdinalIgnoreCase) ||
                                unit.Contains("sq.m", StringComparison.OrdinalIgnoreCase))
                            {
                                sqft = (int)(sqft * 10.764m);
                            }
                        }
                    }

                    var location = cityName;
                    string lat = string.Empty;
                    string lon = string.Empty;

                    if (item.TryGetProperty("location", out var locationObj) && locationObj.ValueKind == JsonValueKind.Object)
                    {
                        if (locationObj.TryGetProperty("full_name", out var fullNameEl))
                        {
                            location = fullNameEl.GetString() ?? cityName;
                        }
                        else if (locationObj.TryGetProperty("path_name", out var pathNameEl))
                        {
                            location = pathNameEl.GetString() ?? cityName;
                        }

                        if (locationObj.TryGetProperty("coordinates", out var coordObj) && coordObj.ValueKind == JsonValueKind.Object)
                        {
                            if (coordObj.TryGetProperty("lat", out var latEl) && latEl.ValueKind == JsonValueKind.Number)
                                lat = latEl.GetDecimal().ToString();
                            if (coordObj.TryGetProperty("lon", out var lonEl) && lonEl.ValueKind == JsonValueKind.Number)
                                lon = lonEl.GetDecimal().ToString();
                        }
                    }

                    if (!IsLocationMatch(location, cityName, citySlug))
                    {
                        _logger.LogDebug("[PropertyFinder] Skipping non-matching JSON location: {Location}", location);
                        continue;
                    }

                    var imgSrc = string.Empty;
                    if (item.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
                    {
                        var firstImage = imagesEl.EnumerateArray().FirstOrDefault();
                        if (firstImage.ValueKind == JsonValueKind.Object &&
                            firstImage.TryGetProperty("url", out var imgUrlEl))
                        {
                            imgSrc = imgUrlEl.GetString() ?? string.Empty;
                        }
                    }

                    var description = item.TryGetProperty("description", out var descEl)
                        ? descEl.GetString() ?? string.Empty
                        : string.Empty;

                    var propertyTypeRaw = item.TryGetProperty("property_type", out var propertyTypeEl)
                        ? propertyTypeEl.GetString() ?? string.Empty
                        : string.Empty;
                    var propertyType = DetectType(title, propertyTypeRaw);

                    var property = BuildProperty(headers, new Dictionary<string, object?>
                    {
                        ["id"]          = Guid.NewGuid().ToString(),
                        ["url"]         = href,
                        ["region"]      = location.Trim(),
                        ["region_url"]  = "",
                        ["price"]       = price,
                        ["type"]        = propertyType,
                        ["sqfeet"]      = sqft,
                        ["beds"]        = beds,
                        ["baths"]       = baths,
                        ["cats_allowed"]            = 0,
                        ["dogs_allowed"]            = 0,
                        ["smoking_allowed"]         = 0,
                        ["wheelchair_access"]       = 0,
                        ["electric_vehicle_charge"] = 0,
                        ["comes_furnished"]         = description.Contains("furnished", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                        ["laundry_options"]         = "",
                        ["parking_options"]         = description.Contains("parking", StringComparison.OrdinalIgnoreCase) ? "parking available" : "",
                        ["image_url"]               = imgSrc,
                        ["description"]             = string.IsNullOrWhiteSpace(description) ? $"{title}. Located in {location}." : description,
                        ["lat"]                     = lat,
                        ["long"]                    = lon,
                        ["state"]                   = cityName
                    });

                    results.Add(property);
                    if (results.Count >= 12) break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PropertyFinder] Failed to parse listing from __NEXT_DATA__");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PropertyFinder] Failed to parse __NEXT_DATA__");
        }

        return results;
    }

    private async Task<List<Property>> CollectCityResultsByPaginationAsync(
        IPage page,
        List<string> headers,
        string cityName,
        string citySlug)
    {
        var aggregated = new List<Property>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan a few pages to find city-specific results when keyword search is broad
        for (var pageNumber = 1; pageNumber <= 10 && aggregated.Count < 12; pageNumber++)
        {
            try
            {
                var url = $"https://www.propertyfinder.ae/en/search?c=1&fu=0&ob=mr&page={pageNumber}";
                await page.GotoAsync(url, new()
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 30000
                });

                await Task.Delay(1000);
                var html = await page.ContentAsync();
                var pageResults = ExtractPropertiesFromNextData(html, headers, cityName, citySlug);

                foreach (var property in pageResults)
                {
                    var urlKey = property.Url ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(urlKey) || !seenUrls.Add(urlKey))
                        continue;

                    aggregated.Add(property);
                    if (aggregated.Count >= 12) break;
                }

                _logger.LogInformation(
                    "[PropertyFinder] Fallback page {Page} yielded {Count} city-matched listings (total: {Total})",
                    pageNumber,
                    pageResults.Count,
                    aggregated.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PropertyFinder] Pagination fallback failed on page {Page}", pageNumber);
            }
        }

        return aggregated;
    }

    // ── Ensure result location matches requested emirate ──
    private static bool IsLocationMatch(string location, string cityName, string citySlug)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;

        var normalizedLocation = location.ToLowerInvariant();
        var normalizedCity = cityName.ToLowerInvariant();
        var normalizedSlug = citySlug.Replace("-", " ").ToLowerInvariant();

        if (normalizedLocation.Contains(normalizedCity) || normalizedLocation.Contains(normalizedSlug))
            return true;

        // Handle common aliases
        return normalizedCity switch
        {
            "rak" or "rasalkhaimah" => normalizedLocation.Contains("ras al khaimah"),
            "abudhabi" => normalizedLocation.Contains("abu dhabi"),
            _ => false
        };
    }

    // ── Extract beds from text using regex ──
    private static int ExtractBedsFromText(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+)\s*(?:bed|br|bedroom)", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    // ── Extract baths from text using regex ──
    private static int ExtractBathsFromText(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+)\s*(?:bath|ba|bathroom)", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    // ── Extract area from text using regex ──
    private static int ExtractAreaFromText(string text)
    {
        // Match patterns like "1,200 sqft" or "100 sq.m"
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"([\d,]+)\s*(?:sq\.?\s*(?:ft|m)|sqft|sqm)", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        if (!match.Success) return 0;
        
        var numText = match.Groups[1].Value.Replace(",", "");
        if (!int.TryParse(numText, out var area)) return 0;
        
        // Convert sq.m to sq.ft if needed
        if (text.Contains("sq.m", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sqm", StringComparison.OrdinalIgnoreCase))
        {
            area = (int)(area * 10.764); // 1 sq.m = 10.764 sq.ft
        }
        
        return area;
    }

    // ── Parse "AED 120,000" or "120,000" → 120000 ──
    private static decimal ParsePrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        
        var clean = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(clean, out var price) ? price : 0;
    }

    // ── Parse "3 Beds" → 3 ──
    private static int ParseNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        
        var clean = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(clean, out var num) ? num : 0;
    }

    // ── Parse "1,200 sq ft" or "120 sq.m" → sqft ──
    private static int ParseArea(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        
        var clean = new string(text.Where(char.IsDigit).ToArray());
        if (!int.TryParse(clean, out var area)) return 0;

        // Convert sq.m to sq.ft if needed
        if (text.Contains("sq.m", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sqm", StringComparison.OrdinalIgnoreCase))
        {
            area = (int)(area * 10.764); // 1 sq.m = 10.764 sq.ft
        }

        return area;
    }

    // ── Detect property type from title/type text ──
    private static string DetectType(string title, string typeText)
    {
        var combined = $"{title} {typeText}".ToLower();

        if (combined.Contains("apartment") || combined.Contains("flat"))
            return "apartment";
        if (combined.Contains("villa") || combined.Contains("house"))
            return "house";
        if (combined.Contains("townhouse"))
            return "townhouse";
        if (combined.Contains("penthouse"))
            return "penthouse";
        if (combined.Contains("studio"))
            return "studio";

        return "apartment"; // default
    }

    // ── Extract city from keyword ──
    private static string ExtractCity(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return "dubai";

        var words = keyword.ToLower()
            .Split(new[] { ' ', ',', '-' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (CityMap.ContainsKey(word))
                return word;
        }

        // Default to Dubai
        return "dubai";
    }

    // ── Map city name to PropertyFinder slug ──
    private static string GetCitySlug(string city)
    {
        return CityMap.TryGetValue(city.ToLower(), out var slug) ? slug : "dubai";
    }

    // ── Read CSV headers ──
    private List<string> ReadCsvHeaders()
    {
        try
        {
            if (!File.Exists(_csvPath)) return new();
            
            var firstLine = File.ReadLines(_csvPath).First();
            return firstLine.Split(',').Select(h => h.Trim()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PropertyFinder] Failed to read CSV headers");
            return new();
        }
    }

    // ── Build Property object from header/value map ──
    private Property BuildProperty(List<string> headers, Dictionary<string, object?> data)
    {
        var prop = new Property();
        
        foreach (var header in headers)
        {
            if (!data.TryGetValue(header, out var value)) continue;
            
            var propInfo = typeof(Property).GetProperty(
                header, 
                System.Reflection.BindingFlags.IgnoreCase | 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.Instance);
            
            if (propInfo != null && propInfo.CanWrite)
            {
                try
                {
                    var convertedValue = Convert.ChangeType(value, 
                        Nullable.GetUnderlyingType(propInfo.PropertyType) ?? propInfo.PropertyType);
                    propInfo.SetValue(prop, convertedValue);
                }
                catch
                {
                    // Skip properties that can't be converted
                }
            }
        }
        
        return prop;
    }
}
