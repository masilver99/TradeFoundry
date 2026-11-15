using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services.Ai;

namespace TradeFoundry.Pages;

public sealed record AiReviewPanelViewModel(Guid JournalId, DateOnly Date, Journal Journal, bool Enabled, DayReviewPreview? Preview)
{
    public string BasePath => $"/journal/{JournalId:D}/ai-review";
    public string DateText => Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record AiReviewRunViewModel(Guid JournalId, Journal Journal, AiReviewRun Run)
{
    public DayReviewOutput? Output { get; } = Run.Status == AiReviewRunStatus.Completed ? DayReviewOutput.TryParse(Run.OutputText) : null;
    public DayReviewManifest? Manifest { get; } = DayReviewManifest.FromJson(Run.BundleManifestJson);
    public string BasePath => $"/journal/{JournalId:D}/ai-review";

    public string TradeUrl(string reviewKey) =>
        $"/journal/{JournalId:D}/review?date={Run.ReviewDate:yyyy-MM-dd}&focus={Uri.EscapeDataString(reviewKey)}";

    public AiBundleTrade? Trade(string reviewKey) => Manifest?.Trades.FirstOrDefault(t => t.ReviewKey == reviewKey);

    public string LocalCreated
    {
        get
        {
            try { return TimeZoneInfo.ConvertTime(Run.CreatedUtc, TimeZoneInfo.FindSystemTimeZoneById(Journal.TimeZone)).ToString("MMM d, h:mm tt"); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return Run.CreatedUtc.ToString("MMM d, HH:mm 'UTC'"); }
        }
    }
}

[Authorize]
public class AiReviewModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly AiReviewService _reviews;
    private readonly ILogger<AiReviewModel> _logger;

    public AiReviewModel(TradeFoundryDb database, AiReviewService reviews, ILogger<AiReviewModel> logger)
    {
        _database = database;
        _reviews = reviews;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }

    public AiReviewPanelViewModel Panel { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var panel = await BuildPanelAsync(cancellationToken);
        if (panel is null) return NotFound();
        Panel = panel;
        return Page();
    }

    public async Task<IActionResult> OnGetPanelAsync(CancellationToken cancellationToken)
    {
        var panel = await BuildPanelAsync(cancellationToken);
        if (panel is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Partial("_AiReviewPanel", panel);
    }

    public async Task<IActionResult> OnPostStartAsync(string? promptId, string? extraInstruction, List<string>? offeredImages, List<string>? includedImages, CancellationToken cancellationToken)
    {
        if (_database.GetJournal(JournalId) is null || Date is not { } date) return NotFound();
        var selection = new AiImageSelection(
            new HashSet<string>(offeredImages ?? [], StringComparer.Ordinal),
            new HashSet<string>(includedImages ?? [], StringComparer.Ordinal));
        try
        {
            var run = await _reviews.StartDayReviewAsync(JournalId, date, new DayReviewStartRequest(promptId, extraInstruction, selection), cancellationToken);
            return new JsonResult(new { runId = run.Id });
        }
        catch (AiProviderException ex)
        {
            return new JsonResult(new { error = ex.Message, code = ex.Code, usageLimit = ex.IsUsageLimit }) { StatusCode = StatusCodes.Status400BadRequest };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "AI review for {Date} could not start.", date);
            return new JsonResult(new { error = $"The review could not start: {ex.Message}", code = "unexpected" }) { StatusCode = StatusCodes.Status500InternalServerError };
        }
    }

    public IActionResult OnGetProgress(Guid runId)
    {
        Response.Headers.CacheControl = "no-store";
        var progress = _reviews.GetProgress(JournalId, runId);
        return progress is null ? NotFound() : new JsonResult(new
        {
            status = progress.Status,
            characters = progress.CharactersReceived,
            elapsedSeconds = Math.Round(progress.ElapsedSeconds, 1),
            error = progress.ErrorMessage,
        });
    }

    public IActionResult OnPostCancel(Guid runId) =>
        _reviews.Cancel(JournalId, runId) ? new JsonResult(new { cancelled = true }) : NotFound();

    public IActionResult OnGetRun(Guid runId)
    {
        var journal = _database.GetJournal(JournalId);
        var run = journal is null ? null : _reviews.GetRun(JournalId, runId);
        if (run is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Partial("_AiReviewRun", new AiReviewRunViewModel(JournalId, journal!, run));
    }

    public IActionResult OnPostDelete(Guid runId) =>
        _reviews.DeleteRun(JournalId, runId) ? new JsonResult(new { deleted = true }) : NotFound();

    private async Task<AiReviewPanelViewModel?> BuildPanelAsync(CancellationToken cancellationToken)
    {
        var journal = _database.GetJournal(JournalId);
        if (journal is null || Date is not { } date) return null;
        var preview = _reviews.Enabled ? await _reviews.PreviewDayAsync(JournalId, date, cancellationToken) : null;
        return new AiReviewPanelViewModel(JournalId, date, journal, _reviews.Enabled, preview);
    }
}
