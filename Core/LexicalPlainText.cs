using System.Text;
using System.Text.Json;

namespace TradeFoundry.Core;

public static class LexicalPlainText
{
    public static string Extract(string? serializedState)
    {
        if (string.IsNullOrWhiteSpace(serializedState)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(serializedState);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("root", out var root))
            {
                var text = new StringBuilder();
                AppendNode(root, text);
                return Normalize(text.ToString());
            }
        }
        catch (JsonException)
        {
            // Older entries may still be plain text. Keep them searchable.
        }

        return Normalize(serializedState);
    }

    private static void AppendNode(JsonElement node, StringBuilder text)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        var type = node.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        if (string.Equals(type, "text", StringComparison.Ordinal)
            && node.TryGetProperty("text", out var textElement)
            && textElement.ValueKind == JsonValueKind.String)
        {
            text.Append(textElement.GetString());
            return;
        }

        if (string.Equals(type, "linebreak", StringComparison.Ordinal))
        {
            AppendNewline(text);
            return;
        }

        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;

        var isBlock = type is "paragraph" or "heading" or "quote" or "listitem" or "code";
        if (isBlock && text.Length > 0) AppendNewline(text);
        foreach (var child in children.EnumerateArray()) AppendNode(child, text);
        if (isBlock) AppendNewline(text);
    }

    private static void AppendNewline(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
    }

    private static string Normalize(string value)
    {
        var lines = value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', lines).Trim();
    }
}
