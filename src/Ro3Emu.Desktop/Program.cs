using System.Runtime.InteropServices;
using Avalonia;

namespace Ro3Emu.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
        // Under Wine the GPU path (ANGLE on Direct3D) often draws nothing; software rendering always works and this
        // window is small. --software forces it elsewhere too.
        if (OperatingSystem.IsWindows() && (UnderWine() || Environment.GetCommandLineArgs().Contains("--software")))
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        return builder;
    }

    /// <summary>Wine's ntdll exports wine_get_version; Windows' does not.</summary>
    internal static bool UnderWine()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var ntdll = GetModuleHandle("ntdll.dll");
            return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
        }
        catch (Exception) { return false; }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string name);
}
