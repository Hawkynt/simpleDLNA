using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer {
  public sealed class FileServer
    : Logging, IMediaServer, IVolatileMediaServer, IDisposable {

    private static readonly StringComparer icomparer =StringComparer.CurrentCultureIgnoreCase;
    private static readonly double ChangeDefaultTime = TimeSpan.FromSeconds(30).TotalMilliseconds;
    private static readonly double ChangeRenamedTime = TimeSpan.FromSeconds(10).TotalMilliseconds;
    private static readonly double ChangeDeleteTime = TimeSpan.FromSeconds(2).TotalMilliseconds;
    private readonly Timer changeTimer =new Timer(TimeSpan.FromSeconds(20).TotalMilliseconds);
    private readonly Timer watchTimer =new Timer(TimeSpan.FromMinutes(10).TotalMilliseconds);
    private readonly Regex re_sansitizeExt =new Regex(@"[^\w\d]+", RegexOptions.Compiled);
    private readonly string[] recognizedExt = (from x in DlnaMaps.Ext2Media select x.Key.ToUpperInvariant()).Union(new[] { "SRT" }).ToArray();

    private readonly List<WeakReference> pendingFiles =new List<WeakReference>();
    private readonly DirectoryInfo[] directories;
    private readonly Identifiers ids;
    private readonly DlnaMediaTypes types;
    private readonly FileSystemWatcher[] watchers;

    private DateTime lastChanged = DateTime.Now;
    private FileStore store;
    private TVStore tvStore;
    
    public FileServer(DlnaMediaTypes types, Identifiers ids,
      params DirectoryInfo[] directories) {
      this.types = types;
      this.ids = ids;
      this.directories =(
        from d in directories.Distinct()
        where
          (this.ShowHidden || !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith(".") || d.IsRoot()) &&
          (this.ShowSample || !d.FullName.ToLower().Contains("sample"))
        select d
      ).ToArray();

      if (this.directories.Length == 0)
        throw new ArgumentException("Provide one or more directories",nameof(directories));

      var parent = this.directories[0].Parent ?? this.directories[0];

      this.FriendlyName = $"{this.directories[0].Name} ({parent.FullName})";
      if (this.directories.Length != 1)
        this.FriendlyName += $" + {this.directories.Length - 1}";

      this.watchers = (
        from d in directories
        select new FileSystemWatcher(d.FullName)
      ).ToArray();

      this.Uuid = this.DeriveUUID();
    }

    public event EventHandler Changed;

    public event EventHandler Changing;

    public IHttpAuthorizationMethod Authorizer { get; set; }

    public string FriendlyName { get; set; }

    public bool ShowHidden { get; set; }

    public bool ShowSample { get; set; }

    public Guid Uuid { get; }

    private Guid DeriveUUID() {
      var bytes = Guid.NewGuid().ToByteArray();
      var i = 0;
      var copy = Encoding.ASCII.GetBytes("sdlnafs");
      for (; i < copy.Length; ++i)
        bytes[i] = copy[i];

      copy = Encoding.UTF8.GetBytes(this.FriendlyName);
      for (var j = 0; j < copy.Length && i < bytes.Length - 1; ++i, ++j)
        bytes[i] = copy[j];

      return new Guid(bytes);
    }

    private void DoRoot() {
      lock (this) {
        IMediaFolder newMaster;
        if (this.directories.Length == 1)
          newMaster = new PlainRootFolder(this, this.types, this.directories[0]);
        else {
          var virtualMaster = new VirtualFolder(null, this.FriendlyName,Identifiers.GeneralRoot);
          foreach (var d in this.directories)
            virtualMaster.Merge(new PlainRootFolder(this, this.types, d));

          newMaster = virtualMaster;
        }

        lock (this.ids) {
          this.ids.RegisterFolder(Identifiers.GeneralRoot, newMaster);
          this.ids.RegisterFolder(Identifiers.SamsungImages,new VirtualClonedFolder(newMaster,Identifiers.SamsungImages, this.types & DlnaMediaTypes.Image));
          this.ids.RegisterFolder(Identifiers.SamsungAudio,new VirtualClonedFolder(newMaster,Identifiers.SamsungAudio, this.types & DlnaMediaTypes.Audio));
          this.ids.RegisterFolder(Identifiers.SamsungVideo,new VirtualClonedFolder(newMaster,Identifiers.SamsungVideo, this.types & DlnaMediaTypes.Video));
        }
      }

      this.Thumbnail();
    }

    private void OnChanged(object source, FileSystemEventArgs e) {
      try {
        if (this.store != null && icomparer.Equals(e.FullPath, this.store.StoreFile.FullName))
          return;

        var ext = string.IsNullOrEmpty(e.FullPath) ? Path.GetExtension(e.FullPath) : string.Empty;
        if (!string.IsNullOrEmpty(ext) &&!this.types.GetExtensions().Contains(ext.Substring(1), StringComparer.OrdinalIgnoreCase)) {
          this.DebugFormat("Skipping name {0} {1} {2}",e.Name, Path.GetExtension(e.FullPath),string.Join(", ", this.types.GetExtensions()));
          return;
        }

        var recognizedExt =(
          from x in this.recognizedExt
          where e.FullPath.ToUpperInvariant().EndsWith(x)
          select x
        ).ToArray();

        if (recognizedExt.Length == 0) {
          this.DebugFormat("Skipping change ({1}): {0}", e.FullPath, e.ChangeType);
          return;
        }

        this.DebugFormat("File System changed ({1}): {0}", e.FullPath, e.ChangeType);
        this.DelayedRescan(e.ChangeType);
      } catch (Exception ex) {
        this.Error("OnChanged failed", ex);
      }
    }

    private void OnRenamed(object source, RenamedEventArgs e) {
      try {
        var exts = this.types.GetExtensions();
        var ext = string.IsNullOrEmpty(e.FullPath) ? Path.GetExtension(e.FullPath) : string.Empty;
        var c = StringComparer.OrdinalIgnoreCase;
        if (!string.IsNullOrEmpty(ext) &&
            !exts.Contains(ext.Substring(1), c) &&
            !exts.Contains(ext.Substring(1), c)) {
          this.DebugFormat("Skipping name {0} {1} {2}",e.Name, Path.GetExtension(e.FullPath), string.Join(", ", exts));
          return;
        }

        if (!File.GetAttributes(e.FullPath).HasFlag(FileAttributes.Directory)) {
          var recognizedExt =(
            from x in this.recognizedExt
            where e.FullPath.ToUpperInvariant().EndsWith(x)
            select x
          ).ToArray();

          if (recognizedExt.Length == 0) {
            this.DebugFormat("Skipping change ({1}): {0}", e.FullPath, e.ChangeType);
            return;
          }
        }

        this.DebugFormat("File System changed ({1}): {0}", e.FullPath, e.ChangeType);
        this.DelayedRescan(e.ChangeType);
      } catch (Exception ex) {
        this.Error("OnRenamed failed", ex);
      }
    }

    private bool rescanning = true;

    public bool Rescanning {
      get => this.rescanning;
      set {
        if (this.rescanning == value)
          return;

        this.rescanning = value;
        if (this.rescanning)
          this.Rescan();
      }
    }

    private void RescanInternal() {
      if (!this.rescanning) {
        this.Debug("Rescanning disabled");
        return;
      }

      Task.Factory.StartNew(() => {
          this.Changing?.Invoke(this, EventArgs.Empty);

          try {
            this.NoticeFormat("Rescanning {0}...", this.FriendlyName);
            this.DoRoot();
            this.NoticeFormat("Done rescanning {0}...", this.FriendlyName);
          } catch (Exception ex) {
            this.Error(ex);
          }


          this.Changed?.Invoke(this, EventArgs.Empty);
        },
        TaskCreationOptions.AttachedToParent | TaskCreationOptions.LongRunning
      );
    }

    private void RescanTimer(object sender, ElapsedEventArgs e) => this.RescanInternal();

    private void Thumbnail() {
      if (this.store == null) {
        lock (this)
          this.pendingFiles.Clear();

        return;
      }

      lock (this) {
        this.DebugFormat("Passing {0} files to background cacher", this.pendingFiles.Count);
        BackgroundCacher.AddFiles(this.store, this.pendingFiles);
        this.pendingFiles.Clear();
      }
    }

    internal void DelayedRescan(WatcherChangeTypes changeType) {
      if (this.changeTimer.Enabled)
        return;

      switch (changeType) {
        case WatcherChangeTypes.Deleted:
          this.changeTimer.Interval = ChangeDeleteTime;
          break;
        case WatcherChangeTypes.Renamed:
          this.changeTimer.Interval = ChangeRenamedTime;
          break;
        default:
          this.changeTimer.Interval = ChangeDefaultTime;
          break;
      }

      var diff = DateTime.Now - this.lastChanged;
      if (diff.TotalSeconds <= 30) {
        this.changeTimer.Interval = Math.Max(TimeSpan.FromSeconds(20).TotalMilliseconds, this.changeTimer.Interval);
        this.InfoFormat("Avoid thrashing {0}", this.changeTimer.Interval);
      }

      this.DebugFormat("Change in {0} on {1}", this.changeTimer.Interval, this.FriendlyName);
      this.changeTimer.Enabled = true;
      this.lastChanged = DateTime.Now;
    }

    internal Cover GetCover(BaseFile file) => this.store?.MaybeGetCover(file);

    internal BaseFile GetFile(PlainFolder aParent, FileInfo info) {
      BaseFile item;
      lock (this.ids)
        item = this.ids.GetItemByPath(info.FullName) as BaseFile;

      if (item != null &&
          item.InfoDate == info.LastAccessTimeUtc &&
          item.InfoSize == info.Length)
        return item;

      var ext = this.re_sansitizeExt.Replace(
        info.Extension.ToUpperInvariant().Substring(1),
        string.Empty
      );

      var type = DlnaMaps.Ext2Dlna[ext];
      var mediaType = DlnaMaps.Ext2Media[ext];

      if (this.store != null) {
        item = this.store.MaybeGetFile(this, info, type);
        if (item != null) {
          lock (this)
            this.pendingFiles.Add(new WeakReference(item));

          return item;
        }
      }

      lock (this) {
        var rv = BaseFile.GetFile(aParent, info, type, mediaType);
        this.pendingFiles.Add(new WeakReference(rv));
        return rv;
      }
    }

    internal void UpdateFileCache(BaseFile aFile) => this.store?.MaybeStoreFile(aFile);
    internal void UpdateTVCache(TVShowInfo tvinfo) => this.tvStore?.Insert(tvinfo);

    public void Dispose() {
      foreach (var w in this.watchers)
        w.Dispose();

      this.changeTimer?.Dispose();
      this.watchTimer?.Dispose();
      this.store?.Dispose();
      FileStreamCache.Clear();
    }

    public IMediaItem GetItem(string id) {
      lock (this.ids)
        return this.ids.GetItemById(id);
    }

    public void Load() {
      if (this.types == DlnaMediaTypes.Audio)
        lock (this.ids)
          if (!this.ids.HasViews)
            this.ids.AddView("music");

      this.DoRoot();

      this.changeTimer.AutoReset = false;
      this.changeTimer.Elapsed += this.RescanTimer;

      foreach (var watcher in this.watchers) {
        watcher.IncludeSubdirectories = true;
        watcher.Created += this.OnChanged;
        watcher.Deleted += this.OnChanged;
        watcher.Renamed += this.OnRenamed;
        watcher.EnableRaisingEvents = true;
      }

      this.watchTimer.Elapsed += this.RescanTimer;
      this.watchTimer.Enabled = true;
    }

    public void Rescan() => this.RescanInternal();

    public void SetCacheFile(FileInfo info) {
      if (this.tvStore == null)
        try {
          this.tvStore = new TVStore();
        } catch (Exception ex) {
          this.Warn("TvStore is not available; failed to load SQLite Adapter", ex);
        }

      if (this.store != null) {
        this.store.Dispose();
        this.store = null;
      }

      try {
        this.store = new FileStore(info);
      } catch (Exception ex) {
        this.Warn("FileStore is not available; failed to load SQLite Adapter", ex);
        this.store = null;
      }
    }
  }
}
