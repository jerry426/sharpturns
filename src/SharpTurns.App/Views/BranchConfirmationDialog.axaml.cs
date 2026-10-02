using Avalonia.Controls;
using Avalonia.Interactivity;
using SharpTurns.App.ViewModels;

namespace SharpTurns.App.Views;

/// <summary>Asks before creating a branch, as in the Workbench. Closes with true when confirmed.</summary>
public sealed partial class BranchConfirmationDialog : Window
{
    public BranchConfirmationDialog() => InitializeComponent();

    public BranchConfirmationDialog(BranchConfirmation confirmation) : this()
    {
        TitleText.Text = confirmation.Title;
        MessageText.Text = confirmation.Message;
        DetailTitleText.Text = confirmation.DetailTitle;
        DetailText.Text = confirmation.DetailText;
        WarningText.Text = confirmation.WarningText;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
}
