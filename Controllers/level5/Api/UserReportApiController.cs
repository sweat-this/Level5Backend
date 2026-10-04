using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Level5Backend.Models;
using Level5Backend.RateLimiting;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.RateLimiting;

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

        [EnableRateLimiting(UserReportRateLimitPolicy.Name)]
        [HttpPost]
        public async Task<ActionResult<User>> PostUserReport(UserReport userReport)
        {
            if (string.IsNullOrWhiteSpace(userReport.Report) || userReport.Report.Length > 255)
            {
                return BadRequest();
            }

            // Report submission deliberately remains anonymous-capable. Attribution is all-or-
            // nothing: only the complete signed claim pair issued by TokenController is trusted.
            // Local profile/body identity is never an authenticated server principal.
            userReport.Userid = null;
            userReport.UserName = null;
            if (TryGetAuthenticatedReporter(out int callerUserid, out string callerUserName))
            {
                userReport.Userid = callerUserid;
                userReport.UserName = callerUserName;
            }

            // Server-derived, never trust request values for network or receipt metadata.
            userReport.Ipaddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            userReport.Date = DateTime.UtcNow;

            // Exact-text deduplication only has meaning for authenticated legacy attribution.
            // Anonymous callers submitting the same text are not proven to be the same person.
            if (userReport.Userid.HasValue
                && await ReportTextExistsAsync(userReport.Userid.Value, userReport.Report))
            {
                return Conflict();
            }

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

        private bool TryGetAuthenticatedReporter(out int userid, out string userName)
        {
            userid = default;
            userName = string.Empty;

            if (User.Identity?.IsAuthenticated != true
                || !int.TryParse(User.FindFirst("Userid")?.Value, out userid))
            {
                return false;
            }

            string? usernameClaim = User.FindFirst("username")?.Value;
            if (string.IsNullOrWhiteSpace(usernameClaim))
            {
                userid = default;
                return false;
            }

            userName = usernameClaim;
            return true;
        }
    }
}
