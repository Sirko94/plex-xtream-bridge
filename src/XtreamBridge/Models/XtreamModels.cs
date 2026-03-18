// Ported from firestaerter3/Jellyfin-Xtream-Library (GPL v3)
// Removed all MediaBrowser/Jellyfin references — pure .NET 8

using System.Collections.Generic;
using Newtonsoft.Json;
using XtreamBridge.Converters;

namespace XtreamBridge.Models;

// ─── Auth ─────────────────────────────────────────────────────────────────────

public class XtreamPlayerApi
{
    [JsonProperty("user_info")]   public XtreamUserInfo UserInfo { get; set; } = new();
    [JsonProperty("server_info")] public XtreamServerInfo ServerInfo { get; set; } = new();
}

public class XtreamUserInfo
{
    [JsonProperty("username")]    public string Username { get; set; } = string.Empty;
    [JsonProperty("password")]    public string Password { get; set; } = string.Empty;
    [JsonProperty("auth")]        public int Auth { get; set; }
    [JsonProperty("status")]      public string Status { get; set; } = string.Empty;

    [JsonConverter(typeof(UnixDateTimeNullableConverter))]
    [JsonProperty("exp_date")]    public DateTime? ExpDate { get; set; }

    [JsonConverter(typeof(StringBoolConverter))]
    [JsonProperty("is_trial")]    public bool IsTrial { get; set; }

    [JsonProperty("active_cons")]     public int ActiveCons { get; set; }
    [JsonProperty("max_connections")] public int MaxConnections { get; set; }

    [JsonConverter(typeof(SingularToListConverter<string>))]
    [JsonProperty("allowed_output_formats")]
    public ICollection<string> AllowedOutputFormats { get; set; } = new List<string>();
}

public class XtreamServerInfo
{
    [JsonProperty("url")]      public string Url { get; set; } = string.Empty;
    [JsonProperty("port")]     public string Port { get; set; } = string.Empty;
    [JsonProperty("timezone")] public string Timezone { get; set; } = string.Empty;
}

// ─── Categories ───────────────────────────────────────────────────────────────

public class XtreamCategory
{
    [JsonProperty("category_id")]   public int CategoryId { get; set; }
    [JsonProperty("category_name")] public string CategoryName { get; set; } = string.Empty;
    [JsonProperty("parent_id")]     public int ParentId { get; set; }
}

// ─── Live Streams ─────────────────────────────────────────────────────────────

public class XtreamLiveStream
{
    [JsonProperty("num")]            public int Num { get; set; }
    [JsonProperty("name")]           public string Name { get; set; } = string.Empty;
    [JsonProperty("stream_type")]    public string StreamType { get; set; } = string.Empty;
    [JsonProperty("stream_id")]      public int StreamId { get; set; }
    [JsonProperty("stream_icon")]    public string StreamIcon { get; set; } = string.Empty;
    [JsonProperty("epg_channel_id")] public string EpgChannelId { get; set; } = string.Empty;
    [JsonProperty("added")]          public long Added { get; set; }
    [JsonProperty("category_id")]    public int? CategoryId { get; set; }
    [JsonProperty("custom_sid")]     public string CustomSid { get; set; } = string.Empty;

    [JsonConverter(typeof(StringBoolConverter))]
    [JsonProperty("tv_archive")]     public bool TvArchive { get; set; }

    [JsonProperty("tv_archive_duration")] public int TvArchiveDuration { get; set; }

    [JsonConverter(typeof(StringBoolConverter))]
    [JsonProperty("is_adult")]       public bool IsAdult { get; set; }

    // Mutable so ChannelOverrideParser can patch it
    [JsonProperty("direct_source")]  public string DirectSource { get; set; } = string.Empty;
}

// ─── VOD ──────────────────────────────────────────────────────────────────────

