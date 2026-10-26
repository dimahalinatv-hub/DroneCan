using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Mavlink;

namespace HobbywingCan.Core.Link;

public sealed class MavlinkCanLinkOptions
{
    /// <summary>1-based CAN interface on the flight controller (CAN_P1 = 1, CAN_P2 = 2).</summary>
    public byte CanBus { get; init; } = 1;

    /// <summary>Ask ArduPilot to forward only the DroneCAN types the app decodes (saves link bandwidth).</summary>
    public bool UseForwardFilter { get; init; } = true;

    /// <summary>Rate requested for ESC_TELEMETRY_x_TO_y; 0 leaves the autopilot's stream rates untouched.</summary>
    public double EscTelemetryRateHz { get; init; } = 5;

    public byte GcsSystemId { get; init; } = 255;
    public byte GcsComponentId { get; init; } = 190;
}

/// <summary>
/// Talks MAVLink to ArduPilot over a byte stream and tunnels CAN frames with
/// MAV_CMD_CAN_FORWARD + CAN_FRAME (the same mechanism Mission Planner / DroneCAN GUI "mavcan" use).
/// </summary>
public sealed class MavlinkCanLink : ICanBus, IAsyncDisposable
{
    private const uint CanEffFlag = 0x80000000;
    private const uint CanRtrFlag = 0x40000000;
    private const uint CanErrFlag = 0x20000000;

    private readonly Stream _stream;
    private readonly MavlinkCanLinkOptions _options;
    private readonly MavlinkParser _parser = new();
    private readonly MavlinkEncoder _encoder;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readTask;
    private Task? _periodicTask;
    private bool _intervalsRequested;

    public MavlinkCanLink(Stream stream, MavlinkCanLinkOptions options)
    {
        _stream = stream;
        _options = options;
        _encoder = new MavlinkEncoder(options.GcsSystemId, options.GcsComponentId);
    }

    public event Action<CanFrame>? FrameReceived;
    public event Action<VehicleHeartbeat>? HeartbeatReceived;
    public event Action<AutopilotEscTelemetry[]>? EscTelemetryReceived;
    public event Action<string>? StatusTextReceived;
    public event Action<ushort, byte>? CommandAckReceived;
    public event Action<Exception>? Faulted;

    public VehicleHeartbeat? Vehicle { get; private set; }
    public DateTime LastHeartbeatUtc { get; private set; }
    public bool IsArmed => Vehicle?.IsArmed ?? false;

    public long CanFramesReceived { get; private set; }
    public long CanFramesSent { get; private set; }
    public long MavlinkPackets => _parser.PacketsParsed;
    public long MavlinkCrcErrors => _parser.CrcErrors;

    private byte TargetSystem => Vehicle?.SystemId ?? 1;
    private byte TargetComponent => Vehicle?.ComponentId ?? 1;

    public void Start()
    {
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        _periodicTask = Task.Run(() => PeriodicLoopAsync(_cts.Token));
    }

    public void Send(CanFrame frame)
    {
        uint id = frame.Extended ? frame.Id | CanEffFlag : frame.Id;
        byte bus = (byte)(_options.CanBus - 1);
        var payload = MavlinkMessages.EncodeCanFrame(TargetSystem, TargetComponent, bus, id, frame.Data);
        Write(MavlinkIds.CanFrame, payload);
        CanFramesSent++;
    }

