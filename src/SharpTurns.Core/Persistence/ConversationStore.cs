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
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Applies pending migrations. Call once at startup. Several instances can share the file, so each migration
    /// re-reads the version inside its write transaction and skips one another instance already applied.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL", cancellationToken).ConfigureAwait(false);
        while (true)
        {
            // BEGIN IMMEDIATE, so a second instance waits here rather than applying the same migration.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var version = Convert.ToInt32(await ScalarAsync(connection, transaction, "PRAGMA user_version", cancellationToken)
                .ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version > Migrations.Scripts.Length)
                throw new InvalidOperationException(
                    $"The database schema (version {version}) is newer than this build supports ({Migrations.Scripts.Length}).");
            if (version == Migrations.Scripts.Length) break;
            await ExecuteAsync(connection, transaction, Migrations.Scripts[version], cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {version + 1}", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Conversations with a turn still marked running, by this instance or another, or by one that exited.</summary>
    public async Task<IReadOnlyList<long>> ListConversationsWithRunningTurnsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "SELECT DISTINCT conversation_id FROM conversation_turns WHERE status = 'running' ORDER BY conversation_id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>Closes the conversation's turns an exited instance left running. Call only while holding its lock.</summary>
    public async Task FailRunningTurnsAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE conversation_turns SET status = 'failed', error_message = $message, finished_at = $now
            WHERE conversation_id = $conversation AND status = 'running'
            """, cancellationToken, ("$conversation", conversationId), ("$message", "The app closed before this turn finished."),
            ("$now", Now())).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "SELECT id, name, working_directory, color FROM projects ORDER BY name COLLATE NOCASE, id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var projects = new List<Project>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            projects.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                ProjectColor.Normalize(reader.IsDBNull(3) ? null : reader.GetString(3))));
        return projects;
    }

    /// <summary>An invalid or missing color uses ProjectColor.Default.</summary>
    public async Task<Project> CreateProjectAsync(string name, string workingDirectory, string? color = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        color = ProjectColor.Normalize(color);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = (long)(await ScalarAsync(connection, null,
            "INSERT INTO projects (name, working_directory, color, created_at) VALUES ($name, $directory, $color, $now) RETURNING id",
            cancellationToken, ("$name", name), ("$directory", workingDirectory), ("$color", color), ("$now", Now()))
            .ConfigureAwait(false))!;
        return new(id, name, workingDirectory, color);
    }

    public async Task UpdateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.WorkingDirectory);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null,
            "UPDATE projects SET name = $name, working_directory = $directory, color = $color WHERE id = $id", "Project",
            cancellationToken, ("$id", project.Id), ("$name", project.Name), ("$directory", project.WorkingDirectory),
            ("$color", ProjectColor.Normalize(project.Color))).ConfigureAwait(false);
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
        await using var command = Command(connection, null, $"""
            SELECT {ConversationColumns} FROM conversations
            WHERE project_id = $project ORDER BY updated_at DESC, id DESC
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var conversations = new List<Conversation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            conversations.Add(ReadConversation(reader));
        return conversations;
    }

    /// <summary>Null when the conversation was deleted, for example by another instance.</summary>
    public async Task<Conversation?> GetConversationAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT {ConversationColumns} FROM conversations WHERE id = $id
            """, ("$id", conversationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConversation(reader) : null;
    }

    /// <summary>A null model or effort leaves the choice to the CLI's own default. New conversations start with Auto-Summarize on.</summary>
    public async Task<Conversation> CreateConversationAsync(long projectId, string title, string? model = null, string? effort = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            INSERT INTO conversations (project_id, title, model, effort, created_at, updated_at, auto_summarize)
            VALUES ($project, $title, $model, $effort, $now, $now, 1)
            RETURNING {ConversationColumns}
            """, ("$project", projectId), ("$title", title), ("$model", model), ("$effort", effort), ("$now", Now()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadConversation(reader);
    }

    /// <summary>
    /// Saves the Edit Conversation dialog: the title, the project (a different one moves the conversation), the
    /// workspace (null uses the project's), protection, the summarizer model and effort (null uses the default's), the
    /// MCP servers its turns start, its context files in order, and its additional folder access, one entry per line.
    /// </summary>
    public async Task<Conversation> UpdateConversationAsync(long conversationId, string title, long projectId,
        string? workingDirectory, bool isProtected, string? summarizerModel, string? summarizerEffort,
        IReadOnlyCollection<long> mcpServerIds, IReadOnlyList<ContextFile> contextFiles,
        string? readOnlyFolders = null, string? readWriteFolders = null, string? blockedPathPatterns = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (workingDirectory is not null) ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (contextFiles.Count > ContextFiles.MaxFiles)
            throw new ArgumentException($"A conversation can have at most {ContextFiles.MaxFiles} context files.", nameof(contextFiles));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        Conversation conversation;
        await using (var command = Command(connection, transaction, $"""
            UPDATE conversations SET title = $title, project_id = $project, working_directory = $directory,
                is_protected = $protected, summarizer_model = $summarizerModel, summarizer_effort = $summarizerEffort,
                read_only_folders = $readOnly, read_write_folders = $readWrite, blocked_path_patterns = $blocked
            WHERE id = $id
            RETURNING {ConversationColumns}
            """, ("$id", conversationId), ("$title", title), ("$project", projectId), ("$directory", workingDirectory),
            ("$protected", isProtected), ("$summarizerModel", summarizerModel), ("$summarizerEffort", summarizerEffort),
            ("$readOnly", Conversation.NormalizeLines(readOnlyFolders)),
            ("$readWrite", Conversation.NormalizeLines(readWriteFolders)),
            ("$blocked", Conversation.NormalizeLines(blockedPathPatterns))))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Conversation no longer exists.");
            conversation = ReadConversation(reader);
        }
        await ExecuteAsync(connection, transaction, "DELETE FROM conversation_mcp_servers WHERE conversation_id = $id",
            cancellationToken, ("$id", conversationId)).ConfigureAwait(false);
        foreach (var serverId in mcpServerIds)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO conversation_mcp_servers (conversation_id, mcp_server_id) VALUES ($id, $server)",
                cancellationToken, ("$id", conversationId), ("$server", serverId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM conversation_context_files WHERE conversation_id = $id",
            cancellationToken, ("$id", conversationId)).ConfigureAwait(false);
        for (var i = 0; i < contextFiles.Count; i++)
        {
            var file = contextFiles[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(file.Path, nameof(contextFiles));
            if (!ContextFileRoles.All.Contains(file.Role))
                throw new ArgumentException($"Unsupported context file role '{file.Role}'.", nameof(contextFiles));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO conversation_context_files
                    (conversation_id, position, path, role, purpose, enabled, required, allow_model_maintenance)
                VALUES ($id, $position, $path, $role, $purpose, $enabled, $required, $maintain)
                """, cancellationToken, ("$id", conversationId), ("$position", i), ("$path", file.Path), ("$role", file.Role),
                ("$purpose", string.IsNullOrWhiteSpace(file.Purpose) ? null : file.Purpose.Trim()), ("$enabled", file.Enabled),
                ("$required", file.Required), ("$maintain", file.AllowModelMaintenance)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    /// <summary>The conversation's context files in order, including disabled ones.</summary>
    public async Task<IReadOnlyList<ContextFile>> ListContextFilesAsync(long conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT path, role, purpose, enabled, required, allow_model_maintenance FROM conversation_context_files
            WHERE conversation_id = $conversation ORDER BY position
            """, ("$conversation", conversationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<ContextFile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            files.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        return files;
    }

    public async Task SetConversationModelAsync(long conversationId, string? model, string? effort,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversations SET model = $model, effort = $effort WHERE id = $id",
            "Conversation", cancellationToken, ("$id", conversationId), ("$model", model), ("$effort", effort))
            .ConfigureAwait(false);
    }

    public async Task SetConversationOutputStyleAsync(long conversationId, string? outputStyle,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversations SET output_style = $style WHERE id = $id",
            "Conversation", cancellationToken, ("$id", conversationId), ("$style", outputStyle)).ConfigureAwait(false);
    }

    public async Task SetConversationAutoSummarizeAsync(long conversationId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversations SET auto_summarize = $enabled WHERE id = $id",
            "Conversation", cancellationToken, ("$id", conversationId), ("$enabled", enabled)).ConfigureAwait(false);
    }

    public async Task DeleteConversationAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "DELETE FROM conversations WHERE id = $id", "Conversation",
            cancellationToken, ("$id", conversationId)).ConfigureAwait(false);
    }

    /// <summary>The conversation's notes in the order they were created.</summary>
    public async Task<IReadOnlyList<ConversationNote>> ListNotesAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT {NoteColumns} FROM conversation_user_notes WHERE conversation_id = $conversation ORDER BY id
            """, ("$conversation", conversationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var notes = new List<ConversationNote>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            notes.Add(ReadNote(reader));
        return notes;
    }

    public async Task<int> CountNotesAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(await ScalarAsync(connection, null,
            "SELECT COUNT(*) FROM conversation_user_notes WHERE conversation_id = $conversation",
            cancellationToken, ("$conversation", conversationId)).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>A note needs a title and content.</summary>
    public async Task<ConversationNote> CreateNoteAsync(long conversationId, string title, string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSingleNoteAsync(connection, $"""
            INSERT INTO conversation_user_notes (conversation_id, title, content, created_at, updated_at)
            VALUES ($conversation, $title, $content, $now, $now)
            RETURNING {NoteColumns}
            """, cancellationToken, ("$conversation", conversationId), ("$title", title), ("$content", content),
            ("$now", Now())).ConfigureAwait(false);
    }

    public async Task<ConversationNote> UpdateNoteAsync(long noteId, string title, string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSingleNoteAsync(connection, $"""
            UPDATE conversation_user_notes SET title = $title, content = $content, updated_at = $now WHERE id = $id
            RETURNING {NoteColumns}
            """, cancellationToken, ("$id", noteId), ("$title", title), ("$content", content), ("$now", Now()))
            .ConfigureAwait(false);
    }

    public async Task DeleteNoteAsync(long noteId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "DELETE FROM conversation_user_notes WHERE id = $id", "Note",
            cancellationToken, ("$id", noteId)).ConfigureAwait(false);
    }

    /// <summary>The model IDs the model pickers offer, in the user's order.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT id FROM models ORDER BY position");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var models = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            models.Add(reader.GetString(0));
        return models;
    }

    /// <summary>Adds the ID at the end of the list.</summary>
    public async Task AddModelAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "INSERT INTO models (id, position) VALUES ($id, (SELECT COALESCE(MAX(position), 0) + 1 FROM models))",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
    }

    /// <summary>Saves the list's order; ids holds every listed ID.</summary>
    public async Task SetModelOrderAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        for (var i = 0; i < ids.Count; i++)
            await ExecuteSingleAsync(connection, transaction, "UPDATE models SET position = $position WHERE id = $id", "Model",
                cancellationToken, ("$id", ids[i]), ("$position", i + 1)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountConversationsUsingModelAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(await ScalarAsync(connection, null,
            "SELECT COUNT(*) FROM conversations WHERE model = $id OR summarizer_model = $id",
            cancellationToken, ("$id", id)).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>Renames the ID in place, along with the conversations and the given settings that use it.</summary>
    public async Task RenameModelAsync(string id, string newId, IReadOnlyCollection<string> settingKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await ExecuteSingleAsync(connection, transaction, "UPDATE models SET id = $new WHERE id = $id", "Model",
            cancellationToken, ("$id", id), ("$new", newId)).ConfigureAwait(false);
        await ReplaceModelUsesAsync(connection, transaction, id, newId, settingKeys, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the ID; the conversations and the given settings that use it move to the replacement, if given.</summary>
    public async Task DeleteModelAsync(string id, string? replacement, IReadOnlyCollection<string> settingKeys,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await ExecuteSingleAsync(connection, transaction, "DELETE FROM models WHERE id = $id", "Model",
            cancellationToken, ("$id", id)).ConfigureAwait(false);
        if (replacement is not null)
            await ReplaceModelUsesAsync(connection, transaction, id, replacement, settingKeys, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceModelUsesAsync(SqliteConnection connection, SqliteTransaction transaction, string id,
        string newId, IReadOnlyCollection<string> settingKeys, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "UPDATE conversations SET model = $new WHERE model = $id",
            cancellationToken, ("$id", id), ("$new", newId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE conversations SET summarizer_model = $new WHERE summarizer_model = $id",
            cancellationToken, ("$id", id), ("$new", newId)).ConfigureAwait(false);
        foreach (var key in settingKeys)
            await ExecuteAsync(connection, transaction, "UPDATE settings SET value = $new WHERE key = $key AND value = $id",
                cancellationToken, ("$id", id), ("$new", newId), ("$key", key)).ConfigureAwait(false);
    }

    /// <summary>The MCP server definitions by name.</summary>
    public async Task<IReadOnlyList<McpServer>> ListMcpServersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT {McpServerColumns} FROM mcp_servers ORDER BY name");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var servers = new List<McpServer>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            servers.Add(ReadMcpServer(reader));
        return servers;
    }

    /// <summary>The name is unique, ignoring case.</summary>
    public async Task<McpServer> CreateMcpServerAsync(string name, string displayName, string? description, string commandJson,
        string? envJson, string? workingDirectory, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandJson);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSingleMcpServerAsync(connection, $"""
            INSERT INTO mcp_servers (name, display_name, description, command, env, working_directory, enabled, created_at, updated_at)
            VALUES ($name, $display, $description, $command, $env, $directory, $enabled, $now, $now)
            RETURNING {McpServerColumns}
            """, cancellationToken, ("$name", name), ("$display", displayName), ("$description", description),
            ("$command", commandJson), ("$env", envJson), ("$directory", workingDirectory), ("$enabled", enabled),
            ("$now", Now())).ConfigureAwait(false);
    }

    /// <summary>Conversations select servers by ID, so a rename keeps their selections.</summary>
    public async Task<McpServer> UpdateMcpServerAsync(long serverId, string name, string displayName, string? description,
        string commandJson, string? envJson, string? workingDirectory, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandJson);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSingleMcpServerAsync(connection, $"""
            UPDATE mcp_servers SET name = $name, display_name = $display, description = $description, command = $command,
                env = $env, working_directory = $directory, enabled = $enabled, updated_at = $now
            WHERE id = $id
            RETURNING {McpServerColumns}
            """, cancellationToken, ("$id", serverId), ("$name", name), ("$display", displayName), ("$description", description),
            ("$command", commandJson), ("$env", envJson), ("$directory", workingDirectory), ("$enabled", enabled),
            ("$now", Now())).ConfigureAwait(false);
    }

    /// <summary>Deleting a server also removes it from every conversation that selected it.</summary>
    public async Task DeleteMcpServerAsync(long serverId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "DELETE FROM mcp_servers WHERE id = $id", "MCP server",
            cancellationToken, ("$id", serverId)).ConfigureAwait(false);
    }

    /// <summary>The servers the conversation selected, by name, including disabled ones.</summary>
    public async Task<IReadOnlyList<McpServer>> ListConversationMcpServersAsync(long conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT {McpServerColumns} FROM mcp_servers
            WHERE id IN (SELECT mcp_server_id FROM conversation_mcp_servers WHERE conversation_id = $conversation)
            ORDER BY name
            """, ("$conversation", conversationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var servers = new List<McpServer>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            servers.Add(ReadMcpServer(reader));
        return servers;
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
                input_tokens, cached_input_tokens, output_tokens, context_tokens,
                request_count, first_request_input_tokens, first_request_cached_tokens, model,
                is_hydrated, summary, summary_model
            FROM conversation_turns WHERE conversation_id = $conversation ORDER BY turn_number
            """, ("$conversation", conversationId));
        await using var turnReader = await turnCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var turns = new List<ConversationTurn>();
        while (await turnReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = turnReader.GetInt64(0);
            long? Optional(int ordinal) => turnReader.IsDBNull(ordinal) ? null : turnReader.GetInt64(ordinal);
            turns.Add(new(id, turnReader.GetInt64(1), turnReader.GetInt32(2),
                Enum.Parse<TurnStatus>(turnReader.GetString(3), ignoreCase: true),
                turnReader.IsDBNull(4) ? null : turnReader.GetString(4),
                ParseTime(turnReader.GetString(5)),
                turnReader.IsDBNull(6) ? null : ParseTime(turnReader.GetString(6)),
                parts.TryGetValue(id, out var list) ? list : [],
                turnReader.IsDBNull(7) ? null : new(turnReader.GetInt64(7), turnReader.GetInt64(8), turnReader.GetInt64(9),
                    Optional(10), (int?)Optional(11), Optional(12), Optional(13)),
                turnReader.IsDBNull(14) ? null : turnReader.GetString(14),
                turnReader.GetBoolean(15),
                turnReader.IsDBNull(16) ? null : turnReader.GetString(16),
                turnReader.IsDBNull(17) ? null : turnReader.GetString(17)));
        }
        return turns;
    }

    /// <summary>A hidden turn stays saved and shown but is left out of the replayed context.</summary>
    public async Task SetTurnHydratedAsync(long turnId, bool isHydrated, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversation_turns SET is_hydrated = $hydrated WHERE id = $id", "Turn",
            cancellationToken, ("$id", turnId), ("$hydrated", isHydrated)).ConfigureAwait(false);
    }

    /// <summary>Hides or shows several turns together, for the Context Management tab's bulk actions and Smart Cleanup.</summary>
    public async Task SetTurnsHydratedAsync(IReadOnlyCollection<(long TurnId, bool IsHydrated)> changes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        foreach (var (turnId, isHydrated) in changes)
            await ExecuteSingleAsync(connection, transaction, "UPDATE conversation_turns SET is_hydrated = $hydrated WHERE id = $id",
                "Turn", cancellationToken, ("$id", turnId), ("$hydrated", isHydrated)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a compressed turn's summary, or clears it (with a null summary) to expand the turn.</summary>
    public async Task SetTurnSummaryAsync(long turnId, string? summary, string? summaryModel,
        CancellationToken cancellationToken = default)
    {
        if (summary is not null) ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "UPDATE conversation_turns SET summary = $summary, summary_model = $model WHERE id = $id",
            "Turn", cancellationToken, ("$id", turnId), ("$summary", summary), ("$model", summary is null ? null : summaryModel))
            .ConfigureAwait(false);
    }

    /// <summary>Chooses whether an image part stays in the replay once its turn is compressed.</summary>
    public async Task SetImageReplayAsync(long turnId, int sequence, bool include, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var content = await ScalarAsync(connection, transaction,
                "SELECT content FROM conversation_turn_parts WHERE turn_id = $turn AND sequence = $sequence AND part_type = $type",
                cancellationToken, ("$turn", turnId), ("$sequence", sequence), ("$type", TurnParts.Image)).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException("Image no longer exists.");
        var part = TurnParts.WithImageReplay(new(sequence, "user", TurnParts.Image, content), include);
        await ExecuteSingleAsync(connection, transaction,
            "UPDATE conversation_turn_parts SET content = $content WHERE turn_id = $turn AND sequence = $sequence", "Image",
            cancellationToken, ("$turn", turnId), ("$sequence", sequence), ("$content", part.Content)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Adds the response parts and records how the turn ended. Model is the model the CLI reported.</summary>
    public async Task FinishTurnAsync(long turnId, TurnStatus status, string? errorMessage, IReadOnlyList<TurnPart> responseParts,
        TurnUsage? usage = null, string? model = null, CancellationToken cancellationToken = default)
    {
        if (status == TurnStatus.Running) throw new ArgumentException("A finished turn needs a final status.", nameof(status));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        foreach (var part in responseParts)
            await InsertPartAsync(connection, transaction, turnId, part, cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, transaction, """
            UPDATE conversation_turns SET status = $status, error_message = $error, finished_at = $now,
                input_tokens = $input, cached_input_tokens = $cached, output_tokens = $output, context_tokens = $context,
                request_count = $requests, first_request_input_tokens = $firstInput,
                first_request_cached_tokens = $firstCached, model = $model
            WHERE id = $id
            """, "Turn", cancellationToken, ("$id", turnId), ("$status", status.ToString().ToLowerInvariant()),
            ("$error", errorMessage), ("$now", Now()), ("$input", usage?.InputTokens), ("$cached", usage?.CachedInputTokens),
            ("$output", usage?.OutputTokens), ("$context", usage?.ContextTokens), ("$requests", usage?.Requests),
            ("$firstInput", usage?.FirstRequestInputTokens), ("$firstCached", usage?.FirstRequestCachedTokens),
            ("$model", model)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the turn and its parts. Later turns keep their numbers.</summary>
    public async Task DeleteTurnAsync(long turnId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteSingleAsync(connection, null, "DELETE FROM conversation_turns WHERE id = $id", "Turn",
            cancellationToken, ("$id", turnId)).ConfigureAwait(false);
    }

    /// <summary>Deletes several turns together; either all are deleted or none.</summary>
    public async Task DeleteTurnsAsync(IReadOnlyCollection<long> turnIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        foreach (var turnId in turnIds)
            await ExecuteSingleAsync(connection, transaction, "DELETE FROM conversation_turns WHERE id = $id", "Turn",
                cancellationToken, ("$id", turnId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a contiguous run of the conversation's turns, renumbered from 1, with their parts and
    /// context state into a new conversation with the same project, title, workspace, MCP servers, context files, folder
    /// access, and settings. The
    /// branch starts unprotected, and the source is unchanged. The
    /// branch has no CLI session, so its first turn reseeds from the copied history. Throws unless turnIds are every
    /// turn between the first and last of them, none still running.
    /// </summary>
    public async Task<Conversation> BranchConversationAsync(long conversationId, IReadOnlyCollection<long> turnIds,
        CancellationToken cancellationToken = default)
    {
        if (turnIds.Count == 0) throw new ArgumentException("Choose at least one turn to branch.", nameof(turnIds));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var turns = new List<(long Id, string Status)>();
        await using (var command = Command(connection, transaction,
            "SELECT id, status FROM conversation_turns WHERE conversation_id = $conversation ORDER BY turn_number",
            ("$conversation", conversationId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                turns.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        var chosen = turnIds.ToHashSet();
        var first = turns.FindIndex(t => chosen.Contains(t.Id));
        var run = turns.Skip(Math.Max(first, 0)).Take(chosen.Count).ToArray();
        if (chosen.Count != turnIds.Count || run.Length != chosen.Count || !run.All(t => chosen.Contains(t.Id)))
            throw new InvalidOperationException("The turns to branch must be every turn from the first chosen to the last.");
        if (run.Any(t => t.Status == "running")) throw new InvalidOperationException("A running turn can't be branched.");

        var now = Now();
        Conversation branch;
        await using (var command = Command(connection, transaction, $"""
            INSERT INTO conversations (project_id, title, model, effort, output_style, auto_summarize, working_directory,
                summarizer_model, summarizer_effort, read_only_folders, read_write_folders, blocked_path_patterns,
                created_at, updated_at)
            SELECT project_id, title, model, effort, output_style, auto_summarize, working_directory,
                summarizer_model, summarizer_effort, read_only_folders, read_write_folders, blocked_path_patterns, $now, $now
            FROM conversations WHERE id = $source
            RETURNING {ConversationColumns}
            """, ("$source", conversationId), ("$now", now)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Conversation no longer exists.");
            branch = ReadConversation(reader);
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO conversation_mcp_servers (conversation_id, mcp_server_id)
            SELECT $branch, mcp_server_id FROM conversation_mcp_servers WHERE conversation_id = $source
            """, cancellationToken, ("$branch", branch.Id), ("$source", conversationId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO conversation_context_files
                (conversation_id, position, path, role, purpose, enabled, required, allow_model_maintenance)
            SELECT $branch, position, path, role, purpose, enabled, required, allow_model_maintenance
            FROM conversation_context_files WHERE conversation_id = $source
            """, cancellationToken, ("$branch", branch.Id), ("$source", conversationId)).ConfigureAwait(false);
        for (var i = 0; i < run.Length; i++)
        {
            var turnId = (long)(await ScalarAsync(connection, transaction, $"""
                INSERT INTO conversation_turns (conversation_id, turn_number, {BranchedTurnColumns})
                SELECT $branch, $number, {BranchedTurnColumns} FROM conversation_turns WHERE id = $turn
                RETURNING id
                """, cancellationToken, ("$branch", branch.Id), ("$number", i + 1), ("$turn", run[i].Id)).ConfigureAwait(false))!;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO conversation_turn_parts (turn_id, sequence, role, part_type, content)
                SELECT $new, sequence, role, part_type, content FROM conversation_turn_parts WHERE turn_id = $turn
                """, cancellationToken, ("$new", turnId), ("$turn", run[i].Id)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return branch;
    }

    // Every turn column but the key, conversation, and number.
    private const string BranchedTurnColumns = """
        status, error_message, created_at, finished_at, input_tokens, cached_input_tokens, output_tokens, context_tokens,
        model, request_count, first_request_input_tokens, first_request_cached_tokens, is_hydrated, summary, summary_model
        """;

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

    /// <summary>
    /// Removes the setting and overwrites its bytes, for a secret that shouldn't linger in the file. The checkpoint
    /// copies the zeroed page into the file and empties the WAL; while another instance is reading, it does less,
    /// and the WAL's old frames are overwritten as it's reused.
    /// </summary>
    public async Task EraseSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // The connection goes back to the pool, so its secure_delete setting is restored.
        var secureDelete = Convert.ToInt64(await ScalarAsync(connection, null, "PRAGMA secure_delete", cancellationToken)
            .ConfigureAwait(false), CultureInfo.InvariantCulture);
        await ExecuteAsync(connection, null, "PRAGMA secure_delete = 1", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, "DELETE FROM settings WHERE key = $key", cancellationToken, ("$key", key))
                .ConfigureAwait(false);
        }
        finally
        {
            await ExecuteAsync(connection, null, $"PRAGMA secure_delete = {secureDelete}", CancellationToken.None)
                .ConfigureAwait(false);
        }
        await ExecuteAsync(connection, null, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
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

    private const string ConversationColumns = "id, project_id, title, model, effort, updated_at, output_style, "
        + "auto_summarize, working_directory, is_protected, summarizer_model, summarizer_effort, "
        + "read_only_folders, read_write_folders, blocked_path_patterns";

    private static Conversation ReadConversation(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            ParseTime(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));

    private const string NoteColumns = "id, conversation_id, title, content, created_at, updated_at";

    private static ConversationNote ReadNote(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            ParseTime(reader.GetString(4)), ParseTime(reader.GetString(5)));

    private static async Task<ConversationNote> ReadSingleNoteAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadNote(reader)
            : throw new InvalidOperationException("Note no longer exists.");
    }

    private const string McpServerColumns =
        "id, name, display_name, description, command, env, working_directory, enabled, created_at, updated_at";

    private static McpServer ReadMcpServer(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetBoolean(7), ParseTime(reader.GetString(8)), ParseTime(reader.GetString(9)));

    private static async Task<McpServer> ReadSingleMcpServerAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMcpServer(reader)
            : throw new InvalidOperationException("MCP server no longer exists.");
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
