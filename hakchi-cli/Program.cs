using System.ComponentModel;
using com.clusterrr.clovershell;
using FelLib;
using Hakchi.Rndis;
using Hakchi.Usb;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hakchi.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        LibUsbBootstrap.EnsureInitialized();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LibUsbBootstrap.Shutdown();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = false;
            LibUsbBootstrap.Shutdown();
        };

        try
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.SetApplicationName("hakchi-cli");
                config.ValidateExamples();

                config.AddCommand<StatusCommand>("status")
                    .WithDescription("Show host, libusb, and classic-device status");

                config.AddCommand<UsbListCommand>("usb")
                    .WithDescription("List USB devices visible to libusb");

                config.AddCommand<WaitFelCommand>("wait-fel")
                    .WithDescription("Wait until a Classic Mini appears in FEL/burn mode (VID 1F3A / PID EFE8)");

                config.AddCommand<FelInfoCommand>("fel-info")
                    .WithDescription("Open the device in FEL mode and print board info");

                config.AddCommand<EnterFelCommand>("enter-fel")
                    .WithDescription("Handshake burn mode and request FEL entry");

                config.AddCommand<ClovershellExecCommand>("exec")
                    .WithDescription("Connect over clovershell and run a remote command");

                config.AddCommand<ClovershellReplCommand>("shell")
                    .WithDescription("Interactive clovershell remote shell (execute commands)");

                config.AddCommand<RndisInfoCommand>("rndis-info")
                    .WithDescription("Open the hakchi RNDIS gadget and print MAC / transfer info (no SSH)");

                config.AddCommand<GamesCommand>("games")
                    .WithDescription("READ-ONLY: list games on the console via USB RNDIS + SSH (no sync/flash)");

                config.AddCommand<AddGameCommand>("add-game")
                    .WithDescription("ADD-ONLY: package and upload one or more ROMs/zips (no full sync, no mass delete)");

                config.AddCommand<RemoteCommand>("remote")
                    .WithDescription("READ-ONLY-ish: run a shell command on the console over RNDIS SSH (no sync)");

                config.AddCommand<MembootCommand>("memboot")
                    .WithDescription("RAM-only memboot via FEL (no NAND write). Boots a recovery image from RAM.");

                config.AddCommand<ReplCommand>("repl")
                    .WithDescription("ASCII menu / interactive mode (default when no args)");
            });

            int code;
            if (args.Length == 0)
                code = new ReplCommand().Execute(new CommandContext(new List<string>(), null!, "repl", null), new ReplSettings());
            else
                code = app.Run(args);

            try { LibUsbBootstrap.Shutdown(); } catch { }
            // LibUsbDotNet / USB threads may otherwise keep the process alive for a minute+.
            Environment.Exit(code);
            return code;
        }
        catch
        {
            LibUsbBootstrap.Shutdown();
            throw;
        }
    }
}

internal static class Ui
{
    public static void Banner()
    {
        AnsiConsole.Write(
            new FigletText("hakchi")
                .Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]macOS/Linux CLI port · headless first · no WinForms[/]");
        AnsiConsole.WriteLine();
    }

    public static void PrintUsbRoleHelp(ClassicUsbRole role)
    {
        switch (role)
        {
            case ClassicUsbRole.FelOrClovershell:
                AnsiConsole.MarkupLine(
                    "[yellow]Device is in FEL/clovershell (1F3A:EFE8), not RNDIS.[/]");
                AnsiConsole.MarkupLine(
                    "[grey]Power-cycle to normal hakchi boot (menu on TV). Do not hold reset unless you want FEL.[/]");
                break;
            case ClassicUsbRole.Unknown:
                AnsiConsole.MarkupLine(
                    "[yellow]No Classic Mini USB id seen right now (neither RNDIS nor FEL).[/]");
                AnsiConsole.MarkupLine("[grey]Checklist:[/]");
                AnsiConsole.MarkupLine("  [grey]1. Mini powered on to the [white]game menu[/] (hakchi already installed)[/]");
                AnsiConsole.MarkupLine("  [grey]2. USB data cable Mac ↔ Mini (not charge-only)[/]");
                AnsiConsole.MarkupLine("  [grey]3. Prefer a direct port (skip hubs if possible)[/]");
                AnsiConsole.MarkupLine("  [grey]4. Unplug/replug USB after the menu is up[/]");
                AnsiConsole.MarkupLine("  [grey]5. Run [cyan]./run usb[/] — look for [cyan]04E8:6863[/] (RNDIS) or [cyan]1F3A:EFE8[/] (FEL)[/]");
                break;
            default:
                break;
        }
    }

