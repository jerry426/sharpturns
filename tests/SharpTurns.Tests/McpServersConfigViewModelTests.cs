using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class McpServersConfigViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-mcp-{Guid.NewGuid():N}");

    [Fact]
    public async Task ServersAreValidatedSavedEditedAndDeleted()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var preferences = new ApplicationPreferencesViewModel(store);
        await preferences.LoadAsync();
        var servers = preferences.McpServers;
        Assert.False(servers.HasServers);
        Assert.False(servers.SaveCommand.CanExecute(null));

        servers.AddServerCommand.Execute(null);
        var edit = servers.Editor!;
        Assert.True(edit.IsNew);
        Assert.False(servers.DeleteCommand.CanExecute(null));
        edit.Name = "chrome devtools";
        edit.DisplayName = "Chrome DevTools";
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.Equal("The name (slug) can't contain spaces.", servers.Status);

        edit.Name = " chrome-devtools ";
        edit.CommandJson = """["npx", 1]""";
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("The command must be a non-empty JSON array of strings", servers.Status);

        edit.CommandJson = """["npx", "-y", "chrome-devtools-mcp"]""";
        edit.EnvJson = "[]";
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("The environment variables must be a JSON object", servers.Status);

        edit.EnvJson = """{"NODE_ENV": "production"}""";
        edit.WorkingDirectory = "relative/dir";
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.Equal("The working directory must be a full path.", servers.Status);
        Assert.Empty(await store.ListMcpServersAsync());

        edit.WorkingDirectory = " ";
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Created chrome-devtools.", servers.Status);
        var saved = Assert.Single(await store.ListMcpServersAsync());
        Assert.Equal(("chrome-devtools", "Chrome DevTools", null, """{"NODE_ENV": "production"}""", null, true),
            (saved.Name, saved.DisplayName, saved.Description, saved.EnvJson, saved.WorkingDirectory, saved.Enabled));
        Assert.Equal("chrome-devtools", servers.SelectedServer?.Name);
        Assert.True(servers.Editor!.IsExisting);
        Assert.Equal("MCP Servers (1)", servers.HeaderLabel);

        // Names are unique ignoring case.
        servers.AddServerCommand.Execute(null);
        servers.Editor!.Name = "Chrome-DevTools";
        servers.Editor.DisplayName = "Duplicate";
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.Equal("An MCP server named chrome-devtools already exists.", servers.Status);

        // Cancel brings back the selected server's saved values; a new server's editor closes.
        servers.CancelCommand.Execute(null);
        Assert.Null(servers.Editor);
        servers.SelectedServer = servers.Servers[0];
        servers.Editor!.Description = "Browser automation";
        servers.CancelCommand.Execute(null);
        Assert.Equal("", servers.Editor!.Description);

        servers.Editor.Description = "Browser automation";
        servers.Editor.Enabled = false;
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Saved chrome-devtools.", servers.Status);
        Assert.Equal(("Browser automation", "Disabled"), (servers.SelectedServer?.Description, servers.SelectedServer?.StatusLabel));
        var reloaded = new ApplicationPreferencesViewModel(store);
        await reloaded.LoadAsync();
        Assert.Equal("Browser automation", Assert.Single(reloaded.McpServers.Servers).Description);

        // A saved server can be renamed.
        var id = servers.Editor!.Id;
        servers.Editor.Name = "browser";
        await servers.SaveCommand.ExecuteAsync(null);
        Assert.Equal(("Saved browser.", "browser"), (servers.Status, servers.SelectedServer?.Name));
        Assert.Equal(id, Assert.Single(await store.ListMcpServersAsync()).Id);
        servers.Editor!.Name = "chrome-devtools";
        await servers.SaveCommand.ExecuteAsync(null);

        // Delete asks first.
        var confirmed = false;
        servers.ConfirmAsync = (_, _) => Task.FromResult(confirmed);
        await servers.DeleteCommand.ExecuteAsync(null);
        Assert.Single(await store.ListMcpServersAsync());
        confirmed = true;
        await servers.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(await store.ListMcpServersAsync());
        Assert.Equal(("Deleted chrome-devtools.", false), (servers.Status, servers.HasServers));
        Assert.Null(servers.Editor);
    }

    [Fact]
    public async Task OpenConversationsFollowServerChanges()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        var conversation = await store.CreateConversationAsync(project.Id, "Conversation", "claude-opus-5-5");
        var server = await store.CreateMcpServerAsync("chrome-devtools", "Chrome DevTools", null, """["npx"]""", null, null, true);
        await store.UpdateConversationAsync(conversation.Id, conversation.Title, project.Id, null, false, null, null, [server.Id], []);
        var preferences = new ApplicationPreferencesViewModel(store);
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store), preferences);
        await main.InitializeAsync();
        await WaitAsync(() => main.Conversations.Count == 1);
        main.SelectedConversation = main.Conversations[0];
        var open = main.CurrentConversation!;
        await WaitAsync(() => open.McpServersLabel == "chrome-devtools");

        var servers = preferences.McpServers;
        servers.SelectedServer = servers.Servers[0];
        servers.Editor!.Name = "browser";
        servers.Editor.Enabled = false;
        await servers.SaveCommand.ExecuteAsync(null);
        await WaitAsync(() => open.McpServersLabel == "browser (disabled)");

        servers.ConfirmAsync = (_, _) => Task.FromResult(true);
        await servers.DeleteCommand.ExecuteAsync(null);
        await WaitAsync(() => !open.HasMcpServers);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
