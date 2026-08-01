using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace Hakchi.Usb;

public sealed record UsbDeviceInfo(int Vid, int Pid, string? Name, string? Serial, ClassicUsbRole Role)
{
    public string Id => $"{Vid:X4}:{Pid:X4}";
    public bool IsClassicFamily => Role != ClassicUsbRole.Unknown;
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? $"{Id} ({Role})" : $"{Id}  {Name} ({Role})";
}

public enum ClassicUsbRole
{
    Unknown = 0,
    /// <summary>Allwinner FEL / burn / legacy clovershell bulk (VID 1F3A PID EFE8).</summary>
    FelOrClovershell,
    /// <summary>Running hakchi kernel USB gadget advertised as "hakchi"/"classic" — RNDIS network (VID 04E8 PID 6863).</summary>
    HakchiRndisGadget,
}

/// <summary>Known USB identities for Classic Mini / hakchi.</summary>
public static class ClassicIds
{
    /// <summary>FEL + classic clovershell bulk device.</summary>
    public const int FelVid = 0x1F3A;
    public const int FelPid = 0xEFE8;

    /// <summary>Running hakchi USB gadget (RNDIS). Seen on macOS as Product "classic", Vendor "hakchi".</summary>
    public const int RndisVid = 0x04E8;
    public const int RndisPid = 0x6863;

    // Back-compat aliases used by older call sites.
    public const int Vid = FelVid;
    public const int Pid = FelPid;

    public static ClassicUsbRole Classify(int vid, int pid)
    {
        if (vid == FelVid && pid == FelPid)
            return ClassicUsbRole.FelOrClovershell;
        if (vid == RndisVid && pid == RndisPid)
            return ClassicUsbRole.HakchiRndisGadget;
        return ClassicUsbRole.Unknown;
    }
}

public static class UsbEnumerator
{
    public static IReadOnlyList<UsbDeviceInfo> ListDevices()
    {
        LibUsbBootstrap.EnsureInitialized();
        var list = new List<UsbDeviceInfo>();

        try
        {
            // Note: avoid DeviceProperties / FullName on non-Windows backends —
            // MonoLibUsb paths can block or throw when opening descriptors.
            foreach (UsbRegistry reg in UsbDevice.AllDevices)
            {
                var role = ClassicIds.Classify(reg.Vid, reg.Pid);
                var name = role switch
                {
                    ClassicUsbRole.FelOrClovershell => "FEL / clovershell",
                    ClassicUsbRole.HakchiRndisGadget => "hakchi classic (RNDIS)",
                    _ => null
                };
                list.Add(new UsbDeviceInfo(reg.Vid, reg.Pid, name, Serial: null, role));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to enumerate USB devices. " + LibUsbBootstrap.DescribeNativeLibraryStatus(),
                ex);
        }

        return list
            .OrderByDescending(d => d.IsClassicFamily)
            .ThenBy(d => d.Vid)
            .ThenBy(d => d.Pid)
            .ToList();
    }

    public static bool ClassicDevicePresent() =>
        ListDevices().Any(d => d.IsClassicFamily);

    public static ClassicUsbRole DetectRole()
    {
        var devices = ListDevices();
        if (devices.Any(d => d.Role == ClassicUsbRole.FelOrClovershell))
            return ClassicUsbRole.FelOrClovershell;
        if (devices.Any(d => d.Role == ClassicUsbRole.HakchiRndisGadget))
            return ClassicUsbRole.HakchiRndisGadget;
        return ClassicUsbRole.Unknown;
    }
}
