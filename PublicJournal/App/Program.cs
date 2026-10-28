using TradeFoundry.PublicJournal;

if (args is ["--health-check"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync("http://127.0.0.1:8080/health");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException) { Environment.ExitCode = 1; }
    catch (OperationCanceledException) { Environment.ExitCode = 1; }
    return;
}

var app = PublicJournalApplication.Build(args);
app.Run();
