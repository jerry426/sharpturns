using System.Collections.ObjectModel;
using SharpTurns.App.ViewModels;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ConversationViewModelTests
{
    [Theory]
    [InlineData("abcde", "ace", "ace")]
    [InlineData("ace", "abcde", "abcde")] // Hidden turns come back in place.
    [InlineData("abcde", "bd", "bd")]
    [InlineData("bd", "abde", "abde")]
    [InlineData("abc", "", "")]
    [InlineData("", "abc", "abc")]
    public void ShownTurnsFollowTheFilterInOrder(string shown, string target, string expected)
    {
        var items = "abcde".Select(c => c.ToString()).ToDictionary(s => s);
        var collection = new ObservableCollection<string>(shown.Select(c => items[c.ToString()]));
        var kept = collection.ToHashSet();
        var removed = new List<string>();
        collection.CollectionChanged += (_, e) => removed.AddRange(e.OldItems?.Cast<string>() ?? []);

        ConversationViewModel.SyncShownTurns(collection, target.Select(c => items[c.ToString()]));

        Assert.Equal(expected, string.Concat(collection));
        // Cards that stay are never removed and re-added, so they keep their rendered content.
        Assert.All(removed, item => Assert.DoesNotContain(item, expected));
        Assert.True(kept.Where(k => expected.Contains(k, StringComparison.Ordinal)).All(collection.Contains));
    }
}
