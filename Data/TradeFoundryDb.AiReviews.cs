using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    private const string AiReviewRunColumns = "id, journal_id, scope, review_date, review_key, prompt_id, prompt_name, prompt_snapshot, extra_instruction, provider, model, reasoning_effort, bundle_hash, bundle_manifest_json, status, output_text, input_tokens, output_tokens, reasoning_tokens, error_code, error_message, created_utc, completed_utc";

    private static void InitializeAiReviews(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ai_review_runs (
                id TEXT PRIMARY KEY,
                journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE,
                scope TEXT NOT NULL,
                review_date TEXT NOT NULL,
                review_key TEXT NULL,
                prompt_id TEXT NOT NULL,
                prompt_name TEXT NOT NULL,
                prompt_snapshot TEXT NOT NULL,
                extra_instruction TEXT NOT NULL DEFAULT '',
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                reasoning_effort TEXT NULL,
                bundle_hash TEXT NOT NULL,
                bundle_manifest_json TEXT NOT NULL DEFAULT '{}',
                status TEXT NOT NULL,
                output_text TEXT NOT NULL DEFAULT '',
                input_tokens INTEGER NULL,
                output_tokens INTEGER NULL,
                reasoning_tokens INTEGER NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                created_utc TEXT NOT NULL,
                completed_utc TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_ai_review_runs_day ON ai_review_runs(journal_id, review_date, created_utc DESC);
            """;
        command.ExecuteNonQuery();
    }

    public void InsertAiReviewRun(AiReviewRun run)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO ai_review_runs ({AiReviewRunColumns}) VALUES ($id, $journal, $scope, $date, $reviewKey, $promptId, $promptName, $prompt, $extra, $provider, $model, $effort, $hash, $manifest, $status, $output, NULL, NULL, NULL, NULL, NULL, $created, NULL)";
        command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
        command.Parameters.AddWithValue("$journal", run.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$scope", run.Scope);
        command.Parameters.AddWithValue("$date", run.ReviewDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$reviewKey", (object?)run.ReviewKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$promptId", run.PromptId);
        command.Parameters.AddWithValue("$promptName", run.PromptName);
        command.Parameters.AddWithValue("$prompt", run.PromptSnapshot);
        command.Parameters.AddWithValue("$extra", run.ExtraInstruction);
        command.Parameters.AddWithValue("$provider", run.Provider);
        command.Parameters.AddWithValue("$model", run.Model);
        command.Parameters.AddWithValue("$effort", (object?)run.ReasoningEffort ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", run.BundleHash);
        command.Parameters.AddWithValue("$manifest", run.BundleManifestJson);
        command.Parameters.AddWithValue("$status", AiReviewRunStatus.Running);
        command.Parameters.AddWithValue("$output", string.Empty);
        command.Parameters.AddWithValue("$created", run.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Finishes a running run. Returns false when the run was already finished: finished runs never change.</summary>
    public bool FinishAiReviewRun(Guid runId, string status, string outputText, long? inputTokens, long? outputTokens, long? reasoningTokens, string? errorCode, string? errorMessage)
    {
        if (status == AiReviewRunStatus.Running) throw new ArgumentException("A run must finish with a terminal status.", nameof(status));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_review_runs SET status=$status, output_text=$output, input_tokens=$input, output_tokens=$outputTokens, reasoning_tokens=$reasoning,
                error_code=$code, error_message=$message, completed_utc=$completed
            WHERE id=$id AND status='running'
            """;
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$output", outputText);
        command.Parameters.AddWithValue("$input", (object?)inputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", (object?)outputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasoning", (object?)reasoningTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Marks runs left running by a previous process (crash or restart) as failed.</summary>
    public int FailInterruptedAiReviewRuns()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ai_review_runs SET status='failed', error_code='interrupted', error_message='TradeFoundry stopped before this review finished.', completed_utc=$completed WHERE status='running'";
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    }

    public AiReviewRun? GetAiReviewRun(Guid journalId, Guid runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AiReviewRunColumns} FROM ai_review_runs WHERE id=$id AND journal_id=$journal";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAiReviewRun(reader) : null;
    }

    public IReadOnlyList<AiReviewRun> GetAiReviewRunsForDay(Guid journalId, DateOnly date, int limit = 20)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AiReviewRunColumns} FROM ai_review_runs WHERE journal_id=$journal AND review_date=$date AND scope='day' ORDER BY created_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<AiReviewRun>();
        while (reader.Read()) runs.Add(ReadAiReviewRun(reader));
        return runs;
    }

    public bool DeleteAiReviewRun(Guid journalId, Guid runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ai_review_runs WHERE id=$id AND journal_id=$journal AND status<>'running'";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        return command.ExecuteNonQuery() == 1;
    }

    private static AiReviewRun ReadAiReviewRun(SqliteDataReader reader)
    {
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        long? Number(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
        DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new AiReviewRun
        {
            Id = Guid.Parse(reader.GetString(0)),
            JournalId = Guid.Parse(reader.GetString(1)),
            Scope = reader.GetString(2),
            ReviewDate = DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReviewKey = Text(4),
            PromptId = reader.GetString(5),
            PromptName = reader.GetString(6),
            PromptSnapshot = reader.GetString(7),
            ExtraInstruction = reader.GetString(8),
            Provider = reader.GetString(9),
            Model = reader.GetString(10),
            ReasoningEffort = Text(11),
            BundleHash = reader.GetString(12),
            BundleManifestJson = reader.GetString(13),
            Status = reader.GetString(14),
            OutputText = reader.GetString(15),
            InputTokens = Number(16),
            OutputTokens = Number(17),
            ReasoningTokens = Number(18),
            ErrorCode = Text(19),
            ErrorMessage = Text(20),
            CreatedUtc = Time(reader.GetString(21)),
            CompletedUtc = Text(22) is { } completed ? Time(completed) : null,
        };
    }
}
