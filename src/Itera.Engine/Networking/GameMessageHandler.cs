namespace Itera.Engine.Networking;

/// <summary>
/// Hands the game's messages of one kind to a method of the class that keeps them, with the way they may travel, which
/// the session checks before the method runs. Main thread.
/// </summary>
public sealed class GameMessageHandler(
    byte kind,
    SessionMessageDirection direction,
    GameMessageHandler.Receiver receive
) : ISessionGameHandler
{
    /// <summary>Receives one message: its sender's slot and its body after the kind byte.</summary>
    public delegate void Receiver(int slot, ReadOnlySpan<byte> body);

    public byte Kind => kind;

    public SessionMessageDirection Direction => direction;

    public void Receive(int slot, ReadOnlySpan<byte> body) => receive(slot, body);
}
