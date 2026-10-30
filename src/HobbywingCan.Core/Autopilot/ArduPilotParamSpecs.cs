using System.Globalization;

namespace HobbywingCan.Core.Autopilot;

public enum ParamGroup
{
    CanPort,
    DroneCan,
    Frame,
}

/// <summary>One value of an enumerated parameter, named as ArduPilot names it.</summary>
public sealed record ParamOption(double Value, string Name);

/// <summary>
/// A flight controller parameter the app edits, with the value recommended for a quadcopter
/// (BetaFlight X Reversed) with Hobbywing ESCs on DroneCAN. Names and option texts stay as ArduPilot spells them.
/// </summary>
/// <param name="HelpKey">resource key of the note shown next to the parameter</param>
public sealed record ParamSpec(string Name, ParamGroup Group, string HelpKey, double Min, double Max)
{
    /// <summary>Named values; the editor offers these (plus the current value when it is another one).</summary>
    public IReadOnlyList<ParamOption>? Options { get; init; }

    /// <summary>Recommended value; for a bitmask with <see cref="RecommendedBits"/> it is computed from the current one.</summary>
    public double? Recommended { get; init; }

    /// <summary>Bits that should be set; the other bits keep what the flight controller has.</summary>
    public uint RecommendedBits { get; init; }

    /// <summary>Names of the bits for a bitmask parameter (index = bit number).</summary>
    public IReadOnlyList<string>? Bits { get; init; }

    /// <summary>Bitmask of outputs (bit 0 = output 1): shown as output ranges.</summary>
    public bool OutputMask { get; init; }

    public bool RebootRequired { get; init; }

    public double? RecommendedFor(double? current) =>
        RecommendedBits != 0 ? (uint)(current ?? 0) | RecommendedBits : Recommended;

    /// <summary>
    /// What a value means: the option name, the bits that are set (output ranges "1–4, 6" for an output mask; "" when no bit
    /// is set), or null for a plain number.
    /// </summary>
    public string? Describe(double value)
    {
        if (Options != null)
        {
            return Options.FirstOrDefault(o => o.Value == value)?.Name;
        }
        uint mask = (uint)Math.Max(0, value);
        if (OutputMask)
        {
            return Ranges(SetBits(mask).Select(b => b + 1).ToList());
        }
        if (Bits != null)
        {
            return string.Join(", ", SetBits(mask).Select(b => b < Bits.Count ? Bits[b] : $"bit {b}"));
        }
        return null;
    }

    public static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static IEnumerable<int> SetBits(uint mask) => Enumerable.Range(0, 32).Where(b => (mask & (1u << b)) != 0);

