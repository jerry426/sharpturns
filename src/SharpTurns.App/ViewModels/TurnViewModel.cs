using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>One turn card: a rail with the turn's details and actions beside its prompt and response items.</summary>
public sealed partial class TurnViewModel : ObservableObject
{
    private readonly Dictionary<string, QuestionItemViewModel> _questions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    private string? _outcome;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>The saved turn; null until a new turn is saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TurnLabel), nameof(IdLabel), nameof(TimeLabel), nameof(DurationLabel), nameof(HasDuration),
        nameof(ModelLabel), nameof(HasModel), nameof(IsHydrated), nameof(IsCompressed), nameof(HydrationActionLabel),
        nameof(HydrationActionToolTip), nameof(CompressionActionLabel), nameof(CompressionActionToolTip), nameof(CompressedLabel),
        nameof(CardBorderBrush), nameof(CardBorderThickness), nameof(RailAccentBrush),
        nameof(RailDividerBrush), nameof(HasViewFullContentButton), nameof(IsViewingFullCompressedContent),
        nameof(ViewFullContentLabel), nameof(CompactViewFullContentLabel), nameof(ReductionToolTip),
        nameof(ContextTurnLabel), nameof(ContextIdLabel), nameof(ContextTimeLabel), nameof(ContextRowBackground))]
    private ConversationTurn? _record;

    /// <summary>Checked in the Context Management tab's turn list for its bulk actions.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>A compressed turn temporarily shown in full; reverts on reload.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewFullContentButton), nameof(IsViewingFullCompressedContent))]
    private bool _isViewingFullContent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompressionActionLabel))]
    private bool _isCompressing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokenUsage), nameof(InputTokensLabel), nameof(OutputTokensLabel), nameof(CacheHitLabel),
        nameof(CacheMissLabel), nameof(CacheHitPercentage), nameof(FirstRequestCacheLabel), nameof(ContextLabel), nameof(HasContext))]
    private TurnUsage? _usage;

    /// <summary>A turn that is starting; its response streams in through the update methods.</summary>
    public TurnViewModel(string userText, IReadOnlyList<ImageAttachment> images)
    {
        UserText = userText;
        Images = images.Select((image, index) => new TurnImageViewModel(this, index, new ImageAttachmentViewModel(image))).ToArray();
        IsRunning = true;
    }

    public TurnViewModel(ConversationTurn turn)
    {
        var parts = turn.Parts.OrderBy(p => p.Sequence).ToArray();
        UserText = Prompt(parts)?.Content ?? "";
        Images = parts.Where(p => p.PartType == TurnParts.Image)
            .Select((p, index) => new TurnImageViewModel(this, index, new ImageAttachmentViewModel(TurnParts.ReadImage(p)))).ToArray();
        Finish(turn);
        RebuildItems();
    }

    public string UserText { get; }

    public IReadOnlyList<TurnImageViewModel> Images { get; }

    public bool HasImages => Images.Count > 0;

    public ObservableCollection<TurnItemViewModel> Items { get; } = [];

    public bool HasOutcome => Outcome is not null;

    public string TurnLabel => Record is { } turn ? $"Turn - {turn.TurnNumber.ToString(CultureInfo.InvariantCulture)}" : "Turn";

    public string IdLabel => Record is { } turn ? $"id: {turn.Id.ToString(CultureInfo.InvariantCulture)}" : "";

    public string TimeLabel => Record?.CreatedAt.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture) ?? "Live";

    public string DurationLabel
    {
        get
        {
            if (Record is not { FinishedAt: { } finished } turn) return "";
            var duration = finished - turn.CreatedAt;
            return duration.TotalSeconds < 60
                ? string.Create(CultureInfo.CurrentCulture, $"Duration: {duration.TotalSeconds:0.#}s")
                : string.Create(CultureInfo.CurrentCulture, $"Duration: {duration.TotalMinutes:0.#}m");
        }
    }

    public bool HasDuration => DurationLabel.Length > 0;

    /// <summary>The model the CLI reported, such as claude-haiku-4-5-20251001.</summary>
    public string ModelLabel => Record?.Model ?? "";

    public bool HasModel => ModelLabel.Length > 0;

    // Totals arrive with each agent cycle's result, so a running turn shows them once its first cycle ends.
    public bool HasTokenUsage => Usage is { InputTokens: > 0 };

    public string InputTokensLabel => Usage is { } usage
        ? $"Input Tokens: {Count(usage.InputTokens)}" + (usage.Requests is { } requests
            ? $" · {Count(requests)} request{(requests == 1 ? "" : "s")}" : "")
        : "";

    public string OutputTokensLabel => Usage is { } usage ? $"Output Tokens: {Count(usage.OutputTokens)}" : "";

    public string CacheHitLabel => Usage is { } usage
        ? $"Input Cache Hits: {Count(usage.CachedInputTokens)}{Percent(usage.CachedInputTokens, usage.InputTokens)}" : "";

    public string CacheMissLabel => Usage is { } usage
        ? $"Input Cache Misses: {Count(usage.InputTokens - usage.CachedInputTokens)}"
          + Percent(usage.InputTokens - usage.CachedInputTokens, usage.InputTokens)
        : "";

    public double CacheHitPercentage => Usage is { InputTokens: > 0 } usage ? 100d * usage.CachedInputTokens / usage.InputTokens : 0;

    public string FirstRequestCacheLabel => Usage is { FirstRequestInputTokens: { } input, FirstRequestCachedTokens: { } cached }
        ? $"First-Request Cache Hits: {Count(cached)}{Percent(cached, input)}"
        : "First-Request Cache Hits: not recorded";

    public string ContextLabel => Usage?.ContextTokens is { } context ? $"Context Size: {Count(context)} tokens" : "";

    public bool HasContext => ContextLabel.Length > 0;

    // Context management, with the Workbench's labels and colors. A live turn counts as shown and uncompressed.
    public bool IsHydrated => Record?.IsHydrated ?? true;

    public bool IsCompressed => Record?.IsCompressed == true;

    public string HydrationActionLabel => IsHydrated ? "Hide" : "Show";

    public string HydrationActionToolTip => IsHydrated
        ? "Hide: leave this turn out of Claude's context. The next turn starts a new CLI session."
        : "Show: put this turn back in Claude's context. The next turn starts a new CLI session.";

    public string CompressionActionLabel => IsCompressing ? "…" : IsCompressed ? "Expand" : "Compress";

    public string CompressionActionToolTip => IsCompressed
        ? "Expand: replay this turn in full again and discard its work summary. The next turn starts a new CLI session."
        : "Compress: replay this turn as its user inputs, a generated work summary, and its final response verbatim. "
          + "The next turn starts a new CLI session.";

    public string CompressedLabel => Record switch
    {
        { IsCompressed: false } or null => "",
        { SummaryModel: { } model } => $"Compressed · summary by {model}",
        _ => "Compressed · no tool calls to summarize",
    };

    public string CardBorderBrush => IsHydrated ? "#0078D4" : "#F89831";

    public Thickness CardBorderThickness => new(IsHydrated ? 1 : 3);

    public string RailAccentBrush => IsCompressed ? "#1EA7FF" : "#C7D7FF";

    public string RailDividerBrush => IsCompressed ? "#174B73" : "#252D3C";

    public bool HasViewFullContentButton => IsCompressed && !IsViewingFullContent;

    public bool IsViewingFullCompressedContent => IsCompressed && IsViewingFullContent;

    public string ViewFullContentLabel => "👁️ View Full Turn Content" + ReductionSuffix(" replay reduction");

    public string CompactViewFullContentLabel => "👁️ View Full Turn" + ReductionSuffix(" reduction");

    public string ReductionToolTip => Record is { IsCompressed: true } turn
        ? string.Create(CultureInfo.CurrentCulture,
            $"View the full turn without changing its compression. Saved content: {TurnCompression.FullContentBytes(turn):N0} bytes; compressed replay: {ClaudeCodeContext.ReplayBytes(turn):N0} bytes.")
        : "";

    // The Context Management tab's row, with the Workbench's labels and colors.
    public string ContextTurnLabel => Record is { } turn ? $"Turn #{turn.TurnNumber.ToString(CultureInfo.InvariantCulture)}" : "Turn";

    public string ContextIdLabel => Record is { } turn ? $"ID: {turn.Id.ToString(CultureInfo.InvariantCulture)}" : "";

    public string ContextTimeLabel => Record?.CreatedAt.ToLocalTime().ToString("M/d, h:mm tt", CultureInfo.CurrentCulture) ?? "Live";

    public string ContextRowBackground => IsHydrated ? "#123F1A" : "#3F2B16";

    public void Started(ConversationTurn turn) => Record = turn;

    // The images' replay choices and labels come from the saved record.
    partial void OnRecordChanged(ConversationTurn? value)
    {
        foreach (var image in Images) image.Refresh();
    }

    public void AppendText(string delta)
    {
        if (Items.LastOrDefault() is TextItemViewModel { IsStreaming: true } last) last.Text += delta;
        else Items.Add(last = new TextItemViewModel(delta, isStreaming: true));
        RenderCompletedCodeBlocks(last);
    }

    public void UpdateTool(ToolCallRecord tool)
    {
        if (Items.OfType<ToolItemViewModel>().FirstOrDefault(item => item.Id == tool.Id) is { } existing)
        {
            existing.Tool = tool;
            return;
        }
        Add(new ToolItemViewModel(tool, Items.OfType<ToolItemViewModel>().Count() + 1));
    }

    public void UpdateQuestion(string key, QuestionRecord question, bool isWaiting)
    {
        if (_questions.TryGetValue(key, out var existing))
        {
            existing.Question = question;
            existing.IsWaiting = isWaiting;
            return;
        }
        Add(_questions[key] = new QuestionItemViewModel(question, isWaiting));
    }

    public void AddUserMessage(string text) => Add(new UserMessageItemViewModel(text));

    public void UpdateUsage(TurnUsage usage) => Usage = usage;

    /// <summary>Shows the saved turn's outcome. The streamed items already match its parts.</summary>
    public void Finish(ConversationTurn turn)
    {
        StopStreaming();
        IsRunning = turn.Status == TurnStatus.Running;
        Outcome = turn.Status switch
        {
            TurnStatus.Stopped => "Stopped.",
            TurnStatus.Failed => "Failed: " + (turn.ErrorMessage ?? "unknown error."),
            _ => null,
        };
        Record = turn;
        Usage = turn.Usage;
    }

    public void Fail(string message)
    {
        StopStreaming();
        IsRunning = false;
        Outcome = "Failed: " + message;
    }

    /// <summary>Shows a saved change to the turn's context state; a new or cleared summary returns to the default view.</summary>
    public void ApplyRecord(ConversationTurn turn)
    {
        var compressionChanged = turn.Summary != Record?.Summary;
        Record = turn;
        if (compressionChanged) IsViewingFullContent = false;
        // The summary heading shows the replay reduction, which counts the images kept in the replay.
        if (compressionChanged || turn.IsCompressed) RebuildItems();
    }

    [RelayCommand]
    private void ViewFullContent()
    {
        IsViewingFullContent = true;
        RebuildItems();
    }

    [RelayCommand]
    private void RestoreCompressedView()
    {
        IsViewingFullContent = false;
        RebuildItems();
    }

    // A compressed turn shows its user inputs and summary unless it is temporarily shown in full.
    private void RebuildItems()
    {
        if (Record is not { } turn) return;
        var summaryView = turn.IsCompressed && !IsViewingFullContent;
        var parts = turn.Parts.OrderBy(p => p.Sequence).ToArray();
        var prompt = Prompt(parts);
        var toolNumber = 0;
        Items.Clear();
        foreach (var part in parts.Where(p => p != prompt))
        {
            switch (part)
            {
                case { PartType: TurnParts.Text, Role: "assistant" } when !summaryView:
                    Items.Add(new TextItemViewModel(part.Content, isStreaming: false));
                    break;
                case { PartType: TurnParts.Text, Role: "user" }:
                    Items.Add(new UserMessageItemViewModel(part.Content));
                    break;
                case { PartType: TurnParts.Tool } when !summaryView:
                    Items.Add(new ToolItemViewModel(TurnParts.ReadTool(part), ++toolNumber));
                    break;
                case { PartType: TurnParts.Question }:
                    Items.Add(new QuestionItemViewModel(TurnParts.ReadQuestion(part), isWaiting: false));
                    break;
            }
        }
        if (summaryView) Items.Add(new TextItemViewModel(DisplaySummary(turn), isStreaming: false, isSummary: true));
    }

    private static TurnPart? Prompt(IEnumerable<TurnPart> parts) => parts.FirstOrDefault(p => p is { Role: "user", PartType: TurnParts.Text });

    // The Work Summary heading gains the tool call count and the reduction, as in the Workbench.
    private static string DisplaySummary(ConversationTurn turn)
    {
        var summary = turn.Summary!;
        const string heading = TurnCompression.WorkSummaryHeading;
        if (!summary.StartsWith(heading + "\n", StringComparison.Ordinal)) return summary;
        var tools = turn.Parts.Count(p => p.PartType == TurnParts.Tool);
        var metrics = $"{Count(tools)} tool call{(tools == 1 ? "" : "s")}";
        if (TurnCompression.Reduction(turn) is > 1 and var reduction)
            metrics += string.Create(CultureInfo.CurrentCulture, $", {reduction:0.0}x replay reduction");
        return $"{heading} ({metrics}){summary[heading.Length..]}";
    }

    private string ReductionSuffix(string label) => Record is { } turn && TurnCompression.Reduction(turn) is > 1 and var reduction
        ? string.Create(CultureInfo.CurrentCulture, $" ({reduction:0.0}x{label})")
        : "";

    /// <summary>The rail's details as plain text, one per line.</summary>
    public string FormatMetrics()
    {
        var lines = new List<string> { Record is null ? TurnLabel : $"{TurnLabel} · {IdLabel}", TimeLabel };
        if (HasDuration) lines.Add(DurationLabel);
        lines.Add("Model: " + (HasModel ? ModelLabel : "not recorded"));
        if (HasTokenUsage)
            lines.AddRange([InputTokensLabel, OutputTokensLabel, "Cumulative across model requests", CacheHitLabel, CacheMissLabel,
                FirstRequestCacheLabel]);
        if (HasContext) lines.Add(ContextLabel);
        if (!IsHydrated) lines.Add("Hidden from Claude's context");
        if (IsCompressed) lines.Add(CompressedLabel + ReductionSuffix(" replay reduction"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The turn's dialogue as Markdown, under the same header lines the Workbench's turn copy uses. Tool activity is
    /// left out; images are named.
    /// </summary>
    public string FormatForClipboard(long conversationId)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Conversation ID: {conversationId}\n");
        if (Record is { } turn)
            text.Append(CultureInfo.InvariantCulture,
                $"Turn ID: {turn.Id}\nTurn #{turn.TurnNumber}\nTimestamp: {turn.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\n");
        text.Append("\n## User\n\n").Append(UserText);
        foreach (var image in Images) text.Append("\n\n[Image: ").Append(image.Preview.FileName).Append(']');
        var inAssistant = false;
        foreach (var item in Items)
        {
            if (item is UserMessageItemViewModel message)
            {
                text.Append("\n\n## User (sent during the turn)\n\n").Append(message.Text);
                inAssistant = false;
                continue;
            }
            var content = item switch
            {
                TextItemViewModel response => response.Text,
                QuestionItemViewModel question => question.Question.Question + "\n\n**Answer:** " + question.AnswerText,
                _ => null,
            };
            if (content is null) continue;
            text.Append(inAssistant ? "\n\n" : "\n\n## Assistant\n\n").Append(content);
            inAssistant = true;
        }
        return text.ToString();
    }

    private static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private static string Percent(long part, long total) => total > 0
        ? string.Create(CultureInfo.CurrentCulture, $" ({100d * part / total:0.0}%)")
        : " (n/a)";

    // A new tool card, question, or message ends the text before it.
    private void Add(TurnItemViewModel item)
    {
        StopStreaming();
        Items.Add(item);
    }

    private void StopStreaming()
    {
        foreach (var item in Items.OfType<TextItemViewModel>()) item.IsStreaming = false;
    }

    // Completed fenced code blocks render while the rest of the text streams; the remainder stays plain text.
    private void RenderCompletedCodeBlocks(TextItemViewModel item)
    {
        var split = StableMarkdownLength(item.Text);
        if (split <= 0) return;
        var stable = item.Text[..split].TrimEnd('\r', '\n');
        var remainder = item.Text[split..].TrimStart('\r', '\n');
        if (string.IsNullOrWhiteSpace(stable)) return;
        item.Text = stable;
        item.IsStreaming = false;
        if (remainder.Length > 0) Items.Add(new TextItemViewModel(remainder, isStreaming: true));
    }

    /// <summary>The length through the last closed code fence, or 0 while inside a fence or without one.</summary>
    internal static int StableMarkdownLength(string content)
    {
        var stable = 0;
        var index = 0;
        string? fence = null;
        while (index < content.Length)
        {
            var newline = content.IndexOf('\n', index);
            var next = newline >= 0 ? newline + 1 : content.Length;
            var line = content[index..(newline >= 0 ? newline : content.Length)].Trim();
            if (fence is not null)
            {
                // A closing fence is a whole line of at least the opening length; an unterminated last line may still grow.
                if (newline >= 0 && line.Length >= fence.Length && line.All(c => c == fence[0]))
                {
                    fence = null;
                    stable = next;
                }
            }
            else if (line.Length >= 3 && line[0] is '`' or '~' && line[1] == line[0] && line[2] == line[0])
                fence = new string(line[0], line.TakeWhile(c => c == line[0]).Count());
            index = next;
        }
        return fence is null ? stable : 0;
    }
}

/// <summary>An image on a turn card, laid out like the Workbench's: thumbnail, details, and its replay choice.</summary>
public sealed class TurnImageViewModel(TurnViewModel owner, int index, ImageAttachmentViewModel preview) : ObservableObject
{
    public TurnViewModel Owner { get; } = owner;

    public ImageAttachmentViewModel Preview { get; } = preview;

    public string MediaType => Preview.Attachment.MediaType;

    /// <summary>The image's saved part; null until the turn is saved. Images are saved in the order they were attached.</summary>
    public TurnPart? Part => Owner.Record?.Parts.Where(p => p.PartType == TurnParts.Image).OrderBy(p => p.Sequence)
        .ElementAtOrDefault(index);

    public bool IncludeInFutureReplay => Part is { } part && TurnParts.ReadImage(part).IncludeInFutureReplay;

    public string ReplayChoiceLabel => ChoiceLabel(Owner.IsCompressed);

    public string ReplayChoiceToolTip => ChoiceToolTip(Owner.IsCompressed);

    // A turn replayed in full sends all its images, so before compression the choice only takes effect later.
    public static string ChoiceLabel(bool compressed) => compressed ? "Include image in future turns" : "Keep when compressed";

    public static string ChoiceToolTip(bool compressed) => compressed
        ? "Send this image with future turns while its turn stays compressed; unchecked, it's described by name, type, and size. "
          + "It adds to the context, so uncheck it when it's no longer needed. A hidden turn stays hidden."
        : "This turn is replayed in full, so the image is sent either way for now. Checked, it stays in the replay after the turn "
          + "is compressed; unchecked, it's then described by name, type, and size.";

    // Always notifies, so a check box the user toggled shows the saved choice again.
    internal void Refresh()
    {
        OnPropertyChanged(nameof(IncludeInFutureReplay));
        OnPropertyChanged(nameof(ReplayChoiceLabel));
        OnPropertyChanged(nameof(ReplayChoiceToolTip));
    }
}
