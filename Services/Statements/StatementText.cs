using TradeFoundry.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace TradeFoundry.Services.Statements;

/// <summary>A word on a statement page, with its horizontal extent in PDF points.</summary>
public sealed record StatementWord(string Text, double Left, double Right)
{
    public double Center => (Left + Right) / 2d;
}

/// <summary>The words that share one baseline on a page, left to right.</summary>
public sealed record StatementLine(int Page, IReadOnlyList<StatementWord> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>
/// Turns a statement PDF into lines of positioned words. Broker statements are column reports: a
/// quantity means buy or sell only by which column it sits under, and the PDF's own drawing order
/// interleaves columns, so the lines are rebuilt from word positions rather than read as a text stream.
/// </summary>
public static class StatementText
{
    private const double BaselineTolerance = 2.5d;
    /// <summary>A busy month runs to a few dozen pages; anything far past that is not a statement worth parsing.</summary>
    public const int MaxPages = 300;
    public const int MaxWords = 300_000;

    public static IReadOnlyList<StatementLine> ReadPdf(byte[] content)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            if (document.NumberOfPages > MaxPages)
                throw new StatementParseException($"This PDF has {document.NumberOfPages} pages; statements are limited to {MaxPages}.");
            var lines = new List<StatementLine>();
            var wordCount = 0;
            foreach (var page in document.GetPages())
            {
                var words = page.GetWords().ToList();
                wordCount += words.Count;
                if (wordCount > MaxWords) throw new StatementParseException("This PDF has more text than a statement would; it was not imported.");
                lines.AddRange(GroupLines(page.Number, words));
            }
            return lines;
        }
        catch (Exception e) when (e is not StatementParseException and not OutOfMemoryException)
        {
            throw new StatementParseException("The file could not be read as a PDF statement.");
        }
    }

    internal static IEnumerable<StatementLine> GroupLines(int pageNumber, IEnumerable<Word> words)
    {
        var current = new List<Word>();
        double? baseline = null;
        foreach (var word in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(Baseline).ThenBy(w => w.BoundingBox.Left))
        {
            if (baseline.HasValue && Math.Abs(Baseline(word) - baseline.Value) > BaselineTolerance)
            {
                yield return ToLine(pageNumber, current);
                current = [];
            }
            if (current.Count == 0) baseline = Baseline(word);
            current.Add(word);
        }
        if (current.Count > 0) yield return ToLine(pageNumber, current);
    }

    /// <summary>The text baseline, so descenders (g, p, y) do not split a line the way glyph bottoms would.</summary>
    private static double Baseline(Word word) => word.Letters.Count > 0 ? word.Letters[0].StartBaseLine.Y : word.BoundingBox.Bottom;

    private static StatementLine ToLine(int pageNumber, List<Word> words) =>
        new(pageNumber, words.OrderBy(w => w.BoundingBox.Left).Select(w => new StatementWord(w.Text.Trim(), w.BoundingBox.Left, w.BoundingBox.Right)).ToArray());
}
