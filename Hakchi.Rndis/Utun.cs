using System.Runtime.InteropServices;
using System.Text;

namespace Hakchi.Rndis;

/// <summary>Minimal macOS utun interface for bridging RNDIS IP traffic. Creating utun typically requires root.</summary>
public sealed class Utun : IDisposable
{
    private const int PF_SYSTEM = 32;
    private const int SOCK_DGRAM = 2;
    private const int SYSPROTO_CONTROL = 2;
    private const int AF_SYSTEM = 32;
    private const int AF_SYS_CONTROL = 2;
    private const int UTUN_OPT_IFNAME = 2;
    // CTLIOCGINFO = _IOWR('N', 3, struct ctl_info) ; sizeof(ctl_info)=100
    private const ulong CTLIOCGINFO = 0xC0644E03UL;
    private const string UTUN_CONTROL_NAME = "com.apple.net.utun_control";

    private int _fd = -1;
    public string InterfaceName { get; private set; } = "";

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct CtlInfo
    {
        public uint ctl_id;
        public fixed byte ctl_name[96];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrCtl
    {
        public byte sc_len;
        public byte sc_family;
        public ushort ss_sysaddr;
        public uint sc_id;
        public uint sc_unit;
        public uint r0, r1, r2, r3, r4;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int connect(int sockfd, ref SockaddrCtl addr, int addrlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref CtlInfo arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, byte[] optval, ref int optlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 4;

    public unsafe void Open(uint unit = 0)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("utun is macOS-only");

        _fd = socket(PF_SYSTEM, SOCK_DGRAM, SYSPROTO_CONTROL);
        if (_fd < 0)
            throw new InvalidOperationException($"socket(PF_SYSTEM) errno={Marshal.GetLastPInvokeError()}");

        var info = new CtlInfo();
        var nameBytes = Encoding.ASCII.GetBytes(UTUN_CONTROL_NAME);
        for (int i = 0; i < nameBytes.Length && i < 95; i++)
            info.ctl_name[i] = nameBytes[i];

        if (ioctl(_fd, CTLIOCGINFO, ref info) < 0)
            throw new InvalidOperationException(
                $"ioctl(CTLIOCGINFO) errno={Marshal.GetLastPInvokeError()}");

        var addr = new SockaddrCtl
        {
            sc_len = (byte)Marshal.SizeOf<SockaddrCtl>(),
            sc_family = AF_SYSTEM,
            ss_sysaddr = AF_SYS_CONTROL,
            sc_id = info.ctl_id,
            // Kernel assigns free unit when 0 is passed (requires privilege on modern macOS).
            sc_unit = unit
        };

        if (connect(_fd, ref addr, Marshal.SizeOf(addr)) < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"connect(utun) errno={errno}. Creating a utun interface usually needs root — re-run with sudo.");
        }

        var nameBuf = new byte[32];
        var len = nameBuf.Length;
        if (getsockopt(_fd, SYSPROTO_CONTROL, UTUN_OPT_IFNAME, nameBuf, ref len) < 0)
            throw new InvalidOperationException($"getsockopt(UTUN_OPT_IFNAME) errno={Marshal.GetLastPInvokeError()}");

        var zero = Array.IndexOf(nameBuf, (byte)0);
        InterfaceName = Encoding.ASCII.GetString(nameBuf, 0, zero < 0 ? nameBuf.Length : zero);

        var flags = fcntl(_fd, F_GETFL, 0);
        _ = fcntl(_fd, F_SETFL, flags | O_NONBLOCK);
    }

    public int Read(byte[] buffer)
    {
        if (_fd < 0) throw new ObjectDisposedException(nameof(Utun));
        return read(_fd, buffer, buffer.Length);
    }

    public void Write(byte[] buffer, int length)
    {
        if (_fd < 0) throw new ObjectDisposedException(nameof(Utun));
        var n = write(_fd, buffer, length);
        if (n < 0)
            throw new IOException($"utun write errno={Marshal.GetLastPInvokeError()}");
    }

    public void Dispose()
    {
        if (_fd >= 0)
        {
            close(_fd);
            _fd = -1;
        }
    }
}
