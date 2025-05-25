using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class TcpListenerService : BackgroundService
{
    private readonly ILogger<TcpListenerService> _logger;
    private TcpListener? _tcpListener;
    private const string IpAddress = "127.0.0.1";
    private const int Port = 12377;
    private const int BufferSize = 4096;

    // Security: Track blocked IPs and client stats for rate limiting
    private static readonly ConcurrentBag<string> BlockedIps = new();
    private static readonly ConcurrentDictionary<string, ClientStats> ClientStatistics = new();

    private class ClientStats
    {
        public int ErrorCount { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public TcpListenerService(ILogger<TcpListenerService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var localEndPoint = new IPEndPoint(IPAddress.Parse(IpAddress), Port);
        _tcpListener = new TcpListener(localEndPoint);

        try
        {
            _tcpListener.Start();
            _logger.LogInformation($"TCP Server started on {IpAddress}:{Port}");

            while (!stoppingToken.IsCancellationRequested)
            {
                var tcpClient = await _tcpListener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(tcpClient, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP listener fatal error");
        }
        finally
        {
            _tcpListener?.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString();
        var clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();

        if (BlockedIps.Contains(clientIp))
        {
            _logger.LogWarning($"Blocked IP attempted connection: {clientIp}");
            client.Close();
            return;
        }

        try
        {
            using (client)
            {
                client.SendTimeout = 5000;
                client.ReceiveTimeout = 5000;

                using var stream = client.GetStream();
                var buffer = new byte[BufferSize];
                var messageBuilder = new StringBuilder();

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, BufferSize, ct);

                    if (bytesRead == 0)
                    {
                        _logger.LogInformation("Client disconnected gracefully");
                        break;
                    }

                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    await ProcessMessageBuffer(messageBuilder, stream, ct, clientIp, client);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is SocketException)
        {
            _logger.LogWarning($"Client connection reset: {clientEndpoint}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling client {clientEndpoint}");
        }
        finally
        {
            _logger.LogInformation($"Client disconnected: {clientEndpoint}");
        }
    }

    private async Task ProcessMessageBuffer(StringBuilder buffer, NetworkStream stream, CancellationToken ct, string clientIp, TcpClient client)
    {
        while (buffer.Length > 0)
        {
            var etxIndex = buffer.ToString().IndexOf('\x03');
            if (etxIndex == -1) break;

            var stxIndex = buffer.ToString().IndexOf('\x02');
            if (stxIndex == -1 || etxIndex <= stxIndex)
            {
                _logger.LogWarning("Malformed message - sending NAK");
                await SendResponse(stream, "\x15", ct); // NAK
                buffer.Clear();
                RegisterError(clientIp, client, ct, stream);
                return;
            }

            var message = buffer.ToString(stxIndex + 1, etxIndex - stxIndex - 1);
            buffer.Remove(0, etxIndex + 1);

            // Security checks
            if (ContainsMaliciousContent(message))
            {
                _logger.LogWarning($"Blocked malicious content from {clientIp}");
                await BlockClient(clientIp, client, ct, stream);
                return;
            }

            if (CheckRateLimit(clientIp))
            {
                _logger.LogWarning($"Rate limit exceeded for {clientIp}");
                await BlockClient(clientIp, client, ct, stream);
                return;
            }

            if (!ValidateLabMessage(message))
            {
                _logger.LogWarning($"Invalid message from {clientIp}");
                await SendResponse(stream, "\x15", ct); // NAK
                RegisterError(clientIp, client, ct, stream);
                return;
            }

            _logger.LogInformation($"Processing message: {message}");
            await ProcessLabData(message);
            await SendResponse(stream, "\x06", ct); // ACK
        }
    }

    private async Task SendResponse(NetworkStream stream, string response, CancellationToken ct)
    {
        var responseBytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(responseBytes, 0, responseBytes.Length, ct);
        _logger.LogDebug($"Sent response: {BitConverter.ToString(responseBytes)}");
    }

    private async Task ProcessLabData(string message)
    {
        // Implement your actual processing logic here
        await Task.Delay(100); // Simulate processing time
        _logger.LogInformation($"Processed: {message}");
    }

    // --- Security and Validation Methods ---

    private bool ValidateLabMessage(string message)
    {
        // Example: PATIENT123|GLUCOSE|120|mg/dL
        if (!message.Contains('|') || message.Count(c => c == '|') != 3)
            return false;

        var parts = message.Split('|');
        if (parts.Length != 4) return false;

        // Patient ID: PATIENT###
        if (!Regex.IsMatch(parts[0], @"^PATIENT\d{3}$"))
            return false;

        // Test name whitelist
        var validTests = new[] { "GLUCOSE", "HEMOGLOBIN", "CHOLESTEROL" };
        if (!validTests.Contains(parts[1]))
            return false;

        // Numeric value check
        if (!double.TryParse(parts[2], out double value) || value <= 0 || value > 1000)
            return false;

        // Unit check
        var validUnits = new[] { "mg/dL", "g/dL", "mmol/L" };
        if (!validUnits.Contains(parts[3]))
            return false;

        return true;
    }

    private bool ContainsMaliciousContent(string input)
    {
        var dangerousPatterns = new[]
        {
            @"[\x00-\x1F]",  // Control characters
            @"[;'""]",       // SQL/Command injection
            @"(--|\/\*|\*\/)", // SQL comments
            @"(drop|delete|insert|update|select|union)", // SQL keywords
            @"(<|>|\/|\\)"   // HTML/XML injection
        };

        return dangerousPatterns.Any(pattern =>
            Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase));
    }

    private bool CheckRateLimit(string clientIp)
    {
        var stats = ClientStatistics.GetOrAdd(clientIp, new ClientStats());

        if (DateTime.Now - stats.LastSeen < TimeSpan.FromMilliseconds(100))
        {
            stats.ErrorCount++;
            return stats.ErrorCount > 5;
        }

        stats.LastSeen = DateTime.Now;
        return false;
    }

    private void RegisterError(string clientIp, TcpClient client, CancellationToken ct, NetworkStream stream)
    {
        var stats = ClientStatistics.GetOrAdd(clientIp, new ClientStats());
        stats.ErrorCount++;
        if (stats.ErrorCount > 10)
        {
            _logger.LogWarning($"Too many errors from {clientIp}, blocking.");
            BlockClient(clientIp, client, ct, stream).Wait(ct);
        }
    }

    private async Task BlockClient(string ip, TcpClient client, CancellationToken ct, NetworkStream stream)
    {
        BlockedIps.Add(ip);
        _logger.LogWarning($"Blocked client: {ip}");
        await SendResponse(stream, "\x04", ct); // EOT
        client.Close();
    }
}
