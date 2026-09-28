namespace SpiffoCON.Core.Rcon;

public class RconException : Exception
{
    public RconException(string message) : base(message) { }
    public RconException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The server rejected the password.</summary>
public sealed class RconAuthenticationException(string message) : RconException(message);

/// <summary>The server sent something that is not valid RCON.</summary>
public sealed class RconProtocolException(string message) : RconException(message);

/// <summary>The command was sent but no reply arrived in time. The connection stays usable.</summary>
public sealed class RconTimeoutException(string message) : RconException(message);

/// <summary>The TCP connection dropped.</summary>
public sealed class RconConnectionLostException(string message, Exception? inner) : RconException(message, inner);
