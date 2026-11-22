namespace TradeFoundry.Core;

/// <summary>When a tag can be known. Only what is known beforehand can serve as a probability.</summary>
public enum TagKnown
{
    /// <summary>Not chosen yet: a tag carried over from the old free-text field.</summary>
    Unset,
    Beforehand,
    During,
    After
}

public enum TagScope
{
    Trade,
    Day,
    Both
}

public enum TagTarget
{
    Trade,
    Day
}

public sealed record Tag
{
    public const int NameMaxLength = 80;
    public const int DescriptionMaxLength = 500;
    public const int MaxPerItem = 25;

    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TagKnown Known { get; init; }
    public TagScope Scope { get; init; }
    public bool Archived { get; init; }
    public Guid? MergedIntoId { get; init; }
    public int Revision { get; init; }
    /// <summary>Former names that still find this tag.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    public bool NeedsReview => Known == TagKnown.Unset || string.IsNullOrWhiteSpace(Description);
    /// <summary>Whether the tag may be used as a probability.</summary>
    public bool IsPredictive => Known == TagKnown.Beforehand && !NeedsReview;

    public bool AppliesTo(TagTarget target) => Scope == TagScope.Both || Scope == (target == TagTarget.Day ? TagScope.Day : TagScope.Trade);

    public static string KnownLabel(TagKnown known) => known switch
    {
        TagKnown.Beforehand => "Beforehand",
        TagKnown.During => "During",
        TagKnown.After => "After",
        _ => "Not set"
    };

    public static string ScopeLabel(TagScope scope) => scope switch
    {
        TagScope.Trade => "Trades",
        TagScope.Day => "Days",
        _ => "Trades and days"
    };
}

public sealed record TagDraft(string? Name, string? Description, TagKnown Known, TagScope Scope);

public sealed record TagReference(Guid Id, string Name);

public sealed record DailyTagSet(DateOnly Date, int Revision, IReadOnlyList<TagReference> Tags)
{
    public static DailyTagSet Empty(DateOnly date) => new(date, 0, Array.Empty<TagReference>());
}

public sealed record DailyTagSaveResult(bool Saved, DailyTagSet Set);

/// <summary>A tag name that needs the trader's attention before it can be saved.</summary>
public sealed class TagNameException : ArgumentException
{
    public TagNameException(string message, Tag? existing, IReadOnlyList<Tag> similar) : base(message)
    {
        Existing = existing;
        Similar = similar;
    }

    /// <summary>The tag that already owns the name.</summary>
    public Tag? Existing { get; }
    /// <summary>Look-alike tags that have not been confirmed as different.</summary>
    public IReadOnlyList<Tag> Similar { get; }
}

/// <summary>A review that has content but whose trade key no longer exists.</summary>
public sealed record DetachedReview(
    TradeReviewAnnotation Review,
    int AttachmentCount,
    IReadOnlyList<string> SuggestedKeys);

/// <summary>All of one journal's tags, resolving merges.</summary>
public sealed class TagCatalog
{
    private readonly Dictionary<Guid, Tag> _byId;
    private readonly Dictionary<string, Tag> _byKey = new(StringComparer.Ordinal);

    public TagCatalog(IReadOnlyList<Tag> tags)
    {
        All = tags;
        _byId = tags.ToDictionary(tag => tag.Id);
        Current = tags.Where(tag => !tag.MergedIntoId.HasValue).OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        foreach (var tag in tags) _byKey[TagNames.Key(tag.Name)] = tag;
        foreach (var tag in tags)
            foreach (var alias in tag.Aliases) _byKey.TryAdd(TagNames.Key(alias), tag);
    }

    public IReadOnlyList<Tag> All { get; }
    /// <summary>Tags that are not merged into another, including archived ones.</summary>
    public IReadOnlyList<Tag> Current { get; }

    public Tag? Get(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>The tag a stored id stands for today.</summary>
    public Tag? Resolve(Guid id)
    {
        var tag = Get(id);
        for (var hops = 0; tag?.MergedIntoId is { } next && hops < _byId.Count; hops++) tag = Get(next);
        return tag;
    }

    /// <summary>The tag that owns this exact (normalized) name or former name, before resolving merges.</summary>
    public Tag? Owner(string name) => _byKey.GetValueOrDefault(TagNames.Key(name));

    public Tag? Find(string name) => Owner(name) is { } owner ? Resolve(owner.Id) : null;

    /// <summary>Current tags whose names, former names, or merged names look like <paramref name="name"/>.</summary>
    public IReadOnlyList<Tag> Similar(string name, Guid? exceptId = null)
    {
        var closest = new Dictionary<Guid, (Tag Tag, int Distance)>();
        foreach (var tag in All)
        {
            if (Resolve(tag.Id) is not { } current || current.Id == exceptId) continue;
            foreach (var known in tag.Aliases.Prepend(tag.Name))
            {
                if (TagNames.Distance(name, known) is not { } distance) continue;
                if (!closest.TryGetValue(current.Id, out var best) || distance < best.Distance) closest[current.Id] = (current, distance);
            }
        }
        return closest.Values.OrderBy(item => item.Distance).ThenBy(item => item.Tag.Name, StringComparer.OrdinalIgnoreCase).Take(5).Select(item => item.Tag).ToArray();
    }
}
