using PropertySearch.Api.Agents;
using PropertySearch.Api.Agents.Base;
using PropertySearch.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace PropertySearch.Api.Services;
/// <summary>
/// Coordinates all agents in the property search pipeline.
/// Agents: NlpParserAgent → DataOptimizationAgent → (future: RecommendationAgent, DataQualityAgent)
/// </summary>
// ── Coordinates all agents ──
public class AgentOrchestrator
{
    private readonly NlpParserAgent _nlp;
    private readonly DataOptimizationAgent _dataOpt;
    private readonly PropertyDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        NlpParserAgent nlp,
        DataOptimizationAgent dataOpt,
        PropertyDbContext db,
        IMemoryCache cache,
        ILogger<AgentOrchestrator> logger)
    {
        _nlp     = nlp;
        _dataOpt = dataOpt;
        _db      = db;
        _cache   = cache;
        _logger  = logger;
    }

    // ── Called on every search ──
    public async Task<NlpOutput> ParseSearchQueryAsync(string keyword)
    {
        var result = await _nlp.ExecuteAsync(new NlpInput { RawQuery = keyword });

        // Log the search
        _db.SearchLogs.Add(new SearchLogEntity
        {
            Keyword       = keyword,
            ParsedIntent  = result.Data?.Interpretation ?? "",
            ResultCount   = 0,
            ExecutionMs   = (long)(result.ExecutionTime.TotalMilliseconds)
        });
        await _db.SaveChangesAsync();

        return result.Data ?? new NlpOutput
        {
            ParsedQuery      = CsvService.ParseKeyword(keyword),
            Interpretation   = $"Searching for: {keyword}"
        };
    }

    // ── Search using SQLite instead of CSV ──
    public async Task<List<PropertyEntity>> SearchDatabaseAsync(
        ParsedPropertyQuery query, int page = 1, int pageSize = 12)
    {
        // Check cache first
        var cacheKey = BuildCacheKey(query, page);
        if (_cache.TryGetValue(cacheKey, out List<PropertyEntity>? cached) && cached != null)
        {
            _logger.LogInformation("Cache hit for: {Key}", cacheKey);
            return cached;
        }

        // Build dynamic query
        var dbQuery = _db.Properties.AsQueryable();
dbQuery = dbQuery.Where(p => p.Price > 0);
        if (query.Beds.HasValue)
            dbQuery = dbQuery.Where(p => p.Beds == query.Beds.Value);

        if (query.MaxBeds.HasValue)
            dbQuery = dbQuery.Where(p => p.Beds <= query.MaxBeds.Value);

        if (query.MinPrice.HasValue)
            dbQuery = dbQuery.Where(p => p.Price >= query.MinPrice.Value);

        if (query.MaxPrice.HasValue)
            dbQuery = dbQuery.Where(p => p.Price <= query.MaxPrice.Value);

        if (!string.IsNullOrEmpty(query.Type))
            dbQuery = dbQuery.Where(p => p.Type.Contains(query.Type));

        if (!string.IsNullOrEmpty(query.Location))
            dbQuery = dbQuery.Where(p =>
                p.Region.Contains(query.Location) ||
                p.State.Contains(query.Location));

        if (!string.IsNullOrEmpty(query.State))
            dbQuery = dbQuery.Where(p => p.State == query.State);

        if (query.PetsAllowed == true)
            dbQuery = dbQuery.Where(p => p.CatsAllowed == 1 || p.DogsAllowed == 1);

        if (query.Furnished == true)
            dbQuery = dbQuery.Where(p => p.ComesFurnished == 1);

        if (query.Laundry == true)
            dbQuery = dbQuery.Where(p => p.LaundryOptions != "");

        var results = await dbQuery
            .OrderBy(p => p.Price)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // Cache the result
        _cache.Set(cacheKey, results, TimeSpan.FromMinutes(5));

        return results;
    }

    // ── Run data optimization (call on startup or manually) ──
    public async Task<DataOptimizationOutput?> OptimizeDataAsync(bool force = false)
    {
        var result = await _dataOpt.ExecuteAsync(new DataOptimizationInput
        {
            ForceRebuild  = force,
            MigrateFromCsv = true,
            RebuildIndexes = true
        });

        return result.Data;
    }

    private static string BuildCacheKey(ParsedPropertyQuery q, int page) =>
        $"search:{q.Beds}:{q.Type}:{q.Location}:{q.MinPrice}:{q.MaxPrice}:{q.PetsAllowed}:{page}";
        // ── Called on restarts when DB already has data ──
public async Task WarmCacheOnlyAsync()
{
    _logger.LogInformation("🔥 Warming cache only (DB already populated)...");

    var commonQueries = new[]
    {
        ("1bed_apartment", 1, "apartment"),
        ("2bed_apartment", 2, "apartment"),
        ("3bed_house",     3, "house"),
        ("2bed_house",     2, "house"),
        ("studio",         0, "studio"),
        ("1bed_condo",     1, "condo"),
    };

    foreach (var (key, beds, type) in commonQueries)
    {
        var results = await _db.Properties
            .Where(p => p.Beds == beds && p.Type.Contains(type))
            .OrderBy(p => p.Price)
            .Take(20)
            .ToListAsync();

        _cache.Set($"query:{key}", results, TimeSpan.FromMinutes(30));
    }

    var total = await _db.Properties.CountAsync();
    _cache.Set("total_properties", total, TimeSpan.FromMinutes(30));

    _logger.LogInformation("✅ Cache warmed — {Total} properties in DB", total);
}
}
