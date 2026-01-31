using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace zenas.Phoenix;

public sealed class PhoenixClient : IAsyncDisposable
{
    public event EventHandler<string>? JsonReceived;
    public event EventHandler<Exception>? Faulted;
    public event EventHandler<bool>? ConnectionChanged;

    private readonly string _host;
    private readonly int _port;
    private readonly byte _terminator;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private TcpClient? _client;
    private NetworkStream? _stream;

    public bool IsConnected { get; private set; }

    public PhoenixClient(string host, int port, byte terminator = 0x01)
    {
        _host = host;
        _port = port;
        _terminator = terminator;
    }

    public Task StartAsync(CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task SendAsync(object payload, CancellationToken token = default)
    {
        var stream = _stream;
        if (stream is null) return;

        var json = JsonConvert.SerializeObject(payload);
        var data = Encoding.UTF8.GetBytes(json);

        await stream.WriteAsync(data, 0, data.Length, token);
        await stream.WriteAsync(new[] { _terminator }, 0, 1, token);
        await stream.FlushAsync(token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var framer = new PhoenixMessageFramer(_terminator);
        var buffer = new byte[8192];

        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;

            try
            {
                client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_host, _port, token);

                stream = client.GetStream();

                _client = client;
                _stream = stream;

                SetConnected(true);

                while (!token.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (read <= 0) break;

                    var msgs = framer.Push(buffer, read); // <-- BEZ Span
                    for (int i = 0; i < msgs.Count; i++)
                        JsonReceived?.Invoke(this, msgs[i]);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(this, ex);
            }
            finally
            {
                SetConnected(false);

                _stream = null;
                _client = null;

                try { stream?.Close(); } catch { }
                try { client?.Close(); } catch { }
            }

            // reconnect delay
            try { await Task.Delay(1000, token); }
            catch { break; }
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(this, value);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loopTask is not null)
        {
            try { await _loopTask; } catch { }
        }

        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
    }
}
