using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Utilities;
using TagLib;
using File = TagLib.File;

namespace NMaier.SimpleDlna.FileMediaServer {
  [Serializable]
  internal sealed class VideoFile: BaseFile, IMediaVideoResource, ISerializable, IBookmarkable {

    private static readonly TimeSpan EmptyDuration = new TimeSpan(0);

    private string[] actors;
    private long? bookmark;
    private string description;
    private string director;
    private TimeSpan? duration;
    private string genre;
    private int? height;
    private bool initialized;
    private Subtitle subTitle;
    private string title;
    private int? width;
    private int? tvshowid;
    private string seriesname;
    private int? season;
    private int? episode;
    private bool isSeries;

    /*
        private readonly static Regex seriesreg = new Regex(
                //@"(([0-9]{1,2})x([0-9]{1,2})|S([0-9]{1,2})+E([0-9]{1,2}))",
                @"(([^0-9][0-9]{1,2})x([0-9]{1,2}[^0-9])|[ \._\-]([0-9]{3})([ \._\-]|$)|(S([0-9]{1,2})+(E([0-9]{1,2})| ))|[_ -]([0-9]{1,3})[\._ -][^\dsS])",
                RegexOptions.Compiled | RegexOptions.IgnoreCase
                );
        private readonly static Regex movieclear = new Regex(
                @"(.*?)[._ ]?(([0-9]{4})|[0-9]{3,4}p)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase
                );
    */
    private VideoFile(SerializationInfo info, StreamingContext ctx): this(info, ctx.Context as DeserializeInfo) { }

    private void FetchTV() {
      try {
        if (this.tvshowid == null || this.tvshowid == -1)
          this.tvshowid = TheTVDB.GetTVShowID(this.Path);

        var name = Directory.GetParent(this.Path).Name.GetNiceNameOrNull();
        if (name == null || name is Formatting.NiceSeriesName seriesName && seriesName.Episode == 0)
          name = base.Title.GetNiceNameOrNull();

        if (name == null)
          name = this.Item.Name.GetNiceNameOrNull();

        TVShowInfo tvinfo = null;

        if (this.tvshowid != null && this.tvshowid > 0) {
          tvinfo = TheTVDB.GetTVShowDetails(this.tvshowid.Value);
          this.Server.UpdateTVCache(tvinfo);
          this.seriesname = tvinfo.Name;
          this.isSeries = true;
        }

        switch (name) {
          case Formatting.NiceSeriesName showName: {
            this.isSeries = true;
            if (string.IsNullOrEmpty(this.seriesname))
              this.seriesname = showName.Name;

            if (showName.Episode > 0) {
              this.title =
                tvinfo != null
                ? tvinfo.Find(showName.Season, showName.Episode)
                : $"s{showName.Season}e{showName.Episode}"
                ;

              this.season = showName.Season;
              this.episode = showName.Episode;
            } else {
              this.title = base.Title;
            }

            if (!string.IsNullOrEmpty(name.Releaser))
              this.title = $"{this.title} ({name.Resolution},{name.Releaser})";

            break;
          }

          case Formatting.MovieName movieName: {
            this.seriesname = $"{movieName.Name} ({movieName.Year})";
            break;
          }

          default:
            this.seriesname = Directory.GetParent(this.Path).Name;
            break;
        }
      } catch (Exception exn) {
        if (exn is ArgumentNullException) { } else {
          this.tvshowid = TheTVDB.GetTVShowID(this.Path);
        }
      }
    }
    
    private VideoFile(SerializationInfo info, DeserializeInfo di): this(di.Server, di.Info, di.Type) {
      this.actors = info.GetValue("a", typeof(string[])) as string[];
      this.description = info.GetString("de");
      this.director = info.GetString("di");
      this.genre = info.GetString("g");
      try {
        this.lastpos = info.GetInt64("lp");
      } catch (Exception) {
        this.lastpos = 0;
      }

      try {
        this.width = info.GetInt32("w");
        this.height = info.GetInt32("h");
      } catch (Exception) {
        // ignored
      }

      var ts = info.GetInt64("du");
      if (ts > 0)
        this.duration = new TimeSpan(ts);

      try {
        this.bookmark = info.GetInt64("b");
      } catch (Exception) {
        this.bookmark = 0;
      }

      try {
        this.subTitle = new Subtitle(new FileInfo(this.Path));
      } catch (Exception) {
        this.subTitle = null;
      }

      try {
        this.tvshowid = info.GetInt32("tvid");
        this.FetchTV();
      } catch (Exception) {
        // ignored
      }

      this.Server.UpdateFileCache(this);
      this.initialized = true;
    }

    internal VideoFile(FileServer server, FileInfo aFile, DlnaMime aType): base(server, aFile, aType, DlnaMediaTypes.Video) { }

    public long? Bookmark {
      get => this.bookmark;
      set {
        this.bookmark = value;
        this.Server.UpdateFileCache(this);
      }
    }

    public IEnumerable<string> MetaActors {
      get {
        this.MaybeInit();
        return this.actors;
      }
    }

