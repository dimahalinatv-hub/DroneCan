using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Link;
using HobbywingCan.Core.Messages;
using HobbywingCan.Core.Monitoring;
using HobbywingCan.Core.Simulation;

namespace HobbywingCan.Core.Tests;

public class ConfigWriterTests
{
    [Fact]
    public void WriteRequests_MatchReferenceEncoding()
    {
        Assert.Equal(Convert.FromHexString("01534e07"), HwReportingFrequency.EncodeWriteRequest(20051, 7));
        Assert.Equal(Convert.FromHexString("010602"), new HwSetLed(HwSetLed.OptionSave, 6, 2).Encode());
        Assert.Equal(Convert.FromHexString("0304"), new HwSetId(3, 4).Encode());
    }

    [Fact]
    public async Task Writes_AreAppliedAndVisibleInMajorConfig()
    {
        var fc = new SimulatedAutopilot([3, 4], autoRun: false);
        await using var link = new MavlinkCanLink(fc, new MavlinkCanLinkOptions { EscTelemetryRateHz = 0 });
        using var node = new DroneCanNode(link, 126);
        using var monitor = new EscMonitor(node, link);
        link.Start();
        var writer = new EscConfigWriter(node);
        var reader = new EscConfigReader(node);

        Assert.True((await writer.SetThrottleSourceAsync(3, HwSetThrottleSource.Pwm)).Success);
        Assert.True((await writer.SetDirectionAsync(3, HwSetDirection.CounterClockwise)).Success);
        Assert.True((await writer.SetReportingRateAsync(3, DataTypes.HwStatusMsg1.Id, 4)).Success);
        Assert.True((await writer.SetReportingRateAsync(3, DataTypes.HwStatusMsg3.Id, 9)).Success);
        Assert.True((await writer.SetLedAsync(3, color: 4, blink: 1, save: true)).Success);
        Assert.True((await writer.SetIdAsync(3, newNodeId: 3, throttleChannel: 7)).Success);

        var report = await reader.ReadAsync(3, includeParameters: false, progress: null);
        var c = report.MajorConfig!;
        Assert.True(c.ThrottleSourcePwm);
        Assert.True(c.CounterClockwise);
        Assert.Equal(7, c.ThrottleChannel);
        Assert.Equal(4, c.Msg1RateCode);
        Assert.Equal(4, c.LedColor);
        Assert.Equal(1, c.LedStatus); // the simulator reports the blink mode as led_status
        Assert.Equal((byte)9, report.ReportingRates[DataTypes.HwStatusMsg3.Id].RateCode);

        // the monitor picks the config up from the responses on the bus
        var state = monitor.Get(3)!;
        Assert.True(state.MajorConfig!.ThrottleSourcePwm);
        Assert.Equal((byte)HwSetDirection.CounterClockwise, state.DirectionSetting);
        Assert.True(state.CounterClockwise);
        Assert.Equal((byte)9, state.ReportingRateCodes[DataTypes.HwStatusMsg3.Id]);

        // the other ESC is untouched
        Assert.False(fc.Escs[1].PwmThrottle);
    }

    [Fact]
    public async Task DirectionQuery_ReportsSettingWithoutChangingIt()
    {
        // second simulated ESC is configured CCW
        var fc = new SimulatedAutopilot([3, 4], autoRun: false);
        await using var link = new MavlinkCanLink(fc, new MavlinkCanLinkOptions { EscTelemetryRateHz = 0 });
        using var node = new DroneCanNode(link, 126);
        using var monitor = new EscMonitor(node, link);
        link.Start();

        foreach (byte id in new byte[] { 3, 4 })
        {
            Assert.NotNull(await node.RequestAsync(DataTypes.HwSetDirection, id, HwSetDirection.EncodeQuery(), TimeSpan.FromSeconds(1)));
        }

        Assert.False(monitor.Get(3)!.CounterClockwise);
        Assert.True(monitor.Get(4)!.CounterClockwise);
        Assert.True(fc.Escs[1].CounterClockwise);
        Assert.False(fc.Escs[0].CounterClockwise);
    }

    [Fact]
    public async Task Write_ToMissingNode_ReportsNoResponse()
    {
        var fc = new SimulatedAutopilot([3], autoRun: false);
        await using var link = new MavlinkCanLink(fc, new MavlinkCanLinkOptions { EscTelemetryRateHz = 0 });
        using var node = new DroneCanNode(link, 126);
        link.Start();

        var result = await new EscConfigWriter(node).SetThrottleSourceAsync(9, HwSetThrottleSource.Can);

        Assert.False(result.Success);
        Assert.Contains("no response", result.Message);
    }
}
