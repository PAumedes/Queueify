using System.Runtime.InteropServices;

namespace Queueify.Launcher;

/// <summary>
/// The first thing on screen: Queueify's icon and name with a line of status, in a small window of its own,
/// while the starter unpacks the app and until the app's window opens. Plain Win32 on a thread of its own,
/// so the starter stays small and keeps working underneath. Light or dark like Windows' apps.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed unsafe partial class LoadingScreen : IDisposable
{
    public const string ClassName = "QueueifyLoading";

    private static string s_status = "";
    private static bool s_dark;

    private readonly Lock _lock = new();
    private readonly ManualResetEventSlim _closing = new();
    private readonly Thread _thread;
    private nint _hwnd;

    private LoadingScreen(string status, TimeSpan delay)
    {
        s_status = status;
        s_dark = WindowsUsesDarkApps();
        _thread = new Thread(() => Run(delay)) { IsBackground = true, Name = "Loading screen" };
        _thread.Start();
    }

    /// <summary>Shows the screen after <paramref name="delay"/>, unless it's closed before that.</summary>
    public static LoadingScreen Show(string status, TimeSpan delay) => new(status, delay);

    public void Dispose()
    {
        lock (_lock)
        {
            _closing.Set();
            if (_hwnd != 0) PostMessageW(_hwnd, WM_CLOSE, 0, 0);
        }
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run(TimeSpan delay)
    {
        if (_closing.Wait(delay)) return;
        var instance = GetModuleHandleW(null);
        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_DROPSHADOW,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(0, IDC_APPSTARTING),
                hIcon = LoadImageW(instance, IconId, IMAGE_ICON, 0, 0, LR_DEFAULTSIZE),
                lpszClassName = className,
            };
            RegisterClassExW(&windowClass);

            // On the screen the mouse is on, where the user just opened Queueify.
            GetCursorPos(out var cursor);
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            GetMonitorInfoW(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info);

            nint hwnd;
            lock (_lock)
            {
                if (_closing.IsSet) return;
                hwnd = CreateWindowExW(WS_EX_APPWINDOW, className, "Queueify", WS_POPUP,
                    info.rcWork.left, info.rcWork.top, 1, 1, 0, 0, instance, 0);
                if (hwnd == 0) return;
                _hwnd = hwnd;
            }

            // Sized for this screen's scaling, then centred on it.
            var scale = GetDpiForWindow(hwnd) / 96.0;
            int width = (int)(Width * scale), height = (int)(Height * scale);
            int x = info.rcWork.left + (info.rcWork.right - info.rcWork.left - width) / 2;
            int y = info.rcWork.top + (info.rcWork.bottom - info.rcWork.top - height) / 2;
            SetWindowPos(hwnd, 0, x, y, width, height, SWP_NOZORDER);
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int)); // Windows 11; ignored before
            ShowWindow(hwnd, SW_SHOWNORMAL);
            UpdateWindow(hwnd);

            MSG msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }

    // Layout in device-independent pixels.
    private const int Width = 400, Height = 150, IconSize = 64, Margin = 32, TitleSize = 28, StatusSize = 15;

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_PAINT:
                Paint(hwnd);
                return 0;
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private static void Paint(nint hwnd)
    {
        PAINTSTRUCT paint;
        var dc = BeginPaint(hwnd, &paint);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        int S(int dip) => (int)(dip * scale);
        GetClientRect(hwnd, out var client);

        // Windows 11's Mica-like base colours.
        uint background = s_dark ? Rgb(0x20, 0x20, 0x20) : Rgb(0xF3, 0xF3, 0xF3);
        uint primary = s_dark ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1A, 0x1A, 0x1A);
        uint secondary = s_dark ? Rgb(0xC5, 0xC5, 0xC5) : Rgb(0x5F, 0x5F, 0x5F);
        var brush = CreateSolidBrush(background);
        FillRect(dc, &client, brush);
        DeleteObject(brush);

        var icon = LoadImageW(GetModuleHandleW(null), IconId, IMAGE_ICON, S(IconSize), S(IconSize), 0);
        int iconTop = (client.bottom - S(IconSize)) / 2;
        if (icon != 0)
        {
            DrawIconEx(dc, S(Margin), iconTop, icon, S(IconSize), S(IconSize), 0, 0, DI_NORMAL);
            DestroyIcon(icon);
        }

        SetBkMode(dc, TRANSPARENT);
        int textLeft = S(Margin + IconSize + 20);
        var title = CreateFontW(-S(TitleSize), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI Variable Display");
        var status = CreateFontW(-S(StatusSize), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI Variable Text");
        var old = SelectObject(dc, title);
        SetTextColor(dc, primary);
        var titleRect = new RECT { left = textLeft, top = iconTop - S(2), right = client.right - S(Margin), bottom = iconTop + S(40) };
        DrawTextW(dc, "Queueify", -1, &titleRect, DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(dc, status);
        SetTextColor(dc, secondary);
        var statusRect = new RECT { left = textLeft, top = iconTop + S(40), right = client.right - S(Margin), bottom = client.bottom };
        DrawTextW(dc, s_status, -1, &statusRect, DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
        SelectObject(dc, old);
        DeleteObject(title);
        DeleteObject(status);
        EndPaint(hwnd, &paint);
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16);

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Win32 ──

    /// <summary>The exe's own icon: .NET puts the ApplicationIcon in as resource 32512.</summary>
    private const nint IconId = 32512;
    private const uint IMAGE_ICON = 1, LR_DEFAULTSIZE = 0x40, DI_NORMAL = 3;
    private const uint CS_DROPSHADOW = 0x20000, WS_POPUP = 0x80000000, WS_EX_APPWINDOW = 0x40000;
    private const uint WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    private const int SW_SHOWNORMAL = 1, TRANSPARENT = 1, FW_NORMAL = 400, FW_SEMIBOLD = 600;
    private const uint SWP_NOZORDER = 0x4, MONITOR_DEFAULTTONEAREST = 2, DEFAULT_CHARSET = 1, CLEARTYPE_QUALITY = 5;
    private const uint DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const nint IDC_APPSTARTING = 32650;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* paint);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndPaint(nint hwnd, PAINTSTRUCT* paint);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] private static partial int FillRect(nint dc, RECT* rect, nint brush);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int DrawTextW(nint dc, string text, int length, RECT* rect, uint format);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT point);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(POINT point, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [LibraryImport("user32.dll")] private static partial nint LoadImageW(nint instance, nint name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] private static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyIcon(nint icon);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, int size);
}
