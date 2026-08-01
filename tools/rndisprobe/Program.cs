using Hakchi.Rndis;
using Hakchi.Usb;
LibUsbBootstrap.EnsureInitialized();
using var d = new RndisDevice();
d.Open();
Console.WriteLine("MAC " + RndisDevice.MacToString(d.DeviceMac));
Console.WriteLine("DHCP...");
var lease = UserspaceDhcp.TryAcquire(d, TimeSpan.FromSeconds(6));
Console.WriteLine(lease == null ? "no lease" : $"lease client={lease.Client} server={lease.Server}");
var buf = new byte[4096];
int n=0;
var end = DateTime.UtcNow.AddSeconds(3);
while (DateTime.UtcNow < end)
{
  if (d.TryReadEthernetFrame(buf, out var len, 100))
  {
    n++;
    Console.WriteLine($"RX {len} {BitConverter.ToString(buf,0,Math.Min(24,len))}");
  }
}
Console.WriteLine("extra frames " + n);
LibUsbBootstrap.Shutdown();
Environment.Exit(0);
