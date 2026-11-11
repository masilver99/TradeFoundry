using TradeFoundry.Core;

namespace TradeFoundry.Services;

/// <summary>What the layout needs to draw the journal filter bar for this request.</summary>
/// <param name="DatesOnly">The page applies the date range but not direction (account and expenses).</param>
public sealed record JournalFilterContext(Guid JournalId, JournalFilterScope Scope, bool AppliesToPage, bool FromUrl, bool DatesOnly = false);

/// <summary>
/// Reads the journal filter for a request. The URL is the source of truth; with no filter
/// parameters in the URL, the last choice for that journal is read from a cookie. A URL
/// choice is remembered so links and redirects without parameters keep it.
/// </summary>
public static class JournalFilterRequest
{
    private const string ItemsKey = "tf-journal-filter";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    public static string CookieName(Guid journalId) => $"tf-filter-{journalId:N}";

    /// <summary>Resolves and remembers the filter for a page that applies it.</summary>
    public static JournalFilterScope Resolve(HttpContext? context, Journal journal, bool datesOnly = false)
    {
        // A page model built without a request (unit tests) shows the whole journal.
        if (context is null) return JournalFilter.None.Scope(journal.TimeZone);
        var (filter, fromUrl) = Read(context, journal.Id);
        if (fromUrl) Remember(context, journal.Id, filter);
        var scope = filter.Scope(journal.TimeZone);
        context.Items[ItemsKey] = new JournalFilterContext(journal.Id, scope, true, fromUrl, datesOnly);
        return scope;
    }

    /// <summary>The filter set for this request by the page, or, for pages that don't apply it, the remembered one (read-only).</summary>
    public static JournalFilterContext? Current(HttpContext context, Journal? journal)
    {
        if (context.Items.TryGetValue(ItemsKey, out var value) && value is JournalFilterContext current) return current;
        if (journal is null) return null;
        var (filter, _) = Read(context, journal.Id, cookieOnly: true);
        return new JournalFilterContext(journal.Id, filter.Scope(journal.TimeZone), false, false);
    }

    /// <summary>Query keys that belong to one view (paging, focus, flash flags) and are dropped when the filter changes.</summary>
    public static readonly IReadOnlySet<string> TransientKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageNumber", "before", "focus", "showHidden", "handler", "saved", "journalSaved", "edit", "add", "categories"
    };

    /// <summary>The current page's query values that survive a filter change.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> PreservedQuery(HttpRequest request) =>
        request.Query
            .Where(item => !TransientKeys.Contains(item.Key) && !JournalFilter.QueryKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
            .SelectMany(item => item.Value.Select(value => new KeyValuePair<string, string>(item.Key, value ?? string.Empty)))
            .ToArray();

    /// <summary>The current URL with some query values replaced (null removes the key).</summary>
    public static string UrlWith(HttpRequest request, params (string Key, string? Value)[] changes)
    {
        var values = request.Query
            .Where(item => !changes.Any(change => string.Equals(change.Key, item.Key, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(item => item.Value.Select(value => new KeyValuePair<string, string?>(item.Key, value)))
            .Concat(changes.Where(change => change.Value is not null).Select(change => new KeyValuePair<string, string?>(change.Key, change.Value)));
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(request.PathBase + request.Path, values);
    }

    /// <summary>The current page with the filter cleared (explicitly, so the remembered filter is forgotten too).</summary>
    /// <param name="keepSide">Keep this direction (a dates-only page clears just its dates).</param>
    public static string ClearUrl(HttpRequest request, string? keepSide = null) =>
        ApplyUrl(request, JournalFilter.Parse("all", null, null, keepSide));

    /// <summary>
    /// The current page showing <paramref name="filter"/>. Period and direction are always written,
    /// even when "all", so the link replaces the remembered filter rather than mixing with it.
    /// </summary>
    public static string ApplyUrl(HttpRequest request, JournalFilter filter)
    {
        var values = filter.ToQuery();
        var filterValues = new List<KeyValuePair<string, string?>> { new(JournalFilter.PeriodKey, filter.Period) };
        if (values.TryGetValue(JournalFilter.FromKey, out var from)) filterValues.Add(new(JournalFilter.FromKey, from));
        if (values.TryGetValue(JournalFilter.ToKey, out var to)) filterValues.Add(new(JournalFilter.ToKey, to));
        filterValues.Add(new(JournalFilter.SideKey, filter.Side));
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(request.PathBase + request.Path,
            PreservedQuery(request).Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)).Concat(filterValues));
    }

    private static (JournalFilter Filter, bool FromUrl) Read(HttpContext context, Guid journalId, bool cookieOnly = false)
    {
        var query = context.Request.Query;
        if (!cookieOnly && JournalFilter.QueryKeys.Any(query.ContainsKey))
            return (JournalFilter.Parse(query[JournalFilter.PeriodKey], query[JournalFilter.FromKey], query[JournalFilter.ToKey], query[JournalFilter.SideKey]), true);

        if (context.Request.Cookies.TryGetValue(CookieName(journalId), out var stored) && !string.IsNullOrWhiteSpace(stored))
            return (JournalFilter.FromQueryString(stored), false);

        return (JournalFilter.None, false);
    }

    private static void Remember(HttpContext context, Guid journalId, JournalFilter filter)
    {
        if (context.Response.HasStarted) return;
        var name = CookieName(journalId);
        if (!filter.IsActive)
        {
            if (context.Request.Cookies.ContainsKey(name)) context.Response.Cookies.Delete(name, new CookieOptions { Path = "/" });
            return;
        }

        context.Response.Cookies.Append(name, filter.ToQueryString(), new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = CookieLifetime
        });
    }
}
