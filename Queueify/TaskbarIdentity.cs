using System.Runtime.InteropServices;
using Queueify.Services;

namespace Queueify;

/// <summary>
/// The download's starter runs the app from an unpacked copy that goes away with the next version, so
/// a taskbar pin would point at a folder that stops existing. These window properties tell Windows to
/// pin the exe the user opened instead (ADR-0008).
/// </summary>
internal static unsafe class TaskbarIdentity
{
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    public static void PinTo(nint window, string startedExe)
    {
        if (SHGetPropertyStoreForWindow(window, IPropertyStore, out var store) < 0 || store == 0) return;
        try
        {
            // The relaunch values only count when the window has its own app id.
            Set(store, 5, "PAumedes.Queueify");
            Set(store, 2, AppData.RelaunchCommand(startedExe));
            Set(store, 4, "Queueify");
            Set(store, 3, $"{startedExe},0");
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)(*(void***)store)[7];
            commit(store);
        }
        finally
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)store)[2];
            release(store);
        }
    }

    private static void Set(nint store, uint pid, string value)
    {
        var key = new PropertyKey { FormatId = AppUserModel, Pid = pid };
        var text = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var variant = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = text };
            // IPropertyStore::SetValue is the 7th method (after IUnknown's 3, GetCount, GetAt and GetValue).
            var setValue = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)(*(void***)store)[6];
            setValue(store, &key, &variant);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid riid, out nint store);
}
