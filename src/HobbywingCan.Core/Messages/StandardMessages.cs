using System.Text;
using HobbywingCan.Core.DroneCan;

namespace HobbywingCan.Core.Messages;

/// <summary>uavcan.protocol.NodeStatus (also embedded in GetNodeInfo response).</summary>
public sealed record NodeStatus(uint UptimeSec, byte Health, byte Mode, byte SubMode, ushort VendorSpecificStatusCode)
{
    public static NodeStatus Decode(BitReader r) =>
        new(r.ReadU32(), (byte)r.ReadUnsigned(2), (byte)r.ReadUnsigned(3), (byte)r.ReadUnsigned(3), r.ReadU16());

    public static NodeStatus Decode(byte[] payload) => Decode(new BitReader(payload));

    public string HealthText => Health switch
    {
        0 => "OK",
        1 => "WARNING",
        2 => "ERROR",
        3 => "CRITICAL",
        _ => Health.ToString(),
    };

    public string ModeText => Mode switch
    {
        0 => "OPERATIONAL",
        1 => "INITIALIZATION",
        2 => "MAINTENANCE",
        3 => "SOFTWARE_UPDATE",
        7 => "OFFLINE",
        _ => Mode.ToString(),
    };
}

/// <summary>uavcan.protocol.GetNodeInfo response.</summary>
public sealed record NodeInfo(
    NodeStatus Status,
    byte SoftwareMajor,
    byte SoftwareMinor,
    byte SoftwareOptionalFieldFlags,
    uint SoftwareVcsCommit,
    ulong SoftwareImageCrc,
    byte HardwareMajor,
    byte HardwareMinor,
    byte[] UniqueId,
    byte[] CertificateOfAuthenticity,
    string Name)
{
    public static NodeInfo Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        var status = NodeStatus.Decode(r);
        byte swMajor = r.ReadU8();
        byte swMinor = r.ReadU8();
        byte flags = r.ReadU8();
        uint vcs = r.ReadU32();
        ulong crc = r.ReadUnsigned(64);
        byte hwMajor = r.ReadU8();
        byte hwMinor = r.ReadU8();
        var uid = r.ReadBytes(16);
        var coa = r.ReadDynamicBytes(8);
        var name = Encoding.ASCII.GetString(r.ReadTailBytes(80));
        return new NodeInfo(status, swMajor, swMinor, flags, vcs, crc, hwMajor, hwMinor, uid, coa, name);
    }

    public string SoftwareVersion
    {
        get
        {
            var text = $"{SoftwareMajor}.{SoftwareMinor}";
            if ((SoftwareOptionalFieldFlags & 1) != 0) text += $" (git {SoftwareVcsCommit:x8})";
            if ((SoftwareOptionalFieldFlags & 2) != 0) text += $" crc {SoftwareImageCrc:X16}";
            return text;
        }
    }

    public string HardwareVersion => $"{HardwareMajor}.{HardwareMinor}";
}

/// <summary>uavcan.equipment.esc.Status.</summary>
public sealed record EscStatus(uint ErrorCount, float Voltage, float Current, float TemperatureK, int Rpm, byte PowerRatingPct, byte EscIndex)
{
    public static EscStatus Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new EscStatus(r.ReadU32(), r.ReadFloat16(), r.ReadFloat16(), r.ReadFloat16(),
            (int)r.ReadSigned(18), (byte)r.ReadUnsigned(7), (byte)r.ReadUnsigned(5));
    }

    public float TemperatureC => TemperatureK - 273.15f;
}

/// <summary>uavcan.equipment.esc.StatusExtended.</summary>
public sealed record EscStatusExtended(byte InputPct, byte OutputPct, short MotorTemperatureC, ushort MotorAngle, uint StatusFlags, byte EscIndex)
{
    public static EscStatusExtended Decode(byte[] payload)
    {
        var r = new BitReader(payload);
        return new EscStatusExtended((byte)r.ReadUnsigned(7), (byte)r.ReadUnsigned(7), (short)r.ReadSigned(9),
            (ushort)r.ReadUnsigned(9), (uint)r.ReadUnsigned(19), (byte)r.ReadUnsigned(5));
    }
}

public enum ParamValueKind
{
    Empty,
    Integer,
    Real,
    Boolean,
    String,
}

/// <summary>uavcan.protocol.param.Value / NumericValue.</summary>
public sealed record ParamValue(ParamValueKind Kind, long Integer = 0, float Real = 0, bool Boolean = false, string Text = "")
{
    public static readonly ParamValue Empty = new(ParamValueKind.Empty);

    public static ParamValue DecodeValue(BitReader r)
    {
        var tag = (int)r.ReadUnsigned(3);
        return tag switch
        {
            0 => Empty,
            1 => new ParamValue(ParamValueKind.Integer, Integer: r.ReadSigned(64)),
            2 => new ParamValue(ParamValueKind.Real, Real: r.ReadFloat32()),
            3 => new ParamValue(ParamValueKind.Boolean, Boolean: r.ReadU8() != 0),
            4 => new ParamValue(ParamValueKind.String, Text: Encoding.ASCII.GetString(r.ReadDynamicBytes(8))),
            _ => throw new FormatException($"Invalid param.Value tag {tag}"),
        };
    }

    public static ParamValue DecodeNumeric(BitReader r)
    {
        var tag = (int)r.ReadUnsigned(2);
        return tag switch
        {
            0 => Empty,
            1 => new ParamValue(ParamValueKind.Integer, Integer: r.ReadSigned(64)),
            2 => new ParamValue(ParamValueKind.Real, Real: r.ReadFloat32()),
            _ => throw new FormatException($"Invalid param.NumericValue tag {tag}"),
        };
    }

    public override string ToString() => Kind switch
    {
        ParamValueKind.Empty => "",
        ParamValueKind.Integer => Integer.ToString(),
        ParamValueKind.Real => Real.ToString("G7", System.Globalization.CultureInfo.InvariantCulture),
        ParamValueKind.Boolean => Boolean ? "true" : "false",
        _ => Text,
    };
}

/// <summary>uavcan.protocol.param.GetSet.</summary>
public sealed record ParamEntry(ushort Index, string Name, ParamValue Value, ParamValue DefaultValue, ParamValue MaxValue, ParamValue MinValue)
{
    /// <summary>Request reading a parameter by index (empty value, empty name).</summary>
    public static byte[] EncodeGetByIndex(ushort index)
    {
        var w = new BitWriter();
        w.WriteUnsigned(index, 13);
        w.WriteUnsigned(0, 3); // Value.empty
        return w.ToArray();
    }

    /// <summary>Decodes a GetSet response; returns null when the name is empty (index past the end).</summary>
    public static ParamEntry? DecodeResponse(ushort index, byte[] payload)
    {
        var r = new BitReader(payload);
        r.Skip(5);
        var value = ParamValue.DecodeValue(r);
        r.Skip(5);
        var def = ParamValue.DecodeValue(r);
        r.Skip(6);
        var max = ParamValue.DecodeNumeric(r);
        r.Skip(6);
        var min = ParamValue.DecodeNumeric(r);
        var name = Encoding.ASCII.GetString(r.ReadTailBytes(92));
        return name.Length == 0 ? null : new ParamEntry(index, name, value, def, max, min);
    }
}
