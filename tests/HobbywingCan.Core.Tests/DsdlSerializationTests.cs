using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Messages;

namespace HobbywingCan.Core.Tests;

/// <summary>Reference payloads were produced with pydronecan (official DSDL) — see README.</summary>
public class DsdlSerializationTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void MajorConfig_DecodesBitFields()
    {
        var c = HwMajorConfig.Decode(Hex("851e752c01aa55"));
        Assert.True(c.CounterClockwise);
        Assert.False(c.ThrottleSourcePwm);
        Assert.Equal(5, c.ThrottleChannel);
        Assert.Equal(3, c.LedStatus);
        Assert.Equal(6, c.LedColor);
        Assert.Equal(7, c.Msg2RateCode);
        Assert.Equal(5, c.Msg1RateCode);
        Assert.Equal(300, c.PositioningAngle);
        Assert.Equal(new byte[] { 0xAA, 0x55 }, c.Reserved);
    }

    [Fact]
    public void MaintenanceInfo_DecodesUint24()
    {
        var m = HwMaintenanceInfo.Decode(Hex("40e20100f1fb09"));
        Assert.Equal(123456u, m.TotalRotationTimeMin);
        Assert.Equal(654321u, m.TimeSinceMaintenanceMin);
    }

    [Fact]
    public void HobbywingStatusMessages_Decode()
    {
        var m1 = HwStatusMsg1.Decode(Hex("8813dc050140"));
        Assert.Equal(5000, m1.Rpm);
        Assert.Equal(1500, m1.Pwm);
        Assert.Equal(0x4001, m1.Status);
        Assert.True(m1.CounterClockwise);

        var m2 = HwStatusMsg2.Decode(Hex("f701f4ff2d"));
        Assert.Equal(50.3, m2.Voltage, 3);
        Assert.Equal(-1.2, m2.Current, 3);
        Assert.Equal(45, m2.Temperature);
    }

    [Fact]
    public void EscStatus_DecodesFloat16AndInt18()
    {
        var s = EscStatus.Decode(Hex("07000000204e80c2d85cc7cfdb8c"));
        Assert.Equal(7u, s.ErrorCount);
        Assert.Equal(24.5f, s.Voltage);
        Assert.Equal(-3.25f, s.Current);
        Assert.Equal(310f, s.TemperatureK);
        Assert.Equal(-12345, s.Rpm);
        Assert.Equal(55, s.PowerRatingPct);
        Assert.Equal(3, s.EscIndex);
    }

    [Fact]
    public void EscStatusExtended_Decodes()
    {
        var s = EscStatusExtended.Decode(Hex("6ef3d259ffffe7"));
        Assert.Equal(55, s.InputPct);
        Assert.Equal(60, s.OutputPct);
        Assert.Equal(-12, s.MotorTemperatureC);
        Assert.Equal(300, s.MotorAngle);
        Assert.Equal(0x7FFFFu, s.StatusFlags); // 0x123456 saturated to uint19 max by the reference encoder
        Assert.Equal(7, s.EscIndex);
    }

    [Fact]
    public void NodeInfo_DecodesWithTailArray()
    {
        var info = NodeInfo.Decode(Hex(
            "e8030000423412010201efbeadde88776655443322110304000102030405060708090a0b0c0d0e0f03010203636f6d2e686f62627977696e672e78386732"));
        Assert.Equal(1000u, info.Status.UptimeSec);
        Assert.Equal(1, info.Status.Health);
        Assert.Equal(2, info.Status.SubMode);
        Assert.Equal(0x1234, info.Status.VendorSpecificStatusCode);
        Assert.Equal(1, info.SoftwareMajor);
        Assert.Equal(2, info.SoftwareMinor);
        Assert.Equal(0xDEADBEEFu, info.SoftwareVcsCommit);
        Assert.Equal(0x1122334455667788ul, info.SoftwareImageCrc);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), info.UniqueId);
        Assert.Equal(new byte[] { 1, 2, 3 }, info.CertificateOfAuthenticity);
        Assert.Equal("com.hobbywing.x8g2", info.Name);
    }

    [Fact]
    public void ParamGetSet_RequestByIndex()
    {
        Assert.Equal(Hex("0500"), ParamEntry.EncodeGetByIndex(5));
    }

    [Theory]
    [InlineData("01f2ffffffffffffff010e000000000000000164000000000000000102000000000000004d4f545f504f4c4553", "MOT_POLES", "-14", "14", "100", "2")]
    [InlineData("020000c03f020000803e00004b", "K", "1.5", "0.25", "", "")]
    [InlineData("040361626300000053", "S", "abc", "", "", "")]
    [InlineData("030100000042", "B", "true", "", "", "")]
    public void ParamGetSet_DecodesResponses(string hex, string name, string value, string def, string max, string min)
    {
        var p = ParamEntry.DecodeResponse(0, Hex(hex));
        Assert.NotNull(p);
        Assert.Equal(name, p.Name);
        Assert.Equal(value, p.Value.ToString());
        Assert.Equal(def, p.DefaultValue.ToString());
        Assert.Equal(max, p.MaxValue.ToString());
        Assert.Equal(min, p.MinValue.ToString());
    }

    [Fact]
    public void ParamGetSet_EmptyNameMeansEndOfList()
    {
        Assert.Null(ParamEntry.DecodeResponse(7, Hex("00000000")));
    }

    [Fact]
    public void BitWriter_RoundTripsOddWidths()
    {
        var w = new BitWriter();
        w.WriteUnsigned(1, 1);
        w.WriteUnsigned(0, 1);
        w.WriteUnsigned(5, 6);
        w.WriteUnsigned(3, 5);
        w.WriteUnsigned(6, 3);
        w.WriteUnsigned(7, 4);
        w.WriteUnsigned(5, 4);
        w.WriteU16(300);
        w.WriteBytes([0xAA, 0x55]);
        Assert.Equal(Hex("851e752c01aa55"), w.ToArray());

        var w2 = new BitWriter();
        w2.WriteSigned(-12345, 18);
        w2.WriteUnsigned(55, 7);
        w2.WriteUnsigned(3, 5);
        Assert.Equal(Hex("c7cfdb8c"), w2.ToArray());
    }
}
