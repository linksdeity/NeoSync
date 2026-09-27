// Windows P/Invoke declarations for: setupapi.dll, hid.dll, winusb.dll, kernel32.dll.
// All USB access goes through these Windows-native APIs — no third-party packages.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NeoSync.Core.Usb;

internal static partial class NativeMethods
{
    // ── kernel32.dll ─────────────────────────────────────────────────────────

    public const uint GenericRead    = 0x80000000;
    public const uint GenericWrite   = 0x40000000;
    public const uint FileShareRead  = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting   = 3;
    public const uint FileFlagOverlapped = 0x40000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    // ── setupapi.dll ──────────────────────────────────────────────────────────

    public static readonly nint InvalidHandleValue = new nint(-1);

    public const uint DigcfPresent         = 0x00000002;
    public const uint DigcfDeviceInterface = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SetupDiGetClassDevs(
        ref Guid ClassGuid,
        string? Enumerator,
        nint hwndParent,
        uint Flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(
        nint DeviceInfoSet,
        nint DeviceInfoData,
        ref Guid InterfaceClassGuid,
        int MemberIndex,
        ref SpDeviceInterfaceData DeviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(
        nint DeviceInfoSet,
        ref SpDeviceInterfaceData DeviceInterfaceData,
        nint DeviceInterfaceDetailData,
        int DeviceInterfaceDetailDataSize,
        out int RequiredSize,
        nint DeviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(nint DeviceInfoSet);

    // ── cfgmgr32.dll ──────────────────────────────────────────────────────────

    private const int CR_SUCCESS = 0;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(
        out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(
        out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Set_DevNode_Registry_PropertyW")]
    private static partial int CM_Set_DevNode_Registry_PropertyW(
        uint dnDevInst, uint ulProperty, ref int Buffer, uint ulLength, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Set_DevNode_Registry_PropertyW")]
    private static partial int CM_Set_DevNode_Registry_PropertyW_Ptr(
        uint dnDevInst, uint ulProperty, nint pBuffer, uint ulLength, uint ulFlags);

    /// <summary>Sets the security descriptor on a device node, allowing non-admin users to open the device.</summary>
    public static bool SetDevNodeSecurity(string deviceInstanceId, byte[] securityDescriptor)
    {
        const uint CM_DRP_SECURITY = 0x1B;
        const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;
        if (CM_Locate_DevNodeW(out uint devInst, deviceInstanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return false;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            securityDescriptor, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return CM_Set_DevNode_Registry_PropertyW_Ptr(
                devInst, CM_DRP_SECURITY, handle.AddrOfPinnedObject(),
                (uint)securityDescriptor.Length, 0) == CR_SUCCESS;
        }
        finally { handle.Free(); }
    }

    /// <summary>
    /// Clears the ConfigFlags registry value on a device node to 0 (removes REINSTALL, FAILEDINSTALL, etc.).
    /// Uses CM_LOCATE_DEVNODE_PHANTOM so it works on non-present (phantom) nodes.
    /// Returns true on success.
    /// </summary>
    public static bool ClearDevNodeConfigFlags(string deviceInstanceId)
    {
        const uint CM_DRP_CONFIGFLAGS = 0x16;
        const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;
        if (CM_Locate_DevNodeW(out uint devInst, deviceInstanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return false;
        int zero = 0;
        return CM_Set_DevNode_Registry_PropertyW(devInst, CM_DRP_CONFIGFLAGS, ref zero, 4, 0) == CR_SUCCESS;
    }

    /// <summary>Returns the CM problem code for a device instance ID, or 0 if OK.</summary>
    public static uint GetDevNodeProblem(string deviceInstanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, deviceInstanceId, 0) != CR_SUCCESS) return 0;
        if (CM_Get_DevNode_Status(out _, out uint problem, devInst, 0) != CR_SUCCESS) return 0;
        return problem;
    }

    /// <summary>
    /// Returns (status, problem) from CM_Get_DevNode_Status.
    /// Tries present-only first (flags=0), then phantom (flags=1) so transitional states are visible.
    /// status flags: DN_STARTED=0x8, DN_DRIVER_LOADED=0x100, DN_HAS_PROBLEM=0x400.
    /// </summary>
    internal static (uint Status, uint Problem) GetDevNodeStatus(string deviceInstanceId)
    {
        for (uint locateFlags = 0; locateFlags <= 1; locateFlags++)
        {
            if (CM_Locate_DevNodeW(out uint devInst, deviceInstanceId, locateFlags) == CR_SUCCESS
                && CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == CR_SUCCESS)
                return (status, problem);
        }
        return (0, 0);
    }

    /// <summary>Returns true if the device instance is physically present in the PnP tree.</summary>
    public static bool IsDevicePresent(string deviceInstanceId)
        => CM_Locate_DevNodeW(out _, deviceInstanceId, 0) == CR_SUCCESS;

    /// <summary>Returns true if the device is present or phantom (known to PnP but not currently on bus).</summary>
    public static bool IsDevicePresentOrPhantom(string deviceInstanceId)
        => CM_Locate_DevNodeW(out _, deviceInstanceId, 1) == CR_SUCCESS;

    // ── setupapi helpers ──────────────────────────────────────────────────────

    /// <summary>Extracts the device path string from a SetupDi enumeration result.</summary>
    public static string? GetDeviceInterfacePath(nint devInfo, ref SpDeviceInterfaceData interfaceData)
    {
        SetupDiGetDeviceInterfaceDetail(devInfo, ref interfaceData, nint.Zero, 0, out int requiredSize, nint.Zero);
        nint detailPtr = Marshal.AllocHGlobal(requiredSize);
        try
        {
            Marshal.WriteInt32(detailPtr, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref interfaceData, detailPtr, requiredSize, out _, nint.Zero))
                return null;
            return Marshal.PtrToStringAuto(detailPtr + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detailPtr);
        }
    }

    // ── hid.dll ───────────────────────────────────────────────────────────────

    [LibraryImport("hid.dll")]
    public static partial void HidD_GetHidGuid(out Guid HidGuid);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_SetOutputReport(
        SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer,
        uint ReportBufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_GetPreparsedData(
        SafeFileHandle HidDeviceObject, out nint PreparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool HidD_FreePreparsedData(nint PreparsedData);

    // LayoutKind.Explicit with fixed Size avoids the Reserved[17] array marshal issue.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct HidpCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
        [FieldOffset(8)] public ushort FeatureReportByteLength;
    }

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetCaps(nint PreparsedData, out HidpCaps Capabilities);

    // ── winusb.dll ────────────────────────────────────────────────────────────

    // USBD_PIPE_TYPE enum values (from Windows SDK usbiodef.h)
    public const uint UsbdPipeTypeControl     = 0;
    public const uint UsbdPipeTypeIsochronous = 1;
    public const uint UsbdPipeTypeBulk        = 2;
    public const uint UsbdPipeTypeInterrupt   = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct UsbInterfaceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    // Mirrors WINUSB_PIPE_INFORMATION (winusb.h).
    // PipeType is USBD_PIPE_TYPE (4-byte enum); natural alignment adds 1 pad byte
    // before MaximumPacketSize. Total struct size = 12 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public struct WinUsbPipeInformation
    {
        public uint   PipeType;            // USBD_PIPE_TYPE: 0=Control 1=Iso 2=Bulk 3=Interrupt
        public byte   PipeId;
        public ushort MaximumPacketSize;   // 1 implicit padding byte before this
        public byte   Interval;
    }

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out nint InterfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Free(nint InterfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_GetAssociatedInterface(
        nint InterfaceHandle,
        byte AssociatedInterfaceIndex,
        out nint AssociatedInterfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryInterfaceSettings(
        nint InterfaceHandle,
        byte AlternateInterfaceNumber,
        ref UsbInterfaceDescriptor UsbAltInterfaceDescriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryPipe(
        nint InterfaceHandle,
        byte AlternateInterfaceNumber,
        byte PipeIndex,
        ref WinUsbPipeInformation PipeInformation);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ReadPipe(
        nint InterfaceHandle,
        byte PipeID,
        byte[] Buffer,
        uint BufferLength,
        ref uint LengthTransferred,
        nint Overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_WritePipe(
        nint InterfaceHandle,
        byte PipeID,
        byte[] Buffer,
        uint BufferLength,
        ref uint LengthTransferred,
        nint Overlapped);

    /// <summary>Setup packet for WinUsb_ControlTransfer.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WinUsbSetupPacket
    {
        public byte  RequestType;   // bmRequestType
        public byte  Request;       // bRequest
        public ushort Value;        // wValue
        public ushort Index;        // wIndex
        public ushort Length;       // wLength
    }

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ControlTransfer(
        nint InterfaceHandle,
        WinUsbSetupPacket SetupPacket,
        byte[]? Buffer,
        uint BufferLength,
        ref uint LengthTransferred,
        nint Overlapped);
}
