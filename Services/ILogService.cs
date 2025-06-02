namespace LabReportAPI.Services
{
    public interface ILogService
    {
        void Log(string message, string level = "INFO", string? context = null);
        IEnumerable<LogEntry> GetLogs();
    }
}
