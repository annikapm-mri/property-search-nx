using System.Diagnostics;
using System.Text.RegularExpressions;
using FuzzySharp;
using FuzzySharp.SimilarityRatio;
using FuzzySharp.SimilarityRatio.Scorer.Composite;
using PropertySearch.Api.Agents.Base;
using PropertySearch.Api.Services;
using FuzzyProcess = FuzzySharp.Process;
namespace PropertySearch.Api.Agents;

// ── Input / Output ──
public class NlpInput
{
    public string RawQuery { get; set; } = "";
}

public class NlpOutput
{
    public ParsedPropertyQuery ParsedQuery { get; set; } = new();
    public string OriginalQuery { get; set; } = "";
    public string CorrectedQuery { get; set; } = "";
    public List<string> Corrections { get; set; } = new();
    public double ConfidenceScore { get; set; }
    public string Interpretation { get; set; } = "";
}
/// <summary>
/// Agent definition: .github/agents/nlp-parser-agent.md
/// Extracts structured search intent from natural language queries with spell correction.
/// </summary>
public class NlpParserAgent : IAgent<NlpInput, NlpOutput>
{
    public string AgentName        => "NLP Parser Agent";
    public string AgentDescription => "Extracts search intent from natural language with spell correction";

    private readonly ILogger<NlpParserAgent> _logger;

    // ── Full vocabulary for fuzzy matching ──
    private static readonly List<string> KnownWords = new()
    {
        // Property types
        "house", "apartment", "condo", "townhouse", "studio", "flat",
        // Bed/bath
        "bedroom", "bedrooms", "bed", "beds", "bath", "baths", "bathroom",
        // Amenities
        "pet", "pets", "friendly", "furnished", "laundry", "washer", "dryer",
        "parking", "garage", "pool", "gym", "balcony", "patio",
        // Price
        "under", "below", "above", "over", "affordable", "cheap", "luxury",
        "budget", "upscale", "premium", "expensive", "moderate",
        // Size
        "spacious", "cozy", "large", "small", "tiny", "big",
        // US Cities
        "austin", "miami", "denver", "seattle", "chicago", "houston",
        "boston", "dallas", "atlanta", "phoenix", "reno", "nashville",
        "portland", "tampa", "orlando", "charlotte", "sacramento",
        "losangeles", "newyork", "sanfrancisco", "sandiego", "lasvegas",
        // US States
        "california", "texas", "florida", "nevada", "colorado", "washington",
        "georgia", "illinois", "arizona", "tennessee"
    };

    public NlpParserAgent(ILogger<NlpParserAgent> logger)
    {
        _logger = logger;
    }

    public Task<AgentResult<NlpOutput>> ExecuteAsync(
        NlpInput input, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("[{Agent}] Processing: '{Query}'",
                AgentName, input.RawQuery);

            // ── Step 1: Normalize ──
            var normalized = Normalize(input.RawQuery);

            // ── Step 2: Spell correct ──
            var (corrected, corrections) = CorrectSpelling(normalized);

            // ── Step 3: Parse intent ──
            var parsed = CsvService.ParseKeyword(corrected);

            // ── Step 4: Score confidence ──
            var confidence = CalculateConfidence(parsed, corrections.Count);

            // ── Step 5: Build interpretation ──
            var interpretation = BuildInterpretation(parsed, corrections);

            sw.Stop();

            var output = new NlpOutput
            {
                ParsedQuery      = parsed,
                OriginalQuery    = input.RawQuery,
                CorrectedQuery   = corrected,
                Corrections      = corrections,
                ConfidenceScore  = confidence,
                Interpretation   = interpretation
            };

            _logger.LogInformation(
                "[{Agent}] Done in {Ms}ms — confidence: {Score:P0} — {Interpretation}",
                AgentName, sw.ElapsedMilliseconds, confidence, interpretation);

            return Task.FromResult(AgentResult<NlpOutput>.Ok(
                AgentName, output, interpretation,
                new Dictionary<string, object>
                {
                    ["executionMs"]     = sw.ElapsedMilliseconds,
                    ["confidence"]      = confidence,
                    ["corrections"]     = corrections.Count,
                    ["correctedQuery"]  = corrected
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Agent}] Failed", AgentName);
            return Task.FromResult(AgentResult<NlpOutput>.Fail(AgentName, ex.Message));
        }
    }

