using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SharpTurns.Core.Persistence;

/// <summary>The app's single SQLite database file, in WAL mode. Each call opens a pooled connection.</summary>
public sealed class ConversationStore
{
    private readonly string _connectionString;

    public ConversationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Applies pending migrations and closes turns a previous run left open. Call once at startup.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL", cancellationToken).ConfigureAwait(false);
        var version = Convert.ToInt32(await ScalarAsync(connection, null, "PRAGMA user_version", cancellationToken)
            .ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version > Migrations.Scripts.Length)
            throw new InvalidOperationException(
                $"The database schema (version {version}) is newer than this build supports ({Migrations.Scripts.Length}).");
        for (; version < Migrations.Scripts.Length; version++)
        {
            await using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, Migrations.Scripts[version], cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {version + 1}", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        // A single app instance runs at a time, so no live process still owns these turns.
        await ExecuteAsync(connection, null,
            "UPDATE conversation_turns SET status = 'failed', error_message = $message, finished_at = $now WHERE status = 'running'",
            cancellationToken, ("$message", "The app closed before this turn finished."), ("$now", Now())).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "SELECT id, name, working_directory FROM projects ORDER BY name COLLATE NOCASE, id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var projects = new List<Project>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            projects.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return projects;
    }

    public async Task<Project> CreateProjectAsync(string name, string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = (long)(await ScalarAsync(connection, null,
            "INSERT INTO projects (name, working_directory, created_at) VALUES ($name, $directory, $now) RETURNING id",
            cancellationToken, ("$name", name), ("$directory", workingDirectory), ("$now", Now())).ConfigureAwait(false))!;
        return new(id, name, workingDirectory);
    }

