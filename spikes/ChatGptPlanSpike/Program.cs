using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGptPlanSpike;

// Phase 1 spike: can TradeFoundry (.NET) use a user's ChatGPT plan for an AI daily review, including images?
//
//   signin [--port N] [--reconsent]   OAuth + PKCE loopback sign-in; saves DPAPI-encrypted credentials
//   status                            Show saved connection (no secrets)
//   models                            List plan models; dumps the raw catalog to models.json
//   ask "<prompt>" [--model slug]     Stream a text response
//   image <file> ["<prompt>"] [--model slug] [--detail low|high|auto]
//                                     Send one local image (png/jpg/webp/gif) with a prompt
//   probe [--model slug]              Run the capability matrix and write probe-report-*.json
//   refresh                           Force a token refresh (tests rotation)
//   signout                           Revoke the refresh token and delete local credentials

var store = new SpikeStore();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var positional = new List<string>();
var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        options[args[i][2..]] = hasValue ? args[++i] : null;
    }
    else positional.Add(args[i]);
}
var command = positional.FirstOrDefault() ?? "help";

try
{
    switch (command)
    {
        case "signin":
        {
            var port = options.TryGetValue("port", out var p) && int.TryParse(p, out var parsed) ? parsed : 0;
            var connection = await OAuth.SignInAsync(store, port, options.ContainsKey("reconsent"), ct);
            Console.WriteLine();
            PrintStatus(connection);
            break;
        }
        case "status":
            if (store.LoadConnection() is { } saved) PrintStatus(saved);
            else Console.WriteLine($"Not signed in. Host ID {store.GetHostId()}, saved client ID {store.LoadClientId() ?? "(none)"}.");
            break;
        case "models":
        {
            var token = await OAuth.GetAccessTokenAsync(store, ct);
            var (listed, raw) = await Responses.ListModelsAsync(token, ct);
            var path = Path.Combine(store.Directory, "models.json");
            await File.WriteAllTextAsync(path, Pretty(raw), ct);
            Console.WriteLine($"{listed.Count} listed model(s):");
            foreach (var model in listed) Console.WriteLine($"  {model.Slug,-32} {model.DisplayName}");
            Console.WriteLine($"Raw catalog (look for input-modality fields): {path}");
            break;
        }
        case "ask":
        {
            var prompt = positional.ElementAtOrDefault(1) ?? "Reply with exactly: Token sharing works.";
            var (token, model) = await TokenAndModelAsync();
            var result = await Responses.StreamAsync(token, Responses.TextRequest(model, prompt), Console.Write, ct);
            Console.WriteLine();
            PrintUsage(result);
            break;
        }
        case "image":
        {
            var file = positional.ElementAtOrDefault(1) ?? throw new ArgumentException("Usage: image <file> [\"prompt\"]");
            var prompt = positional.ElementAtOrDefault(2) ?? "Describe this trading chart or screenshot: instrument, timeframe, visible price action, and any marked entries, exits, or annotations.";
            var contentType = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                var other => throw new ArgumentException($"Unsupported image type {other}."),
            };
            var bytes = await File.ReadAllBytesAsync(file, ct);
            Console.WriteLine($"Sending {bytes.Length / 1024.0:F0} KB {contentType}...");
            var (token, model) = await TokenAndModelAsync();
            var body = Responses.ImageRequest(model, prompt, [Responses.DataUrl(bytes, contentType)], options.GetValueOrDefault("detail"));
            var result = await Responses.StreamAsync(token, body, Console.Write, ct);
            Console.WriteLine();
            PrintUsage(result);
            break;
        }
        case "probe":
            await RunProbesAsync();
            break;
        case "refresh":
        {
            await OAuth.GetAccessTokenAsync(store, ct, forceRefresh: true);
            PrintStatus(store.LoadConnection()!);
            break;
        }
        case "signout":
            await OAuth.RevokeAsync(store, ct);
            Console.WriteLine("Signed out and revoked. (Client registration and host ID kept.)");
            break;
        default:
            Console.WriteLine("Commands: signin [--port N] [--reconsent] | status | models | ask \"prompt\" | image <file> [\"prompt\"] [--detail d] | probe | refresh | signout");
            Console.WriteLine("Common option: --model <slug>  (defaults to the first listed model)");
            break;
    }
    return 0;
}
catch (ChatGptException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(ex.ToString());
    if (ex.Code == "subscription_sharing_usage_limit_exceeded") Console.Error.WriteLine("Manage usage: https://chatgpt.com/settings/usage");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 2;
}

