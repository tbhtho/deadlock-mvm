using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;
using DeadlockMVM.Launcher.ViewModels;

namespace DeadlockMVM.Launcher.Director;

/// <summary>Initial two-keyframe, replay-tick-driven linear Dolly editor.</summary>
public sealed class CampathViewModel : INotifyPropertyChanged
{
    private readonly ICameraService _camera;
    private readonly ReplayController _controller;
    private readonly NativeReplayCameraSession _native;
    private readonly ILogService _log;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private CampathKeyframe? _selectedKeyframe;
    private string _status = "Add two camera keyframes to create a linear path.";
    private bool _operationInFlight;

    public CampathViewModel(
        ICameraService camera,
        ReplayController controller,
        NativeReplayCameraSession native,
        ILogService log)
    {
        _camera = camera;
        _controller = controller;
        _native = native;
        _log = log;
        _dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);

        Keyframes = new ObservableCollection<CampathKeyframe>();
        AddCommand = new RelayCommand(() => _ = AddAsync(), CanAdd);
        UpdateCommand = new RelayCommand(() => _ = UpdateAsync(), () => SelectedKeyframe is not null && CanCapture());
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedKeyframe is not null && !_native.CampathPlaying);
        ClearCommand = new RelayCommand(Clear, () => Keyframes.Count > 0 && !_native.CampathPlaying);
        PlayCommand = new RelayCommand(() => _ = PlayAsync(), () => Keyframes.Count == 2 && _native.Available && !_native.CampathPlaying && !_operationInFlight);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => _native.CampathPlaying && !_operationInFlight);

        _native.StatusChanged += OnNativeStatusChanged;
        _controller.StateChanged += OnReplayStateChanged;
    }

    public ObservableCollection<CampathKeyframe> Keyframes { get; }

    public CampathKeyframe? SelectedKeyframe
    {
        get => _selectedKeyframe;
        set
        {
            if (!SetProperty(ref _selectedKeyframe, value))
                return;
            RaiseCommandStates();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string NativeState => $"NATIVE {_native.State.ToString().ToUpperInvariant()}";
    public string NativeMessage => _native.Message;
    public bool IsPlaying => _native.CampathPlaying;
    public string KeyframeCount => $"{Keyframes.Count} / 2 KEYFRAMES";

    public ICommand AddCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }

    private bool CanAdd() => CanCapture() && Keyframes.Count < 2;

    private bool CanCapture() =>
        !_operationInFlight && !_native.CampathPlaying && _controller.State.CurrentTick is not null;

    private async Task AddAsync()
    {
        if (Keyframes.Count >= 2)
        {
            Status = "The initial linear milestone supports exactly two keyframes.";
            return;
        }

        var keyframe = await CaptureAsync().ConfigureAwait(true);
        if (keyframe is null)
            return;
        var existing = Keyframes.FirstOrDefault(k => k.DemoTick == keyframe.DemoTick);
        if (existing is not null)
            Keyframes.Remove(existing);
        InsertSorted(keyframe);
        SelectedKeyframe = keyframe;
        Status = $"Added keyframe at demo tick {keyframe.DemoTick}.";
        OnCollectionChanged();
    }

    private async Task UpdateAsync()
    {
        var selected = SelectedKeyframe;
        if (selected is null)
            return;
        var updated = await CaptureAsync().ConfigureAwait(true);
        if (updated is null)
            return;

        Keyframes.Remove(selected);
        var collision = Keyframes.FirstOrDefault(k => k.DemoTick == updated.DemoTick);
        if (collision is not null)
            Keyframes.Remove(collision);
        InsertSorted(updated);
        SelectedKeyframe = updated;
        Status = $"Updated keyframe at demo tick {updated.DemoTick}.";
        OnCollectionChanged();
    }

    private async Task<CampathKeyframe?> CaptureAsync()
    {
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            Status = "Pausing replay for an exact keyframe sample…";
            _controller.Pause();
            var pauseDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_controller.State.IsPaused != true && DateTime.UtcNow < pauseDeadline)
                await Task.Delay(100).ConfigureAwait(true);
            if (_controller.State.IsPaused != true)
            {
                Status = "Replay did not confirm a paused state.";
                return null;
            }

            var state = await _camera.ReadStateAsync().ConfigureAwait(true);
            var tick = _controller.State.CurrentTick;
            if (tick is null || state?.ActiveTransform is not { } transform || state.ActiveFov is not { } fov)
            {
                Status = "Camera transform, active FOV, and demo tick must all be available.";
                return null;
            }

            var sample = new CameraSample(
                transform.X, transform.Y, transform.Z,
                transform.Pitch, transform.Yaw, transform.Roll, fov);
            if (!sample.IsValid)
            {
                Status = "The active camera sample is outside safe native bounds.";
                return null;
            }
            return new CampathKeyframe(tick.Value, sample);
        }
        catch (Exception ex)
        {
            _log.Warn($"Campath keyframe capture failed: {ex.Message}");
            Status = "Could not capture the current camera sample.";
            return null;
        }
        finally
        {
            _operationInFlight = false;
            RaiseCommandStates();
        }
    }

    private async Task PlayAsync()
    {
        if (Keyframes.Count != 2)
            return;
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            var ordered = Keyframes.OrderBy(k => k.DemoTick).ToArray();
            var path = new LinearCampath(ordered[0], ordered[1]);
            await _native.PlayLinearCampathAsync(path).ConfigureAwait(true);
            Status = $"Playing linear path from tick {path.From.DemoTick} to {path.To.DemoTick}.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Campath play failed: {ex.Message}");
            Status = ex.Message;
        }
        finally
        {
            _operationInFlight = false;
            RaiseNativeProperties();
        }
    }

    private async Task StopAsync()
    {
        _operationInFlight = true;
        RaiseCommandStates();
        try
        {
            await _native.StopCampathAsync().ConfigureAwait(true);
            Status = "Campath stopped; ordinary Free Roam restored.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Campath stop failed: {ex.Message}");
            Status = "Campath release failed; native heartbeat will fail closed.";
        }
        finally
        {
            _operationInFlight = false;
            RaiseNativeProperties();
        }
    }

    private void DeleteSelected()
    {
        if (SelectedKeyframe is not { } selected)
            return;
        Keyframes.Remove(selected);
        SelectedKeyframe = null;
        Status = "Keyframe deleted.";
        OnCollectionChanged();
    }

    private void Clear()
    {
        Keyframes.Clear();
        SelectedKeyframe = null;
        Status = "Campath cleared.";
        OnCollectionChanged();
    }

    private void InsertSorted(CampathKeyframe keyframe)
    {
        var index = 0;
        while (index < Keyframes.Count && Keyframes[index].DemoTick < keyframe.DemoTick)
            index++;
        Keyframes.Insert(index, keyframe);
    }

    private void OnNativeStatusChanged(object? sender, EventArgs e) => SetOnUi(RaiseNativeProperties);

    private void OnReplayStateChanged(object? sender, ReplayState state) => SetOnUi(RaiseCommandStates);

    private void RaiseNativeProperties()
    {
        OnPropertyChanged(nameof(NativeState));
        OnPropertyChanged(nameof(NativeMessage));
        OnPropertyChanged(nameof(IsPlaying));
        RaiseCommandStates();
    }

    private void OnCollectionChanged()
    {
        OnPropertyChanged(nameof(KeyframeCount));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (AddCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PlayCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void SetOnUi(Action action)
    {
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
