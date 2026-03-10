using Microsoft.EntityFrameworkCore;
using PropertySearch.Api.Agents;
using PropertySearch.Api.Data;
using PropertySearch.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Database ──
builder.Services.AddDbContext<PropertyDbContext>(options =>
    options.UseSqlite("Data Source=Data/properties.db"));

// ── Caching ──
builder.Services.AddMemoryCache();

// ── Services ──
builder.Services.AddScoped<ICsvService, CsvService>();
// ── Main Scraper Service ──
builder.Services.AddScoped<IScraperService, PropertyFinderScraperService>();

// ── Agents ──
builder.Services.AddScoped<NlpParserAgent>();
builder.Services.AddScoped<DataOptimizationAgent>();
builder.Services.AddScoped<AgentOrchestrator>();

// ── CORS ──
builder.Services.AddCors(options =>
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(
            "http://localhost:4200",
            "http://localhost:5173",
            "http://localhost:4201")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

// ── Auto-run Data Optimization Agent on startup ──
using (var scope = app.Services.CreateScope())
{
    var db           = scope.ServiceProvider.GetRequiredService<PropertyDbContext>();
    var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
    var logger       = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    await db.Database.EnsureCreatedAsync();
    var count = await db.Properties.CountAsync();

    logger.LogInformation("🤖 DB has {Count} properties", count);

    if (count == 0)
    {
        logger.LogInformation("🤖 DB empty — running full optimization...");
        var result = await orchestrator.OptimizeDataAsync(force: false);
        logger.LogInformation("✅ DB ready — {Total} properties loaded",
            result?.TotalProperties ?? 0);
    }
    else
    {
        logger.LogInformation("✅ DB already has {Count} properties — warming cache only", count);
        await orchestrator.WarmCacheOnlyAsync();
    }
}

app.UseCors("AllowFrontend");
app.UseCors("AllowFrontend");
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

app.Run();