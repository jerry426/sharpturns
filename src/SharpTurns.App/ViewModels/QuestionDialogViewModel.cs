using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.ClaudeCli;

namespace SharpTurns.App.ViewModels;

/// <summary>One AskUserQuestion question: pick the offered options or write a custom answer.</summary>
public sealed partial class QuestionDialogViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _useCustomAnswer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _customAnswer = "";

    public QuestionDialogViewModel(ClaudeCliQuestion question, string projectName, string conversationTitle)
    {
        Header = string.IsNullOrWhiteSpace(question.Header) ? "Question" : question.Header.Trim();
        Question = question.Text.Trim();
        ProjectName = projectName;
        ConversationTitle = conversationTitle;
        MultiSelect = question.MultiSelect;
        Options = question.Options.Select(o => new QuestionOptionViewModel(o.Label, o.Description, o.Preview, MultiSelect)).ToArray();
        foreach (var option in Options) option.PropertyChanged += OnOptionChanged;
        // Without options, the only way to answer is in your own words.
        UseCustomAnswer = Options.Count == 0;
    }

    public string Header { get; }

    public string Question { get; }

    public string ProjectName { get; }

    public string ConversationTitle { get; }

    public bool MultiSelect { get; }

    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    public bool HasOptions => Options.Count > 0;

    public string Instructions => !HasOptions ? "Type your answer."
        : MultiSelect ? "Select one or more options, or write your own answer." : "Select an option, or write your own answer.";

    public bool CanSubmit => !string.IsNullOrWhiteSpace(Answer);

    /// <summary>The custom text when chosen; otherwise the selected labels joined with ", ".</summary>
    public string Answer => UseCustomAnswer ? CustomAnswer.Trim()
        : string.Join(", ", Options.Where(o => o.IsSelected).Select(o => o.Label));

    // A custom answer replaces the options; selecting an option turns it off again.
    partial void OnUseCustomAnswerChanged(bool value)
    {
        if (value)
            foreach (var option in Options) option.IsSelected = false;
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuestionOptionViewModel.IsSelected)) return;
        if (sender is QuestionOptionViewModel { IsSelected: true } selected)
        {
            UseCustomAnswer = false;
            if (!MultiSelect)
                foreach (var other in Options.Where(o => o != selected)) other.IsSelected = false;
        }
        OnPropertyChanged(nameof(CanSubmit));
    }
}

public sealed partial class QuestionOptionViewModel(string label, string? description, string? preview, bool multiSelect)
    : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Label { get; } = label;

    public string? Description { get; } = description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string? Preview { get; } = preview;

    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);

    public bool MultiSelect { get; } = multiSelect;

    public bool SingleSelect => !MultiSelect;
}
