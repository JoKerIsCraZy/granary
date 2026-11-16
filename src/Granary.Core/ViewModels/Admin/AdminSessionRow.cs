using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// A stream of Activity, as the web's StreamRow and its details. It stays the same object while its session plays, so a
/// read again keeps it opened (<see cref="IsExpanded"/>) and only its values change.
/// </summary>
public sealed partial class AdminSessionRow : ObservableObject
{
    public AdminSessionRow(AdminPlaybackSession session, Uri server, DateTimeOffset now)
    {
        SessionId = session.SessionId;
        Session = session;
        Server = server;
        Now = now;
    }

    public string SessionId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(HasSubtitle), nameof(Username), nameof(Initial), nameof(Profile),
        nameof(HasProfile), nameof(Client), nameof(ClientFull), nameof(HasClient), nameof(ClientIp), nameof(HasClientIp), nameof(PosterUrl),
        nameof(ContentId), nameof(CanOpen), nameof(IsPlain), nameof(Method), nameof(MethodLabel), nameof(Container), nameof(Video),
        nameof(Audio), nameof(VideoLine), nameof(IsVideoConverted), nameof(VideoEncoder), nameof(HasVideoEncoder), nameof(AudioLine),
        nameof(IsAudioConverted), nameof(Bitrate), nameof(HasBitrate), nameof(BitrateReference), nameof(HasBitrateReference),
        nameof(BitrateShare), nameof(HasBitrateShare), nameof(IsBitrateLimited), nameof(BitrateLimit), nameof(HasBitrateLimit),
        nameof(Route), nameof(Position), nameof(Progress), nameof(IsPaused), nameof(PauseLabel), nameof(CanControl), nameof(CannotControl),
        nameof(Location), nameof(MediaType), nameof(Elapsed), nameof(ActiveText))]
    public partial AdminPlaybackSession Session { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Elapsed), nameof(ActiveText))]
    public partial DateTimeOffset Now { get; private set; }

    /// <summary>The details below the row are open.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>A command for the session is on its way: its buttons wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    /// <summary>How long a pause or resume sent shows before the server reports it; past that the server's word stands.</summary>
    public static readonly TimeSpan ExpectFor = TimeSpan.FromSeconds(20);

    private DateTimeOffset _expectedAt;

    /// <summary>A pause or resume sent, until the server reports it or <see cref="ExpectFor"/> passed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused), nameof(PauseLabel))]
    public partial bool? PausedSoon { get; private set; }

    public Uri Server { get; }

    public bool IsIdle => !IsBusy;

    public string Title => AdminSessionText.Title(Session);

    public string Subtitle => AdminSessionText.Subtitle(Session);

    public bool HasSubtitle => Subtitle.Length > 0;

    public string Username => AdminSessionText.Username(Session);

    public string Initial => AdminText.Initial(Username);

    public string Profile => AdminText.Profile(Session);

    public bool HasProfile => Profile.Length > 0;

    public string Client => AdminText.Client(Session);

    /// <summary>The client with its build, for the details.</summary>
    public string ClientFull => Session.ClientLabelFull?.Trim() is { Length: > 0 } full ? full : Client;

    public bool HasClient => Client.Length > 0;

    public string ClientIp => Session.ClientIp?.Trim() ?? "";

    public bool HasClientIp => ClientIp.Length > 0;

    public Uri? PosterUrl => Api.Images.ArtworkUrl.Resolve(Server, Session.PosterUrl);

    public string? ContentId => string.IsNullOrEmpty(Session.ContentId) ? null : Session.ContentId;

    public bool CanOpen => ContentId is not null;

    public bool IsPlain => !CanOpen;

    public string Method => AdminPlayMethod.Of(Session);

    public string MethodLabel => AdminPlayMethod.Label(Method);

    public string Container => AdminSessionText.Container(Session);

    public string Video => AdminSessionText.Video(Session);

    public string Audio => AdminSessionText.Audio(Session);

    /// <summary>The rate the viewer receives, "8.0 Mbps"; empty when unknown.</summary>
    public string Bitrate => AdminSessionText.Bitrate(Session.StreamBitrateKbps);

    public bool HasBitrate => Bitrate.Length > 0;

    /// <summary>The muted line under the rate: "limit 4.0 Mbps · source 20.0 Mbps", either alone, or empty.</summary>
    public string BitrateReference => AdminSessionText.SessionBitrate(Session).Reference;

    public bool HasBitrateReference => BitrateReference.Length > 0;

    /// <summary>How much of the source's bitrate reaches the viewer, 0 to 1, for the bar under the rate; 0 when unknown.</summary>
    public double BitrateShare => AdminSessionText.SessionBitrate(Session).Share ?? 0;

    /// <summary>The bar shows only while both the delivered and the source's rate are known.</summary>
    public bool HasBitrateShare => AdminSessionText.SessionBitrate(Session).Share is not null;

    /// <summary>A transcode encodes to a limit: the bar shows in the warning colour, else in success.</summary>
    public bool IsBitrateLimited => AdminSessionText.SessionBitrate(Session).IsLimited;

    /// <summary>"limit 4.0 Mbps", the tag of Bitrate in the details; empty without a limit.</summary>
    public string BitrateLimit => AdminSessionText.SessionBitrate(Session).LimitTag;

    public bool HasBitrateLimit => BitrateLimit.Length > 0;

    /// <summary>The video line, as the web's row: "HEVC 2160p", or "HEVC 2160p → H.264 1080p" while it is transcoded.</summary>
    public string VideoLine => AdminSessionText.VideoConversion(Session).Text;

    /// <summary>The server converts the video: its line shows in the text colour, an untouched one muted.</summary>
    public bool IsVideoConverted => AdminSessionText.VideoConversion(Session).IsConverted;

    /// <summary>"HW QSV", "SW": the encoder after a video transcode's line; empty otherwise.</summary>
    public string VideoEncoder => AdminSessionText.VideoEncoder(Session);

    public bool HasVideoEncoder => VideoEncoder.Length > 0;

    /// <summary>The audio line: "TrueHD 7.1", or "TrueHD 7.1 → AAC 5.1" while it is re-encoded.</summary>
    public string AudioLine => AdminSessionText.AudioConversion(Session).Text;

    public bool IsAudioConverted => AdminSessionText.AudioConversion(Session).IsConverted;

    /// <summary>"Transcode gpu → Server Local server".</summary>
    public string Route => string.Join(" → ", AdminSessionText.Route(Session).Select(node => $"{node.Label} {node.Name}"));

    public string Position => AdminSessionText.Position(Session);

    public double Progress => AdminSessionText.Progress(Session);

    public bool IsPaused => PausedSoon ?? Session.IsPaused;

    /// <summary>Pause, or Resume for a paused stream.</summary>
    public string PauseLabel => AppText.Get(IsPaused ? "web.admin.sessions.playback.action.resume" : "web.admin.sessions.playback.action.pause");

    /// <summary>The player takes live commands: pause, resume and messages. Stop and Terminate work for every session.</summary>
    public bool CanControl => Session.HasPlaybackControl;

    public bool CannotControl => !CanControl;

    /// <summary>Local or Remote, as the server's bitrate policy sees the viewer.</summary>
    public string Location => AdminSessionText.Location(Session);

    public string MediaType => Session.MediaType;

    /// <summary>How long the session has run: "12:04", "1:02:09".</summary>
    public string Elapsed => AdminSessionText.Clock((Now - Session.StartedAt).TotalSeconds);

    /// <summary>"Session active 5:00", as the web's row says it under the position.</summary>
    public string ActiveText => AppText.Get("web.admin.activity.row.sessionActive", ("elapsed", Elapsed));

    /// <summary>A pause (or resume) the player was sent: the row shows it at once.</summary>
    public void ExpectPaused(bool paused, DateTimeOffset at)
    {
        PausedSoon = paused;
        _expectedAt = at;
    }

    /// <summary>
    /// The session as read again: a pause or resume the server now reports needs no stand-in, nor one the player did not
    /// take within <see cref="ExpectFor"/>.
    /// </summary>
    public void Update(AdminPlaybackSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (PausedSoon is { } expected && (expected == session.IsPaused || now - _expectedAt >= ExpectFor))
        {
            PausedSoon = null;
        }
        Session = session;
        Now = now;
    }

    /// <summary>Whether a search names this stream: its viewer, title, client, address or nodes.</summary>
    public bool Matches(string query)
    {
        bool Has(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        return Has(Session.Username) || Has(Session.MediaTitle) || Has(Session.SeriesName) || Has(Session.EpisodeName) || Has(ClientFull)
            || Has(Session.ClientIp) || AdminSessionText.Route(Session).Any(node => Has($"{node.Label} {node.Name}"));
    }
}
