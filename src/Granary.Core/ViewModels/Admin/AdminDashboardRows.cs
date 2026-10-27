using System.Windows.Input;

namespace Granary.Core.ViewModels.Admin;

/// <summary>How a dashboard value reads: as it is, as a warning (amber) or as an error (red).</summary>
public enum AdminTone
{
    Normal,
    Warn,
    Error,
}

/// <summary>A number tile: "Active Streams", "2", "2 sessions"; "Unavailable" when its source failed.</summary>
public sealed record AdminStatTile(string Label, string Value, string Sub, bool IsUnavailable = false)
{
    public bool IsAvailable => !IsUnavailable;
}

/// <summary>A cell of the health strip: "Postgres", "OK", "0.06 ms".</summary>
public sealed record AdminHealthCell(string Label, string Value, string Detail = "", AdminTone Tone = AdminTone.Normal)
{
    public bool HasDetail => Detail.Length > 0;

    public bool IsWarn => Tone == AdminTone.Warn;

    public bool IsError => Tone == AdminTone.Error;

    public bool IsNormal => Tone == AdminTone.Normal;
}

/// <summary>A stream of Now Playing, as the web's stream card shows it.</summary>
/// <param name="Method">One of <see cref="AdminPlayMethod"/>'s methods; <see cref="MethodLabel"/> words it.</param>
/// <param name="ContentId">The title it opens; null when the server sent none.</param>
/// <param name="OpenCommand">Takes <see cref="ContentId"/>.</param>
public sealed record AdminStreamCard(
    string Title,
    string Subtitle,
    string Method,
    string Client,
    string Profile,
    string Username,
    string Elapsed,
    bool IsPaused,
    Uri? PosterUrl,
    string? ContentId,
    ICommand OpenCommand)
{
    public string MethodLabel => AdminPlayMethod.Label(Method);

    public string Initial => AdminText.Initial(Username);

    public bool HasSubtitle => Subtitle.Length > 0;

    public bool HasClient => Client.Length > 0;

    public bool HasProfile => Profile.Length > 0;

    public bool CanOpen => ContentId is not null;

    /// <summary>The title shows as text, with nothing to open.</summary>
    public bool IsPlain => !CanOpen;
}

/// <summary>A row of Top titles or Most active profiles: its plays as a bar against the busiest row's.</summary>
/// <param name="Rank">"1" for the busiest row.</param>
/// <param name="Fraction">The row's plays as a share of the busiest row's, 0 to 1.</param>
/// <param name="ContentId">The title a row of Top titles opens; null for a profile.</param>
public sealed record AdminBarRow(string Rank, string Label, string Value, string Secondary, double Fraction, string? ContentId = null, ICommand? OpenCommand = null)
{
    public bool CanOpen => ContentId is not null && OpenCommand is not null;

    /// <summary>The label shows as text, with nothing to open.</summary>
    public bool IsPlain => !CanOpen;
}

/// <summary>A library: "Movies", "movies · 2 paths", and whether it is on.</summary>
public sealed record AdminLibraryRow(string Name, string Detail, bool IsEnabled)
{
    public string StatusText => IsEnabled ? "Library enabled" : "Library disabled";
}

/// <summary>An account of Users: its role badge only for an administrator ("admin", or "owner" for the server's Owner).</summary>
public sealed record AdminUserRow(string Username, string Email, string Role, string LastActive, bool IsEnabled)
{
    public string Initial => AdminText.Initial(Username);

    public bool HasRole => Role.Length > 0;

    public bool HasEmail => Email.Length > 0;

    public string StatusText => IsEnabled ? "Active" : "Disabled";
}

/// <summary>A line of Recent Activity: "elena [Elena] started watching Dark", "5m ago · Granary 0.3.0".</summary>
public sealed record AdminActivityRow(string Username, string Profile, string Title, string Meta)
{
    public bool HasProfile => Profile.Length > 0;

    /// <summary>" (Kids)" after the account, when a profile is known.</summary>
    public string ProfileNote => HasProfile ? $" ({Profile})" : "";
}
