using System.Buffers.Binary;
using System.Text;

namespace HobbywingCan.Core.Mavlink;

public sealed record VehicleHeartbeat(byte SystemId, byte ComponentId, byte Type, byte Autopilot, byte BaseMode, uint CustomMode, byte SystemStatus)
{
    public bool IsArmed => (BaseMode & MavlinkIds.MavModeFlagSafetyArmed) != 0;
}

/// <summary>One ESC slot of ESC_TELEMETRY_x_TO_y as published by ArduPilot.</summary>
public sealed record AutopilotEscTelemetry(int Index, double Voltage, double Current, int ConsumedMah, int Rpm, int Count, int TemperatureC);

/// <summary>Pack/unpack helpers for the handful of MAVLink messages this app uses (wire order = sorted by type size).</summary>
public static class MavlinkMessages
{
    public static VehicleHeartbeat DecodeHeartbeat(MavlinkPacket p) =>
        new(p.SystemId, p.ComponentId, Type: p.U8(4), Autopilot: p.U8(5), BaseMode: p.U8(6), CustomMode: p.U32(0), SystemStatus: p.U8(7));

    public static byte[] EncodeHeartbeat(byte type, byte autopilot)
    {
        var b = new byte[9];
        b[4] = type;
        b[5] = autopilot;
        b[8] = 3; // mavlink_version
        return b;
    }

    public static byte[] EncodeCommandLong(byte targetSystem, byte targetComponent, ushort command,
        float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
    {
        var b = new byte[33];
        var span = b.AsSpan();
        float[] ps = [p1, p2, p3, p4, p5, p6, p7];
        for (int i = 0; i < ps.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[(i * 4)..], ps[i]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], command);
        b[30] = targetSystem;
        b[31] = targetComponent;
        b[32] = 0; // confirmation
        return b;
    }

    public static (ushort Command, byte Result) DecodeCommandAck(MavlinkPacket p) => (p.U16(0), p.U8(2));

    /// <param name="bus">0-based CAN interface index.</param>
    /// <param name="id">Raw CAN id; bit 31 set for 29-bit extended frames (ArduPilot CANFrame::FlagEFF).</param>
    public static byte[] EncodeCanFrame(byte targetSystem, byte targetComponent, byte bus, uint id, ReadOnlySpan<byte> data)
    {
        if (data.Length > 8)
        {
            throw new ArgumentException("Classic CAN frame payload is limited to 8 bytes", nameof(data));
        }
        var b = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(b, id);
        b[4] = targetSystem;
        b[5] = targetComponent;
        b[6] = bus;
        b[7] = (byte)data.Length;
        data.CopyTo(b.AsSpan(8));
        return b;
    }

    /// <summary>Decodes CAN_FRAME / CANFD_FRAME: returns raw id (with flag bits), 0-based bus and data.</summary>
    public static (uint Id, byte Bus, byte[] Data) DecodeCanFrame(MavlinkPacket p)
    {
        int maxLength = p.MessageId == MavlinkIds.CanFdFrame ? 64 : 8;
        int length = Math.Min((int)p.U8(7), maxLength);
        return (p.U32(0), p.U8(6), p.Bytes(8, length));
    }

    /// <param name="bus">1-based bus number, as ArduPilot expects for CAN_FILTER_MODIFY.</param>
    public static byte[] EncodeCanFilterModify(byte targetSystem, byte targetComponent, byte bus, byte operation, IReadOnlyList<ushort> ids)
    {
        if (ids.Count > 16)
        {
            throw new ArgumentException("At most 16 ids per CAN_FILTER_MODIFY", nameof(ids));
        }
        var b = new byte[37];
        for (int i = 0; i < ids.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), ids[i]);
        }
        b[32] = targetSystem;
        b[33] = targetComponent;
        b[34] = bus;
        b[35] = operation;
        b[36] = (byte)ids.Count;
        return b;
    }

    /// <summary>Decodes ESC_TELEMETRY_1_TO_4 / 5_TO_8 / 9_TO_12 into four entries.</summary>
    public static AutopilotEscTelemetry[] DecodeEscTelemetry(MavlinkPacket p)
    {
        int first = p.MessageId switch
        {
            MavlinkIds.EscTelemetry1To4 => 0,
            MavlinkIds.EscTelemetry5To8 => 4,
            _ => 8,
        };
        var result = new AutopilotEscTelemetry[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = new AutopilotEscTelemetry(
                Index: first + i,
                Voltage: p.U16(i * 2) / 100.0,
                Current: p.U16(8 + i * 2) / 100.0,
                ConsumedMah: p.U16(16 + i * 2),
                Rpm: p.U16(24 + i * 2),
                Count: p.U16(32 + i * 2),
                TemperatureC: p.U8(40 + i));
        }
        return result;
    }

    public static (byte Severity, string Text) DecodeStatusText(MavlinkPacket p)
    {
        var raw = p.Bytes(1, 50);
        int end = Array.IndexOf(raw, (byte)0);
        return (p.U8(0), Encoding.ASCII.GetString(raw, 0, end < 0 ? raw.Length : end));
    }
}
