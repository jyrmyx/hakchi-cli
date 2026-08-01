using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Hakchi.Cli;

/// <summary>
/// Packages a ROM (or zip containing one) into a hakchi-style CLV game folder.
/// Supports NES (.nes) and SNES (.sfc/.smc/.sfrom) including zip wrappers.
/// </summary>
internal static class GamePackager
{
    public const string DefaultNesEmulator = "/bin/clover-kachikachi-wr";
    public const string DefaultNesArgs =
        "--guest-overscan-dimensions 0,0,9,3 --initial-fadein-durations 3,2 --volume 75 --enable-armet";

    // Note: canoe expects "-rom" before the path (matches Windows SnesGame + console samples).
    public const string DefaultSnesEmulator = "/bin/clover-canoe-shvc-wr -rom";
    public const string DefaultSnesArgs = "--volume 100 -rollback-snapshot-period 600";

    public const string GamesMountPath = "/var/games";
    public const string GamesProfilePath = "/var/saves";

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly HashSet<string> NesExt = new(StringComparer.OrdinalIgnoreCase) { ".nes" };
    private static readonly HashSet<string> SnesExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sfc", ".smc", ".sfrom", ".fig", ".swc"
    };

    private static readonly HashSet<string> SupportedRomExt = new(
        NesExt.Concat(SnesExt), StringComparer.OrdinalIgnoreCase);

    public enum GameSystem { Nes, Snes }

    public static GamePackage Package(string inputPath, string? outputRoot = null, string? emulatorOverride = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            throw new FileNotFoundException("ROM or zip not found", inputPath);

        outputRoot = ResolveOutputRoot(outputRoot);

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        string romFileName;
        byte[] romData;
        string displaySourceName;

        if (ext is ".zip")
        {
            (romFileName, romData, displaySourceName) = ExtractRomFromZip(inputPath);
        }
        else if (SupportedRomExt.Contains(ext))
        {
            romData = File.ReadAllBytes(inputPath);
            romFileName = SanitizeFileName(Path.GetFileName(inputPath));
            displaySourceName = Path.GetFileNameWithoutExtension(inputPath);
        }
        else
        {
            throw new NotSupportedException(
                $"Unsupported file type '{ext}'. Supported: .nes, .sfc, .smc, .sfrom, or .zip containing one of those.");
        }

        var romExt = Path.GetExtension(romFileName).ToLowerInvariant();
        if (!SupportedRomExt.Contains(romExt))
            throw new NotSupportedException($"Unsupported ROM type '{romExt}'.");

        var system = DetectSystem(romExt);
        var name = CleanDisplayName(displaySourceName);
        byte saveCount = 0;
        uint crc;
        char prefix;
        string safeRomName;
        string emulator;
        string emuArgs;
        string systemLabel;
        string coreLabel;
        string iconAsset;
        string iconAssetSmall;

        if (system == GameSystem.Nes)
        {
            crc = Crc32NesOrFull(romData);
            prefix = 'H';
            safeRomName = SanitizeFileName(romFileName);
            saveCount = DetectNesBatterySaves(romData) ? (byte)3 : (byte)0;
            emulator = string.IsNullOrWhiteSpace(emulatorOverride) ? DefaultNesEmulator : emulatorOverride!;
            emuArgs = DefaultNesArgs;
            systemLabel = "Nintendo - Nintendo Entertainment System";
            coreLabel = "kachikachi";
            iconAsset = "blank_nes.png";
            iconAssetSmall = "blank_nes_small.png";
        }
        else
        {
            // SNES: CRC of (header-stripped) ROM body before SFROM wrap
            var body = romData;
            if (romExt != ".sfrom" && (body.Length % 1024) != 0 && body.Length > 512)
            {
                var stripped = new byte[body.Length - 512];
                Array.Copy(body, 512, stripped, 0, stripped.Length);
                body = stripped;
            }
            crc = romExt == ".sfrom" ? Crc32(romData) : Crc32(body);
            prefix = 'U';

            if (romExt == ".sfrom")
            {
                safeRomName = SanitizeFileName(Path.GetFileNameWithoutExtension(romFileName) + ".sfrom");
                romData = body;
                try
                {
                    // Best-effort SRAM guess from embedded header is hard; leave 0
                    saveCount = 0;
                }
                catch { saveCount = 0; }
            }
            else
            {
                romData = SnesSfrom.ConvertToSfrom(romData, out saveCount);
                safeRomName = SanitizeFileName(Path.GetFileNameWithoutExtension(romFileName) + ".sfrom");
            }

            emulator = string.IsNullOrWhiteSpace(emulatorOverride) ? DefaultSnesEmulator : emulatorOverride!;
            // If user passed bare binary without -rom, keep canoe convention when it looks like canoe
            if (emulator.Contains("canoe", StringComparison.OrdinalIgnoreCase) &&
                !emulator.Contains("-rom", StringComparison.Ordinal))
                emulator = emulator.TrimEnd() + " -rom";
            emuArgs = DefaultSnesArgs;
            systemLabel = "Nintendo - Super Nintendo Entertainment System";
            coreLabel = "canoe";
            iconAsset = "blank_snes-us.png";
            iconAssetSmall = "blank_snes-us_small.png";
        }

        var code = GenerateCode(crc, prefix);

        var packageDir = Path.Combine(outputRoot, code);
        if (Directory.Exists(packageDir))
            Directory.Delete(packageDir, recursive: true);
        Directory.CreateDirectory(packageDir);

        File.WriteAllBytes(Path.Combine(packageDir, safeRomName), romData);

        var iconName = code + ".png";
        File.WriteAllBytes(Path.Combine(packageDir, iconName), LoadIcon(iconAsset));
        var small = LoadIconOptional(iconAssetSmall);
        if (small != null)
            File.WriteAllBytes(Path.Combine(packageDir, code + "_small.png"), small);

        // Placeholder paths rewritten at upload for .storage layout
        var exec = BuildExec(emulator, $"{GamesMountPath}/{code}/{safeRomName}", emuArgs);

        var desktop = BuildDesktopFile(
            code: code,
            name: name,
            exec: exec,
            profilePath: GamesProfilePath,
            iconPath: GamesMountPath,
            iconFilename: iconName,
            sortTitle: name.ToLowerInvariant(),
            saveCount: saveCount,
            players: system == GameSystem.Snes ? (byte)2 : (byte)1,
            simultaneous: system == GameSystem.Snes);

        File.WriteAllText(Path.Combine(packageDir, code + ".desktop"), desktop, new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(packageDir, "metadata.json"),
            $$"""
            {
              "system": {{JsonString(systemLabel)}},
              "core": {{JsonString(coreLabel)}},
              "originalFilename": {{JsonString(Path.GetFileName(inputPath))}},
              "originalCrc32": {{crc}},
              "code": {{JsonString(code)}}
            }
            """,
            new UTF8Encoding(false));

        return new GamePackage(
            Code: code,
            Name: name,
            LocalDirectory: packageDir,
            RomFileName: safeRomName,
            DesktopFileName: code + ".desktop",
            Crc32: crc,
            Emulator: emulator,
            EmulatorArgs: emuArgs,
            System: system,
            SourcePath: inputPath);
    }

    public static void RewriteDesktopPaths(GamePackage package, string emulator, string? mediaBase = null)
    {
        mediaBase ??= GamesMountPath;
        mediaBase = mediaBase.TrimEnd('/');
        var desktopPath = Path.Combine(package.LocalDirectory, package.DesktopFileName);
        var romPath = $"{mediaBase}/{package.Code}/{package.RomFileName}";
        var iconPath = $"{mediaBase}/{package.Code}/{package.Code}.png";
        var exec = BuildExec(emulator, romPath, package.EmulatorArgs);

        var text = File.ReadAllText(desktopPath);
        text = Regex.Replace(text, @"^Exec=.*$", "Exec=" + exec, RegexOptions.Multiline);
        text = Regex.Replace(text, @"^Icon=.*$", "Icon=" + iconPath, RegexOptions.Multiline);
        File.WriteAllText(desktopPath, text, new UTF8Encoding(false));
    }

    public static void RewriteEmulator(GamePackage package, string emulator) =>
        RewriteDesktopPaths(package, emulator, GamesMountPath);

    public static string BuildExec(string emulator, string romPath, string args)
    {
        // emulator may already include flags like "-rom"
        return $"{emulator.Trim()} {romPath} {args}".Trim();
    }

    private static GameSystem DetectSystem(string romExt) =>
        NesExt.Contains(romExt) ? GameSystem.Nes : GameSystem.Snes;

    private static string ResolveOutputRoot(string? outputRoot)
    {
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            Directory.CreateDirectory(outputRoot);
            return outputRoot;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "share", "hakchi-cli", "game-packages"),
            Path.Combine(Path.GetTempPath(), "hakchi-cli", "game-packages"),
        };

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return dir;
            }
            catch { /* next */ }
        }

        throw new IOException("Could not find a writable directory for game packages.");
    }

    private static (string romFileName, byte[] data, string displayName) ExtractRomFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var romEntries = zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.EndsWith('/'))
            .Where(e => SupportedRomExt.Contains(Path.GetExtension(e.Name)))
            .ToList();

        ZipArchiveEntry entry;
        if (romEntries.Count == 1)
        {
            entry = romEntries[0];
        }
        else if (romEntries.Count == 0)
        {
            var all = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.EndsWith('/')).ToList();
            if (all.Count == 1 && SupportedRomExt.Contains(Path.GetExtension(all[0].Name)))
                entry = all[0];
            else if (all.Count == 1)
                throw new NotSupportedException(
                    $"Zip contains '{all[0].Name}' which is not a supported ROM.");
            else
                throw new InvalidOperationException(
                    "No supported ROM found inside the zip (.nes / .sfc / .smc / .sfrom).");
        }
        else
        {
            throw new InvalidOperationException(
                $"Multiple ROMs in zip ({romEntries.Count}); need exactly one. Found: " +
                string.Join(", ", romEntries.Select(e => e.FullName)));
        }

        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var name = Path.GetFileName(entry.Name);
        return (name, ms.ToArray(), Path.GetFileNameWithoutExtension(name));
    }

    public static string GenerateCode(uint crc32, char prefixCode)
    {
        return string.Format(
            "CLV-{5}-{0}{1}{2}{3}{4}",
            (char)('A' + (crc32 % 26)),
            (char)('A' + ((crc32 >> 5) % 26)),
            (char)('A' + ((crc32 >> 10) % 26)),
            (char)('A' + ((crc32 >> 15) % 26)),
            (char)('A' + ((crc32 >> 20) % 26)),
            prefixCode);
    }

    public static string CleanDisplayName(string name)
    {
        name = Regex.Replace(name, @" ?\(.*?\)| ?\[.*?\]", string.Empty);
        name = name.Replace('_', ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(name) ? "Unknown Game" : name;
    }

    public static string SanitizeFileName(string fileName)
    {
        var f = Path.GetFileNameWithoutExtension(fileName);
        var e = Path.GetExtension(fileName);
        f = Regex.Replace(f, @" ?\(.*?\)| ?\[.*?\]", string.Empty);
        f = Regex.Replace(f, @"[^A-Za-z0-9\.\!\-]+", "_");
        f = Regex.Replace(f, @"_+", "_");
        f = f.Trim('_', '.');
        if (string.IsNullOrEmpty(f)) f = "game";
        if (f.Length > 48) f = f[..48];
        return f + e.ToLowerInvariant();
    }

    private static uint Crc32NesOrFull(byte[] data)
    {
        if (data.Length > 16 && data[0] == 'N' && data[1] == 'E' && data[2] == 'S' && data[3] == 0x1A)
            return Crc32(data.AsSpan(16));
        return Crc32(data);
    }

    private static bool DetectNesBatterySaves(byte[] data)
    {
        if (data.Length > 16 && data[0] == 'N' && data[1] == 'E' && data[2] == 'S' && data[3] == 0x1A)
            return (data[6] & 0x02) != 0;
        return false;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & ~((crc & 1) - 1));
        }
        return ~crc;
    }

    private static byte[] LoadIcon(string fileName)
    {
        foreach (var path in IconCandidates(fileName))
        {
            try
            {
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch { /* next */ }
        }
        Trace.WriteLine($"Icon asset missing: {fileName}, using 1x1 placeholder");
        return TinyPng;
    }

    private static byte[]? LoadIconOptional(string fileName)
    {
        foreach (var path in IconCandidates(fileName))
        {
            try
            {
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch { /* next */ }
        }
        return null;
    }

    private static IEnumerable<string> IconCandidates(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "assets", fileName);
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets", fileName));
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "assets", fileName));
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "hakchi_gui", "images", fileName));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "hakchi_gui", "images", fileName));
    }

    private static string BuildDesktopFile(
        string code,
        string name,
        string exec,
        string profilePath,
        string iconPath,
        string iconFilename,
        string sortTitle,
        byte saveCount,
        byte players,
        bool simultaneous)
    {
        return
            $"[Desktop Entry]\n" +
            $"Type=Application\n" +
            $"Exec={exec}\n" +
            $"Path={profilePath}/{code}\n" +
            $"Name={name}\n" +
            $"CePrefix=\n" +
            $"Icon={iconPath}/{code}/{iconFilename}\n" +
            $"\n" +
            $"[X-CLOVER Game]\n" +
            $"Code={code}\n" +
            $"TestID=0\n" +
            $"ID=0\n" +
            $"Players={players}\n" +
            $"Simultaneous={(simultaneous ? 1 : 0)}\n" +
            $"ReleaseDate=\n" +
            $"SaveCount={saveCount}\n" +
            $"SortRawTitle={sortTitle}\n" +
            $"SortRawPublisher=UNKNOWN\n" +
            $"Copyright=\n" +
            $"\n" +
            $"[m2engage]\n" +
            $"regionTag=\n" +
            $"sortRawGenre=\n" +
            $"index=\n" +
            $"demo_time=\n" +
            $"country=\n" +
            $"\n" +
            $"[Description]\n" +
            $"Text = \n";
    }

    private static string JsonString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

internal sealed record GamePackage(
    string Code,
    string Name,
    string LocalDirectory,
    string RomFileName,
    string DesktopFileName,
    uint Crc32,
    string Emulator,
    string EmulatorArgs,
    GamePackager.GameSystem System,
    string SourcePath);
