using System;
using System.Runtime.InteropServices;

namespace C99.Helpers;

public class TrayIconHelper : IDisposable
{
    private const int NIM_ADD = 0;
    private const int NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint WM_TRAYICON = 0x8001;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const int CS_HREDRAW = 2;
    private const int CS_VREDRAW = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    private const uint MF_STRING = 0x00000000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint IDM_SHOW = 1;
    private const uint IDM_EXIT = 2;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly WndProcDelegate _wndProc;
    private IntPtr _hwnd;
    private bool _isClassRegistered;

    public event Action? OnDoubleClick;
    public event Action? OnShowRequest;
    public event Action? OnExitRequest;

    public TrayIconHelper()
    {
        _wndProc = WndProc;
        CreateMessageWindow();
    }

    private void CreateMessageWindow()
    {
        var hInstance = GetModuleHandle(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = "C99_TrayIconHelper"
        };

        var atom = RegisterClassExW(ref wc);
        if (atom == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _isClassRegistered = true;

        _hwnd = CreateWindowExW(0, "C99_TrayIconHelper", "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseEvent = (uint)lParam;
            if (mouseEvent == WM_LBUTTONDBLCLK)
                OnDoubleClick?.Invoke();
            else if (mouseEvent == WM_RBUTTONUP)
                ShowContextMenu();
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Show(string tooltip)
    {
        var hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = tooltip
        };

        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    public void Hide()
    {
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1
        };

        Shell_NotifyIconW(NIM_DELETE, ref nid);
    }

    private void ShowContextMenu()
    {
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);

        var hMenu = CreatePopupMenu();
        AppendMenuW(hMenu, MF_STRING, IDM_SHOW, "显示/隐藏");
        AppendMenuW(hMenu, MF_STRING, IDM_EXIT, "退出");

        var cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_LEFTALIGN,
            pt.x, pt.y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(hMenu);

        switch (cmd)
        {
            case IDM_SHOW:
                OnShowRequest?.Invoke();
                break;
            case IDM_EXIT:
                OnExitRequest?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_isClassRegistered)
        {
            UnregisterClassW("C99_TrayIconHelper", GetModuleHandle(null));
            _isClassRegistered = false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }
}