public class XtreamVodStream
{
    [JsonProperty("num")]                  public int Num { get; set; }
    [JsonProperty("name")]                 public string Name { get; set; } = string.Empty;
    [JsonProperty("stream_type")]          public string StreamType { get; set; } = string.Empty;
    [JsonProperty("stream_id")]            public int StreamId { get; set; }
    [JsonProperty("stream_icon")]          public string StreamIcon { get; set; } = string.Empty;
    [JsonProperty("added")]                public string Added { get; set; } = string.Empty;
    [JsonProperty("category_id")]          public int? CategoryId { get; set; }
    [JsonProperty("container_extension")]  public string ContainerExtension { get; set; } = string.Empty;
    [JsonProperty("custom_sid")]           public string CustomSid { get; set; } = string.Empty;
    [JsonProperty("direct_source")]        public string DirectSource { get; set; } = string.Empty;
}

public class XtreamVodInfoResponse
{
    [JsonConverter(typeof(OnlyObjectConverter<XtreamVodInfoDetails>))]
    [JsonProperty("info")]       public XtreamVodInfoDetails? Info { get; set; }

    [JsonProperty("movie_data")] public XtreamVodMovieData? MovieData { get; set; }
}

public class XtreamVodInfoDetails
{
    [JsonProperty("movie_image")]  public string? MovieImage { get; set; }
    [JsonProperty("tmdb_id")]      public string? TmdbId { get; set; }
    [JsonProperty("name")]         public string? Name { get; set; }
    [JsonProperty("o_name")]       public string? OriginalName { get; set; }
    [JsonProperty("plot")]         public string? Plot { get; set; }
    [JsonProperty("cast")]         public string? Cast { get; set; }
    [JsonProperty("director")]     public string? Director { get; set; }
    [JsonProperty("genre")]        public string? Genre { get; set; }
    [JsonProperty("releasedate")]  public string? ReleaseDate { get; set; }
    [JsonProperty("rating")]       public string? Rating { get; set; }
    [JsonProperty("duration")]     public string? Duration { get; set; }
    [JsonProperty("duration_secs")] public int? DurationSecs { get; set; }
}

public class XtreamVodMovieData
{
    [JsonProperty("stream_id")]            public int StreamId { get; set; }
    [JsonProperty("name")]                 public string Name { get; set; } = string.Empty;
    [JsonProperty("container_extension")]  public string ContainerExtension { get; set; } = string.Empty;
}

// ─── Series ───────────────────────────────────────────────────────────────────

public class XtreamSeries
{
    [JsonProperty("num")]          public int Num { get; set; }
    [JsonProperty("name")]         public string Name { get; set; } = string.Empty;
    [JsonProperty("series_id")]    public int SeriesId { get; set; }
    [JsonProperty("cover")]        public string Cover { get; set; } = string.Empty;
    [JsonProperty("plot")]         public string Plot { get; set; } = string.Empty;
    [JsonProperty("cast")]         public string Cast { get; set; } = string.Empty;
    [JsonProperty("director")]     public string Director { get; set; } = string.Empty;
    [JsonProperty("genre")]        public string Genre { get; set; } = string.Empty;
    [JsonProperty("rating")]       public decimal? Rating { get; set; }
    [JsonProperty("episode_run_time")] public int? EpisodeRunTime { get; set; }
    [JsonProperty("category_id")]  public int? CategoryId { get; set; }

    [JsonConverter(typeof(SingularToListConverter<string>))]
    [JsonProperty("backdrop_path")] public ICollection<string> BackdropPaths { get; set; } = new List<string>();
}

public class XtreamSeriesStreamInfo
{
    [JsonConverter(typeof(SingularToListConverter<XtreamSeason>))]
    [JsonProperty("seasons")] public ICollection<XtreamSeason> Seasons { get; set; } = new List<XtreamSeason>();

    [JsonConverter(typeof(OnlyObjectConverter<XtreamSeriesInfo>))]
    [JsonProperty("info")] public XtreamSeriesInfo Info { get; set; } = new();

    [JsonConverter(typeof(EpisodeDictionaryConverter))]
    [JsonProperty("episodes")] public Dictionary<int, ICollection<XtreamEpisode>>? Episodes { get; set; } = new();
}

