using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text.RegularExpressions;
using PropertySearch.Api.Models;

namespace PropertySearch.Api.Services;

public class PropertyMap : ClassMap<Property>
{
    public PropertyMap()
    {
        Map(p => p.Id).Name("id");
        Map(p => p.Url).Name("url");
        Map(p => p.Region).Name("region");
        Map(p => p.Price).Name("price");
        Map(p => p.Type).Name("type");
        Map(p => p.SqFeet).Name("sqfeet");
        Map(p => p.Beds).Name("beds");
        Map(p => p.Baths).Name("baths");
        Map(p => p.CatsAllowed).Name("cats_allowed");
        Map(p => p.DogsAllowed).Name("dogs_allowed");
        Map(p => p.SmokingAllowed).Name("smoking_allowed");
        Map(p => p.WheelchairAccess).Name("wheelchair_access");
        Map(p => p.ComesFurnished).Name("comes_furnished");
        Map(p => p.LaundryOptions).Name("laundry_options");
        Map(p => p.ParkingOptions).Name("parking_options");
        Map(p => p.ImageUrl).Name("image_url");
        Map(p => p.Description).Name("description");
        Map(p => p.Lat).Name("lat");
        Map(p => p.Long).Name("long");
        Map(p => p.State).Name("state");
    }
}

public class ParsedPropertyQuery
{
    public int? Beds { get; set; }
    public int? MaxBeds { get; set; }
    public double? Baths { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public string? Type { get; set; }
    public string? Location { get; set; }
    public string? State { get; set; }
    public bool? PetsAllowed { get; set; }
    public bool? Furnished { get; set; }
    public bool? Laundry { get; set; }
    public List<string> Keywords { get; set; } = new();
    public string ClaudeInterpretation { get; set; } = "";
}

public interface ICsvService
{
    List<Property> SearchProperties(string keyword);
    List<Property> SearchWithQuery(ParsedPropertyQuery query);
    void AppendProperties(List<Property> properties);
}

public class CsvService : ICsvService
{
    private readonly string _csvPath;

    public CsvService(IConfiguration config)
    {
        _csvPath = config["CsvPath"] ?? "Data/properties.csv";
    }

    private List<Property> ReadAllRecords()
    {
        if (!File.Exists(_csvPath)) return new List<Property>();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
            BadDataFound = null,
            IgnoreBlankLines = true
        };

        using var reader = new StreamReader(_csvPath);
        using var csv = new CsvReader(reader, config);

        csv.Context.TypeConverterOptionsCache.GetOptions<double?>().NullValues.Add("");
        csv.Context.TypeConverterOptionsCache.GetOptions<double?>().NullValues.Add(" ");
        csv.Context.TypeConverterOptionsCache.GetOptions<decimal?>().NullValues.Add("");
        csv.Context.TypeConverterOptionsCache.GetOptions<int?>().NullValues.Add("");
        csv.Context.RegisterClassMap<PropertyMap>();

        var records = new List<Property>();
        while (csv.Read())
        {
            try
            {
                var record = csv.GetRecord<Property>();
                if (record != null) records.Add(record);
            }
            catch { /* skip bad rows */ }
        }
        return records;
    }

    public List<Property> SearchProperties(string keyword)
    {
        var records = ReadAllRecords();
        var parsed = ParseKeyword(keyword);
        return records.Where(p => MatchesFilters(p, parsed)).Take(20).ToList();
    }

    public List<Property> SearchWithQuery(ParsedPropertyQuery query)
    {
        var records = ReadAllRecords();
        return records.Where(p => MatchesQuery(p, query)).Take(20).ToList();
    }

