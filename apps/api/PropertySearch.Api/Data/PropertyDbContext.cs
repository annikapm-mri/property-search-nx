using Microsoft.EntityFrameworkCore;
using PropertySearch.Api.Models;

namespace PropertySearch.Api.Data;

public class PropertyDbContext : DbContext
{
    public PropertyDbContext(DbContextOptions<PropertyDbContext> options)
        : base(options) { }

    public DbSet<PropertyEntity> Properties => Set<PropertyEntity>();
    public DbSet<SearchLogEntity> SearchLogs => Set<SearchLogEntity>();
    public DbSet<AgentLogEntity> AgentLogs => Set<AgentLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── Property indexes for fast searching ──
        modelBuilder.Entity<PropertyEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.State);
            e.HasIndex(p => p.Type);
            e.HasIndex(p => p.Beds);
            e.HasIndex(p => p.Price);
            e.HasIndex(p => p.Region);
            // Composite index for common queries
            e.HasIndex(p => new { p.Beds, p.Type, p.Price });
            e.HasIndex(p => new { p.State, p.Type });
        });

        modelBuilder.Entity<SearchLogEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.SearchedAt);
            e.HasIndex(s => s.Keyword);
        });

        modelBuilder.Entity<AgentLogEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.AgentName);
            e.HasIndex(a => a.ExecutedAt);
        });
    }
}

// ── DB Entities ──
public class PropertyEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Url { get; set; } = "";
    public string Region { get; set; } = "";
    public decimal Price { get; set; }
    public string Type { get; set; } = "";
    public double SqFeet { get; set; }
    public int Beds { get; set; }
    public double Baths { get; set; }
    public int CatsAllowed { get; set; }
    public int DogsAllowed { get; set; }
    public int SmokingAllowed { get; set; }
    public int WheelchairAccess { get; set; }
    public int ComesFurnished { get; set; }
    public string LaundryOptions { get; set; } = "";
    public string ParkingOptions { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public double? Lat { get; set; }
    public double? Long { get; set; }
    public string State { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "csv"; // csv | scrape
    public double QualityScore { get; set; } = 1.0;
}

public class SearchLogEntity
{
    public int Id { get; set; }
    public string Keyword { get; set; } = "";
    public string ParsedIntent { get; set; } = "";
    public int ResultCount { get; set; }
    public DateTime SearchedAt { get; set; } = DateTime.UtcNow;
    public long ExecutionMs { get; set; }
}

public class AgentLogEntity
{
    public int Id { get; set; }
    public string AgentName { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public long ExecutionMs { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}