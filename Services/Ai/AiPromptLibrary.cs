using System.Globalization;
using System.Text.RegularExpressions;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Ai;

/// <summary>Values substituted into <c>{{placeholder}}</c> tokens when a prompt runs.</summary>
public sealed record AiPromptContext(DateOnly Date, string JournalName, string Currency, string TimeZone, int TradeCount)
{
    public static AiPromptContext Sample { get; } = new(new DateOnly(2026, 7, 24), "Live futures", "USD", "America/New_York", 4);
}

/// <summary>
/// Built-in and user-written review prompts. User prompts may be edited or archived freely: every run stores the exact
/// instructions it sent, so past reviews never change meaning. The evidence rules are always appended and cannot be removed.
/// </summary>
public sealed partial class AiPromptLibrary(TradeFoundryDb database)
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 240;
    public const int MaxBodyLength = 8000;
    public const string UserPrefix = "user:";
    private const string DefaultPromptSetting = "ai_default_day_prompt";

    public static IReadOnlyDictionary<string, string> Placeholders { get; } = new Dictionary<string, string>
    {
        ["date"] = "The trading day, for example 2026-07-24",
        ["weekday"] = "The day of the week, for example Friday",
        ["journal"] = "The journal's name",
        ["currency"] = "The journal's currency",
        ["timezone"] = "The journal's time zone",
        ["trade_count"] = "Number of trades on the day",
    };

    public IReadOnlyList<AiReviewPrompt> List() =>
        [.. AiReviewPrompts.DayReviewPrompts, .. database.GetAiPrompts().Where(p => p.Scope == "day").Select(ToPrompt)];

    public AiReviewPrompt? Find(string? id)
    {
        if (AiReviewPrompts.Find(id) is { } builtIn) return builtIn;
        return TryParseUserId(id, out var guid) && database.GetAiPrompt(guid) is { Archived: false } record ? ToPrompt(record) : null;
    }

    public AiReviewPrompt GetDefault() => Find(database.GetAppSetting(DefaultPromptSetting)) ?? AiReviewPrompts.DefaultDayReview;

    public void SetDefault(string id)
    {
        var prompt = Find(id) ?? throw new InvalidOperationException("That prompt no longer exists.");
        database.SetAppSetting(DefaultPromptSetting, prompt.BuiltIn && prompt.Id == AiReviewPrompts.DefaultDayReview.Id ? null : prompt.Id);
    }

    public AiReviewPrompt Create(string name, string? description, string body) => Save(Guid.NewGuid(), name, description, body, created: true);

    public AiReviewPrompt Update(string id, string name, string? description, string body)
    {
        if (!TryParseUserId(id, out var guid) || database.GetAiPrompt(guid) is not { Archived: false })
            throw new InvalidOperationException("Built-in prompts can't be edited. Duplicate it to make your own version.");
        return Save(guid, name, description, body, created: false);
    }

    public AiReviewPrompt Duplicate(string id)
    {
        var source = Find(id) ?? throw new InvalidOperationException("That prompt no longer exists.");
        var name = source.Name.Length + 7 > MaxNameLength ? source.Name[..(MaxNameLength - 7)] : source.Name;
        return Create($"{name} (copy)", source.Description, source.Body);
    }

    /// <summary>Archives a user prompt; past runs keep their own copy of its instructions.</summary>
    public void Archive(string id)
    {
        if (!TryParseUserId(id, out var guid) || database.GetAiPrompt(guid) is not { } record)
            throw new InvalidOperationException("Built-in prompts can't be removed.");
        database.SaveAiPrompt(new AiPromptRecord
        {
            Id = record.Id, Name = record.Name, Description = record.Description, Body = record.Body, Scope = record.Scope,
            Archived = true, CreatedUtc = record.CreatedUtc, UpdatedUtc = DateTimeOffset.UtcNow,
        });
        if (database.GetAppSetting(DefaultPromptSetting) == id) database.SetAppSetting(DefaultPromptSetting, null);
    }

    /// <summary>Replaces known placeholders; unknown ones are left as written.</summary>
    public static string Render(string? body, AiPromptContext context) => PlaceholderPattern().Replace(body ?? string.Empty, match =>
        match.Groups[1].Value.ToLowerInvariant() switch
        {
            "date" => context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "weekday" => context.Date.DayOfWeek.ToString(),
            "journal" => context.JournalName,
            "currency" => context.Currency,
            "timezone" => context.TimeZone,
            "trade_count" => context.TradeCount.ToString(CultureInfo.InvariantCulture),
            _ => match.Value,
        });

    public static IReadOnlyList<string> UnknownPlaceholders(string? body) => body is null ? [] :
        PlaceholderPattern().Matches(body).Select(m => m.Groups[1].Value).Where(name => !Placeholders.ContainsKey(name.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> Validate(string? name, string? description, string? body)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add("Give the prompt a name.");
        else if (name.Trim().Length > MaxNameLength) errors.Add($"Keep the name under {MaxNameLength} characters.");
        if ((description?.Trim().Length ?? 0) > MaxDescriptionLength) errors.Add($"Keep the description under {MaxDescriptionLength} characters.");
        if (string.IsNullOrWhiteSpace(body)) errors.Add("Write the prompt's instructions.");
        else if (body.Trim().Length > MaxBodyLength) errors.Add($"Keep the instructions under {MaxBodyLength:N0} characters.");
        return errors;
    }

    private AiReviewPrompt Save(Guid id, string name, string? description, string body, bool created)
    {
        var errors = Validate(name, description, body);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        var now = DateTimeOffset.UtcNow;
        var existing = created ? null : database.GetAiPrompt(id);
        var record = new AiPromptRecord
        {
            Id = id, Name = name.Trim(), Description = description?.Trim() ?? string.Empty, Body = body.Trim().ReplaceLineEndings("\n"),
            Scope = "day", CreatedUtc = existing?.CreatedUtc ?? now, UpdatedUtc = now,
        };
        database.SaveAiPrompt(record);
        return ToPrompt(record);
    }

    private static AiReviewPrompt ToPrompt(AiPromptRecord record) =>
        new($"{UserPrefix}{record.Id:D}", record.Name, record.Description, record.Body, BuiltIn: false);

    private static bool TryParseUserId(string? id, out Guid guid)
    {
        guid = Guid.Empty;
        return id is not null && id.StartsWith(UserPrefix, StringComparison.Ordinal) && Guid.TryParse(id[UserPrefix.Length..], out guid);
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();
}
