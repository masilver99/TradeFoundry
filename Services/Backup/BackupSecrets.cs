using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// The backup passphrase and the B2 application key, stored with Data Protection so scheduled backups
/// can use them, and publishing tokens, unprotected for an encrypted backup and protected again with
/// this machine's keys on restore.
/// </summary>
public sealed class BackupSecrets
{
    private readonly TradeFoundryDb _database;
    private readonly IDataProtector _backup;
    private readonly IDataProtector _publishing;

    public BackupSecrets(TradeFoundryDb database, IDataProtectionProvider protection)
    {
        _database = database;
        _backup = protection.CreateProtector("TradeFoundry.BackupSecrets.v1");
        _publishing = protection.CreateProtector(PublicationWorkflow.TokenPurpose);
    }

    public bool EncryptionEnabled => _database.GetProtectedBackupPassphrase() is not null;

    /// <summary>The stored passphrase, or null when encryption is off.</summary>
    /// <exception cref="BackupFailedException">It is stored but this machine's keys can no longer read it.</exception>
    public string? GetPassphrase()
    {
        if (_database.GetProtectedBackupPassphrase() is not { } stored) return null;
        try
        {
            return _backup.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            throw new BackupFailedException("The saved backup passphrase can no longer be read on this machine. Enter it again in Settings > Backups.");
        }
    }

    public void SetPassphrase(string? passphrase) =>
        _database.SetProtectedBackupPassphrase(passphrase is null ? null : _backup.Protect(passphrase));

    public BackupCloudSettings? CloudSettings => _database.GetBackupCloudSettings();

    /// <summary>The configured bucket with its key, or null when cloud backup is off.</summary>
    public B2Connection? GetCloudConnection()
    {
        if (_database.GetBackupCloudSettings() is not { } settings) return null;
        try
        {
            return new B2Connection(settings.Endpoint, settings.Bucket, settings.KeyId, _backup.Unprotect(settings.ProtectedApplicationKey));
        }
        catch (CryptographicException)
        {
            throw new BackupFailedException("The saved Backblaze B2 key can no longer be read on this machine. Enter it again in Settings > Backups.");
        }
    }

    public void SetCloudConnection(B2Connection? connection) => _database.SetBackupCloudSettings(connection is null
        ? null
        : new BackupCloudSettings(connection.Endpoint, connection.Bucket, connection.KeyId, _backup.Protect(connection.ApplicationKey)));

    /// <summary>Unprotects publishing tokens for <c>secrets.json</c>. Tokens this machine cannot read are reported, not carried.</summary>
    public (IReadOnlyList<BackupPublishingToken> Tokens, int Unreadable) UnprotectPublishingTokens(IReadOnlyDictionary<Guid, string> protectedTokens)
    {
        var tokens = new List<BackupPublishingToken>();
        var unreadable = 0;
        foreach (var (journalId, value) in protectedTokens)
        {
            try
            {
                tokens.Add(new BackupPublishingToken(journalId, _publishing.Unprotect(value)));
            }
            catch (CryptographicException)
            {
                unreadable++;
            }
        }

        return (tokens, unreadable);
    }

    public string ProtectPublishingToken(string token) => _publishing.Protect(token);
}
