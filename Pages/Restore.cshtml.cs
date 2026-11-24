using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services.Backup;

namespace TradeFoundry.Pages;

[AllowAnonymous]
public class RestoreModel : PageModel
{
    private const string CodeKey = "RestoreCode";
    private readonly TradeFoundryDb _database;
    private readonly RestoreService _restore;
    private readonly BackupService _backups;

    public RestoreModel(TradeFoundryDb database, RestoreService restore, BackupService backups)
    {
        _database = database;
        _restore = restore;
        _backups = backups;
    }

    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Folder { get; set; }

    public RestoreAccess Access { get; private set; }
    public bool RequiresCode { get; private set; }
    public bool HasOwner { get; private set; }
    public RestoreJob? Job { get; private set; }
    public IReadOnlyList<BackupListing>? Backups { get; private set; }
    public IReadOnlyList<BackupListing>? CloudBackups { get; private set; }
    [BindProperty] public string? Endpoint { get; set; }
    [BindProperty] public string? Bucket { get; set; }
    [BindProperty] public string? KeyId { get; set; }
    [BindProperty] public bool ShowHidden { get; set; }
    public IReadOnlyList<SetAsideCopy> SetAside { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    /// <summary>The verified code, sent again with the upload, which cannot read TempData.</summary>
    public string? VerifiedCode { get; private set; }
    public string? LastOperationError => _restore.LastOperationError;

    public IActionResult OnGet()
    {
        if (!Load(verifyCode: false)) return Redirect("/Login?ReturnUrl=%2Frestore");
        Folder ??= _backups.Folder;
        ErrorMessage = TempData["RestoreError"] as string;
        return Page();
    }

    public IActionResult OnGetStatus()
    {
        Response.Headers.CacheControl = "no-store";
        var job = _restore.Job;
        return new JsonResult(new { state = job?.State ?? "none", stage = job?.Stage, fraction = job?.Fraction, message = job?.Message });
    }

    public IActionResult OnPostList()
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access != RestoreAccess.Allowed) return Page();
        Folder = string.IsNullOrWhiteSpace(Folder) ? _backups.Folder : Folder.Trim();
        if (!Path.IsPathFullyQualified(Folder))
        {
            ErrorMessage = "Enter a full folder path.";
            return Page();
        }

        try
        {
            Backups = _restore.ListBackups(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = $"That folder cannot be read: {ex.Message}";
        }

        return Page();
    }

    public IActionResult OnPostStage(string archive)
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access != RestoreAccess.Allowed) return Page();
        if (!_restore.TryStageFromFolder(Folder ?? string.Empty, archive, out var message)) TempData["RestoreError"] = message;
        return Redirect("/restore");
    }

    public IActionResult OnPostDiscard()
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access == RestoreAccess.Allowed) _restore.Discard();
        return Redirect("/restore");
    }

    public IActionResult OnPostCommit(string? password, bool confirm)
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access != RestoreAccess.Allowed) return Page();
        if (!confirm)
        {
            ErrorMessage = "Tick the box to confirm.";
            return Page();
        }

        ErrorMessage = _restore.Commit(password ?? string.Empty);
        Job = _restore.Job;
        return Page();
    }

    public async Task<IActionResult> OnPostCloudList(string? applicationKey, CancellationToken cancellationToken)
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access != RestoreAccess.Allowed) return Page();
        var endpoint = B2Cloud.NormalizeEndpoint(Endpoint);
        if (endpoint is null)
        {
            ErrorMessage = "Enter the bucket's S3 endpoint, such as https://s3.us-west-002.backblazeb2.com.";
            return Page();
        }

        if (!B2Cloud.IsValidBucket(Bucket?.Trim()) || string.IsNullOrWhiteSpace(KeyId) || string.IsNullOrWhiteSpace(applicationKey))
        {
            ErrorMessage = "Enter the bucket name, application key ID and application key.";
            return Page();
        }

        try
        {
            CloudBackups = await _restore.ListCloudBackupsAsync(new B2Connection(endpoint, Bucket!.Trim(), KeyId.Trim(), applicationKey.Trim()), ShowHidden, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = B2Cloud.Describe(ex);
        }

        return Page();
    }

    public IActionResult OnPostCloudStage(string archiveKey, string? versionId)
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access != RestoreAccess.Allowed) return Page();
        if (!_restore.TryStageFromCloud(archiveKey, string.IsNullOrEmpty(versionId) ? null : versionId, out var message)) TempData["RestoreError"] = message;
        return Redirect("/restore");
    }

    public IActionResult OnPostUnlock(string? passphrase)
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access != RestoreAccess.Allowed) return Page();
        if (!_restore.Unlock(passphrase ?? string.Empty, out var message)) TempData["RestoreError"] = message;
        return Redirect("/restore");
    }

    public IActionResult OnPostPutBack(string name)
    {
        if (!Load(verifyCode: true)) return Redirect("/Login?ReturnUrl=%2Frestore");
        if (Access != RestoreAccess.Allowed || !HasOwner) return Page();
        ErrorMessage = _restore.PutBack(name);
        if (ErrorMessage is null) TempData["Restarting"] = "put-back";
        return Page();
    }

    public bool PutBackStarted => TempData.Peek("Restarting") is "put-back";

    public static string FormatBytes(long bytes) => BackupService.FormatBytes(bytes);

    /// <summary>Works out what this visitor may do. Returns false when they must sign in first.</summary>
    private bool Load(bool verifyCode)
    {
        HasOwner = _database.HasOwner();
        RequiresCode = _restore.RequiresCode;
        if (RequiresCode) _restore.EnsureCode();
        var code = string.IsNullOrWhiteSpace(Code) ? TempData.Peek(CodeKey) as string : Code;
        Access = _restore.CheckAccess(User.Identity?.IsAuthenticated == true, code);
        if (Access == RestoreAccess.NeedsSignIn) return false;
        if (Access == RestoreAccess.Allowed && RequiresCode)
        {
            TempData[CodeKey] = code;
            VerifiedCode = code;
        }
        if (Access == RestoreAccess.NeedsCode && verifyCode && !string.IsNullOrWhiteSpace(Code)) ErrorMessage = "That code is not right. Copy it from the server log.";
        Job = _restore.Job;
        if (Access == RestoreAccess.Allowed && HasOwner) SetAside = _restore.ListSetAside();
        return true;
    }
}
