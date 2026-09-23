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
    [Route("api/servermessages")]
    [ApiController]
    public class ServerMessagesController : Controller
    {
        private readonly Level5Context _context;
        public ServerMessagesController(Level5Context context)
        {
            _context = context;
        }

        //--------------------- HTTP GET ---------------------------------------------------

        [HttpGet]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<ActionResult<IEnumerable<ServerMessage>>> GetAllVersions()
        {
            return await _context.ServerMessages.OrderByDescending(x => x.Id).Take(5).ToListAsync();
        }
    }
}