    /// <summary>1,2,3,4,6 → "1–4, 6".</summary>
    private static string Ranges(IReadOnlyList<int> numbers)
    {
        var parts = new List<string>();
        for (int i = 0; i < numbers.Count;)
        {
            int j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1)
            {
                j++;
            }
            parts.Add(j == i ? $"{numbers[i]}" : $"{numbers[i]}–{numbers[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}

/// <summary>One motor of the frame: ArduPilot motor number (= SERVOn_FUNCTION 32 + n), where it sits and how it spins.</summary>
public sealed record MotorPosition(int Number, bool Front, bool Right, bool Clockwise);

/// <summary>The parameters the app edits (checked against ArduPilot master, AP_CANManager / AP_DroneCAN / ArduCopter).</summary>
public static class ArduPilotParamSpecs
{
    public const double QuadClass = 1;
    /// <summary>MOTOR_FRAME_TYPE_BF_X_REV.</summary>
    public const double BetaFlightXReversed = 18;
    public const uint HobbyWingEscBit = 1u << 7;
    public const int Motors = 4;

    /// <summary>Speeds a Hobbywing ESC can be set to (SetBaud), so the bus can match them.</summary>
    public static IReadOnlyList<ParamOption> Bitrates { get; } =
    [
        new(1000000, "1000000 (1 Mbit/s)"), new(500000, "500000"), new(250000, "250000"),
        new(200000, "200000"), new(100000, "100000"), new(50000, "50000"),
    ];

    /// <summary>
    /// BetaFlight X Reversed, from AP_MotorsMatrix (MOTOR_FRAME_TYPE_BF_X_REV): BetaFlight order with every motor
    /// spinning the other way ("props out").
    /// </summary>
    public static IReadOnlyList<MotorPosition> BetaFlightXReversedMotors { get; } =
    [
        new(1, Front: false, Right: true, Clockwise: false),
        new(2, Front: true, Right: true, Clockwise: true),
        new(3, Front: false, Right: false, Clockwise: true),
        new(4, Front: true, Right: false, Clockwise: false),
    ];

    private static readonly ParamOption[] DriverOptions =
        [new(0, "Disabled"), new(1, "First driver"), new(2, "Second driver"), new(3, "Third driver")];

    private static readonly ParamOption[] ProtocolOptions =
    [
        new(0, "Disabled"), new(1, "DroneCAN"), new(4, "PiccoloCAN"), new(6, "EFI_NWPMU"), new(7, "USD1"), new(8, "KDECAN"),
        new(10, "Scripting"), new(11, "Benewake"), new(12, "Scripting2"), new(13, "TOFSenseP"), new(14, "RadarCAN"),
    ];

    private static readonly string[] DroneCanOptionBits =
    [
        "ClearDNADatabase", "IgnoreDNANodeConflicts", "EnableCanfd", "IgnoreDNANodeUnhealthy", "SendServoAsPWM",
        "SendGNSS", "UseHimarkServo", "HobbyWingESC", "EnableStats", "EnableFlexDebug", "SecondaryAllowExtendedFrames",
    ];

    private static readonly ParamOption[] LogLevelOptions =
    [
        new(0, "Log None"), new(1, "Log Error"), new(2, "Log Warning and below"), new(3, "Log Info and below"),
        new(4, "Log Everything"),
    ];

    private static readonly ParamOption[] FrameClassOptions =
    [
        new(0, "Undefined"), new(1, "Quad"), new(2, "Hexa"), new(3, "Octa"), new(4, "OctaQuad"), new(5, "Y6"), new(6, "Heli"),
        new(7, "Tri"), new(8, "SingleCopter"), new(9, "CoaxCopter"), new(10, "BiCopter"), new(11, "Heli_Dual"),
        new(12, "DodecaHexa"), new(13, "HeliQuad"), new(14, "Deca"),
    ];

    private static readonly ParamOption[] FrameTypeOptions =
    [
        new(0, "Plus"), new(1, "X"), new(2, "V"), new(3, "H"), new(4, "V-Tail"), new(5, "A-Tail"), new(10, "Y6B"),
        new(11, "Y6F"), new(12, "BetaFlightX"), new(13, "DJIX"), new(14, "ClockwiseX"), new(15, "I"),
        new(18, "BetaFlightXReversed"), new(19, "Y4"),
    ];

    /// <summary>SERVOn_FUNCTION values that matter here; others are shown as plain numbers.</summary>
    private static readonly ParamOption[] ServoFunctionOptions =
        [new(0, "Disabled"), .. Enumerable.Range(1, 8).Select(m => new ParamOption(32 + m, $"Motor{m}"))];

    /// <param name="canBus">1-based CAN port the ESCs are on (CAN_P{bus}_*)</param>
    /// <param name="driver">driver that port uses (CAN_P{bus}_DRIVER), 1..3; the DroneCAN settings are CAN_D{driver}_*</param>
    public static IReadOnlyList<ParamSpec> Build(int canBus, int driver)
    {
        string p = $"CAN_P{canBus}_";
        string d = $"CAN_D{Math.Clamp(driver, 1, 3)}_";
        return
        [
            new(p + "DRIVER", ParamGroup.CanPort, "Help_Driver", 0, 3)
            {
                Options = DriverOptions, Recommended = Math.Clamp(driver, 1, 3), RebootRequired = true,
            },
            // no recommendation: it must be the speed the ESCs run at, which only the user knows
            new(p + "BITRATE", ParamGroup.CanPort, "Help_Bitrate", 10000, 1000000) { Options = Bitrates, RebootRequired = true },
            new(p + "OPTIONS", ParamGroup.CanPort, "Help_PortOptions", 0, 1) { Bits = ["LogAllFrames"] },

            new(d + "PROTOCOL", ParamGroup.DroneCan, "Help_Protocol", 0, 14)
            {
                Options = ProtocolOptions, Recommended = 1, RebootRequired = true,
            },
            new(d + "UC_NODE", ParamGroup.DroneCan, "Help_Node", 1, 125) { Recommended = 10, RebootRequired = true },
            new(d + "UC_ESC_BM", ParamGroup.DroneCan, "Help_EscBm", 0, uint.MaxValue)
            {
                Recommended = (1 << Motors) - 1, OutputMask = true,
            },
            new(d + "UC_OPTION", ParamGroup.DroneCan, "Help_Option", 0, 2047)
            {
                RecommendedBits = HobbyWingEscBit, Bits = DroneCanOptionBits, RebootRequired = true,
            },
            new(d + "UC_ESC_OF", ParamGroup.DroneCan, "Help_EscOf", 0, 18) { Recommended = 0 },
            new(d + "UC_ESC_RV", ParamGroup.DroneCan, "Help_EscRv", 0, uint.MaxValue) { Recommended = 0, OutputMask = true },
            new(d + "UC_SRV_BM", ParamGroup.DroneCan, "Help_SrvBm", 0, uint.MaxValue) { Recommended = 0, OutputMask = true },
            new(d + "UC_SRV_RT", ParamGroup.DroneCan, "Help_SrvRt", 1, 200),
            new(d + "UC_NTF_RT", ParamGroup.DroneCan, "Help_NtfRt", 1, 200),
            new(d + "UC_POOL", ParamGroup.DroneCan, "Help_Pool", 1024, 16384) { RebootRequired = true },
            new("CAN_LOGLEVEL", ParamGroup.DroneCan, "Help_LogLevel", 0, 4) { Options = LogLevelOptions },

            new("FRAME_CLASS", ParamGroup.Frame, "Help_FrameClass", 0, 17)
            {
                Options = FrameClassOptions, Recommended = QuadClass, RebootRequired = true,
            },
            new("FRAME_TYPE", ParamGroup.Frame, "Help_FrameType", 0, 19)
            {
                Options = FrameTypeOptions, Recommended = BetaFlightXReversed, RebootRequired = true,
            },
            .. Enumerable.Range(1, Motors).Select(m => new ParamSpec($"SERVO{m}_FUNCTION", ParamGroup.Frame, "Help_ServoFunction", -1, 200)
            {
                Options = ServoFunctionOptions, Recommended = 32 + m,
            }),
        ];
    }
}
