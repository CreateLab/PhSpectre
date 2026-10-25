using System.Runtime.InteropServices;

namespace PhSpectre.Wpd;

// Hand-written COM declarations for the few WPD interfaces we use. Method order is the vtable order from
// the Windows SDK headers (PortableDeviceApi.h / PortableDeviceTypes.h); unused methods are placeholders
// that must stay so later slots line up.

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(string guid, uint pid)
    {
        FormatId = new Guid(guid);
        PropertyId = pid;
    }
}

/// <summary>PROPVARIANT for 64-bit processes (24 bytes); only the VT_UI4 case is needed.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantUInt32
{
    private const ushort VtUi4 = 19;

    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public uint Value;

    public static PropVariantUInt32 Of(uint value) => new() { Vt = VtUi4, Value = value };
}

[ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    void GetDevices(IntPtr pnpDeviceIds, ref uint count);
    void RefreshDeviceList();
    void GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IntPtr name, ref uint chars);
}

[ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    void Open([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IPortableDeviceValues clientInfo);
    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
    void Content_Unused();
    void Capabilities_Unused();
    void Cancel();
    void Close();
}

[ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    void GetCount_Unused();
    void GetAt_Unused();
    void SetValue_Unused();
    void GetValue_Unused();
    void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetStringValue(ref PropertyKey key, out IntPtr value);
    void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    void GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    void SetSignedIntegerValue_Unused();
    void GetSignedIntegerValue_Unused();
    void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
    void GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
    void SetSignedLargeIntegerValue_Unused();
    void GetSignedLargeIntegerValue_Unused();
    void SetFloatValue_Unused();
    void GetFloatValue_Unused();
    void SetErrorValue_Unused();
    void GetErrorValue_Unused();
    void SetKeyValue_Unused();
    void GetKeyValue_Unused();
    void SetBoolValue_Unused();
    void GetBoolValue_Unused();
    void SetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    void GetIUnknownValue(ref PropertyKey key, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    void SetGuidValue(ref PropertyKey key, ref Guid value);
    void GetGuidValue_Unused();
    void SetBufferValue(ref PropertyKey key, IntPtr buffer, uint length);
    void GetBufferValue(ref PropertyKey key, out IntPtr buffer, out uint length);
}

[ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropVariantUInt32 value);
    void Add(ref PropVariantUInt32 value);
}

internal static class WpdClasses
{
    public static readonly Type Device = Type.GetTypeFromCLSID(new Guid("728a21c5-3d9e-48d7-9810-864848f0f404"))!;
    public static readonly Type Manager = Type.GetTypeFromCLSID(new Guid("0af10cec-2ecd-4b92-9581-34f6ae0637f3"))!;
    public static readonly Type Values = Type.GetTypeFromCLSID(new Guid("0c15d503-d017-47ce-9016-7b3f978721cc"))!;
    public static readonly Type PropVariantCollection = Type.GetTypeFromCLSID(new Guid("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9"))!;
}

internal static class WpdKeys
{
    private const string Common = "f0422a9c-5dc8-4440-b5bd-5df28835658a";
    private const string MtpExt = "4d545058-1a2e-4106-a357-771e0819fc56";
    private const string Client = "204d9f0c-2292-4080-9f42-40664e70f859";

    public static readonly Guid MtpExtCategory = new(MtpExt);

    public static readonly PropertyKey CommandCategory = new(Common, 1001);
    public static readonly PropertyKey CommandId = new(Common, 1002);
    public static readonly PropertyKey Hresult = new(Common, 1003);

    public const uint CommandWithoutDataPhase = 12;
    public const uint CommandWithDataToRead = 13;
    public const uint CommandWithDataToWrite = 14;
    public const uint CommandReadData = 15;
    public const uint CommandWriteData = 16;
    public const uint CommandEndDataTransfer = 17;

    public static readonly PropertyKey OperationCode = new(MtpExt, 1001);
    public static readonly PropertyKey OperationParams = new(MtpExt, 1002);
    public static readonly PropertyKey ResponseCode = new(MtpExt, 1003);
    public static readonly PropertyKey ResponseParams = new(MtpExt, 1004);
    public static readonly PropertyKey TransferContext = new(MtpExt, 1006);
    public static readonly PropertyKey TransferTotalDataSize = new(MtpExt, 1007);
    public static readonly PropertyKey TransferNumBytesToRead = new(MtpExt, 1008);
    public static readonly PropertyKey TransferNumBytesRead = new(MtpExt, 1009);
    public static readonly PropertyKey TransferNumBytesToWrite = new(MtpExt, 1010);
    public static readonly PropertyKey TransferNumBytesWritten = new(MtpExt, 1011);
    public static readonly PropertyKey TransferData = new(MtpExt, 1012);
    public static readonly PropertyKey OptimalTransferBufferSize = new(MtpExt, 1013);

    public static readonly PropertyKey ClientName = new(Client, 2);
    public static readonly PropertyKey ClientMajorVersion = new(Client, 3);
    public static readonly PropertyKey ClientMinorVersion = new(Client, 4);
    public static readonly PropertyKey ClientRevision = new(Client, 5);
    public static readonly PropertyKey ClientDesiredAccess = new(Client, 9);
}
