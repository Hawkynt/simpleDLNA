using System;
using System.IO;
using System.Runtime.Serialization;
using NMaier.SimpleDlna.Server;
using TagLib;
using File = System.IO.File;

namespace NMaier.SimpleDlna.FileMediaServer {
  [Serializable]
  internal sealed class AudioFile
    : BaseFile, IMediaAudioResource, ISerializable {private string album;

    private static readonly TimeSpan EmptyDuration = new TimeSpan(0);
    private string artist;
    private string description;
    private TimeSpan? duration;
    private string genre;
    private bool initialized;
    private string performer;
    private string title;
    private int? track;

    private AudioFile(SerializationInfo info, DeserializeInfo di): this(di.Server, di.Info, di.Type) {
      this.album = info.GetString("al");
      this.artist = info.GetString("ar");
      this.genre = info.GetString("g");
      this.performer = info.GetString("p");
      this.title = info.GetString("ti");
      try {
        this.track = info.GetInt32("tr");
      } catch (Exception) {
        // no op
      }

      var ts = info.GetInt64("d");
      if (ts > 0)
        this.duration = new TimeSpan(ts);

      this.initialized = true;
    }

    private AudioFile(SerializationInfo info, StreamingContext ctx):this(info, ctx.Context as DeserializeInfo) { }

    internal AudioFile(FileServer server, FileInfo aFile, DlnaMime aType): base(server, aFile, aType, DlnaMediaTypes.Audio) { }

    public override IMediaCoverResource Cover {
      get {
        if (this.cover == null && !this.LoadCoverFromCache())
          this.MaybeInit();

        return this.cover;
      }
    }

    public string MetaAlbum {
      get {
        this.MaybeInit();
        return this.album;
      }
    }

    public string MetaArtist {
      get {
        this.MaybeInit();
        return this.artist;
      }
    }

    public string MetaDescription {
      get {
        this.MaybeInit();
        return this.description;
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
        return this.genre;
      }
    }

    public string MetaPerformer {
      get {
        this.MaybeInit();
        return this.performer;
      }
    }

    public int? MetaTrack {
      get {
        this.MaybeInit();
        return this.track;
      }
    }

    public override IHeaders Properties {
      get {
        this.MaybeInit();
        var rv = base.Properties;
        if (this.album != null)
          rv.Add("Album", this.album);

        if (this.artist != null)
          rv.Add("Artist", this.artist);

        if (this.description != null)
          rv.Add("Description", this.description);

        if (this.duration != null)
          rv.Add("Duration", this.duration.Value.ToString("g"));

        if (this.genre != null)
          rv.Add("Genre", this.genre);

        if (this.performer != null)
          rv.Add("Performer", this.performer);

        if (this.track != null)
          rv.Add("Track", this.track.Value.ToString());

        return rv;
      }
    }

    public override string Title {
      get {
        this.MaybeInit();
        if (string.IsNullOrWhiteSpace(this.title))
          return base.Title;

        return
          this.track.HasValue
          ? $"{this.track.Value:D2}. — {this.title}"
          : this.title
        ;
      }
    }

    private void InitCover(Tag tag) {
      IPicture pic = null;
      foreach (var p in tag.Pictures) {
        if (p.Type == PictureType.FrontCover) {
          pic = p;
          break;
        }

        switch (p.Type) {
          case PictureType.Other:
          case PictureType.OtherFileIcon:
          case PictureType.FileIcon:
            pic = p;
            break;
          default:
            if (pic == null)
              pic = p;

            break;
        }
      }

      if (pic != null)
        try {
          this.cover = new Cover(this.Item, pic.Data.ToStream());
        } catch (Exception ex) {
          this.Debug($"Failed to generate thumb for {this.Item.FullName}", ex);
        }
      else
        try {
          var path = System.IO.Path.Combine(this.Item.Directory?.FullName??string.Empty, "folder.jpg");
          if (File.Exists(path))
            using (var stream = File.OpenRead(path))
              this.cover = new Cover(this.Item, stream);

        } catch (Exception ex) {
          this.Debug("Failed to generate thumb for " + this.Item.FullName, ex);
        }
    }

    private void MaybeInit() {
      if (this.initialized)
        return;

      try {
        using (var tl = TagLib.File.Create(new TagLibFileAbstraction(this.Item))) {
          try {
            this.duration = tl.Properties.Duration;
            if (this.duration.Value.TotalSeconds < 0.1)
              this.duration = null;

          } catch (Exception ex) {
            this.Debug("Failed to transpose Properties props", ex);
          }

          try {
            var t = tl.Tag;
            this.SetProperties(t);
            this.InitCover(t);
          } catch (Exception ex) {
            this.Debug("Failed to transpose Tag props", ex);
          }
        }

        this.initialized = true;
        this.Server.UpdateFileCache(this);

      } catch (CorruptFileException ex) {
        this.Debug($"Failed to read meta data via taglib for file {this.Item.FullName}", ex);
        this.initialized = true;
      } catch (UnsupportedFormatException ex) {
        this.Debug($"Failed to read meta data via taglib for file {this.Item.FullName}", ex);
        this.initialized = true;
      } catch (Exception ex) {
        this.Warn($"Unhandled exception reading meta data for file {this.Item.FullName}",ex);
      }
    }

    private void SetProperties(Tag tag) {
      this.genre = tag.FirstGenre;
      if (string.IsNullOrWhiteSpace(this.genre))
        this.genre = null;

      if (tag.Track != 0 && tag.Track < 1 << 10)
        this.track = (int) tag.Track;
      
      this.title = tag.Title;
      if (string.IsNullOrWhiteSpace(this.title))
        this.title = null;

      this.description = tag.Comment;
      if (string.IsNullOrWhiteSpace(this.description))
        this.description = null;

      this.performer =
        string.IsNullOrWhiteSpace(this.artist)
        ? tag.JoinedPerformers
        : tag.JoinedPerformersSort
      ;

      if (string.IsNullOrWhiteSpace(this.performer))
        this.performer = null;

      this.artist = tag.JoinedAlbumArtists;
      if (string.IsNullOrWhiteSpace(this.artist))
        this.artist = tag.JoinedComposers;

      if (string.IsNullOrWhiteSpace(this.artist))
        this.artist = null;

      this.album = tag.AlbumSort;
      if (string.IsNullOrWhiteSpace(this.album))
        this.album = tag.Album;

      if (string.IsNullOrWhiteSpace(this.album))
        this.album = null;
    }

    public override int CompareTo(IMediaItem other) {
      if (!this.track.HasValue)
        return base.CompareTo(other);

      var oa = other as AudioFile;
      int rv;
      if (oa?.track != null && (rv = this.track.Value.CompareTo(oa.track.Value)) != 0)
        return rv;

      return base.CompareTo(other);
    }

    public void GetObjectData(SerializationInfo info, StreamingContext ctx) {
      if (info == null)
        throw new ArgumentNullException(nameof(info));

      info.AddValue("al", this.album);
      info.AddValue("ar", this.artist);
      info.AddValue("g", this.genre);
      info.AddValue("p", this.performer);
      info.AddValue("ti", this.title);
      info.AddValue("tr", this.track);
      info.AddValue("d", this.duration.GetValueOrDefault(EmptyDuration).Ticks);
    }

    public override void LoadCover() {
      // No op
    }
  }
}
