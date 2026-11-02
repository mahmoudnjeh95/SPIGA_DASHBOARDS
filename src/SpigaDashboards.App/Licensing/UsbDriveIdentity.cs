using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SpigaDashboards.App.Licensing;

// Hardware identity of the device behind a drive letter, read straight from the
// storage driver (IOCTL_STORAGE_QUERY_PROPERTY): no WMI and no admin rights. The
// serial is the one burnt into the USB stick, so it doesn't follow a copied file.
public sealed record UsbDriveIdentity(bool IsUsb, string? Serial, string Model)
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int BusTypeUsb = 7;

    // Null when the volume can't be queried at all (no such drive, no media...).
    public static UsbDriveIdentity? Read(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\', '/', ':');
        // Access 0 = query only: enough for this IOCTL, and allowed without admin rights.
        using var handle = CreateFile($@"\\.\{letter}:", 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery, AdditionalParameters[1] }
        var query = new byte[12];
        var buffer = new byte[1024];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < 32)
            return null;

        // STORAGE_DEVICE_DESCRIPTOR: offsets of vendor (12), product (16), serial (24); BusType at 28.
        var busType = BitConverter.ToInt32(buffer, 28);
        var model = $"{ReadString(buffer, returned, 12)} {ReadString(buffer, returned, 16)}".Trim();
        var serial = Normalize(ReadString(buffer, returned, 24));
        return new UsbDriveIdentity(busType == BusTypeUsb, serial, model);
    }

    // Some sticks pad their serial with spaces; a blank or all-zero one is no serial.
    private static string? Normalize(string raw)
    {
        var serial = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return serial.Trim('0', '.', '_', '-').Length == 0 ? null : serial;
    }

    private static string ReadString(byte[] buffer, int length, int offsetField)
    {
        var offset = BitConverter.ToInt32(buffer, offsetField);
        if (offset <= 0 || offset >= length)
            return "";
        var end = Array.IndexOf(buffer, (byte)0, offset, length - offset);
        return Encoding.ASCII.GetString(buffer, offset, (end < 0 ? length : end) - offset).Trim();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
