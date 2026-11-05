namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>An RCON operation failed: the connection, the authentication or the command.</summary>
internal sealed class RconException : Exception
{
    public RconException(string message) : base(message)
    {
    }

    public RconException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
