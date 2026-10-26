using HobbywingCan.Core.DroneCan;

namespace HobbywingCan.Core.Messages;

/// <summary>com.hobbywing.esc.StatusMsg1 (20050): fast status.</summary>
public sealed record HwStatusMsg1(ushort Rpm, ushort Pwm, ushort Status)
{
    public static HwStatusMsg1 Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new HwStatusMsg1(r.ReadU16(), r.ReadU16(), r.ReadU16());
    }

    /// <summary>Bit 14 of status is the rotation direction (as used by the DroneCAN GUI Hobbywing panel).</summary>
    public bool CounterClockwise => (Status & (1 << 14)) != 0;
}

/// <summary>com.hobbywing.esc.StatusMsg2 (20051): voltage (0.1 V), current (0.1 A), temperature (°C).</summary>
public sealed record HwStatusMsg2(short InputVoltageRaw, short CurrentRaw, byte Temperature)
{
    public static HwStatusMsg2 Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new HwStatusMsg2((short)r.ReadSigned(16), (short)r.ReadSigned(16), r.ReadU8());
    }

    public double Voltage => InputVoltageRaw * 0.1;
    public double Current => CurrentRaw * 0.1;
}

/// <summary>com.hobbywing.esc.StatusMsg3 (20052): MOSFET, capacitor and motor temperatures (°C).</summary>
public sealed record HwStatusMsg3(byte MosTemperature, byte CapacitorTemperature, byte MotorTemperature)
{
    public static HwStatusMsg3 Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new HwStatusMsg3(r.ReadU8(), r.ReadU8(), r.ReadU8());
    }
}

/// <summary>com.hobbywing.esc.GetEscID (20013): ESC replies with [node_id, throttle_channel].</summary>
public static class HwGetEscId
{
    public static byte[] EncodeQuery() => [0];

    public static bool TryDecodeReply(byte sourceNodeId, byte[] payload, out byte throttleChannel)
    {
        throttleChannel = 0;
        if (payload.Length != 2 || payload[0] != sourceNodeId)
        {
            return false;
        }
        throttleChannel = payload[1];
        return true;
    }
}

/// <summary>Reporting rate codes shared by SetReportingFrequency and GetMajorConfig.</summary>
public static class HwRate
{
    public const byte Read = 0;
    public const byte Write = 1;

    /// <summary>Selectable rates (code, Hz) in descending order.</summary>
    public static IReadOnlyList<(byte Code, int Hz)> Options { get; } =
        [(1, 500), (2, 250), (3, 200), (4, 100), (5, 50), (6, 20), (7, 10), (8, 1), (9, 0)];

    public static int? ToHz(int code) => Options.Where(o => o.Code == code).Select(o => (int?)o.Hz).FirstOrDefault();

    public static string ToText(int code) => code switch
    {
        1 => "500 Hz",
        2 => "250 Hz",
        3 => "200 Hz",
        4 => "100 Hz",
        5 => "50 Hz",
        6 => "20 Hz",
        7 => "10 Hz",
        8 => "1 Hz",
        9 => "0 Hz (off)",
        _ => $"code {code}",
    };
}

/// <summary>com.hobbywing.esc.GetMajorConfig (242) response.</summary>
public sealed record HwMajorConfig(
    bool CounterClockwise,
    bool ThrottleSourcePwm,
    byte ThrottleChannel,
    byte LedStatus,
    byte LedColor,
    byte Msg2RateCode,
    byte Msg1RateCode,
    ushort PositioningAngle,
    byte[] Reserved)
{
    public static byte[] EncodeRequest(byte option = 0) => [option];

    public static HwMajorConfig Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new HwMajorConfig(
            r.ReadBool(),
            r.ReadBool(),
            (byte)r.ReadUnsigned(6),
            (byte)r.ReadUnsigned(5),
            (byte)r.ReadUnsigned(3),
            (byte)r.ReadUnsigned(4),
            (byte)r.ReadUnsigned(4),
            r.ReadU16(),
            r.ReadBytes(2));
    }

    public string DirectionText => CounterClockwise ? "CCW" : "CW";
    public string ThrottleSourceText => ThrottleSourcePwm ? "PWM" : "CAN (digital)";
    public string LedColorText => HwSetLed.ColorName(LedColor);
    public string LedModeText => HwSetLed.BlinkName(LedStatus);
}