    public long Progress {
      get {
        if (this.InfoSize.HasValue)
          return this.lastpos * 100L / this.InfoSize.Value;

        return 0;
      }
    }

    public string MovieTitle {
      get {
        this.MaybeInit();
        return this.seriesname ?? base.Title;
      }
    }

    public bool IsSeries {
      get {
        this.MaybeInit();
        return this.isSeries;
      }
    }

    public int? Season {
      get {
        this.MaybeInit();
        return this.season;
      }
    }

    public int? Episode {
      get {
        this.MaybeInit();
        return this.episode;
      }
    }

    public string MetaDescription {
      get {
        this.MaybeInit();
        return this.description;
      }
    }

    public string MetaDirector {
      get {
        this.MaybeInit();
        return this.director;
      }
    }

    public TimeSpan? MetaDuration {
      get {
        this.MaybeInit();
        return this.duration;
      }
    }

    public string MetaGenre {
      get {
        this.MaybeInit();
        if (string.IsNullOrWhiteSpace(this.genre))
          throw new NotSupportedException();

        return this.genre;
      }
    }

    public int? MetaHeight {
      get {
        this.MaybeInit();
        return this.height;
      }
    }

    public int? MetaWidth {
      get {
        this.MaybeInit();
        return this.width;
      }
    }

    public override IHeaders Properties {
      get {
        this.MaybeInit();
        var rv = base.Properties;
        if (this.description != null)
          rv.Add("Description", this.description);

        if (this.actors != null && this.actors.Length != 0)
          rv.Add("Actors", string.Join(", ", this.actors));

        if (this.director != null)
          rv.Add("Director", this.director);

        if (this.duration != null)
          rv.Add("Duration", this.duration.Value.ToString("g"));

        if (this.genre != null)
          rv.Add("Genre", this.genre);

        if (this.width != null && this.height != null)
          rv.Add("Resolution", $"{this.width.Value}x{this.height.Value}");

        return rv;
      }
    }

    public Subtitle Subtitle {
      get {
        try {
          if (this.subTitle == null) {
            this.subTitle = new Subtitle(this.Item);
            this.Server.UpdateFileCache(this);
          }
        } catch (Exception ex) {
          this.Error("Failed to look up subtitle", ex);
          this.subTitle = new Subtitle();
        }

        return this.subTitle;
      }
    }

    public override string Title {
      get {
        this.MaybeInit();
        var result = base.Title;
        if (!string.IsNullOrWhiteSpace(this.title))
          result = this.title;

        return (this.Subtitle.HasSubtitle ? "*" : "") + result;
      }
    }

    private void MaybeInit() {
      if (this.initialized)
        return;

      this.FetchTV();
      try {
        using (var tl = File.Create(new TagLibFileAbstraction(this.Item))) {
          try {
            this.duration = tl.Properties.Duration;
            if (this.duration.Value.TotalSeconds < 0.1)
              this.duration = null;

            this.width = tl.Properties.VideoWidth;
            this.height = tl.Properties.VideoHeight;
          } catch (Exception ex) {
            this.Debug("Failed to transpose Properties props", ex);
          }

          try {
            var t = tl.Tag;
            this.genre = t.FirstGenre;
            this.description = t.Comment;
            this.director = t.FirstComposerSort;
            if (string.IsNullOrWhiteSpace(this.director))
              this.director = t.FirstComposer;

            this.actors = t.PerformersSort;
            if (this.actors == null || this.actors.Length == 0) {
              this.actors = t.PerformersSort;
              if (this.actors == null || this.actors.Length == 0) {
                this.actors = t.Performers;
                if (this.actors == null || this.actors.Length == 0)
                  this.actors = t.AlbumArtists;
              }
            }
          } catch (Exception ex) {
            this.Debug("Failed to transpose Tag props", ex);
          }
        }

        this.initialized = true;

        this.Server.UpdateFileCache(this);
      } catch (CorruptFileException ex) {
        this.Debug("Failed to read meta data via taglib for file " + this.Item.FullName, ex);
        this.initialized = true;
      } catch (UnsupportedFormatException ex) {
        this.Debug("Failed to read meta data via taglib for file " + this.Item.FullName, ex);
        this.initialized = true;
      } catch (Exception ex) {
        this.Warn("Unhandled exception reading meta data for file " + this.Item.FullName,ex);
      }
    }

    public void GetObjectData(SerializationInfo info, StreamingContext context) {
      if (info == null)
        throw new ArgumentNullException(nameof(info));

      this.MaybeInit();
      info.AddValue("a", this.actors, typeof(string[]));
      info.AddValue("de", this.description);
      info.AddValue("di", this.director);
      info.AddValue("g", this.genre);
      info.AddValue("t", this.title);
      info.AddValue("w", this.width);
      info.AddValue("h", this.height);
      info.AddValue("b", this.bookmark);
      info.AddValue("du", this.duration.GetValueOrDefault(EmptyDuration).Ticks);
      info.AddValue("tvid", this.tvshowid ?? -1);
      info.AddValue("lp", this.lastpos);
    }
  }
}