    public static void StatusPanel()
    {
        var role = UsbEnumerator.DetectRole();
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Key").AddColumn("Value");
        table.AddRow("OS", $"{Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        table.AddRow("Runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        table.AddRow("libusb", LibUsbBootstrap.DescribeNativeLibraryStatus());
        table.AddRow("Classic USB", role switch
        {
            ClassicUsbRole.HakchiRndisGadget =>
                $"[green]present[/] — running hakchi RNDIS gadget [cyan]{ClassicIds.RndisVid:X4}:{ClassicIds.RndisPid:X4}[/]",
            ClassicUsbRole.FelOrClovershell =>
                $"[green]present[/] — FEL/clovershell [cyan]{ClassicIds.FelVid:X4}:{ClassicIds.FelPid:X4}[/]",
            _ => "[yellow]not found[/]"
        });
        table.AddRow("Mode", role switch
        {
            ClassicUsbRole.HakchiRndisGadget =>
                "[yellow]USB ethernet (RNDIS)[/] — needs host RNDIS/network + SSH; not clovershell bulk",
            ClassicUsbRole.FelOrClovershell =>
                "[green]FEL / clovershell bulk[/] — low-level USB shell path",
            _ => "[grey]—[/]"
        });
        table.AddRow("FEL DeviceExists", Fel.DeviceExists() ? "[green]yes[/]" : "[grey]no[/]");
        table.AddRow("Safety", "[grey]CLI defaults are read-only; no sync/flash unless you ask[/]");
        AnsiConsole.Write(table);
    }
}

internal sealed class StatusCommand : Command<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        Ui.Banner();
        Ui.StatusPanel();
        var role = UsbEnumerator.DetectRole();
        if (role != ClassicUsbRole.HakchiRndisGadget)
        {
            AnsiConsole.WriteLine();
            Ui.PrintUsbRoleHelp(role);
        }
        return 0;
    }
}

internal sealed class UsbListCommand : Command<UsbListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-a|--all")]
        [Description("Show all devices, not only the classic VID/PID")]
        public bool All { get; init; } = true;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        LibUsbBootstrap.EnsureInitialized();
        IReadOnlyList<UsbDeviceInfo> devices;
        try
        {
            devices = UsbEnumerator.ListDevices();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]USB enumeration failed:[/] {ex.Message}");
            return 1;
        }

        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No USB devices reported by libusb.[/]");
            AnsiConsole.MarkupLine("[grey]On macOS this is common if nothing is plugged in, or if permission/access is blocked.[/]");
            AnsiConsole.MarkupLine($"[grey]{LibUsbBootstrap.DescribeNativeLibraryStatus()}[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumns("VID:PID", "Role", "Name");
        foreach (var d in devices)
        {
            if (!settings.All && !d.IsClassicFamily)
                continue;
            table.AddRow(
                Markup.Escape(d.Id),
                d.Role == ClassicUsbRole.Unknown ? "[grey]—[/]" : $"[green]{d.Role}[/]",
                Markup.Escape(d.Name ?? "—"));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{devices.Count} device(s) via libusb[/]");
        return 0;
    }
}

internal sealed class WaitFelCommand : Command<WaitFelCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-t|--timeout")]
        [Description("Seconds to wait (0 = forever)")]
        public int TimeoutSeconds { get; init; } = 0;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var start = DateTime.UtcNow;
        AnsiConsole.MarkupLine(
            $"Waiting for FEL/burn device [cyan]{ClassicIds.Vid:X4}:{ClassicIds.Pid:X4}[/] " +
            "(hold reset on Classic while powering on)…");

        return AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("listening on USB…", ctx =>
            {
                while (true)
                {
                    if (Fel.DeviceExists())
                    {
                        AnsiConsole.MarkupLine("[green]Device detected.[/]");
                        return 0;
                    }

                    if (settings.TimeoutSeconds > 0 &&
                        (DateTime.UtcNow - start).TotalSeconds >= settings.TimeoutSeconds)
                    {
                        AnsiConsole.MarkupLine("[red]Timeout.[/]");
                        return 1;
                    }

                    Thread.Sleep(500);
                }
            });
    }
}

