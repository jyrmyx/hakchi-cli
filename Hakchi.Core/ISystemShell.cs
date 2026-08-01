namespace Hakchi.Core;

public delegate void OnConnectedEventHandler(ISystemShell caller);
public delegate void OnDisconnectedEventHandler();

public interface ISystemShell : IDisposable
{
    bool Enabled { get; set; }
    bool IsOnline { get; }
    bool ShellEnabled { get; set; }
    ushort ShellPort { get; }
    void Connect();
    void Disconnect();
    event OnConnectedEventHandler OnConnected;
    event OnDisconnectedEventHandler OnDisconnected;
    string ExecuteSimple(string command, int timeout = 2000, bool throwOnNonZero = false);
    int Execute(string command, Stream? stdin = null, Stream? stdout = null, Stream? stderr = null, int timeout = 0, bool throwOnNonZero = false);
    Task<string> ExecuteSimpleAsync(string command, int timeout = 2000, bool throwOnNonZero = false);
    Task<int> ExecuteAsync(string command, Stream? stdin = null, Stream? stdout = null, Stream? stderr = null, int timeout = 0, bool throwOnNonZero = false);
}

public sealed class OfflineShell : ISystemShell
{
    public bool Enabled { get => false; set { } }
    public bool IsOnline => false;
    public bool ShellEnabled { get => false; set { } }
    public ushort ShellPort => 0;
    public void Connect() { }
    public void Disconnect() { }
    public event OnConnectedEventHandler OnConnected = delegate { };
    public event OnDisconnectedEventHandler OnDisconnected = delegate { };
    public string ExecuteSimple(string command, int timeout = 2000, bool throwOnNonZero = false) => string.Empty;
    public int Execute(string command, Stream? stdin = null, Stream? stdout = null, Stream? stderr = null, int timeout = 0, bool throwOnNonZero = false) => 255;
    public Task<string> ExecuteSimpleAsync(string command, int timeout = 2000, bool throwOnNonZero = false) =>
        Task.FromResult(string.Empty);
    public Task<int> ExecuteAsync(string command, Stream? stdin = null, Stream? stdout = null, Stream? stderr = null, int timeout = 0, bool throwOnNonZero = false) =>
        Task.FromResult(255);
    public void Dispose() { }
}