    /// <summary>Synchronous so that frames of a multi-frame transfer leave in order.</summary>
    private void Write(uint messageId, byte[] payload)
    {
        try
        {
            lock (_writeLock)
            {
                _stream.Write(_encoder.Encode(messageId, payload));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
            if (!_cts.IsCancellationRequested)
            {
                Faulted?.Invoke(ex);
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    await Task.Delay(5, ct).ConfigureAwait(false);
                    continue;
                }
                foreach (var packet in _parser.Push(buffer.AsSpan(0, n)))
                {
                    Dispatch(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Faulted?.Invoke(ex);
            }
        }
    }

    private void Dispatch(MavlinkPacket packet)
    {
        switch (packet.MessageId)
        {
            case MavlinkIds.Heartbeat:
                var hb = MavlinkMessages.DecodeHeartbeat(packet);
                // only track the autopilot itself, not other GCSs / companion computers on the link
                if (hb.Autopilot != MavlinkIds.MavAutopilotInvalid && hb.Type != MavlinkIds.MavTypeGcs)
                {
                    bool first = Vehicle == null;
                    Vehicle = hb;
                    LastHeartbeatUtc = DateTime.UtcNow;
                    if (first)
                    {
                        // don't wait for the next periodic tick to start forwarding
                        SendForwardingSetup();
                    }
                    HeartbeatReceived?.Invoke(hb);
                }
                break;

            case MavlinkIds.CanFrame:
            case MavlinkIds.CanFdFrame:
                var (rawId, bus, data) = MavlinkMessages.DecodeCanFrame(packet);
                if (bus != _options.CanBus - 1 || (rawId & (CanRtrFlag | CanErrFlag)) != 0)
                {
                    break;
                }
                CanFramesReceived++;
                bool extended = (rawId & CanEffFlag) != 0;
                FrameReceived?.Invoke(new CanFrame(rawId & (extended ? 0x1FFFFFFFu : 0x7FFu), data, extended));
                break;

            case MavlinkIds.EscTelemetry1To4:
            case MavlinkIds.EscTelemetry5To8:
            case MavlinkIds.EscTelemetry9To12:
                EscTelemetryReceived?.Invoke(MavlinkMessages.DecodeEscTelemetry(packet));
                break;

            case MavlinkIds.StatusText:
                StatusTextReceived?.Invoke(MavlinkMessages.DecodeStatusText(packet).Text);
                break;

            case MavlinkIds.CommandAck:
                var (command, result) = MavlinkMessages.DecodeCommandAck(packet);
                CommandAckReceived?.Invoke(command, result);
                break;
        }
    }

    private async Task PeriodicLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Write(MavlinkIds.Heartbeat, MavlinkMessages.EncodeHeartbeat(MavlinkIds.MavTypeGcs, MavlinkIds.MavAutopilotInvalid));

                if (Vehicle != null)
                {
                    SendForwardingSetup();
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SendForwardingSetup()
    {
        // ArduPilot stops forwarding 5 s after the last MAV_CMD_CAN_FORWARD, so this is repeated every second
        Write(MavlinkIds.CommandLong, MavlinkMessages.EncodeCommandLong(
            TargetSystem, TargetComponent, MavlinkIds.MavCmdCanForward, _options.CanBus));
        SendFilter(_options.UseForwardFilter ? ForwardFilterIds : []);

        if (!_intervalsRequested && _options.EscTelemetryRateHz > 0)
        {
            _intervalsRequested = true;
            float intervalUs = (float)(1e6 / _options.EscTelemetryRateHz);
            foreach (var msg in new[] { MavlinkIds.EscTelemetry1To4, MavlinkIds.EscTelemetry5To8 })
            {
                Write(MavlinkIds.CommandLong, MavlinkMessages.EncodeCommandLong(
                    TargetSystem, TargetComponent, MavlinkIds.MavCmdSetMessageInterval, msg, intervalUs));
            }
        }
    }

    /// <summary>
    /// DroneCAN type ids passed to CAN_FILTER_MODIFY. ArduPilot matches message frames on the 16-bit
    /// message type id and service frames on the 8-bit service id.
    /// </summary>
    public static IReadOnlyList<ushort> ForwardFilterIds { get; } =
        DataTypes.All.Select(t => t.Id).Distinct().OrderBy(id => id).ToArray();

    /// <summary>One message carries at most 16 ids: the first replaces the filter, the rest are added to it.</summary>
    private void SendFilter(IReadOnlyList<ushort> ids)
    {
        int offset = 0;
        do
        {
            var chunk = ids.Skip(offset).Take(16).ToArray();
            Write(MavlinkIds.CanFilterModify, MavlinkMessages.EncodeCanFilterModify(TargetSystem, TargetComponent, _options.CanBus,
                offset == 0 ? MavlinkIds.CanFilterReplace : MavlinkIds.CanFilterAdd, chunk));
            offset += 16;
        }
        while (offset < ids.Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (Vehicle != null)
        {
            // stop forwarding and clear the filter so the autopilot is left as we found it
            SendFilter([]);
            Write(MavlinkIds.CommandLong, MavlinkMessages.EncodeCommandLong(
                TargetSystem, TargetComponent, MavlinkIds.MavCmdCanForward, 0));
        }
        _cts.Cancel();
        try
        {
            await Task.WhenAll(_readTask ?? Task.CompletedTask, _periodicTask ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        _cts.Dispose();
    }
}
