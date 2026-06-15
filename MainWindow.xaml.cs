using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using TrikiReader;

namespace Triki_Knob;

public partial class MainWindow : Window
{
    private TrikiBleReader? _bleReader;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _readerTask;
    private TrikiDeviceInfo _latestDeviceInfo = TrikiDeviceInfo.Empty;
    private DateTimeOffset _lastSensorUiUpdateUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastActionLogUtc = DateTimeOffset.MinValue;
    private string? _lastActionLogMessage;
    private bool _isExitRequested;
    private bool _isLoadingSettings = true;
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Triki-Knob",
        "settings.json");
    private readonly ObservableCollection<ActivityLogEntry> _activityItems = new();
    private readonly MotionVolumeController _motionVolumeController = new();
    private readonly MotionScrollController _motionScrollController = new();
    private readonly IVisualOrientationMapper _orientationMapper = new ComplementaryTiltOrientationMapper();
    private readonly Drawing.Icon _connectedTrayIcon = LoadTrayIcon("256g.ico");
    private readonly Drawing.Icon _busyTrayIcon = LoadTrayIcon("256.ico");
    private readonly Drawing.Icon _disconnectedTrayIcon = LoadTrayIcon("256r.ico");
    private readonly Forms.NotifyIcon _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        ActivityLogGrid.ItemsSource = _activityItems;
        LoadSettings();
        _isLoadingSettings = false;
        ApplyVolumeMapping();
        ApplyScrollMapping();
        SetDisconnectedState();
        ResetLiveSensorData();
        AddActivity("system", "Ready");
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => HideToTray();

    private void Close_Click(object sender, RoutedEventArgs e)
        => HideToTray();

    private void BlConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_bleReader != null)
        {
            StopConnection();
            return;
        }

        StartConnection();
    }

    private void VolumeSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyVolumeMapping();
    }

    private void VolumeDeadZoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyVolumeMapping();
    }

    private void VolumeStepSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyVolumeMapping();
    }

    private void VolumeDirection_Checked(object sender, RoutedEventArgs e)
    {
        ApplyVolumeMapping();
    }

    private void ScrollSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyScrollMapping();
    }

    private void ScrollDeadZoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyScrollMapping();
    }

    private void ScrollSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyScrollMapping();
    }

    private void ScrollDirection_Checked(object sender, RoutedEventArgs e)
    {
        ApplyScrollMapping();
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        _orientationMapper.Reset();
        _motionVolumeController.Reset();
        _motionScrollController.Reset();
        TxtSensorPitch.Text = FormatDegrees(0.0);
        TxtSensorRoll.Text = FormatDegrees(0.0);
        TxtSensorYaw.Text = FormatDegrees(0.0);
        AddActivity("system", "Calibration applied");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _connectedTrayIcon.Dispose();
        _busyTrayIcon.Dispose();
        _disconnectedTrayIcon.Dispose();
        base.OnClosed(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void StartConnection()
    {
        AddActivity("system", "Connecting");
        _latestDeviceInfo = TrikiDeviceInfo.Empty;
        _motionVolumeController.Reset();
        _motionScrollController.Reset();
        ApplyVolumeMapping();
        ApplyScrollMapping();
        _orientationMapper.ResetForNewStream();
        _cancellationTokenSource = new CancellationTokenSource();

        _bleReader = new TrikiBleReader(AppOptions.Default);
        _bleReader.DeviceInfoReceived += BleReader_DeviceInfoReceived;
        _bleReader.SampleReceived += BleReader_SampleReceived;
        _bleReader.ConnectionLost += BleReader_ConnectionLost;
        _bleReader.LogMessage += BleReader_LogMessage;

        SetConnectingState();
        _readerTask = RunReaderAsync(_bleReader, _cancellationTokenSource);
    }

    private void StopConnection()
    {
        AddActivity("system", "Disconnect requested");
        SetDisconnectingState();
        _cancellationTokenSource?.Cancel();
    }

    private async Task RunReaderAsync(TrikiBleReader reader, CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await reader.RunAsync(cancellationTokenSource.Token);

            if (!cancellationTokenSource.IsCancellationRequested &&
                ReferenceEquals(_bleReader, reader) &&
                _latestDeviceInfo == TrikiDeviceInfo.Empty)
            {
                RunOnUiThread(SetNotFoundState);
            }
        }
        catch (OperationCanceledException)
        {
            RunOnUiThread(SetDisconnectedState);
        }
        catch
        {
            RunOnUiThread(SetErrorState);
        }
        finally
        {
            if (ReferenceEquals(_bleReader, reader))
            {
                reader.DeviceInfoReceived -= BleReader_DeviceInfoReceived;
                reader.SampleReceived -= BleReader_SampleReceived;
                reader.ConnectionLost -= BleReader_ConnectionLost;
                reader.LogMessage -= BleReader_LogMessage;

                _bleReader = null;
                _readerTask = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _motionVolumeController.Reset();
                _motionScrollController.Reset();
                _orientationMapper.Reset();
            }
        }
    }

    private void BleReader_DeviceInfoReceived(object? sender, TrikiDeviceInfo deviceInfo)
    {
        RunOnUiThread(() =>
        {
            _latestDeviceInfo = deviceInfo;
            TxtBattery.Text = deviceInfo.BatteryLevelPercent is null
                ? "--"
                : $"{deviceInfo.BatteryLevelPercent}%";
            AddActivity(
                "system",
                deviceInfo.BatteryLevelPercent is null ? "Device info received" : "Battery updated",
                deviceInfo.BatteryLevelPercent is null ? "--" : $"{deviceInfo.BatteryLevelPercent}%");
            SetConnectedState();
        });
    }

    private void BleReader_SampleReceived(object? sender, ImuSample sample)
    {
        var orientation = _orientationMapper.Update(sample);
        LogAction(_motionVolumeController.Update(sample, orientation));
        LogAction(_motionScrollController.Update(sample.TimestampUtc, orientation));
        UpdateLiveSensorData(sample, orientation);
    }

    private void BleReader_ConnectionLost(object? sender, EventArgs e)
    {
        RunOnUiThread(SetDisconnectedState);
    }

    private void BleReader_LogMessage(object? sender, string message)
    {
        if (message.Contains("Scanning", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Connecting", StringComparison.OrdinalIgnoreCase))
        {
            RunOnUiThread(SetConnectingState);
        }
        else if (message.Contains("Reading gyro data", StringComparison.OrdinalIgnoreCase))
        {
            RunOnUiThread(SetConnectedState);
        }
        else if (message.Contains("No matching BLE device found", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("Scan timed out", StringComparison.OrdinalIgnoreCase))
        {
            RunOnUiThread(SetNotFoundState);
        }
    }

    private void SetConnectingState()
    {
        SetTrayIcon(_busyTrayIcon, "Triki-Knob - connecting");
        StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 196, 87));
        TxtStatus.Text = "Connecting...";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Disconnect";
        BtnConnect.IsEnabled = true;
    }

    private void SetConnectedState()
    {
        SetTrayIcon(_connectedTrayIcon, "Triki-Knob - connected");
        StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 211, 148));
        TxtStatus.Text = "Connected";
        BtnConnect.Content = "Disconnect";
        BtnConnect.IsEnabled = true;
        AddActivity("system", "Connected");
    }

    private void SetDisconnectingState()
    {
        SetTrayIcon(_busyTrayIcon, "Triki-Knob - disconnecting");
        StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 196, 87));
        TxtStatus.Text = "Disconnecting...";
        BtnConnect.Content = "Disconnecting...";
        BtnConnect.IsEnabled = false;
    }

    private void SetDisconnectedState()
    {
        SetTrayIcon(_disconnectedTrayIcon, "Triki-Knob - disconnected");
        StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 100, 97));
        TxtStatus.Text = "Disconnected";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Connect";
        BtnConnect.IsEnabled = true;
        ResetLiveSensorData();
        AddActivity("system", "Disconnected");
    }

    private void SetNotFoundState()
    {
        SetTrayIcon(_disconnectedTrayIcon, "Triki-Knob - not found");
        StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 100, 97));
        TxtStatus.Text = "Not found";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Connect";
        BtnConnect.IsEnabled = true;
        ResetLiveSensorData();
        AddActivity("system", "Device not found");
    }

    private void SetErrorState()
    {
        SetTrayIcon(_disconnectedTrayIcon, "Triki-Knob - error");
        StatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 100, 97));
        TxtStatus.Text = "Error";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Connect";
        BtnConnect.IsEnabled = true;
        ResetLiveSensorData();
        AddActivity("system", "Connection error");
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.Invoke(action);
    }

    private void ApplyVolumeMapping()
    {
        if (TxtVolumeSensitivity is null ||
            VolumeSensitivitySlider is null ||
            TxtVolumeDeadZone is null ||
            VolumeDeadZoneSlider is null ||
            TxtVolumeStep is null ||
            VolumeStepSlider is null ||
            VolumeLeftDirection is null)
        {
            return;
        }

        var sensitivity = Math.Round(VolumeSensitivitySlider.Value);
        var deadZone = Math.Round(VolumeDeadZoneSlider.Value);
        var stepCount = (int)Math.Round(VolumeStepSlider.Value);
        TxtVolumeSensitivity.Text = $"{sensitivity:0}%";
        TxtVolumeDeadZone.Text = $"{deadZone:0} deg";
        TxtVolumeStep.Text = $"{stepCount}x";
        _motionVolumeController.SensitivityPercent = sensitivity;
        _motionVolumeController.DeadZoneDegrees = deadZone;
        _motionVolumeController.StepCount = stepCount;
        _motionVolumeController.Direction = VolumeLeftDirection.IsChecked == true
            ? VolumeRotationDirection.Left
            : VolumeRotationDirection.Right;
        SaveSettings();
    }

    private void ApplyScrollMapping()
    {
        if (TxtScrollSensitivity is null ||
            ScrollSensitivitySlider is null ||
            TxtScrollDeadZone is null ||
            ScrollDeadZoneSlider is null ||
            TxtScrollSpeed is null ||
            ScrollSpeedSlider is null ||
            ScrollInvertedDirection is null)
        {
            return;
        }

        var sensitivity = Math.Round(ScrollSensitivitySlider.Value);
        var deadZone = Math.Round(ScrollDeadZoneSlider.Value);
        var speed = Math.Round(ScrollSpeedSlider.Value);
        TxtScrollSensitivity.Text = $"{sensitivity:0}%";
        TxtScrollDeadZone.Text = $"{deadZone:0} deg";
        TxtScrollSpeed.Text = $"{speed:0}%";
        _motionScrollController.SensitivityPercent = sensitivity;
        _motionScrollController.DeadZoneDegrees = deadZone;
        _motionScrollController.SpeedPercent = speed;
        _motionScrollController.Direction = ScrollInvertedDirection.IsChecked == true
            ? ScrollDirection.Inverted
            : ScrollDirection.Normal;
        SaveSettings();
    }

    private void UpdateLiveSensorData(ImuSample sample, VisualOrientation orientation)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastSensorUiUpdateUtc).TotalMilliseconds < 50)
        {
            return;
        }

        _lastSensorUiUpdateUtc = now;
        RunOnUiThread(() =>
        {
            TxtSensorX.Text = FormatAxis(sample.AccelX);
            TxtSensorY.Text = FormatAxis(sample.AccelY);
            TxtSensorZ.Text = FormatAxis(sample.AccelZ);
            SensorBarX.Value = AxisBarValue(sample.AccelX);
            SensorBarY.Value = AxisBarValue(sample.AccelY);
            SensorBarZ.Value = AxisBarValue(sample.AccelZ);
            TxtSensorPitch.Text = FormatDegrees(orientation.Pitch);
            TxtSensorRoll.Text = FormatDegrees(orientation.Roll);
            TxtSensorYaw.Text = FormatDegrees(orientation.Yaw);
        });
    }

    private void ResetLiveSensorData()
    {
        TxtSensorX.Text = "--";
        TxtSensorY.Text = "--";
        TxtSensorZ.Text = "--";
        TxtSensorPitch.Text = "--";
        TxtSensorRoll.Text = "--";
        TxtSensorYaw.Text = "--";
        SensorBarX.Value = 0;
        SensorBarY.Value = 0;
        SensorBarZ.Value = 0;
        _lastSensorUiUpdateUtc = DateTimeOffset.MinValue;
    }

    private static string FormatAxis(double value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value:0.00}g");
    }

    private static double AxisBarValue(double value)
    {
        return Math.Clamp(Math.Abs(value), 0.0, 1.0) * 100.0;
    }

    private static string FormatDegrees(double value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value:0.0} deg");
    }

    private void LogAction(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (message == _lastActionLogMessage &&
            (now - _lastActionLogUtc).TotalMilliseconds < 300)
        {
            return;
        }

        _lastActionLogMessage = message;
        _lastActionLogUtc = now;
        RunOnUiThread(() => AddActionActivity(message));
    }

    private void AddActionActivity(string message)
    {
        if (message.StartsWith("Volume:", StringComparison.OrdinalIgnoreCase))
        {
            AddActivity("action", $"{NormalizeActionMessage(message)} triggered", VolumeStepValue(message));
        }
        else if (message.StartsWith("Scroll:", StringComparison.OrdinalIgnoreCase))
        {
            AddActivity("action", $"{NormalizeActionMessage(message)} triggered", "--");
        }
        else
        {
            AddActivity("action", message);
        }
    }

    private void AddActivity(string type, string message, string value = "--")
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new ActivityLogEntry(
            DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            type,
            message,
            value);
        _activityItems.Insert(0, entry);
        while (_activityItems.Count > 5)
        {
            _activityItems.RemoveAt(_activityItems.Count - 1);
        }
    }

    private string VolumeStepValue(string message)
    {
        var step = Math.Clamp(_motionVolumeController.StepCount, 1, 5);
        if (message.EndsWith("down", StringComparison.OrdinalIgnoreCase))
        {
            return $"-{step}x";
        }

        return $"+{step}x";
    }

    private static string NormalizeActionMessage(string message)
    {
        return message.Replace(":", "", StringComparison.Ordinal);
    }

    private void LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is null)
            {
                return;
            }

            VolumeLeftDirection.IsChecked = settings.VolumeDirection == VolumeRotationDirection.Left;
            VolumeRightDirection.IsChecked = settings.VolumeDirection != VolumeRotationDirection.Left;
            VolumeSensitivitySlider.Value = Clamp(settings.VolumeSensitivity, 10, 100);
            VolumeDeadZoneSlider.Value = Clamp(settings.VolumeDeadZone, 1, 20);
            VolumeStepSlider.Value = Clamp(settings.VolumeStep, 1, 5);

            ScrollNormalDirection.IsChecked = settings.ScrollDirection != ScrollDirection.Inverted;
            ScrollInvertedDirection.IsChecked = settings.ScrollDirection == ScrollDirection.Inverted;
            ScrollSensitivitySlider.Value = Clamp(settings.ScrollSensitivity, 10, 100);
            ScrollDeadZoneSlider.Value = Clamp(settings.ScrollDeadZone, 1, 25);
            ScrollSpeedSlider.Value = Clamp(settings.ScrollSpeed, 25, 200);
        }
        catch
        {
            AddActivity("system", "Settings load failed");
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings ||
            VolumeSensitivitySlider is null ||
            VolumeDeadZoneSlider is null ||
            VolumeStepSlider is null ||
            ScrollSensitivitySlider is null ||
            ScrollDeadZoneSlider is null ||
            ScrollSpeedSlider is null)
        {
            return;
        }

        try
        {
            var settings = new AppSettings(
                VolumeLeftDirection.IsChecked == true ? VolumeRotationDirection.Left : VolumeRotationDirection.Right,
                Math.Round(VolumeSensitivitySlider.Value),
                Math.Round(VolumeDeadZoneSlider.Value),
                (int)Math.Round(VolumeStepSlider.Value),
                ScrollInvertedDirection.IsChecked == true ? ScrollDirection.Inverted : ScrollDirection.Normal,
                Math.Round(ScrollSensitivitySlider.Value),
                Math.Round(ScrollDeadZoneSlider.Value),
                Math.Round(ScrollSpeedSlider.Value));

            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            AddActivity("system", "Settings save failed");
        }
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Clamp(double.IsFinite(value) ? value : minimum, minimum, maximum);
    }

    private sealed record ActivityLogEntry(
        string Time,
        string Type,
        string Event,
        string Value);

    private sealed record AppSettings(
        VolumeRotationDirection VolumeDirection,
        double VolumeSensitivity,
        double VolumeDeadZone,
        int VolumeStep,
        ScrollDirection ScrollDirection,
        double ScrollSensitivity,
        double ScrollDeadZone,
        double ScrollSpeed);

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var icon = new Forms.NotifyIcon
        {
            Icon = _disconnectedTrayIcon,
            Text = "Triki-Knob - disconnected",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return icon;
    }

    private void SetTrayIcon(Drawing.Icon icon, string text)
    {
        _trayIcon.Icon = icon;
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private static Drawing.Icon LoadTrayIcon(string fileName)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ico", fileName);
        if (File.Exists(iconPath))
        {
            return new Drawing.Icon(iconPath);
        }

        var resourceName = $"Assets.ico.{fileName}";
        var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        return resourceStream is null
            ? Drawing.SystemIcons.Application
            : new Drawing.Icon(resourceStream);
    }

    private void HideToTray()
    {
        Hide();
        AddActivity("system", "Hidden to tray");
        _trayIcon.ShowBalloonTip(
            1000,
            "Triki-Knob",
            "Still running in the background.",
            Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        AddActivity("system", "Window restored");
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        Close();
    }
}
