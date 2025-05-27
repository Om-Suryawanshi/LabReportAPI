public class LabMessage
{
    public string PatientId { get; set; } = "";
    public string TestName { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
