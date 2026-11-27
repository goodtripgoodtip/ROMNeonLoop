using System;
using System.Runtime.InteropServices;

public static class OverlayInput
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MenuState();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StartupPrompt();

    private static MenuState readMenu;
    private static MenuState consumeHotspotToggle;
    private static bool promptPending;

    public static void TitleScreenShown(bool isTitleScreen)
    {
        if (!isTitleScreen)
            return;
        promptPending = true;
        NotifyStartupPrompt();
    }

    internal static void NotifyStartupPrompt()
    {
        if (!promptPending)
            return;
        IntPtr module = GetModuleHandle("ROMEffects.addon64");
        IntPtr export = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "ROMShowStartupPrompt");
        if (export == IntPtr.Zero)
            return;
        promptPending = false;
        ((StartupPrompt)Marshal.GetDelegateForFunctionPointer(export, typeof(StartupPrompt)))();
    }

    public static bool IsMenuOpen()
    {
        if (readMenu == null)
        {
            IntPtr module = GetModuleHandle("ROMEffects.addon64");
            if (module == IntPtr.Zero)
                return false;
            IntPtr function = GetProcAddress(module, "ROMEffectsMenuOpen");
            if (function == IntPtr.Zero)
                return false;
            readMenu = (MenuState)Marshal.GetDelegateForFunctionPointer(function, typeof(MenuState));
        }
        return readMenu() != 0;
    }

    internal static int ConsumeHotspotToggle()
    {
        if (consumeHotspotToggle == null)
        {
            IntPtr module = GetModuleHandle("ROMEffects.addon64");
            IntPtr export = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "ROMConsumeHotspotToggle");
            if (export == IntPtr.Zero)
                return -1;
            consumeHotspotToggle = (MenuState)Marshal.GetDelegateForFunctionPointer(export, typeof(MenuState));
        }
        return consumeHotspotToggle();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string file);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string function);
}
