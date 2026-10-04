using SharpTurns.Core;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ContextFilesTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"sharpturns-context-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task OnlyReadableTextInsideTheWorkspaceIsIncluded()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_directory, "workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "plan.md"), "\uFEFF  Plan  \n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "empty.md"), "");
        await File.WriteAllBytesAsync(Path.Combine(workspace, "image.bin"), [0xFF, 0xFE, 0x00, 0x81]);
        await File.WriteAllTextAsync(Path.Combine(_directory, "outside.md"), "Secret");

        var resolved = await ContextFiles.ResolveAsync(
        [
            Setting("plan.md"), Setting("empty.md"), Setting("image.bin"), Setting("../outside.md"), Setting("gone.md"),
            Setting("plan.md") with { Enabled = false },
        ], workspace, CancellationToken.None);

        Assert.Equal(["Plan", null, null, null, null], resolved.Select(f => f.Content));
        Assert.Equal([null, "The file is empty.", "The file isn't UTF-8 text.", "The path isn't inside the workspace.",
            "The file doesn't exist."], resolved.Select(f => f.Error));
        // Only a required file that fails stops the turn.
        ContextFiles.ThrowIfRequiredFailed(resolved);
        Assert.Throws<InvalidOperationException>(() =>
            ContextFiles.ThrowIfRequiredFailed([resolved[4] with { File = resolved[4].File with { Required = true } }]));
    }

    [Fact]
    public void TheFingerprintFollowsContentAndSettings()
    {
        var included = new ResolvedContextFile(Setting("plan.md"), "Plan", null);

        Assert.Null(ContextFiles.Fingerprint([]));
        Assert.Equal(ContextFiles.Fingerprint([included]), ContextFiles.Fingerprint([included]));
        Assert.NotEqual(ContextFiles.Fingerprint([included]), ContextFiles.Fingerprint([included with { Content = "Plan 2" }]));
        Assert.NotEqual(ContextFiles.Fingerprint([included]),
            ContextFiles.Fingerprint([included with { File = included.File with { Role = ContextFileRoles.NormativeGuidance } }]));
        // Without context files, the session fingerprint is the dialogue's alone, as before.
        Assert.NotEqual(ClaudeCodeContext.Fingerprint([]), ClaudeCodeContext.Fingerprint([], ContextFiles.Fingerprint([included])));
    }

    [Fact]
    public void RelativePathsMustStayInsideTheWorkspace()
    {
        Assert.Equal("docs/plan.md", ContextFiles.RelativePath(_directory, Path.Combine(_directory, "docs", "plan.md")));
        Assert.Throws<ArgumentException>(() => ContextFiles.RelativePath(Path.Combine(_directory, "docs"),
            Path.Combine(_directory, "plan.md")));
        Assert.Throws<ArgumentException>(() => ContextFiles.RelativePath(_directory, _directory));
    }

    private static ContextFile Setting(string path) => new(path, ContextFileRoles.ReferenceSource, null, true, false, false);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