/// <summary>com.hobbywing.esc.GetMaintenanceInformation (241) response.</summary>
public sealed record HwMaintenanceInfo(uint TotalRotationTimeMin, uint TimeSinceMaintenanceMin)
{
    /// <param name="clear">true would reset the maintenance counter on the ESC; the app only reads (false).</param>
    public static byte[] EncodeRequest(bool clear = false) => [clear ? (byte)1 : (byte)0];

    public static HwMaintenanceInfo Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new HwMaintenanceInfo(r.ReadU32(), (uint)r.ReadUnsigned(24));
    }
}

/// <summary>com.hobbywing.esc.SetDirection (213); direction 255 is a query that does not change anything.</summary>
public static class HwSetDirection
{
    public const byte Clockwise = 0;
    public const byte CounterClockwise = 1;
    public const byte Query = 255;

    public static byte[] EncodeQuery() => [Query];

    public static byte[] Encode(byte direction) => [direction];

    public static byte DecodeResponse(byte[] payload) => payload.Length > 0 ? payload[0] : (byte)0;

    public static string ToText(byte direction) => direction switch
    {
        0 => "CW",
        1 => "CCW",
        _ => $"unknown ({direction})",
    };
}

/// <summary>com.hobbywing.esc.SetReportingFrequency (214) with OPTION_READ.</summary>
public sealed record HwReportingFrequency(byte Option, ushort MessageId, byte RateCode)
{
    public static byte[] EncodeReadRequest(ushort messageId) => Encode(HwRate.Read, messageId, 0);

    public static byte[] EncodeWriteRequest(ushort messageId, byte rateCode) => Encode(HwRate.Write, messageId, rateCode);

    private static byte[] Encode(byte option, ushort messageId, byte rateCode)
    {
        var w = new BitWriter();
        w.WriteU8(option);
        w.WriteU16(messageId);
        w.WriteU8(rateCode);
        return w.ToArray();
    }

    public static HwReportingFrequency Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new HwReportingFrequency(r.ReadU8(), r.ReadU16(), r.ReadU8());
    }
}

/// <summary>com.hobbywing.esc.SetThrottleSource (215): where the ESC takes its throttle from.</summary>
public static class HwSetThrottleSource
{
    public const byte Can = 0;
    public const byte Pwm = 1;

    public static byte[] Encode(byte source) => [source];

    public static byte DecodeResponse(byte[] payload) => payload.Length > 0 ? payload[0] : (byte)0xFF;

    public static string ToText(byte source) => source switch
    {
        Can => "CAN (digital)",
        Pwm => "PWM",
        _ => $"unknown ({source})",
    };
}

/// <summary>com.hobbywing.esc.SetID (210): sets node ID and throttle channel together.</summary>
public sealed record HwSetId(byte NodeId, byte ThrottleChannel)
{
    public byte[] Encode() => [NodeId, ThrottleChannel];

    public static HwSetId Decode(byte[] payload) =>
        new(payload.Length > 0 ? payload[0] : (byte)0, payload.Length > 1 ? payload[1] : (byte)0);
}

/// <summary>com.hobbywing.esc.SetLED (212).</summary>
public sealed record HwSetLed(byte Option, byte Color, byte Blink)
{
    public const byte OptionSave = 1;

    public static IReadOnlyList<(byte Color, string Name)> Colors { get; } =
        [(0, "Off"), (4, "Red"), (2, "Green"), (1, "Blue"), (6, "Yellow (R+G)"), (5, "Magenta (R+B)"), (3, "Cyan (G+B)"), (7, "White (R+G+B)")];

    public static IReadOnlyList<(byte Blink, string Name)> BlinkModes { get; } =
        [(0, "Steady"), (1, "Blink 1 Hz"), (2, "Blink 2 Hz"), (5, "Blink 5 Hz")];

    public static string ColorName(byte color) =>
        Colors.Where(c => c.Color == color).Select(c => c.Name).FirstOrDefault() ?? $"code {color}";

    public static string BlinkName(byte blink) =>
        BlinkModes.Where(b => b.Blink == blink).Select(b => b.Name).FirstOrDefault() ?? $"code {blink}";

    public byte[] Encode() => [Option, Color, Blink];

    public static HwSetLed Decode(byte[] payload) => new(
        payload.Length > 0 ? payload[0] : (byte)0,
        payload.Length > 1 ? payload[1] : (byte)0,
        payload.Length > 2 ? payload[2] : (byte)0);
}
