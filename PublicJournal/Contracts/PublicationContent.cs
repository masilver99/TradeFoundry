using System.Net;
using System.Text;

namespace TradeFoundry.Publishing;

public static class PublicationContent
{
    private static readonly HashSet<string> Types = ["paragraph", "h1", "h2", "h3", "h4", "h5", "h6", "quote", "ul", "ol", "li", "code", "text", "linebreak", "tab", "link", "image"];

    public static bool SafeLink(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http" || uri.Scheme == "mailto");

    public static void Validate(IEnumerable<PublicationNode> nodes, ISet<string> images, int depth = 0)
    {
        if (depth > 24) throw new InvalidOperationException("Journal content is nested too deeply.");
        foreach (var node in nodes)
        {
            if (node is null || !Types.Contains(node.Type)) throw new InvalidOperationException("Unsupported journal content type.");
            if (node.Text is null || node.Text.Length > 100_000 || node.Alt is null || node.Alt.Length > 1000 || node.Children is null)
                throw new InvalidOperationException("Invalid journal content.");
            if (node.Type == "link" && !SafeLink(node.Url)) throw new InvalidOperationException("Journal links must use HTTP, HTTPS, or mailto.");
            if (node.Type == "image" && !images.Contains(node.ImageId)) throw new InvalidOperationException("A referenced journal image is missing.");
            if (node.Width < 0 || node.Width > 4096 || node.Format < 0 || node.Format > 255 || node.ListStart is < 1 or > 100_000
                || node.Alignment is not ("" or "left" or "right" or "center" or "justify" or "start" or "end"))
                throw new InvalidOperationException("Unsupported journal formatting.");
            Validate(node.Children, images, depth + 1);
        }
    }

    // Every string is encoded; all markup and attributes originate in this renderer.
    public static string Render(IEnumerable<PublicationNode> nodes, Func<string, string> imageUrl)
    {
        var html = new StringBuilder();
        foreach (var node in nodes)
        {
            string E(string value) => WebUtility.HtmlEncode(value);
            if (node.Type == "text")
            {
                var value = E(node.Text);
                foreach (var (flag, tag) in new[] { (1, "strong"), (2, "em"), (4, "s"), (8, "u"), (16, "code"), (32, "sub"), (64, "sup"), (128, "mark") })
                    if ((node.Format & flag) != 0) value = $"<{tag}>{value}</{tag}>";
                html.Append(value);
            }
            else if (node.Type == "linebreak") html.Append("<br>");
            else if (node.Type == "tab") html.Append("&#9;");
            else if (node.Type == "image")
                html.Append($"<figure><img loading=\"lazy\" src=\"{E(imageUrl(node.ImageId))}\" alt=\"{E(node.Alt)}\"{(node.Width > 0 ? $" width=\"{node.Width}\"" : "")}></figure>");
            else if (node.Type == "link")
                html.Append($"<a href=\"{E(node.Url)}\" rel=\"noopener noreferrer\">{Render(node.Children, imageUrl)}</a>");
            else
            {
                var tag = node.Type switch { "paragraph" => "p", "quote" => "blockquote", "code" => "pre", _ => node.Type };
                if (!Types.Contains(node.Type)) throw new InvalidOperationException("Unsupported journal content type.");
                var align = node.Alignment is "left" or "right" or "center" or "justify" or "start" or "end" ? $" class=\"align-{node.Alignment}\"" : "";
                var start = node.Type == "ol" && node.ListStart > 1 ? $" start=\"{node.ListStart}\"" : "";
                html.Append($"<{tag}{align}{start}>{Render(node.Children, imageUrl)}</{tag}>");
            }
        }
        return html.ToString();
    }
}
