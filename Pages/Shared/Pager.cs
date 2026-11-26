namespace TradeFoundry.Pages;

/// <summary>
/// Grouped page navigation: pages come in fixed groups (1–10, 11–20, …) and only the current
/// group is linked, with first/last, previous/next page, and previous/next group around it.
/// </summary>
public sealed class Pager
{
    public const int DefaultGroupSize = 10;

    private readonly Func<int, string> _url;

    public Pager(int page, int pageCount, Func<int, string> url, int groupSize = DefaultGroupSize)
    {
        ArgumentNullException.ThrowIfNull(url);
        PageCount = Math.Max(0, pageCount);
        Page = Math.Clamp(page, 1, Math.Max(1, PageCount));
        GroupSize = Math.Max(1, groupSize);
        GroupStart = (Page - 1) / GroupSize * GroupSize + 1;
        GroupEnd = Math.Min(PageCount, GroupStart + GroupSize - 1);
        _url = url;
    }

    public int Page { get; }
    public int PageCount { get; }
    public int GroupSize { get; }
    public int GroupStart { get; }
    public int GroupEnd { get; }

    public bool IsVisible => PageCount > 1;
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
    public bool HasPreviousGroup => GroupStart > 1;
    public bool HasNextGroup => GroupEnd < PageCount;

    /// <summary>The last page of the previous group.</summary>
    public int PreviousGroupPage => Math.Max(1, GroupStart - 1);

    /// <summary>The first page of the next group.</summary>
    public int NextGroupPage => Math.Min(PageCount, GroupEnd + 1);

    public IEnumerable<int> GroupPages => Enumerable.Range(GroupStart, Math.Max(0, GroupEnd - GroupStart + 1));

    public string Url(int page) => _url(Math.Clamp(page, 1, Math.Max(1, PageCount)));
}
