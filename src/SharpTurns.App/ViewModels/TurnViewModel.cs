using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
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
        nameof(ModelLabel), nameof(HasModel))]
    private ConversationTurn? _record;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokenUsage), nameof(InputTokensLabel), nameof(OutputTokensLabel), nameof(CacheHitLabel),
        nameof(CacheMissLabel), nameof(CacheHitPercentage), nameof(FirstRequestCacheLabel), nameof(ContextLabel), nameof(HasContext))]
    private TurnUsage? _usage;

    /// <summary>A turn that is starting; its response streams in through the update methods.</summary>
    public TurnViewModel(string userText, IReadOnlyList<ImageAttachment> images)
    {
        UserText = userText;
        Images = images.Select(image => new ImageAttachmentViewModel(image)).ToArray();
        IsRunning = true;
    }

    public TurnViewModel(ConversationTurn turn)
    {
        var parts = turn.Parts.OrderBy(p => p.Sequence).ToArray();
        var prompt = parts.FirstOrDefault(p => p is { Role: "user", PartType: TurnParts.Text });
        UserText = prompt?.Content ?? "";
        Images = parts.Where(p => p.PartType == TurnParts.Image)
            .Select(p => new ImageAttachmentViewModel(TurnParts.ReadImage(p))).ToArray();
        foreach (var part in parts.Where(p => p != prompt))
        {
            switch (part)
            {
                case { PartType: TurnParts.Text, Role: "assistant" }:
                    Items.Add(new TextItemViewModel(part.Content, isStreaming: false));
                    break;
                case { PartType: TurnParts.Text, Role: "user" }:
                    Items.Add(new UserMessageItemViewModel(part.Content));
                    break;
                case { PartType: TurnParts.Tool }:
                    Items.Add(new ToolItemViewModel(TurnParts.ReadTool(part)));
                    break;
                case { PartType: TurnParts.Question }:
                    Items.Add(new QuestionItemViewModel(TurnParts.ReadQuestion(part), isWaiting: false));
                    break;
            }
        }
        Finish(turn);
    }

    public string UserText { get; }

    public IReadOnlyList<ImageAttachmentViewModel> Images { get; }

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

    public void Started(ConversationTurn turn) => Record = turn;

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
        Add(new ToolItemViewModel(tool));
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
        foreach (var image in Images) text.Append("\n\n[Image: ").Append(image.Attachment.FileName).Append(']');
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
