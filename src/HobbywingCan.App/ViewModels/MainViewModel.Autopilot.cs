using System.Collections.ObjectModel;
using System.Windows.Input;
using HobbywingCan.Core.Autopilot;
using static HobbywingCan.App.Localization.Localizer;

namespace HobbywingCan.App.ViewModels;

/// <summary>Flight controller parameters in the ArduPilot popup: CAN port, DroneCAN driver, frame and motors.</summary>
public sealed partial class MainViewModel
{
    private AutopilotParams? _fcParams;
    private bool _fcBusy;
    // false → the parameters are read on the next timer tick while the popup is open
    private bool _fcReadDone;
    private bool _fcRebootNeeded;
    private string _fcStatus = "";
    private int _fcRowsBus;
    private int _fcRowsDriver;
    private TaskCompletionSource<byte>? _rebootAck;

    public ObservableCollection<FcParamRowViewModel> FcCanPortRows { get; } = new();
    public ObservableCollection<FcParamRowViewModel> FcDroneCanRows { get; } = new();
    public ObservableCollection<FcParamRowViewModel> FcFrameRows { get; } = new();

    private IEnumerable<FcParamRowViewModel> FcRows => FcCanPortRows.Concat(FcDroneCanRows).Concat(FcFrameRows);

    public ICommand ReadFcParamsCommand { get; private set; } = null!;
    public ICommand ApplyRecommendedFcCommand { get; private set; } = null!;
    public ICommand RebootFcCommand { get; private set; } = null!;

    public bool FcBusy
    {
        get => _fcBusy;
        private set
        {
            if (SetProperty(ref _fcBusy, value))
            {
                RefreshFcCommands();
            }
        }
    }

    /// <summary>A parameter that only takes effect after a reboot was written.</summary>
    public bool FcRebootNeeded { get => _fcRebootNeeded; private set => SetProperty(ref _fcRebootNeeded, value); }

    public string FcStatus { get => _fcStatus; private set => SetProperty(ref _fcStatus, value); }

    // BetaFlight X Reversed, as seen from above with the nose up
    public string MotorFrontLeft => MotorText(front: true, right: false);
    public string MotorFrontRight => MotorText(front: true, right: true);
    public string MotorRearLeft => MotorText(front: false, right: false);
    public string MotorRearRight => MotorText(front: false, right: true);

    private static string MotorText(bool front, bool right)
    {
        var m = ArduPilotParamSpecs.BetaFlightXReversedMotors.Single(m => m.Front == front && m.Right == right);
        return Tr("Fc_Motor", m.Number, 32 + m.Number, m.Clockwise ? "CW ↻" : "CCW ↺");
    }

    private void InitAutopilot()
    {
        ReadFcParamsCommand = new Command(() => _fcReadDone = false, () => IsConnected && !FcBusy);
        ApplyRecommendedFcCommand = new Command(async () => await ApplyRecommendedFcAsync(), () => CanWriteFc() && FcRows.Any(r => r.Current != null));
        RebootFcCommand = new Command(async () => await RebootFcAsync(), CanWriteFc);
        BuildFcRows(SelectedCanBus, 1);
        FcStatus = Tr("Fc_NotConnected");
    }

    private bool CanWriteFc() => IsConnected && !FcBusy;

    private void RefreshFcCommands()
    {
        foreach (var c in new[] { ReadFcParamsCommand, ApplyRecommendedFcCommand, RebootFcCommand })
        {
            ((Command?)c)?.ChangeCanExecute();
        }
        foreach (var row in FcRows)
        {
            row.RefreshCommand();
        }
    }

    /// <summary>Rows for CAN_P{bus}_* and CAN_D{driver}_*; values are read again afterwards.</summary>
    private void BuildFcRows(int bus, int driver)
    {
        _fcRowsBus = bus;
        _fcRowsDriver = driver;
        FcCanPortRows.Clear();
        FcDroneCanRows.Clear();
        FcFrameRows.Clear();
        foreach (var spec in ArduPilotParamSpecs.Build(bus, driver))
        {
            var row = new FcParamRowViewModel(spec, SetFcParamAsync, CanWriteFc);
            row.Clear();
            (spec.Group switch
            {
                ParamGroup.CanPort => FcCanPortRows,
                ParamGroup.DroneCan => FcDroneCanRows,
                _ => FcFrameRows,
            }).Add(row);
        }
    }