internal sealed class FelInfoCommand : Command<FelInfoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--burn")]
        [Description("Open in burn mode instead of FEL")]
        public bool Burn { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (!Fel.DeviceExists())
        {
            AnsiConsole.MarkupLine("[red]No classic USB device present. Use wait-fel first.[/]");
            return 1;
        }

        try
        {
            using var fel = new Fel();
            fel.WriteLine += msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]");
            if (!fel.Open(isFel: !settings.Burn))
            {
                AnsiConsole.MarkupLine("[red]Failed to open device in the requested mode.[/]");
                return 1;
            }

            if (!settings.Burn)
            {
                var info = fel.VerifyDevice();
                var table = new Table().Border(TableBorder.Rounded).AddColumn("Field").AddColumn("Value");
                table.AddRow("Board", $"0x{info.Board:X8}");
                table.AddRow("FW", $"0x{info.FW:X8}");
                table.AddRow("Mode", $"0x{info.Mode:X}");
                table.AddRow("DataFlag", $"0x{info.DataFlag:X}");
                table.AddRow("DataLength", info.DataLength.ToString());
                table.AddRow("DataStartAddress", $"0x{info.DataStartAddress:X8}");
                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.MarkupLine("[green]Opened in burn mode.[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}

internal sealed class EnterFelCommand : Command<EnterFelCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (!Fel.DeviceExists())
        {
            AnsiConsole.MarkupLine("[red]No classic USB device present.[/]");
            return 1;
        }

        try
        {
            using var fel = new Fel();
            fel.WriteLine += msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]");
            if (!fel.Open(isFel: false))
            {
                AnsiConsole.MarkupLine("[red]USB device not found / not in burn mode.[/]");
                return 1;
            }

            if (!fel.UsbUpdateProbe())
            {
                AnsiConsole.MarkupLine("[red]Failed to handshake with burn mode.[/]");
                return 1;
            }

            if (!fel.UsbUpdateEnterFel())
            {
                AnsiConsole.MarkupLine("[red]Failed to enter FEL.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]Requested FEL entry.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}

internal sealed class ClovershellExecCommand : Command<ClovershellExecCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<command>")]
        [Description("Remote shell command")]
        public string Command { get; init; } = "uname -a";

        [CommandOption("-t|--timeout")]
        public int TimeoutMs { get; init; } = 5000;

        [CommandOption("-w|--wait")]
        [Description("Seconds to wait for clovershell device (0 = no wait)")]
        public int WaitSeconds { get; init; } = 15;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var shell = new ClovershellConnection { AutoReconnect = false };
        if (!WaitForClovershell(shell, settings.WaitSeconds))
            return 1;

        try
        {
            var result = shell.ExecuteSimple(settings.Command, settings.TimeoutMs, throwOnNonZero: false);
            AnsiConsole.WriteLine(result);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    internal static bool WaitForClovershell(ClovershellConnection shell, int waitSeconds)
    {
        var start = DateTime.UtcNow;
        shell.Enabled = true;
        AnsiConsole.MarkupLine("[grey]Connecting clovershell (device must be on with clovershell mod)…[/]");

        while (!shell.IsOnline)
        {
            if (waitSeconds > 0 && (DateTime.UtcNow - start).TotalSeconds >= waitSeconds)
            {
                AnsiConsole.MarkupLine("[red]Timed out waiting for clovershell.[/]");
                shell.Enabled = false;
                return false;
            }
            Thread.Sleep(100);
        }

        AnsiConsole.MarkupLine("[green]Clovershell online.[/]");
        return true;
    }
}

internal sealed class ClovershellReplCommand : Command<ClovershellReplCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-w|--wait")]
        public int WaitSeconds { get; init; } = 30;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        using var shell = new ClovershellConnection { AutoReconnect = true };
        if (!ClovershellExecCommand.WaitForClovershell(shell, settings.WaitSeconds))
            return 1;

        AnsiConsole.MarkupLine("[grey]Type remote commands. Empty line or 'exit' to quit.[/]");
        while (true)
        {
            var line = AnsiConsole.Prompt(new TextPrompt<string>("[cyan]clover>[/]").AllowEmpty());
            if (string.IsNullOrWhiteSpace(line) || line is "exit" or "quit")
                break;
            try
            {
                var stdout = new MemoryStream();
                var stderr = new MemoryStream();
                var code = shell.Execute(line, null, stdout, stderr, timeout: 10000, throwOnNonZero: false);
                var outText = System.Text.Encoding.UTF8.GetString(stdout.ToArray());
                var errText = System.Text.Encoding.UTF8.GetString(stderr.ToArray());
                if (!string.IsNullOrEmpty(outText))
                    Console.Write(outText.EndsWith('\n') ? outText : outText + "\n");
                if (!string.IsNullOrEmpty(errText))
                    AnsiConsole.Markup($"[red]{Markup.Escape(errText)}[/]");
                AnsiConsole.MarkupLine($"[grey]exit {code}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                if (!shell.IsOnline)
                {
                    AnsiConsole.MarkupLine("[yellow]Device offline.[/]");
                    break;
                }
            }
        }

        return 0;
    }
}

internal sealed class RndisInfoCommand : Command<RndisInfoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--seconds")]
        [Description("Seconds to listen for bulk RX after init")]
        public int Seconds { get; init; } = 3;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            using var d = new RndisDevice();
            d.Open();
            var xmit0 = d.QueryXmitOk();
            var rcv0 = d.QueryRcvOk();
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Key").AddColumn("Value");
            table.AddRow("USB", $"{ClassicIds.RndisVid:X4}:{ClassicIds.RndisPid:X4}");
            table.AddRow("Device MAC", RndisDevice.MacToString(d.DeviceMac));
            table.AddRow("Host MAC", RndisDevice.MacToString(d.HostMac));
            table.AddRow("Max transfer", d.MaxTransferSize.ToString());
            table.AddRow("Link speed", d.QueryLinkSpeed().ToString());
            table.AddRow("Media connect", d.QueryMediaConnect() == 0 ? "connected (0)" : d.QueryMediaConnect().ToString());
            table.AddRow("XMIT_OK (device TX)", xmit0.ToString());
            table.AddRow("RCV_OK (device RX)", rcv0.ToString());
            AnsiConsole.Write(table);

            // Inject a broadcast ARP and a DHCP discover, then watch counters / bulk RX
            AnsiConsole.MarkupLine("[grey]Injecting ARP + DHCP discover, listening…[/]");
            var eth = BuildDhcpDiscover(d.HostMac);
            d.WriteEthernetFrame(eth);

            var buf = new byte[4096];
            var frames = 0;
            var end = DateTime.UtcNow.AddSeconds(Math.Max(1, settings.Seconds));
            while (DateTime.UtcNow < end)
            {
                if (d.TryReadEthernetFrame(buf, out var len, 100))
                {
                    frames++;
                    AnsiConsole.MarkupLine($"[green]RX {len}[/] {BitConverter.ToString(buf, 0, Math.Min(24, len))}");
                }
            }

            var xmit1 = d.QueryXmitOk();
            var rcv1 = d.QueryRcvOk();
            AnsiConsole.MarkupLine($"Bulk eth frames received by host: [bold]{frames}[/]");
            AnsiConsole.MarkupLine($"Device XMIT_OK {xmit0} → {xmit1} (should rise if Classic transmits)");
            AnsiConsole.MarkupLine($"Device RCV_OK  {rcv0} → {rcv1} (should rise when our TX is accepted)");
            if (xmit1 == xmit0 && frames == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Diagnosis: Classic accepts host frames but never transmits. " +
                                      "SSH cannot work until the device sends Ethernet frames (DHCP/ARP/TCP).[/]");
            }
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static byte[] BuildDhcpDiscover(byte[] hostMac)
    {
        // Minimal-but-valid broadcast Ethernet + IPv4 UDP DHCPDISCOVER
        var dhcp = new byte[300];
        dhcp[0] = 1; dhcp[1] = 1; dhcp[2] = 6;
        dhcp[4] = 0x12; dhcp[5] = 0x34; dhcp[6] = 0x56; dhcp[7] = 0x78;
        dhcp[10] = 0x80;
        Array.Copy(hostMac, 0, dhcp, 28, 6);
        dhcp[236] = 99; dhcp[237] = 130; dhcp[238] = 83; dhcp[239] = 99;
        dhcp[240] = 53; dhcp[241] = 1; dhcp[242] = 1; // discover
        dhcp[243] = 255;

        var ipTotal = 20 + 8 + dhcp.Length;
        var eth = new byte[14 + ipTotal];
        for (int i = 0; i < 6; i++) eth[i] = 0xff;
        Array.Copy(hostMac, 0, eth, 6, 6);
        eth[12] = 0x08; eth[13] = 0x00;
        eth[14] = 0x45;
        eth[16] = (byte)(ipTotal >> 8); eth[17] = (byte)ipTotal;
        eth[22] = 64; eth[23] = 17;
        for (int i = 0; i < 4; i++) eth[30 + i] = 0xff;
        // IP checksum
        int sum = 0;
        for (int i = 0; i < 20; i += 2)
            sum += (eth[14 + i] << 8) | eth[15 + i];
        while ((sum >> 16) != 0) sum = (sum & 0xffff) + (sum >> 16);
        sum = ~sum & 0xffff;
        eth[24] = (byte)(sum >> 8); eth[25] = (byte)sum;
        eth[35] = 68; eth[37] = 67;
        var ulen = 8 + dhcp.Length;
        eth[38] = (byte)(ulen >> 8); eth[39] = (byte)ulen;
        Array.Copy(dhcp, 0, eth, 42, dhcp.Length);
        return eth;
    }
}

internal sealed class MembootCommand : Command<MembootCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--fes1")]
        [Description("Path to fes1.bin")]
        public string? Fes1 { get; init; }

        [CommandOption("--uboot")]
        [Description("Path to uboot.bin")]
        public string? Uboot { get; init; }

        [CommandOption("--boot")]
        [Description("Path to Android boot.img (recovery/shell)")]
        public string? Boot { get; init; }

        [CommandOption("--wait")]
        [Description("Seconds to wait after memboot for device reappear")]
        public int WaitSeconds { get; init; } = 45;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.MarkupLine("[yellow]Memboot is RAM-only — does not write NAND/games storage.[/]");
        AnsiConsole.MarkupLine("[grey]Device must already be in FEL (1F3A:EFE8).[/]");

        try
        {
            var repo = FindRepoRoot();
            var fes1Path = settings.Fes1 ?? Path.Combine(repo, "hakchi_gui", "data", "fes1.bin");
            var ubootPath = settings.Uboot
                ?? FirstExisting(
                    Path.Combine(repo, "macport", "assets", "uboot.bin"),
                    "/tmp/hakchi-hmod/boot/uboot.bin");
            var bootPath = settings.Boot
                ?? FirstExisting(
                    Path.Combine(repo, "macport", "assets", "boot.img"),
                    "/tmp/hakchi-hmod/boot/boot.img");

            foreach (var (label, path) in new[] { ("fes1", fes1Path), ("uboot", ubootPath), ("boot", bootPath) })
            {
                if (path == null || !File.Exists(path))
                {
                    AnsiConsole.MarkupLine($"[red]Missing {label}: {Markup.Escape(path ?? "(null)")}[/]");
                    return 1;
                }
                AnsiConsole.MarkupLine($"[grey]{label}: {Markup.Escape(path)} ({new FileInfo(path).Length / 1024} KiB)[/]");
            }

            if (!Fel.DeviceExists())
            {
                AnsiConsole.MarkupLine("[red]No FEL device. Hold reset and power on until 1F3A:EFE8 appears.[/]");
                return 1;
            }

            var fes1 = File.ReadAllBytes(fes1Path!);
            var uboot = File.ReadAllBytes(ubootPath!);
            var boot = File.ReadAllBytes(bootPath!);

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Membooting…", _ =>
            {
                Memboot.BootFromRam(fes1, uboot, boot, msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"));
            });

            AnsiConsole.MarkupLine("[green]Memboot command sent.[/] Waiting for device to re-enumerate…");
            var deadline = DateTime.UtcNow.AddSeconds(settings.WaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var role = UsbEnumerator.DetectRole();
                if (role != ClassicUsbRole.Unknown)
                {
                    AnsiConsole.MarkupLine($"[green]Device back as {role}[/]");
                    Ui.StatusPanel();
                    return 0;
                }
                // Also detect clovershell endpoints on 1F3A after reboot
                if (Fel.DeviceExists())
                {
                    // still/again in FEL — recovery might re-enter FEL briefly
                }
                Thread.Sleep(500);
            }

            AnsiConsole.MarkupLine("[yellow]Timed out waiting for known USB mode. Check status manually.[/]");
            Ui.StatusPanel();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "hakchi_gui.sln")) ||
                Directory.Exists(Path.Combine(dir.FullName, "hakchi_gui")))
                return dir.FullName;
            // walk up from macport/hakchi-cli/bin/...
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string? FirstExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists);
}

