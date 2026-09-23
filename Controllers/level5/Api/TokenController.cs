
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Level5Backend.Utility;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Level5Backend.Models;
using Level5Backend.Models.Dto;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.RateLimiting;

namespace Level5Backend.Controllers
{
    [EnableCors("ApiCors")]
    [Route("api/token")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ApiController]
    public class TokenController : Controller
    {
        public IConfiguration _configuration;
        private readonly Level5Context _context;

        public TokenController(IConfiguration config, Level5Context context)
        {
            _configuration = config;
            _context = context;
        }
        /// <summary>
        /// Get bearer token for in game game log in and high score post
        /// </summary>
        [EnableRateLimiting("LoginPolicy")]
        [HttpPost]
        public async Task<ActionResult> Post(UserLoginDto _userData)
        {
            var user = await GetUser(_userData.Username, _userData.Password);

            if (user == null)
            {
                return BadRequest("Invalid credentials");
            }

            //create claims details based on the user information
            var claims = new[] {
                new Claim(JwtRegisteredClaimNames.Sub, _configuration["Jwt:Subject"] ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                // "iat" is a registered NumericDate claim (RFC 7519) - it must be serialized as a
                // JSON number, not the human-readable date string this used to produce, or newer
                // JWT parsers reject the token outright while reading it.
                new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim("Userid",user.Userid.ToString()),
                // gates the "RequireDev" authorization policy used by the remaining admin-only endpoints
                new Claim("IsDev", (user.Isdev == 1).ToString().ToLowerInvariant()),
                new Claim("Firstname", user.Firstname ?? string.Empty),
                new Claim("Lastname", user.Lastname ?? string.Empty),
                new Claim("username", user.Username),
                new Claim( "email", user.Email)
               };

            // Jwt:Key is validated non-empty at startup (see Program.cs), so it's never null here.
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));

            var signIn = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(_configuration["Jwt:Issuer"], _configuration["Jwt:Audience"], claims, expires: DateTime.UtcNow.AddDays(1), signingCredentials: signIn);

            return Ok(new JwtSecurityTokenHandler().WriteToken(token));
        }

        private async Task<User?> GetUser(string username, string password)
        {
            // Password is a PBKDF2 hash (see PasswordHashing/UsersApiController.PostUser) - it is
            // never compared with == against the raw submitted password.
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null || !PasswordHashing.Verify(user.Password, password))
            {
                return null;
            }

            return user;
        }
    }
}
