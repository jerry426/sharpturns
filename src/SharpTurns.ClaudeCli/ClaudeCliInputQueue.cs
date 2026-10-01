namespace SharpTurns.ClaudeCli;

/// <summary>
/// Messages the user sends while a turn runs. The client takes them as the CLI accepts input and closes the queue
/// when the turn's last result arrives with nothing queued. Thread-safe.
/// </summary>
public sealed class ClaudeCliInputQueue
{
    public const int Capacity = 5;
    private readonly object _gate = new();
    private readonly Queue<ClaudeCliUserMessage> _queued = new();
    private TaskCompletionSource _available = NewSignal();
    private bool _closed;

    public bool TryEnqueue(string text, out ClaudeCliUserMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        lock (_gate)
        {
            if (_closed || _queued.Count >= Capacity) return false;
            message = new(Guid.NewGuid().ToString(), text.Trim());
            _queued.Enqueue(message);
            _available.TrySetResult();
            return true;
        }
    }

    /// <summary>Completes when a message is queued or the queue closes.</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return _available.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Takes every queued message; empty once the queue is closed and drained.</summary>
    public IReadOnlyList<ClaudeCliUserMessage> Take()
    {
        lock (_gate)
        {
            var messages = _queued.ToArray();
            _queued.Clear();
            if (!_closed) _available = NewSignal();
            return messages;
        }
    }

    /// <summary>Closes the queue only if nothing is waiting to be sent.</summary>
    public bool TryClose()
    {
        lock (_gate)
        {
            if (_queued.Count > 0) return false;
            Close();
            return true;
        }
    }

    /// <summary>Rejects new messages. Messages still queued stay available to Take.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _available.TrySetResult();
        }
    }

    public ClaudeCliTurnInput ToTurnInput(Func<IReadOnlyList<ClaudeCliUserMessage>>? take = null) =>
        new(WaitAsync, take ?? Take, TryClose);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
