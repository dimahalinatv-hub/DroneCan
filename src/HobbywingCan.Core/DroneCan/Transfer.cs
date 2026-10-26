namespace HobbywingCan.Core.DroneCan;

public enum TransferKind
{
    Broadcast,
    Request,
    Response,
}

/// <summary>A complete (reassembled) DroneCAN transfer.</summary>
public sealed record Transfer(
    TransferKind Kind,
    ushort DataTypeId,
    byte SourceNodeId,
    byte DestinationNodeId,
    byte TransferId,
    byte Priority,
    byte[] Payload)
{
    public bool IsService => Kind != TransferKind.Broadcast;
}

/// <summary>Encodes/decodes the 29-bit DroneCAN CAN identifier.</summary>
public static class CanId
{
    public static uint Message(byte priority, ushort dataTypeId, byte sourceNodeId) =>
        (uint)(priority & 0x1F) << 24 | (uint)dataTypeId << 8 | (uint)(sourceNodeId & 0x7F);

    public static uint Service(byte priority, byte dataTypeId, bool request, byte destinationNodeId, byte sourceNodeId) =>
        (uint)(priority & 0x1F) << 24 | (uint)dataTypeId << 16 | (request ? 1u << 15 : 0) |
        (uint)(destinationNodeId & 0x7F) << 8 | 1u << 7 | (uint)(sourceNodeId & 0x7F);

    public static byte Priority(uint id) => (byte)((id >> 24) & 0x1F);

    public static byte SourceNodeId(uint id) => (byte)(id & 0x7F);

    public static bool IsService(uint id) => (id & 0x80) != 0;

    public static TransferKind Kind(uint id) =>
        !IsService(id) ? TransferKind.Broadcast : (id & 0x8000) != 0 ? TransferKind.Request : TransferKind.Response;

    public static ushort DataTypeId(uint id)
    {
        if (IsService(id))
        {
            return (ushort)((id >> 16) & 0xFF);
        }
        // anonymous messages only carry the 2 lowest bits of the type id
        return SourceNodeId(id) == 0 ? (ushort)((id >> 8) & 0x3) : (ushort)((id >> 8) & 0xFFFF);
    }

    public static byte DestinationNodeId(uint id) => IsService(id) ? (byte)((id >> 8) & 0x7F) : (byte)0;
}
