using System.Net;
using System.Net.Sockets;
using WindowsIsland.Contracts;

namespace WindowsIsland.Plugins;

internal sealed class PluginNetworkBroker(ExternalPlugin plugin, Func<bool> active,
    Func<SocketsHttpHandler, HttpMessageHandler>? transportFactory = null,
    PluginRunSnapshot? snapshot = null) : IPluginNetwork
{
    private readonly SemaphoreSlim _request = new(1, 1);
    private DateTimeOffset _lastRequest;
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal) { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" };
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Connection", "Content-Length", "Transfer-Encoding", "Upgrade", "Expect", "TE", "Trailer", "Proxy-Authorization", "Proxy-Connection", "Accept-Encoding" };

    public async Task<PluginNetworkResponse> SendAsync(PluginNetworkRequest request, CancellationToken token = default)
    {
        // Authorization is issued by the host after the supplied disclosure is accepted.
        if (!await _request.WaitAsync(0, token)) throw new InvalidOperationException("A network request is already pending.");
        try
        {
            if (!active() || !plugin.Trust.IsGranted(PluginPermissions.Network)
                || plugin.Manifest.Network is not { } disclosure)
                throw new UnauthorizedAccessException();
            if (DateTimeOffset.UtcNow - _lastRequest < TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Network request rate exceeded.");
            _lastRequest = DateTimeOffset.UtcNow;
            if (request.Url.Length > 4096 || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || !Methods.Contains(request.Method) || request.Purpose != disclosure.Purpose)
                throw new InvalidDataException("The request is outside the declared scope.");
            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!disclosure.Origins.Any(value => Uri.TryCreate(value, UriKind.Absolute, out var declared)
                && string.Equals(declared.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException();
            // 廉价范围检查先行；不为超限正文分配副本，也不为拒绝的请求哈希整个插件包。
            if (request.Body?.Length > PluginNetworkLimits.MaximumRequestBytes || request.Headers?.Count > 32) throw new InvalidDataException();
            var body = request.Body?.ToArray() ?? [];
            var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Headers ?? new Dictionary<string, string>())
            {
                if (pair.Key.Length is 0 or > 128 || pair.Value.Length > 2048 || ReservedHeaders.Contains(pair.Key)
                    || pair.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                    || pair.Value.Any(c => c < 32 || c == 127) || !headers.TryAdd(pair.Key, pair.Value)) throw new InvalidDataException();
            }
            headers.Add("Host", uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost);
            headers.Add("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (body.Length != 0) headers.TryAdd("Content-Type", "application/octet-stream");
            // 运行会话持有稳定文件句柄，仍逐次核对撤销、代次和树成员。
            if (!active() || !(snapshot?.IsCurrent() ?? PluginConsentPolicy.HasConsent(plugin))) throw new UnauthorizedAccessException();
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(token);
            transfer.CancelAfter(TimeSpan.FromSeconds(30));
            var transferToken = transfer.Token;
            transferToken.ThrowIfCancellationRequested();
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
                AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
                MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = async (context, cancellation) =>
                {
                    if (!string.Equals(context.DnsEndPoint.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != 443)
                        throw new UnauthorizedAccessException();
                    var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellation);
                    if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address))) throw new UnauthorizedAccessException();
                    Exception? last = null;
                    foreach (var address in addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try { await socket.ConnectAsync(new IPEndPoint(address, 443), cancellation); return new NetworkStream(socket, ownsSocket: true); }
                        catch (Exception error) { socket.Dispose(); last = error; }
                    }
                    throw new IOException("Connection failed.", last);
                }
            };
            using var client = new HttpClient(transportFactory?.Invoke(handler) ?? handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            message.Content = new ByteArrayContent(body);
            foreach (var pair in headers)
            {
                if (pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue; // ByteArrayContent calculates the identical value.
                if (pair.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                { if (message.Content is null || !message.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw new InvalidDataException(); }
                else if (!message.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw new InvalidDataException();
            }
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, transferToken);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Response exceeds the limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(transferToken);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(block, transferToken)) != 0)
            {
                if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Response exceeds the limit.");
                buffer.Write(block, 0, count);
            }
            var responseHeaders = response.Headers.Concat(response.Content.Headers).Take(PluginNetworkLimits.MaximumHeaders)
                .ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value).TruncateHeader(), StringComparer.OrdinalIgnoreCase);
            return new((int)response.StatusCode, responseHeaders, buffer.ToArray());
        }
        finally { _request.Release(); }
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 4)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
                || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 198 && b[1] is 18 or 19
                || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2 || b[0] == 192 && b[1] == 88 && b[2] == 99
                || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113
                || b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16);
        return b.Length == 16 && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x02) // 6to4 can tunnel private IPv4.
            && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0 && b[3] == 0 || b[2] == 0x0d && b[3] == 0xb8));
    }
}

internal static class PluginHeaderLimit
{
    public static string TruncateHeader(this string value) => value.Length <= PluginNetworkLimits.MaximumHeaderValueLength
        ? value : value[..PluginNetworkLimits.MaximumHeaderValueLength];
}
