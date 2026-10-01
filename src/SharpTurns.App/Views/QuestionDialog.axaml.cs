using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>Shows one AskUserQuestion question. Answer is null when the user declines or closes the window.</summary>
public sealed partial class QuestionDialog : Window
{
    public QuestionDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            // Rebuild once the window is shown; LiveMarkdown can skip rendering while a window is still hidden.
            QuestionDocument.RefreshDocument();
            if (AnswerBox.IsVisible) AnswerBox.Focus();
        };
    }

    public string? Answer { get; private set; }

    private void Answer_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QuestionDialogViewModel { CanSubmit: true } viewModel) return;
        Answer = viewModel.Answer;
        Close();
    }

    private void Decline_Click(object? sender, RoutedEventArgs e) => Close();
}
