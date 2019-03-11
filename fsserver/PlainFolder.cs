using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Server.Metadata;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer
{
  internal class PlainFolder : VirtualFolder, IMetaInfo
  {
    private readonly DirectoryInfo dir;

    private PlainFolder(FileServer server, DlnaMediaTypes types,
                        VirtualFolder parent, DirectoryInfo dir,
                        IEnumerable<string> exts)
      : base(parent, dir.Name)
    {
      Server = server;
      this.dir = dir;
      folders = (from d in dir.GetDirectories()
                 let m = TryGetFolder(server, types, d)
                 where 
                  m != null && m.ChildCount > 0 && 
                  (server.ShowSample || !d.FullName.ToLower().Contains("sample")) && 
                  (server.ShowHidden || (!d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith(".")))
                 select m as IMediaFolder).ToList();

      var rawfiles = from f in dir.GetFiles("*.*")
                     select f;
      var files = new List<BaseFile>();
      foreach (var f in rawfiles) {
        var ext = f.Extension;
        if (string.IsNullOrEmpty(ext) ||
          !exts.Contains(ext.Substring(1), StringComparer.OrdinalIgnoreCase)) {
          continue;
        }
        try {
          if ((server.ShowSample || !f.FullName.ToLower().Contains("sample")) &&
            (server.ShowHidden || (!f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Name.StartsWith(".")))) {
            files.Add(server.GetFile(this, f));
          }
        }
        catch (Exception ex) {
          server.Warn(f, ex);
        }
      }
      resources.AddRange(files);
    }

    protected PlainFolder(FileServer server, DlnaMediaTypes types,
                          VirtualFolder parent, DirectoryInfo dir)
      : this(server, types, parent, dir, types.GetExtensions())
    {
    }

    public DateTime InfoDate
    {
      get
      {
        return dir.LastWriteTimeUtc;
      }
    }

    public long? InfoSize
    {
      get
      {
        return null;
      }
    }

    public override string Path
    {
      get
      {
        return dir.FullName;
      }
    }

    public FileServer Server
    {
      get;
      protected set;
    }

    public override string Title
    {
      get
      {
        return dir.Name;
      }
    }


    private static readonly string[] _EXCLUDED_ROOT_DIRECTORY_NAMES =  {"System Volume Information","$RECYCLE.BIN" };

    private PlainFolder TryGetFolder(FileServer server, DlnaMediaTypes types,
                                     DirectoryInfo d)
    {

      // HAWKYNT: exclude recycle bin and system volume information
      if (d.Parent.IsRoot()) {
        if (_EXCLUDED_ROOT_DIRECTORY_NAMES.Any(i => i == d.Name)) {
          Trace.WriteLine($"[Info]Skipping {d.FullName} because it is blacklisted");
          return null;
        }
      }

      try {
        return new PlainFolder(server, types, this, d);
      }
      catch (Exception ex) {
        server.Warn("Failed to access folder", ex);
        return null;
      }
    }
  }
}
