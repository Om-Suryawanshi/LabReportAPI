using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

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
        var localIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
                        .AddressList.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
                        .ToString() ?? "Unknown";

        return Ok(new
        {
            Status = "Active",
            Protocol = "STX/ETX",
            Port = 12377,
            ServerIp = localIp,
            LastMessageReceivedAt = TcpListenerService.GetLastMessageTime(),
            LastWriteStatus = TcpListenerService.GetLastWriteStatus(),
            LastWriteTime = TcpListenerService.GetLastWriteTime()
        });
    }

    [HttpPost("save")]
    public async Task<IActionResult> TriggerSave()
    {
        if (TcpListenerService.Instance is null)
            return StatusCode(500, new { message = "TCP Listener service is not available." });

        var (success, message) = await TcpListenerService.Instance.TriggerManualSave();
        return Ok(new { success, message });
    }
}