    public void AppendProperties(List<Property> properties)
    {
        if (!File.Exists(_csvPath)) return;

        List<string> headers;
        using (var headerReader = new StreamReader(_csvPath))
        {
            var line = headerReader.ReadLine() ?? "";
            headers = line.Split(',')
                .Select(h => h.Trim().Trim('"').ToLower())
                .ToList();
        }

        using var writer = new StreamWriter(_csvPath, append: true);

        foreach (var p in properties)
        {
            var values = new Dictionary<string, string>
            {
                ["id"]                      = p.Id,
                ["url"]                     = p.Url ?? "",
                ["region"]                  = p.Region ?? "",
                ["region_url"]              = "",
                ["price"]                   = p.Price.ToString(),
                ["type"]                    = p.Type ?? "",
                ["sqfeet"]                  = p.SqFeet.ToString(),
                ["beds"]                    = p.Beds.ToString(),
                ["baths"]                   = p.Baths.ToString(),
                ["cats_allowed"]            = p.CatsAllowed?.ToString() ?? "0",
                ["dogs_allowed"]            = p.DogsAllowed?.ToString() ?? "0",
                ["smoking_allowed"]         = p.SmokingAllowed?.ToString() ?? "0",
                ["wheelchair_access"]       = p.WheelchairAccess?.ToString() ?? "0",
                ["electric_vehicle_charge"] = "0",
                ["comes_furnished"]         = p.ComesFurnished?.ToString() ?? "0",
                ["laundry_options"]         = p.LaundryOptions ?? "",
                ["parking_options"]         = p.ParkingOptions ?? "",
                ["image_url"]               = p.ImageUrl ?? "",
                ["description"]             = $"\"{(p.Description ?? "").Replace("\"", "\"\"")}\"",
                ["lat"]                     = p.Lat?.ToString() ?? "",
                ["long"]                    = p.Long?.ToString() ?? "",
                ["state"]                   = p.State ?? ""
            };

