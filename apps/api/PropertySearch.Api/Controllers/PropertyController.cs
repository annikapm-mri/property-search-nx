using Microsoft.AspNetCore.Mvc;
using PropertySearch.Api.Agents;
using PropertySearch.Api.Data;
using PropertySearch.Api.Models;
using PropertySearch.Api.Services;
using PropertySearch.Api.Validation;

namespace PropertySearch.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PropertyController : ControllerBase
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly ICsvService _csv;
    private readonly IScraperService _scraper;
    private readonly ILogger<PropertyController> _logger;

    public PropertyController(
        AgentOrchestrator orchestrator,
        ICsvService csv,
        IScraperService scraper,
        ILogger<PropertyController> logger)
    {
        _orchestrator = orchestrator;
        _csv          = csv;
        _scraper      = scraper;
        _logger       = logger;
    }

    [HttpGet("/api/health")]
    public ActionResult Health() => Ok(new
    {
        status    = "healthy",
        timestamp = DateTime.UtcNow,
        version   = "1.0.0"
    });

    [HttpGet("search")]
    public async Task<ActionResult<SearchResult>> Search(
        [FromQuery] string keyword,
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 12)
    {
        var (valid, errors) = SearchValidation.ValidateKeyword(keyword);
        if (!valid) return BadRequest(new { errors });

        // ── NLP Agent parses the query ──
        var nlpOutput = await _orchestrator.ParseSearchQueryAsync(keyword);

        // ── Search SQLite via orchestrator ──
        var dbResults = await _orchestrator.SearchDatabaseAsync(
            nlpOutput.ParsedQuery, page, pageSize);

        // ── Fallback to CSV if DB is empty ──
        List<Property> properties;
        int total;

        if (dbResults.Count > 0)
        {
            properties = dbResults.Select(MapToProperty).ToList();
            total      = dbResults.Count;
        }
        else
        {
            properties = _csv.SearchWithQuery(nlpOutput.ParsedQuery);
            total      = properties.Count;
        }

        return Ok(new SearchResult
        {
            Source         = "database",
            Properties     = properties,
            Total          = total,
            Page           = page,
            PageSize       = pageSize,
            Interpretation = nlpOutput.Interpretation
        });
    }

    [HttpGet("scrape")]
    public async Task<ActionResult<SearchResult>> Scrape([FromQuery] string keyword)
    {
        var (valid, errors) = SearchValidation.ValidateKeyword(keyword);
        if (!valid) return BadRequest(new { errors });
        var normalizedKeyword = keyword.ToLower().Trim();
        var scraped = await _scraper.ScrapePropertiesAsync(normalizedKeyword);
        if (scraped.Count > 0) _csv.AppendProperties(scraped);

        return Ok(new SearchResult
        {
            Source         = "web_scrape",
            Properties     = scraped,
            Total          = scraped.Count,
            Page           = 1,
            PageSize       = scraped.Count,
            Interpretation = $"HUD Fair Market Rent data for: {keyword}"
        });
    }

    [HttpPost("optimize")]
    public async Task<ActionResult> Optimize([FromQuery] bool force = false)
    {
        var result = await _orchestrator.OptimizeDataAsync(force);
        return Ok(result);
    }

    private static Property MapToProperty(PropertyEntity e) => new()
    {
        Id          = e.Id,
        Url         = e.Url,
        Region      = e.Region,
        Price       = e.Price,
        Type        = e.Type,
        SqFeet      = e.SqFeet,
        Beds        = e.Beds,
        Baths       = e.Baths,
        ImageUrl    = e.ImageUrl,
        Description = e.Description,
        State       = e.State,
        Lat         = e.Lat,
        Long        = e.Long
    };
    [HttpGet("recommend")]
