using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Level5Backend.Models;
using Microsoft.AspNetCore.Cors;

namespace Level5Backend.Controllers
{
    [EnableCors("ApiCors")]
    [Route("api/serverstats")]
    [ApiController]
    public class ServerStatsController : ControllerBase
    {
        private readonly Level5Context _context;

        public ServerStatsController(Level5Context context)
        {
            _context = context;
        }

        //--------------------- HTTP GET ---------------------------------------------------
        // GET: /api/serverstats/current
        /// <summary>
        /// Get the most recently computed server stats snapshot. ServerStatsBackgroundService
        /// recomputes this every 10 minutes off the request path (see ServerStatsService) - this
        /// is the only way to read that data back out; the usernames/counts here are the same
        /// kind of public leaderboard data already exposed unmasked by /api/highscores.
        /// </summary>
        [HttpGet("current")]
        public async Task<ActionResult<ServerStat>> GetCurrentServerStats()
        {
            var stats = await _context.ServerStats
                .AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            if (stats == null)
            {
                return NotFound();
            }

            return stats;
        }
    }
}
