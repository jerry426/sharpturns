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
    ];
}
