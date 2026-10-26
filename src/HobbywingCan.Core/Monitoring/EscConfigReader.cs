using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Messages;

namespace HobbywingCan.Core.Monitoring;

public sealed record ConfigItem(string Group, string Name, string Value);

public sealed class EscConfigReport
{
    public required byte NodeId { get; init; }
    public NodeInfo? Info { get; set; }
    public HwMajorConfig? MajorConfig { get; set; }
    public HwMaintenanceInfo? Maintenance { get; set; }
    public byte? Direction { get; set; }
    public Dictionary<ushort, HwReportingFrequency> ReportingRates { get; } = new();

    /// <summary>Last throttle source the ESC confirmed to a write (shown when GetMajorConfig is not answered).</summary>
    public byte? LastSetThrottleSource { get; set; }

    /// <summary>Last LED setting the ESC confirmed to a write (shown when GetMajorConfig is not answered).</summary>
    public HwSetLed? LastSetLed { get; set; }
    public List<ParamEntry> Parameters { get; } = new();

    /// <summary>Raw response payloads (hex) so they can be checked against Hobbywing docs.</summary>
    public Dictionary<string, string> RawResponses { get; } = new();

    public List<string> Errors { get; } = new();

    public IReadOnlyList<ConfigItem> ToItems(bool includeErrors = true)
    {
        var items = new List<ConfigItem>();
        if (Info != null)
        {
            items.Add(new("Node", "Name", Info.Name));
            items.Add(new("Node", "Software", Info.SoftwareVersion));
            items.Add(new("Node", "Hardware", Info.HardwareVersion));
            items.Add(new("Node", "Unique ID", Convert.ToHexString(Info.UniqueId)));
            items.Add(new("Node", "Health / mode", $"{Info.Status.HealthText} / {Info.Status.ModeText}"));
            items.Add(new("Node", "Uptime", TimeSpan.FromSeconds(Info.Status.UptimeSec).ToString()));
            items.Add(new("Node", "Vendor status", $"0x{Info.Status.VendorSpecificStatusCode:X4}"));
        }
        if (MajorConfig is { } c)
        {
            items.Add(new("Major config", "Direction", c.DirectionText));
            items.Add(new("Major config", "Throttle source", c.ThrottleSourceText));
            items.Add(new("Major config", "Throttle channel", c.ThrottleChannel.ToString()));
            items.Add(new("Major config", "LED color", c.LedColorText));
            items.Add(new("Major config", "LED mode", c.LedModeText));
            items.Add(new("Major config", "StatusMsg1 rate", HwRate.ToText(c.Msg1RateCode)));
            items.Add(new("Major config", "StatusMsg2 rate", HwRate.ToText(c.Msg2RateCode)));
            items.Add(new("Major config", "Positioning angle", c.PositioningAngle.ToString()));
            items.Add(new("Major config", "Reserved", Convert.ToHexString(c.Reserved)));
        }
        else
        {
            // only GetMajorConfig reports these; a write answer is the next best thing, otherwise they are left out
            if (LastSetThrottleSource is { } s)
            {
                items.Add(new("Major config", "Throttle source", $"{HwSetThrottleSource.ToText(s)} (last set)"));
            }
            if (LastSetLed is { } led)
            {
                items.Add(new("Major config", "LED color", $"{HwSetLed.ColorName(led.Color)} (last set)"));
                items.Add(new("Major config", "LED mode", $"{HwSetLed.BlinkName(led.Blink)} (last set)"));
            }
        }
        if (Direction is { } d)
        {
            items.Add(new("Direction query", "Direction", HwSetDirection.ToText(d)));
        }
        foreach (var (msgId, rate) in ReportingRates.OrderBy(kv => kv.Key))
        {
            items.Add(new("Reporting rate", $"StatusMsg{msgId - 20049} ({msgId})", HwRate.ToText(rate.RateCode)));
        }
        if (Maintenance is { } m)
        {
            items.Add(new("Maintenance", "Total rotation time", FormatMinutes(m.TotalRotationTimeMin)));
            items.Add(new("Maintenance", "Since maintenance", FormatMinutes(m.TimeSinceMaintenanceMin)));
        }
        foreach (var p in Parameters)
        {
            var range = p.MinValue.Kind != ParamValueKind.Empty || p.MaxValue.Kind != ParamValueKind.Empty
                ? $"  [{p.MinValue} … {p.MaxValue}]"
                : "";
            var def = p.DefaultValue.Kind != ParamValueKind.Empty ? $"  (default {p.DefaultValue})" : "";
            items.Add(new("Parameters", $"#{p.Index} {p.Name}", $"{p.Value}{def}{range}"));
        }
        foreach (var (name, hex) in RawResponses)
        {
            items.Add(new("Raw", name, hex));
        }
        if (includeErrors)
        {
            foreach (var e in Errors)
            {
                items.Add(new("Errors", "", e));
            }
        }
        return items;
    }

