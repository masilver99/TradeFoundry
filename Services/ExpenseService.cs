using System.Globalization;
using System.Text;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class ExpenseService(TradeFoundryDb database)
{
    public const long MaxReceiptLength = 10 * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "expense-receipts");

    public string? GetReceiptPath(Guid journalId, Guid expenseId, Guid receiptId)
    {
        var receipt = database.GetExpenseReceipts(journalId, expenseId).FirstOrDefault(r => r.Id == receiptId);
        if (receipt is null) return null;
        var path = SafePath(journalId, receipt.StorageKey);
        return File.Exists(path) ? path : null;
    }

    private string SafePath(Guid journalId, string key)
    {
        if (key != Path.GetFileName(key) || key.Contains('/') || key.Contains('\\')) throw new InvalidOperationException("Invalid receipt storage key.");
        return Path.Combine(_root, journalId.ToString("D"), key);
    }

    public async Task<ExpenseReceipt> AttachAsync(Guid journalId, Guid expenseId, int revision, Stream content, string fileName, string contentType, long declaredLength, CancellationToken cancellationToken = default)
    {
        var expense = database.GetExpense(journalId, expenseId) ?? throw new InvalidOperationException("Expense was not found.");
        if (expense.Revision != revision) throw new ExpenseConflictException();
        if (declaredLength <= 0 || declaredLength > MaxReceiptLength) throw new InvalidOperationException("Receipts must be between 1 byte and 10 MB.");
        var (type, extension) = contentType.Trim().ToLowerInvariant() switch
        {
            "application/pdf" => ("application/pdf", ".pdf"),
            "image/png" => ("image/png", ".png"),
            "image/jpeg" or "image/jpg" => ("image/jpeg", ".jpg"),
            "image/webp" => ("image/webp", ".webp"),
            _ => throw new InvalidOperationException("Upload a PDF, PNG, JPEG, or WebP receipt.")
        };
        var key = $"{Guid.NewGuid():N}{extension}";
        var path = SafePath(journalId, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            long total = 0;
            var header = new byte[12];
            var headerLength = 0;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > MaxReceiptLength) throw new InvalidOperationException("Receipts must be between 1 byte and 10 MB.");
                    var copy = Math.Min(read, header.Length - headerLength);
                    buffer.AsSpan(0, copy).CopyTo(header.AsSpan(headerLength));
                    headerLength += copy;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var valid = type switch
            {
                "application/pdf" => headerLength >= 5 && header.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
                "image/png" => headerLength >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "image/jpeg" => headerLength >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255,
                "image/webp" => headerLength >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
                _ => false
            };
            if (!valid) throw new InvalidOperationException("The receipt content does not match its file type.");
            var safeName = Path.GetFileName(fileName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "receipt" + extension;
            safeName = new string(safeName.Where(c => !char.IsControl(c)).ToArray());
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "receipt" + extension;
            if (safeName.Length > 180) safeName = safeName[..180];
            var receipt = new ExpenseReceipt(Guid.NewGuid(), journalId, expenseId, key, safeName, type, total, DateTimeOffset.UtcNow);
            database.AddExpenseReceipt(receipt, revision);
            return receipt;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public void RemoveReceipt(Guid journalId, Guid expenseId, Guid receiptId, int revision)
    {
        var receipt = database.RemoveExpenseReceipt(journalId, expenseId, receiptId, revision);
        File.Delete(SafePath(journalId, receipt.StorageKey));
    }

    public static byte[] ExportCsv(IEnumerable<Expense> expenses)
    {
        static string Cell(string value)
        {
            if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("Date,Vendor,Description,Category,Amount,Currency,Notes,Receipt count\r\n");
        foreach (var expense in expenses)
        {
            var d = expense.Details;
            csv.AppendJoin(',', new[] { d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Vendor ?? "", d.Description, expense.CategoryName, d.Amount.ToString(CultureInfo.InvariantCulture), expense.Currency, d.Notes ?? "", expense.ReceiptCount.ToString(CultureInfo.InvariantCulture) }.Select(Cell));
            csv.Append("\r\n");
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }
}