    // ── Normalize: lowercase, remove punctuation, collapse spaces ──
    private static string Normalize(string input)
    {
        var lower   = input.ToLower().Trim();
        var cleaned = Regex.Replace(lower, @"[^\w\s\-\$]", " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    // ── Intelligent spell correction using FuzzySharp ──
    private static (string corrected, List<string> corrections) CorrectSpelling(string input)
    {
        var words       = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var corrections = new List<string>();
        var corrected   = new List<string>();

        foreach (var word in words)
        {
            // Skip numbers, short words, prices
            if (word.Length <= 2 ||
                Regex.IsMatch(word, @"^\d+$") ||
                Regex.IsMatch(word, @"^\$?\d+(-\$?\d+)?$"))
            {
                corrected.Add(word);
                continue;
            }

            // Check if already a known word
            if (KnownWords.Contains(word))
            {
                corrected.Add(word);
                continue;
            }

            // Fuzzy match
            var best = FuzzyProcess.ExtractOne(
    word, KnownWords,
    scorer: ScorerCache.Get<WeightedRatioScorer>());

            if (best != null && best.Score >= 82 && best.Value != word)
            {
                corrections.Add($"'{word}'→'{best.Value}'");
                corrected.Add(best.Value);
            }
            else
            {
                corrected.Add(word);
            }
        }

        return (string.Join(" ", corrected), corrections);
    }

    // ── Score how confident we are in the parse ──
    private static double CalculateConfidence(ParsedPropertyQuery query, int correctionCount)
    {
        double score = 0.5; // base

        if (query.Beds.HasValue)                        score += 0.15;
        if (!string.IsNullOrEmpty(query.Type))          score += 0.15;
        if (!string.IsNullOrEmpty(query.Location))      score += 0.10;
        if (query.MaxPrice.HasValue)                    score += 0.10;
        if (query.MinPrice.HasValue)                    score += 0.05;
        if (query.PetsAllowed.HasValue)                 score += 0.05;
        if (query.Furnished.HasValue)                   score += 0.05;

        // Penalize for corrections needed
        score -= correctionCount * 0.05;

        return Math.Clamp(score, 0.1, 1.0);
    }

    // ── Build human readable interpretation ──
    private static string BuildInterpretation(
        ParsedPropertyQuery query, List<string> corrections)
    {
        var parts = new List<string>();

        if (query.Beds.HasValue)
            parts.Add(query.Beds == 0 ? "studio" : $"{query.Beds} bedroom");

        if (!string.IsNullOrEmpty(query.Type) && query.Type != "studio")
            parts.Add(query.Type);

        if (!string.IsNullOrEmpty(query.Location))
            parts.Add($"in {query.Location}");

        if (query.MaxPrice.HasValue)
            parts.Add($"under ${query.MaxPrice:N0}/mo");

        if (query.MinPrice.HasValue)
            parts.Add($"over ${query.MinPrice:N0}/mo");

        var amenities = new List<string>();
        if (query.PetsAllowed == true) amenities.Add("pet friendly");
        if (query.Furnished == true)   amenities.Add("furnished");
        if (query.Laundry == true)     amenities.Add("with laundry");

        if (amenities.Count > 0)
            parts.Add($"({string.Join(", ", amenities)})");

        var interpretation = parts.Count > 0
            ? $"Looking for a {string.Join(" ", parts)}"
            : "General property search";

        if (corrections.Count > 0)
            interpretation += $" — corrected: {string.Join(", ", corrections)}";

        return interpretation;
    }
}