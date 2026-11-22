using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Tests;

internal static class TestTags
{
    /// <summary>The form value for these names, creating any tags the journal doesn't have yet.</summary>
    public static string Ids(TradeFoundryDb database, Guid journalId, params string[] names) =>
        string.Join(",", names.Select(name => Get(database, journalId, name).Id));

    public static Tag Get(TradeFoundryDb database, Guid journalId, string name, TagKnown known = TagKnown.Beforehand, TagScope scope = TagScope.Both)
    {
        var catalog = database.GetTagCatalog(journalId);
        return catalog.Find(name)
            ?? database.CreateTag(journalId, new TagDraft(name, $"When {name} applies.", known, scope), catalog.Similar(name).Select(tag => tag.Id).ToArray());
    }
}
