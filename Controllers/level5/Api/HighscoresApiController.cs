using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Level5Backend.Models;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Authorization;
using System.Dynamic;

namespace Level5Backend.Controllers
{
    //[Authorize]
    [ApiController]
    [EnableCors("ApiCors")]
    [Route("api/highscores")]

    public class HighscoresApiController : ControllerBase
    {
        private readonly Level5Context _context;

        public HighscoresApiController(Level5Context context)
        {
            _context = context;
        }

        //--------------------- HTTP GET ---------------------------------------------------
        // GET: /api/highscores?page=0&results=50
        /// <summary>
        /// Get all high scores, paginated (defaults to the first 50, capped at 200 per page).
        /// </summary>
        [EnableCors("ApiCors")]
        [HttpGet(Name = "GetHighScores")]
        public async Task<IEnumerable<Highscore>> GetAllHighscores(int page = 0, int results = 50)
        {
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            var highscores = await _context.Highscores.AsNoTracking()
                 .OrderByDescending(x => x.Id)
                 .Skip(skip)
                 .Take(take)
                 .ToListAsync();
            HideHighScoreDetails(highscores);

            return highscores;
        }

        //--------------------- HTTP GET  Platform ---------------------------------------------------
        // GET: /api/highscores/platform/{platform}?page=0&results=50
        /// <summary>
        /// Get high scores by platform [handheld, desktop], paginated (defaults to the first 50, capped at 200 per page).
        /// </summary>
        [EnableCors("ApiCors")]
        [HttpGet("platform/{platform}")]
        public async Task<ActionResult<IEnumerable<Highscore>>> GetHighScoreByPlatform(string platform, int page = 0, int results = 50)
        {
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            var highscores = await _context.Highscores.AsNoTracking()
                .Where(x => x.Platform == platform)
                .OrderByDescending(x => x.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
            HideHighScoreDetails(highscores);

            return highscores;
        }

        //--------------------- HTTP GET  Modeid by Userid ---------------------------------------------------
        // GET: /api/highscores/modeid/1/userid/1?page=0&results=50
        /// <summary>
        /// Get high scores by mode id and user id, paginated (defaults to the first 50, capped at 200 per page).
        /// </summary>
        [HttpGet("modeid/{modeid}/userid/{userid}")]
        public async Task<ActionResult<IEnumerable<Highscore>>> GetHighScoreByModeIdUserId(int modeid, int userid, int page = 0, int results = 50)
        {
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            var highscores = await _context.Highscores.AsNoTracking()
                .Where(x => x.Modeid == modeid && x.Userid == userid)
                .OrderByDescending(x => x.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync();

            HideHighScoreDetails(highscores);

            return highscores;
        }
        //--------------------- HTTP GET Modeid by Platform ---------------------------------------------------
        // GET: /api/highscores/modeid/1/platform/1?page=0&results=50
        /// <summary>
        /// Get high scores by mode id and platform, paginated (defaults to the first 50, capped at 200 per page).
        /// </summary>
        [HttpGet("modeid/{modeid}/platform/{platform}")]
        public async Task<ActionResult<IEnumerable<Highscore>>> GetHighScoreByModeIdPlatform(int modeid, string platform, int page = 0, int results = 50)
        {
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            var highscores = await _context.Highscores.AsNoTracking()
                .Where(x => x.Modeid == modeid && x.Platform == platform)
                .OrderByDescending(x => x.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync();

            HideHighScoreDetails(highscores);

            return highscores;
        }

        // Which stat a mode id is ranked by. Centralizes the mode-id groupings that used to be
        // duplicated across the Filtered/All endpoints below (they were kept in exact sync by hand);
        // applyHighscoreDefaults's per-modeid display-name switch is a separate, legitimately 1:1 lookup
        // (several modeids sharing a metric still get distinct display names) so it isn't folded in.
        private enum ScoreMetric { TotalPoints, MaxShotMade, TotalDistance, Time, ConsecutiveShots, EnemiesKilled }

        private static ScoreMetric? GetScoreMetric(int modeid) => modeid switch
        {
            1 => ScoreMetric.TotalPoints,
            > 14 and < 20 => ScoreMetric.TotalPoints,
            23 or 24 or 26 => ScoreMetric.TotalPoints,
            > 1 and < 5 => ScoreMetric.MaxShotMade,
            6 => ScoreMetric.TotalDistance,
            > 6 and < 10 => ScoreMetric.Time,
            25 => ScoreMetric.Time,
            14 => ScoreMetric.ConsecutiveShots,
            20 or 21 or 22 => ScoreMetric.EnemiesKilled,
            _ => null
        };

        //--------------------- HTTP GET  Modeid by Modeid - Filtered  ---------------------------------------------------
        // GET: /api/highscores/modeid/{modeid}?hardcore={int}&traffic={int}&enemies={int}
        /// <summary>
        /// Get high scores by mode id and optional filters. [hardcoreEnabled, trafficEnabled, enemiesEnabled, sniperEnabled]
        /// </summary>
        [HttpGet("modeid/filter/{modeid}")]
        public async Task<ActionResult<IEnumerable<Object>>> GetHighScoreByModeIdForGameDisplayFiltered(int modeid,
            int hardcore,
            int traffic,
            int enemies,
            int sniper,
            int page,
            int results)
        {
            // results was previously passed straight into Take() with no upper bound, and page
            // could go negative into Skip() - both directly attacker-controlled on an unauthenticated
            // endpoint. Clamped the same way GetAllHighscores already is.
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            var task = QueryHighScoresByMetric(modeid, hardcore, traffic, sniper, enemies, skip, take);
            if (task == null)
            {
                return NotFound();
            }

            return await task;
        }

        //--------------------- HTTP GET  Modeid by Modeid - All  ---------------------------------------------------
        // GET: /api/highscores/modeid/{modeid}?hardcore={int}&traffic={int}&enemies={int}
        /// <summary>
        /// Get all high scores for specific game mode by mode id
        /// </summary>
        [HttpGet("modeid/all/{modeid}")]
        public async Task<ActionResult<IEnumerable<Object>>> GetHighScoreByModeIdForGameDisplayAll(int modeid,
            int page,
            int results)
        {
            int take = Math.Clamp(results, 1, 200);
            int skip = Math.Max(page, 0) * take;

            var task = QueryHighScoresByMetric(modeid, null, null, null, null, skip, take);
            if (task == null)
            {
                return NotFound();
            }

            return await task;
        }

        // Null filter arguments mean "unfiltered" (the "All" endpoint above); non-null values come
        // from the "Filtered" endpoint. Returns null when modeid doesn't map to any known metric.
        private Task<List<object>>? QueryHighScoresByMetric(int modeid, int? hardcore, int? traffic, int? sniper, int? enemies, int skip, int take)
        {
            var metric = GetScoreMetric(modeid);
            if (metric == null)
            {
                return null;
            }

            return GetByMetric(metric.Value, modeid, hardcore, traffic, sniper, enemies, skip, take);
        }

        private static IQueryable<Highscore> ApplyOptionalFilters(IQueryable<Highscore> query, int? hardcore, int? traffic, int? sniper, int? enemies)
        {
            if (hardcore.HasValue) query = query.Where(x => x.HardcoreEnabled == hardcore.Value);
            if (traffic.HasValue) query = query.Where(x => x.TrafficEnabled == traffic.Value);
            if (sniper.HasValue) query = query.Where(x => x.SniperEnabled == sniper.Value);
            if (enemies.HasValue) query = query.Where(x => x.EnemiesEnabled == enemies.Value);
            return query;
        }

        // Replaces what used to be six near-identical GetByXxx methods (one per ScoreMetric) that
        // only differed in which field they sorted/labeled as "Score". Ordering/filtering/paging
        // still happen server-side in SQL exactly as before - only the final per-row shaping (which
        // varies by metric) moves into memory, over just the `take` rows already fetched.
        private async Task<List<object>> GetByMetric(ScoreMetric metric, int modeid, int? hardcore, int? traffic, int? sniper, int? enemies, int skip, int take)
        {
            IQueryable<Highscore> query = _context.Highscores.Where(x => x.Modeid == modeid);

            // Mirrors GetByEnemiesKilled's original behavior exactly: for that metric, hardcore==0
            // (the Filtered endpoint's default) only filters by modeid; any other hardcore value -
            // including null, which the "All" endpoint always passes - applies the rest of the
            // filters too. Every other metric always applies whatever filters were given.
            bool applyFilters = metric != ScoreMetric.EnemiesKilled || (hardcore.HasValue && hardcore.Value != 0);
            if (applyFilters)
            {
                query = ApplyOptionalFilters(query, hardcore, traffic, sniper, enemies);
            }

            query = metric switch
            {
                ScoreMetric.TotalPoints => query.OrderByDescending(x => x.TotalPoints),
                ScoreMetric.MaxShotMade => query.OrderByDescending(x => x.MaxShotMade),
                ScoreMetric.TotalDistance => query.OrderByDescending(x => x.TotalDistance),
                ScoreMetric.Time => query.OrderBy(x => x.Time),
                ScoreMetric.ConsecutiveShots => query.OrderByDescending(x => x.ConsecutiveShots),
                ScoreMetric.EnemiesKilled => query.OrderByDescending(x => x.EnemiesKilled),
                _ => query
            };

            var rows = await query
                .Select(x => new
                {
                    x.Character,
                    x.Level,
                    x.Date,
                    x.Time,
                    UserId = x.Userid.ToString(),
                    x.Username,
                    x.HardcoreEnabled,
                    x.EnemiesEnabled,
                    x.TrafficEnabled,
                    x.EnemiesKilled,
                    x.Platform,
                    x.TotalPoints,
                    x.MaxShotMade,
                    x.TotalDistance,
                    x.ConsecutiveShots
                })
                .Skip(skip)
                .Take(take)
                .ToListAsync();

            // Built as a dictionary rather than a single fixed anonymous type because each metric's
            // original shape differs: TotalPoints/MaxShotMade/TotalDistance/ConsecutiveShots each
            // add one extra field named after themselves, Time and EnemiesKilled don't (they're
            // already present in the common fields below), and Time is a raw number only for the
            // Time metric - a string everywhere else. This preserves each metric's exact original
            // wire shape instead of changing what existing clients parse.
            //
            // Keys are written in camelCase explicitly - unlike the anonymous types this replaces,
            // a Dictionary's keys are literal JSON property names that ASP.NET Core's default
            // PropertyNamingPolicy (CamelCase) does NOT rewrite, so a PascalCase key here would
            // silently change the wire format every existing client parses.
            return rows.Select(x =>
            {
                IDictionary<string, object?> row = new ExpandoObject();
                row["score"] = metric switch
                {
                    ScoreMetric.TotalPoints => x.TotalPoints.ToString(),
                    ScoreMetric.MaxShotMade => x.MaxShotMade.ToString(),
                    ScoreMetric.TotalDistance => x.TotalDistance.ToString(),
                    ScoreMetric.Time => x.Time.ToString(),
                    ScoreMetric.ConsecutiveShots => x.ConsecutiveShots.ToString(),
                    ScoreMetric.EnemiesKilled => x.EnemiesKilled.ToString(),
                    _ => string.Empty
                };
                row["character"] = x.Character;
                row["level"] = x.Level;
                row["date"] = x.Date;
                row["time"] = metric == ScoreMetric.Time ? x.Time : x.Time.ToString();
                row["userId"] = x.UserId;
                row["username"] = x.Username;
                row["hardcoreEnabled"] = x.HardcoreEnabled;
                row["enemiesEnabled"] = x.EnemiesEnabled;
                row["trafficEnabled"] = x.TrafficEnabled;
                row["enemiesKilled"] = x.EnemiesKilled;
                row["platform"] = x.Platform;

                switch (metric)
                {
                    case ScoreMetric.TotalPoints: row["totalPoints"] = x.TotalPoints; break;
                    case ScoreMetric.MaxShotMade: row["maxShotMade"] = x.MaxShotMade; break;
                    case ScoreMetric.TotalDistance: row["totalDistance"] = x.TotalDistance; break;
                    case ScoreMetric.ConsecutiveShots: row["consecutiveShots"] = x.ConsecutiveShots; break;
                        // Time and EnemiesKilled: no extra field, matching the original per-metric shapes.
                }

                return (object)row;
            }).ToList();
        }

        //--------------------- HTTP PUT ---------------------------------------------------
        // PUT: api/Highscores/scoreid/5
        /// <summary>
        /// Replace an existing high score's data. Despite the name, this does not insert - a
        /// scoreid that doesn't already exist returns 404 (see the DbUpdateConcurrencyException
        /// handling below).
        /// </summary>
        [Authorize]
        [HttpPut("scoreid/{scoreid}")]
        public async Task<IActionResult> PutHighscore(string scoreid, Highscore highscores)
        {
            if (scoreid != highscores.Scoreid)
            {
                return BadRequest();
            }

            if (!TryGetCallerUserid(out int callerUserid) || highscores.Userid != callerUserid)
            {
                return Forbid();
            }

            // The route/lookup key is scoreid, not Id - the client is never asked to know or send
            // the server-assigned numeric Id, so highscores.Id here is always the default (0).
            // Setting EntityState.Modified directly on it would generate "UPDATE ... WHERE id = 0",
            // which matches no row and throws DbUpdateConcurrencyException on every single call.
            // Loading the tracked row by scoreid first and copying values onto it (which carries the
            // real Id) is what makes the update actually target the right row.
            var existing = await _context.Highscores.FirstOrDefaultAsync(h => h.Scoreid == scoreid);
            if (existing == null)
            {
                return NotFound();
            }

            // server-derived, never trust whatever the client put in the request body - same as
            // PostHighscore/PostUnSubmittedHighscore, this was previously only enforced on create,
            // so a PUT could still overwrite it with an arbitrary client-supplied value
            highscores.Ipaddress = HttpContext.Connection.RemoteIpAddress?.ToString();

            // If the client's PUT body omits modeName/sniperModeName/difficulty (not every caller
            // round-trips every field), this backfills them instead of overwriting the existing
            // value with null/0 - SetValues below writes every property as given.
            applyHighscoreDefaults(highscores);

            // Id is part of the key, so SetValues refuses to touch it if the source and target
            // disagree - highscores.Id is always 0 (the client never sends it), so it has to be
            // aligned with the real Id before copying the rest of the properties across.
            highscores.Id = existing.Id;
            _context.Entry(existing).CurrentValues.SetValues(highscores);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // A genuine race - existing was deleted between the lookup above and SaveChanges.
                if (!ScoreIdExists(scoreid))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }
        //--------------------- HTTP POST Unsubmitted Highscores ---------------------------------------------------
        // POST: api/Highscores
        /// <summary>
        /// Create new high score
        /// </summary>
        /// 
        [Authorize]
        [EnableCors("ApiCors")]
        [HttpPost]
        [Route("unsubmitted")]
        public async Task<ActionResult<List<Highscore>>> PostUnSubmittedHighscore([FromBody] List<Highscore> highscores)
        {
            if (highscores == null) { return BadRequest(); }

            if (!TryGetCallerUserid(out int callerUserid) || !await _context.Users.AnyAsync(u => u.Userid == callerUserid))
            {
                return Forbid();
            }

            // one batched lookup instead of two queries per item (was N+1 before)
            var incomingScoreIds = highscores.Select(h => h.Scoreid).ToList();
            var existingScoreIds = (await _context.Highscores
                .Where(e => incomingScoreIds.Contains(e.Scoreid))
                .Select(e => e.Scoreid)
                .ToListAsync())
                .ToHashSet();

            List<Highscore> list = new List<Highscore>();
            string? callerIp = HttpContext.Connection.RemoteIpAddress?.ToString();

            foreach (var highscore in highscores)
            {
                // skip (not abort) anything that's a duplicate, missing a username, or doesn't
                // belong to the authenticated caller - one bad item shouldn't drop the rest of
                // the batch, which is what the previous "break" did.
                if (existingScoreIds.Contains(highscore.Scoreid)
                    || string.IsNullOrEmpty(highscore.Username)
                    || highscore.Userid != callerUserid)
                {
                    continue;
                }

                // server-derived, never trust whatever the client put in the request body
                highscore.Ipaddress = callerIp;
                applyHighscoreDefaults(highscore);
                _context.Highscores.Add(highscore);
                list.Add(highscore);
            }

            await _context.SaveChangesAsync();
            return list;
        }

        //--------------------- HTTP POST Highscore ---------------------------------------------------
        // POST: api/Highscores
        /// <summary>
        /// Create new high score
        /// </summary>
        [Authorize]
        [HttpPost]
        public async Task<ActionResult<Highscore>> PostHighscore([FromBody] Highscore highscore)
        {
            if (!TryGetCallerUserid(out int callerUserid) || highscore.Userid != callerUserid)
            {
                return Forbid();
            }

            // check if unique scoreid already exists in database
            if (await _context.Highscores.AnyAsync(e => e.Scoreid == highscore.Scoreid))
            {
                return Conflict();
            }
            // if empty Username or userid NOT in user table
            if (string.IsNullOrEmpty(highscore.Username) || !await _context.Users.AnyAsync(e => e.Userid == highscore.Userid))
            {
                return BadRequest();
            }

            // server-derived, never trust whatever the client put in the request body
            highscore.Ipaddress = HttpContext.Connection.RemoteIpAddress?.ToString();

            applyHighscoreDefaults(highscore);
            _context.Highscores.Add(highscore);
            await _context.SaveChangesAsync();

            // ServerStats is recomputed periodically by ServerStatsBackgroundService, not inline here -
            // it used to run synchronously on every single POST, scanning the entire Highscores table.

            return CreatedAtAction(nameof(GetAllHighscores), new { id = highscore.Id }, highscore);
        }

        //--------------------- HTTP DELETE HighScore ---------------------------------------------------
        /// <summary>
        /// Delete high score by score id
        /// </summary>
        [Authorize]
        [HttpDelete("{id}")]
        public async Task<ActionResult<Highscore>> DeleteHighscore(int id)
        {
            var highscores = await _context.Highscores.FindAsync(id);
            if (highscores == null)
            {
                return NotFound();
            }

            if (!TryGetCallerUserid(out int callerUserid) || highscores.Userid != callerUserid)
            {
                return Forbid();
            }

            _context.Highscores.Remove(highscores);
            await _context.SaveChangesAsync();

            return highscores;
        }

        //--------------------- UTILITY FUNCTIONS ---------------------------------------------------
        private bool ScoreIdExists(string scoreid)
        {
            return _context.Highscores.Any(e => e.Scoreid == scoreid);
        }

        // the JWT issued by TokenController carries the authenticated user's id as a "Userid"
        // claim - mutating endpoints use this to confirm the caller owns the score being touched.
        private bool TryGetCallerUserid(out int userid)
        {
            var claim = User.FindFirst("Userid")?.Value;
            return int.TryParse(claim, out userid);
        }

        /// <summary>
        /// Get # high scores for game mode by mode id
        /// </summary>
        [HttpGet("modeid/count")]
        public async Task<ActionResult<IEnumerable<Object>>> ModePlayedCount(int modeid)
        {
            var modeidList = _context.Highscores
                .GroupBy(e => e.Modeid)
                .Select(e => new { Modeid = e.Key, Count = e.Count() }).ToListAsync();
            return await modeidList;
        }

        //--------------------- HTTP GET  Modeid by Modeid ---------------------------------------------------
        // GET: /api/highscores/modeid/{modeid}?hardcore={int}&traffic={int}&enemies={int}
        /// <summary>
        /// Get # high scores for game mode by mode id with optional filters
        /// </summary>
        [HttpGet("modeid/count/{modeid}")]
        public async Task<ActionResult<object>> GetHighScoreCountByModeId(int modeid,
            int hardcore,
            int traffic,
            int sniper,
            int enemies)
        {
            var count = await _context.Highscores
                .Where(x => x.Modeid == modeid
                && x.HardcoreEnabled == hardcore
                && x.TrafficEnabled == traffic
                && x.SniperEnabled == sniper
                && x.EnemiesEnabled == enemies)
                .Select(x => x.Id)
                .CountAsync();

            return count;
        }

        private void applyHighscoreDefaults(Highscore highscores)
        {
            // if modename is null, insert based on modeid
            if (String.IsNullOrEmpty(highscores.ModeName))
            {
                {
                    switch (highscores.Modeid)
                    {
                        case 1:
                            highscores.ModeName = "Total Points";
                            break;
                        case 2:
                            highscores.ModeName = "Total 3 Pointers";
                            break;
                        case 3:
                            highscores.ModeName = "Total 4 Pointers";
                            break;
                        case 4:
                            highscores.ModeName = "Total 7 Pointers";
                            break;
                        case 6:
                            highscores.ModeName = "Total Distance";
                            break;
                        case 7:
                            highscores.ModeName = "Spot up some 3s";
                            break;
                        case 8:
                            highscores.ModeName = "Spot up some 4s";
                            break;
                        case 9:
                            highscores.ModeName = "Spot up some All";
                            break;
                        case 10:
                            highscores.ModeName = "Moneyball 3s";
                            break;
                        case 11:
                            highscores.ModeName = "Moneyball 4s";
                            break;
                        case 12:
                            highscores.ModeName = "Moneyball All";
                            break;
                        case 14:
                            highscores.ModeName = "Consecutive Shots";
                            break;
                        case 15:
                            highscores.ModeName = "In the Pocket";
                            break;
                        case 16:
                            highscores.ModeName = "3 point Contest";
                            break;
                        case 17:
                            highscores.ModeName = "4 point Contest";
                            break;
                        case 18:
                            highscores.ModeName = "All point Contest";
                            break;
                        case 19:
                            highscores.ModeName = "Points by Distance";
                            break;
                        case 20:
                            highscores.ModeName = "Bash up some Nerds";
                            break;
                        case 21:
                            highscores.ModeName = "Battle Royal";
                            break;
                        case 22:
                            highscores.ModeName = "Cage Match";
                            break;
                        case 23:
                            highscores.ModeName = "Versus";
                            break;
                        case 24:
                            highscores.ModeName = "7 point Contest";
                            break;
                        case 25:
                            highscores.ModeName = "Spot up some 7s";
                            break;
                        case 26:
                            highscores.ModeName = "Beat tha Computahs";
                            break;
                        case 98:
                            highscores.ModeName = "Arcade";
                            break;
                        case 99:
                            highscores.ModeName = "Free Play";
                            break;
                        default:
                            highscores.ModeName = "none";
                            break;
                    }
                }
                // No SaveChanges here - every call site persists this via its own SaveChangesAsync()
                // right after (Add() for the POST endpoints, CurrentValues.SetValues() for
                // PutHighscore), so a SaveChanges call in here would either do nothing yet (POST) or
                // be redundant (PUT). It used to run once per loop iteration in
                // PostUnSubmittedHighscore, which meant a sync, blocking round-trip that flushed
                // previously-added-but-unsaved rows
                // early - the batch's single SaveChangesAsync() after the loop is enough.
            }

            // Same idea as ModeName above: fall back to the DB column's own default instead of
            // requiring every client to send this explicitly.
            if (String.IsNullOrEmpty(highscores.SniperModeName))
            {
                highscores.SniperModeName = "none";
            }

            highscores.Difficulty ??= 1;
        }

        private static void HideHighScoreDetails(List<Highscore> highscores)
        {
            foreach (Highscore h in highscores)
            {
                h.Os = "*************";
                h.Scoreid = "*************";
                h.Device = "*************";
                h.Ipaddress = "*************";
            }
        }
    }
}


