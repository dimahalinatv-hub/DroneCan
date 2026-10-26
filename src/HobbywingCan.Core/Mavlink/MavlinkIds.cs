namespace HobbywingCan.Core.Mavlink;

/// <summary>MAVLink message ids and CRC_EXTRA values (common + ardupilotmega dialects) used by this app.</summary>
public static class MavlinkIds
{
    public const uint Heartbeat = 0;
    public const uint CommandLong = 76;
    public const uint CommandAck = 77;
    public const uint StatusText = 253;
    public const uint CanFrame = 386;
    public const uint CanFdFrame = 387;
    public const uint CanFilterModify = 388;
    public const uint EscTelemetry1To4 = 11030;
    public const uint EscTelemetry5To8 = 11031;
    public const uint EscTelemetry9To12 = 11032;

    public const ushort MavCmdSetMessageInterval = 511;
    public const ushort MavCmdCanForward = 32000;

    public const byte MavTypeGcs = 6;
    public const byte MavAutopilotInvalid = 8;
    public const byte MavModeFlagSafetyArmed = 0x80;

    public const byte CanFilterReplace = 0;

    private static readonly Dictionary<uint, byte> CrcExtras = new()
    {
        [Heartbeat] = 50,
        [CommandLong] = 152,
        [CommandAck] = 143,
        [StatusText] = 83,
        [CanFrame] = 132,
        [CanFdFrame] = 4,
        [CanFilterModify] = 8,
        [EscTelemetry1To4] = 144,
        [EscTelemetry5To8] = 133,
        [EscTelemetry9To12] = 85,
    };

    public static bool TryGetCrcExtra(uint messageId, out byte crcExtra) => CrcExtras.TryGetValue(messageId, out crcExtra);
}
