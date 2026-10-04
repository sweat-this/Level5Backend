using System;
using System.Collections.Generic;

namespace Level5Backend.Models;

public partial class UserReport
{
    public int Id { get; set; }

    public string Report { get; set; } = null!;

    public int? Userid { get; set; }

    public string? UserName { get; set; }

    public string Os { get; set; } = null!;

    public string Device { get; set; } = null!;

    public string DeviceName { get; set; } = null!;

    public string Version { get; set; } = null!;

    // Nullable/server-derived (see UserReportApiController.PostUserReport) rather than trusted
    // from the client, like Highscore.Ipaddress - this used to be a NOT NULL, client-supplied
    // column, which meant a client that didn't happen to send one couldn't submit a report at all.
    public string? Ipaddress { get; set; }

    public DateTime Date { get; set; }
}
