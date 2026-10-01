using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

public sealed partial class TurnViewModel : ObservableObject
{
    private readonly Dictionary<string, QuestionItemViewModel> _questions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    private string? _outcome;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    private string? _usageText;

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

    public bool HasUsage => UsageText is not null;

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

    public void UpdateUsage(TurnUsage usage) => UsageText = FormatUsage(usage);

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
        UsageText = turn.Usage is { } usage ? FormatUsage(usage) : null;
    }

    public void Fail(string message)
    {
        StopStreaming();
        IsRunning = false;
        Outcome = "Failed: " + message;
    }

    internal static string FormatUsage(TurnUsage usage)
    {
        var text = $"{Tokens(usage.InputTokens)} input tokens";
        if (usage.InputTokens > 0)
            text += $" ({Math.Round(100d * usage.CachedInputTokens / usage.InputTokens).ToString(CultureInfo.CurrentCulture)}% cached)";
        text += $" · {Tokens(usage.OutputTokens)} output";
        return usage.ContextTokens is { } context ? text + $" · context {Tokens(context)}" : text;
    }

    private static string Tokens(long count) => count >= 1000
        ? (count / 1000d).ToString(count >= 100_000 ? "0" : "0.#", CultureInfo.CurrentCulture) + "k"
        : count.ToString(CultureInfo.CurrentCulture);

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
