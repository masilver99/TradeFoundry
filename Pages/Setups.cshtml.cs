using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class SetupsModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly TradingSetupService _setups;

    public SetupsModel(TradeFoundryDb database, TradingSetupService setups)
    {
        _database = database;
        _setups = setups;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? SetupId { get; set; }

    [BindProperty] public string NewName { get; set; } = string.Empty;
    [BindProperty] public string NewShortDescription { get; set; } = string.Empty;
    [BindProperty] public string NewDetailedDescription { get; set; } = string.Empty;
    [BindProperty] public string NewCategory { get; set; } = string.Empty;
    [BindProperty] public List<CriterionInput> NewCriteria { get; set; } = new();

    [BindProperty] public string EditName { get; set; } = string.Empty;
    [BindProperty] public string EditShortDescription { get; set; } = string.Empty;
    [BindProperty] public string EditDetailedDescription { get; set; } = string.Empty;
    [BindProperty] public string EditCategory { get; set; } = string.Empty;
    [BindProperty] public List<CriterionInput> VersionCriteria { get; set; } = new();

    public Journal? Journal { get; private set; }
    public IReadOnlyList<TradingSetupSummary> Setups { get; private set; } = Array.Empty<TradingSetupSummary>();
    public TradingSetupDetail? SelectedSetup { get; private set; }
    public IReadOnlyList<SetupPerformanceSummary> Performance { get; private set; } = Array.Empty<SetupPerformanceSummary>();
    public string? FlashMessage { get; private set; }
    public string FlashKind { get; private set; } = "success";

    public TradingSetupVersion? CurrentVersion => SelectedSetup?.CurrentVersion;
    public int SelectedSetupTradeCount => SelectedSetup is null ? 0 : Setups.FirstOrDefault(setup => setup.Id == SelectedSetup.Setup.Id)?.TradeCount ?? 0;
    public bool CanEditCurrentVersion => SelectedSetup is not null && SelectedSetupTradeCount == 0;

    public IActionResult OnGet()
    {
        Load();
        if (Journal is null) return NotFound();
        FlashMessage = TempData["FlashMessage"] as string;
        FlashKind = TempData["FlashKind"] as string ?? "success";
        return Page();
    }

    public IActionResult OnPostCreateSetup()
    {
        try
        {
            var detail = _setups.CreateSetup(JournalId, new TradingSetupDraft
            {
                Name = NewName,
                ShortDescription = NewShortDescription,
                DetailedDescription = NewDetailedDescription,
                Category = NewCategory
            }, ToDrafts(NewCriteria));
            TempData["FlashMessage"] = $"Created {detail.Setup.Name} v1.";
            TempData["FlashKind"] = "success";
            return RedirectToPage(new { journalId = JournalId, setupId = detail.Setup.Id });
        }
        catch (ArgumentException exception)
        {
            return PageWithError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return PageWithError(exception.Message);
        }
    }

    public IActionResult OnPostUpdateMetadata(Guid setupId)
    {
        try
        {
            var saved = _setups.UpdateMetadata(JournalId, setupId, new TradingSetupDraft
            {
                Name = EditName,
                ShortDescription = EditShortDescription,
                DetailedDescription = EditDetailedDescription,
                Category = EditCategory
            });
            if (saved is null) return NotFound();
            TempData["FlashMessage"] = $"Updated {saved.Name} metadata.";
            TempData["FlashKind"] = "success";
            return RedirectToPage(new { journalId = JournalId, setupId });
        }
        catch (ArgumentException exception)
        {
            SetupId = setupId;
            return PageWithError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            SetupId = setupId;
            return PageWithError(exception.Message);
        }
    }

    public IActionResult OnPostCreateVersion(Guid setupId, string? versionNotes)
    {
        try
        {
            var detail = _setups.CreateVersion(JournalId, setupId, versionNotes, ToDrafts(VersionCriteria));
            TempData["FlashMessage"] = $"Created {detail.Setup.Name} v{detail.CurrentVersion?.Version}. Existing trade evaluations remain on their original versions.";
            TempData["FlashKind"] = "success";
            return RedirectToPage(new { journalId = JournalId, setupId });
        }
        catch (ArgumentException exception)
        {
            SetupId = setupId;
            return PageWithError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            SetupId = setupId;
            return PageWithError(exception.Message);
        }
    }

    public IActionResult OnPostUpdateCurrentVersion(Guid setupId, string? versionNotes)
    {
        try
        {
            var detail = _setups.UpdateCurrentVersion(JournalId, setupId, versionNotes, ToDrafts(VersionCriteria));
            TempData["FlashMessage"] = $"Updated {detail.Setup.Name} v{detail.CurrentVersion?.Version}. No new version was needed because no trades use this setup yet.";
            TempData["FlashKind"] = "success";
            return RedirectToPage(new { journalId = JournalId, setupId });
        }
        catch (ArgumentException exception)
        {
            SetupId = setupId;
            return PageWithError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            SetupId = setupId;
            return PageWithError(exception.Message);
        }
    }

    public IActionResult OnPostSetActive(Guid setupId, bool active)
    {
        if (!_setups.SetActive(JournalId, setupId, active)) return NotFound();
        TempData["FlashMessage"] = active ? "Setup reactivated." : "Setup deactivated. Historical trade links were preserved.";
        TempData["FlashKind"] = "success";
        return RedirectToPage(new { journalId = JournalId, setupId });
    }

    public IActionResult OnPostMoveCriterion(Guid setupId, Guid criterionId, bool moveUp)
    {
        if (!_setups.MoveCriterion(JournalId, criterionId, moveUp))
        {
            TempData["FlashMessage"] = "The criterion is already at that edge of the list.";
            TempData["FlashKind"] = "error";
        }
        else
        {
            TempData["FlashMessage"] = "Criterion order updated.";
            TempData["FlashKind"] = "success";
        }
        return RedirectToPage(new { journalId = JournalId, setupId });
    }

    private void Load()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return;
        Setups = _setups.GetSetups(JournalId, includeInactive: true);
        Performance = _setups.GetPerformance(JournalId);
        var selectedId = SetupId ?? Setups.FirstOrDefault()?.Id;
        if (selectedId.HasValue)
        {
            SelectedSetup = _setups.GetSetup(JournalId, selectedId.Value);
            SetupId = SelectedSetup?.Setup.Id;
        }

        if (SelectedSetup is not null)
        {
            EditName = SelectedSetup.Setup.Name;
            EditShortDescription = SelectedSetup.Setup.ShortDescription;
            EditDetailedDescription = SelectedSetup.Setup.DetailedDescription;
            EditCategory = SelectedSetup.Setup.Category;
            VersionCriteria = SelectedSetup.CurrentVersion?.Criteria.Select(CriterionInput.From).ToList() ?? new();
        }
        if (NewCriteria.Count == 0) NewCriteria.Add(new CriterionInput());
        if (VersionCriteria.Count == 0) VersionCriteria.Add(new CriterionInput());
    }

    private IActionResult PageWithError(string message)
    {
        Load();
        FlashMessage = message;
        FlashKind = "error";
        return Page();
    }

    private static IReadOnlyList<TradingSetupCriterionDraft> ToDrafts(IEnumerable<CriterionInput> inputs)
        => inputs.Select((input, index) => new TradingSetupCriterionDraft
        {
            Name = input.Name,
            Description = input.Description,
            CriterionType = input.CriterionType,
            DisplayOrder = index,
            Active = input.Active,
            EvaluationMode = input.EvaluationMode,
            RuleMetadataJson = input.RuleMetadataJson,
            Stage = input.Stage
        }).ToArray();

    public sealed class CriterionInput
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public SetupCriterionType CriterionType { get; set; } = SetupCriterionType.Required;
        public bool Active { get; set; } = true;
        public SetupEvaluationMode EvaluationMode { get; set; } = SetupEvaluationMode.Manual;
        public string RuleMetadataJson { get; set; } = string.Empty;
        public SetupCriterionStage Stage { get; set; } = SetupCriterionStage.Setup;

        public static CriterionInput From(TradingSetupCriterion criterion) => new()
        {
            Name = criterion.Name,
            Description = criterion.Description,
            CriterionType = criterion.CriterionType,
            Active = criterion.Active,
            EvaluationMode = criterion.EvaluationMode,
            RuleMetadataJson = criterion.RuleMetadataJson,
            Stage = criterion.Stage
        };
    }
}
