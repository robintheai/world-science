namespace ServiceLib.Manager;

/// <summary>Simple opts in: only a deliberate Connect action can consume one OS prompt.</summary>
public sealed class MacAuthorizationPromptGate
{
    private int _permit;
    public void BeginUserConnect() => Interlocked.Exchange(ref _permit, 1);
    public void EndUserConnect() => Interlocked.Exchange(ref _permit, 0);
    public bool TryConsume() => Interlocked.Exchange(ref _permit, 0) == 1;
}

public sealed class MacNetworkAuthorizationException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
