// Waits for a USB device to arrive using the standard Win32 RegisterDeviceNotification /
// WM_DEVICECHANGE pattern, instead of polling the registry on a timer. A hidden message-only
// window receives arrival/removal notifications for all USB device interfaces
// (GUID_DEVINTERFACE_USB_DEVICE) and filters them by a VID/PID substring match on the
// device path - the same broad-GUID-plus-filter approach used by most USB device managers,
// since the exact per-driver interface GUID varies (inbox HID vs WinUSB vs vendor driver).
using System.Runtime.InteropServices;

namespace NeoSync.App;

internal sealed class DeviceArrivalWatcher : NativeWindow, IDisposable
{
    private static readonly Guid GuidDevinterfaceUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private const int WM_DEVICECHANGE            = 0x0219;
    private const int DBT_DEVICEARRIVAL           = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE    = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE  = 5;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private static readonly nint HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterDeviceNotification(nint hRecipient, nint notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(nint handle);

    private readonly object _lock = new();
    private readonly List<(string substring, bool arrival, TaskCompletionSource<bool> tcs)> _waiters = [];
    private nint _notifyHandle;

    public DeviceArrivalWatcher()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

        int size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size       = size,
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid  = GuidDevinterfaceUsbDevice
            };
            Marshal.StructureToPtr(filter, buffer, false);
            _notifyHandle = RegisterDeviceNotification(Handle, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Waits for a USB device interface whose path contains <paramref name="vidPidSubstring"/>
    /// (e.g. "vid_081e&amp;pid_bd01", case-insensitive) to arrive. Checks
    /// <paramref name="alreadyPresent"/> first so an already-connected device resolves instantly
    /// instead of waiting for a fresh arrival event that may never come.</summary>
    public Task<bool> WaitForArrivalAsync(string vidPidSubstring, int timeoutMs, Func<bool>? alreadyPresent = null)
    {
        if (alreadyPresent?.Invoke() == true) return Task.FromResult(true);
        return Wait(vidPidSubstring, arrival: true, timeoutMs);
    }

    private Task<bool> Wait(string vidPidSubstring, bool arrival, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _waiters.Add((vidPidSubstring.ToLowerInvariant(), arrival, tcs));

        var timer = new System.Threading.Timer(_ => tcs.TrySetResult(false), null, timeoutMs, Timeout.Infinite);
        _ = tcs.Task.ContinueWith(_ => timer.Dispose(), TaskScheduler.Default);
        return tcs.Task;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE) HandleDeviceChange(m.WParam, m.LParam);
        base.WndProc(ref m);
    }

    private void HandleDeviceChange(nint wParam, nint lParam)
    {
        int eventType = wParam.ToInt32();
        bool isArrival = eventType == DBT_DEVICEARRIVAL;
        if ((!isArrival && eventType != DBT_DEVICEREMOVECOMPLETE) || lParam == nint.Zero) return;

        var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
        if (hdr.dbch_devicetype != DBT_DEVTYP_DEVICEINTERFACE) return;

        nint namePtr = lParam + Marshal.OffsetOf<DEV_BROADCAST_DEVICEINTERFACE>(nameof(DEV_BROADCAST_DEVICEINTERFACE.dbcc_name));
        string path = (Marshal.PtrToStringUni(namePtr) ?? string.Empty).ToLowerInvariant();

        lock (_lock)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                var (substring, wantArrival, tcs) = _waiters[i];
                if (wantArrival == isArrival && path.Contains(substring))
                {
                    tcs.TrySetResult(true);
                    _waiters.RemoveAt(i);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_notifyHandle != nint.Zero) UnregisterDeviceNotification(_notifyHandle);
        if (Handle != nint.Zero) DestroyHandle();
    }
}
