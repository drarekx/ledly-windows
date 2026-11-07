namespace Ledly;

// CONTRACT STUB — replaced by the device port. Keep these public signatures.

public enum DeviceStatusKind { disconnected, connecting, connected, failed }

/// <summary>Firmware: the hello line ("Ada Ledly 2 pin 6 max 400 baud 1000000"), null for a silent Adalight sketch.</summary>
public sealed record DeviceStatus(DeviceStatusKind Kind, string? Firmware = null, string? Error = null)
{
    public static readonly DeviceStatus Disconnected = new(DeviceStatusKind.disconnected);
    public static readonly DeviceStatus Connecting = new(DeviceStatusKind.connecting);
}

/// <summary>Connection to the Arduino that drives the strip (Adalight protocol). Only used from the engine thread.</summary>
public sealed class LEDDevice
{
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public bool IsConnected => Status.Kind == DeviceStatusKind.connected;
    public string? Port { get; private set; }

    /// <summary>Blocking: opens, resets, waits for the hello, tries the known baud rates.</summary>
    public void Connect(string port) => throw new NotImplementedException();
    public void Disconnect() => throw new NotImplementedException();
    /// <summary>Sends one frame; false when skipped (waiting for the 'k') or failed.</summary>
    public bool Send(RGB[] colors) => throw new NotImplementedException();

    public static int? LedlyVersion(string? hello) => throw new NotImplementedException();
    public static int? LedlyPin(string? hello) => throw new NotImplementedException();
}
