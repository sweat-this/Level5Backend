using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Level5Backend.Models;
using Microsoft.AspNetCore.Cors;

namespace Level5Backend.Controllers
{
    [EnableCors("ApiCors")]
    [Route("api/application")]
    [ApiController]
    public class ApplicationController : ControllerBase
    {
        private readonly Level5Context _context;

        public ApplicationController(Level5Context context)
        {
            _context = context;
        }

        //--------------------- HTTP GET ---------------------------------------------------
        // GET: /api/highscores
        /// <summary>
        /// Get all application versions
        /// </summary>
        [HttpGet("version")]
        public async Task<IEnumerable<object>> GetAllVersionsAsync()
        {
            return await _context.Application.OrderByDescending(x => x.Id)
                .ToListAsync();
        }

        //--------------------- HTTP GET ---------------------------------------------------
        // GET: /api/highscores
        /// <summary>
        /// Get current application versions
        /// </summary>
        [HttpGet("version/current")]
        public async Task<ActionResult<object>> GetCurrentVersion()
        {
            var version = await _context.Application
                .OrderByDescending(x => x.Id)
                .Select(x => x.CurrentVersion)
                .FirstOrDefaultAsync();

            if (version == null)
            {
                return NotFound();
            }

            return version;
        }

        //--------------------- HTTP POST new application version ---------------------------------------------------
        // POST: api/Highscores
        /// <summary>
        /// Add new application version
        /// </summary>
        [Authorize(Policy = "RequireDev")]
        [Route("version")]
        [ApiExplorerSettings(IgnoreApi = true)]
        [HttpPost]
        public async Task<ActionResult<Application>> PostApplicationVersion(Application application)
        {
            if (string.IsNullOrEmpty(application.CurrentVersion)
                || await _context.Application.AnyAsync(e => e.CurrentVersion == application.CurrentVersion))
            {
                return BadRequest();
            }
            else
            {
                _context.Application.Add(application);
                await _context.SaveChangesAsync();

                return CreatedAtAction(nameof(GetAllVersionsAsync), new { id = application.Id }, application);
            }
        }
    }
}