    public async Task UpdateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.WorkingDirectory);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null,
            "UPDATE projects SET name = $name, working_directory = $directory WHERE id = $id", "Project",
            cancellationToken, ("$id", project.Id), ("$name", project.Name), ("$directory", project.WorkingDirectory))
            .ConfigureAwait(false);
    }

    /// <summary>Deletes the project with all of its conversations.</summary>
    public async Task DeleteProjectAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "DELETE FROM projects WHERE id = $id", "Project",
            cancellationToken, ("$id", projectId)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Conversation>> ListConversationsAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT id, project_id, title, model, effort, updated_at FROM conversations
            WHERE project_id = $project ORDER BY updated_at DESC, id DESC
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var conversations = new List<Conversation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            conversations.Add(ReadConversation(reader));
        return conversations;
    }

    public async Task<Conversation> CreateConversationAsync(long projectId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            INSERT INTO conversations (project_id, title, created_at, updated_at) VALUES ($project, $title, $now, $now)
            RETURNING id, project_id, title, model, effort, updated_at
            """, ("$project", projectId), ("$title", title), ("$now", Now()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadConversation(reader);
    }

    public async Task RenameConversationAsync(long conversationId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversations SET title = $title WHERE id = $id", "Conversation",
            cancellationToken, ("$id", conversationId), ("$title", title)).ConfigureAwait(false);
    }

    public async Task SetConversationModelAsync(long conversationId, string? model, string? effort,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversations SET model = $model, effort = $effort WHERE id = $id",
            "Conversation", cancellationToken, ("$id", conversationId), ("$model", model), ("$effort", effort))
            .ConfigureAwait(false);
    }

    public async Task DeleteConversationAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "DELETE FROM conversations WHERE id = $id", "Conversation",
            cancellationToken, ("$id", conversationId)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationTurn>> LoadTurnsAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var parts = new Dictionary<long, List<TurnPart>>();
        await using (var command = Command(connection, null, """
            SELECT p.turn_id, p.sequence, p.role, p.part_type, p.content
            FROM conversation_turn_parts p JOIN conversation_turns t ON t.id = p.turn_id
            WHERE t.conversation_id = $conversation ORDER BY p.turn_id, p.sequence
            """, ("$conversation", conversationId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var turnId = reader.GetInt64(0);
                if (!parts.TryGetValue(turnId, out var list)) parts[turnId] = list = [];
                list.Add(new(reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }
        await using var turnCommand = Command(connection, null, """
            SELECT id, conversation_id, turn_number, status, error_message, created_at, finished_at,
                input_tokens, cached_input_tokens, output_tokens, context_tokens
            FROM conversation_turns WHERE conversation_id = $conversation ORDER BY turn_number
            """, ("$conversation", conversationId));
        await using var turnReader = await turnCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var turns = new List<ConversationTurn>();
        while (await turnReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = turnReader.GetInt64(0);
            turns.Add(new(id, turnReader.GetInt64(1), turnReader.GetInt32(2),
                Enum.Parse<TurnStatus>(turnReader.GetString(3), ignoreCase: true),
                turnReader.IsDBNull(4) ? null : turnReader.GetString(4),
                ParseTime(turnReader.GetString(5)),
                turnReader.IsDBNull(6) ? null : ParseTime(turnReader.GetString(6)),
                parts.TryGetValue(id, out var list) ? list : [],
                turnReader.IsDBNull(7) ? null : new(turnReader.GetInt64(7), turnReader.GetInt64(8), turnReader.GetInt64(9),
                    turnReader.IsDBNull(10) ? null : turnReader.GetInt64(10))));
        }
        return turns;
    }

    /// <summary>Saves the prompt and its images as a running turn before the CLI starts, so a failed launch never loses them.</summary>
    public async Task<ConversationTurn> StartTurnAsync(long conversationId, string prompt,
        IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var now = Now();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await ExecuteSingleAsync(connection, transaction, "UPDATE conversations SET updated_at = $now WHERE id = $id",
            "Conversation", cancellationToken, ("$id", conversationId), ("$now", now)).ConfigureAwait(false);
        long id;
        int number;
        await using (var command = Command(connection, transaction, """
            INSERT INTO conversation_turns (conversation_id, turn_number, status, created_at)
            VALUES ($conversation,
                (SELECT COALESCE(MAX(turn_number), 0) + 1 FROM conversation_turns WHERE conversation_id = $conversation),
                'running', $now)
            RETURNING id, turn_number
            """, ("$conversation", conversationId), ("$now", now)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            id = reader.GetInt64(0);
            number = reader.GetInt32(1);
        }
        TurnPart[] parts = [new(1, "user", TurnParts.Text, prompt), .. (images ?? []).Select((image, index) => TurnParts.FromImage(index + 2, image))];
        foreach (var part in parts)
            await InsertPartAsync(connection, transaction, id, part, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(id, conversationId, number, TurnStatus.Running, null, ParseTime(now), null, parts);
    }

    /// <summary>Adds the response parts and records how the turn ended.</summary>
    public async Task FinishTurnAsync(long turnId, TurnStatus status, string? errorMessage, IReadOnlyList<TurnPart> responseParts,
        TurnUsage? usage = null, CancellationToken cancellationToken = default)
    {
        if (status == TurnStatus.Running) throw new ArgumentException("A finished turn needs a final status.", nameof(status));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        foreach (var part in responseParts)
            await InsertPartAsync(connection, transaction, turnId, part, cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, transaction, """
            UPDATE conversation_turns SET status = $status, error_message = $error, finished_at = $now,
                input_tokens = $input, cached_input_tokens = $cached, output_tokens = $output, context_tokens = $context
            WHERE id = $id
            """, "Turn", cancellationToken, ("$id", turnId), ("$status", status.ToString().ToLowerInvariant()),
            ("$error", errorMessage), ("$now", Now()), ("$input", usage?.InputTokens), ("$cached", usage?.CachedInputTokens),
            ("$output", usage?.OutputTokens), ("$context", usage?.ContextTokens)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaudeCodeSessionState?> LoadClaudeCodeSessionAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var value = await ScalarAsync(connection, null, "SELECT claude_session FROM conversations WHERE id = $id",
            cancellationToken, ("$id", conversationId)).ConfigureAwait(false);
        return value is string json ? JsonSerializer.Deserialize<ClaudeCodeSessionState>(json) : null;
    }

    public async Task SaveClaudeCodeSessionAsync(long conversationId, ClaudeCodeSessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SessionId is not null && !Guid.TryParse(state.SessionId, out _))
            throw new ArgumentException("Claude Code session must be a UUID.", nameof(state));
        ArgumentException.ThrowIfNullOrWhiteSpace(state.WorkingDirectory);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversations SET claude_session = $state WHERE id = $id",
            "Conversation", cancellationToken, ("$id", conversationId), ("$state", JsonSerializer.Serialize(state)))
            .ConfigureAwait(false);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync(connection, null, "SELECT value FROM settings WHERE key = $key",
            cancellationToken, ("$key", key)).ConfigureAwait(false) as string;
    }

    /// <summary>A null value removes the setting.</summary>
    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, value is null
                ? "DELETE FROM settings WHERE key = $key"
                : "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            cancellationToken, ("$key", key), ("$value", value)).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteSingleAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        string entity, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        if (await ExecuteAsync(connection, transaction, sql, cancellationToken, parameters).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"{entity} no longer exists.");
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    private static Task<int> InsertPartAsync(SqliteConnection connection, SqliteTransaction transaction, long turnId, TurnPart part,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO conversation_turn_parts (turn_id, sequence, role, part_type, content)
            VALUES ($turn, $sequence, $role, $type, $content)
            """, cancellationToken, ("$turn", turnId), ("$sequence", part.Sequence), ("$role", part.Role),
            ("$type", part.PartType), ("$content", part.Content));

    private static Conversation ReadConversation(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            ParseTime(reader.GetString(5)));

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
