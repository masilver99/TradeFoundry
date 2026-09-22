using System.Security.Cryptography;
using System.Text;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class McpTokenService(TradeFoundryDb database)
{
    public CreatedMcpAccessToken Create(string name, IReadOnlyCollection<Guid> journalIds, bool allowFeeProfileWrites = false)
    {
        var secret = "tfmcp_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(secret);
        var prefix = secret[..Math.Min(secret.Length, 16)];
        var token = database.CreateMcpAccessToken(name, prefix, hash, journalIds, allowFeeProfileWrites);
        return new CreatedMcpAccessToken { Token = token, Secret = secret };
    }

    public McpAccessToken? Validate(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length != 49 || !secret.StartsWith("tfmcp_", StringComparison.Ordinal)
            || !secret[6..].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return null;
        return database.FindActiveMcpAccessToken(Hash(secret));
    }

    public IReadOnlyList<McpAccessToken> List() => database.GetMcpAccessTokens();
    public bool Revoke(Guid tokenId) => database.RevokeMcpAccessToken(tokenId);
    public void RecordUsed(Guid tokenId) => database.RecordMcpAccessTokenUsed(tokenId);

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