internal sealed class GamesCommand : Command<GamesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-t|--timeout")]
        [Description("Seconds to wait for network/SSH")]
        public int TimeoutSeconds { get; init; } = 30;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.MarkupLine("[yellow]Read-only games list — will not sync, flash, or erase anything.[/]");
        if (Environment.OSVersion.Platform == PlatformID.Unix &&
            Environment.GetEnvironmentVariable("USER") != "root" &&
            Environment.GetEnvironmentVariable("SUDO_USER") == null)
        {
            AnsiConsole.MarkupLine("[grey]Tip: if utun/IP config fails, re-run with sudo: sudo ./run games[/]");
        }

        try
        {
            using var session = new DeviceSession();
            session.Connect(settings.TimeoutSeconds);
            var games = session.ListGames();

            if (games.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No CLV-*.desktop games found (paths may differ on this firmware).[/]");
                var ls = session.Run("hakchi; echo '---'; ls -la /var/lib/hakchi 2>/dev/null; ls -la /var/games 2>/dev/null; ls -la /media 2>/dev/null");
                AnsiConsole.WriteLine(ls);
                return 0;
            }

            var table = new Table().Border(TableBorder.Simple).AddColumns("#", "Code", "Name");
            var i = 1;
            foreach (var g in games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(i.ToString(), Markup.Escape(g.Code), Markup.Escape(g.Name));
                i++;
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[green]{games.Count}[/] game entry(ies). Nothing was modified.");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}

internal sealed class RemoteCommand : Command<RemoteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<command>")]
        [Description("Remote shell command (quoted)")]
        public string Command { get; init; } = "";

        [CommandOption("-t|--timeout")]
        public int TimeoutSeconds { get; init; } = 30;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            using var session = new DeviceSession();
            session.Connect(settings.TimeoutSeconds);
            var outp = session.Run(settings.Command, timeoutMs: Math.Max(5000, settings.TimeoutSeconds * 1000));
            Console.Write(outp);
            if (!outp.EndsWith('\n')) Console.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}

internal sealed class AddGameCommand : Command<AddGameCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<roms>")]
        [Description("One or more ROMs/zips, or a shell/glob pattern (e.g. ~/Downloads/*.zip game.nes)")]
        public string[] Paths { get; init; } = Array.Empty<string>();

        [CommandOption("-t|--timeout")]
        [Description("Seconds to wait for network/SSH")]
        public int TimeoutSeconds { get; init; } = 30;

        [CommandOption("--dry-run")]
        [Description("Package locally only; do not connect or upload")]
        public bool DryRun { get; init; }

        [CommandOption("--force")]
        [Description("If this game code already exists on the console, replace only that one folder")]
        public bool Force { get; init; }

        [CommandOption("--no-refresh")]
        [Description("Skip overmount_games / UI restart after each/all uploads")]
        public bool NoRefresh { get; init; }

        [CommandOption("--package-dir")]
        [Description("Where to write local CLV packages (default: ~/.local/share/hakchi-cli/game-packages)")]
        public string? PackageDir { get; init; }

        [CommandOption("--emulator")]
        [Description("Override remote emulator binary (default: auto per system)")]
        public string? Emulator { get; init; }

        [CommandOption("--stop-on-error")]
        [Description("Stop the batch on the first failure (default: continue and summarize)")]
        public bool StopOnError { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.MarkupLine("[yellow]Add-only game upload — will not full-sync or mass-delete games.[/]");
        AnsiConsole.MarkupLine("[grey]Supports NES (.nes) and SNES (.sfc/.smc/.sfrom), or a zip with one of those.[/]");
        AnsiConsole.MarkupLine("[grey]Requires a Classic already running [white]hakchi[/] (this tool does not flash stock → hakchi).[/]");

        try
        {
            var inputs = ExpandInputs(settings.Paths).ToList();
            if (inputs.Count == 0)
                throw new ArgumentException("No ROM/zip paths matched. Pass files and/or globs, e.g. add-game a.zip b.nes '~/Downloads/*.sfc'");

            AnsiConsole.MarkupLine($"[grey]Queue:[/] {inputs.Count} file(s)");
            foreach (var p in inputs)
                AnsiConsole.MarkupLine($"  [grey]•[/] {Markup.Escape(p)}");

            // Package all first (local only) so dry-run and preflight are fast to reason about
            var packages = new List<(string Source, GamePackage Package)>();
            var packageFailures = new List<(string Source, string Error)>();
            foreach (var input in inputs)
            {
                try
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[bold]Packaging[/] {Markup.Escape(Path.GetFileName(input))}…");
                    var package = GamePackager.Package(input, settings.PackageDir, settings.Emulator);
                    AnsiConsole.MarkupLine(
                        $"[green]Packaged[/] [bold]{Markup.Escape(package.Name)}[/] as [cyan]{Markup.Escape(package.Code)}[/] " +
                        $"[grey]({package.System}, {package.RomFileName})[/]");
                    packages.Add((input, package));
                }
                catch (Exception ex)
                {
                    packageFailures.Add((input, ex.Message));
                    AnsiConsole.MarkupLine($"[red]Package failed:[/] {Markup.Escape(Path.GetFileName(input))} — {Markup.Escape(ex.Message)}");
                    if (settings.StopOnError)
                        break;
                }
            }

            if (settings.DryRun)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[green]Dry-run complete[/] — packaged [green]{packages.Count}[/], failed [red]{packageFailures.Count}[/]. Nothing sent to the console.");
                return packageFailures.Count == 0 ? 0 : 1;
            }

            if (packages.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Nothing to upload.[/]");
                return 1;
            }

            var role = UsbEnumerator.DetectRole();
            if (role != ClassicUsbRole.HakchiRndisGadget)
            {
                AnsiConsole.MarkupLine("[red]Console not in hakchi RNDIS mode (need 04E8:6863).[/]");
                Ui.PrintUsbRoleHelp(role);
                AnsiConsole.MarkupLine("[grey]Packages are ready under the package dir; re-run when RNDIS is up.[/]");
                return 2;
            }

            using var session = new DeviceSession();
            session.Connect(settings.TimeoutSeconds);
            // One layout probe for the whole batch
            var layout = session.ProbeGamesLayout();

            var uploaded = 0;
            var uploadFailures = new List<(string Name, string Error)>();
            for (var i = 0; i < packages.Count; i++)
            {
                var (source, package) = packages[i];
                var isLast = i == packages.Count - 1;
                // Refresh UI once at the end of the batch (unless --no-refresh)
                var refresh = !settings.NoRefresh && isLast;

                try
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine(
                        $"[bold]Uploading[/] ({i + 1}/{packages.Count}) [bold]{Markup.Escape(package.Name)}[/]…");

                    var emu = string.IsNullOrWhiteSpace(settings.Emulator)
                        ? session.PickEmulator(layout, package)
                        : settings.Emulator!;
                    AnsiConsole.MarkupLine(
                        $"[grey]System:[/] {package.System}  [grey]emulator:[/] {Markup.Escape(emu)}");

                    var result = session.UploadGamePackage(
                        package,
                        layout,
                        emulator: emu,
                        force: settings.Force,
                        refresh: refresh);

                    AnsiConsole.MarkupLine(
                        $"[green]Uploaded[/] {Markup.Escape(package.Name)} → {Markup.Escape(result.RemoteDirectory)} " +
                        $"({result.BytesUploaded} bytes)");
                    uploaded++;
                }
                catch (Exception ex)
                {
                    uploadFailures.Add((package.Name, ex.Message));
                    AnsiConsole.MarkupLine($"[red]Upload failed:[/] {Markup.Escape(package.Name)} — {Markup.Escape(ex.Message)}");
                    if (settings.StopOnError)
                        break;
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[bold]Batch summary[/]: uploaded [green]{uploaded}[/] / {packages.Count}, " +
                $"package errors [red]{packageFailures.Count}[/], upload errors [red]{uploadFailures.Count}[/].");
            AnsiConsole.MarkupLine("[grey]Other games were not modified (add-only).[/]");
            AnsiConsole.MarkupLine(
                "[grey]On the Mini: open the letter folder for each title (e.g. DuckTales → [white]AKU - NIN[/]). " +
                "Back out / re-enter or power-cycle if icons don't refresh.[/]");

            return packageFailures.Count == 0 && uploadFailures.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    /// <summary>Expand ~, globs, and multi-args into distinct existing files.</summary>
    internal static IEnumerable<string> ExpandInputs(IEnumerable<string> rawPaths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            foreach (var path in ExpandOne(raw))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> ExpandOne(string raw)
    {
        var path = ExpandHome(raw.Trim());

        // Explicit file
        if (File.Exists(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        // Glob: ~/Downloads/*.zip or ./games/*.nes
        var hasGlob = path.Contains('*', StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal);
        if (hasGlob)
        {
            var dir = Path.GetDirectoryName(path);
            var pattern = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir))
                dir = Directory.GetCurrentDirectory();
            if (string.IsNullOrEmpty(pattern))
                yield break;
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"Glob directory not found: {dir}");

            var matches = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matches.Count == 0)
                throw new FileNotFoundException($"No files matched pattern: {path}");
            foreach (var m in matches)
                yield return Path.GetFullPath(m);
            yield break;
        }

        throw new FileNotFoundException($"ROM or zip not found: {path}", path);
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path == "~" ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }
}

