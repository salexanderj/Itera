using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Networking;

/// <summary>The first byte of every session message.</summary>
internal enum MessageKind : byte
{
    /// <summary>Client to host, first: protocol version, generator, step rate, and name.</summary>
    Hello = 1,

    /// <summary>Host to client: slot, match settings, and the join payload's size and edit count.</summary>
    Welcome = 2,

    /// <summary>Host to client: the next part of the join payload (the host's saved edit log).</summary>
    JoinChunk = 3,

    /// <summary>Host to client: the payload is complete.</summary>
    JoinDone = 4,

    /// <summary>Host to client: the next edit of the host's log, and its maker (since protocol 30).</summary>
    Edit = 5,

    /// <summary>Host to client: the next edit, a lift, and its maker, with the piece it lifted as runs of one material.</summary>
    EditWithPiece = 6,

    /// <summary>Host to every client: each slot's presence and name, sent when a player joins or leaves.</summary>
    Roster = 7,
}

/// <summary>What a disconnect's data says: the first byte, then a reason byte and a text.</summary>
internal enum DisconnectKind : byte
{
    /// <summary>The host refused the client (a <see cref="RejectReason"/>).</summary>
    Reject = 1,

    /// <summary>The peer is leaving: the host quits, or a client does.</summary>
    Bye = 2,

    /// <summary>The peer sent something this build cannot accept.</summary>
    ProtocolError = 3,
}

/// <summary>Why a host refused a client.</summary>
internal enum RejectReason : byte
{
    /// <summary>Another protocol version.</summary>
    Protocol = 1,

    /// <summary>Another match identity: with a voxel world, another generator or generator version.</summary>
    Identity = 2,

    /// <summary>The session has as many players as it allows.</summary>
    Full = 3,

    /// <summary>Another fixed step rate.</summary>
    StepRate = 4,

    /// <summary>No hello in time.</summary>
    NoHello = 5,

    /// <summary>
    /// Through Steam, a player neither in the host's lobby nor its Steam friend (a host's
    /// Steam id is public, and <c>steam:ID</c> joins once passed the friends-only lobby).
    /// </summary>
    NotInvited = 6,
}

/// <summary>
/// The session's wire format. Every message starts with a <see cref="MessageKind"/>; numbers are little-endian, strings
/// UTF-8 behind a 16-bit length. Kinds 1 to 63 are the engine's; the game's messages start at
/// <see cref="FirstGameKind"/>. A peer with another <see cref="Version"/> is refused.
/// </summary>
internal static class SessionProtocol
{
    /// <summary>
    /// Raised whenever any message changes shape or meaning or a kind is added, the game's included: the game's messages
    /// have no version of their own, so a mismatch is refused in the handshake with this number instead of failing later.
    /// </summary>
    public const int Version = 39;

    /// <summary>Lobby data: the players in the session now, the host's included (<c>SessionService</c> writes it).</summary>
    public const string LobbyPlayersKey = "itera_players";

    /// <summary>Lobby data: the most players the session takes.</summary>
    public const string LobbyMaxKey = "itera_max";

    /// <summary>Lobby data: what a client must match to join (<see cref="MatchKey"/>).</summary>
    public const string LobbyMatchKey = "itera_match";

