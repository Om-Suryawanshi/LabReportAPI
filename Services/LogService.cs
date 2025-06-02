namespace LabReportAPI.Services
{
    public class LogService : ILogService
    {
        private readonly List<LogEntry> _logs = new();
        private readonly object _lock = new();

        public void Log(string message, string level = "INFO", string? context = null)
        {
            var entry = new LogEntry { Message = message, Level = level, Context = context };
            lock (_lock)
            {
                _logs.Add(entry);
                if (_logs.Count > 1000) _logs.RemoveAt(0); // keep memory usage in check
            }

            // Optional: Also write to file
            File.AppendAllText("logs/log.txt", $"{entry.Timestamp:o} [{level}] ({context}) {message}{Environment.NewLine}");
        }

        public IEnumerable<LogEntry> GetLogs()
        {
            lock (_lock)
            {
                return _logs.ToList();
            }
        }
    }
}
