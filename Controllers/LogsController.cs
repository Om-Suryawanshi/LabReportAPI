using LabReportAPI.Services;
using LabReportAPI.Models;
using Microsoft.AspNetCore.Mvc;

namespace LabReportAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LogsController : ControllerBase
    {
        private readonly ILogService _logService;

        public LogsController(ILogService logService)
        {
            _logService = logService;
        }

        [HttpGet]
        public ActionResult<IEnumerable<LogEntry>> GetLogs()
        {
            return Ok(_logService.GetLogs());
        }
    }
}