internal sealed class ReplSettings : CommandSettings { }

internal sealed class ReplCommand : Command<ReplSettings>
{
    public override int Execute(CommandContext context, ReplSettings settings)
    {
        Ui.Banner();
        Ui.StatusPanel();
        AnsiConsole.WriteLine();

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What next?[/]")
                    .PageSize(12)
                    .AddChoices(
                        "Refresh status",
                        "List USB devices",
                        "Wait for FEL device",
                        "FEL board info",
                        "Enter FEL from burn mode",
                        "RNDIS info",
                        "List games (read-only SSH)",
                        "Add game (ROM/zip → console, add-only)",
                        "Clovershell: exec uname -a",
                        "Clovershell: interactive shell",
                        "Quit"));

            AnsiConsole.WriteLine();
            switch (choice)
            {
                case "Refresh status":
                    Ui.StatusPanel();
                    break;
                case "List USB devices":
                    new UsbListCommand().Execute(context, new UsbListCommand.Settings());
                    break;
                case "Wait for FEL device":
                    new WaitFelCommand().Execute(context, new WaitFelCommand.Settings { TimeoutSeconds = 0 });
                    break;
                case "FEL board info":
                    new FelInfoCommand().Execute(context, new FelInfoCommand.Settings());
                    break;
                case "Enter FEL from burn mode":
                    new EnterFelCommand().Execute(context, new EnterFelCommand.Settings());
                    break;
                case "RNDIS info":
                    new RndisInfoCommand().Execute(context, new RndisInfoCommand.Settings());
                    break;
                case "List games (read-only SSH)":
                    new GamesCommand().Execute(context, new GamesCommand.Settings());
                    break;
                case "Add game (ROM/zip → console, add-only)":
                {
                    var path = AnsiConsole.Ask<string>("Path(s) to ROM/zip (space-separated or one glob, e.g. ~/Downloads/*.zip):");
                    var dry = AnsiConsole.Confirm("Dry-run only (package, no upload)?", false);
                    // Split on spaces but keep a single glob token; users can quote in CLI more easily.
                    var parts = path.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    new AddGameCommand().Execute(context, new AddGameCommand.Settings
                    {
                        Paths = parts,
                        DryRun = dry,
                    });
                    break;
                }
                case "Clovershell: exec uname -a":
                    new ClovershellExecCommand().Execute(context, new ClovershellExecCommand.Settings
                    {
                        Command = "uname -a",
                        WaitSeconds = 20
                    });
                    break;
                case "Clovershell: interactive shell":
                    new ClovershellReplCommand().Execute(context, new ClovershellReplCommand.Settings());
                    break;
                case "Quit":
                    return 0;
            }

            AnsiConsole.WriteLine();
        }
    }
}
