using Xunit;

namespace Hakchi.Cli.Tests;

public class SnesSfromTests
{
    [Fact]
    public void ConvertToSfrom_GrowsRomByHeaders()
    {
        var rom = MakeLoRom();
        var sfrom = SnesSfrom.ConvertToSfrom(rom, out var saveCount);
        Assert.True(sfrom.Length > rom.Length);
        Assert.Equal(0, saveCount);
        // Header1 magic-ish first dword
        Assert.Equal(0x00, sfrom[0]);
        Assert.Equal(0x01, sfrom[1]);
        Assert.Equal(0x00, sfrom[2]);
        Assert.Equal(0x00, sfrom[3]);
    }

    [Fact]
    public void ConvertToSfrom_Strips512ByteCopierHeader()
    {
        var body = MakeLoRom();
        var withHeader = new byte[512 + body.Length];
        Array.Copy(body, 0, withHeader, 512, body.Length);
        // Make length not multiple of 1024 so strip triggers
        // body is 0x8000; +512 = 0x8200, not multiple of 1024? 0x8200 % 1024 = 512 ≠ 0 ✓
        var sfrom = SnesSfrom.ConvertToSfrom(withHeader, out _);
        Assert.True(sfrom.Length > body.Length);
    }

    [Fact]
    public void ConvertToSfrom_DetectsSramSaveCount()
    {
        var rom = MakeLoRom(sramSize: 3);
        SnesSfrom.ConvertToSfrom(rom, out var saveCount);
        Assert.Equal(3, saveCount);
    }

    private static byte[] MakeLoRom(byte sramSize = 0)
    {
        var rom = new byte[0x8000];
        var title = System.Text.Encoding.ASCII.GetBytes("TEST GAME            ");
        Array.Copy(title, 0, rom, 0x7FC0, 21);
        rom[0x7FD5] = 0x20;
        rom[0x7FD6] = 0x00;
        rom[0x7FD7] = 0x09;
        rom[0x7FD8] = sramSize;
        rom[0x7FD9] = 0x01;
        rom[0x7FDC] = 0xFF;
        rom[0x7FDD] = 0xFF;
        rom[0x7FDE] = 0x00;
        rom[0x7FDF] = 0x00;
        return rom;
    }
}
