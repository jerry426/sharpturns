using System.Text;

namespace SharpTurns.Core.Persistence;

/// <summary>
/// Keeps two SharpTurns instances out of the same conversation. Each held conversation has a lock file open with
/// <see cref="FileShare.None"/>, so the OS releases it when the instance exits or crashes. Each holder writes a new token
/// into the file, so an instance taking a conversation back can tell whether another one held it meanwhile. Not
/// thread-safe; the app uses it from the UI thread.
/// </summary>
public sealed class ConversationLocks : IDisposable
{
    private readonly string _directory;
    private readonly Dictionary<long, FileStream> _held = [];
    // The token this instance last wrote for each conversation.
    private readonly Dictionary<long, string> _tokens = [];

    public ConversationLocks(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>The locks directory beside the store's database file.</summary>
    public static ConversationLocks For(ConversationStore store) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.DatabasePath))!, "locks"));

    public bool IsHeld(long conversationId) => _held.ContainsKey(conversationId);

    /// <summary>True when this instance holds the conversation, or now does; false when another instance holds it.</summary>
    public bool TryAcquire(long conversationId) => TryAcquire(conversationId, out _);

    /// <summary>
    /// As <see cref="TryAcquire(long)"/>. heldElsewhereSince is true when this instance takes the conversation and
    /// another instance held it since this one last did, or this one never has, so what's loaded may be out of date.
    /// </summary>
    public bool TryAcquire(long conversationId, out bool heldElsewhereSince)
    {
        heldElsewhereSince = false;
        if (_held.ContainsKey(conversationId)) return true;
        Directory.CreateDirectory(_directory);
        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(_directory, $"conversation-{conversationId}.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }
        _held[conversationId] = stream;
        try
        {
            var buffer = new byte[64];
            var previous = Encoding.ASCII.GetString(buffer, 0, stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false));
            heldElsewhereSince = !_tokens.TryGetValue(conversationId, out var mine) || previous != mine;
            var token = Guid.NewGuid().ToString("N");
            stream.SetLength(0);
            stream.Write(Encoding.ASCII.GetBytes(token));
            stream.Flush();
            _tokens[conversationId] = token;
        }
        catch (IOException)
        {
            // Held either way; without a token, the next time counts as changed.
            heldElsewhereSince = true;
            _tokens.Remove(conversationId);
        }
        return true;
    }

    // The file stays; deleting it could race another instance opening it.
    public void Release(long conversationId)
    {
        if (_held.Remove(conversationId, out var stream)) stream.Dispose();
    }

    public void Dispose()
    {
        foreach (var stream in _held.Values) stream.Dispose();
        _held.Clear();
    }
}
