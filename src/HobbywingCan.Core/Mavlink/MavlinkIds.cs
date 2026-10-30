namespace HobbywingCan.Core.Mavlink;

/// <summary>MAVLink message ids and CRC_EXTRA values (common + ardupilotmega dialects) used by this app.</summary>
public static class MavlinkIds
{
    public const uint Heartbeat = 0;
    public const uint ParamRequestRead = 20;
    public const uint ParamValue = 22;
    public const uint ParamSet = 23;
    public const uint CommandLong = 76;
    public const uint CommandAck = 77;
    public const uint StatusText = 253;
    public const uint CanFrame = 386;
    public const uint CanFdFrame = 387;
    public const uint CanFilterModify = 388;
    public const uint EscTelemetry1To4 = 11030;
    public const uint EscTelemetry5To8 = 11031;
    public const uint EscTelemetry9To12 = 11032;

    public const ushort MavCmdPreflightRebootShutdown = 246;
    public const ushort MavCmdSetMessageInterval = 511;
    public const ushort MavCmdCanForward = 32000;

    public const byte MavTypeGcs = 6;
    public const byte MavAutopilotInvalid = 8;
    public const byte MavModeFlagSafetyArmed = 0x80;

    // MAV_PARAM_TYPE; ArduPilot sends every value as a float, whatever the type
    public const byte MavParamTypeInt8 = 2;
    public const byte MavParamTypeInt16 = 4;
    public const byte MavParamTypeInt32 = 6;
    public const byte MavParamTypeReal32 = 9;

    public const byte CanFilterReplace = 0;
    public const byte CanFilterAdd = 1;

    private static readonly Dictionary<uint, byte> CrcExtras = new()
    {
        [Heartbeat] = 50,
        [ParamRequestRead] = 214,
        [ParamValue] = 220,
        [ParamSet] = 168,
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
