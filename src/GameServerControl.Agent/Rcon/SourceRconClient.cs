using System.Net.Sockets;
using System.Text;

namespace GameServerControl.Agent.Rcon;

/// <summary>
/// Minimal Source-RCON client (Valve protocol). One-shot connect → auth → command → close.
///
/// Packet:
///   int32 size          (excludes itself)
///   int32 request id
///   int32 type          (3 = auth, 2 = exec / auth-response, 0 = response value)
///   byte[] body (UTF-8 + null terminator)
///   byte 0 (padding)
/// </summary>
public sealed class SourceRconClient
{
    private const int TYPE_AUTH = 3;
    private const int TYPE_AUTH_RESPONSE = 2;
    private const int TYPE_EXECCOMMAND = 2;
    private const int TYPE_RESPONSE_VALUE = 0;
    private const int AUTH_FAIL_ID = -1;

    public async Task<string> ExecuteAsync(string host, int port, string password, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var to = timeout ?? TimeSpan.FromSeconds(8);
        using var tcp = new TcpClient();
        var connectTask = tcp.ConnectAsync(host, port);
        var done = await Task.WhenAny(connectTask, Task.Delay(to, ct));
        if (done != connectTask) throw new TimeoutException($"Connect to {host}:{port} timed out");
        await connectTask;
        tcp.NoDelay = true;

        using var stream = tcp.GetStream();
        stream.ReadTimeout = (int)to.TotalMilliseconds;
        stream.WriteTimeout = (int)to.TotalMilliseconds;

        // Auth
        await WritePacketAsync(stream, requestId: 1, type: TYPE_AUTH, body: password, ct);
        // Some servers send a TYPE_RESPONSE_VALUE before AUTH_RESPONSE — drain until we see the auth response
        while (true)
        {
            var (id, type, _) = await ReadPacketAsync(stream, ct);
            if (type == TYPE_AUTH_RESPONSE)
            {
                if (id == AUTH_FAIL_ID) throw new UnauthorizedAccessException("RCON authentication failed (bad password).");
                break;
            }
        }

        // Exec
        await WritePacketAsync(stream, requestId: 2, type: TYPE_EXECCOMMAND, body: command, ct);
        // Send a junk pseudo-command to know when we're done: many servers respond out-of-order,
        // and SRCDS-style servers reply to a bogus packet with an empty value, marking the end.
        await WritePacketAsync(stream, requestId: 3, type: TYPE_RESPONSE_VALUE, body: "", ct);

        var sb = new StringBuilder();
        while (true)
        {
            var (id, type, body) = await ReadPacketAsync(stream, ct);
            if (id == 3 || (id == 2 && string.IsNullOrEmpty(body) && sb.Length > 0))
                break;
            if (type == TYPE_RESPONSE_VALUE && id == 2)
                sb.Append(body);
            if (sb.Length > 200_000) break; // sanity cap
        }
        return sb.ToString();
    }

    private static async Task WritePacketAsync(NetworkStream s, int requestId, int type, string body, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var size = 4 /*id*/ + 4 /*type*/ + bodyBytes.Length + 2 /*null + pad*/;
        var buf = new byte[4 + size];
        WriteI32(buf, 0, size);
        WriteI32(buf, 4, requestId);
        WriteI32(buf, 8, type);
        Buffer.BlockCopy(bodyBytes, 0, buf, 12, bodyBytes.Length);
        // last 2 bytes already zero
        await s.WriteAsync(buf, 0, buf.Length, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<(int id, int type, string body)> ReadPacketAsync(NetworkStream s, CancellationToken ct)
    {
        var head = await ReadExactlyAsync(s, 4, ct);
        var size = ReadI32(head, 0);
        if (size < 10 || size > 8_192) throw new IOException($"Bad RCON packet size {size}");
        var rest = await ReadExactlyAsync(s, size, ct);
        var id = ReadI32(rest, 0);
        var type = ReadI32(rest, 4);
        // body terminated by null; size includes both null bytes
        var bodyLen = size - 4 - 4 - 2;
        var body = bodyLen <= 0 ? "" : Encoding.UTF8.GetString(rest, 8, bodyLen);
        return (id, type, body);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = await s.ReadAsync(buf, off, count - off, ct);
            if (n <= 0) throw new IOException("RCON connection closed early");
            off += n;
        }
        return buf;
    }

    private static int ReadI32(byte[] b, int o) =>
        b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static void WriteI32(byte[] b, int o, int v)
    {
        b[o] = (byte)(v & 0xff);
        b[o + 1] = (byte)((v >> 8) & 0xff);
        b[o + 2] = (byte)((v >> 16) & 0xff);
        b[o + 3] = (byte)((v >> 24) & 0xff);
    }
}