    /// <summary>
    /// What a host and a client must share to join: the protocol, the match identity's id and version (with a voxel world,
    /// the generator's), and the step rate, as one line ("protocol;game.world;9;60"). A friend's game whose lobby gives
    /// another shows it as another version.
    /// </summary>
    public static string MatchKey(string identity, int identityVersion, int fixedStepHz) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Version};{identity};{identityVersion};{fixedStepHz}"
        );

    /// <summary>The key LiteNetLib connection requests carry; others are ignored.</summary>
    public const string ConnectKey = "ITRA";

    /// <summary>The first message kind the game may use.</summary>
    public const byte FirstGameKind = 64;

    /// <summary>The longest player name, in characters.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The longest string in a message, in UTF-8 bytes.</summary>
    public const int MaxStringBytes = 512;

    /// <summary>
    /// A position's steps a metre in a message (<see cref="MessageWriter.WritePosition"/>): 1/64 cm, 0.16 mm, so three
    /// int32 reach ±335 km, past the farthest map (±160 km).
    /// </summary>
    public const int PositionStepsPerMetre = 6400;

    /// <summary>The farthest a position in a message may lie from the map's middle on any axis, in metres.</summary>
    public const double MaxPositionMetres = (double)int.MaxValue / PositionStepsPerMetre;

    /// <summary>Bytes of join payload per <see cref="MessageKind.JoinChunk"/>.</summary>
    public const int JoinChunkBytes = 16 * 1024;

    /// <summary>
    /// The longest text a disconnect carries, in UTF-8 bytes. LiteNetLib sends disconnect data in one packet of its
    /// smallest MTU (576 bytes) and drops more with an Error, so a longer reason is cut.
    /// </summary>
    public const int MaxDisconnectTextBytes = 200;

    public static void WriteEdit(MessageWriter writer, in VoxelEdit edit)
    {
        writer.WriteLong(edit.Sequence);
        writer.WriteByte((byte)edit.Shape);
        writer.WriteByte((byte)edit.Operation);
        writer.WriteByte(edit.Material);
        WriteCoord(writer, edit.Origin);
        WriteCoord(writer, edit.Extent);
        writer.WriteLong(edit.Piece);
        writer.WriteLong(edit.Turn);
    }

    public static VoxelEdit ReadEdit(ref MessageReader reader) =>
        new(
            reader.ReadLong(),
            (VoxelEditShape)reader.ReadByte(),
            (VoxelEditOperation)reader.ReadByte(),
            reader.ReadByte(),
            ReadCoord(ref reader),
            ReadCoord(ref reader),
            reader.ReadLong(),
            reader.ReadLong()
        );

    /// <summary>
    /// Writes an edit's maker (<see cref="VoxelWorld.MakerOf"/>) after it in an edit message, since protocol 30: the
    /// player's slot, or 255 for none.
    /// </summary>
    public static void WriteMaker(MessageWriter writer, int maker) =>
        writer.WriteByte(maker == VoxelEdit.NoMaker ? byte.MaxValue : (byte)maker);

    /// <summary>
    /// Reads an edit's maker: a slot below <see cref="MaxSlots"/>, or <see cref="VoxelEdit.NoMaker"/>. The slot need not
    /// be in the roster now: the player who made the edit may have left since.
    /// </summary>
    public static int ReadMaker(ref MessageReader reader)
    {
        var maker = reader.ReadByte();
        if (maker == byte.MaxValue)
        {
            return VoxelEdit.NoMaker;
        }

        return maker < MaxSlots ? maker : throw new InvalidDataException($"An edit made by slot {maker}.");
    }

    /// <summary>The largest game block a match carries, in bytes.</summary>
    public const int MaxMatchGameBytes = Voxels.EditLogSerializer.MaxGameDataBytes;

    /// <summary>The most slots a roster may list (the options allow at most 16 players).</summary>
    public const int MaxSlots = 16;

    public static void WriteMatch(MessageWriter writer, in MatchSettings match)
    {
        writer.WriteULong(match.Seed);
        writer.WriteInt(match.FixedStepHz);
        writer.WriteString(match.Identity);
        writer.WriteInt(match.IdentityVersion);
        var game = match.Game ?? [];
        writer.WriteInt(game.Length);
        writer.WriteBytes(game);
    }

    public static MatchSettings ReadMatch(ref MessageReader reader)
    {
        var seed = reader.ReadULong();
        var fixedStepHz = reader.ReadInt();
        var identity = reader.ReadString();
        var identityVersion = reader.ReadInt();
        var length = reader.ReadInt();
        if (length < 0 || length > MaxMatchGameBytes)
        {
            throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"A match claims {length} bytes of game data.")
            );
        }

        return new MatchSettings(seed, fixedStepHz, identity, identityVersion, reader.ReadBytes(length).ToArray());
    }

    /// <summary>A disconnect's data: its kind, a reason byte, and a text for the other side's log.</summary>
    public static void WriteDisconnect(MessageWriter writer, DisconnectKind kind, byte reason, string text)
    {
        writer.Reset();
        writer.WriteByte((byte)kind);
        writer.WriteByte(reason);
        writer.WriteString(Cut(text, MaxDisconnectTextBytes));
    }

    /// <summary>
    /// <paramref name="name"/> cut to <see cref="MaxNameLength"/> characters, never inside a surrogate pair: a client's
    /// name as the host keeps it, and this player's Steam name.
    /// </summary>
    public static string CutName(string name)
    {
        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        var length = MaxNameLength;
        if (char.IsHighSurrogate(name[length - 1]))
        {
            length--;
        }

        return name[..length];
    }

    /// <summary><paramref name="text"/>, shortened by whole characters to at most <paramref name="maxBytes"/> of UTF-8.</summary>
    public static string Cut(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var length = Math.Min(text.Length, maxBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > maxBytes - 3)
        {
            length--;
        }

        // Not inside a surrogate pair.
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length), "...");
    }

    /// <summary>Reads a disconnect's data; false when it is empty or not one this build wrote.</summary>
    public static bool TryReadDisconnect(
        ReadOnlySpan<byte> data,
        out DisconnectKind kind,
        out byte reason,
        out string text
    )
    {
        kind = 0;
        reason = 0;
        text = "";
        try
        {
            var reader = new MessageReader(data);
            kind = (DisconnectKind)reader.ReadByte();
            reason = reader.ReadByte();
            text = reader.ReadString();
            return kind is DisconnectKind.Reject or DisconnectKind.Bye or DisconnectKind.ProtocolError;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void WriteCoord(MessageWriter writer, VoxelCoord coord)
    {
        writer.WriteInt(coord.X);
        writer.WriteInt(coord.Y);
        writer.WriteInt(coord.Z);
    }

    private static VoxelCoord ReadCoord(ref MessageReader reader) =>
        new(reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
}

/// <summary>Builds one message at a time in a buffer it keeps, so writing allocates nothing once the buffer has grown.</summary>
public sealed class MessageWriter
{
    private byte[] _buffer = new byte[1024];
    private int _length;

    /// <summary>What has been written since <see cref="Reset"/>.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public int Length => _length;

    public void Reset() => _length = 0;

    public void WriteByte(byte value) => Take(1)[0] = value;

    public void WriteInt(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

    public void WriteLong(long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);

    public void WriteULong(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);

    public void WriteFloat(float value) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);

    /// <summary>
    /// An absolute position as three int32 in 1/64 cm (<see cref="SessionProtocol.PositionStepsPerMetre"/>), the same
    /// whatever either peer's origin; throws when it is not finite or lies past
    /// <see cref="SessionProtocol.MaxPositionMetres"/>. A game's messages use it (the engine's own carry no positions).
    /// </summary>
    public void WritePosition(in WorldPoint point)
    {
        WriteInt(PositionSteps(point.X, nameof(point)));
        WriteInt(PositionSteps(point.Y, nameof(point)));
        WriteInt(PositionSteps(point.Z, nameof(point)));
    }

    /// <summary>A local position (from <paramref name="origin"/>, this peer's) as <see cref="WritePosition"/> writes it.</summary>
    public void WriteLocalPosition(WorldOrigin origin, Vector3 local)
    {
        ArgumentNullException.ThrowIfNull(origin);
        WritePosition(origin.ToWorld(local));
    }

    /// <summary>A non-negative number in 1 to 5 bytes, 7 bits each, low first (as <c>BinaryWriter.Write7BitEncodedInt</c>).</summary>
    public void WriteVarInt(int value)
    {
        var rest = (uint)value;
        while (rest >= 0x80)
        {
            WriteByte((byte)(rest | 0x80));
            rest >>= 7;
        }

        WriteByte((byte)rest);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Take(bytes.Length));

    /// <summary>UTF-8 behind a 16-bit byte count; throws when longer than <see cref="SessionProtocol.MaxStringBytes"/>.</summary>
    public void WriteString(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        if (count > SessionProtocol.MaxStringBytes)
        {
            throw new ArgumentException("The string is too long for a session message.", nameof(value));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(Take(2), (ushort)count);
        Encoding.UTF8.GetBytes(value, Take(count));
    }

    private static int PositionSteps(double metres, string name)
    {
        var steps = Math.Round(metres * SessionProtocol.PositionStepsPerMetre);
        return Math.Abs(steps) <= int.MaxValue
            ? (int)steps
            : throw new ArgumentException($"A message cannot carry a position at {metres} m.", name);
    }

    private Span<byte> Take(int count)
    {
        if (_length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        }

        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }
}

/// <summary>
/// Reads a message from a peer, who is not trusted: reading past the end throws <see cref="InvalidDataException"/>, and
/// nothing read is larger than the bytes that hold it.
/// </summary>
public ref struct MessageReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte() => Take(1)[0];

    public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong ReadULong() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public int ReadVarInt()
    {
        var value = 0u;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var next = ReadByte();
            // The fifth byte carries bits 28 to 30 only: more would be shifted out and lost, so the number is refused
            // rather than read as another.
            if (shift == 28 && (next & 0x7F) > 0x07)
            {
                throw Invalid("A number is out of range.");
            }

            value |= (uint)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return value <= int.MaxValue ? (int)value : throw Invalid("A number is out of range.");
            }
        }

        throw Invalid("A number does not end within five bytes.");
    }

    public string ReadString()
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        if (count > SessionProtocol.MaxStringBytes)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"A string claims {count} bytes."));
        }

        return Encoding.UTF8.GetString(Take(count));
    }

    /// <summary>The next <paramref name="count"/> bytes.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count) =>
        count >= 0 ? Take(count) : throw Invalid("A negative byte count.");

    public float ReadFloat() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    /// <summary>
    /// A position <see cref="MessageWriter.WritePosition"/> wrote: absolute, within
    /// <see cref="SessionProtocol.MaxPositionMetres"/> on every axis, which may lie off the map; an axis of
    /// <see cref="int.MinValue"/>, which the writer never writes, is refused.
    /// </summary>
    public WorldPoint ReadPosition()
    {
        var x = ReadPositionSteps();
        var y = ReadPositionSteps();
        var z = ReadPositionSteps();
        return new WorldPoint(
            (double)x / SessionProtocol.PositionStepsPerMetre,
            (double)y / SessionProtocol.PositionStepsPerMetre,
            (double)z / SessionProtocol.PositionStepsPerMetre
        );
    }

    /// <summary>A position <see cref="ReadPosition"/> reads, local to <paramref name="origin"/> (this peer's).</summary>
    public Vector3 ReadLocalPosition(WorldOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.ToLocal(ReadPosition());
    }

    /// <summary>Everything not read yet.</summary>
    public ReadOnlySpan<byte> ReadRest() => Take(Remaining);

    /// <summary>Throws unless the whole message has been read.</summary>
    public readonly void EnsureEnd()
    {
        if (Remaining != 0)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"The message has {Remaining} bytes too many."));
        }
    }

    private int ReadPositionSteps()
    {
        var steps = ReadInt();
        return steps != int.MinValue ? steps : throw Invalid("A position is out of range.");
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _data.Length - _position)
        {
            throw Invalid("The message ends early.");
        }

        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
