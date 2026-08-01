using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hakchi.Rndis;
using Hakchi.Usb;
using Renci.SshNet;
using Spectre.Console;

namespace Hakchi.Cli;

/// <summary>
/// Session over USB RNDIS using a userspace stack.
/// Hakchi S92rndis assigns the Classic fixed link-local: 169.254.13.37/16 on rndis0.
/// Upload path is add-only (single game package); never full library sync.
/// </summary>
internal sealed class DeviceSession : IDisposable
{
    // From hakchi rootfs /etc/init.d/S92rndis
    public static readonly IPAddress DeviceIp = IPAddress.Parse("169.254.13.37");
    public static readonly IPAddress HostIp = IPAddress.Parse("169.254.13.1");

    private RndisDevice? _rndis;
    private LocalTcpRelay? _relay;
    private SshClient? _ssh;

    public string? LocalAddress { get; private set; }
    public string? RemoteAddress { get; private set; }

    public void Connect(int timeoutSeconds = 25)
    {
        if (UsbEnumerator.DetectRole() != ClassicUsbRole.HakchiRndisGadget)
            throw new InvalidOperationException("No hakchi RNDIS gadget (04E8:6863). Power the Classic on with hakchi installed.");

        AnsiConsole.MarkupLine("[grey]Opening RNDIS (userspace)…[/]");
        _rndis = new RndisDevice();
        _rndis.Open();
        AnsiConsole.MarkupLine(
            $"[grey]Device MAC {RndisDevice.MacToString(_rndis.DeviceMac)} · Host MAC {RndisDevice.MacToString(_rndis.HostMac)}[/]");

        AnsiConsole.MarkupLine($"[grey]ARP for {DeviceIp}…[/]");
        var peerMac = ArpResolve(_rndis, HostIp, DeviceIp, _rndis.HostMac, TimeSpan.FromSeconds(Math.Min(8, timeoutSeconds)))
            ?? throw new InvalidOperationException($"No ARP reply from {DeviceIp}. Is hakchi network shell up?");

        AnsiConsole.MarkupLine($"[green]ARP OK[/] peer MAC {RndisDevice.MacToString(peerMac)}");
        LocalAddress = HostIp.ToString();
        RemoteAddress = DeviceIp.ToString();

        AnsiConsole.MarkupLine($"[grey]TCP → {DeviceIp}:22 …[/]");
        var tcp = new UserspaceTcpClient(_rndis, HostIp, DeviceIp, peerMac);
        tcp.Connect(22, timeoutMs: Math.Max(8000, timeoutSeconds * 400));

        _relay = new LocalTcpRelay(tcp);
        _relay.Start();
        Thread.Sleep(150);

        AnsiConsole.MarkupLine($"[grey]SSH via 127.0.0.1:{_relay.LocalPort}…[/]");
        Exception? last = null;
        foreach (var password in new[] { "", "root", "clover" })
        {
            try
            {
                _ssh = new SshClient("127.0.0.1", _relay.LocalPort, "root", password);
                // Userspace TCP is slow/lossy vs a real NIC — be patient.
                _ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds));
                _ssh.KeepAliveInterval = TimeSpan.FromSeconds(10);
                _ssh.Connect();
                if (_ssh.IsConnected)
                {
                    AnsiConsole.MarkupLine($"[green]SSH connected[/] (password {(password.Length == 0 ? "empty" : "set")}).");
                    last = null;
                    break;
                }
            }
            catch (Exception ex)
            {
                last = ex;
                try { _ssh?.Dispose(); } catch { }
                _ssh = null;
            }
        }
        if (_ssh == null || !_ssh.IsConnected)
            throw new InvalidOperationException("SSH failed: " + last?.Message);

        // Warm-up: tiny exec proves the channel path works before big scripts/uploads.
        try
        {
            var pong = Run("echo HAKCHI_OK", timeoutMs: 20000).Trim();
            if (!pong.Contains("HAKCHI_OK", StringComparison.Ordinal))
                AnsiConsole.MarkupLine($"[yellow]SSH warm-up odd reply:[/] {Markup.Escape(pong)}");
            else
                AnsiConsole.MarkupLine("[grey]SSH channel OK.[/]");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "SSH connected but first command timed out. Userspace USB TCP may be stalled. Retry once; if it keeps failing, reboot the Classic.",
                ex);
        }
    }

    private static byte[]? ArpResolve(RndisDevice rndis, IPAddress localIp, IPAddress targetIp, byte[] localMac, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var spa = localIp.GetAddressBytes();
        var tpa = targetIp.GetAddressBytes();
        var buf = new byte[2048];

        while (DateTime.UtcNow < deadline)
        {
            var arp = new byte[42];
            for (int i = 0; i < 6; i++) arp[i] = 0xff;
            Array.Copy(localMac, 0, arp, 6, 6);
            arp[12] = 0x08; arp[13] = 0x06;
            arp[14] = 0; arp[15] = 1; arp[16] = 0x08; arp[17] = 0; arp[18] = 6; arp[19] = 4;
            arp[20] = 0; arp[21] = 1;
            Array.Copy(localMac, 0, arp, 22, 6);
            Array.Copy(spa, 0, arp, 28, 4);
            Array.Copy(tpa, 0, arp, 38, 4);
            try { rndis.WriteEthernetFrame(arp); } catch { }

            var end = DateTime.UtcNow.AddMilliseconds(400);
            while (DateTime.UtcNow < end)
            {
                if (!rndis.TryReadEthernetFrame(buf, out var len, 50) || len < 42)
                    continue;
                if (buf[12] != 0x08 || buf[13] != 0x06) continue;
                var op = (buf[20] << 8) | buf[21];
                if (op != 2) continue;
                if (buf[28] == tpa[0] && buf[29] == tpa[1] && buf[30] == tpa[2] && buf[31] == tpa[3])
                {
                    var mac = new byte[6];
                    Array.Copy(buf, 22, mac, 0, 6);
                    return mac;
                }
            }
        }
        return null;
    }

    public string Run(string command, int timeoutMs = 60000, bool allowDestructive = false)
    {
        if (_ssh == null || !_ssh.IsConnected)
            throw new InvalidOperationException("Not connected");

        if (!allowDestructive)
        {
            var lower = command.ToLowerInvariant();
            string[] banned =
            {
                "hakchi sync", "sync games", "rm -rf", "mkfs", "nandwrite",
                "fdisk", "parted", "poweroff", "reboot"
            };
            foreach (var b in banned)
            {
                if (lower.Contains(b))
                    throw new InvalidOperationException($"Refusing potentially destructive command: {command}");
            }
        }

        // Prefer short commands — long `echo BASE64 | sh` lines blow our USB MTU path.
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var cmd = _ssh.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMs);
                var result = cmd.Execute();
                if (!string.IsNullOrEmpty(cmd.Error))
                    Trace.WriteLine("ssh stderr: " + cmd.Error);
                return result;
            }
            catch (Exception ex) when (attempt < 2)
            {
                last = ex;
                AnsiConsole.MarkupLine($"[yellow]SSH command retry ({Markup.Escape(ex.GetType().Name)})…[/]");
                Thread.Sleep(400);
            }
        }
        throw last ?? new InvalidOperationException("SSH Run failed");
    }

    /// <summary>Run a shell script by piping it on stdin (avoids huge exec argv over USB TCP).</summary>
    public string RunScript(string script, int timeoutMs = 180000, bool allowDestructive = false)
    {
        if (!allowDestructive)
        {
            var lower = script.ToLowerInvariant();
            string[] banned =
            {
                "hakchi sync", "sync games", "mkfs", "nandwrite",
                "fdisk", "parted", "poweroff", "reboot"
            };
            foreach (var b in banned)
            {
                if (lower.Contains(b))
                    throw new InvalidOperationException($"Refusing potentially destructive script content: {b}");
            }
            // Allow targeted rm -rf only when the caller set allowDestructive on Run.
            if (lower.Contains("rm -rf") && !allowDestructive)
                throw new InvalidOperationException("Refusing rm -rf in script without allowDestructive.");
        }

        var bytes = Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"));
        using var ms = new MemoryStream(bytes);
        return RunWithStdin("sh", ms, timeoutMs, requireZeroExit: false);
    }

    /// <summary>Run remote command with stdin stream (for tar upload / cat &gt; file).</summary>
    public string RunWithStdin(string command, Stream stdin, int timeoutMs = 600000, bool requireZeroExit = true)
    {
        if (_ssh == null || !_ssh.IsConnected)
            throw new InvalidOperationException("Not connected");

        using var cmd = _ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMs);
        var asyncResult = cmd.BeginExecute();
        using (var input = cmd.CreateInputStream())
        {
            // Copy in modest chunks so userspace TCP can segment + ACK
            var buf = new byte[4 * 1024];
            int n;
            while ((n = stdin.Read(buf, 0, buf.Length)) > 0)
                input.Write(buf, 0, n);
        }
        var result = cmd.EndExecute(asyncResult);
        if (!string.IsNullOrEmpty(cmd.Error))
            Trace.WriteLine("ssh stderr: " + cmd.Error);
        if (requireZeroExit && cmd.ExitStatus != 0)
            throw new InvalidOperationException(
                $"Remote command failed (exit {cmd.ExitStatus}): {command}\n{cmd.Error}\n{result}");
        return result;
    }

    public IReadOnlyList<GameEntry> ListGames()
    {
        var script = """
            set +e
            ROOTFS=$(hakchi get rootfs 2>/dev/null)
            echo "META rootfs=$ROOTFS"
            echo "META board=$(hakchi get board 2>/dev/null || true)"
            echo "META region=$(hakchi get region 2>/dev/null || true)"
            PATHS=""
            for p in \
              "$ROOTFS/var/lib/hakchi/games" \
              /var/lib/hakchi/games \
              /var/games \
              /media/hakchi/games \
              /media/data/games \
              /usr/share/games
            do
              [ -d "$p" ] && PATHS="$PATHS $p"
            done
            echo "META paths=$PATHS"
            find $PATHS -type f -name 'CLV-*.desktop' 2>/dev/null | sort | while read -r f; do
              code=$(basename "$f" .desktop)
              name=$(sed -n 's/^Name=//p' "$f" 2>/dev/null | head -n1 | tr -d '\r')
              printf 'GAME\t%s\t%s\t%s\n' "$code" "$name" "$f"
            done
            """;

        var raw = RunScript(script, timeoutMs: 180000);

        var games = new List<GameEntry>();
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("META ", StringComparison.Ordinal))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
            if (!line.StartsWith("GAME\t", StringComparison.Ordinal))
                continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            games.Add(new GameEntry(parts[1], parts[2], parts.Length > 3 ? parts[3] : ""));
        }
        return games;
    }

    public ConsoleGamesLayout ProbeGamesLayout()
    {
        var script = """
            set +e
            SYNC=$(hakchi findGameSyncStorage 2>/dev/null)
            GAMEPATH=$(hakchi get gamepath 2>/dev/null)
            ROOTFS=$(hakchi get rootfs 2>/dev/null)
            BOARD=$(hakchi get board 2>/dev/null)
            REGION=$(hakchi get region 2>/dev/null)
            echo "META sync=$SYNC"
            echo "META gamepath=$GAMEPATH"
            echo "META rootfs=$ROOTFS"
            echo "META board=$BOARD"
            echo "META region=$REGION"

            # Prefer real storage under hakchi, not the live overmount
            CAND=""
            for p in "$SYNC" /var/lib/hakchi/games "$ROOTFS/var/lib/hakchi/games" /media/hakchi/games /media/data/games; do
              [ -n "$p" ] && [ -d "$p" ] && CAND="$CAND $p"
            done
            echo "META candidates=$CAND"

            # Pick the candidate that already holds the most CLV desktops
            BEST=""
            BESTN=-1
            for p in $CAND; do
              n=$(find "$p" -type f -name 'CLV-*.desktop' 2>/dev/null | wc -l | tr -d ' ')
              echo "META count $p = $n"
              if [ "$n" -gt "$BESTN" ]; then BESTN=$n; BEST=$p; fi
            done
            [ -z "$BEST" ] && BEST=${SYNC:-/var/lib/hakchi/games}
            echo "META best=$BEST"

            # If BEST has system subdirs (nes-usa, snes-usa, …) with games, prefer busiest
            SUBBEST=""
            SUBN=-1
            for d in "$BEST"/*/; do
              [ -d "$d" ] || continue
              base=$(basename "$d")
              case "$base" in
                .storage|lost+found) continue ;;
              esac
              n=$(find "$d" -type f -name 'CLV-*.desktop' 2>/dev/null | wc -l | tr -d ' ')
              if [ "$n" -gt 0 ]; then
                echo "META sub $d = $n"
                if [ "$n" -gt "$SUBN" ]; then SUBN=$n; SUBBEST=${d%/}; fi
              fi
            done
            TARGET=${SUBBEST:-$BEST}
            echo "META target=$TARGET"

            # Numbered menu pages
            MENUS=""
            for d in "$TARGET"/[0-9][0-9][0-9]; do
              [ -d "$d" ] || continue
              base=$(basename "$d")
              MENUS="$MENUS $base"
              gc=$(find "$d" -maxdepth 2 \( -type d -o -type l \) -name 'CLV-*' ! -name 'CLV-S-*' 2>/dev/null | wc -l | tr -d ' ')
              echo "META foldercount $base = $gc"
            done
            echo "META menus=$MENUS"

            # Folder shortcuts: CLV-S-0000N → menu page NNN (chmenu target), Name= label
            find "$TARGET" -type f -name 'CLV-S-*.desktop' 2>/dev/null | sort | while read -r fd; do
              code=$(basename "$fd" .desktop)
              idx=$(echo "$code" | sed -n 's/^CLV-S-0*//p')
              [ -z "$idx" ] && continue
              page=$(printf '%03d' "$idx" 2>/dev/null) || page=$(printf '%03d' "$idx")
              nm=$(sed -n 's/^Name=//p' "$fd" 2>/dev/null | head -n1 | tr -d '\r')
              echo "META folderlabel $page	$nm"
              echo "META foldersrc $page	$fd"
            done

            if [ -d "$TARGET/.storage" ]; then echo "META storage=yes"; else echo "META storage=no"; fi
            find "$TARGET" \( -type d -o -type l -o -type f \) -name 'CLV-H-JLKBN*' 2>/dev/null | head -n10 | while read -r x; do
              echo "META found $x"
            done

            # Free space (busybox df: prefer -k, fall back)
            df -k "$TARGET" 2>/dev/null | awk 'NR==2 && $4 ~ /^[0-9]+$/ {print "META df_avail_kb="$4}'
            df -P "$TARGET" 2>/dev/null | awk 'NR==2 && $4 ~ /^[0-9]+$/ {print "META df_avail_kb="$4}'

            # Emulator probes (NES + SNES)
            for b in /bin/clover-kachikachi-wr /usr/bin/clover-kachikachi /bin/nes /bin/fceumm /bin/nestopia \
                     /bin/clover-canoe-shvc-wr /usr/bin/clover-canoe-shvc /bin/snes /bin/snes9x /bin/snes9x2010; do
              if [ -e "$b" ] || [ -L "$b" ]; then echo "META emu $b"; fi
            done
            for b in nes fceumm clover-kachikachi-wr clover-canoe-shvc-wr snes snes9x; do
              w=$(command -v "$b" 2>/dev/null)
              [ -n "$w" ] && echo "META which $w"
            done

            # Sample a CLV-H (NES custom) desktop if present; else any desktop.
            # Note: folders like nes-usa/ are hakchi "system storage" labels — mixed
            # consoles (e.g. SNES CLV-U-*) are often stored under the same tree.
            sample=$(find "$TARGET" -type f -name 'CLV-H-*.desktop' 2>/dev/null | head -n1)
            [ -z "$sample" ] && sample=$(find "$TARGET" -type f -name 'CLV-*.desktop' 2>/dev/null | head -n1)
            if [ -n "$sample" ]; then
              echo "META sample=$sample"
              sed -n 's/^Exec=/META exec=/p' "$sample" | head -n1
            fi
            echo "META note=system_folder_is_storage_label_not_rom_type_filter"
            """;

        // Pipe script on stdin — never put multi-KB base64 on the SSH exec line.
        var raw = RunScript(script, timeoutMs: 180000);

        var layout = new ConsoleGamesLayout();
        var menus = new List<string>();
        var emus = new List<string>();

        foreach (var line in raw.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (!t.StartsWith("META ", StringComparison.Ordinal)) continue;
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(t)}[/]");
            var body = t["META ".Length..];

            if (body.StartsWith("target=", StringComparison.Ordinal))
                layout.TargetRoot = body["target=".Length..].Trim();
            else if (body.StartsWith("sync=", StringComparison.Ordinal))
                layout.SyncStorage = body["sync=".Length..].Trim();
            else if (body.StartsWith("gamepath=", StringComparison.Ordinal))
                layout.GamePath = body["gamepath=".Length..].Trim();
            else if (body.StartsWith("menus=", StringComparison.Ordinal))
            {
                menus.AddRange(body["menus=".Length..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (body.StartsWith("folderlabel ", StringComparison.Ordinal))
            {
                // "folderlabel 001\tAKU - NIN"
                var rest = body["folderlabel ".Length..];
                var tab = rest.Split('\t', 2);
                if (tab.Length == 2)
                    layout.FolderLabels[tab[0].Trim()] = tab[1].Trim();
            }
            else if (body.StartsWith("storage=", StringComparison.Ordinal))
                layout.HasDotStorage = body.Contains("yes", StringComparison.OrdinalIgnoreCase);
            else if (body.StartsWith("df_avail_kb=", StringComparison.Ordinal) &&
                     long.TryParse(body["df_avail_kb=".Length..].Trim(), out var kb))
                layout.AvailableKb = kb;
            else if (body.StartsWith("emu ", StringComparison.Ordinal))
                emus.Add(body["emu ".Length..].Trim());
            else if (body.StartsWith("which ", StringComparison.Ordinal))
                emus.Add(body["which ".Length..].Trim());
            else if (body.StartsWith("exec=", StringComparison.Ordinal))
                layout.SampleExec = body["exec=".Length..].Trim();
            else if (body.StartsWith("board=", StringComparison.Ordinal))
                layout.Board = body["board=".Length..].Trim();
            else if (body.StartsWith("region=", StringComparison.Ordinal))
                layout.Region = body["region=".Length..].Trim();
        }

        layout.MenuFolders = menus;
        layout.AvailableEmulators = emus.Distinct(StringComparer.Ordinal).ToList();

        if (string.IsNullOrWhiteSpace(layout.TargetRoot))
            layout.TargetRoot = "/var/lib/hakchi/games";

        return layout;
    }

    /// <summary>
    /// Pick which 000/001/002… page should hold a game title.
    /// Letter folders like "AKU - NIN" / "POC - TOE" are CLV-S shortcuts to those pages.
    /// Root 000 is usually only "Original games" + folder links — custom games go in 001+.
    /// </summary>
    public static string? PickMenuFolderForGame(ConsoleGamesLayout layout, string gameName)
    {
        var key = NormalizeSortKey(gameName);
        if (string.IsNullOrEmpty(key))
            key = "A";

        // Letter-range labels (POC - TOE, AKU - NIN, A - H, …)
        foreach (var (folder, label) in layout.FolderLabels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (folder == "000") continue;
            if (!TryParseLetterRange(label, out var start, out var end))
                continue;
            // Inclusive range on 3-letter keys (pad short sides)
            var k = key.PadRight(3, 'A');
            var a = start.PadRight(3, 'A');
            var b = end.PadRight(3, 'Z');
            if (string.Compare(k, a, StringComparison.Ordinal) >= 0 &&
                string.Compare(k, b, StringComparison.Ordinal) <= 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]Menu match:[/] page [cyan]{Markup.Escape(folder)}[/] [grey]\"{Markup.Escape(label)}\"[/] for [bold]{Markup.Escape(gameName)}[/]");
                return folder;
            }
        }

        // "More games..." often points at the first custom page (001) with no letter range in the name.
        foreach (var (folder, label) in layout.FolderLabels)
        {
            if (folder == "000") continue;
            if (label.Contains("More games", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("more game", StringComparison.OrdinalIgnoreCase))
            {
                // Early alphabet → first "more games" page; late → last non-000 page
                var pages = layout.MenuFolders.Where(m => m != "000").OrderBy(m => m, StringComparer.Ordinal).ToList();
                if (pages.Count == 0) return folder;
                var pick = key[0] <= 'M' ? pages.First() : pages.Last();
                AnsiConsole.MarkupLine(
                    $"[grey]Menu via \"More games\":[/] page [cyan]{Markup.Escape(pick)}[/] for [bold]{Markup.Escape(gameName)}[/]");
                return pick;
            }
        }

        var fallbackPages = layout.MenuFolders
            .Where(m => m != "000")
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
        if (fallbackPages.Count > 0)
        {
            var pick = key[0] <= 'M' ? fallbackPages.First() : fallbackPages.Last();
            AnsiConsole.MarkupLine(
                $"[grey]Menu fallback:[/] page [cyan]{Markup.Escape(pick)}[/] for {Markup.Escape(gameName)}");
            return pick;
        }

        return layout.MenuFolders.OrderBy(m => m, StringComparer.Ordinal).LastOrDefault();
    }

    private static string NormalizeSortKey(string name)
    {
        var s = Regex.Replace(name ?? "", @"[^A-Za-z0-9]+", "").ToUpperInvariant();
        return s.Length > 3 ? s[..3] : s;
    }

    private static bool TryParseLetterRange(string label, out string start, out string end)
    {
        start = end = "";
        if (string.IsNullOrWhiteSpace(label)) return false;
        // Skip non-letter folders
        var lower = label.ToLowerInvariant();
        if (lower.Contains("original") || lower.Contains("more games") || lower.Contains("back"))
            return false;

        // "AKU - NIN" / "A - C" / "POC - TOE"
        var m = Regex.Match(label.Trim(), @"^([A-Za-z0-9]{1,8})\s*-\s*([A-Za-z0-9]{1,8})$");
        if (!m.Success) return false;
        start = m.Groups[1].Value.ToUpperInvariant();
        end = m.Groups[2].Value.ToUpperInvariant();
        // End is exclusive-ish in hakchi sometimes; pad end so "NIN" includes "NINTENDO..."
        // Compare using start-of-name keys of same length as start.
        return start.Length > 0 && end.Length > 0;
    }

    public string PickEmulator(ConsoleGamesLayout layout, GamePackage package)
    {
        if (package.System == GamePackager.GameSystem.Snes)
            return PickSnesEmulator(layout, package.Emulator);
        return PickNesEmulator(layout, package.Emulator);
    }

    public string PickNesEmulator(ConsoleGamesLayout layout, string preferred = GamePackager.DefaultNesEmulator)
    {
        string[] order =
        {
            preferred.Split(' ')[0],
            "/bin/clover-kachikachi-wr",
            "/usr/bin/clover-kachikachi",
            "/bin/nes",
            "/bin/fceumm",
            "/bin/nestopia",
        };

        foreach (var cand in order)
        {
            if (layout.AvailableEmulators.Any(e =>
                    e.Equals(cand, StringComparison.Ordinal) ||
                    e.EndsWith("/" + Path.GetFileName(cand), StringComparison.Ordinal)))
                return cand == preferred.Split(' ')[0] ? preferred : cand;
        }

        return preferred;
    }

    public string PickSnesEmulator(ConsoleGamesLayout layout, string preferred = GamePackager.DefaultSnesEmulator)
    {
        // Probe lists paths like /bin/clover-canoe-shvc-wr; canoe needs " -rom" after binary.
        string[] bins =
        {
            "/bin/clover-canoe-shvc-wr",
            "/usr/bin/clover-canoe-shvc",
            "/bin/snes",
            "/bin/snes9x",
            "/bin/snes9x2010",
        };

        foreach (var bin in bins)
        {
            if (layout.AvailableEmulators.Any(e =>
                    e.Equals(bin, StringComparison.Ordinal) ||
                    e.EndsWith("/" + Path.GetFileName(bin), StringComparison.Ordinal)))
            {
                if (bin.Contains("canoe", StringComparison.OrdinalIgnoreCase))
                    return bin + " -rom";
                return bin;
            }
        }

        // Also match sample exec from console (canoe path)
        if (!string.IsNullOrEmpty(layout.SampleExec) &&
            layout.SampleExec.Contains("canoe", StringComparison.OrdinalIgnoreCase))
            return GamePackager.DefaultSnesEmulator;

        return preferred;
    }

    /// <summary>
    /// Upload a single packaged game (add-only). Does not delete other games.
    /// </summary>
    public UploadResult UploadGamePackage(
        GamePackage package,
        ConsoleGamesLayout layout,
        string? emulator = null,
        bool force = false,
        bool refresh = true)
    {
        if (!Directory.Exists(package.LocalDirectory))
            throw new DirectoryNotFoundException(package.LocalDirectory);

        var code = package.Code;
        var emu = string.IsNullOrWhiteSpace(emulator) ? package.Emulator : emulator!;
        if (!Regex.IsMatch(code, @"^CLV-[A-Za-z0-9]-.+$"))
            throw new ArgumentException("Invalid game code: " + code);

        // Menu folder (000/001/002…) — match letter-range folders (AKU-NIN, POC-TOE, …).
        // Putting "DuckTales" into 002/POC-TOE made it invisible in AKU-NIN on the UI.
        string? menuFolder = PickMenuFolderForGame(layout, package.Name);
        if (menuFolder == null && layout.MenuFolders.Count > 0)
            menuFolder = layout.MenuFolders.OrderBy(m => m, StringComparer.Ordinal).Last();

        var targetRoot = layout.TargetRoot.TrimEnd('/');
        // When hakchi uses linked storage (.storage), match existing games: files live in
        // .storage/CODE and the menu folder only gets a symlink (see Super Mario World sample).
        var useLinkedStorage = layout.HasDotStorage;
        var storageParent = useLinkedStorage ? $"{targetRoot}/.storage" : null;
        var menuParent = menuFolder != null ? $"{targetRoot}/{menuFolder}" : targetRoot;
        var fileParent = useLinkedStorage ? storageParent! : menuParent;
        var destGameDir = $"{fileParent}/{code}";
        var menuLink = $"{menuParent}/{code}";

        AnsiConsole.MarkupLine(
            useLinkedStorage
                ? $"[grey]Remote storage:[/] {Markup.Escape(destGameDir)}  [grey]menu link:[/] {Markup.Escape(menuLink)}"
                : $"[grey]Remote destination:[/] {Markup.Escape(destGameDir)}");

        // Soft free-space check (df_avail_kb=0 often means parse failed — ignore)
        var localSize = DirSize(package.LocalDirectory);
        if (layout.AvailableKb is > 1024 && localSize > layout.AvailableKb.Value * 1024)
            throw new IOException(
                $"Not enough free space on console (need ~{localSize / 1024} KiB, df reports {layout.AvailableKb} KiB avail).");

        // If game already present anywhere under target, refuse unless force
        var existing = Run(
            $"find \"{Escape(targetRoot)}\" \\( -type d -o -type l \\) -name '{Escape(code)}' 2>/dev/null | head -n8",
            timeoutMs: 60000).Trim();
        if (!string.IsNullOrEmpty(existing) && !force)
        {
            throw new InvalidOperationException(
                $"\"{package.Name}\" is already installed as {code}:\n{existing}\n\n" +
                "That is this same game (not another title). To reinstall/update only these paths, run again with --force.\n" +
                "Example: ./run add-game <rom-or-zip> --force");
        }

        if (!string.IsNullOrEmpty(existing) && force)
        {
            foreach (var line in existing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.Contains(code, StringComparison.Ordinal)) continue;
                if (!line.TrimEnd('/').EndsWith(code, StringComparison.Ordinal)) continue;
                AnsiConsole.MarkupLine($"[yellow]Removing previous[/] {Markup.Escape(line)}");
                Run($"rm -rf -- \"{Escape(line)}\"", timeoutMs: 60000, allowDestructive: true);
            }
        }

        // Match existing hakchi "linked" layout (see Mega Man 6 on this console):
        //   .storage/CODE/   → ROM + png + full package
        //   001/CODE/CODE.desktop → REAL directory + desktop only (NOT a symlink to .storage)
        // Clover does not list games that are pure symlinks into .storage.
        var mediaBase = useLinkedStorage ? storageParent! : GamePackager.GamesMountPath;
        GamePackager.RewriteDesktopPaths(package, emu, mediaBase);

        Run($"mkdir -p \"{Escape(fileParent)}\" \"{Escape(menuParent)}\"", timeoutMs: 15000);

        using var tarStream = BuildTarOfDirectory(package.LocalDirectory, code);
        tarStream.Position = 0;
        AnsiConsole.MarkupLine($"[grey]Uploading package to storage ({tarStream.Length} bytes)…[/]");
        RunWithStdin($"tar -xC \"{Escape(fileParent)}\"", tarStream, timeoutMs: 600000);

        string menuDesktopPath;
        if (useLinkedStorage)
        {
            // Remove mistaken whole-dir symlink from earlier uploads
            Run(
                $"rm -rf -- \"{Escape(menuLink)}\"; mkdir -p \"{Escape(menuLink)}\"",
                timeoutMs: 15000,
                allowDestructive: true);

            var desktopLocal = Path.Combine(package.LocalDirectory, package.DesktopFileName);
            var desktopBytes = File.ReadAllBytes(desktopLocal);
            menuDesktopPath = $"{menuLink}/{code}.desktop";
            using var deskMs = new MemoryStream(desktopBytes);
            RunWithStdin($"cat > \"{Escape(menuDesktopPath)}\"", deskMs, timeoutMs: 60000);
            AnsiConsole.MarkupLine($"[grey]Menu desktop (real file):[/] {Markup.Escape(menuDesktopPath)}");
        }
        else
        {
            menuDesktopPath = $"{destGameDir}/{code}.desktop";
        }

        // Verify desktop landed where clover lists games
        var verify = Run(
            $"test -f \"{Escape(destGameDir)}/{Escape(code)}.desktop\" && echo STORAGE_OK || echo STORAGE_FAIL; " +
            $"test -f \"{Escape(menuDesktopPath)}\" && echo MENU_OK || echo MENU_FAIL; " +
            $"ls -la \"{Escape(destGameDir)}\"; ls -la \"{Escape(menuLink)}\" 2>/dev/null; " +
            $"test ! -L \"{Escape(menuLink)}\" && echo NOT_SYMLINK || echo IS_SYMLINK; true",
            timeoutMs: 30000);
        AnsiConsole.WriteLine(verify.TrimEnd());
        if (!verify.Contains("MENU_OK", StringComparison.Ordinal) &&
            !verify.Contains("STORAGE_OK", StringComparison.Ordinal))
            throw new InvalidOperationException("Upload verification failed — desktop file missing after upload.");
        if (useLinkedStorage && !verify.Contains("MENU_OK", StringComparison.Ordinal))
            throw new InvalidOperationException("Upload verification failed — menu desktop missing (clover will not show the game).");
        if (useLinkedStorage && verify.Contains("IS_SYMLINK", StringComparison.Ordinal))
            throw new InvalidOperationException("Menu entry is still a symlink; clover will not list it.");

        if (refresh)
        {
            AnsiConsole.MarkupLine("[grey]Refreshing game mount (overmount_games)…[/]");
            try
            {
                // Stop UI briefly so mount can refresh; no reboot/poweroff
                Run("uistop 2>/dev/null; hakchi overmount_games 2>/dev/null; uistart 2>/dev/null; echo REFRESH_DONE",
                    timeoutMs: 60000);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Refresh warning:[/] {Markup.Escape(ex.Message)}");
                AnsiConsole.MarkupLine("[grey]Game files are on disk; reboot the Classic if it does not appear.[/]");
            }
        }

        return new UploadResult(destGameDir, localSize);
    }

    private static MemoryStream BuildTarOfDirectory(string directory, string rootName)
    {
        // macOS user IDs (often > 2^21) and some timestamps exceed classic USTAR field
        // limits. Write controlled metadata (uid/gid 0, mode 0644) so tar is portable.
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(directory, file).Replace('\\', '/');
                var entryName = rootName + "/" + rel;
                using var fs = File.OpenRead(file);
                var entry = new UstarTarEntry(TarEntryType.RegularFile, entryName)
                {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                           UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    Uid = 0,
                    Gid = 0,
                    UserName = "root",
                    GroupName = "root",
                    ModificationTime = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000),
                    DataStream = fs,
                };
                writer.WriteEntry(entry);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static long DirSize(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static string Escape(string s) => s.Replace("\"", "\\\"");

    public void Dispose()
    {
        // Teardown must not block the CLI for ~60s (userspace TCP/SSH.NET can stall).
        // Run cleanup with a hard time budget, then abandon leftover threads.
        try
        {
            var done = Task.Run(() =>
            {
                try
                {
                    if (_ssh != null)
                    {
                        try { _ssh.ConnectionInfo.Timeout = TimeSpan.FromMilliseconds(500); } catch { }
                        try { _ssh.Dispose(); } catch { }
                    }
                }
                catch { }
                _ssh = null;

                try { _relay?.Dispose(); } catch { }
                _relay = null;

                try { _rndis?.Dispose(); } catch { }
                _rndis = null;
            });
            if (!done.Wait(TimeSpan.FromSeconds(2)))
                AnsiConsole.MarkupLine("[grey](USB session teardown timed out — exiting anyway)[/]");
        }
        catch { }
        _ssh = null;
        _relay = null;
        _rndis = null;
    }
}

internal sealed record GameEntry(string Code, string Name, string Path);

internal sealed class ConsoleGamesLayout
{
    public string TargetRoot { get; set; } = "/var/lib/hakchi/games";
    public string SyncStorage { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string Board { get; set; } = "";
    public string Region { get; set; } = "";
    public List<string> MenuFolders { get; set; } = new();
    /// <summary>Menu page id → folder desktop Name (e.g. 001 → "AKU - NIN").</summary>
    public Dictionary<string, string> FolderLabels { get; set; } = new(StringComparer.Ordinal);
    public bool HasDotStorage { get; set; }
    public long? AvailableKb { get; set; }
    public List<string> AvailableEmulators { get; set; } = new();
    public string SampleExec { get; set; } = "";
}

internal sealed record UploadResult(string RemoteDirectory, long BytesUploaded);
