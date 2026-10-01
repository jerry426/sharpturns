using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.App.Services;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>The Workbench's export options for one turn or the whole conversation, with a live preview.</summary>
public sealed partial class TurnExportDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<ConversationTurn> _turns;
    private readonly Conversation _conversation;
    private readonly Project _project;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkdown), nameof(IsText), nameof(IsDocx), nameof(CanOpenInMacDown), nameof(CanCopyToClipboard),
        nameof(SaveButtonLabel), nameof(PreviewLabel), nameof(PreviewText))]
    private TurnExportFormat _format = TurnExportFormat.Markdown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private bool _includeToolDetails = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private bool _fullToolResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private bool _includeMetadata = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private bool _fullContent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private bool _includeHiddenTurns;

    [ObservableProperty]
    private string _status = "Choose export options, then copy, open in MacDown, or save to a file.";

    private TurnExportDialogViewModel(IReadOnlyList<ConversationTurn> turns, Conversation conversation, Project project,
        bool isConversationExport)
    {
        _turns = turns;
        _conversation = conversation;
        _project = project;
        IsConversationExport = isConversationExport;
    }

    public static TurnExportDialogViewModel ForConversation(IReadOnlyList<ConversationTurn> turns, Conversation conversation,
        Project project) => new(turns, conversation, project, isConversationExport: true);

    /// <summary>fullContent starts a compressed turn's export in full, as when its card shows the full turn.</summary>
    public static TurnExportDialogViewModel ForTurn(ConversationTurn turn, Conversation conversation, Project project,
        bool fullContent) => new([turn], conversation, project, isConversationExport: false) { FullContent = fullContent };

    public bool IsConversationExport { get; }

    public string Title => IsConversationExport ? "Export Conversation" : $"Export Turn {_turns[0].TurnNumber}";

    public string TurnLabel => IsConversationExport
        ? string.Create(CultureInfo.CurrentCulture, $"{_turns.Count:N0} turn{(_turns.Count == 1 ? "" : "s")} · Conversation ID {_conversation.Id}")
        : $"Turn #{_turns[0].TurnNumber} · ID {_turns[0].Id}";

    public string ConversationLabel => $"{_conversation.Title} · Conversation ID {_conversation.Id}";

    public bool HasCompressedTurns => _turns.Any(t => t.IsCompressed);

    public bool IsMarkdown
    {
        get => Format == TurnExportFormat.Markdown;
        set { if (value) Format = TurnExportFormat.Markdown; }
    }

    public bool IsText
    {
        get => Format == TurnExportFormat.Text;
        set { if (value) Format = TurnExportFormat.Text; }
    }

    public bool IsDocx
    {
        get => Format == TurnExportFormat.Docx;
        set { if (value) Format = TurnExportFormat.Docx; }
    }

    public bool CanOpenInMacDown => Format == TurnExportFormat.Markdown;

    public bool CanCopyToClipboard => Format != TurnExportFormat.Docx;

    public string SaveButtonLabel => Format == TurnExportFormat.Docx ? "Save DOCX" : "Save to File";

    public string PreviewLabel => Format == TurnExportFormat.Docx ? "Preview uses the Markdown that is converted to DOCX" : "Preview";

    public string PreviewText => GenerateContent();

    public string DefaultFileName => BaseFileName + TurnExportFormatter.Extension(Format);

    private string BaseFileName => IsConversationExport
        ? $"conversation_{_conversation.Id}_{TurnExportFormatter.SanitizeFileName(_conversation.Title)}"
        : $"turn_{_turns[0].Id}";

    /// <summary>The Markdown, or the plain text for the Text format. DOCX is written from the Markdown.</summary>
    public string GenerateContent() => Format == TurnExportFormat.Text ? TurnExportFormatter.ToText(GenerateMarkdown()) : GenerateMarkdown();

    private string GenerateMarkdown()
    {
        var options = new TurnExportOptions(IncludeToolDetails, IncludeMetadata, FullContent, FullToolResults, IncludeHiddenTurns);
        return IsConversationExport
            ? TurnExportFormatter.FormatMarkdown(_turns, _conversation, _project, options)
            : TurnExportFormatter.FormatMarkdown(_turns[0], _conversation, _project, options);
    }

    // Full tool results only apply when tool calls are exported.
    partial void OnIncludeToolDetailsChanged(bool value)
    {
        if (!value) FullToolResults = false;
    }

    /// <summary>Owns its errors: the outcome goes to the status line.</summary>
    public async Task SaveAsync(string path)
    {
        try
        {
            if (Format == TurnExportFormat.Docx) TurnExportDocxWriter.Save(GenerateMarkdown(), path);
            else await File.WriteAllTextAsync(path, GenerateContent());
            Status = $"Saved the export to {path}";
        }
        catch (Exception e) { Status = "Couldn't save the export: " + e.Message; }
    }

    /// <summary>
    /// Owns its errors. Writes the Markdown to a temporary file and opens it in MacDown on macOS, or in the default app
    /// for .md files elsewhere, as the Workbench does.
    /// </summary>
    public async Task OpenInMacDownAsync()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"sharpturns_{BaseFileName}.md");
            await File.WriteAllTextAsync(path, TurnExportFormatter.FixMacDownListSpacing(GenerateMarkdown()));
            if (OperatingSystem.IsMacOS())
            {
                using var open = Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-a", "MacDown", path } })
                    ?? throw new InvalidOperationException("open didn't start.");
                await open.WaitForExitAsync();
                if (open.ExitCode != 0)
                {
                    Status = $"Couldn't open MacDown; is it installed? The Markdown is at {path}";
                    return;
                }
            }
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            Status = $"Opened the Markdown preview: {path}";
        }
        catch (Win32Exception) { Status = "No app is set to open Markdown files."; }
        catch (Exception e) { Status = "Couldn't open the Markdown preview: " + e.Message; }
    }
}
