using System.Buffers.Binary;
using System.Text;

namespace Triki_Knob
{
    public sealed record TrikiDeviceInfo(
        string? DeviceName,
        int? BatteryLevelPercent,
        string? FirmwareRevision,
        string? HardwareRevision,
        string? SoftwareRevision,
        string? ManufacturerName,
        string? ModelNumber,
        string? SerialNumber,
        string? SystemId,
        string? PnpId)
    {
        public static TrikiDeviceInfo Empty { get; } = new(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        public static int? DecodeBatteryLevel(byte[] bytes)
        {
            return bytes.Length > 0 ? bytes[0] : null;
        }

        public static string? DecodeText(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return null;
            }

            var value = Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\t', '\r', '\n');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static string? DecodeSystemId(byte[] bytes)
        {
            return bytes.Length == 0 ? null : FormatHex(bytes);
        }

        public static string? DecodePnpId(byte[] bytes)
        {
            if (bytes.Length < 7)
            {
                return bytes.Length == 0 ? null : FormatHex(bytes);
            }

            var source = bytes[0] switch
            {
                1 => "Bluetooth SIG",
                2 => "USB",
                _ => $"source={bytes[0]}"
            };
            var vendorId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1, 2));
            var productId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(3, 2));
            var productVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5, 2));

            return $"{source} vendor=0x{vendorId:X4} product=0x{productId:X4} version=0x{productVersion:X4}";
        }

        public string ToDisplayText()
        {
            var lines = new List<string>
            {
                $"Device: {ValueOrPlaceholder(DeviceName)}",
                $"Battery: {BatteryText()}",
                $"System/Firmware: {ValueOrPlaceholder(FirmwareRevision)}"
            };

            AddIfPresent(lines, "Software", SoftwareRevision);
            AddIfPresent(lines, "Hardware", HardwareRevision);

            return string.Join("\n", lines);
        }

        public string ToLogText()
        {
            return
                ToDisplayText() + "\n" +
                $"Model: {ValueOrPlaceholder(ModelNumber)}\n" +
                $"Serial: {ValueOrPlaceholder(SerialNumber)}\n" +
                $"Manufacturer: {ValueOrPlaceholder(ManufacturerName)}\n" +
                $"System ID: {ValueOrPlaceholder(SystemId)}\n" +
                $"PnP ID: {ValueOrPlaceholder(PnpId)}";
        }

        private string BatteryText()
        {
            return BatteryLevelPercent is null ? "--" : $"{BatteryLevelPercent}%";
        }

        private static string ValueOrPlaceholder(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "--" : value;
        }

        private static void AddIfPresent(List<string> lines, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{label}: {value}");
            }
        }

        private static string FormatHex(byte[] bytes)
        {
            return string.Join(" ", Array.ConvertAll(bytes, b => b.ToString("X2")));
        }
    }
}