    private void OnFcConnected()
    {
        _fcParams = new AutopilotParams(_link!);
        _fcReadDone = false;
        FcRebootNeeded = false;
        BuildFcRows(SelectedCanBus, 1);
        FcStatus = Tr("Fc_WaitingHeartbeat");
        RefreshFcCommands();
    }

    private void OnFcDisconnected()
    {
        _fcParams?.Dispose();
        _fcParams = null;
        _rebootAck?.TrySetCanceled();
        foreach (var row in FcRows)
        {
            row.Clear();
        }
        FcStatus = Tr("Fc_NotConnected");
        RefreshFcCommands();
    }

    /// <summary>Called by the timer: reads the parameters once the popup is open and the vehicle is known.</summary>
    private void AutoReadFcParams()
    {
        if (ShowArduPilotHelp && !_fcReadDone && !FcBusy && _fcParams != null && _link?.Vehicle != null)
        {
            _fcReadDone = true;
            _ = ReadFcParamsAsync();
        }
    }

    private async Task ReadFcParamsAsync()
    {
        if (_fcParams is not { } fc)
        {
            return;
        }
        FcBusy = true;
        try
        {
            int bus = SelectedCanBus;
            // the driver decides which CAN_D{n}_* the DroneCAN settings are
            FcStatus = Tr("Fc_Reading", 1, "…");
            var driverParam = await fc.ReadAsync($"CAN_P{bus}_DRIVER");
            int driver = driverParam?.Value is >= 1 and <= 3 and var d ? (int)d : 1;
            if (bus != _fcRowsBus || driver != _fcRowsDriver)
            {
                BuildFcRows(bus, driver);
            }

            var rows = FcRows.ToList();
            bool protocolMissing = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (_fcParams != fc)
                {
                    // disconnected meanwhile
                    return;
                }
                var row = rows[i];
                FcStatus = Tr("Fc_Reading", i + 1, rows.Count);
                // CAN_D{n}_UC_* only exist once the driver runs DroneCAN: no need to wait for each to time out
                var param = row.Name == driverParam?.Name ? driverParam
                    : protocolMissing && row.Name.Contains("_UC_", StringComparison.Ordinal) ? null
                    : await fc.ReadAsync(row.Name);
                if (row.Name.EndsWith("_PROTOCOL", StringComparison.Ordinal))
                {
                    protocolMissing = param == null;
                }
                row.Loaded(param);
            }
            int missing = rows.Count(r => r.Missing);
            FcStatus = missing == 0 ? Tr("Fc_ReadDone", rows.Count) : Tr("Fc_ReadMissing", rows.Count - missing, missing);
            AddLog(FcStatus);
        }
        finally
        {
            FcBusy = false;
        }
    }

    private async Task SetFcParamAsync(FcParamRowViewModel row)
    {
        if (row.EditValue is not { } value || value < row.Spec.Min || value > row.Spec.Max)
        {
            AddLog(Tr("Log_FcValueRange", row.Name, ParamSpec.Format(row.Spec.Min), ParamSpec.Format(row.Spec.Max)));
            return;
        }
        await WriteFcAsync(Tr("Act_SetFcParam", row.Name, ParamSpec.Format(value)), [(row, value)]);
    }

    /// <summary>Every parameter the flight controller holds with another value than recommended.</summary>
    private async Task ApplyRecommendedFcAsync()
    {
        var changes = FcRows
            .Where(r => r.Current != null && r.Recommended is { } rec && rec != r.Current)
            .Select(r => (r, r.Recommended!.Value))
            .ToList();
        if (changes.Count == 0)
        {
            AddLog(Tr("Log_FcAllRecommended"));
            return;
        }
        await WriteFcAsync(Tr("Act_ApplyRecommendedFc"), changes);
    }

    /// <summary>Writes after one confirmation listing old → new and what the changes mean; stops at the first failure.</summary>
    private async Task WriteFcAsync(string action, IReadOnlyList<(FcParamRowViewModel Row, double Value)> writes)
    {
        if (_fcParams is not { } fc || !EnsureDisarmed())
        {
            return;
        }
        var changes = writes.Where(w => w.Row.Current != w.Value).ToList();
        if (changes.Count == 0)
        {
            AddLog(Tr("Log_FcNothing", action));
            return;
        }

        var lines = changes.Select(c => $"{c.Row.Name}: {c.Row.CurrentText} → {ParamSpec.Format(c.Value)}").ToList();
        var notes = new List<string>();
        if (changes.Any(c => c.Row.Name.EndsWith("_BITRATE", StringComparison.Ordinal)))
        {
            notes.Add(Tr("Warn_FcBitrate"));
        }
        if (changes.Any(c => c.Row.Name.EndsWith("_DRIVER", StringComparison.Ordinal) || c.Row.Name.EndsWith("_PROTOCOL", StringComparison.Ordinal)))
        {
            notes.Add(Tr("Warn_FcDriver"));
        }
        if (changes.Any(c => c.Row.Spec.Group == ParamGroup.Frame))
        {
            notes.Add(Tr("Warn_FcFrame"));
        }
        if (changes.Any(c => c.Row.Spec.RebootRequired))
        {
            notes.Add(Tr("Warn_FcReboot"));
        }
        string message = string.Join("\n", lines) + (notes.Count > 0 ? "\n\n" + string.Join("\n\n", notes.Select(n => $"⚠ {n}")) : "");
        if (Confirm != null && !await Confirm(Tr("Dlg_FcWriteTitle", action), message))
        {
            AddLog(Tr("Log_Cancelled", action));
            return;
        }

        FcBusy = true;
        try
        {
            for (int i = 0; i < changes.Count; i++)
            {
                var (row, value) = changes[i];
                string old = row.CurrentText;
                var result = await fc.SetAsync(row.Name, (float)value);
                switch (result.Status)
                {
                    case ParamWriteStatus.Ok:
                        row.Written(result.Param!);
                        AddLog(Tr("Log_FcSet", row.Name, old, row.CurrentText));
                        FcRebootNeeded |= row.Spec.RebootRequired;
                        continue;
                    case ParamWriteStatus.Mismatch:
                        row.Loaded(result.Param);
                        AddLog(Tr("Log_FcMismatch", row.Name, ParamSpec.Format(value), row.CurrentText));
                        break;
                    case ParamWriteStatus.NotFound:
                        row.Loaded(null);
                        AddLog(Tr("Log_FcNotFound", row.Name));
                        break;
                    default:
                        AddLog(Tr("Log_FcNoResponse", row.Name));
                        break;
                }
                if (i < changes.Count - 1)
                {
                    AddLog(Tr("Log_Stopped", action, changes.Count - i - 1));
                }
                break;
            }
        }
        finally
        {
            FcBusy = false;
        }
    }

    private async Task RebootFcAsync()
    {
        if (_link is not { } link || !EnsureDisarmed())
        {
            return;
        }
        if (Ask != null && !await Ask(Tr("Dlg_RebootTitle"), Tr("Dlg_Reboot"), Tr("Ui_RebootFc")))
        {
            return;
        }
        _rebootAck = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.RebootAutopilot();
        AddLog(Tr("Log_FcRebootSent"));
        byte result;
        try
        {
            result = await _rebootAck.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            // the USB port may vanish before the answer arrives; the link error then disconnects
            AddLog(Tr("Log_FcRebootNoAck"));
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _rebootAck = null;
        }
        if (result != 0)
        {
            AddLog(Tr("Log_FcRebootRejected", result));
            return;
        }
        FcRebootNeeded = false;
        // the flight controller's USB port goes away while it restarts
        await DisconnectAsync(Tr("Log_FcRebooting"));
    }

    private void OnRebootAck(byte result) => _rebootAck?.TrySetResult(result);

    private void RelocalizeFc()
    {
        foreach (var row in FcRows)
        {
            row.Relocalize();
        }
        OnPropertyChanged(nameof(MotorFrontLeft));
        OnPropertyChanged(nameof(MotorFrontRight));
        OnPropertyChanged(nameof(MotorRearLeft));
        OnPropertyChanged(nameof(MotorRearRight));
        if (!IsConnected)
        {
            FcStatus = Tr("Fc_NotConnected");
        }
    }
}
