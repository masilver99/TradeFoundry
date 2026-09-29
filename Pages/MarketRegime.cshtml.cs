using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class MarketRegimeModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly MarketProbabilityService _marketProbability;

    public MarketRegimeModel(TradeFoundryDb database, MarketProbabilityService marketProbability)
    {
        _database = database;
        _marketProbability = marketProbability;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public string Symbol { get; set; } = "ES/MES";
    [BindProperty(SupportsGet = true)] public int? Month { get; set; }
    [BindProperty(Name = "DayOfWeek", SupportsGet = true)] public string? DayOfWeekFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? VolatilityRegime { get; set; }
    [BindProperty(SupportsGet = true)] public string? OvernightDirection { get; set; }
    [BindProperty(SupportsGet = true)] public string? GapDirection { get; set; }
    [BindProperty(SupportsGet = true)] public string? StartDate { get; set; }
    [BindProperty(SupportsGet = true)] public string? EndDate { get; set; }
    [BindProperty(SupportsGet = true)] public string? AsOfDate { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? RangeGreaterThanPoints { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? RangeLessThanPoints { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? NormalizedRangeGreaterThan { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? NormalizedRangeLessThan { get; set; }

    public Journal? Journal { get; private set; }
    public MarketProbabilityQueryResult Result { get; private set; } = new();
    public MarketDaysResult RecentDays { get; private set; } = new();
    public string? ErrorMessage { get; private set; }
    public IReadOnlyList<string> Symbols { get; } = ["ES/MES", "ES", "MES"];
    public IReadOnlyList<VolatilityRegime> VolatilityRegimes { get; } = [
        Services.VolatilityRegime.Low,
        Services.VolatilityRegime.Normal,
        Services.VolatilityRegime.High,
        Services.VolatilityRegime.Extreme
    ];
    public IReadOnlyList<MarketDirection> Directions { get; } = [
        MarketDirection.Up,
        MarketDirection.Down,
        MarketDirection.Neutral
    ];

    public IActionResult OnGet()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        try
        {
            var query = BuildQuery();
            var snapshot = _marketProbability.GetPageSnapshot(JournalId, query, recentDayLimit: 50);
            Result = snapshot.Probabilities;
            RecentDays = snapshot.RecentDays;
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = exception.Message;
        }

        return Page();
    }

    public string FormatProbability(decimal? value) => value.HasValue ? value.Value.ToString("P1", CultureInfo.InvariantCulture) : "—";

    public string FormatNumber(decimal? value) => value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "—";

    private MarketProbabilityQuery BuildQuery() => new()
    {
        Symbol = string.IsNullOrWhiteSpace(Symbol) ? null : Symbol,
        Month = Month,
        DayOfWeek = ParseEnum<DayOfWeek>(DayOfWeekFilter, nameof(DayOfWeekFilter)),
        StartDate = ParseDate(StartDate, nameof(StartDate)),
        EndDate = ParseDate(EndDate, nameof(EndDate)),
        AsOfDate = ParseDate(AsOfDate, nameof(AsOfDate)),
        VolatilityRegime = ParseEnum<Services.VolatilityRegime>(VolatilityRegime, nameof(VolatilityRegime)),
        OvernightDirection = ParseDirection(OvernightDirection, nameof(OvernightDirection)),
        GapDirection = ParseDirection(GapDirection, nameof(GapDirection)),
        RangeGreaterThanPoints = RangeGreaterThanPoints,
        RangeLessThanPoints = RangeLessThanPoints,
        NormalizedRangeGreaterThan = NormalizedRangeGreaterThan,
        NormalizedRangeLessThan = NormalizedRangeLessThan
    };

    private static DateOnly? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"{name} must use YYYY-MM-DD format.");
    }

    private static T? ParseEnum<T>(string? value, string name) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"{name} is invalid.");
    }

    private static MarketDirection? ParseDirection(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
        return ParseEnum<MarketDirection>(value, name);
    }
}