public class XtreamSeriesInfo
{
    [JsonProperty("name")]             public string Name { get; set; } = string.Empty;
    [JsonProperty("cover")]            public string Cover { get; set; } = string.Empty;
    [JsonProperty("plot")]             public string Plot { get; set; } = string.Empty;
    [JsonProperty("cast")]             public string Cast { get; set; } = string.Empty;
    [JsonProperty("director")]         public string Director { get; set; } = string.Empty;
    [JsonProperty("genre")]            public string Genre { get; set; } = string.Empty;
    [JsonProperty("rating")]           public decimal Rating { get; set; }
    [JsonProperty("category_id")]      public int CategoryId { get; set; }
    [JsonProperty("tmdb")]             public string? Tmdb { get; set; }
    [JsonProperty("episode_run_time")] public int EpisodeRunTime { get; set; }

    [JsonConverter(typeof(SingularToListConverter<string>))]
    [JsonProperty("backdrop_path")] public ICollection<string> BackdropPaths { get; set; } = new List<string>();
}

public class XtreamSeason
{
    [JsonProperty("season_number")] public int SeasonNumber { get; set; }
    [JsonProperty("name")]          public string Name { get; set; } = string.Empty;
    [JsonProperty("air_date")]      public DateTime? AirDate { get; set; }
    [JsonProperty("episode_count")] public int EpisodeCount { get; set; }
    [JsonProperty("cover")]         public string Cover { get; set; } = string.Empty;
    [JsonProperty("cover_big")]     public string CoverBig { get; set; } = string.Empty;
    [JsonProperty("overview")]      public string Overview { get; set; } = string.Empty;
}

public class XtreamEpisode
{
    [JsonProperty("id")]                  public int EpisodeId { get; set; }
    [JsonProperty("episode_num")]         public int EpisodeNum { get; set; }
    [JsonProperty("title")]               public string Title { get; set; } = string.Empty;
    [JsonProperty("container_extension")] public string ContainerExtension { get; set; } = string.Empty;
    [JsonProperty("custom_sid")]          public string CustomSid { get; set; } = string.Empty;
    [JsonProperty("added")]               public DateTime? Added { get; set; }
    [JsonProperty("season")]              public int Season { get; set; }
    [JsonProperty("direct_source")]       public string DirectSource { get; set; } = string.Empty;

    [JsonConverter(typeof(OnlyObjectConverter<XtreamEpisodeInfo>))]
    [JsonProperty("info")] public XtreamEpisodeInfo? Info { get; set; } = new();
}

public class XtreamEpisodeInfo
{
    [JsonProperty("movie_image")]   public string? MovieImage { get; set; }
    [JsonProperty("plot")]          public string? Plot { get; set; }
    [JsonProperty("releasedate")]   public string? ReleaseDate { get; set; }
    [JsonProperty("rating")]        public decimal? Rating { get; set; }
    [JsonProperty("duration_secs")] public int? DurationSecs { get; set; }
}

// ─── EPG ──────────────────────────────────────────────────────────────────────

public class XtreamEpgListings
{
    [JsonProperty("epg_listings")] public List<XtreamEpgProgram> Listings { get; set; } = new();
}

public class XtreamEpgProgram
{
    [JsonProperty("id")]              public string Id { get; set; } = string.Empty;
    [JsonProperty("epg_id")]          public string EpgId { get; set; } = string.Empty;

    [JsonConverter(typeof(Base64Converter))]
    [JsonProperty("title")]           public string Title { get; set; } = string.Empty;

    [JsonProperty("lang")]            public string Language { get; set; } = string.Empty;
    [JsonProperty("start")]           public string Start { get; set; } = string.Empty;
    [JsonProperty("end")]             public string End { get; set; } = string.Empty;

    [JsonConverter(typeof(Base64Converter))]
    [JsonProperty("description")]     public string Description { get; set; } = string.Empty;

    [JsonProperty("channel_id")]      public string ChannelId { get; set; } = string.Empty;
    [JsonProperty("start_timestamp")] public long StartTimestamp { get; set; }
    [JsonProperty("stop_timestamp")]  public long StopTimestamp { get; set; }

    [JsonConverter(typeof(StringBoolConverter))]
    [JsonProperty("has_archive")]     public bool HasArchive { get; set; }
}
