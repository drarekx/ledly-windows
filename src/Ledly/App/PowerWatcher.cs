using System.Runtime.InteropServices;
using Ledly.Windows.Interop;
using Microsoft.Win32;

namespace Ledly.Windows.App;

/// <summary>
/// Tells the engine when the PC suspends or its screens turn off (and back), so the strip goes dark
/// with them (<see cref="Settings.OffWhenAsleep"/>). Locking the session alone is not sleep.
/// </summary>
sealed class PowerWatcher : NativeWindow, IDisposable
{
    IntPtr _notification;
    bool _suspended;
    bool _displayOff;
    bool _reported;

    public PowerWatcher()
    {
        // A hidden top-level window: message-only windows don't get broadcasts like WM_POWERBROADCAST.
        CreateHandle(new CreateParams { Caption = "Ledly power" });
        var guid = Native.GUID_CONSOLE_DISPLAY_STATE;
        _notification = Native.RegisterPowerSettingNotification(Handle, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Set(suspended: true, _displayOff);
        else if (e.Mode == PowerModes.Resume) Set(suspended: false, _displayOff);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_POWERBROADCAST)
        {
            switch ((int)m.WParam)
            {
                case Native.PBT_APMSUSPEND:
                    Set(true, _displayOff);
                    break;
                case Native.PBT_APMRESUMEAUTOMATIC:
                    Set(false, _displayOff);
                    break;
                case Native.PBT_POWERSETTINGCHANGE when m.LParam != IntPtr.Zero:
                    // POWERBROADCAST_SETTING: GUID PowerSetting, DWORD DataLength, BYTE Data[].
                    var setting = Marshal.PtrToStructure<Guid>(m.LParam);
                    if (setting == Native.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        // 0 = off, 1 = on, 2 = dimmed (still on).
                        var state = Marshal.ReadInt32(m.LParam, 20);
                        Set(_suspended, state == 0);
                    }
                    break;
            }
            m.Result = new IntPtr(1);
            return;
        }
        base.WndProc(ref m);
    }

    void Set(bool suspended, bool displayOff)
    {
        _suspended = suspended;
        _displayOff = displayOff;
        var asleep = suspended || displayOff;
        if (asleep == _reported) return;
        _reported = asleep;
        Log.Write(asleep ? "power: asleep" : "power: awake");
        try { LEDEngine.Shared.SetSleeping(asleep); } catch (Exception e) { Log.Write($"power: {e.Message}"); }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_notification != IntPtr.Zero)
        {
            Native.UnregisterPowerSettingNotification(_notification);
            _notification = IntPtr.Zero;
        }
        DestroyHandle();
    }
}
