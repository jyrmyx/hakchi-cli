using System.IO.Compression;
using Xunit;

namespace Hakchi.Cli.Tests;

public class GamePackagerTests
{
    [Fact]
    public void GenerateCode_IsStableForSameCrc()
    {
        var a = GamePackager.GenerateCode(0xEFB09075u, 'H');
        var b = GamePackager.GenerateCode(0xEFB09075u, 'H');
        Assert.Equal(a, b);
        Assert.StartsWith("CLV-H-", a);
        Assert.Equal(11, a.Length); // CLV-H-XXXXX
    }

    [Fact]
    public void GenerateCode_PrefixChangesCode()
    {
        var nes = GamePackager.GenerateCode(0x12345678u, 'H');
        var snes = GamePackager.GenerateCode(0x12345678u, 'U');
        Assert.StartsWith("CLV-H-", nes);
        Assert.StartsWith("CLV-U-", snes);
        Assert.NotEqual(nes, snes);
    }

    [Theory]
    [InlineData("DuckTales (USA)", "DuckTales")]
    [InlineData("Mortal_Kombat_II (USA) (Rev 1)", "Mortal Kombat II")]
    [InlineData("  foo  [!]", "foo")]
    public void CleanDisplayName_StripsRegionTags(string input, string expected)
    {
        Assert.Equal(expected, GamePackager.CleanDisplayName(input));
    }

    [Fact]
    public void Package_NesRom_CreatesDesktopAndRom()
    {
        using var tmp = new TempDir();
        var nesPath = Path.Combine(tmp.Path, "Demo (USA).nes");
        File.WriteAllBytes(nesPath, MakeMinimalINes());

        var pkg = GamePackager.Package(nesPath, outputRoot: tmp.Path);
        Assert.Equal(GamePackager.GameSystem.Nes, pkg.System);
        Assert.StartsWith("CLV-H-", pkg.Code);
        Assert.Equal("Demo", pkg.Name);
        Assert.True(File.Exists(Path.Combine(pkg.LocalDirectory, pkg.RomFileName)));
        Assert.True(File.Exists(Path.Combine(pkg.LocalDirectory, pkg.DesktopFileName)));
        var desktop = File.ReadAllText(Path.Combine(pkg.LocalDirectory, pkg.DesktopFileName));
        Assert.Contains("Name=Demo", desktop);
        Assert.Contains("clover-kachikachi-wr", desktop);
    }

    [Fact]
    public void Package_SnesRom_ConvertsToSfrom()
    {
        using var tmp = new TempDir();
        var sfcPath = Path.Combine(tmp.Path, "Fighter (USA).sfc");
        File.WriteAllBytes(sfcPath, MakeMinimalSnesRom());

        var pkg = GamePackager.Package(sfcPath, outputRoot: tmp.Path);
        Assert.Equal(GamePackager.GameSystem.Snes, pkg.System);
        Assert.StartsWith("CLV-U-", pkg.Code);
        Assert.EndsWith(".sfrom", pkg.RomFileName);
        Assert.True(new FileInfo(Path.Combine(pkg.LocalDirectory, pkg.RomFileName)).Length >
                    new FileInfo(sfcPath).Length);
        var desktop = File.ReadAllText(Path.Combine(pkg.LocalDirectory, pkg.DesktopFileName));
        Assert.Contains("clover-canoe-shvc-wr", desktop);
        Assert.Contains("-rom", desktop);
    }

    [Fact]
    public void Package_ZipWithSingleNes_Works()
    {
        using var tmp = new TempDir();
        var nes = Path.Combine(tmp.Path, "inner.nes");
        File.WriteAllBytes(nes, MakeMinimalINes(seed: 42));
        var zip = Path.Combine(tmp.Path, "game.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            z.CreateEntryFromFile(nes, "Cool Game (USA).nes");

        var pkg = GamePackager.Package(zip, outputRoot: tmp.Path);
        Assert.Equal("Cool Game", pkg.Name);
        Assert.Equal(GamePackager.GameSystem.Nes, pkg.System);
    }

    private static byte[] MakeMinimalINes(byte seed = 1)
    {
        // 16-byte iNES header + 16 KiB PRG
        var data = new byte[16 + 16384];
        data[0] = (byte)'N';
        data[1] = (byte)'E';
        data[2] = (byte)'S';
        data[3] = 0x1A;
        data[4] = 1; // 1 × 16KB PRG
        for (int i = 16; i < data.Length; i++)
            data[i] = (byte)(seed + i);
        return data;
    }

    /// <summary>32 KiB LoROM image with a header that passes checksum complementarity.</summary>
    private static byte[] MakeMinimalSnesRom()
    {
        var rom = new byte[0x8000];
        // Title at 0x7FC0 (21 bytes), padded with spaces
        var title = System.Text.Encoding.ASCII.GetBytes("TEST GAME            ");
        Array.Copy(title, 0, rom, 0x7FC0, 21);
        rom[0x7FD5] = 0x20; // map mode-ish
        rom[0x7FD6] = 0x00; // ROM type
        rom[0x7FD7] = 0x09; // size
        rom[0x7FD8] = 0x00; // SRAM
        rom[0x7FD9] = 0x01; // country
        // Checksum / complement: any pair with XOR 0xFFFF
        rom[0x7FDC] = 0xFF; // complement lo
        rom[0x7FDD] = 0xFF; // complement hi
        rom[0x7FDE] = 0x00; // checksum lo
        rom[0x7FDF] = 0x00; // checksum hi
        // 0x0000 ^ 0xFFFF == 0xFFFF ✓
        return rom;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hakchi-cli-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }
}
