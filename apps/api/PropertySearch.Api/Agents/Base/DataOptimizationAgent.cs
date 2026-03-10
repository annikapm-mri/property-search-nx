using System.Diagnostics;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PropertySearch.Api.Agents.Base;
using PropertySearch.Api.Data;
using PropertySearch.Api.Models;
using PropertySearch.Api.Services;

namespace PropertySearch.Api.Agents;

// ── Input / Output ──
public class DataOptimizationInput
{
    public bool ForceRebuild { get; set; } = false;
    public bool MigrateFromCsv { get; set; } = true;
    public bool RebuildIndexes { get; set; } = true;
    public bool ClearCache { get; set; } = false;
}

public class DataOptimizationOutput
{
    public int TotalProperties { get; set; }
    public int MigratedFromCsv { get; set; }
    public int Duplicates { get; set; }
    public int IndexesBuilt { get; set; }
    public bool CacheWarmed { get; set; }
    public List<string> Steps { get; set; } = new();
}
/// <summary>
/// Agent definition: .github/agents/data-optimization-agent.md
/// Migrates CSV to SQLite, builds indexes, removes duplicates, warms cache.
/// </summary>
public class DataOptimizationAgent : IAgent<DataOptimizationInput, DataOptimizationOutput>
{
    public string AgentName        => "Data Optimization Agent";
    public string AgentDescription => "Migrates CSV to SQLite, builds indexes, warms cache";

    private readonly PropertyDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<DataOptimizationAgent> _logger;

