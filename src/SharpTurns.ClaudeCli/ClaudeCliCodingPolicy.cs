using System.Security.Cryptography;
using System.Text;

namespace SharpTurns.ClaudeCli;

/// <summary>The reviewed native coding surface, shared by CLI launch and host permission handling.</summary>
public static class ClaudeCliCodingPolicy
{
    // Bump for tool-policy or session-compatibility changes: native snapshots survive resumes.
    // The system prompt is hashed separately by VersionFor.
    private const string LaunchProfile = "sharpturns-2";
    public const string Description = "Native coding tools run without per-action approval, except commands matching the ask rules.";
    private const string CommonTools = "Read,Glob,Grep,Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch";
    public static string AutomaticTools => OperatingSystem.IsWindows() ? CommonTools + ",PowerShell" : CommonTools;
    public static string AvailableTools => AutomaticTools + ",AskUserQuestion";
    // Pattern-based process killers can stop unrelated apps; block them even when the user's settings don't.
    public const string DeniedNativeTools = "Agent,Task,Workflow,Bash(pkill:*),Bash(killall:*),Bash(/usr/bin/pkill:*),Bash(/usr/bin/killall:*)";
    // A turn with selected MCP servers denies only the native tools; --strict-mcp-config still excludes other servers.
    public const string DeniedTools = DeniedNativeTools + ",mcp__*";
    // Overrides the user's settings files, which load so the CLI keeps discovering CLAUDE.md.
    // The app owns context management, so auto-compact stays off.
    public const string Settings = "{\"fallbackModel\":[],\"switchModelsOnFlag\":false,\"disableAllHooks\":true,\"disableClaudeAiConnectors\":true,\"autoCompactEnabled\":false}";
    // The CLI's built-in styles. Null leaves the style to the user's settings files.
    public static readonly IReadOnlyList<string> OutputStyles = ["Proactive", "Concise", "Explanatory", "Learning"];
    // Matching tool calls go to the host's approval dialog even though their tool is preapproved.
    public static readonly IReadOnlyList<string> DefaultAskRules = ["Bash(git commit:*)", "Bash(git push:*)"];

    /// <summary>A Claude Code permission rule: a tool name, optionally with a specifier in parentheses.</summary>
    public static bool IsPermissionRule(string rule) =>
        System.Text.RegularExpressions.Regex.IsMatch(rule, @"^[A-Za-z][A-Za-z0-9_-]*(\(.+\))?$");

