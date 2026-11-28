using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class PatternsModel(TradeFoundryDb database, PatternDiscoveryWorker worker, TradingSetupService setups) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Instrument { get; set; }
    [TempData] public string? Message { get; set; }

    public Journal? Journal { get; private set; }
    public PatternDiscoverySnapshot? Snapshot { get; private set; }
    public PatternInstrumentReport? Report { get; private set; }
    public bool IsCurrent { get; private set; }

    public IActionResult OnGet()
    {
        Journal = database.GetJournal(JournalId);
        if (Journal is null) return NotFound();
        (Snapshot, IsCurrent) = worker.Read(JournalId);
        if (!IsCurrent) worker.RequestCheck();
        Report = Snapshot?.Instruments.FirstOrDefault(item => item.Instrument == Instrument)
            ?? Snapshot?.Instruments.OrderByDescending(item => item.TradeCount).FirstOrDefault();
        return Page();
    }

    public IActionResult OnPostCheck()
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        worker.RequestCheck();
        return RedirectToPage(new { journalId = JournalId, instrument = Instrument });
    }

    public IActionResult OnPostSaveSetup(string? findingKey, string? setupName)
    {
        if (database.GetJournal(JournalId) is null) return NotFound();
        var (snapshot, current) = worker.Read(JournalId);
        var finding = current ? snapshot?.Instruments.FirstOrDefault(item => item.Instrument == Instrument)?.Findings
            .FirstOrDefault(item => item.Key == findingKey && item is { Direction: > 0, Management: false, Weakened: false }) : null;
        if (finding is null)
        {
            Message = "This candidate is unavailable. Review the latest scan before saving.";
            return RedirectToPage(new { journalId = JournalId, instrument = Instrument });
        }

        try
        {
            var saved = setups.CreateSetup(JournalId, new TradingSetupDraft
            {
                Name = setupName ?? string.Empty,
                Category = "Discovered setup",
                ShortDescription = $"Historical {finding.Instrument} entry candidate from Pattern Discovery.",
                DetailedDescription = $"Instrument: {finding.Instrument}. The conditions are intended to be met together."
            }, new[] { $"Instrument is {finding.Instrument}" }.Concat(finding.Criteria).Select(criterion => new TradingSetupCriterionDraft
            {
                Name = criterion,
                CriterionType = SetupCriterionType.Required,
                Stage = SetupCriterionStage.Setup
            }).ToArray());
            return RedirectToPage("/Setups", new { journalId = JournalId, setupId = saved.Setup.Id });
        }
        catch (ArgumentException exception)
        {
            Message = exception.Message;
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
        {
            Message = "A setup with this name already exists in the journal.";
        }
        return RedirectToPage(new { journalId = JournalId, instrument = Instrument });
    }
}