    private static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
        SlidingExpiration = TimeSpan.FromMinutes(10),
        Priority = CacheItemPriority.High
    };

    public DataOptimizationAgent(
        PropertyDbContext db,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<DataOptimizationAgent> logger)
    {
        _db     = db;
        _cache  = cache;
        _config = config;
        _logger = logger;
    }

    public async Task<AgentResult<DataOptimizationOutput>> ExecuteAsync(
        DataOptimizationInput input, CancellationToken ct = default)
    {
        var sw     = Stopwatch.StartNew();
        var output = new DataOptimizationOutput();

        try
        {
            _logger.LogInformation("[{Agent}] Starting...", AgentName);

            // ── Step 1: Ensure DB schema exists ──
            await _db.Database.EnsureCreatedAsync(ct);
            output.Steps.Add("✅ Database schema ready");
            _logger.LogInformation("[{Agent}] DB schema ready", AgentName);

            // ── Step 2: Migrate CSV → SQLite ──
            if (input.MigrateFromCsv)
            {
                var migrated = await MigrateCsvAsync(input.ForceRebuild, ct);
                output.MigratedFromCsv = migrated;
                output.Steps.Add($"✅ Migrated {migrated} records from CSV");
                _logger.LogInformation("[{Agent}] Migrated {Count} records", AgentName, migrated);
            }

            // ── Step 3: Deduplicate ──
            var dupes = await DeduplicateAsync(ct);
            output.Duplicates = dupes;
            output.Steps.Add($"✅ Removed {dupes} duplicates");

            // ── Step 4: Total count ──
            output.TotalProperties = await _db.Properties.CountAsync(ct);
            output.Steps.Add($"✅ Total properties in DB: {output.TotalProperties}");

            // ── Step 5: Warm cache ──
            await WarmCacheAsync(ct);
            output.CacheWarmed = true;
            output.Steps.Add("✅ Cache warmed with common queries");

            // ── Step 6: Log this run ──
            sw.Stop();
            _db.AgentLogs.Add(new AgentLogEntity
            {
                AgentName   = AgentName,
                Success     = true,
                Message     = $"Migrated {output.MigratedFromCsv}, removed {output.Duplicates} dupes",
                ExecutionMs = sw.ElapsedMilliseconds
            });
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[{Agent}] Completed in {Ms}ms — {Total} total properties",
                AgentName, sw.ElapsedMilliseconds, output.TotalProperties);

            return AgentResult<DataOptimizationOutput>.Ok(
                AgentName, output,
                $"Optimization complete — {output.TotalProperties} properties ready",
                new Dictionary<string, object>
                {
                    ["executionMs"]  = sw.ElapsedMilliseconds,
                    ["totalRecords"] = output.TotalProperties,
                    ["migrated"]     = output.MigratedFromCsv,
                    ["duplicates"]   = output.Duplicates
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Agent}] Failed", AgentName);
            return AgentResult<DataOptimizationOutput>.Fail(AgentName, ex.Message);
        }
    }

    // ── Migrate CSV → SQLite ──
    private async Task<int> MigrateCsvAsync(bool force, CancellationToken ct)
    {
        var csvPath = _config["CsvPath"] ?? "Data/properties.csv";
        if (!File.Exists(csvPath)) return 0;

        // Skip if already migrated and not forcing
        if (!force && await _db.Properties.AnyAsync(ct))
        {
            _logger.LogInformation("[{Agent}] DB already has data — skipping migration", AgentName);
            return 0;
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
            BadDataFound = null,
            IgnoreBlankLines = true
        };

        using var reader = new StreamReader(csvPath);
        using var csv    = new CsvReader(reader, config);

        csv.Context.TypeConverterOptionsCache.GetOptions<double?>().NullValues.Add("");
        csv.Context.TypeConverterOptionsCache.GetOptions<double?>().NullValues.Add(" ");
        csv.Context.TypeConverterOptionsCache.GetOptions<decimal?>().NullValues.Add("");
        csv.Context.TypeConverterOptionsCache.GetOptions<int?>().NullValues.Add("");
        csv.Context.RegisterClassMap<PropertyMap>();

        var batch   = new List<PropertyEntity>();
        var count   = 0;
        const int batchSize = 500;

        while (await csv.ReadAsync())
        {
            try
            {
                var p = csv.GetRecord<Property>();
                if (p == null) continue;

                // Skip if already exists
                if (force || !await _db.Properties.AnyAsync(x => x.Id == p.Id, ct))
                {
                    batch.Add(MapToEntity(p));
                    count++;
                }

                // Save in batches for performance
                if (batch.Count >= batchSize)
                {
                    await _db.Properties.AddRangeAsync(batch, ct);
                    await _db.SaveChangesAsync(ct);
                    batch.Clear();
                    _logger.LogInformation("[{Agent}] Migrated {Count} so far...", AgentName, count);
                }
            }
            catch { /* skip bad rows */ }
        }

        // Save remaining
        if (batch.Count > 0)
        {
            await _db.Properties.AddRangeAsync(batch, ct);
            await _db.SaveChangesAsync(ct);
        }

        return count;
    }

    // ── Remove duplicate properties ──
    private async Task<int> DeduplicateAsync(CancellationToken ct)
{
    // ✅ Pull to memory first, then find duplicates client-side
    var allProperties = await _db.Properties
        .Select(p => new { p.Id, p.Region, p.Price, p.Beds, p.Type, p.CreatedAt })
        .ToListAsync(ct);

    var duplicateIds = allProperties
        .GroupBy(p => new { p.Region, p.Price, p.Beds, p.Type })
        .Where(g => g.Count() > 1)
        .SelectMany(g => g.OrderBy(p => p.CreatedAt).Skip(1))
        .Select(p => p.Id)
        .ToList();

    if (duplicateIds.Count == 0) return 0;

    // Delete in batches of 100 to avoid SQLite parameter limits
    var deleted = 0;
    foreach (var batch in duplicateIds.Chunk(100))
    {
        var toDelete = await _db.Properties
            .Where(p => batch.Contains(p.Id))
            .ToListAsync(ct);
        _db.Properties.RemoveRange(toDelete);
        await _db.SaveChangesAsync(ct);
        deleted += toDelete.Count;
    }

    return deleted;
}

    // ── Warm cache with most common queries ──
    private async Task WarmCacheAsync(CancellationToken ct)
    {
        var commonQueries = new[]
        {
            ("1bed_apartment",  1, "apartment"),
            ("2bed_apartment",  2, "apartment"),
            ("3bed_house",      3, "house"),
            ("2bed_house",      2, "house"),
            ("studio",          0, "studio"),
            ("1bed_condo",      1, "condo"),
        };

        foreach (var (key, beds, type) in commonQueries)
        {
            var results = await _db.Properties
                .Where(p => p.Beds == beds && p.Type.Contains(type))
                .OrderBy(p => p.Price)
                .Take(20)
                .ToListAsync(ct);

            _cache.Set($"query:{key}", results, CacheOptions);
        }

        // Cache total count
        var total = await _db.Properties.CountAsync(ct);
        _cache.Set("total_properties", total, CacheOptions);

        _logger.LogInformation("[{Agent}] Cache warmed with {Count} query sets",
            AgentName, commonQueries.Length);
    }

    private static PropertyEntity MapToEntity(Property p) => new()
    {
        Id              = p.Id,
        Url             = p.Url ?? "",
        Region          = p.Region ?? "",
        Price           = p.Price,
        Type            = p.Type ?? "",
        SqFeet          = p.SqFeet,
        Beds            = p.Beds,
        Baths           = p.Baths,
        CatsAllowed     = p.CatsAllowed ?? 0,
        DogsAllowed     = p.DogsAllowed ?? 0,
        SmokingAllowed  = p.SmokingAllowed ?? 0,
        WheelchairAccess = p.WheelchairAccess ?? 0,
        ComesFurnished  = p.ComesFurnished ?? 0,
        LaundryOptions  = p.LaundryOptions ?? "",
        ParkingOptions  = p.ParkingOptions ?? "",
        ImageUrl        = p.ImageUrl ?? "",
        Description     = p.Description ?? "",
        Lat             = p.Lat,
        Long            = p.Long,
        State           = p.State ?? "",
        Source          = "csv"
    };
}