public async Task<ActionResult> Recommend(
    [FromQuery] string keyword,
    [FromQuery] int limit = 5)
{
    var (valid, errors) = SearchValidation.ValidateKeyword(keyword);
    if (!valid) return BadRequest(new { errors });

    // NLP agent parses intent
    var nlpOutput = await _orchestrator.ParseSearchQueryAsync(keyword);

    // Get results from WEB SCRAPER (real external data)
    _logger.LogInformation("[Recommend] Scraping web for: {Keyword}", keyword);
    var webResults = await _scraper.ScrapePropertiesAsync(keyword);
    _logger.LogInformation("[Recommend] Got {Count} results from web scraper", webResults.Count);

    // Also get results from DB (fallback if scraper returns nothing)
    var dbResults = await _orchestrator.SearchDatabaseAsync(
        nlpOutput.ParsedQuery, page: 1, pageSize: 20);
    _logger.LogInformation("[Recommend] Got {Count} results from database", dbResults.Count);

    // Combine results: prioritize web results, fill with DB results if needed
    var combinedResults = new List<PropertyEntity>();
    
    // Convert web results to PropertyEntity
    combinedResults.AddRange(webResults.Select(p => new PropertyEntity
    {
        Id = p.Id,
        Url = p.Url,
        Region = p.Region,
        Price = p.Price,
        Type = p.Type,
        SqFeet = p.SqFeet,
        Beds = p.Beds,
        Baths = p.Baths,
        CatsAllowed = p.CatsAllowed ?? 0,
        DogsAllowed = p.DogsAllowed ?? 0,
        SmokingAllowed = p.SmokingAllowed ?? 0,
        WheelchairAccess = p.WheelchairAccess ?? 0,
        ComesFurnished = p.ComesFurnished ?? 0,
        LaundryOptions = p.LaundryOptions ?? "",
        ParkingOptions = p.ParkingOptions ?? "",
        ImageUrl = p.ImageUrl,
        Description = p.Description,
        Lat = p.Lat,
        Long = p.Long,
        State = p.State,
        Source = "web"
    }));

    // Add DB results if we don't have enough web results
    if (combinedResults.Count < limit)
    {
        var needed = limit - combinedResults.Count;
        combinedResults.AddRange(dbResults.Take(needed));
    }

    // Score and rank results
    var scored = combinedResults
        .Where(p => p.Price > 0)
        .Select(p => new
        {
            property   = p,
            score      = ScoreProperty(p, nlpOutput.ParsedQuery),
            reasons    = GetMatchReasons(p, nlpOutput.ParsedQuery)
        })
        .OrderByDescending(x => x.score)
        .Take(limit)
        .ToList();

    _logger.LogInformation("[Recommend] Returning {Count} scored results", scored.Count);

    return Ok(new
    {
        interpretation = nlpOutput.Interpretation,
        recommendations = scored.Select(x => new
        {
            property    = x.property,
            score       = x.score,
            matchReasons = x.reasons,
            explanation = $"This {x.property.Type} scores {x.score}/100 because: {string.Join(", ", x.reasons)}"
        })
    });
}

private static int ScoreProperty(PropertyEntity p, ParsedPropertyQuery q)
{
    int score = 0;

    if (q.Beds.HasValue && p.Beds == q.Beds.Value)          score += 30;
    else if (q.Beds.HasValue && Math.Abs(p.Beds - q.Beds.Value) == 1) score += 10;

    if (!string.IsNullOrEmpty(q.Type) && p.Type.Contains(q.Type)) score += 25;

    if (q.MaxPrice.HasValue && p.Price <= q.MaxPrice.Value)  score += 20;
    else if (q.MaxPrice.HasValue && p.Price <= q.MaxPrice.Value * 1.1m) score += 10;

    if (!string.IsNullOrEmpty(q.Location) &&
        (p.Region.Contains(q.Location, StringComparison.OrdinalIgnoreCase) ||
         p.State.Contains(q.Location, StringComparison.OrdinalIgnoreCase))) score += 20;

    if (q.PetsAllowed == true && (p.CatsAllowed == 1 || p.DogsAllowed == 1)) score += 10;
    if (q.Furnished == true && p.ComesFurnished == 1)        score += 10;
    if (q.Laundry == true && !string.IsNullOrEmpty(p.LaundryOptions)) score += 10;

    if (!string.IsNullOrEmpty(p.ImageUrl))                   score += 5;
    if (!string.IsNullOrEmpty(p.Description))                score += 5;

    return Math.Min(score, 100);
}

private static List<string> GetMatchReasons(PropertyEntity p, ParsedPropertyQuery q)
{
    var reasons = new List<string>();

    if (q.Beds.HasValue && p.Beds == q.Beds.Value)
        reasons.Add($"Exact {p.Beds} bedroom match");

    if (!string.IsNullOrEmpty(q.Type) && p.Type.Contains(q.Type))
        reasons.Add($"Matches type: {p.Type}");

    if (q.MaxPrice.HasValue && p.Price <= q.MaxPrice.Value)
        reasons.Add($"Within budget at ${p.Price}/mo");

    if (!string.IsNullOrEmpty(q.Location) &&
        p.Region.Contains(q.Location, StringComparison.OrdinalIgnoreCase))
        reasons.Add($"Located in {q.Location}");

    if (q.PetsAllowed == true && (p.CatsAllowed == 1 || p.DogsAllowed == 1))
        reasons.Add("Pet friendly");

    if (q.Furnished == true && p.ComesFurnished == 1)
        reasons.Add("Furnished");

    if (q.Laundry == true && !string.IsNullOrEmpty(p.LaundryOptions))
        reasons.Add("Has laundry");

    if (reasons.Count == 0)
        reasons.Add("General match");

    return reasons;
}
}

// ── Response model ──
public class SearchResult
{
    public string Source { get; set; } = "";
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string Interpretation { get; set; } = "";
    public List<Property> Properties { get; set; } = new();
}
