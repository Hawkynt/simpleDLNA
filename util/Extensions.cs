using System.IO;

namespace NMaier.SimpleDlna.Utilities {
  public static class DirectoryInfoExtensions {
    public static bool IsRoot(this DirectoryInfo @this) => @this.Root.FullName == @this.FullName;
  }
}
