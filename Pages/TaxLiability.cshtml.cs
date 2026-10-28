using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class TaxLiabilityModel(TradeFoundryDb database) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    public Journal? Journal { get; private set; }
    public string BaseUrl => $"/journal/{JournalId:D}/tax-liability";
    public decimal? TaxRate => 18.6m;
    [BindProperty(SupportsGet = true)] public int? TaxYear { get; set; }
    public DateOnly TaxAsOf { get; private set; }
    public List<(int Quarter, decimal Profit, decimal Expenses)> TaxQuarters { get; } = [];
    public decimal TaxNet => TaxQuarters.Sum(q => q.Profit - q.Expenses);
    public decimal EstimatedTax => Math.Round(Math.Max(0m, TaxNet) * (TaxRate ?? 0m) / 100m, 2);
    public decimal ProjectedNet { get; private set; }
    public decimal ProjectedTax => Math.Round(ProjectedNet * .186m, 2);
    public int ExcludedTaxExpenses { get; private set; }
    public IActionResult OnGet()
    {
        Journal = database.GetJournal(JournalId);
        if (Journal is null) return NotFound();
        var today = DateOnly.FromDateTime(TimeZoneCatalog.Convert(DateTimeOffset.UtcNow, Journal.TimeZone).DateTime);
        TaxYear ??= today.Year;
        if (TaxYear < 2000 || TaxYear > today.Year) { ModelState.AddModelError("TaxYear", "Choose a year from 2000 through the current year."); TaxYear = today.Year; }
        TaxAsOf = TaxYear == today.Year ? today : new DateOnly(TaxYear.Value, 12, 31);
        var taxStart = new DateOnly(TaxYear.Value, 1, 1);
        var taxExpenses = database.GetExpenses(JournalId, taxStart, TaxAsOf);
        ExcludedTaxExpenses = taxExpenses.Count(e => e.Currency != Journal.Currency);
        var closedTrades = database.GetAllTrades(JournalId).Where(t => t.Status == "closed" && t.ExitUtc.HasValue)
            .Select(t => (Trade: t, Date: DateOnly.FromDateTime(TimeZoneCatalog.Convert(t.ExitUtc!.Value, Journal.TimeZone).DateTime)))
            .Where(t => t.Date >= taxStart && t.Date <= TaxAsOf).ToArray();
        TaxQuarters.Clear();
        for (var quarter = 1; quarter <= 4; quarter++)
            TaxQuarters.Add((quarter, closedTrades.Where(t => (t.Date.Month - 1) / 3 + 1 == quarter).Sum(t => t.Trade.NetPnl),
                taxExpenses.Where(e => e.Currency == Journal.Currency && (e.Details.Date.Month - 1) / 3 + 1 == quarter).Sum(e => e.Details.Amount)));
        var elapsedDays = TaxAsOf.DayNumber - taxStart.DayNumber + 1;
        var yearDays = DateTime.IsLeapYear(TaxYear.Value) ? 366 : 365;
        ProjectedNet = Math.Max(0m, TaxNet * yearDays / elapsedDays);
        return Page();
    }
}
