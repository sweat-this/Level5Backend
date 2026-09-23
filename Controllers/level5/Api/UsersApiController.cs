
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Level5Backend.Models;
using Level5Backend.Models.Dto;
using Level5Backend.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Level5Backend.Controllers
{
    [EnableCors("ApiCors")]
    [Route("api/users")]
    [ApiController]
    public class UsersApiController : Controller
    {
        private readonly Level5Context _context;

        public UsersApiController(Level5Context context)
        {
            _context = context;
        }

        //--------------------- HTTP GET ---------------------------------------------------
        // GET: /api/highscores
        // get all users
        /// <summary>
        /// Get all users in database, paginated (defaults to the first 50, capped at 200 per page).
        /// Admin-only: this is a full directory listing (userid + username per account), not
        /// something every logged-in player should be able to page through.
        /// </summary>
        [Authorize(Policy = "RequireDev")]
        [HttpGet]
        public async Task<ActionResult<IEnumerable<User>>> GetAllUsers(int page = 0, int results = 50)
        {
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            var users = await _context.Users.AsNoTracking()
                .OrderBy(u => u.Userid)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
            foreach (User u in users)
            {
                HideUserDetails(u);
            }
            return users;
        }


        //--------------------- HTTP GET Userid ---------------------------------------------------
        // GET: /api/highscores/userid/{userid}
        /// <summary>
        /// Get user by user id. Admin-only: lets a caller look up an arbitrary account by id,
        /// which regular players have no legitimate need for.
        /// </summary>
        [Authorize(Policy = "RequireDev")]
        [HttpGet("userid/{userid}")]
        // GET: Users by userid
        // get user by user id
        public async Task<ActionResult<User>> GetUserById(int userid)
        {
            var user = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Userid == userid);
            if (user == null)
            {
                return NotFound();
            }

            HideUserDetails(user);

            return user;
        }

        //--------------------- HTTP GET Username ---------------------------------------------------
        // GET: /api/users/username/{userid}
        /// <summary>
        /// Get user by username. Used pre-login (looking a user up by name happens before the
        /// caller has a token), so this intentionally stays anonymous - but the password (and
        /// other sensitive fields) must never be part of the response.
        /// </summary>
        [ApiExplorerSettings(IgnoreApi = true)]
        [HttpGet("username/{username}")]
        public async Task<ActionResult<User>> GetUserByUsername(string username)
        {
            var user = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Username == username);
            if (user == null)
            {
                return NotFound();
            }

            HideUserDetails(user);

            return user;
        }

        //--------------------- HTTP GET Username ---------------------------------------------------
        // GET: /api/users/email/{email}
        /// <summary>
        /// Check whether an email is already registered. Used pre-registration (the game's
        /// EmailExists helper calls this anonymously and only ever looks at the 200-vs-404 status
        /// code - never the response body, unlike GetUserByUserName's equivalent), so this must
        /// stay anonymous. Returns no body at all (not even a masked User, unlike
        /// GetUserByUsername) - nothing about the matched account needs to be, or is, disclosed.
        /// </summary>
        [ApiExplorerSettings(IgnoreApi = true)]
        [HttpGet("email/{email}")]
        public async Task<IActionResult> GetUserByEmail(string email)
        {
            bool exists = await _context.Users.AsNoTracking().AnyAsync(m => m.Email == email);
            return exists ? NoContent() : NotFound();
        }

        //--------------------- HTTP PUT ---------------------------------------------------
        // PUT: api/users/5
        /// <summary>
        /// Update user data
        /// </summary>
        // Binds a UserUpdateDto rather than the User entity - Isdev (which grants the RequireDev
        // authorization policy) and Password must never be settable by a profile-edit request.
        [Authorize]
        [HttpPut("{id}")]
        public async Task<IActionResult> PutUser(int id, UserUpdateDto dto)
        {
            if (!CallerOwnsUserid(id))
            {
                return Forbid();
            }

            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            user.Username = dto.Username;
            user.Email = dto.Email;
            user.Firstname = dto.Firstname;
            user.Lastname = dto.Lastname;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        //--------------------- HTTP POST ---------------------------------------------------
        // POST: api/Highscores
        /// <summary>
        /// Create new user. Registration is inherently anonymous - there's no token to require yet.
        /// </summary>
        // Binds a UserRegisterDto rather than the User entity - Isdev must never be settable at
        // signup either, or any account could self-grant the RequireDev authorization policy.
        [EnableRateLimiting("RegisterPolicy")]
        [HttpPost]
        public async Task<ActionResult<User>> PostUser(UserRegisterDto dto)
        {
            if (await UserNameExistsAsync(dto.Username))
            {
                return BadRequest();
            }

            var user = new User
            {
                Username = dto.Username,
                Password = PasswordHashing.Hash(dto.Password),
                Email = dto.Email,
                Firstname = dto.Firstname,
                Lastname = dto.Lastname,
                Ipaddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Signupdate = DateTime.UtcNow.ToString("o"),
            };

            _context.Users.Add(user);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // The UserNameExistsAsync check above is inherently racy (check-then-insert, no
                // transaction) - two concurrent signups for the same username can both pass it and
                // then collide on the DB's unique index. Without this, the loser gets an unhandled
                // 500 instead of the same response a non-racing duplicate signup already gets above.
                if (await UserNameExistsAsync(dto.Username))
                {
                    return BadRequest();
                }
                throw;
            }

            HideUserDetails(user);
            return CreatedAtAction(nameof(GetUserById), new { userid = user.Userid }, user);
        }

        //--------------------- HTTP DELETE ---------------------------------------------------
        // DELETE: api/users/5
        /// <summary>
        /// Deletes user by user id.
        /// </summary>
        [Authorize]
        [HttpDelete("{id}")]
        public async Task<ActionResult<User>> DeleteUser(int id)
        {
            if (!CallerOwnsUserid(id))
            {
                return Forbid();
            }

            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return user;
        }

        //--------------------- UTILITY FUNCTIONS ---------------------------------------------------
        private async Task<bool> UserNameExistsAsync(string username)
        {
            return await _context.Users.AnyAsync(e => e.Username == username);
        }

        // the JWT issued by TokenController carries the authenticated user's id as a "Userid"
        // claim - mutating/deleting endpoints must confirm the caller matches the target account.
        private bool CallerOwnsUserid(int id)
        {
            var claim = User.FindFirst("Userid")?.Value;
            return int.TryParse(claim, out int callerUserid) && callerUserid == id;
        }

        private static void HideUserDetails(User users)
        {
            users.Firstname = "*************";
            users.Lastname = "*************";
            users.Email = "*************";
            users.Password = "*************";
            users.Ipaddress = "*************";
            users.Lastlogin = "*************";
            users.Signupdate = "*************";
        }
    }
}
