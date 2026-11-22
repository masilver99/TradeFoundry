using System.Text;
using System.Text.RegularExpressions;

namespace TradeFoundry.Core;

/// <summary>
/// Decides when two tag names are the same or look alike. The picker in tags.js mirrors <see cref="Key"/>
/// to highlight matches; the server's answer is the one that counts.
/// </summary>
public static partial class TagNames
{
    /// <summary>
    /// Ignores case, spacing and separators but keeps symbols that carry meaning: "Break-out" is "breakout",
    /// while "A+" and "A-", "-1R" and "1R", "1.5R" and "15R", and "5 15" and "515" stay different.
    /// </summary>
    public static string Key(string? name)
    {
        var text = (name ?? string.Empty).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        text = Quotes().Replace(text, string.Empty);
        text = JoiningDash().Replace(text, " ");
        text = JoiningPunctuation().Replace(text, " ");
        text = Spacing().Replace(text, " ").Trim();
        return RemovableSpace().Replace(text, string.Empty);
    }

    /// <summary>Trims and collapses whitespace for display.</summary>
    public static string Clean(string? name) => Whitespace().Replace(name ?? string.Empty, " ").Trim();

    /// <summary>How far apart two names are when they are close enough to be a likely typo; otherwise null.</summary>
    public static int? Distance(string first, string second)
    {
        var left = Key(first);
        var right = Key(second);
        if (left == right) return 0;
        if (Words(first) == Words(second)) return 1;

        var longest = Math.Max(left.Length, right.Length);
        var allowed = longest switch { <= 2 => 0, <= 5 => 1, <= 10 => 2, _ => 3 };
        if (allowed == 0 || Math.Abs(left.Length - right.Length) > allowed) return null;
        var edits = Edits(left, right);
        return edits <= allowed ? edits : null;
    }

    private static string Words(string name) => string.Join(' ', Spacing().Replace(name.ToLowerInvariant(), " ")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Order(StringComparer.Ordinal));

    // Optimal string alignment, so a swapped pair ("VWPA") is one edit.
    private static int Edits(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), substitution);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    [GeneratedRegex("['‘’`\"“”]")]
    private static partial Regex Quotes();

    // A dash joins words when it touches both or has space on both sides. "Loss -1R" is a minus sign.
    [GeneratedRegex(@"(?<=[\p{L}\p{N}])(?:[-–—]|\s+[-–—]+\s+)(?=[\p{L}\p{N}])")]
    private static partial Regex JoiningDash();

    [GeneratedRegex(@"(?<=\p{L})[./](?=[\p{L}\p{N}])|(?<=\p{N})[./](?=\p{L})")]
    private static partial Regex JoiningPunctuation();

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex Spacing();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@" (?!\p{Nd})|(?<!\p{Nd}) ")]
    private static partial Regex RemovableSpace();
}
