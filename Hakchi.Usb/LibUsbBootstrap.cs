using System.Reflection;
using System.Runtime.InteropServices;
using LibUsbDotNet;

namespace Hakchi.Usb;

/// <summary>
/// Ensures Homebrew (and other common) libusb installs are loadable on macOS/Linux
/// before any LibUsbDotNet call touches native code.
/// </summary>
public static class LibUsbBootstrap
{
    private static int _initialized;
    private static readonly object Gate = new();

    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 1)
            return;

        lock (Gate)
        {
            if (_initialized == 1)
                return;

            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(UsbDevice).Assembly,
                    ResolveLibUsb);
            }
            catch (InvalidOperationException)
            {
                // Resolver already set for this assembly in this process.
            }

            // Eager-load so later failures are clearer.
            TryLoadLibUsb(out _);
            Volatile.Write(ref _initialized, 1);
        }
    }

    private static IntPtr ResolveLibUsb(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsLibUsbName(libraryName))
            return IntPtr.Zero;

        return TryLoadLibUsb(out var handle) ? handle : IntPtr.Zero;
    }

    private static bool IsLibUsbName(string name) =>
        name is "libusb-1.0" or "libusb-1.0.dylib" or "libusb-1.0.so" or "libusb-1.0.so.0" or "usb-1.0";

    public static bool TryLoadLibUsb(out IntPtr handle)
    {
        foreach (var path in CandidatePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                return true;
        }

        // Fall back to default lookup (PATH / dyld default paths).
        if (NativeLibrary.TryLoad("libusb-1.0", out handle))
            return true;
        if (NativeLibrary.TryLoad("usb-1.0", out handle))
            return true;

        handle = IntPtr.Zero;
        return false;
    }

    public static IEnumerable<string> CandidatePaths()
    {
        // Prefer native lib shipped next to the binary (downloadable releases).
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            yield return Path.Combine(exeDir, "libusb-1.0.dylib");
            yield return Path.Combine(exeDir, "libusb-1.0.so");
            yield return Path.Combine(exeDir, "libusb-1.0.so.0");
            yield return Path.Combine(exeDir, "runtimes", "native", "libusb-1.0.dylib");
            yield return Path.Combine(exeDir, "runtimes", "native", "libusb-1.0.so.0");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dylib");
        yield return Path.Combine(AppContext.BaseDirectory, "libusb-1.0.so");
        yield return Path.Combine(AppContext.BaseDirectory, "libusb-1.0.so.0");

        // Dev machines: Homebrew / system packages
        var homebrew = Environment.GetEnvironmentVariable("HOMEBREW_PREFIX") ?? "/opt/homebrew";
        yield return Path.Combine(homebrew, "lib", "libusb-1.0.dylib");
        yield return "/usr/local/lib/libusb-1.0.dylib";
        yield return "/opt/local/lib/libusb-1.0.dylib";
        yield return "/usr/lib/x86_64-linux-gnu/libusb-1.0.so.0";
        yield return "/usr/lib/aarch64-linux-gnu/libusb-1.0.so.0";
        yield return "/usr/lib/libusb-1.0.so.0";
    }

    public static string DescribeNativeLibraryStatus()
    {
        EnsureInitialized();
        if (TryLoadLibUsb(out var handle) && handle != IntPtr.Zero)
            return "libusb-1.0 loaded";

        var tried = string.Join(", ", CandidatePaths().Where(File.Exists));
        return tried.Length == 0
            ? "libusb-1.0 not found (install with: brew install libusb)"
            : $"libusb-1.0 present on disk but failed to load (candidates: {tried})";
    }

    /// <summary>
    /// Stop LibUsbDotNet's Mono event thread so the process can exit cleanly on macOS/Linux.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            UsbDevice.Exit();
        }
        catch
        {
            // Best-effort; some backends throw if never fully initialized.
        }
    }
}

