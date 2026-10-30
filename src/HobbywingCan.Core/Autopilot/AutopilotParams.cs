using System.Collections.Concurrent;
using HobbywingCan.Core.Link;
using HobbywingCan.Core.Mavlink;

namespace HobbywingCan.Core.Autopilot;

public enum ParamWriteStatus
{
    /// <summary>The flight controller echoed the requested value.</summary>
    Ok,
    /// <summary>The parameter does not exist on this flight controller (no answer to a read).</summary>
    NotFound,
    /// <summary>The parameter was read, but nothing came back to the write.</summary>
    NoResponse,
    /// <summary>The flight controller answered with another value (read-only or clamped).</summary>
    Mismatch,
}

/// <param name="Param">The value the flight controller holds now, when it answered.</param>
public sealed record ParamWriteResult(ParamWriteStatus Status, AutopilotParam? Param)
{
    public bool Success => Status == ParamWriteStatus.Ok;
}

/// <summary>
/// Reads and writes ArduPilot parameters by name with PARAM_REQUEST_READ / PARAM_SET over the MAVLink link.
/// ArduPilot does not answer a read of a name it does not have (e.g. CAN_D1_* before CAN_P1_DRIVER and a reboot),
/// so a missing parameter is a timeout. Callers are responsible for only writing while disarmed.
/// </summary>
public sealed class AutopilotParams : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private const int Attempts = 3;

    private readonly MavlinkCanLink _link;
    private readonly ConcurrentDictionary<string, AutopilotParam> _known = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AutopilotParam>> _waiting = new();

    public AutopilotParams(MavlinkCanLink link)
    {
        _link = link;
        _link.ParamValueReceived += OnParamValue;
    }

    /// <summary>Last value the flight controller reported for each parameter seen so far.</summary>
    public IReadOnlyDictionary<string, AutopilotParam> Known => _known;

    private void OnParamValue(AutopilotParam param)
    {
        _known[param.Name] = param;
        if (_waiting.TryRemove(param.Name, out var tcs))
        {
            tcs.TrySetResult(param);
        }
    }

    /// <returns>null when the flight controller has no such parameter (or does not answer).</returns>
    public Task<AutopilotParam?> ReadAsync(string name, CancellationToken ct = default) =>
        ExchangeAsync(name, () => _link.RequestParam(name), ct);

    public async Task<ParamWriteResult> SetAsync(string name, float value, CancellationToken ct = default)
    {
        // PARAM_SET carries the type; take it from the flight controller rather than guess
        if (!_known.TryGetValue(name, out var current))
        {
            current = await ReadAsync(name, ct).ConfigureAwait(false);
            if (current == null)
            {
                return new ParamWriteResult(ParamWriteStatus.NotFound, null);
            }
        }
        float sent = Round(value, current.Type);
        var echo = await ExchangeAsync(name, () => _link.SetParam(name, sent, current.Type), ct).ConfigureAwait(false);
        if (echo == null)
        {
            return new ParamWriteResult(ParamWriteStatus.NoResponse, null);
        }
        return new ParamWriteResult(echo.Value == sent ? ParamWriteStatus.Ok : ParamWriteStatus.Mismatch, echo);
    }

    /// <summary>Integer parameters only hold whole numbers; send what ArduPilot will store.</summary>
    private static float Round(float value, byte type) => type == MavlinkIds.MavParamTypeReal32 ? value : MathF.Round(value);

    private async Task<AutopilotParam?> ExchangeAsync(string name, Action send, CancellationToken ct)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var tcs = new TaskCompletionSource<AutopilotParam>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[name] = tcs;
            send();
            try
            {
                return await tcs.Task.WaitAsync(Timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                _waiting.TryRemove(new KeyValuePair<string, TaskCompletionSource<AutopilotParam>>(name, tcs));
            }
        }
        return null;
    }

    public void Dispose() => _link.ParamValueReceived -= OnParamValue;
}
