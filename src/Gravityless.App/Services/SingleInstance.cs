using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Gravityless.App.Services;

/// <summary>
/// Enforces a single running instance and lets a second launch raise the existing window.
/// Uses a named mutex for ownership and a registered window message for the hand-off,
/// so no IPC channel or extra process is needed.
/// </summary>
internal static class SingleInstance
{
    /// <summary>Message broadcast by a secondary launch; every top-level window receives it.</summary>
    private static readonly int ActivateMessage =
        NativeMethods.RegisterWindowMessage("Gravityless.Activate.9F3C1A2E");

    private static Mutex? _mutex;

    /// <summary>
    /// Takes ownership of the instance mutex.
    /// Returns true when this process is the first instance and should continue starting.
    /// </summary>
    public static bool TryAcquire()
    {
        // Local\ scopes the mutex to the login session: separate users may each run their own copy.
        var name = $"Local\\Gravityless.Instance.{UserScope()}";

        _mutex = new Mutex(initiallyOwned: false, name, out _);

        try
        {
            // Zero timeout: a live instance holds the mutex for its whole lifetime.
            if (!_mutex.WaitOne(0))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing; this process now owns it.
            return true;
        }

        return true;
    }

    /// <summary>Asks the running instance to restore and focus its main window.</summary>
    public static void ActivateExisting()
    {
        if (ActivateMessage == 0)
        {
            return;
        }

        // HWND_BROADCAST reaches every top-level window; only our instance listens for this id.
        NativeMethods.PostMessage(
            NativeMethods.HWND_BROADCAST,
            ActivateMessage,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    /// <summary>Hooks the activation message on the given window. Call once the handle exists.</summary>
    public static void Listen(Window window, Action onActivate)
    {
        if (ActivateMessage == 0)
        {
            return;
        }

        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != ActivateMessage)
            {
                return IntPtr.Zero;
            }

            handled = true;
            onActivate();
            return IntPtr.Zero;
        });
    }

    /// <summary>Per-user suffix so two logged-in users each get their own single instance.</summary>
    private static string UserScope()
    {
        var raw = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
