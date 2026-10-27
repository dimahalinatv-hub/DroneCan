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
        Assert.Equal(2, probe.SameIdAnswers);
        Assert.False(probe.IsolatedTarget);
        Assert.True(monitor.Get(3)!.DuplicateSuspected);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task GetEscId_CountsDevicesSharingANodeId()
    {
        var (_, link, node, monitor) = await Start([1, 1, 1, 4], setIdQuirk: false);

        for (int i = 0; i < 4; i++)
        {
            monitor.QueryEscIds();
            await Task.Delay(200);
        }

        Assert.Equal(3, monitor.Get(1)!.DeviceCount);
        Assert.True(monitor.Get(1)!.DuplicateSuspected);
        Assert.Equal(1, monitor.Get(4)!.DeviceCount);
        Assert.False(monitor.Get(4)!.DuplicateSuspected);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task GetEscId_AutopilotPollingDoesNotDoubleTheCount()
    {
        var (fc, link, node, monitor) = await Start([1, 1], setIdQuirk: false);

        for (int i = 0; i < 4; i++)
        {
            monitor.QueryEscIds();
            await Task.Delay(150);
            // ArduPilot polls too, and that query may never be forwarded to the app: both ESCs answer it as well
            foreach (var esc in fc.Escs)
            {
                fc.ForwardBroadcast(1, DataTypes.HwGetEscId, [1, esc.ThrottleChannel], esc.EscIdTransferId);
                esc.EscIdTransferId++;
            }
            await Task.Delay(150);
        }

        Assert.Equal(2, monitor.Get(1)!.DeviceCount);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    /// <summary>Feeds GetEscID replies from node 1 (not simulated) with the given transfer ids, one group per reply.</summary>
    private static async Task<List<int?>> Replies(SimulatedAutopilot fc, EscMonitor monitor, IEnumerable<byte[]> groups)
    {
        var seen = new List<int?>();
        foreach (var group in groups)
        {
            foreach (byte tid in group)
            {
                fc.ForwardBroadcast(1, DataTypes.HwGetEscId, [1, 1], tid);
            }
            await Task.Delay(120);
            seen.Add(monitor.Get(1)?.DeviceCount);
        }
        return seen;
    }

    [Fact]
    public async Task GetEscId_OneEscAnsweringTwoPollers_IsOneEsc()
    {
        var (fc, link, node, monitor) = await Start([4], setIdQuirk: false);

        // the app's query and ArduPilot's: two replies a second, numbered one after the other
        var seen = await Replies(fc, monitor, Enumerable.Range(0, 20).Select(i => new[] { (byte)i }));

        Assert.All(seen, c => Assert.Equal(1, c));

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task GetEscId_MergedRepliesDoNotMakeTheCountJump()
    {
        // two ESCs powered up together: every reply twice with the same id, but every other pair merged on CAN
        var (fc, link, node, monitor) = await Start([4], setIdQuirk: false);

        var seen = await Replies(fc, monitor, Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? new[] { (byte)i, (byte)i } : [(byte)i]));

        Assert.All(seen, c => Assert.Equal(2, c));

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task GetEscId_UnpluggingOneOfTwo_DropsToOne()
    {
        var (fc, link, node, monitor) = await Start([4], setIdQuirk: false);

        var seen = await Replies(fc, monitor, Enumerable.Range(0, 25).Select(i => i < 5 ? new[] { (byte)i, (byte)i } : [(byte)i]));

        Assert.Equal(2, seen[4]);
        Assert.Equal(1, seen[^1]);

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task GetEscId_TwoEscsNotInStep_AreTwoEscs()
    {
        // one ESC was plugged in later, so its replies are numbered differently; each answers two pollers
        var (fc, link, node, monitor) = await Start([4], setIdQuirk: false);

        var seen = await Replies(fc, monitor, Enumerable.Range(0, 12).Select(i => new[] { (byte)i, (byte)((i + 17) & 0x1F) }));

        Assert.All(seen.Skip(1), c => Assert.Equal(2, c));

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }

    [Fact]
    public async Task GetEscId_FirmwareWithoutTransferIdCounter_IsOneEsc()
    {
        var (fc, link, node, monitor) = await Start([4], setIdQuirk: false);

        var seen = await Replies(fc, monitor, Enumerable.Range(0, 8).Select(_ => new byte[] { 0 }));

        Assert.All(seen, c => Assert.Equal(1, c));

        monitor.Dispose();
        node.Dispose();
        await link.DisposeAsync();
    }
}
