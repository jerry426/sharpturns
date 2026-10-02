namespace SharpTurns.MarkdownViewer.App.Views;

internal sealed class InitialWindowActionGate(bool requireActivation)
{
    private bool _hasOpened;
    private bool _hasActivated;
    private bool _hasGranted;

    public bool MarkOpened()
    {
        _hasOpened = true;
        return TryGrant();
    }

    public bool MarkActivated()
    {
        _hasActivated = true;
        return TryGrant();
    }

    private bool TryGrant()
    {
        if (_hasGranted || !_hasOpened || (requireActivation && !_hasActivated))
        {
            return false;
        }

        _hasGranted = true;
        return true;
    }
}
