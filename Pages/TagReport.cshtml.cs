using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class TagReportModel : PageModel
{
    public const int PageSize = 50;

    public static readonly IReadOnlyList<(string Value, string Label)> Sorts =
    [
        ("rank", "Ranking"), ("count", "Sample size"), ("win", "Win rate"),
        ("average", "Expectancy"), ("total", "Total P&L"), ("factor", "Profit factor"), ("lift", "Lift")
    ];

    private readonly TradeFoundryDb _database;

    public TagReportModel(TradeFoundryDb database) => _database = database;

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    /// <summary>trades or days.</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>r, money, or empty to choose by risk coverage.</summary>
    [BindProperty(SupportsGet = true)] public string? Metric { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public List<string> Pick { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    [BindProperty(SupportsGet = true, Name = "pageNumber")] public int PageNumber { get; set; } = 1;

    public Journal Journal { get; private set; } = new();
    public JournalFilterScope Filter { get; private set; } = JournalFilter.None.Scope(null);
    public TagPerformanceReport Report { get; private set; } = null!;
    public IReadOnlyList<TagPerformanceRow> Rows { get; private set; } = Array.Empty<TagPerformanceRow>();
    public TagLookup? Lookup { get; private set; }
    public IReadOnlyList<TagObservation> Shown { get; private set; } = Array.Empty<TagObservation>();
    public int ShownTotal { get; private set; }
    public int PageCount => (int)Math.Ceiling(ShownTotal / (double)PageSize);

    public bool IsDays => Tab == "days";
    public string Noun => IsDays ? "days" : "trades";

    public IActionResult OnGet()
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null) return NotFound();
        Journal = journal;
        Filter = JournalFilterRequest.Resolve(HttpContext, journal);
        if (Tab != "days") Tab = "trades";
        if (Metric is not ("r" or "money")) Metric = null;
        if (!Sorts.Any(item => item.Value == Sort)) Sort = "rank";

        Report = TagPerformanceReport.Load(_database, journal, Filter, IsDays ? TagTarget.Day : TagTarget.Trade, Metric);
        Rows = Sort switch
        {
            "count" => Report.Rows.OrderByDescending(row => row.Sample.Count).ToArray(),
            "win" => Report.Rows.OrderByDescending(row => row.Sample.WinRate).ToArray(),
            "average" => Report.Rows.OrderByDescending(row => row.Sample.Value(Report.UsesR).Mean).ToArray(),
            "total" => Report.Rows.OrderByDescending(row => row.Sample.TotalPnl).ToArray(),
            // No losses means an unbounded profit factor.
            "factor" => Report.Rows.OrderByDescending(row => row.Sample.ProfitFactor ?? (row.Sample.Wins > 0 ? decimal.MaxValue : decimal.MinValue)).ToArray(),
            "lift" => Report.Rows.OrderByDescending(row => row.ValueLift).ToArray(),
            _ => Report.Rows
        };

        Lookup = Report.Lookup(Report.ParsePicks(Pick));
        if (Lookup is { } lookup)
        {
            var items = Report.Show(lookup, Show);
            ShownTotal = items.Count;
            PageNumber = Math.Clamp(PageNumber, 1, Math.Max(1, PageCount));
            Shown = items.OrderByDescending(item => item.Order).Skip((PageNumber - 1) * PageSize).Take(PageSize).ToArray();
        }
        return Page();
    }

    /// <summary>A link within the report that keeps the filter, unit and sort.</summary>
    public string Link(string? tab = null, string? metric = null, IEnumerable<string>? picks = null, string? show = null, int page = 1)
    {
        var values = new List<KeyValuePair<string, string?>> { new("tab", tab ?? Tab) };
        var chosenMetric = metric ?? Metric;
        if (chosenMetric is "r" or "money") values.Add(new("metric", chosenMetric));
        if (Sort is { } sort && sort != "rank") values.Add(new("sort", sort));
        values.AddRange(Filter.Filter.ToQuery().Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)));
        foreach (var pick in picks ?? Array.Empty<string>()) values.Add(new("pick", pick));
        if (!string.IsNullOrEmpty(show)) values.Add(new("show", show));
        if (page > 1) values.Add(new("pageNumber", page.ToString(CultureInfo.InvariantCulture)));
        return QueryHelpers.AddQueryString($"/journal/{JournalId:D}/tag-report", values);
    }

    public string RowUrl(Tag tag, string show) =>
        Link(picks: new[] { TagPick.FormatValue(IsDays ? TagTarget.Day : TagTarget.Trade, tag.Id) }, show: show) + "#tag-evidence";

    public string LookupUrl(string show, int page = 1) =>
        Link(picks: Lookup?.Picks.Select(pick => pick.Value), show: show, page: page) + "#tag-evidence";

    public string ItemUrl(TagObservation item)
    {
        var date = item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return item.Trade is { } trade
            ? $"/journal/{JournalId:D}/review?date={date}&focus={Uri.EscapeDataString(trade.ReviewKey)}&section=review"
            : $"/journal/{JournalId:D}/daily-journal?date={date}#daily-journal-{date}";
    }

    public string Amount(decimal? value) =>
        value is not { } amount ? "—"
        : Report.UsesR ? amount.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + "R"
        : amount.ToString("C2", CultureInfo.CurrentCulture);

    public static string Percent(decimal? value) => value is { } rate ? (rate * 100m).ToString("0", CultureInfo.CurrentCulture) + "%" : "—";

    public static string Share(int count, int of) => of == 0 ? "—" : Percent((decimal)count / of);

    public string ShownLabel => Show switch
    {
        "wins" => $"Winning {Noun}",
        "losses" => $"Losing {Noun}",
        "risk" => $"Matching {Noun} with risk data",
        "value" => Report.UsesR ? $"Matching {Noun} with risk data" : $"Matching {Noun}",
        "closed-1r" => "Closed at 1R or more",
        "reach" => "With risk and MFE data",
        "reached-1r" => "Reached 1R",
        "reached-2r" => "Reached 2R",
        "mae" => "With MAE data",
        "mfe" => "With MFE data",
        "earlier" => "Earlier half",
        "later" => "Later half",
        "others" => $"Other tagged {Noun}",
        "others-wins" => $"Winning other tagged {Noun}",
        "others-value" => $"Other tagged {Noun} in the comparison",
        _ => $"Matching {Noun}"
    };
}
