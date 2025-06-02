public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
    public string? Context { get; set; }
}
