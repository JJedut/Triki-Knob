using System.Buffers.Binary;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace Triki_Knob
{
    public sealed class TrikiBleReader
    {
        private static readonly Guid NusServiceUuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
        private static readonly Guid NusRxCharacteristicUuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
        private static readonly Guid NusTxCharacteristicUuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
        private static readonly Guid GenericAccessServiceUuid = BluetoothUuid(0x1800);
        private static readonly Guid DeviceNameCharacteristicUuid = BluetoothUuid(0x2A00);
        private static readonly Guid BatteryServiceUuid = BluetoothUuid(0x180F);
        private static readonly Guid BatteryLevelCharacteristicUuid = BluetoothUuid(0x2A19);
        private static readonly Guid DeviceInformationServiceUuid = BluetoothUuid(0x180A);
        private static readonly Guid ManufacturerNameCharacteristicUuid = BluetoothUuid(0x2A29);
        private static readonly Guid ModelNumberCharacteristicUuid = BluetoothUuid(0x2A24);
        private static readonly Guid SerialNumberCharacteristicUuid = BluetoothUuid(0x2A25);
        private static readonly Guid FirmwareRevisionCharacteristicUuid = BluetoothUuid(0x2A26);
        private static readonly Guid HardwareRevisionCharacteristicUuid = BluetoothUuid(0x2A27);
        private static readonly Guid SoftwareRevisionCharacteristicUuid = BluetoothUuid(0x2A28);
        private static readonly Guid SystemIdCharacteristicUuid = BluetoothUuid(0x2A23);
        private static readonly Guid PnpIdCharacteristicUuid = BluetoothUuid(0x2A50);

        private readonly AppOptions _options;
        private readonly ImuStats _stats = new();
        private BluetoothLEDevice? _connectedDevice;
        private GattDeviceService? _nusService;
        private GattCharacteristic? _txCharacteristic;
        private GattCharacteristic? _rxCharacteristic;

        public event EventHandler<ImuSample>? SampleReceived;
        public event EventHandler<TrikiDeviceInfo>? DeviceInfoReceived;
        public event EventHandler<string>? LogMessage;
        public event EventHandler? ConnectionLost;

        public ImuStats Stats => _stats;

        public TrikiBleReader(AppOptions options)
        {
            _options = options;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var device = await FindDeviceAsync(cancellationToken);
            if (device is null)
            {
                await LogAsync("No matching BLE device found. Press the button on Triki to wake it, then run again.");
                return;
            }

            await LogAsync($"Connecting to {device.Name} ({device.BluetoothAddress:X})...");
            _connectedDevice = device;

            Windows.Foundation.TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? valueChangedHandler = null;
            Channel<BleNotification>? notificationChannel = null;
            Task? notificationProcessingTask = null;

            try
            {
                await LogGattDatabaseAsync(device, cancellationToken);
                var deviceInfo = await ReadDeviceInfoAsync(device, cancellationToken);
                DeviceInfoReceived?.Invoke(this, deviceInfo);
                await LogAsync("Device info:\n" + deviceInfo.ToLogText());

                var services = await device.GetGattServicesForUuidAsync(NusServiceUuid, BluetoothCacheMode.Uncached);
                await LogAsync($"NUS service lookup: {services.Status}, count={services.Services.Count}");
                if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                {
                    await LogAsync($"NUS service not found. Status: {services.Status}");
                    return;
                }

                _nusService = services.Services[0];

                var characteristics = await _nusService.GetCharacteristicsForUuidAsync(
                    NusTxCharacteristicUuid,
                    BluetoothCacheMode.Uncached);

                await LogAsync($"TX characteristic lookup: {characteristics.Status}, count={characteristics.Characteristics.Count}");
                if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                {
                    await LogAsync($"TX characteristic not found. Status: {characteristics.Status}");
                    return;
                }

                _txCharacteristic = characteristics.Characteristics[0];
                await LogAsync($"Subscribing to handle 0x{_txCharacteristic.AttributeHandle:X4}");
                notificationChannel = Channel.CreateUnbounded<BleNotification>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                var notificationProcessor = new BleNotificationProcessor(_options, _stats);
                notificationProcessingTask = ProcessNotificationsAsync(
                    notificationChannel.Reader,
                    notificationProcessor,
                    cancellationToken);

                valueChangedHandler = (_, eventArgs) =>
                {
                    var notificationTimestampUtc = DateTimeOffset.UtcNow;
                    CryptographicBuffer.CopyToByteArray(eventArgs.CharacteristicValue, out var bytes);
                    notificationChannel.Writer.TryWrite(new BleNotification(bytes, notificationTimestampUtc));
                };

                _txCharacteristic.ValueChanged += valueChangedHandler;

                var notifyStatus = await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);

                await LogAsync($"Enable notifications: {notifyStatus}");
                if (notifyStatus != GattCommunicationStatus.Success)
                {
                    await LogAsync($"Could not enable notifications. Status: {notifyStatus}");
                    return;
                }

                if (_options.StartCommand.Length > 0)
                {
                    var rxCharacteristics = await _nusService.GetCharacteristicsForUuidAsync(
                        NusRxCharacteristicUuid,
                        BluetoothCacheMode.Uncached);

                    if (rxCharacteristics.Status == GattCommunicationStatus.Success && rxCharacteristics.Characteristics.Count > 0)
                    {
                        _rxCharacteristic = rxCharacteristics.Characteristics[0];
                        if (_options.SettleDelaySeconds > 0)
                        {
                            await LogAsync($"Place Triki flat and keep it still. Starting stream in {_options.SettleDelaySeconds} seconds...");
                            await Task.Delay(TimeSpan.FromSeconds(_options.SettleDelaySeconds), cancellationToken);
                        }

                        await LogAsync($"Writing start command to handle 0x{_rxCharacteristic.AttributeHandle:X4}");
                        var writeStatus = await _rxCharacteristic.WriteValueAsync(
                            CryptographicBuffer.CreateFromByteArray(_options.StartCommand),
                            GattWriteOption.WriteWithoutResponse);
                        if (writeStatus != GattCommunicationStatus.Success)
                        {
                            await LogAsync($"Failed to write start command: {writeStatus}");
                        }
                    }
                }

                await LogAsync("Reading gyro data...");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await LogAsync("Stopping.");
            }
            finally
            {
                if (_txCharacteristic is not null && valueChangedHandler is not null)
                {
                    _txCharacteristic.ValueChanged -= valueChangedHandler;
                    try
                    {
                        var stopNotifyStatus = await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None);
                        await LogAsync($"Disable notifications: {stopNotifyStatus}");
                    }
                    catch (Exception ex)
                    {
                        await LogAsync($"Failed to disable notifications: {ex.Message}");
                    }
                }

                _txCharacteristic = null;
                _rxCharacteristic = null;
                notificationChannel?.Writer.TryComplete();
                if (notificationProcessingTask is not null)
                {
                    try
                    {
                        await notificationProcessingTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (_nusService is not null)
                {
                    _nusService.Dispose();
                    _nusService = null;
                }

                if (_connectedDevice is not null)
                {
                    _connectedDevice.Dispose();
                    _connectedDevice = null;
                }

                ConnectionLost?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task LogGattDatabaseAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
        {
            try
            {
                var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                await LogAsync($"GATT services: {services.Status}, count={services.Services.Count}");
                if (services.Status != GattCommunicationStatus.Success)
                {
                    return;
                }

                foreach (var service in services.Services)
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        await LogAsync($"GATT service 0x{service.AttributeHandle:X4}: {FormatUuid(service.Uuid)}");
                        var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                        await LogAsync($"  characteristics: {characteristics.Status}, count={characteristics.Characteristics.Count}");
                        if (characteristics.Status != GattCommunicationStatus.Success)
                        {
                            continue;
                        }

                        foreach (var characteristic in characteristics.Characteristics)
                        {
                            await LogAsync(
                                $"  char 0x{characteristic.AttributeHandle:X4}: {FormatUuid(characteristic.Uuid)} props={characteristic.CharacteristicProperties}");
                        }
                    }
                    finally
                    {
                        service.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync($"GATT diagnostics failed: {ex.Message}");
            }
        }

        private async Task<TrikiDeviceInfo> ReadDeviceInfoAsync(
            BluetoothLEDevice device,
            CancellationToken cancellationToken)
        {
            var deviceNameBytes = await TryReadCharacteristicBytesAsync(
                device,
                GenericAccessServiceUuid,
                DeviceNameCharacteristicUuid,
                "Device name",
                cancellationToken);
            var batteryLevelBytes = await TryReadCharacteristicBytesAsync(
                device,
                BatteryServiceUuid,
                BatteryLevelCharacteristicUuid,
                "Battery level",
                cancellationToken);
            var manufacturerNameBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                ManufacturerNameCharacteristicUuid,
                "Manufacturer name",
                cancellationToken);
            var modelNumberBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                ModelNumberCharacteristicUuid,
                "Model number",
                cancellationToken);
            var serialNumberBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                SerialNumberCharacteristicUuid,
                "Serial number",
                cancellationToken);
            var firmwareRevisionBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                FirmwareRevisionCharacteristicUuid,
                "Firmware revision",
                cancellationToken);
            var hardwareRevisionBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                HardwareRevisionCharacteristicUuid,
                "Hardware revision",
                cancellationToken);
            var softwareRevisionBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                SoftwareRevisionCharacteristicUuid,
                "Software revision",
                cancellationToken);
            var systemIdBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                SystemIdCharacteristicUuid,
                "System ID",
                cancellationToken);
            var pnpIdBytes = await TryReadCharacteristicBytesAsync(
                device,
                DeviceInformationServiceUuid,
                PnpIdCharacteristicUuid,
                "PnP ID",
                cancellationToken);

            return new TrikiDeviceInfo(
                DeviceName: TrikiDeviceInfo.DecodeText(deviceNameBytes ?? Array.Empty<byte>()) ?? device.Name,
                BatteryLevelPercent: batteryLevelBytes is null ? null : TrikiDeviceInfo.DecodeBatteryLevel(batteryLevelBytes),
                FirmwareRevision: DecodeTextOrNull(firmwareRevisionBytes),
                HardwareRevision: DecodeTextOrNull(hardwareRevisionBytes),
                SoftwareRevision: DecodeTextOrNull(softwareRevisionBytes),
                ManufacturerName: DecodeTextOrNull(manufacturerNameBytes),
                ModelNumber: DecodeTextOrNull(modelNumberBytes),
                SerialNumber: DecodeTextOrNull(serialNumberBytes),
                SystemId: systemIdBytes is null ? null : TrikiDeviceInfo.DecodeSystemId(systemIdBytes),
                PnpId: pnpIdBytes is null ? null : TrikiDeviceInfo.DecodePnpId(pnpIdBytes));
        }

        private async Task<byte[]?> TryReadCharacteristicBytesAsync(
            BluetoothLEDevice device,
            Guid serviceUuid,
            Guid characteristicUuid,
            string label,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var services = await device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
                await LogAsync($"{label}: service lookup {services.Status}, count={services.Services.Count}");
                if (services.Status != GattCommunicationStatus.Success)
                {
                    return null;
                }

                foreach (var service in services.Services)
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return null;
                        }

                        var characteristics = await service.GetCharacteristicsForUuidAsync(
                            characteristicUuid,
                            BluetoothCacheMode.Uncached);
                        await LogAsync($"{label}: characteristic lookup {characteristics.Status}, count={characteristics.Characteristics.Count}");
                        if (characteristics.Status != GattCommunicationStatus.Success ||
                            characteristics.Characteristics.Count == 0)
                        {
                            continue;
                        }

                        var characteristic = characteristics.Characteristics[0];
                        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                        {
                            await LogAsync($"{label}: characteristic 0x{characteristic.AttributeHandle:X4} is not readable");
                            continue;
                        }

                        var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                        var valueLength = read.Value?.Length ?? 0;
                        await LogAsync($"{label}: read {read.Status} from handle 0x{characteristic.AttributeHandle:X4}, bytes={valueLength}");
                        if (read.Status != GattCommunicationStatus.Success || read.Value is null)
                        {
                            continue;
                        }

                        CryptographicBuffer.CopyToByteArray(read.Value, out var bytes);
                        return bytes;
                    }
                    finally
                    {
                        service.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync($"{label}: read failed: {ex.Message}");
            }

            return null;
        }

        private async Task ProcessNotificationsAsync(
            ChannelReader<BleNotification> reader,
            BleNotificationProcessor processor,
            CancellationToken cancellationToken)
        {
            await foreach (var notification in reader.ReadAllAsync(cancellationToken))
            {
                foreach (var sample in processor.Process(notification.Bytes, notification.TimestampUtc))
                {
                    SampleReceived?.Invoke(this, sample);
                }
            }
        }

        private async Task<BluetoothLEDevice?> FindDeviceAsync(CancellationToken cancellationToken)
        {
            var foundDevice = new TaskCompletionSource<BluetoothLEDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var scanTimeout = _options.ScanTimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(_options.ScanTimeoutSeconds))
                : new CancellationTokenSource();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                scanTimeout.Token);
            using var registration = linkedCancellation.Token.Register(() => foundDevice.TrySetResult(null));

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            watcher.Received += async (_, eventArgs) =>
            {
                var name = eventArgs.Advertisement.LocalName;
                if (string.IsNullOrWhiteSpace(name) ||
                    !name.Contains(_options.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                watcher.Stop();
                var device = await BluetoothLEDevice.FromBluetoothAddressAsync(eventArgs.BluetoothAddress);
                foundDevice.TrySetResult(device);
            };

            await LogAsync($"Scanning for BLE device for {_options.ScanTimeoutSeconds} seconds...");
            watcher.Start();

            var result = await foundDevice.Task;
            watcher.Stop();
            if (result is null && !cancellationToken.IsCancellationRequested)
            {
                await LogAsync("Scan timed out.");
            }
            return result;
        }

        private Task LogAsync(string message)
        {
            LogMessage?.Invoke(this, $"{DateTimeOffset.Now:O} {message}");
            return Task.CompletedTask;
        }

        private static string? DecodeTextOrNull(byte[]? bytes)
        {
            return bytes is null ? null : TrikiDeviceInfo.DecodeText(bytes);
        }

        private static Guid BluetoothUuid(ushort value)
        {
            return Guid.Parse($"0000{value:X4}-0000-1000-8000-00805f9b34fb");
        }

        private static string FormatUuid(Guid uuid)
        {
            var label = KnownUuidLabel(uuid);
            var compactUuid = CompactBluetoothUuid(uuid);
            return label is null ? compactUuid : $"{label} ({compactUuid})";
        }

        private static string? KnownUuidLabel(Guid uuid)
        {
            if (uuid == GenericAccessServiceUuid) return "Generic Access";
            if (uuid == DeviceNameCharacteristicUuid) return "Device Name";
            if (uuid == BatteryServiceUuid) return "Battery Service";
            if (uuid == BatteryLevelCharacteristicUuid) return "Battery Level";
            if (uuid == DeviceInformationServiceUuid) return "Device Information";
            if (uuid == ManufacturerNameCharacteristicUuid) return "Manufacturer Name";
            if (uuid == ModelNumberCharacteristicUuid) return "Model Number";
            if (uuid == SerialNumberCharacteristicUuid) return "Serial Number";
            if (uuid == FirmwareRevisionCharacteristicUuid) return "Firmware Revision";
            if (uuid == HardwareRevisionCharacteristicUuid) return "Hardware Revision";
            if (uuid == SoftwareRevisionCharacteristicUuid) return "Software Revision";
            if (uuid == SystemIdCharacteristicUuid) return "System ID";
            if (uuid == PnpIdCharacteristicUuid) return "PnP ID";
            if (uuid == NusServiceUuid) return "Nordic UART Service";
            if (uuid == NusRxCharacteristicUuid) return "NUS RX";
            if (uuid == NusTxCharacteristicUuid) return "NUS TX";
            return null;
        }

        private static string CompactBluetoothUuid(Guid uuid)
        {
            var text = uuid.ToString();
            const string bluetoothBaseSuffix = "-0000-1000-8000-00805f9b34fb";
            if (text.StartsWith("0000", StringComparison.OrdinalIgnoreCase) &&
                text.EndsWith(bluetoothBaseSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return "0x" + text.Substring(4, 4).ToUpperInvariant();
            }

            return text;
        }
    }

    public sealed class FrameParser
    {
        private const int FrameLength = 14;
        private readonly List<byte> _buffer = new();

        public long DroppedByteCount { get; private set; }

        public IEnumerable<byte[]> Push(byte[] bytes)
        {
            _buffer.AddRange(bytes);

            while (true)
            {
                var headerIndex = FindHeader();
                if (headerIndex < 0)
                {
                    if (_buffer.Count > 0)
                    {
                        var keepTrailingHeaderByte = _buffer[^1] == 0x22;
                        var dropCount = keepTrailingHeaderByte ? _buffer.Count - 1 : _buffer.Count;
                        DroppedByteCount += dropCount;

                        if (keepTrailingHeaderByte)
                        {
                            var trailingByte = _buffer[^1];
                            _buffer.Clear();
                            _buffer.Add(trailingByte);
                        }
                        else
                        {
                            _buffer.Clear();
                        }
                    }
                    yield break;
                }

                if (headerIndex > 0)
                {
                    DroppedByteCount += headerIndex;
                    _buffer.RemoveRange(0, headerIndex);
                }

                if (_buffer.Count < FrameLength)
                {
                    yield break;
                }

                var frame = _buffer.GetRange(0, FrameLength).ToArray();
                _buffer.RemoveRange(0, FrameLength);
                yield return frame;
            }
        }

        private int FindHeader()
        {
            for (var i = 0; i < _buffer.Count - 1; i++)
            {
                if (_buffer[i] == 0x22 && _buffer[i + 1] == 0x00)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    public readonly record struct BleNotification(byte[] Bytes, DateTimeOffset TimestampUtc);

    public sealed class BleNotificationProcessor
    {
        private readonly FrameParser _parser = new();
        private readonly ImuSampleProcessor _processor;
        private readonly ImuStats _stats;

        public BleNotificationProcessor(AppOptions options, ImuStats stats)
        {
            _processor = new ImuSampleProcessor(options);
            _stats = stats;
        }

        public IReadOnlyList<ImuSample> Process(byte[] bytes, DateTimeOffset notificationTimestampUtc)
        {
            _stats.NotificationReceived(notificationTimestampUtc);
            var samples = new List<ImuSample>();

            foreach (var frame in _parser.Push(bytes))
            {
                var sample = _processor.ProcessFrame(frame, notificationTimestampUtc);

                if (sample is null)
                {
                    continue;
                }

                _stats.CopyFrom(_processor.Stats);
                _stats.SetDroppedBytes(_parser.DroppedByteCount);
                samples.Add(sample.Value);
            }

            _stats.CopyFrom(_processor.Stats);
            _stats.SetDroppedBytes(_parser.DroppedByteCount);
            return samples;
        }
    }

    public sealed class ImuSampleProcessor
    {
        private readonly AppOptions _options;
        private readonly ImuStats _stats = new();
        private long _nextFrameIndex;

        public ImuSampleProcessor(AppOptions options)
        {
            _options = options;
        }

        public ImuStats Stats => _stats;

        public ImuSample? ProcessFrame(byte[] frame)
        {
            return ProcessFrame(frame, DateTimeOffset.UtcNow);
        }

        public ImuSample? ProcessFrame(byte[] frame, DateTimeOffset timestampUtc)
        {
            _stats.FrameParsed();

            if (_stats.DiscardedStartupSampleCount < _options.StartupDiscardSamples)
            {
                _stats.StartupSampleDiscarded();
                return null;
            }

            var sample = ImuSample.FromFrame(
                frame,
                _nextFrameIndex,
                _options.GyroScale,
                _options.AccelScale,
                timestampUtc);

            _nextFrameIndex++;
            _stats.SampleWritten(sample);
            return sample;
        }
    }

    public readonly record struct ImuSample(
        long FrameIndex,
        DateTimeOffset TimestampUtc,
        double GyroX,
        double GyroY,
        double GyroZ,
        double AccelX,
        double AccelY,
        double AccelZ,
        short RawGyroX,
        short RawGyroY,
        short RawGyroZ,
        short RawAccelX,
        short RawAccelY,
        short RawAccelZ)
    {
        public static ImuSample FromFrame(byte[] frame, long frameIndex, double gyroScale, double accelScale)
        {
            return FromFrame(frame, frameIndex, gyroScale, accelScale, DateTimeOffset.UtcNow);
        }

        public static ImuSample FromFrame(
            byte[] frame,
            long frameIndex,
            double gyroScale,
            double accelScale,
            DateTimeOffset timestampUtc)
        {
            var rawGyroX = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(2, 2));
            var rawGyroY = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(4, 2));
            var rawGyroZ = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(6, 2));
            var rawAccelX = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(8, 2));
            var rawAccelY = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(10, 2));
            var rawAccelZ = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(12, 2));

            return new ImuSample(
                frameIndex,
                timestampUtc,
                rawGyroX / gyroScale,
                rawGyroY / gyroScale,
                rawGyroZ / gyroScale,
                rawAccelX / accelScale,
                rawAccelY / accelScale,
                rawAccelZ / accelScale,
                rawGyroX,
                rawGyroY,
                rawGyroZ,
                rawAccelX,
                rawAccelY,
                rawAccelZ);
        }
    }

    public sealed class ImuStats
    {
        public long NotificationCount { get; private set; }
        public long ParsedFrameCount { get; private set; }
        public long DiscardedStartupSampleCount { get; private set; }
        public long WrittenSampleCount { get; private set; }
        public long DroppedByteCount { get; private set; }
        public double LastNotificationGapMilliseconds { get; private set; }
        public double MaxNotificationGapMilliseconds { get; private set; }
        private DateTimeOffset? _lastNotificationTimestampUtc;

        public void NotificationReceived() => NotificationReceived(DateTimeOffset.UtcNow);
        public void NotificationReceived(DateTimeOffset timestampUtc)
        {
            if (_lastNotificationTimestampUtc is not null)
            {
                LastNotificationGapMilliseconds = Math.Max(
                    0.0,
                    (timestampUtc - _lastNotificationTimestampUtc.Value).TotalMilliseconds);
                MaxNotificationGapMilliseconds = Math.Max(
                    MaxNotificationGapMilliseconds,
                    LastNotificationGapMilliseconds);
            }

            _lastNotificationTimestampUtc = timestampUtc;
            NotificationCount++;
        }
        public void FrameParsed() => ParsedFrameCount++;
        public void StartupSampleDiscarded() => DiscardedStartupSampleCount++;
        public void SetDroppedBytes(long droppedByteCount) => DroppedByteCount = droppedByteCount;
        public void SampleWritten(ImuSample sample)
        {
            WrittenSampleCount++;
        }

        public void CopyFrom(ImuStats stats)
        {
            ParsedFrameCount = stats.ParsedFrameCount;
            DiscardedStartupSampleCount = stats.DiscardedStartupSampleCount;
            WrittenSampleCount = stats.WrittenSampleCount;
        }
    }

    public sealed record AppOptions(
        string DeviceName,
        string OutputPath,
        double GyroScale,
        double AccelScale,
        string DebugLogPath,
        int DurationSeconds,
        byte[] StartCommand,
        int ConsoleEverySamples,
        int StartupDiscardSamples,
        int ScanTimeoutSeconds,
        int SettleDelaySeconds)
    {
        public static AppOptions Default => new(
            "Triki",
            "triki_data.csv",
            131.0,
            2048.0,
            "triki_debug.log",
            0,
            Convert.FromHexString("201000D007680003"),
            20,
            20,
            30,
            3);
    }
}