            var row = headers.Select(h => values.TryGetValue(h, out var v) ? v : "");
            writer.WriteLine(string.Join(",", row));
        }
    }

    // ── Natural language parser ──
    public static ParsedPropertyQuery ParseKeyword(string keyword)
    {
        var query = new ParsedPropertyQuery
        {
            ClaudeInterpretation = $"Searching for: {keyword}"
        };
        var lower = keyword.ToLower().Trim();
        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            var word = words[i];

            // Beds: "3 bed", "3br", "3bedroom"
            if (Regex.IsMatch(word, @"^\d+$") && i + 1 < words.Length &&
                (words[i + 1].StartsWith("bed") || words[i + 1].StartsWith("br")))
            { query.Beds = int.Parse(word); i++; continue; }

            var bedMatch = Regex.Match(word, @"^(\d+)(br|bed|beds|bedroom|bedrooms)$");
            if (bedMatch.Success)
            { query.Beds = int.Parse(bedMatch.Groups[1].Value); continue; }

            // Studio
            if (word == "studio") { query.Beds = 0; query.Type = "studio"; continue; }

            // Size descriptors
            if (word is "cozy" or "small" or "tiny") { query.MaxBeds = 1; continue; }
            if (word is "spacious" or "large" or "big") { query.Beds ??= 2; continue; }

            // Budget descriptors
            if (word is "affordable" or "cheap" or "budget") { query.MaxPrice = 1200; continue; }
            if (word is "luxury" or "upscale" or "premium") { query.MinPrice = 2500; continue; }
            if (word is "mid-range" or "moderate") { query.MinPrice = 1000; query.MaxPrice = 2500; continue; }

            // Price: "under 2000", "below 1500"
            if (word is "under" or "below" or "max" or "maximum" && i + 1 < words.Length)
            {
                var priceStr = new string(words[i + 1].Where(c => char.IsDigit(c) || c == '.').ToArray());
                if (decimal.TryParse(priceStr, out var maxP)) { query.MaxPrice = maxP; i++; continue; }
            }

            // Price: "over 1000", "above 800"
            if (word is "over" or "above" or "min" or "minimum" && i + 1 < words.Length)
            {
                var priceStr = new string(words[i + 1].Where(c => char.IsDigit(c) || c == '.').ToArray());
                if (decimal.TryParse(priceStr, out var minP)) { query.MinPrice = minP; i++; continue; }
            }

            // Price range: "1000-2000"
            var rangeMatch = Regex.Match(word, @"^\$?(\d+)-\$?(\d+)$");
            if (rangeMatch.Success)
            {
                query.MinPrice = decimal.Parse(rangeMatch.Groups[1].Value);
                query.MaxPrice = decimal.Parse(rangeMatch.Groups[2].Value);
                continue;
            }

            // Standalone price: "$1500"
            var priceMatch = Regex.Match(word, @"^\$(\d+)$");
            if (priceMatch.Success) { query.MaxPrice = decimal.Parse(priceMatch.Groups[1].Value); continue; }

            // Property types
            if (word is "house" or "home" or "sfr") { query.Type = "house"; continue; }
            if (word is "apartment" or "apt" or "flat") { query.Type = "apartment"; continue; }
            if (word is "condo" or "condominium") { query.Type = "condo"; continue; }
            if (word is "townhouse" or "townhome") { query.Type = "townhouse"; continue; }

            // Amenities
            if (word is "pet" or "pets" or "cats" or "dogs" or "dog" or "cat" or "petfriendly")
            { query.PetsAllowed = true; continue; }

            if (word is "furnished" or "furnish")
            { query.Furnished = true; continue; }

            if (word is "laundry" or "washer" or "dryer" or "w/d")
            { query.Laundry = true; continue; }

            // Skip filler words
            if (word is "a" or "an" or "the" or "in" or "at" or "for" or "with"
                     or "and" or "or" or "near" or "close" or "bedroom" or "bedrooms"
                     or "bath" or "baths" or "friendly" or "looking" or "want"
                     or "need" or "find" or "me" or "i" or "please" or "to")
                continue;

            // Everything else → location/keyword
            query.Keywords.Add(word);
        }

        if (query.Keywords.Count > 0 && query.Location == null)
            query.Location = string.Join(" ", query.Keywords);

        // Build human-readable interpretation
        var parts = new List<string>();
        if (query.Beds.HasValue) parts.Add($"{query.Beds} bedroom");
        if (!string.IsNullOrEmpty(query.Type)) parts.Add(query.Type);
        if (!string.IsNullOrEmpty(query.Location)) parts.Add($"in {query.Location}");
        if (query.MaxPrice.HasValue) parts.Add($"under ${query.MaxPrice}");
        if (query.MinPrice.HasValue) parts.Add($"over ${query.MinPrice}");
        if (query.PetsAllowed == true) parts.Add("pet friendly");
        if (query.Furnished == true) parts.Add("furnished");
        if (query.Laundry == true) parts.Add("with laundry");

        query.ClaudeInterpretation = parts.Count > 0
            ? $"Looking for a {string.Join(" ", parts)}"
            : $"Searching for: {keyword}";

        return query;
    }

    private static bool MatchesFilters(Property p, ParsedPropertyQuery query)
    {
        if (p.Price <= 0) return false;
        if (query.Beds.HasValue && p.Beds != query.Beds.Value) return false;
        if (query.MaxBeds.HasValue && p.Beds > query.MaxBeds.Value) return false;
        if (query.MinPrice.HasValue && p.Price < query.MinPrice.Value) return false;
        if (query.MaxPrice.HasValue && p.Price > query.MaxPrice.Value) return false;

        if (!string.IsNullOrEmpty(query.Type))
        {
            var pType = p.Type?.ToLower() ?? "";
            var match = query.Type switch
            {
                "house"     => pType.Contains("house") || pType.Contains("home"),
                "apartment" => pType.Contains("apart") || pType.Contains("apt") || pType.Contains("flat"),
                "condo"     => pType.Contains("condo"),
                "townhouse" => pType.Contains("town"),
                "studio"    => pType.Contains("studio") || p.Beds == 0,
                _           => pType.Contains(query.Type)
            };
            if (!match) return false;
        }

        if (!string.IsNullOrEmpty(query.Location))
        {
            var loc      = query.Location.ToLower();
            var inRegion = p.Region?.ToLower().Contains(loc) == true;
            var inState  = p.State?.ToLower().Contains(loc) == true;
            var inDesc   = p.Description?.ToLower().Contains(loc) == true;
            if (!inRegion && !inState && !inDesc) return false;
        }

        if (query.PetsAllowed == true && p.CatsAllowed != 1 && p.DogsAllowed != 1) return false;
        if (query.Furnished == true && p.ComesFurnished != 1) return false;
        if (query.Laundry == true && string.IsNullOrEmpty(p.LaundryOptions)) return false;

        return true;
    }

    private static bool MatchesQuery(Property p, ParsedPropertyQuery query)
        => MatchesFilters(p, query); // same logic
}