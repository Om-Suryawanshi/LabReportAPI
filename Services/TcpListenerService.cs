using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using LabReportAPI.Services;
using LabReportAPI.Models;


namespace LabReportAPI.Services
{

    public class TcpListenerService : BackgroundService
    {
        public static TcpListenerService? Instance { get; private set; }

        private readonly ILogger<TcpListenerService> _logger;
        private readonly ILogService _logService;
        private readonly LabSettings _settings;
        private TcpListener? _tcpListener;

        private readonly string _localIp = Dns.GetHostEntry(Dns.GetHostName())
        .AddressList
        .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?
        .ToString() ?? "127.0.0.1";

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

        public TcpListenerService(ILogger<TcpListenerService> logger, IOptions<LabSettings> settings, ILogService logService)
        {
            _logger = logger;
            _logService = logService;
            _settings = settings.Value;
            Instance = this;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var localEndPoint = new IPEndPoint(IPAddress.Parse(_localIp), Port);
            _tcpListener = new TcpListener(localEndPoint);

            try
            {
                _tcpListener.Start();
                _logger.LogInformation($"✅ TCP Server started on {_localIp}:{Port}");
                _logService.Log($"TCP Listener started", "INFO", "TcpListenerService");


                // Background task for saving data
                var saveTask = SaveMessagesToJsonPeriodically(stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var tcpClient = await _tcpListener.AcceptTcpClientAsync(stoppingToken);
                    _ = HandleClientAsync(tcpClient, stoppingToken);
                }

                _logger.LogInformation("🛑 Cancellation requested. Beginning graceful shutdown...");
                _logService.Log("TCP Listener Cancellation requested", "INFO", "🛑 Cancellation requested. Beginning graceful shutdown...");

            }
            catch (OperationCanceledException)
            {
                _logService.Log("TCP Listener Cancellation requested", "INFO", "🟡 TCP server is shutting down due to cancellation request.");
                _logger.LogInformation("🟡 TCP server is shutting down due to cancellation request.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ TCP listener fatal error");
                _logService.Log("TCP Listener fatal error", "ERROR", "❌ TCP listener fatal error.");

            }
            finally
            {
                try
                {
                    _tcpListener?.Stop();
                    _logger.LogInformation("🔌 TCP listener stopped.");
                    _logService.Log("TCP Listener stopped", "INFO", "🔌 TCP listener stopped.");

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error during TCP listener shutdown.");
                    _logService.Log("ERROR During TCP Listener Shutdown", "ERROR", "❌ Error during TCP listener shutdown....");

                }
            }
        }



        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var clientEndpoint = client.Client.RemoteEndPoint?.ToString();
            var clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();

            if (BlockedIps.Contains(clientIp))
            {
                _logger.LogWarning($"Blocked IP attempted connection: {clientIp}");
                _logService.Log("Blocked IP attempted connection", "WARNING", $"Blocked IP: {clientIp}");

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
                            // _logService.Log("Client disconnected gracefully", "INFO", $" IP: {clientIp}");

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
                    _logService.Log("Malformed message", "WARNING", $"Client IP: {clientIp}");
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
                    _logService.Log("MALICIOUS INPUT", "WARNING", $"Client IP: {clientIp}");
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
                    _logService.Log("INVALID MESSAGE", "WARNING", $"Client IP: {clientIp}");
                    await SendResponse(stream, "\x15", ct); // NAK
                    RegisterError(clientIp);
                    continue;
                }

                _logger.LogInformation($"[VALID MESSAGE] from {clientIp}: {message}");
                _logService.Log("Valid Message", "INFO", $"Client IP: {clientIp}");
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
            // _logService.Log("Processed", "INFO", $"Client IP: {clientIp}");

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
                        if (!string.IsNullOrWhiteSpace(_settings.UsbPath))
                        {
                            usbDrive = new DriveInfo(_settings.UsbPath);
                            if (!usbDrive.IsReady || usbDrive.DriveType != DriveType.Removable)
                            {
                                _logger.LogWarning("Configured USB path is invalid or not ready.");
                                _logService.Log("Configured USB path is invalid or not ready.", "WARNING", "Invalid Path");
                                usbDrive = null;
                            }
                        }

                        if (usbDrive == null)
                        {
                            usbDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
                        }

                        if (usbDrive == null)
                        {
                            _logger.LogWarning("⚠️ No USB drive detected.");
                            _logService.Log("No USB Drive detected.", "WARNING", "⚠️ No USB drive detected.");

                            _lastWriteStatus = "USB not found";
                            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                            continue;
                        }

                        var filename = "LabData.json";
                        var path = Path.Combine(usbDrive.RootDirectory.FullName, filename);