    private static string FormatMinutes(uint minutes) => $"{minutes} min ({minutes / 60.0:F1} h)";
}

/// <summary>Reads everything readable from a Hobbywing DroneCAN ESC without changing its settings.</summary>
public sealed class EscConfigReader(DroneCanNode node)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(400);
    private const int Attempts = 3;
    private const int MaxParams = 256;

    public async Task<EscConfigReport> ReadAsync(byte nodeId, bool includeParameters, IProgress<string>? progress,
        CancellationToken ct = default)
    {
        var report = new EscConfigReport { NodeId = nodeId };

        progress?.Report("GetNodeInfo…");
        var info = await Call(report, "GetNodeInfo", DataTypes.GetNodeInfo, nodeId, [], ct);
        if (info != null)
        {
            report.Info = Decode(report, "GetNodeInfo", () => NodeInfo.Decode(info));
        }

        progress?.Report("GetMajorConfig…");
        var major = await Call(report, "GetMajorConfig", DataTypes.HwGetMajorConfig, nodeId, HwMajorConfig.EncodeRequest(), ct);
        if (major != null)
        {
            report.MajorConfig = Decode(report, "GetMajorConfig", () => HwMajorConfig.Decode(major));
        }

        progress?.Report("SetDirection (query)…");
        var dir = await Call(report, "SetDirection(query)", DataTypes.HwSetDirection, nodeId, HwSetDirection.EncodeQuery(), ct);
        if (dir != null)
        {
            report.Direction = HwSetDirection.DecodeResponse(dir);
        }

        foreach (var msgId in new ushort[] { DataTypes.HwStatusMsg1.Id, DataTypes.HwStatusMsg2.Id, DataTypes.HwStatusMsg3.Id })
        {
            progress?.Report($"Reporting rate {msgId}…");
            var rate = await Call(report, $"ReportingFrequency({msgId})", DataTypes.HwSetReportingFrequency, nodeId,
                HwReportingFrequency.EncodeReadRequest(msgId), ct);
            if (rate != null && Decode(report, $"ReportingFrequency({msgId})", () => HwReportingFrequency.Decode(rate)) is { } r)
            {
                report.ReportingRates[msgId] = r;
            }
        }

        progress?.Report("GetMaintenanceInformation…");
        var maint = await Call(report, "GetMaintenanceInformation", DataTypes.HwGetMaintenanceInformation, nodeId,
            HwMaintenanceInfo.EncodeRequest(clear: false), ct);
        if (maint != null)
        {
            report.Maintenance = Decode(report, "GetMaintenanceInformation", () => HwMaintenanceInfo.Decode(maint));
        }

        if (includeParameters)
        {
            for (ushort index = 0; index < MaxParams; index++)
            {
                progress?.Report($"Parameter #{index}…");
                var response = await node.RequestWithRetryAsync(DataTypes.ParamGetSet, nodeId,
                    ParamEntry.EncodeGetByIndex(index), Timeout, Attempts, ct);
                if (response == null)
                {
                    if (index == 0)
                    {
                        report.Errors.Add("param.GetSet: no response (ESC may not expose standard parameters)");
                    }
                    else
                    {
                        report.Errors.Add($"param.GetSet: timeout at index {index}");
                    }
                    break;
                }
                var entry = Decode(report, $"param #{index}", () => ParamEntry.DecodeResponse(index, response.Payload));
                if (entry == null)
                {
                    break;
                }
                report.Parameters.Add(entry);
            }
        }

        progress?.Report("Done");
        return report;
    }

    private async Task<byte[]?> Call(EscConfigReport report, string name, DataTypeInfo service, byte nodeId, byte[] request,
        CancellationToken ct)
    {
        var before = node.GetResponseStats(service.Id, nodeId);
        var response = await node.RequestWithRetryAsync(service, nodeId, request, Timeout, Attempts, ct);
        if (response == null)
        {
            var seen = node.GetResponseStats(service.Id, nodeId).Since(before);
            if (seen.LastCrcMismatch is { } unverified)
            {
                // a firmware that seeds the CRC with another signature still sends a usable payload
                report.Errors.Add($"{name}: answer failed the CRC check ({seen.CrcErrors}x); its values are shown but unverified");
                report.RawResponses[$"{name} (CRC mismatch)"] = Convert.ToHexString(unverified);
                return unverified;
            }
            report.Errors.Add(seen switch
            {
                { Incomplete: > 0 } => $"{name}: no complete response ({seen.Incomplete} answer(s) lost frames on the link)",
                { Frames: > 0 } => $"{name}: {seen.Frames} answer frame(s) arrived but never formed a complete answer",
                _ => $"{name}: no response (node {nodeId} sent nothing back)",
            });
            return null;
        }
        report.RawResponses[name] = Convert.ToHexString(response.Payload);
        return response.Payload;
    }

    private static T? Decode<T>(EscConfigReport report, string name, Func<T?> decode) where T : class
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            report.Errors.Add($"{name}: cannot decode ({ex.Message})");
            return null;
        }
    }
}