async Task<(string Token, string Model)> TokenAndModelAsync()
{
    var token = await OAuth.GetAccessTokenAsync(store, ct);
    if (options.GetValueOrDefault("model") is { } requested) return (token, requested);
    var (listed, _) = await Responses.ListModelsAsync(token, ct);
    var model = listed.FirstOrDefault()?.Slug ?? throw new ChatGptException("no_models", "No models are available for this connection.");
    Console.WriteLine($"(model: {model})");
    return (token, model);
}

async Task RunProbesAsync()
{
    var (token, model) = await TokenAndModelAsync();
    var rgb = Responses.DataUrl(TestImages.Bands(TestImages.RedGreenBlue), "image/png");
    var yb = Responses.DataUrl(TestImages.Bands(TestImages.YellowBlack), "image/png");
    const string colorsPrompt = "The image shows vertical colored bands. Answer with only the band colors from left to right, lowercase, comma-separated.";
    string[] rgbWords = ["red", "green", "blue"];

    JsonObject With(JsonObject body, string key, JsonNode value) { body[key] = value; return body; }

    var probes = new (string Name, string Purpose, Func<JsonObject> Body, Func<string, bool>? Check)[]
    {
        ("text", "Baseline plan inference",
            () => Responses.TextRequest(model, "Reply with exactly: Token sharing works.", "You are a terse assistant."),
            t => t.Contains("Token sharing works", StringComparison.OrdinalIgnoreCase)),
        ("image-single", "Screenshot/chart input via base64 data URL",
            () => Responses.ImageRequest(model, colorsPrompt, [rgb]),
            t => TestImages.InOrder(t, rgbWords)),
        ("image-detail-high", "detail=high parameter on input_image",
            () => Responses.ImageRequest(model, colorsPrompt, [rgb], "high"),
            t => TestImages.InOrder(t, rgbWords)),
        ("image-multi", "Several images in one message (day + trade charts)",
            () => Responses.ImageRequest(model, "There are two images of vertical colored bands. For each image in order, list its colors left to right, lowercase. Format: image1: ...; image2: ...", [rgb, yb]),
            t => TestImages.InOrder(t, ["red", "green", "blue", "yellow", "black"])),
        ("developer-role", "Developer message for the review boundary rules",
            () => new JsonObject
            {
                ["model"] = model,
                ["input"] = new JsonArray(
                    new JsonObject { ["role"] = "developer", ["content"] = "Always answer with the single word BANANA." },
                    new JsonObject { ["role"] = "user", ["content"] = "What fruit?" }),
            },
            t => t.Contains("banana", StringComparison.OrdinalIgnoreCase)),
        ("multi-turn", "Client-side history for follow-up questions",
            () => new JsonObject
            {
                ["model"] = model,
                ["input"] = new JsonArray(
                    new JsonObject { ["role"] = "user", ["content"] = "Remember the code word: KESTREL." },
                    new JsonObject { ["role"] = "assistant", ["content"] = "Noted." },
                    new JsonObject { ["role"] = "user", ["content"] = "What was the code word? Answer with the word only." }),
            },
            t => t.Contains("kestrel", StringComparison.OrdinalIgnoreCase)),
        ("structured-output", "JSON schema output for storing structured reviews",
            () => With(Responses.TextRequest(model, "Review this trade: long ES at 5000, stopped at 4995 after 3 minutes, plan said wait for a retest. Give a grade and one lesson."),
                "text", JsonNode.Parse("""
                    {"format":{"type":"json_schema","name":"trade_review","strict":true,"schema":{
                      "type":"object","additionalProperties":false,"required":["grade","lesson"],
                      "properties":{"grade":{"type":"string","enum":["A","B","C","D","F"]},"lesson":{"type":"string"}}}}}
                    """)!),
            t => { try { var j = JsonDocument.Parse(t).RootElement; return j.Str("grade") is not null && j.Str("lesson") is not null; } catch (JsonException) { return false; } }),
        ("reasoning-effort", "reasoning.effort parameter",
            () => With(Responses.TextRequest(model, "Reply with exactly: ok"), "reasoning", new JsonObject { ["effort"] = "low" }),
            t => t.Contains("ok", StringComparison.OrdinalIgnoreCase)),
        ("temperature", "Expected unsupported per secondary sources",
            () => With(Responses.TextRequest(model, "Reply with exactly: ok"), "temperature", 0.2),
            null),
        ("max-output-tokens", "Expected unsupported per secondary sources",
            () => With(Responses.TextRequest(model, "Reply with exactly: ok"), "max_output_tokens", 64),
            null),
    };

    var results = new List<object>();
    Console.WriteLine($"Running {probes.Length} probes against {model}...\n");
    foreach (var probe in probes)
    {
        Console.Write($"  {probe.Name,-20} ");
        try
        {
            var result = await Responses.StreamAsync(token, probe.Body(), null, ct);
            bool? passed = probe.Check?.Invoke(result.Text);
            var verdict = passed switch { true => "OK", false => "ACCEPTED, CHECK FAILED", null => "ACCEPTED" };
            Console.WriteLine($"{verdict,-24} {result.Elapsed.TotalSeconds,5:F1}s  in={result.InputTokens} out={result.OutputTokens}  \"{Excerpt(result.Text, 60)}\"");
            results.Add(new
            {
                probe.Name, probe.Purpose, accepted = true, checkPassed = passed, result.Model,
                result.InputTokens, result.OutputTokens, result.ReasoningTokens,
                seconds = Math.Round(result.Elapsed.TotalSeconds, 2), result.RequestId, text = Excerpt(result.Text, 400),
            });
        }
        catch (ChatGptException ex)
        {
            Console.WriteLine($"REJECTED [{ex.Code}] {ex.Status} {Excerpt(ex.ServerDetail ?? ex.Message, 80)}");
            results.Add(new { probe.Name, probe.Purpose, accepted = false, error = ex.Code, ex.Status, ex.RequestId, message = ex.Message, server = ex.ServerDetail });
            if (ex.Code is "subscription_sharing_usage_limit_exceeded" or "invalid_token" or "sharing_not_granted") break;
        }
    }

    var reportPath = Path.Combine(store.Directory, $"probe-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { model, ranAt = DateTimeOffset.Now, results }, new JsonSerializerOptions { WriteIndented = true }), ct);
    Console.WriteLine($"\nReport: {reportPath}");
}

static void PrintStatus(Connection c)
{
    Console.WriteLine($"Connected as  {c.Name ?? "(no name)"} <{c.Email ?? "no email"}>  sub={c.Subject}");
    Console.WriteLine($"Client ID     {c.ClientId}");
    Console.WriteLine($"Scopes        {string.Join(' ', c.Scopes)}");
    Console.WriteLine($"Plan usage    {(c.Sharing ? "GRANTED (Using ChatGPT plan)" : "NOT granted, identity only")}");
    Console.WriteLine($"Token expires {c.ExpiresAt.ToLocalTime():g} ({(c.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes:F0} min)  refresh token: {(string.IsNullOrEmpty(c.RefreshToken) ? "no" : "yes")}");
    Console.WriteLine("Manage usage  https://chatgpt.com/settings/usage");
}

static void PrintUsage(ResponseResult r) =>
    Console.WriteLine($"-- model={r.Model} in={r.InputTokens} out={r.OutputTokens} reasoning={r.ReasoningTokens} {r.Elapsed.TotalSeconds:F1}s request={r.RequestId}");

static string Excerpt(string text, int max)
{
    var flat = text.ReplaceLineEndings(" ").Trim();
    return flat.Length <= max ? flat : flat[..max] + "…";
}

static string Pretty(string json)
{
    try { return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
    catch (JsonException) { return json; }
}
