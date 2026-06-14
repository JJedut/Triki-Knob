using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrikiReader;

namespace Triki_Knob;

public partial class MainWindow : Window
{
    private TrikiBleReader? _bleReader;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _readerTask;
    private TrikiDeviceInfo _latestDeviceInfo = TrikiDeviceInfo.Empty;
    private readonly MotionVolumeController _motionVolumeController = new();
    private readonly IVisualOrientationMapper _orientationMapper = new ComplementaryTiltOrientationMapper();

    public MainWindow()
    {
        InitializeComponent();
        ApplyVolumeMapping();
        SetDisconnectedState();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

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

    private void VolumeDirection_Checked(object sender, RoutedEventArgs e)
    {
        ApplyVolumeMapping();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        base.OnClosed(e);
    }

    private void StartConnection()
    {
        _latestDeviceInfo = TrikiDeviceInfo.Empty;
        _motionVolumeController.Reset();
        ApplyVolumeMapping();
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
            SetConnectedState();
        });
    }

    private void BleReader_SampleReceived(object? sender, ImuSample sample)
    {
        var orientation = _orientationMapper.Update(sample);
        _motionVolumeController.Update(sample, orientation);
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
        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 196, 87));
        TxtStatus.Text = "Connecting...";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Disconnect";
        BtnConnect.IsEnabled = true;
    }

    private void SetConnectedState()
    {
        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(96, 211, 148));
        TxtStatus.Text = "Connected";
        BtnConnect.Content = "Disconnect";
        BtnConnect.IsEnabled = true;
    }

    private void SetDisconnectingState()
    {
        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 196, 87));
        TxtStatus.Text = "Disconnecting...";
        BtnConnect.Content = "Disconnecting...";
        BtnConnect.IsEnabled = false;
    }

    private void SetDisconnectedState()
    {
        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 100, 97));
        TxtStatus.Text = "Disconnected";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Connect";
        BtnConnect.IsEnabled = true;
    }

    private void SetNotFoundState()
    {
        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 100, 97));
        TxtStatus.Text = "Not found";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Connect";
        BtnConnect.IsEnabled = true;
    }

    private void SetErrorState()
    {
        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 100, 97));
        TxtStatus.Text = "Error";
        TxtBattery.Text = "--";
        BtnConnect.Content = "Connect";
        BtnConnect.IsEnabled = true;
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
            VolumeLeftDirection is null)
        {
            return;
        }

        var sensitivity = Math.Round(VolumeSensitivitySlider.Value);
        TxtVolumeSensitivity.Text = $"{sensitivity:0}%";
        _motionVolumeController.SensitivityPercent = sensitivity;
        _motionVolumeController.Direction = VolumeLeftDirection.IsChecked == true
            ? VolumeRotationDirection.Left
            : VolumeRotationDirection.Right;
    }
}
