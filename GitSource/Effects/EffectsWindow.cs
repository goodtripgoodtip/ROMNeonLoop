using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class EffectsWindow
{
    private const int WindowStyle = -16;
    private const int Caption = 0x00C00000;
    private const int Resizable = 0x00040000;
    private const int MaximizeBtn = 0x00010000;
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;
    private const int AsyncPlacement = 4;
    private const uint RefreshFrame = 0x0037;
    private const uint MoveFrame = 0x0014;
    private const uint RememberMonitor = 1u << 18;
    private const uint StartWindowState = 1u << 20;
    private const uint StartMaximized = 1u << 21;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length;
        public int Flags;
        public int Show;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect Normal;
    }

    internal static void Maximize(Process game, string gameFolder, DateTime startedUtc, string effects)
    {
        string log = Path.Combine(gameFolder, "launcher.log");
        var elapsed = Stopwatch.StartNew();
        var className = new StringBuilder(64);
        bool sawWindow = false;
        while (!game.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(60))
        {
            game.Refresh();
            IntPtr window = game.MainWindowHandle;
            sawWindow |= window != IntPtr.Zero;
            if (window != IntPtr.Zero && GetClassName(window, className, className.Capacity) > 0 &&
                string.Equals(className.ToString(), "UnityWndClass", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    int style = AddFrame(window);
                    SetRestoreSize(window, style);
                    int show = ApplyStartupOptions(window, effects);
                    Thread.Sleep(350);
                    if (!game.HasExited && !ShowWindowAsync(window, show))
                        WriteLog(log, "Windows did not apply the requested game window state. The game window is available.");
                }
                catch (Win32Exception error)
                {
                    WriteLog(log, "The game window opened, but Windows rejected placement: " + error.Message);
                }
                finally
                {
                    SetProp(window, "NeonLoop.StartupPlacementReady", new IntPtr(1));
                }
                if (!game.WaitForExit(750))
                    return;
                break;
            }
            Thread.Sleep(250);
        }
        if (!game.HasExited)
        {
            WriteLog(log, "The game is running. The launcher did not find a Unity window to maximize within 60 seconds.");
            return;
        }
        int code = game.ExitCode;
        WriteLog(log, "The game exited before window placement. Exit code 0x" + code.ToString("X8") + ".");
        if (code != 0 || !sawWindow)
            throw new InvalidOperationException("The game closed during startup (0x" + code.ToString("X8") +
                "). See launcher.log" + (ReShadeStarted(gameFolder, startedUtc) ? " and ReShade.log" : "") +
                " in " + gameFolder + ".");
    }

    private static int ApplyStartupOptions(IntPtr window, string effects)
    {
        uint flags;
        try
        {
            if (!uint.TryParse(File.ReadAllText(Path.Combine(effects, "GameplaySettings.txt")), out flags))
                return ShowMaximized;
        }
        catch (IOException) { return ShowMaximized; }
        catch (UnauthorizedAccessException) { return ShowMaximized; }

        bool rememberMonitor = (flags & RememberMonitor) != 0;
        bool chooseState = (flags & StartWindowState) != 0;
        if (!rememberMonitor && !chooseState)
            return ShowMaximized;

        string[] saved;
        try { saved = File.ReadAllLines(Path.Combine(effects, "GameplayWindow.txt")); }
        catch (IOException) { saved = new string[0]; }
        catch (UnauthorizedAccessException) { saved = new string[0]; }
        Screen monitor = Screen.FromHandle(window);
        if (rememberMonitor)
        {
            Screen match = Array.Find(Screen.AllScreens, screen =>
                saved.Length > 0 && string.Equals(screen.DeviceName, saved[0], StringComparison.OrdinalIgnoreCase));
            monitor = match ?? Screen.PrimaryScreen ?? monitor;
        }

        var area = monitor.WorkingArea;
        Rect rect;
        if (!GetWindowRect(window, out rect))
            throw new Win32Exception();
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int x = area.Left;
        int y = area.Top;
        if (chooseState && saved.Length > 1)
        {
            string[] bounds = saved[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int left, top, savedWidth, savedHeight;
            if (bounds.Length == 4 && int.TryParse(bounds[0], out left) && int.TryParse(bounds[1], out top) &&
                int.TryParse(bounds[2], out savedWidth) && int.TryParse(bounds[3], out savedHeight) &&
                savedWidth > 0 && savedHeight > 0)
            {
                width = savedWidth;
                height = savedHeight;
                if (string.Equals(monitor.DeviceName, saved[0], StringComparison.OrdinalIgnoreCase))
                {
                    x = left;
                    y = top;
                }
            }
        }
        width = Math.Min(Math.Max(width, 320), area.Width);
        height = Math.Min(Math.Max(height, 240), area.Height);
        x = Math.Min(Math.Max(x, area.Left), area.Right - width);
        y = Math.Min(Math.Max(y, area.Top), area.Bottom - height);
        if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, MoveFrame))
            throw new Win32Exception();
        return chooseState && (flags & StartMaximized) == 0 ? ShowNormal : ShowMaximized;
    }

    private static bool ReShadeStarted(string gameFolder, DateTime startedUtc)
    {
        string path = Path.Combine(gameFolder, "ReShade.log");
        return File.Exists(path) && File.GetLastWriteTimeUtc(path) >= startedUtc;
    }

    private static void WriteLog(string path, string message)
    {
        try { File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int AddFrame(IntPtr window)
    {
        int style = GetWindowLong(window, WindowStyle);
        int desired = style | Caption | Resizable | MaximizeBtn;
        if (style != desired &&
            (SetWindowLong(window, WindowStyle, desired) == 0 ||
            !SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, RefreshFrame)))
        {
            throw new Win32Exception();
        }
        return desired;
    }

    private static void SetRestoreSize(IntPtr window, int style)
    {
        var placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
        var frame = new Rect { Right = 1280, Bottom = 720 };
        if (!GetWindowPlacement(window, ref placement) ||
            !AdjustWindowRectEx(ref frame, style, false, GetWindowLong(window, -20)))
        {
            throw new Win32Exception();
        }
        placement.Normal.Right = placement.Normal.Left + frame.Right - frame.Left;
        placement.Normal.Bottom = placement.Normal.Top + frame.Bottom - frame.Top;
        placement.Show = ShowNormal;
        placement.Flags = AsyncPlacement;
        if (!SetWindowPlacement(window, ref placement))
            throw new Win32Exception();
    }

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out Rect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectEx(ref Rect bounds, int style, bool menu, int extendedStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);
}
