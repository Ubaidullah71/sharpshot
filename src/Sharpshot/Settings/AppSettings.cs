using System.Text.Json.Serialization;
using Sharpshot.Links;

namespace Sharpshot.Settings;

public enum FullscreenTarget
{
    CursorMonitor,
    AllMonitors,
    PrimaryMonitor,
}

public enum CopyLinkMode
{
    Immediately,

    AfterUpload,
}

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public R2Settings R2 { get; set; } = new();

    public LinkSettings Links { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    public EmbedSettings Embed { get; set; } = new();

    public AppSettings Clone() => new()
    {
        Version = Version,
        R2 = R2 with { },
        Links = Links with { },
        Capture = Capture with { },
        General = General with { },
        Embed = Embed with { },
    };
}

public sealed record R2Settings
{
    public string AccountId { get; set; } = "";

    public string AccessKeyId { get; set; } = "";

    /// <summary>Plain text in memory only; stored encrypted (<see cref="EncryptedSecretAccessKey"/>).</summary>
    [JsonIgnore]
    public string SecretAccessKey { get; set; } = "";

    /// <summary>DPAPI-encrypted for the current Windows user. Useless if copied to another PC or account.</summary>
    public string EncryptedSecretAccessKey { get; set; } = "";

    public string Bucket { get; set; } = "";

    /// <summary>The custom domain connected to the bucket, e.g. https://cdn.example.com.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>Optional folder inside the bucket, e.g. "shots/".</summary>
    public string PathPrefix { get; set; } = "";

    /// <summary>Optional. Only needed for jurisdiction-specific buckets (e.g. EU).</summary>
    public string Endpoint { get; set; } = "";

    [JsonIgnore]
    public string EffectiveEndpoint => string.IsNullOrWhiteSpace(Endpoint)
        ? $"https://{AccountId.Trim()}.r2.cloudflarestorage.com"
        : Endpoint.Trim().TrimEnd('/');

    /// <summary>Never includes the secret, so settings are safe to print (e.g. in a failing test).</summary>
    public override string ToString() => $"R2Settings {{ Bucket = {Bucket}, PublicUrl = {PublicUrl}, PathPrefix = {PathPrefix} }}";
}

public sealed record LinkSettings
{
    public LinkStyle Style { get; set; } = LinkStyle.Plain;

    /// <summary>Number of random symbols. 0 means the style's default.</summary>
    public int Length { get; set; }

    public CopyLinkMode Copy { get; set; } = CopyLinkMode.Immediately;

    [JsonIgnore]
    internal int EffectiveLength => LinkStyles.Get(Style).ClampLength(Length);
}

public sealed record CaptureSettings
{
    public Hotkey RegionHotkey { get; set; } = new(HotkeyModifiers.Control, Keys.PrintScreen);

    public Hotkey FullscreenHotkey { get; set; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.PrintScreen);

    public FullscreenTarget FullscreenTarget { get; set; } = FullscreenTarget.CursorMonitor;

    public bool SaveLocalCopy { get; set; } = true;

    public string LocalFolder { get; set; } = AppPaths.DefaultScreenshotFolder;
}

public sealed record GeneralSettings
{
    public bool NotifyOnUpload { get; set; } = true;
}

/// <summary>"Share as embed": links point to a small HTML page that Discord and other apps show as a card.</summary>
public sealed record EmbedSettings
{
    public const string DefaultColor = "#5B6CFF";

    public bool Enabled { get; set; }

    /// <summary>The embed's accent bar, as #RRGGBB.</summary>
    public string Color { get; set; } = DefaultColor;

    /// <summary>Small text above the title. Supports placeholders like {date}.</summary>
    public string SiteName { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "{date} at {time}";
}