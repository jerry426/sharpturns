namespace SharpTurns.Core.Persistence;

/// <summary>
/// Schema scripts in order; script N brings the database to PRAGMA user_version N + 1.
/// Append new scripts. Never edit one that has been merged to main.
/// </summary>
internal static class Migrations
{
    public static readonly string[] Scripts =
    [
        """
        CREATE TABLE projects (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            working_directory TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE conversations (
            id INTEGER PRIMARY KEY,
            project_id INTEGER NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
            title TEXT NOT NULL,
            model TEXT,
            effort TEXT,
            claude_session TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE INDEX conversations_project ON conversations (project_id, updated_at);

        CREATE TABLE conversation_turns (
            id INTEGER PRIMARY KEY,
            conversation_id INTEGER NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
            turn_number INTEGER NOT NULL,
            status TEXT NOT NULL,
            error_message TEXT,
            created_at TEXT NOT NULL,
            finished_at TEXT,
            UNIQUE (conversation_id, turn_number)
        );

        CREATE TABLE conversation_turn_parts (
            id INTEGER PRIMARY KEY,
            turn_id INTEGER NOT NULL REFERENCES conversation_turns (id) ON DELETE CASCADE,
            sequence INTEGER NOT NULL,
            role TEXT NOT NULL,
            part_type TEXT NOT NULL,
            content TEXT NOT NULL,
            UNIQUE (turn_id, sequence)
        );

        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
        """
        ALTER TABLE conversation_turns ADD COLUMN input_tokens INTEGER;
        ALTER TABLE conversation_turns ADD COLUMN cached_input_tokens INTEGER;
        ALTER TABLE conversation_turns ADD COLUMN output_tokens INTEGER;
        ALTER TABLE conversation_turns ADD COLUMN context_tokens INTEGER;
        """,
        """
        ALTER TABLE conversation_turns ADD COLUMN model TEXT;
        ALTER TABLE conversation_turns ADD COLUMN request_count INTEGER;
        ALTER TABLE conversation_turns ADD COLUMN first_request_input_tokens INTEGER;
        ALTER TABLE conversation_turns ADD COLUMN first_request_cached_tokens INTEGER;
        """,
        """
        ALTER TABLE projects ADD COLUMN color TEXT;
        """,
        """
        ALTER TABLE conversations ADD COLUMN output_style TEXT;
        """,
        """
        ALTER TABLE conversation_turns ADD COLUMN is_hydrated INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE conversation_turns ADD COLUMN summary TEXT;
        ALTER TABLE conversation_turns ADD COLUMN summary_model TEXT;
        """,
        """
        ALTER TABLE conversations ADD COLUMN auto_summarize INTEGER NOT NULL DEFAULT 0;
        """,
        """
        CREATE TABLE conversation_user_notes (
            id INTEGER PRIMARY KEY,
            conversation_id INTEGER NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
            title TEXT NOT NULL,
            content TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE INDEX conversation_user_notes_conversation ON conversation_user_notes (conversation_id);
        """,
        """
        CREATE TABLE models (
            id TEXT PRIMARY KEY,
            position INTEGER NOT NULL
        );

        INSERT INTO models (id, position) VALUES
            ('claude-sonnet-5-5', 1),
            ('claude-opus-5-5', 2),
            ('claude-fable-5-1', 3),
            ('claude-opus-4-8', 4),
            ('claude-opus-4-7', 5),
            ('claude-sonnet-4-6', 6),
            ('claude-opus-4-6', 7),
            ('claude-haiku-4-5-20251001', 8);

        -- Earlier builds saved CLI aliases; these are the IDs the CLI ran for them.
        UPDATE conversations SET model = CASE model
            WHEN 'opus' THEN 'claude-opus-5-5' WHEN 'sonnet' THEN 'claude-sonnet-5-5' ELSE 'claude-haiku-4-5-20251001' END
        WHERE model IN ('opus', 'sonnet', 'haiku');
        UPDATE settings SET value = CASE value
            WHEN 'opus' THEN 'claude-opus-5-5' WHEN 'sonnet' THEN 'claude-sonnet-5-5' ELSE 'claude-haiku-4-5-20251001' END
        WHERE key IN ('default_model', 'summarizer_model') AND value IN ('opus', 'sonnet', 'haiku');

        -- Every conversation names its model: the new conversation model if listed, otherwise the first listed one.
        UPDATE conversations SET model = COALESCE(
            (SELECT s.value FROM settings s JOIN models m ON m.id = s.value WHERE s.key = 'default_model'),
            (SELECT id FROM models ORDER BY position LIMIT 1))
        WHERE model IS NULL;
        """,
        """
        CREATE TABLE mcp_servers (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            display_name TEXT NOT NULL,
            description TEXT,
            command TEXT NOT NULL,
            env TEXT,
            working_directory TEXT,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """,
        """
        ALTER TABLE conversations ADD COLUMN working_directory TEXT;
        ALTER TABLE conversations ADD COLUMN is_protected INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE conversation_mcp_servers (
            conversation_id INTEGER NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
            mcp_server_id INTEGER NOT NULL REFERENCES mcp_servers (id) ON DELETE CASCADE,
            PRIMARY KEY (conversation_id, mcp_server_id)
        );
        """,
        """
        CREATE TABLE conversation_context_files (
            conversation_id INTEGER NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            path TEXT NOT NULL COLLATE NOCASE,
            role TEXT NOT NULL,
            purpose TEXT,
            enabled INTEGER NOT NULL DEFAULT 1,
            required INTEGER NOT NULL DEFAULT 0,
            allow_model_maintenance INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (conversation_id, position),
            UNIQUE (conversation_id, path)
        );
        """,
    ];
}
