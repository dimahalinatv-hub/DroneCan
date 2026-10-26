using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Link;
using HobbywingCan.Core.Mavlink;
using HobbywingCan.Core.Monitoring;
using HobbywingCan.Core.Simulation;

namespace HobbywingCan.Core.Tests;

/// <summary>Runs link + node + monitor + config reader against a fake ArduPilot with Hobbywing ESCs on CAN1.</summary>
public class SimulatedAutopilotTests
{
    private const byte EscNode = 3;

    [Fact]
    public async Task EndToEnd_TelemetryAndConfiguration()
    {
        var fc = new SimulatedAutopilot([EscNode], autoRun: false);
        var link = new MavlinkCanLink(fc, new MavlinkCanLinkOptions { CanBus = 1, EscTelemetryRateHz = 0 });
        using var node = new DroneCanNode(link, 126);
        using var monitor = new EscMonitor(node, link);
        link.Start();

        fc.SendHeartbeat(armed: false);
        fc.ForwardBroadcast(EscNode, DataTypes.HwStatusMsg1, Convert.FromHexString("8813dc050140"));
        fc.ForwardBroadcast(EscNode, DataTypes.HwStatusMsg2, Convert.FromHexString("f701f4ff2d"));
        fc.ForwardBroadcast(EscNode, DataTypes.HwGetEscId, [EscNode, 4]);

        var state = await WaitFor(() => monitor.Get(EscNode) is { Msg1: not null, Msg2: not null, ThrottleChannel: not null } s ? s : null);
        Assert.Equal(5000, state.Msg1!.Rpm);
        Assert.Equal(50.3, state.Msg2!.Voltage, 3);
        Assert.Equal((byte)4, state.ThrottleChannel);

        var traffic = monitor.Traffic.Single(t => t.SourceNodeId == EscNode && t.DataTypeId == DataTypes.HwStatusMsg1.Id);
        Assert.Equal("com.hobbywing.esc.StatusMsg1", traffic.TypeName);
        Assert.Equal(1, traffic.Count);

        var report = await new EscConfigReader(node).ReadAsync(EscNode, includeParameters: true, progress: null);

        Assert.Equal("com.hobbywing.esc.sim", report.Info?.Name);
        Assert.Equal((byte?)1, report.MajorConfig?.ThrottleChannel);
        Assert.Equal((byte)0, report.Direction);
        Assert.Equal(123456u, report.Maintenance?.TotalRotationTimeMin);
        Assert.Equal(3, report.ReportingRates.Count);
        Assert.Equal(["MOT_POLES", "K"], report.Parameters.Select(p => p.Name));
        Assert.Empty(report.Errors);

        // the app must have enabled forwarding and never asked to clear maintenance counters
        Assert.Contains(fc.Commands, c => c == MavlinkIds.MavCmdCanForward);
        Assert.DoesNotContain(fc.Requests, r => r.Type == DataTypes.HwGetMaintenanceInformation.Id && r.Payload[0] != 0);

        await link.DisposeAsync();
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe) where T : class
    {
        for (int i = 0; i < 200; i++)
        {
            if (probe() is { } value)
            {
                return value;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }
}
