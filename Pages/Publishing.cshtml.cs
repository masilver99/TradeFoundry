using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Publishing;
using TradeFoundry.Services;

namespace TradeFoundry.Pages;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PublishingModel(TradeFoundryDb database, PublicationWorkflow workflow) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty] public string DestinationUrl { get; set; } = "";
    [BindProperty] public string PublicTitle { get; set; } = "";
    [BindProperty] public string Introduction { get; set; } = "";
    [BindProperty] public string PublishingToken { get; set; } = "";
    public Journal? Journal { get; private set; }
    public PublishingState State { get; private set; } = null!;
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    private bool Load(bool settings = true)
    {
        Journal = database.GetJournal(JournalId);
        if (Journal is null) return false;
        State = database.GetPublishingState(JournalId);
        if (settings) { DestinationUrl = State.Settings.DestinationUrl; PublicTitle = State.Settings.Title; Introduction = State.Settings.Introduction; }
        return true;
    }

    public IActionResult OnGet() => Load() ? Page() : NotFound();

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => Action(async () =>
    {
        await workflow.SaveSettings(JournalId, new(DestinationUrl ?? "", PublicTitle ?? "", Introduction ?? ""), PublishingToken, ct);
    }, "Settings saved. Create a new preview before publishing.", ct);

    public Task<IActionResult> OnPostPreviewAsync(CancellationToken ct) => Action(() => workflow.Preview(JournalId, ct), "Preview prepared. Review the full snapshot below before publishing.", ct);
    public Task<IActionResult> OnPostPublishAsync(Guid snapshotId, CancellationToken ct) => Action(() => workflow.Publish(JournalId, snapshotId, ct), "Publication succeeded. The website now displays the previewed snapshot.", ct);
    public Task<IActionResult> OnPostStatusAsync(CancellationToken ct) => Action(() => workflow.RefreshStatus(JournalId, ct), "Website status refreshed.", ct);
    public Task<IActionResult> OnPostUnpublishAsync(long expectedRevision, bool resetBinding, CancellationToken ct) => Action(() => workflow.Unpublish(JournalId, expectedRevision, resetBinding, ct), "The website is unpublished.", ct);

    private async Task<IActionResult> Action(Func<Task> action, string success, CancellationToken ct)
    {
        if (!Load(settings: false)) return NotFound();
        try
        {
            // Settings fields are irrelevant to preview/status/publish requests.
            await action(); Message = success;
        }
        catch (InvalidOperationException ex) { Error = ex.Message; }
        catch (System.Text.Json.JsonException)
        { Error = "Saved publication data contains invalid JSON. Save the publishing settings again and create a new preview."; }
        catch (IOException)
        { Error = "Local publication data could not be read. Check that the journal storage is available and accessible."; }
        catch (HttpRequestException) { Error = "Could not reach the website. Check the destination URL, TLS certificate, and network connection."; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { Error = "The request timed out. Refresh website status to check whether it succeeded; retrying the same snapshot is safe."; }
        PublishingToken = "";
        ModelState.Clear();
        Load();
        return Page();
    }

    public IActionResult OnGetPreviewDocument(Guid snapshotId)
    {
        if (!Load()) return NotFound();
        if (State.Draft?.Snapshot.SnapshotId != snapshotId) return NotFound();
        var snapshot = State.Draft.Snapshot;
        var path = $"/journal/{JournalId:D}/publishing";
        var document = PublicationPresentation.Document(snapshot, null,
            id => $"{path}?handler=PreviewImage&snapshotId={snapshotId:D}&imageId={id}", path, $"{path}?handler=Style", preview: true);
        return Content(document, "text/html; charset=utf-8");
    }

    public IActionResult OnGetPreviewImage(Guid snapshotId, string imageId)
    {
        if (!Load()) return NotFound();
        if (State.Draft?.Snapshot.SnapshotId != snapshotId) return NotFound();
        var image = State.Draft.Snapshot.Images.FirstOrDefault(i => i.Id == imageId);
        return image is null ? NotFound() : File(image.Content, image.ContentType);
    }

    public IActionResult OnGetStyle() => Content(PublicationPresentation.Style(), "text/css");
}
