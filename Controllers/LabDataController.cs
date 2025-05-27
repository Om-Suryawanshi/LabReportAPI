using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("api/[controller]")]
public class LabDataController : ControllerBase
{
    private readonly ILogger<LabDataController> _logger;

    public LabDataController(ILogger<LabDataController> logger)
    {
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            Status = "Active",
            Protocol = "STX/ETX",
            Port = 12377,
            LastMessageReceivedAt = TcpListenerService.GetLastMessageTime(),
            LastWriteStatus = TcpListenerService.GetLastWriteStatus(),
            LastWriteTime = TcpListenerService.GetLastWriteTime()
        });
    }
    [HttpPost("save")]
    public IActionResult TriggerSave()
    {
        Task.Run(() => TcpListenerService.TriggerManualSave());
        return Ok(new { message = "Manual save triggered." });
    }

}
