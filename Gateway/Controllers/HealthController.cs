using Microsoft.AspNetCore.Mvc;

namespace Gateway.Controllers
{
    [ApiController]
    [Route("health")]
    public class HealthController : ControllerBase
    {
        [HttpGet("live")]
        public IActionResult GetHealthLive()
        {
            return Ok();
        }
    }
}

