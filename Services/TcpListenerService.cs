using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


public class TcpListenerService : BackgroundService
{
    public static TcpListenerService? Instance { get; private set; }

    private readonly ILogger<TcpListenerService> _logger;
    private readonly LabSettings _settings;
    private TcpListener? _tcpListener;
    private const string IpAddress = "0.0.0.0";
    private const int Port = 12377;
    private const int BufferSize = 4096;

    private static DateTime _lastReceivedMessage = DateTime.UtcNow;
    private static string _lastWriteStatus = "Idle";
    private static DateTime _lastWriteTime = DateTime.MinValue;


    // Security: Track blocked IPs and client stats for rate limiting
    private static readonly ConcurrentBag<string> BlockedIps = new();
    private static readonly ConcurrentBag<LabMessage> LabMessages = new();

    private static readonly ConcurrentDictionary<string, ClientStats> ClientStatistics = new();

    private class ClientStats
    {
        public int ErrorCount { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public TcpListenerService(ILogger<TcpListenerService> logger, IOptions<LabSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
        Instance = this;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var localEndPoint = new IPEndPoint(IPAddress.Parse(IpAddress), Port);
        _tcpListener = new TcpListener(localEndPoint);

        try
        {
            _tcpListener.Start();
            _logger.LogInformation($"TCP Server started on {IpAddress}:{Port}");

            // Start USB saving in background
            _ = SaveMessagesToJsonPeriodically(stoppingToken);

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
        _logger.LogInformation(buffer.ToString());
        while (buffer.Length > 0)
        {
            var etxIndex = buffer.ToString().IndexOf('\x03');
            if (etxIndex == -1) break;

            var stxIndex = buffer.ToString().IndexOf('\x02');
            if (stxIndex == -1 || etxIndex <= stxIndex)
            {
                _logger.LogWarning($"Malformed message from {clientIp}: {buffer}");
                await SendResponse(stream, "\x15", ct); // NAK
                buffer.Clear();
                RegisterError(clientIp); // Don't block immediately
                return;
            }

            var message = buffer.ToString(stxIndex + 1, etxIndex - stxIndex - 1);
            buffer.Remove(0, etxIndex + 1);

            if (ContainsMaliciousContent(message))
            {
                _logger.LogWarning($"[MALICIOUS INPUT] from {clientIp}: {message}");
                await SendResponse(stream, "\x15", ct); // NAK
                RegisterError(clientIp);
                continue;
            }

            if (CheckRateLimit(clientIp))
            {
                _logger.LogWarning($"[RATE LIMIT EXCEEDED] {clientIp}");
                await SendResponse(stream, "\x15", ct); // NAK
                RegisterError(clientIp);
                continue;
            }

            if (!ValidateLabMessage(message))
            {
                _logger.LogWarning($"[INVALID MESSAGE] from {clientIp}: {message}");
                await SendResponse(stream, "\x15", ct); // NAK
                RegisterError(clientIp);
                continue;
            }

            _logger.LogInformation($"[VALID MESSAGE] from {clientIp}: {message}");
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
        // Parse message
        var parts = message.Split('|');
        var labMessage = new LabMessage
        {
            PatientId = parts[0],
            TestName = parts[1],
            Value = double.Parse(parts[2]),
            Unit = parts[3],
            Timestamp = DateTime.UtcNow
        };

        LabMessages.Add(labMessage);
        _lastReceivedMessage = DateTime.UtcNow;
        _logger.LogInformation($"Processed: {message}");
        await Task.CompletedTask;
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
        @"[;'""]",                        // Quotes/semi-colons
        @"(--|\/\*|\*\/)",               // SQL comments
        @"\b(drop|delete|insert|update|select|union)\b", // SQL keywords
        @"(<|>|\\)"                      // HTML/XML/escape
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

    private void RegisterError(string clientIp)
    {
        var stats = ClientStatistics.GetOrAdd(clientIp, new ClientStats());
        stats.ErrorCount++;

        if (stats.ErrorCount > 10)
        {
            _logger.LogWarning($"Too many errors from {clientIp}, now blocking.");
            BlockedIps.Add(clientIp);
        }
    }


    private async Task BlockClient(string ip, TcpClient client, CancellationToken ct, NetworkStream stream)
    {
        BlockedIps.Add(ip);
        _logger.LogWarning($"Blocked client: {ip}");
        await SendResponse(stream, "\x04", ct); // EOT
        client.Close();
    }

    private void ResetClientStats(string clientIp)
    {
        if (ClientStatistics.TryGetValue(clientIp, out var stats))
        {
            stats.ErrorCount = 0;
        }
    }



    // USB 
    private async Task SaveMessagesToJsonPeriodically(CancellationToken stoppingToken)
    {
        var idleTimeout = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var idle = (now - _lastReceivedMessage) > idleTimeout;

            if (idle && LabMessages.Count > 0)
            {
                _lastWriteStatus = "Writing to USB...";

                DriveInfo? usbDrive = null;

                try
                {
                    // 1. Try to use configured USB path if present
                    if (!string.IsNullOrWhiteSpace(_settings.UsbPath))
                    {
                        usbDrive = new DriveInfo(_settings.UsbPath);
                        if (!usbDrive.IsReady || usbDrive.DriveType != DriveType.Removable)
                        {
                            _logger.LogWarning("Configured USB path is invalid or not ready.");
                            usbDrive = null;
                        }
                    }

                    // 2. Fallback to automatic USB detection
                    if (usbDrive == null)
                    {
                        usbDrive = DriveInfo.GetDrives()
                            .FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
                    }

                    if (usbDrive == null)
                    {
                        _logger.LogWarning("⚠️ No USB drive detected.");
                        _lastWriteStatus = "USB not found";
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                        continue;
                    }

                    // 3. Attempt write with retry logic
                    int retryCount = 3;
                    while (retryCount-- > 0)
                    {
                        try
                        {
                            var timestamp = now.ToString("yyyyMMdd_HHmmss");
                            var filename = $"LabData_{timestamp}.json";
                            var path = Path.Combine(usbDrive.RootDirectory.FullName, filename);

                            var messagesSnapshot = LabMessages.ToArray();
                            var json = JsonSerializer.Serialize(messagesSnapshot, new JsonSerializerOptions { WriteIndented = true });

                            await File.WriteAllTextAsync(path, json, stoppingToken);
                            _logger.LogInformation($"✅ Saved {messagesSnapshot.Length} messages to USB at: {path}");

                            // Clear after successful write
                            while (!LabMessages.IsEmpty)
                                LabMessages.TryTake(out _);

                            _lastWriteStatus = $"Saved at {now:HH:mm:ss}";
                            _lastWriteTime = now;
                            break; // Success
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Retrying USB write... attempts left: {retryCount}");
                            _lastWriteStatus = $"Retrying write... {retryCount} attempts left";
                            await Task.Delay(2000, stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error detecting USB or writing file.");
                    _lastWriteStatus = "Error writing to USB";
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }


    public async Task<(bool success, string message)> TriggerManualSave()
    {
        await SaveMessagesToUsb(force: true);
        return (_lastWriteStatus.StartsWith("Saved"), _lastWriteStatus);
    }


    private async Task SaveMessagesToUsb(bool force = false)

    {
        try
        {
            var now = DateTime.UtcNow;

            // Skip if not forced and not idle
            if (!force && (now - _lastReceivedMessage) < TimeSpan.FromSeconds(30))
                return;

            _lastWriteStatus = "Manual USB write triggered...";

            DriveInfo? usbDrive = null;

            try
            {
                // 1. Try configured path
                if (!string.IsNullOrWhiteSpace(_settings.UsbPath))
                {
                    usbDrive = new DriveInfo(_settings.UsbPath);
                    if (!usbDrive.IsReady || usbDrive.DriveType != DriveType.Removable)
                    {
                        _logger.LogWarning("Configured USB path is invalid or not ready.");
                        usbDrive = null;
                    }
                }

                // 2. Fallback auto-detection
                if (usbDrive == null)
                {
                    usbDrive = DriveInfo.GetDrives()
                        .FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
                }

                if (usbDrive == null)
                {
                    _logger.LogWarning("❌ No USB drive detected.");
                    _lastWriteStatus = "Manual save failed: USB not found";
                    return;
                }

                // 3. Retry save logic
                int retryCount = 3;
                while (retryCount-- > 0)
                {
                    try
                    {
                        var timestamp = now.ToString("yyyyMMdd_HHmmss");
                        var filename = $"LabData_{timestamp}.json";
                        var path = Path.Combine(usbDrive.RootDirectory.FullName, filename);

                        var messagesSnapshot = LabMessages.ToArray();
                        var json = JsonSerializer.Serialize(messagesSnapshot, new JsonSerializerOptions { WriteIndented = true });

                        await File.WriteAllTextAsync(path, json);

                        _logger.LogInformation($"✅ Manual save: {messagesSnapshot.Length} messages to {path}");

                        while (!LabMessages.IsEmpty)
                            LabMessages.TryTake(out _);

                        _lastWriteTime = now;
                        _lastWriteStatus = $"Manual save at {now:HH:mm:ss}";
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Retrying manual USB write... attempts left: {retryCount}");
                        _lastWriteStatus = $"Retrying manual write... {retryCount} left";
                        await Task.Delay(2000);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during manual save");
                _lastWriteStatus = "Manual save failed: internal error";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in SaveMessagesToUsb");
        }
    }


    public static DateTime GetLastMessageTime() => _lastReceivedMessage;
    public static string GetLastWriteStatus() => _lastWriteStatus;
    public static DateTime GetLastWriteTime() => _lastWriteTime;


}
