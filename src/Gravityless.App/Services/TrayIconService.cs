using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Gravityless.App.Services;

public class TrayIconService : IDisposable
{
    private const int WM_USER = 0x0400;
    public const int WM_TRAY_CALLBACK = WM_USER + 2048;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Window _window;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _isCustomIcon;
    private bool _isDisposed;

    public Func<ContextMenu>? ContextMenuProvider { get; set; }
    public Action? DoubleClickAction { get; set; }

    public TrayIconService(Window window)
    {
        _window = window;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero)
        {
            Setup();
        }
        else
        {
            _window.SourceInitialized += (_, _) => Setup();
        }
    }

    private void Setup()
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero) return;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        CreateTrayIcon();
    }

    private void CreateTrayIcon()
    {
        _hIcon = LoadAppIcon(out _isCustomIcon);

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon = _hIcon,
            szTip = Tooltip,
            uTimeoutOrVersion = NOTIFYICON_VERSION_4
        };

        Shell_NotifyIcon(NIM_ADD, ref nid);
        Shell_NotifyIcon(NIM_SETVERSION, ref nid);
    }

    /// <summary>Tray tooltip text; follows the current language.</summary>
    public string Tooltip { get; private set; } = LocalizationService.GetString(
        "Tray_Tooltip", "Gravityless - Antigravity 编排与运行守护伴侣");

    public void UpdateTooltip(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Tooltip = text;
        if (_hwnd == IntPtr.Zero || _isDisposed) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_TIP,
            szTip = text
        };
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public void ShowBalloonNotification(string title, string text)
    {
        if (_hwnd == IntPtr.Zero || _isDisposed) return;
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_INFO,
            szInfoTitle = title,
            szInfo = text,
            dwInfoFlags = NIIF_INFO
        };
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    // Shell sends the mouse event id in the low word of lParam.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY_CALLBACK)
        {
            int eventId = lParam.ToInt32() & 0xFFFF;
            if (eventId == WM_LBUTTONDBLCLK || eventId == WM_LBUTTONUP)
            {
                DoubleClickAction?.Invoke();
                handled = true;
            }
            else if (eventId == WM_RBUTTONUP)
            {
                OnRightClick();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void OnRightClick()
    {
        if (ContextMenuProvider == null) return;
        var menu = ContextMenuProvider();
        if (menu == null) return;

        SetForegroundWindow(_hwnd);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // Fallback chain: Assets\tray.ico at the system small-icon size, then the exe icon, then IDI_APPLICATION.
    private static IntPtr LoadAppIcon(out bool isCustom)
    {
        isCustom = false;

        try
        {
            var trayIco = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
            if (File.Exists(trayIco))
            {
                var preferred = Math.Clamp(GetSystemMetrics(SM_CXSMICON), 16, 64);
                foreach (var size in new[] { preferred, 32, 24, 16 })
                {
                    var handle = LoadImage(IntPtr.Zero, trayIco, IMAGE_ICON, size, size, LR_LOADFROMFILE);
                    if (handle != IntPtr.Zero)
                    {
                        isCustom = true;
                        return handle;
                    }
                }
            }
        }
        catch { }

        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                IntPtr[] smallIcons = new IntPtr[1];
                if (ExtractIconEx(exePath, 0, null, smallIcons, 1) > 0 && smallIcons[0] != IntPtr.Zero)
                {
                    isCustom = true;
                    return smallIcons[0];
                }
            }
        }
        catch { }

        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
        }

        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        if (_isCustomIcon && _hIcon != IntPtr.Zero)
        {
            try
            {
                DestroyIcon(_hIcon);
            }
            catch { }
            _hIcon = IntPtr.Zero;
        }
    }
}