    public static string SettingsFor(string? outputStyle, IReadOnlyList<string>? askRules = null,
        ClaudeCliFolderAccess? folderAccess = null)
    {
        if (outputStyle is not null && !OutputStyles.Contains(outputStyle, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported Claude CLI output style.", nameof(outputStyle));
        if (askRules?.FirstOrDefault(rule => !IsPermissionRule(rule)) is { } invalid)
            throw new ArgumentException($"Not a permission rule: {invalid}", nameof(askRules));
        IReadOnlyList<string> denyRules = folderAccess is null ? [] : DenyRules(folderAccess);
        if (outputStyle is null && askRules is not { Count: > 0 } && denyRules.Count == 0) return Settings;
        // Settings JSON, unlike --disallowed-tools, keeps commas and spaces in paths intact.
        var permissions = new List<string>();
        if (askRules is { Count: > 0 }) permissions.Add("\"ask\":" + System.Text.Json.JsonSerializer.Serialize(askRules));
        if (denyRules.Count > 0) permissions.Add("\"deny\":" + System.Text.Json.JsonSerializer.Serialize(denyRules));
        return Settings[..^1]
            + (outputStyle is null ? "" : ",\"outputStyle\":\"" + outputStyle + "\"")
            + (permissions.Count == 0 ? "" : ",\"permissions\":{" + string.Join(',', permissions) + "}")
            + "}";
    }

    /// <summary>
    /// Deny rules use gitignore patterns from the filesystem root. An Edit rule covers every file-editing tool;
    /// a Read rule also blocks Edit and Write but not NotebookEdit, so blocked fragments get both.
    /// </summary>
    public static IReadOnlyList<string> DenyRules(ClaudeCliFolderAccess folderAccess)
    {
        var rules = folderAccess.ReadOnlyDirectories.Select(directory => $"Edit({RootPattern(directory)}**)").ToList();
        foreach (var fragment in folderAccess.BlockedPathFragments.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            // Matches any path containing the text, and everything under a directory whose path contains it.
            // A leading slash starts at a directory boundary, which may be the root, so no * goes before it.
            var text = fragment;
            if (OperatingSystem.IsWindows())
            {
                text = text.Replace('\\', '/');
                if (text.Length >= 2 && text[1] == ':') text = "/" + char.ToLowerInvariant(text[0]) + text[2..];
            }
            var pattern = (text.StartsWith('/') ? "//**" : "//**/*") + EscapePattern(text) + "*";
            rules.AddRange([$"Read({pattern})", $"Read({pattern}/**)", $"Edit({pattern})", $"Edit({pattern}/**)"]);
        }
        return rules;
    }

    // The CLI matches Windows paths in POSIX form: C:\Users becomes //c/Users.
    private static string RootPattern(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows())
        {
            path = path.Replace('\\', '/');
            if (path.Length >= 2 && path[1] == ':') path = char.ToLowerInvariant(path[0]) + path[2..];
        }
        path = EscapePattern(path.Trim('/'));
        return path.Length == 0 ? "//" : "//" + path + "/";
    }

    private static string EscapePattern(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"[\\*?\[\]]", @"\$0");

    public const string DefaultSystemPrompt = """
        You are running inside SharpTurns, a desktop conversation UI for the Claude Code CLI. Claude Code owns tool execution.
        The user interacts with you through SharpTurns' graphical conversation UI, not an interactive terminal.
        Your response text is streamed and rendered as Markdown. Native tool activity appears in separate collapsible
        cards. AskUserQuestion opens a graphical dialog, and the user's answers are returned to you within the same turn. Do not assume the user can see raw terminal output or use Claude Code
        terminal shortcuts or slash commands.
        Each turn has a Turn ID and a Turn #. The Turn ID is a database identifier that is unique across all conversations;
        the Turn # is the turn's position within its conversation, starting at 1. Deleting or hiding a turn does not renumber
        the others. Replayed history labels each turn with turn_id and turn_number, and copied turns show "Turn ID:" and
        "Turn #" lines. Refer to turns by Turn # (for example, "Turn #3"). If the user mentions a turn by a bare number and
        it could be either, say which one you took it to be.
        Use one brief progress note before a meaningful batch of edits, validation, git operations, long-running work,
        or a retry after failure. Do not narrate routine searches, reads, or every tool call.
        Native file, search, editing, notebook, shell/code execution and network tools are preapproved.
        AskUserQuestion remains interactive and must be used when you need the user's input or approval.
        Git policy: never commit, push, or merge without the user's explicit approval for that operation.
        A request to implement, fix, test, or continue work is NOT permission to commit, push, or merge.
        This policy also applies to Git operations performed indirectly through scripts or other commands.
        Preserve unrelated user work. Never discard changes, rewrite history, force-push, or delete branches
        without explicit instruction.
        Subagents and delegation are disabled. Do not launch other agents through shell commands or scripts.
        Run requested commands in the foreground and finish them within the turn; do not leave background work.
        Never put credentials or secrets in commands, prompts, or tool descriptions.
        """;

    /// <summary>
    /// Session compatibility version. The CLI reuses a session's saved system prompt on resume, so a prompt edit
    /// or an output style change (the style is part of that prompt) must change this value and reseed the next turn.
    /// So must a change to the started MCP servers, which changes the session's tools; without any, the value is unchanged.
    /// </summary>
    public static string VersionFor(string systemPrompt, string? outputStyle = null, IReadOnlyCollection<string>? mcpServerNames = null) =>
        LaunchProfile + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(systemPrompt)))[..16]
        + (outputStyle is null ? "" : ":" + outputStyle)
        + (mcpServerNames is not { Count: > 0 } ? "" : ":mcp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', mcpServerNames.Order(StringComparer.Ordinal)))))[..16]);

    /// <summary>
    /// Tells Claude the folder access, which the deny rules alone don't show it. Null access gives the note that clears an
    /// earlier one. Each note replaces the previous, so a resumed session needs one only when the access changed.
    /// </summary>
    public static string FolderAccessNote(ClaudeCliFolderAccess? folderAccess)
    {
        var text = new StringBuilder("[FOLDER ACCESS]\n");
        if (folderAccess is null)
            text.Append("No additional folders or blocked path patterns are configured now. This replaces any earlier folder access note.\n");
        else
        {
            text.Append("The user configured this folder access for the conversation. It replaces any earlier folder access note.\n");
            void List(string heading, IReadOnlyList<string> items)
            {
                if (items.Count == 0) return;
                text.Append(heading).Append('\n');
                foreach (var item in items) text.Append("- ").Append(item).Append('\n');
            }
            List("Read/write folders:", folderAccess.ReadWriteDirectories);
            List("Read-only folders (file-tool edits are denied; don't modify them with shell commands either):",
                folderAccess.ReadOnlyDirectories);
            List("Blocked path patterns (file-tool reads and edits of paths containing the text are denied; don't access them with shell commands either):",
                folderAccess.BlockedPathFragments);
        }
        return text.Append("[/FOLDER ACCESS]").ToString();
    }

    public static bool AllowsAutomatically(string toolName, IReadOnlyCollection<string>? mcpServerNames = null) =>
        AutomaticTools.Split(',').Contains(toolName, StringComparer.Ordinal)
        // Selecting a server for a conversation authorizes its tools, like the preapproved native tools.
        || mcpServerNames?.Any(name => toolName.StartsWith(McpToolPrefix(name) + "__", StringComparison.Ordinal)) == true;

    /// <summary>The CLI's tool-name prefix and permission rule for a server; it replaces unsupported characters.</summary>
    public static string McpToolPrefix(string serverName) =>
        "mcp__" + System.Text.RegularExpressions.Regex.Replace(serverName, "[^a-zA-Z0-9_-]", "_");
}
