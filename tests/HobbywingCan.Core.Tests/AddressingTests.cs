using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Link;
using HobbywingCan.Core.Monitoring;
using HobbywingCan.Core.Simulation;

namespace HobbywingCan.Core.Tests;

/// <summary>ESCs that ignore the destination node id or share an id must be detected, not silently written to.</summary>
public class AddressingTests
{
    private static async Task<(SimulatedAutopilot Fc, MavlinkCanLink Link, DroneCanNode Node, EscMonitor Monitor)> Start(
        byte[] escIds, bool setIdQuirk)
    {
        var fc = new SimulatedAutopilot(escIds, autoRun: false, setIdIgnoresDestination: setIdQuirk);
        var link = new MavlinkCanLink(fc, new MavlinkCanLinkOptions { EscTelemetryRateHz = 0 });
        var node = new DroneCanNode(link, 126);
        var monitor = new EscMonitor(node, link);
        link.Start();
        await Task.CompletedTask;
        return (fc, link, node, monitor);
    }

    [Fact]
    public async Task SetId_OnX8G2_ReportsEveryEscThatTookIt()
    {
        var (fc, link, node, monitor) = await Start([3, 4], setIdQuirk: true);

        var result = await new EscConfigWriter(node).SetIdAsync(3, newNodeId: 3, throttleChannel: 7);

        // this is what the user saw: both ESCs now have node 3 / channel 7
        Assert.All(fc.Escs, e => Assert.Equal(7, e.ThrottleChannel));
        Assert.True(result.AffectedOthers);
        Assert.Equal([4], result.AlsoAnsweredBy);
        Assert.Contains("node(s) 4 answered too", result.Message);
        Assert.True(monitor.Get(4)!.IgnoresDestination);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task Probe_DistinctIds_TargetIsIsolated()
    {
        var (_, link, node, monitor) = await Start([3, 4], setIdQuirk: true);

        var probe = await new EscConfigWriter(node).ProbeAddressingAsync(3);

        Assert.True(probe.IsolatedTarget);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task Probe_FallsBackWhenFirstReadIsNotImplemented()
    {
        var (fc, link, node, monitor) = await Start([2, 4], setIdQuirk: true);
        fc.UnansweredServices.Add(DataTypes.HwSetReportingFrequency.Id);

        var probe = await new EscConfigWriter(node).ProbeAddressingAsync(2);

        Assert.True(probe.IsolatedTarget);
        Assert.Equal(DataTypes.HwGetMajorConfig.Name, probe.ProbedWith);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task Probe_NoReadAnswered_IsInconclusiveButWriteStillWorks()
    {
        // like the user's X8 G2: none of the read requests are answered
        var (fc, link, node, monitor) = await Start([2, 4], setIdQuirk: true);
        fc.UnansweredServices.UnionWith([DataTypes.HwSetReportingFrequency.Id, DataTypes.HwGetMajorConfig.Id, DataTypes.GetNodeInfo.Id]);
        var writer = new EscConfigWriter(node);

        var probe = await writer.ProbeAddressingAsync(2);
        var result = await writer.SetThrottleSourceAsync(2, Messages.HwSetThrottleSource.Pwm);

        Assert.True(probe.Inconclusive);
        Assert.Null(probe.ProbedWith);
        Assert.True(result.Success);
        Assert.True(fc.Escs[0].PwmThrottle);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task Probe_SharedNodeId_IsDetected()
    {
        var (_, link, node, monitor) = await Start([3, 3], setIdQuirk: false);

        var probe = await new EscConfigWriter(node).ProbeAddressingAsync(3);

        Assert.True(probe.TargetAnswered);
        Assert.True(probe.DuplicateNodeId);
        Assert.False(probe.IsolatedTarget);
        Assert.True(monitor.Get(3)!.DuplicateSuspected);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }
}
