namespace LabReportAPI.Services
{
    public class LogService : ILogService
    {
        private readonly List<LogEntry> _logs = new();
        private readonly object _lock = new();
        private const string LogFilePath = "logs/log.txt";

        public LogService()
        {
            Directory.CreateDirectory("logs");

            // Load existing logs from file at startup
            if (File.Exists(LogFilePath))
            {
                var lines = File.ReadAllLines(LogFilePath);
                foreach (var line in lines)
                {
                    var entry = ParseLogLine(line);
                    if (entry != null)
                        _logs.Add(entry);
                }
            }
        }

        public void Log(string message, string level = "INFO", string? context = null)
        {
            var entry = new LogEntry
            {
                Message = message,
                Level = level,
                Context = context,
                Timestamp = DateTime.UtcNow
            };

            lock (_lock)
            {
                _logs.Add(entry);
                if (_logs.Count > 10000) _logs.RemoveAt(0); // optional trim

                File.AppendAllText(LogFilePath,
                    $"{entry.Timestamp:o} [{entry.Level}] ({entry.Context}) {entry.Message}{Environment.NewLine}");
            }
        }

        public IEnumerable<LogEntry> GetLogs()
        {
            lock (_lock)
            {
                return _logs.ToList();
            }
        }

        private LogEntry? ParseLogLine(string line)
        {
            try
            {
                // Format: 2025-06-02T12:34:56.7890000Z [INFO] (Context) Message
                var timestampEnd = line.IndexOf('[') - 1;
                var timestamp = DateTime.Parse(line.Substring(0, timestampEnd).Trim());

                var levelStart = line.IndexOf('[') + 1;
                var levelEnd = line.IndexOf(']');
                var level = line.Substring(levelStart, levelEnd - levelStart);

                var contextStart = line.IndexOf('(', levelEnd) + 1;
                var contextEnd = line.IndexOf(')', contextStart);
                var context = line.Substring(contextStart, contextEnd - contextStart);

                var messageStart = line.IndexOf(')', contextEnd) + 2;
                var message = line.Substring(messageStart);

                return new LogEntry
                {
                    Timestamp = timestamp,
                    Level = level,
                    Context = context,
                    Message = message
                };
            }
            catch
            {
                // Ignore malformed lines
                return null;
            }
        }
    }
}
