using System.Security.Cryptography;
using System.Text;

namespace SharpTurns.ClaudeCli;

/// <summary>The reviewed native coding surface, shared by CLI launch and host permission handling.</summary>
public static class ClaudeCliCodingPolicy
{
    // Bump for tool-policy or session-compatibility changes: native snapshots survive resumes.
    // The system prompt is hashed separately by VersionFor.
    private const string LaunchProfile = "sharpturns-2";
    public const string Description = "Native coding tools run without per-action approval; Git approval is instruction-based.";
    private const string CommonTools = "Read,Glob,Grep,Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch";
    public static string AutomaticTools => OperatingSystem.IsWindows() ? CommonTools + ",PowerShell" : CommonTools;
    public static string AvailableTools => AutomaticTools + ",AskUserQuestion";
    // Pattern-based process killers can stop unrelated apps; block them even when the user's settings don't.
    public const string DeniedTools = "Agent,Task,Workflow,Bash(pkill:*),Bash(killall:*),Bash(/usr/bin/pkill:*),Bash(/usr/bin/killall:*),mcp__*";
    // Overrides the user's settings files, which load so the CLI keeps discovering CLAUDE.md.
    // The app owns context management, so auto-compact stays off.
    public const string Settings = "{\"fallbackModel\":[],\"switchModelsOnFlag\":false,\"disableAllHooks\":true,\"disableClaudeAiConnectors\":true,\"autoCompactEnabled\":false}";
    // The CLI's built-in styles, as in the Workbench. Null leaves the style to the user's settings files.
    public static readonly IReadOnlyList<string> OutputStyles = ["Proactive", "Concise", "Explanatory", "Learning"];

    public static string SettingsFor(string? outputStyle)
    {
        if (outputStyle is null) return Settings;
        if (!OutputStyles.Contains(outputStyle, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported Claude CLI output style.", nameof(outputStyle));
        return Settings[..^1] + ",\"outputStyle\":\"" + outputStyle + "\"}";
    }

    public const string DefaultSystemPrompt = """
        You are running inside SharpTurns, a desktop conversation UI for the Claude Code CLI. Claude Code owns tool execution.
        The user interacts with you through SharpTurns' graphical conversation UI, not an interactive terminal.
        Your response text is streamed and rendered as Markdown. Native tool activity appears in separate collapsible
        cards. AskUserQuestion opens a graphical dialog, and the user's answers are returned to you within the same turn. Do not assume the user can see raw terminal output or use Claude Code
        terminal shortcuts or slash commands.
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
    /// </summary>
    public static string VersionFor(string systemPrompt, string? outputStyle = null) =>
        LaunchProfile + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(systemPrompt)))[..16]
        + (outputStyle is null ? "" : ":" + outputStyle);

    public static bool AllowsAutomatically(string toolName) =>
        AutomaticTools.Split(',').Contains(toolName, StringComparer.Ordinal);
}
