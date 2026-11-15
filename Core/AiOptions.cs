namespace TradeFoundry.Core;

public sealed class AiOptions
{
    public const string ChatGptProvider = "chatgpt";
    public const string FakeProvider = "fake";

    /// <summary>Shows AI review settings and allows provider connections. Nothing is sent anywhere until the owner connects.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>chatgpt</c> (Sign in with ChatGPT plan usage) or <c>fake</c> (deterministic, offline; for tests and smoke hosts).</summary>
    public string Provider { get; set; } = ChatGptProvider;

    /// <summary>IPv4 loopback port for the sign-in callback; 0 selects a free port per sign-in.</summary>
    public int RedirectPort { get; set; }

    public int SignInTimeoutMinutes { get; set; } = 5;

    public int RequestTimeoutMinutes { get; set; } = 4;

    /// <summary>Most images sent with one review; the trader's own screenshots take priority over generated charts.</summary>
    public int MaxImagesPerReview { get; set; } = 12;
}
