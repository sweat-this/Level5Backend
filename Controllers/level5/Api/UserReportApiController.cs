using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Level5Backend.Models;
using Microsoft.AspNetCore.Cors;

namespace Level5Backend.Controllers
{
    [EnableCors("ApiCors")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Route("api/userreport")]
    [ApiController]
    public class UserReportApiController : Controller
    {
        private readonly Level5Context _context;
        private readonly ILogger<UserReportApiController> _logger;

        public UserReportApiController(Level5Context context, ILogger<UserReportApiController> logger)
        {
            _context = context;
            _logger = logger;
        }

        //--------------------- HTTP GET ---------------------------------------------------
        // GET: /api/highscores
        // get all users
        /// <summary>
        /// Get all user reports, paginated (defaults to the first 50, capped at 200 per page).
        /// </summary>
        [Authorize(Policy = "RequireDev")]
        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserReport>>> GetAllReports(int page = 0, int results = 50)
        {
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            return await _context.UserReports
                .OrderByDescending(r => r.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }

        [HttpPost]
        public async Task<ActionResult<User>> PostUserReport(UserReport userReport)
        {
            // Deliberately not [Authorize] - reports may legitimately come in before login (e.g. a
            // crash report). But when a valid token IS present, trust it over whatever Userid/
            // UserName the client put in the body - those fields aren't otherwise verified against
            // the caller at all.
            if (TryGetCallerUserid(out int callerUserid))
            {
                userReport.Userid = callerUserid;
                userReport.UserName = User.FindFirst("username")?.Value ?? userReport.UserName;
            }

            // server-derived, never trust whatever the client put in the request body - same as
            // Highscore.Ipaddress
            userReport.Ipaddress = HttpContext.Connection.RemoteIpAddress?.ToString();

            // empty text
            if (String.IsNullOrEmpty(userReport.Report))
            {
                return BadRequest();
            }
            // text exists
            if (await ReportTextExistsAsync(userReport.Userid, userReport.Report))
            {
                return Conflict();
            }

            userReport.Date = DateTime.UtcNow;
            try
            {
                _context.UserReports.Add(userReport);
                await _context.SaveChangesAsync();

                return CreatedAtAction(nameof(GetAllReports), new { id = userReport.Id }, userReport);
            }
            catch (DbUpdateException e)
            {
                // DbUpdateException, not DbUpdateConcurrencyException - UserReport has no
                // concurrency token configured, so a plain insert can never throw the latter; this
                // was catching an exception type that could never actually be raised here.
                _logger.LogWarning(e, "Failed to save user report");
                return BadRequest();
            }
        }

        // Scoped per-user rather than globally - two different users legitimately submitting the
        // same report text (e.g. "crashes on level 3") shouldn't conflict with each other; this is
        // meant to catch one user re-submitting (a broken retry, or spam), not coincidental phrasing.
        private async Task<bool> ReportTextExistsAsync(int userid, string report)
        {
            return await _context.UserReports.AnyAsync(e => e.Userid == userid && e.Report == report);
        }

        // the JWT issued by TokenController carries the authenticated user's id as a "Userid"
        // claim - false here just means no valid token was presented, not an error.
        private bool TryGetCallerUserid(out int userid)
        {
            var claim = User.FindFirst("Userid")?.Value;
            return int.TryParse(claim, out userid);
        }
    }
}
