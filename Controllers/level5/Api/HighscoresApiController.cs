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
        // duplicated across the Filtered/All endpoints below (they were kept in exact sync by hand).
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

        //--------------------- RETIRED HTTP MUTATIONS ------------------------------------
        [Authorize]
        [HttpPut("scoreid/{scoreid}")]
        public IActionResult PutHighscore(string scoreid) => LegacyScoreTransportRetired();

        [Authorize]
        [EnableCors("ApiCors")]
        [HttpPost]
        [Route("unsubmitted")]
        public IActionResult PostUnSubmittedHighscore() => LegacyScoreTransportRetired();

        [Authorize]
        [HttpPost]
        public IActionResult PostHighscore() => LegacyScoreTransportRetired();

        [Authorize]
        [HttpDelete("{id}")]
        public IActionResult DeleteHighscore(int id) => LegacyScoreTransportRetired();

        private ObjectResult LegacyScoreTransportRetired()
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status410Gone,
                Title = "Legacy score transport retired",
                Detail = "Legacy V1 high-score mutations are no longer supported."
            };
            problem.Extensions["code"] = "legacy_score_transport_retired";

            var result = StatusCode(StatusCodes.Status410Gone, problem);
            result.ContentTypes.Add("application/problem+json");
            return result;
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


