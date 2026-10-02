using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Level5Backend.Models;

// StringLength caps mirror the persisted column widths declared in Level5Context. The legacy V1
// API now treats Highscore as a read-only historical model; its existing nullable and unannotated
// fields remain aligned with the stored schema rather than defining a mutation request contract.
public partial class Highscore
{
    public int Id { get; set; }

    public int Userid { get; set; }

    [StringLength(45)]
    public string? Username { get; set; }

    /// <summary>
    /// unique
    /// </summary>
    [StringLength(100)]
    public string? Scoreid { get; set; }

    public int Modeid { get; set; }

    [StringLength(45)]
    public string? ModeName { get; set; }

    public int Characterid { get; set; }

    public int Levelid { get; set; }

    [Required, StringLength(45)]
    public string Character { get; set; } = null!;

    [Required, StringLength(45)]
    public string Level { get; set; } = null!;

    [Required, StringLength(45)]
    public string Os { get; set; } = null!;

    [Required, StringLength(45)]
    public string Version { get; set; } = null!;

    [Required, StringLength(45)]
    public string Date { get; set; } = null!;

    // Nullable to preserve the historical model and schema shape. The database default remains 1,
    // but the retired V1 score API no longer inserts or updates this value.
    public int? Difficulty { get; set; }

    public float Time { get; set; }

    public int TotalPoints { get; set; }

    public float LongestShot { get; set; }

    public float TotalDistance { get; set; }

    public int ConsecutiveShots { get; set; }

    public int TrafficEnabled { get; set; }

    public int HardcoreEnabled { get; set; }

    public int EnemiesEnabled { get; set; }

    public int EnemiesKilled { get; set; }

    public int SniperEnabled { get; set; }

    public int SniperMode { get; set; }

    // Nullable to preserve historical rows and the existing database contract. The database
    // default remains 'none' (see Level5Context), but V1 score mutations are retired.
    [StringLength(45)]
    public string? SniperModeName { get; set; }

    public int SniperHits { get; set; }

    public int SniperShots { get; set; }

    public int MaxShotMade { get; set; }

    public int MaxShotAtt { get; set; }

    public int? TwoMade { get; set; }

    public int? TwoAtt { get; set; }

    public int? ThreeMade { get; set; }

    public int? ThreeAtt { get; set; }

    public int? FourMade { get; set; }

    public int? FourAtt { get; set; }

    public int? SevenMade { get; set; }

    public int? SevenAtt { get; set; }

    public int? BonusPoints { get; set; }

    public int? MoneyBallMade { get; set; }

    public int? MoneyBallAtt { get; set; }

    /// <summary>
    /// if desktop/mobile
    /// </summary>
    [StringLength(45)]
    public string? Platform { get; set; }

    /// <summary>
    /// what specific device being used
    /// </summary>
    [StringLength(45)]
    public string? Device { get; set; }

    public string? Ipaddress { get; set; }

    public int P1TotalPoints { get; set; }

    public int P2TotalPoints { get; set; }

    public int P3TotalPoints { get; set; }

    public int P4TotalPoints { get; set; }

    [StringLength(50)]
    public string? FirstPlace { get; set; }

    [StringLength(50)]
    public string? SecondPlace { get; set; }

    [StringLength(50)]
    public string? ThirdPlace { get; set; }

    [StringLength(50)]
    public string? FourthPlace { get; set; }

    public int P1IsCpu { get; set; }

    public int P2IsCpu { get; set; }

    public int P3IsCpu { get; set; }

    public int P4IsCpu { get; set; }

    public int NumPlayers { get; set; }
}
