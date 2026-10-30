using HobbywingCan.Core.Autopilot;
using HobbywingCan.Core.Link;
using HobbywingCan.Core.Mavlink;
using HobbywingCan.Core.Simulation;

namespace HobbywingCan.Core.Tests;

/// <summary>PARAM_* encoding (reference bytes from pymavlink 2.4.50, MAVLink 2) and reading / writing against the simulator.</summary>
public class AutopilotParamTests
{
    private static string Encode(uint id, byte[] payload, byte sys = 255, byte comp = 190) =>
        Convert.ToHexString(new MavlinkEncoder(sys, comp).Encode(id, payload));

    [Fact]
    public void ParamRequestRead_MatchesPymavlink()
    {
        Assert.Equal("FD12000000FFBE140000FFFF010143414E5F50315F42495452415445C708",
            Encode(MavlinkIds.ParamRequestRead, MavlinkMessages.EncodeParamRequestRead(1, 1, "CAN_P1_BITRATE")));
    }

    [Fact]
    public void ParamSet_MatchesPymavlink()
    {
        Assert.Equal("FD17000000FFBE1700000024F448010143414E5F50315F42495452415445000006E96F",
            Encode(MavlinkIds.ParamSet, MavlinkMessages.EncodeParamSet(1, 1, "CAN_P1_BITRATE", 500000, MavlinkIds.MavParamTypeInt32)));
        // a 16-character name has no terminator
        Assert.Equal("FD17000000FFBE17000000007041010143414E5F44315F55435F4553435F424D06119B",
            Encode(MavlinkIds.ParamSet, MavlinkMessages.EncodeParamSet(1, 1, "CAN_D1_UC_ESC_BM", 15, MavlinkIds.MavParamTypeInt32)));
    }

    [Fact]
    public void ParamValue_MatchesPymavlinkAndDecodes()
    {
        var param = new AutopilotParam("FRAME_TYPE", 18, MavlinkIds.MavParamTypeInt32, 1234, 56);
        var bytes = new MavlinkEncoder(1, 1).Encode(MavlinkIds.ParamValue, MavlinkMessages.EncodeParamValue(param));
        Assert.Equal("FD19000000010116000000009041D20438004652414D455F54595045000000000000069E07", Convert.ToHexString(bytes));

        var packet = Assert.Single(new MavlinkParser().Push(bytes));
        Assert.Equal(param, MavlinkMessages.DecodeParamValue(packet));
    }

    [Fact]
    public void Reboot_MatchesPymavlink()
    {
        Assert.Equal("FD20000000FFBE4C00000000803F000000000000000000000000000000000000000000000000F60001010EC8",
            Encode(MavlinkIds.CommandLong, MavlinkMessages.EncodeCommandLong(1, 1, MavlinkIds.MavCmdPreflightRebootShutdown, 1)));
    }

    [Fact]
    public async Task ReadWriteAndReboot_AgainstSimulator()
    {
        using var fc = new SimulatedAutopilot([1], autoRun: false);
        await using var link = new MavlinkCanLink(fc, new MavlinkCanLinkOptions { EscTelemetryRateHz = 0 });
        using var parameters = new AutopilotParams(link);
        var acks = new List<(ushort, byte)>();
        link.CommandAckReceived += (c, r) => { lock (acks) { acks.Add((c, r)); } };
        link.Start();
        fc.SendHeartbeat(armed: false);

        var bitrate = await parameters.ReadAsync("CAN_P1_BITRATE");
        Assert.Equal(1000000f, bitrate?.Value);
        Assert.Equal(MavlinkIds.MavParamTypeInt32, bitrate?.Type);
        Assert.Null(await parameters.ReadAsync("CAN_D2_UC_NODE"));

        var write = await parameters.SetAsync("CAN_P1_BITRATE", 500000);
        Assert.Equal(ParamWriteStatus.Ok, write.Status);
        Assert.Equal(500000f, fc.GetParam("CAN_P1_BITRATE"));

        // not read before: the type comes from a read first; integers are rounded
        Assert.True((await parameters.SetAsync("FRAME_TYPE", 18.4f)).Success);
        Assert.Equal(18f, fc.GetParam("FRAME_TYPE"));

        Assert.Equal(ParamWriteStatus.NotFound, (await parameters.SetAsync("CAN_D2_UC_NODE", 10)).Status);
        Assert.Null(fc.GetParam("CAN_D2_UC_NODE"));

        link.RebootAutopilot();
        for (int i = 0; i < 100 && fc.Reboots == 0; i++)
        {
            await Task.Delay(10);
        }
        await Task.Delay(50);
        Assert.Equal(1, fc.Reboots);
        lock (acks)
        {
            Assert.Contains((MavlinkIds.MavCmdPreflightRebootShutdown, (byte)0), acks);
        }
    }

    [Fact]
    public void Specs_FollowBusAndDriverAndKeepOtherOptionBits()
    {
        var specs = ArduPilotParamSpecs.Build(canBus: 2, driver: 1);
        Assert.Contains(specs, s => s.Name == "CAN_P2_BITRATE");
        Assert.Contains(specs, s => s.Name == "CAN_D1_UC_ESC_BM");
        Assert.DoesNotContain(specs, s => s.Name.StartsWith("CAN_P1_"));
        Assert.All(specs, s => Assert.True(s.Name.Length <= 16, s.Name));

        var option = specs.Single(s => s.Name == "CAN_D1_UC_OPTION");
        Assert.Equal(128 + 4, option.RecommendedFor(4));
        Assert.Equal("EnableCanfd, HobbyWingESC", option.Describe(132));

        Assert.Equal(18, specs.Single(s => s.Name == "FRAME_TYPE").Recommended);
        Assert.Equal("BetaFlightXReversed", specs.Single(s => s.Name == "FRAME_TYPE").Describe(18));
        Assert.Equal(36, specs.Single(s => s.Name == "SERVO4_FUNCTION").Recommended);
        Assert.Null(specs.Single(s => s.Name == "CAN_P2_BITRATE").Recommended);

        var escBm = specs.Single(s => s.Name == "CAN_D1_UC_ESC_BM");
        Assert.Equal(15, escBm.Recommended);
        Assert.Equal("1–4, 6", escBm.Describe(47));
        Assert.Equal("", escBm.Describe(0));

        Assert.Contains(ArduPilotParamSpecs.Build(1, 2), s => s.Name == "CAN_D2_PROTOCOL");
    }
}
