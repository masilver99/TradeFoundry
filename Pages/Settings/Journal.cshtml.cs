using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class JournalModel : PageModel
{
    private const int MaxDescriptionLength = 100_000;
    private readonly TradeFoundryDb _database;

    public JournalModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string ExecutionContext { get; set; } = "live";
    [BindProperty] public string? Labels { get; set; }
    [BindProperty] public string DescriptionLexicalStateJson { get; set; } = string.Empty;
    [BindProperty] public string TimeZone { get; set; } = "UTC";
    [BindProperty] public string Currency { get; set; } = "USD";
    [BindProperty] public string GroupingPolicy { get; set; } = "flat_to_flat";
    [BindProperty] public decimal? StartingEquity { get; set; }
    [BindProperty] public decimal? MaeTargetPerContract { get; set; }
    [BindProperty] public string? MaeInstrumentOverrides { get; set; } = string.Empty;

    public Journal? Journal { get; private set; }
    public string? FlashMessage { get; private set; }
    public string? FlashKind { get; private set; }

    public IActionResult OnGet()
    {
        Load();
        if (Journal is null) return NotFound();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        return Page();
    }

    public IActionResult OnPostSave()
    {
        if (_database.GetJournal(JournalId) is null) return NotFound();
        if ((DescriptionLexicalStateJson?.Length ?? 0) > MaxDescriptionLength)
            ModelState.AddModelError(nameof(DescriptionLexicalStateJson), $"Keep the description under {MaxDescriptionLength:N0} characters.");

        if (!ModelState.IsValid)
        {
            Load();
            return Page();
        }

        var saved = _database.UpdateJournal(JournalId, Name, ExecutionContext, Labels ?? string.Empty, TimeZone, Currency, GroupingPolicy, StartingEquity, DescriptionLexicalStateJson ?? string.Empty);
        TempData["FlashMessage"] = saved ? "Journal settings saved." : "The journal could not be updated.";
        TempData["FlashKind"] = saved ? "success" : "error";
        return Redirect($"/journal/{JournalId:D}/settings/journal");
    }

    public IActionResult OnPostSaveMaeTargets()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        foreach (var unrelatedField in new[]
        {
            nameof(Name), nameof(ExecutionContext), nameof(Labels), nameof(DescriptionLexicalStateJson),
            nameof(TimeZone), nameof(Currency), nameof(GroupingPolicy), nameof(StartingEquity)
        })
            ModelState.Remove(unrelatedField);

        if (MaeTargetPerContract is <= 0m)
            ModelState.AddModelError(nameof(MaeTargetPerContract), "Enter a target greater than zero, or leave it empty to disable the default.");

        var overrides = ParseInstrumentOverrides();
        if (!ModelState.IsValid)
        {
            Load();
            return Page();
        }

        _database.SaveMaeTargetSettings(JournalId, MaeTargetPerContract, overrides);
        TempData["FlashMessage"] = "MAE review targets saved.";
        TempData["FlashKind"] = "success";
        return Redirect($"/journal/{JournalId:D}/settings/journal");
    }

    private Dictionary<string, decimal> ParseInstrumentOverrides()
    {
        var targets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var lines = (MaeInstrumentOverrides ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1 || line.IndexOf('=', separator + 1) >= 0)
            {
                ModelState.AddModelError(nameof(MaeInstrumentOverrides), $"Line {index + 1}: use INSTRUMENT=amount, for example MES=35.00.");
                continue;
            }

            var instrument = InstrumentCatalog.ExtractRoot(line[..separator].Trim());
            var amountText = line[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(instrument))
            {
                ModelState.AddModelError(nameof(MaeInstrumentOverrides), $"Line {index + 1}: enter an instrument root.");
                continue;
            }
            if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0m)
            {
                ModelState.AddModelError(nameof(MaeInstrumentOverrides), $"Line {index + 1}: enter an amount greater than zero using a period for decimals.");
                continue;
            }
            if (!targets.TryAdd(instrument, amount))
                ModelState.AddModelError(nameof(MaeInstrumentOverrides), $"Instrument {instrument} appears more than once.");
        }

        return targets;
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return;
        Name = Journal.Name;
        ExecutionContext = Journal.ExecutionContext;
        Labels = Journal.Labels;
        DescriptionLexicalStateJson = Journal.DescriptionLexicalStateJson;
        TimeZone = Journal.TimeZone;
        Currency = Journal.Currency;
        GroupingPolicy = Journal.GroupingPolicy;
        StartingEquity = Journal.StartingEquity;
        var maeTargets = _database.GetMaeTargetSettings(JournalId);
        MaeTargetPerContract = maeTargets.DefaultPerContract;
        MaeInstrumentOverrides = string.Join(Environment.NewLine, maeTargets.InstrumentTargets
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));
    }
}
