namespace LabReportAPI.Models
{
    public class SaveResult
    {
        public string StatusCode { get; set; } = "";
        public string Message { get; set; } = "";
        public int MessagesSaved { get; set; }
    }
}