using System.Globalization;
using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Mavlink;
using HobbywingCan.Core.Messages;
using HobbywingCan.Core.Monitoring;

namespace HobbywingCan.App.ViewModels;

/// <summary>One line of the live DroneCAN ESC table.</summary>
public sealed class EscRowViewModel(byte nodeId) : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private long _lastMsg1Count, _lastMsg2Count, _lastMsg3Count, _lastEscCount;
    private DateTime _lastRateUpdate = DateTime.MinValue;

    private string _name = "";
    private string _throttleChannel = "–";
    private string _rpm = "–";
    private string _pwm = "–";
    private string _status = "–";
    private string _direction = "–";
    private string _voltage = "–";
    private string _current = "–";
    private string _temperature = "–";
    private string _extraTemperatures = "–";
    private string _health = "–";
    private EscCountBadge _escCount = EscCountBadge.Unknown;
    private string _rates = "";
    private string _msg1Set = "–";
    private string _msg2Set = "–";
    private string _msg3Set = "–";
    private string _age = "";
    private bool _isStale;

    public byte NodeId { get; } = nodeId;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string ThrottleChannel { get => _throttleChannel; private set => SetProperty(ref _throttleChannel, value); }
    public string Rpm { get => _rpm; private set => SetProperty(ref _rpm, value); }
    public string Pwm { get => _pwm; private set => SetProperty(ref _pwm, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Direction { get => _direction; private set => SetProperty(ref _direction, value); }
    public string Voltage { get => _voltage; private set => SetProperty(ref _voltage, value); }
    public string Current { get => _current; private set => SetProperty(ref _current, value); }
    public string Temperature { get => _temperature; private set => SetProperty(ref _temperature, value); }
    public string ExtraTemperatures { get => _extraTemperatures; private set => SetProperty(ref _extraTemperatures, value); }
    public string Health { get => _health; private set => SetProperty(ref _health, value); }
    public EscCountBadge EscCount { get => _escCount; private set => SetProperty(ref _escCount, value); }
    public string Rates { get => _rates; private set => SetProperty(ref _rates, value); }
    public string Msg1Set { get => _msg1Set; private set => SetProperty(ref _msg1Set, value); }
    public string Msg2Set { get => _msg2Set; private set => SetProperty(ref _msg2Set, value); }
    public string Msg3Set { get => _msg3Set; private set => SetProperty(ref _msg3Set, value); }
    public string Age { get => _age; private set => SetProperty(ref _age, value); }
    public bool IsStale { get => _isStale; private set => SetProperty(ref _isStale, value); }

    public void Update(NodeState s, DateTime nowUtc)
    {
        Name = s.Info?.Name ?? (s.IsHobbywing ? "Hobbywing ESC" : "");
        ThrottleChannel = s.ThrottleChannel?.ToString(Inv) ?? s.EscStatus?.EscIndex.ToString(Inv) ?? "–";
        Health = s.Status is { } st ? $"{st.HealthText} / {st.ModeText}" : "–";
        if (s.IgnoresDestination)
        {
            Health = "⚠ answers for others";
        }
        EscCount = EscCountBadge.For(s);
        Direction = s.CounterClockwise switch
        {
            true => "CCW",
            false => "CW",
            null => "–",
        };
        Msg1Set = ConfiguredText(s, DataTypes.HwStatusMsg1.Id);
        Msg2Set = ConfiguredText(s, DataTypes.HwStatusMsg2.Id);
        Msg3Set = ConfiguredText(s, DataTypes.HwStatusMsg3.Id);

        double? volts = null, amps = null;
        if (s.Msg1 is { } m1)
        {
            Rpm = m1.Rpm.ToString(Inv);
            Pwm = m1.Pwm.ToString(Inv);
            Status = $"0x{m1.Status:X4}";
        }
        else if (s.EscStatus is { } es)
        {
            Rpm = es.Rpm.ToString(Inv);
            Status = $"err {es.ErrorCount}, {es.PowerRatingPct}%";
        }

        if (s.Msg2 is { } m2)
        {
            volts = m2.Voltage;
            amps = m2.Current;
            Temperature = $"{m2.Temperature} °C";
        }
        else if (s.EscStatus is { } es)
        {
            volts = es.Voltage;
            amps = es.Current;
            Temperature = $"{es.TemperatureC:F0} °C";
        }

        if (volts is { } v && amps is { } a)
        {
            Voltage = v.ToString("F1", Inv) + " V";
            Current = a.ToString("F1", Inv) + " A";
        }

        if (s.Msg3 is { } m3)
        {
            ExtraTemperatures = $"{m3.MosTemperature} / {m3.CapacitorTemperature} / {m3.MotorTemperature} °C";
        }
        else if (s.EscStatusExtended is { } ext)
        {
            ExtraTemperatures = $"motor {ext.MotorTemperatureC} °C, in {ext.InputPct}% out {ext.OutputPct}%";
        }

        double age = (nowUtc - s.LastSeenUtc).TotalSeconds;
        IsStale = age > 2;
        Age = age < 1 ? "live" : $"{age:F0} s ago";

        double dt = (nowUtc - _lastRateUpdate).TotalSeconds;
        if (dt >= 1)
        {
            if (_lastRateUpdate != DateTime.MinValue)
            {
                var parts = new List<string>();
                AddRate(parts, "M1", s.Msg1Count - _lastMsg1Count, dt);
                AddRate(parts, "M2", s.Msg2Count - _lastMsg2Count, dt);
                AddRate(parts, "M3", s.Msg3Count - _lastMsg3Count, dt);
                AddRate(parts, "esc.Status", s.EscStatusCount - _lastEscCount, dt);
                Rates = string.Join("  ", parts);
            }
            _lastMsg1Count = s.Msg1Count;
            _lastMsg2Count = s.Msg2Count;
            _lastMsg3Count = s.Msg3Count;
            _lastEscCount = s.EscStatusCount;
            _lastRateUpdate = nowUtc;
        }
    }

    private static void AddRate(List<string> parts, string name, long delta, double seconds)
    {
        if (delta > 0)
        {
            parts.Add($"{name} {delta / seconds:F0}");
        }
    }

    /// <summary>Reporting rate configured in the ESC for one status message, e.g. "50 Hz".</summary>
    private static string ConfiguredText(NodeState s, ushort messageId) =>
        s.ReportingRateCodes.TryGetValue(messageId, out var code)
            ? HwRate.ToHz(code) switch
            {
                null => $"code {code}",
                0 => "off",
                var hz => $"{hz} Hz",
            }
            : "–";
}

/// <summary>How many ESCs answer as one node id: "✓ 1 ESC" in green, "⚠ N ESC" in yellow.</summary>
public sealed record EscCountBadge(string Icon, string Text, Color Color)
{
    private static readonly Color Ok = Color.FromArgb("#4CAF50");
    private static readonly Color Warning = Color.FromArgb("#FFC107");

    /// <summary>Not counted yet (GetEscID is only polled while disarmed).</summary>
    public static readonly EscCountBadge Unknown = new("", "", Colors.Transparent);

    public static EscCountBadge For(NodeState? s) => s switch
    {
        // the count follows the bus as ESCs are unplugged, so it wins over the sticky suspicion
        { DeviceCount: 1 } => new("✓", "1 ESC", Ok),
        { DeviceCount: { } n } => new("⚠", $"{n} ESC", Warning),
        { DuplicateSuspected: true } => new("⚠", "2+ ESC", Warning),
        _ => Unknown,
    };
}

/// <summary>One line of ArduPilot's own ESC telemetry (ESC_TELEMETRY_x_TO_y).</summary>
public sealed class AutopilotEscRowViewModel(int index) : ObservableObject
{
    private string _rpm = "", _voltage = "", _current = "", _temperature = "", _consumed = "", _count = "";

    public int Index { get; } = index;
    public string Title => $"ESC {Index + 1}";
    public string Rpm { get => _rpm; private set => SetProperty(ref _rpm, value); }
    public string Voltage { get => _voltage; private set => SetProperty(ref _voltage, value); }
    public string Current { get => _current; private set => SetProperty(ref _current, value); }
    public string Temperature { get => _temperature; private set => SetProperty(ref _temperature, value); }
    public string Consumed { get => _consumed; private set => SetProperty(ref _consumed, value); }
    public string Count { get => _count; private set => SetProperty(ref _count, value); }

    public void Update(AutopilotEscTelemetry t)
    {
        var inv = CultureInfo.InvariantCulture;
        Rpm = t.Rpm.ToString(inv);
        Voltage = t.Voltage.ToString("F2", inv) + " V";
        Current = t.Current.ToString("F2", inv) + " A";
        Temperature = $"{t.TemperatureC} °C";
        Consumed = $"{t.ConsumedMah} mAh";
        Count = t.Count.ToString(inv);
    }
}

public sealed record NodeChoice(byte NodeId, string Display)
{
    // Picker shows ToString()
    public override string ToString() => Display;
}

/// <summary>One line of the bus traffic table: what a node sends and how often.</summary>
public sealed class TrafficRowViewModel(byte nodeId, TransferKind kind, ushort dataTypeId, string typeName) : ObservableObject
{
    private long _lastCount;
    private DateTime _lastRateUpdate = DateTime.MinValue;
    private string _rate = "";
    private string _count = "";
    private string _age = "";

    public byte NodeId { get; } = nodeId;
    public TransferKind Kind { get; } = kind;
    public ushort DataTypeId { get; } = dataTypeId;
    public string KindText { get; } = kind switch
    {
        TransferKind.Broadcast => "msg",
        TransferKind.Request => "req",
        _ => "resp",
    };
    public string TypeText { get; } = $"{ShortName(typeName)} ({dataTypeId})";

    private static string ShortName(string name) =>
        name.StartsWith("uavcan.", StringComparison.Ordinal) ? name["uavcan.".Length..]
        : name.StartsWith("com.", StringComparison.Ordinal) ? name["com.".Length..]
        : name;
    public string Rate { get => _rate; private set => SetProperty(ref _rate, value); }
    public string Count { get => _count; private set => SetProperty(ref _count, value); }
    public string Age { get => _age; private set => SetProperty(ref _age, value); }

    public void Update(TrafficEntry e, DateTime nowUtc)
    {
        Count = e.Count.ToString(CultureInfo.InvariantCulture);
        double age = (nowUtc - e.LastUtc).TotalSeconds;
        Age = age < 1 ? "live" : $"{age:F0} s";
        double dt = (nowUtc - _lastRateUpdate).TotalSeconds;
        if (dt >= 1)
        {
            if (_lastRateUpdate != DateTime.MinValue)
            {
                Rate = $"{(e.Count - _lastCount) / dt:F1} Hz";
            }
            _lastCount = e.Count;
            _lastRateUpdate = nowUtc;
        }
    }
}
