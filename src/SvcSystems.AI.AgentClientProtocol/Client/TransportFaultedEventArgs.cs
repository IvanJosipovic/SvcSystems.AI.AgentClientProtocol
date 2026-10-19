namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Describes a transport or protocol failure that terminated an ACP connection.</summary>
public sealed class TransportFaultedEventArgs : EventArgs
{
    /// <summary>Initializes event data for a connection failure.</summary>
    /// <param name="exception">The failure that terminated the connection.</param>
    public TransportFaultedEventArgs(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
    }

    /// <summary>The failure that terminated the connection. Never null.</summary>
    /// <remarks>Usually a <see cref="ProcessException"/> or <see cref="ProtocolException"/>. The same
    /// value is available as the result of <see cref="IClient.Completion"/>.</remarks>
    public Exception Exception { get; }
}
