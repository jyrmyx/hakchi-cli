using System;
using System.Text;

namespace FelLib;

/// <summary>
/// RAM-only memboot helpers (no NAND write).
/// Uploads an Android boot.img into DRAM via FEL and runs u-boot "boota".
/// </summary>
public static class Memboot
{
    public static uint CalcAndroidBootImageSize(byte[] header)
    {
        if (header.Length < 44 || Encoding.ASCII.GetString(header, 0, 8) != "ANDROID!")
            throw new Exception("Invalid Android boot image header");

        uint kernelSize = BitConverter.ToUInt32(header, 8);
        uint ramdiskSize = BitConverter.ToUInt32(header, 16);
        uint secondSize = BitConverter.ToUInt32(header, 24);
        uint pageSize = BitConverter.ToUInt32(header, 36);
        uint dtSize = BitConverter.ToUInt32(header, 40);

        uint pages = 1;
        pages += (kernelSize + pageSize - 1) / pageSize;
        pages += (ramdiskSize + pageSize - 1) / pageSize;
        pages += (secondSize + pageSize - 1) / pageSize;
        pages += (dtSize + pageSize - 1) / pageSize;
        return pages * pageSize;
    }

    /// <param name="log">Optional progress/log sink</param>
    public static void BootFromRam(
        byte[] fes1,
        byte[] uboot,
        byte[] bootImage,
        Action<string>? log = null)
    {
        using var fel = new Fel();
        fel.WriteLine += msg => log?.Invoke(msg);
        fel.Fes1Bin = fes1;
        fel.UBootBin = uboot;

        if (!fel.Open(isFel: true))
            throw new Exception("Could not open device in FEL mode (need 1F3A:EFE8)");

        var size = CalcAndroidBootImageSize(bootImage);
        if (size > bootImage.Length || size > Fel.transfer_max_size)
            throw new Exception($"Invalid boot image size: {size} (file={bootImage.Length})");

        // Pad to sector multiple like upstream FelHelpers
        size = (size + Fel.sector_size - 1) / Fel.sector_size * Fel.sector_size;
        if (bootImage.Length != size)
        {
            var padded = new byte[size];
            Array.Copy(bootImage, padded, bootImage.Length);
            bootImage = padded;
        }

        log?.Invoke($"Uploading boot image ({bootImage.Length / 1024} KiB) to RAM @ 0x{Fel.transfer_base_m:X}…");
        long chunks = 0;
        long maxChunks = Math.Max(1, bootImage.Length / 65536);
        fel.WriteMemory(Fel.transfer_base_m, bootImage, (action, _) =>
        {
            if (action == Fel.CurrentAction.WritingMemory)
            {
                chunks++;
                if (chunks % 8 == 0 || chunks >= maxChunks)
                    log?.Invoke($"  upload progress ~{Math.Min(100, chunks * 100 / maxChunks)}%");
            }
        });

        var cmd = $"boota {Fel.transfer_base_m:x}";
        log?.Invoke($"Running u-boot command (no return): {cmd}");
        // noreturn: device leaves FEL and boots the image from RAM
        fel.RunUbootCmd(cmd, noreturn: true);
        log?.Invoke("Memboot issued — device should leave FEL and start the recovery/shell image.");
    }
}
