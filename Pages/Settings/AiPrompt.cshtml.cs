using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services.Ai;

namespace TradeFoundry.Pages.Settings;

[Authorize]
public class AiPromptModel : PageModel
{
    private readonly TradeFoundryDb _database;
    private readonly AiPromptLibrary _prompts;
    private readonly AiOptions _options;

    public AiPromptModel(TradeFoundryDb database, AiPromptLibrary prompts, IOptions<AiOptions> options)
    {
        _database = database;
        _prompts = prompts;
        _options = options.Value;
    }

    [BindProperty(SupportsGet = true)] public Guid JournalId { get; set; }
    [BindProperty(SupportsGet = true, Name = "id")] public string? PromptId { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Body { get; set; }

    public AiReviewPrompt? Existing { get; private set; }
    public bool IsBuiltIn => Existing?.BuiltIn == true;
    public bool IsNew => Existing is null;
    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> UnknownPlaceholders => AiPromptLibrary.UnknownPlaceholders(Body);
    public string? FlashMessage { get; private set; }
    public string SettingsPath => $"/journal/{JournalId:D}/settings/ai";
    public string EditorPath => $"{SettingsPath}/prompt";

    /// <summary>The full instructions a review would send, using sample values for placeholders.</summary>
    public string PreviewInstructions => AiReviewPrompts.BuildInstructions(
        new AiReviewPrompt("preview", Name ?? string.Empty, Description ?? string.Empty, string.IsNullOrWhiteSpace(Body) ? "(your instructions)" : Body, false),
        extraInstruction: null, AiPromptContext.Sample);

    public IActionResult OnGet()
    {
        if (!_options.Enabled || _database.GetJournal(JournalId) is null) return NotFound();
        if (PromptId is not null)
        {
            Existing = _prompts.Find(PromptId);
            if (Existing is null) return Redirect(SettingsPath);
            Name = Existing.Name;
            Description = Existing.Description;
            Body = Existing.Body;
        }
        else
        {
            Body = AiReviewPrompts.DefaultDayReview.Body;
            Name = string.Empty;
        }
        FlashMessage = TempData["FlashMessage"] as string;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!_options.Enabled || _database.GetJournal(JournalId) is null) return NotFound();
        if (PromptId is not null)
        {
            Existing = _prompts.Find(PromptId);
            if (Existing is null) return Redirect(SettingsPath);
            if (Existing.BuiltIn) return BadRequest();
        }

        Errors = AiPromptLibrary.Validate(Name, Description, Body);
        if (Errors.Count > 0) return Page();
        try
        {
            var saved = Existing is null ? _prompts.Create(Name!, Description, Body!) : _prompts.Update(Existing.Id, Name!, Description, Body!);
            TempData["FlashMessage"] = "Prompt saved.";
            return Redirect($"{EditorPath}?id={Uri.EscapeDataString(saved.Id)}");
        }
        catch (InvalidOperationException ex)
        {
            Errors = [ex.Message];
            return Page();
        }
    }
}
