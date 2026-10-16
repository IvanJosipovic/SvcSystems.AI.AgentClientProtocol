namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Base exception for ACP client failures.</summary>
public class AcpException : Exception
{
    /// <summary>Initializes an exception with a message.</summary>
    public AcpException(string message) : base(message) { }

    /// <summary>Initializes an exception with a message and inner exception.</summary>
    public AcpException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The ACP agent process could not be started or terminated unexpectedly.</summary>
public sealed class AcpProcessException : AcpException
{
    /// <summary>Initializes a process exception.</summary>
    public AcpProcessException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>The ACP peer sent a malformed or unsupported protocol message.</summary>
public sealed class AcpProtocolException : AcpException
{
    /// <summary>Initializes a protocol exception.</summary>
    public AcpProtocolException(string message) : base(message) { }

    /// <summary>Initializes a protocol exception with an inner exception.</summary>
    public AcpProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>An ACP JSON-RPC request failed.</summary>
public sealed class AcpRpcException : AcpException
{
    /// <summary>Initializes an RPC exception.</summary>
    public AcpRpcException(int code, string message, string? method = null)
        : base(method is null ? message : $"ACP method '{method}' failed ({code}): {message}")
    {
        Code = code;
        Method = method;
    }

    /// <summary>JSON-RPC error code.</summary>
    public int Code { get; }

    /// <summary>Method that returned the error, if known.</summary>
    public string? Method { get; }
}
