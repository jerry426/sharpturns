using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Config tab's MCP Servers: a server list and editor, storing definitions only. Conversations select the servers
/// their turns start, and the CLI starts them, so there is no Test Connection.
/// </summary>
public sealed partial class McpServersConfigViewModel : ObservableObject
{
    private readonly ConversationStore _store;

    [ObservableProperty]
    private McpServerRowViewModel? _selectedServer;

    /// <summary>The selected server as edited, or a new one; null when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private McpServerEditViewModel? _editor;

    [ObservableProperty]
    private string _status = "MCP servers not loaded.";

    internal McpServersConfigViewModel(ConversationStore store)
    {
        _store = store;
        Servers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasServers));
            OnPropertyChanged(nameof(HeaderLabel));
        };
    }

    public ObservableCollection<McpServerRowViewModel> Servers { get; } = [];

    public bool HasServers => Servers.Count > 0;

    public string HeaderLabel => string.Create(CultureInfo.CurrentCulture, $"MCP Servers ({Servers.Count:N0})");

    /// <summary>Title and message; returns true when confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Raised after a server is saved or deleted, so views of conversations' selections can reload.</summary>
    public event Action? ServersChanged;

    // Choosing another server discards unsaved edits.
    partial void OnSelectedServerChanged(McpServerRowViewModel? value)
    {
        if (value is not null) Editor = new(value.Server);
    }

    /// <summary>Owns its errors: a list that can't be read stays empty, and the reason goes to the status.</summary>
    [RelayCommand]
    internal async Task LoadAsync()
    {
        try
        {
            await ReloadAsync(SelectedServer?.Name);
            Status = HasServers
                ? string.Create(CultureInfo.CurrentCulture, $"Loaded {Servers.Count:N0} MCP server{(Servers.Count == 1 ? "" : "s")}.")
                : "No MCP servers configured.";
        }
        catch (Exception e) { Status = "Couldn't load the MCP servers: " + e.Message; }
    }

    [RelayCommand]
    private void AddServer()
    {
        SelectedServer = null;
        Editor = new();
        Status = "Enter the new MCP server's details, then click Save.";
    }

    private bool HasEditor() => Editor is not null;

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private void Cancel()
    {
        Editor = SelectedServer is { } row ? new(row.Server) : null;
        Status = "Discarded unsaved MCP server changes.";
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task SaveAsync()
    {
        if (Editor is not { } edit || Validate(edit) is not { } fields) return;
        try
        {
            var saved = edit.Id is { } id
                ? await _store.UpdateMcpServerAsync(id, fields.Name, fields.DisplayName, fields.Description, fields.CommandJson,
                    fields.EnvJson, fields.WorkingDirectory, edit.Enabled)
                : await _store.CreateMcpServerAsync(fields.Name, fields.DisplayName, fields.Description, fields.CommandJson,
                    fields.EnvJson, fields.WorkingDirectory, edit.Enabled);
            await ReloadAsync(saved.Name);
            Status = edit.IsNew ? $"Created {saved.Name}." : $"Saved {saved.Name}.";
            ServersChanged?.Invoke();
        }
        catch (Exception e) { Status = "Couldn't save the MCP server: " + e.Message; }
    }

    private bool CanDelete() => Editor?.IsExisting == true;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Editor is not { Id: { } id } edit
            || ConfirmAsync is null || !await ConfirmAsync("Delete MCP Server", $"Delete the MCP server {edit.Name}?"))
            return;
        try
        {
            await _store.DeleteMcpServerAsync(id);
            await ReloadAsync(null);
            Status = $"Deleted {edit.Name}.";
            ServersChanged?.Invoke();
        }
        catch (Exception e) { Status = "Couldn't delete the MCP server: " + e.Message; }
    }

    // Reloads the list and selects the named server; with none, clears the editor.
    private async Task ReloadAsync(string? select)
    {
        var servers = await _store.ListMcpServersAsync();
        Servers.Clear();
        foreach (var server in servers) Servers.Add(new(server));
        SelectedServer = Servers.FirstOrDefault(s => s.Name == select);
        if (SelectedServer is null) Editor = null;
    }

    // Returns the trimmed fields, or null with the reason in Status.
    private McpServerFields? Validate(McpServerEditViewModel edit)
    {
        var name = edit.Name.Trim();
        var displayName = edit.DisplayName.Trim();
        var commandJson = edit.CommandJson.Trim();
        var envJson = TrimToNull(edit.EnvJson);
        var workingDirectory = TrimToNull(edit.WorkingDirectory);
        string? error = null;
        if (name.Length == 0)
            error = "Enter a name (slug), such as chrome-devtools.";
        else if (name.Any(char.IsWhiteSpace))
            error = "The name (slug) can't contain spaces.";
        else if (Servers.FirstOrDefault(s => s.Server.Id != edit.Id
                     && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
            error = $"An MCP server named {existing.Name} already exists.";
        else if (displayName.Length == 0)
            error = "Enter a display name.";
        else if (workingDirectory is not null && !Path.IsPathFullyQualified(workingDirectory))
            error = "The working directory must be a full path.";
        else
            error = CommandError(commandJson) ?? EnvironmentError(envJson);
        if (error is not null)
        {
            Status = error;
            return null;
        }
        return new(name, displayName, TrimToNull(edit.Description), commandJson, envJson, workingDirectory);
    }

    // A non-empty JSON array of strings.
    private static string? CommandError(string json)
    {
        const string Shape = """The command must be a non-empty JSON array of strings, such as ["npx", "-y", "package-name"].""";
        if (json.Length == 0) return Shape;
        try
        {
            return JsonNode.Parse(json) is JsonArray { Count: > 0 } array
                && array.All(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                ? null
                : Shape;
        }
        catch (JsonException e) { return "The command isn't valid JSON: " + e.Message; }
    }

    // Blank, null, or a JSON object.
    private static string? EnvironmentError(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json) is null or JsonObject
                ? null
                : """The environment variables must be a JSON object, such as {"NODE_ENV": "production"}.""";
        }
        catch (JsonException e) { return "The environment variables aren't valid JSON: " + e.Message; }
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record McpServerFields(string Name, string DisplayName, string? Description, string CommandJson, string? EnvJson,
        string? WorkingDirectory);
}

/// <summary>A server in the list.</summary>
public sealed class McpServerRowViewModel(McpServer server)
{
    public McpServer Server { get; } = server;

    public string Name => Server.Name;

    public string DisplayName => Server.DisplayName;

    public string? Description => Server.Description;

    public bool HasDescription => Server.Description is not null;

    public string StatusLabel => Server.Enabled ? "Enabled" : "Disabled";

    public string UpdatedLabel => Server.UpdatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>
/// The editor for a new or saved server. The command and environment are edited as JSON text.
/// </summary>
public sealed partial class McpServerEditViewModel : ObservableObject
{
    public McpServerEditViewModel()
    {
        _name = "";
        _displayName = "";
        _description = "";
        _commandJson = """["npx", "-y", "package-name"]""";
        _envJson = "";
        _workingDirectory = "";
        _enabled = true;
    }

    public McpServerEditViewModel(McpServer server)
    {
        Id = server.Id;
        _name = server.Name;
        _displayName = server.DisplayName;
        _description = server.Description ?? "";
        _commandJson = server.CommandJson;
        _envJson = server.EnvJson ?? "";
        _workingDirectory = server.WorkingDirectory ?? "";
        _enabled = server.Enabled;
    }

    /// <summary>Null for a server not saved yet.</summary>
    public long? Id { get; }

    public bool IsNew => Id is null;

    public bool IsExisting => Id is not null;

    public string Title => IsNew ? "Add MCP Server" : $"Edit: {Name}";

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _commandJson;

    [ObservableProperty]
    private string _envJson;

    [ObservableProperty]
    private string _workingDirectory;

    [ObservableProperty]
    private bool _enabled;
}