                        int retryCount = 3;
                        while (retryCount-- > 0)
                        {
                            try
                            {
                                var messagesSnapshot = LabMessages.ToArray();
                                List<LabMessage> allMessages = new();

                                if (File.Exists(path))
                                {
                                    var existingJson = await File.ReadAllTextAsync(path, stoppingToken);
                                    allMessages = JsonSerializer.Deserialize<List<LabMessage>>(existingJson) ?? new();
                                }

                                allMessages.AddRange(messagesSnapshot);
                                var updatedJson = JsonSerializer.Serialize(allMessages, new JsonSerializerOptions { WriteIndented = true });

                                await File.WriteAllTextAsync(path, updatedJson, stoppingToken);
                                _logger.LogInformation($"✅ Saved {messagesSnapshot.Length} messages to USB at: {path}");
                                _logService.Log("Data Saved.", "INFO", $"Data Saved to {path}");


                                while (!LabMessages.IsEmpty)
                                    LabMessages.TryTake(out _);

                                _lastWriteStatus = $"Saved at {now:HH:mm:ss}";
                                _lastWriteTime = now;
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"Retrying USB write... attempts left: {retryCount}");
                                _logService.Log("Retrying USB write...", "WARNING", "Retrying");
                                _lastWriteStatus = $"Retrying write... {retryCount} attempts left";
                                await Task.Delay(2000, stoppingToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error detecting USB or writing file.");
                        _logService.Log("Error detecting USB or writing file.", "WARNING", "❌ Error detecting USB or writing file...");
                        _lastWriteStatus = "Error writing to USB";
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }


        public async Task<(bool success, SaveResult result)> TriggerManualSave()
        {
            var result = await SaveMessagesToUsb(force: true);
            return (result.StatusCode == "SUCCESS", result);
        }



        private async Task<SaveResult> SaveMessagesToUsb(bool force = false)
        {
            int messagesSaved = 0;
            try
            {
                var now = DateTime.UtcNow;

                if (!force && (now - _lastReceivedMessage) < TimeSpan.FromSeconds(30))
                {
                    return new SaveResult
                    {
                        StatusCode = "TOO_SOON",
                        Message = "Manual save skipped: recent message received.",
                        MessagesSaved = 0
                    };

                }

                _lastWriteStatus = "Manual USB write triggered...";
                _logger.LogInformation("🟡 Manual save triggered at {Time}", DateTime.UtcNow);


                DriveInfo? usbDrive = null;

                try
                {
                    // 1. Configured path or fallback
                    if (!string.IsNullOrWhiteSpace(_settings.UsbPath))
                    {
                        usbDrive = new DriveInfo(_settings.UsbPath);
                        if (!usbDrive.IsReady || usbDrive.DriveType != DriveType.Removable)
                        {
                            _logger.LogWarning("Configured USB path is invalid or not ready.");
                            usbDrive = null;
                        }
                    }

                    if (usbDrive == null)
                    {
                        usbDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
                    }

                    if (usbDrive == null)
                    {
                        _logger.LogWarning("❌ No USB drive detected.");
                        _logService.Log("No USB drive detected.", "WARNING", "❌ No USB drive detected.");

                        _lastWriteStatus = "Manual save failed: USB not found";
                        return new SaveResult
                        {
                            StatusCode = "USB_NOT_FOUND",
                            Message = "Manual save failed: USB not found",
                            MessagesSaved = 0
                        };
                    }

                    var filename = "LabData.json";
                    var path = Path.Combine(usbDrive.RootDirectory.FullName, filename);

                    int retryCount = 3;
                    while (retryCount-- > 0)
                    {
                        try
                        {
                            var messagesSnapshot = LabMessages.ToArray();
                            messagesSaved = messagesSnapshot.Length;
                            List<LabMessage> allMessages = new();

                            if (File.Exists(path))
                            {
                                var existingJson = await File.ReadAllTextAsync(path);
                                allMessages = JsonSerializer.Deserialize<List<LabMessage>>(existingJson) ?? new();
                            }

                            allMessages.AddRange(messagesSnapshot);
                            var updatedJson = JsonSerializer.Serialize(allMessages, new JsonSerializerOptions { WriteIndented = true });

                            await File.WriteAllTextAsync(path, updatedJson);
                            _logger.LogInformation($"✅ Manual save: {messagesSnapshot.Length} messages appended to {path}");
                            _logService.Log("Manual save.", "INFO", "Manual save.");


                            while (!LabMessages.IsEmpty)
                                LabMessages.TryTake(out _);

                            _lastWriteTime = now;
                            _lastWriteStatus = $"Manual save at {now:HH:mm:ss}";
                            _logService.Log("Manual save.", "INFO", $"Manual save at {now:HH:mm:ss}");

                            return new SaveResult
                            {
                                StatusCode = messagesSaved == 0 ? "NO_MESSAGES" : "SUCCESS",
                                Message = $"Manual save at {now:HH:mm:ss}",
                                MessagesSaved = messagesSaved
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Retrying manual USB write... attempts left: {retryCount}");
                            _logService.Log("Retrying manual USB write.", "WARNING", "Retrying manual USB write...");

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
            return new SaveResult
            {
                StatusCode = "ERROR",
                Message = "Manual save failed: unexpected internal error",
                MessagesSaved = 0
            };
        }

        public static DateTime GetLastMessageTime() => _lastReceivedMessage;
        public static string GetLastWriteStatus() => _lastWriteStatus;
        public static DateTime GetLastWriteTime() => _lastWriteTime;
    }

}