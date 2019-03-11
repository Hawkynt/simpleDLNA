using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NMaier.SimpleDlna.Utilities {
  public static class Formatting {
    private static readonly Regex sanitizer = new Regex(
      @"\b(?:the|an?|ein(?:e[rs]?)?|der|die|das)\b",
      RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex trim = new Regex(
      @"\s+|^[._+)}\]-]+|[._+({\[-]+$",
      RegexOptions.Compiled
    );

    private static readonly Regex trimmore =
      new Regex(@"^[^\d\w]+|[^\d\w]+$", RegexOptions.Compiled);

    private static readonly Regex respace =
      new Regex(@"[.+]+", RegexOptions.Compiled);

    public static bool Booley(string str) {
      str = str.Trim();
      var sc = StringComparer.CurrentCultureIgnoreCase;
      return sc.Equals("yes", str) || sc.Equals("1", str) || sc.Equals("true", str);
    }

    public static string FormatFileSize(this long size) {
      const long UNIT_WRAP_AROUND_AT = 9000;

      if (size < UNIT_WRAP_AROUND_AT)
        return $"{size} B";

      var ds = size / 1024.0;
      if (ds < UNIT_WRAP_AROUND_AT)
        return $"{ds:F2} KB";

      ds /= 1024.0;
      if (ds < UNIT_WRAP_AROUND_AT)
        return $"{ds:F2} MB";

      ds /= 1024.0;
      if (ds < UNIT_WRAP_AROUND_AT)
        return $"{ds:F3} GB";

      ds /= 1024.0;
      if (ds < UNIT_WRAP_AROUND_AT)
        return $"{ds:F3} TB";

      ds /= 1024.0;
      return $"{ds:F4} PB";
    }

    public static string GetSystemName() {
      var buffer=IntPtr.Zero;
      try {
        buffer = Marshal.AllocHGlobal(8192);
        // This is a hacktastic way of getting sysname from uname ()
        if (SafeNativeMethods.uname(buffer) != 0)
          throw new ArgumentException("Failed to get uname");

        return Marshal.PtrToStringAnsi(buffer);
      } finally {
        if(buffer!=IntPtr.Zero)
          Marshal.FreeHGlobal(buffer);
      }
    }

    public static string StemCompareBase(this string name) {
      if (name == null)
        throw new ArgumentNullException(nameof(name));

      var san = trimmore.Replace(sanitizer.Replace(name, string.Empty), string.Empty).Trim();
      return string.IsNullOrWhiteSpace(san) ? name : san.StemNameBase();
    }

    /*
        private readonly static Regex seriesreg = new Regex(
            @"(?<title>.*?)((?<season>[^0-9][0-9]{1,2})x(?<episode>[0-9]{1,2}[^0-9])|[ \._\-](?<episode>[0-9]{3})([ \._\-]|$)|(S(?<season>[0-9]{1,2})+(E(?<episode>[0-9]{1,2})| ))|[_ \-](?<episode>[0-9]{1,3})[\._ \-][^\dsS])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
        private readonly static string[] sregs = new string[] {
          @"(?<title>.*?)((?<season>[^0-9][0-9]{1,2})x(?<episode>[0-9]{1,2}[^0-9])", @"[ \._\-](?<episode>[0-9]{3})([ \._\-]|$)",@"(S(?<season>[0-9]{1,2})+(E(?<episode>[0-9]{1,2})| ))",@"[_ \-](?<episode>[0-9]{1,3})[\._ \-][^\dsS])"};
    */
    private static readonly Regex[] seriesregs = {
      new Regex(@"(?<title>.*?)([^0-9](?<season>[0-9]{1,2})x((?<episode>[0-9]{1,2})[^0-9]))", RegexOptions.Compiled | RegexOptions.IgnoreCase),
      new Regex(@"(?<title>.*?)?(S(?<season>[0-9]{1,2}).?(E(?<episode>[0-9]{1,2})|[ \._-]))", RegexOptions.Compiled | RegexOptions.IgnoreCase),
      new Regex(@"(?<title>.*?)[ \._\-](?<episode>[0-9]{3})([ \._\-]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
      //new Regex(@"(?<title>.*?)[_ \-](?<episode>[0-9]{2,3})[\._ \-][^\dsS]", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };

    private static readonly Regex[] movieclear = {
      new Regex(@"(?<title>.*?) ?\((?<year>[0-9]{4})\)",RegexOptions.Compiled | RegexOptions.IgnoreCase),
      new Regex(@"(?<title>.*?)[._ ]?((?<year>[0-9]{4})|[0-9]{3,4}p)",RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };

    private static readonly Regex cleanstr = new Regex(
      @"^\[.*?\]",
      RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex resolionRegex = new Regex(
      @"[_ \.](\d+p)[_ \.]",
      RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex releaserRegex = new Regex(
      @"[\- \.](\w+?)$",
      RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public class NiceName {
      public string Name { get; set; }
      public string Releaser { get; set; }
      public string Resolution { get; set; }
    }

    public class NiceSeriesName : NiceName {
      public int Season { get; set; }
      public int Episode { get; set; }
    }

    public class MovieName : NiceName {
      public int Year { get; set; }
    }

    private static bool _TryParseShow(string name, out NiceName result,string releaser=null,string resolution=null) {
      Match match = null;
      foreach (var regex in seriesregs) {
        match = regex.Match(name);
        if (match.Success)
          break;
      }

      if (match == null || !match.Success) {
        result = null;
        return false;
      }

      var season = 0;
      var episode = 0;
      var nicename = string.Empty;

      if (!string.IsNullOrEmpty(match.Groups["title"].Value)) {
        nicename = match.Groups["title"].Value;
      } else {
        Trace.WriteLine(name);
        Trace.WriteLine("ERROR PARSING " + match.Groups["title"].Value);
      }

      if (!string.IsNullOrEmpty(match.Groups["season"].Value))
        if (!int.TryParse(match.Groups["season"].Value, out season)) {
          Trace.WriteLine(name);
          Trace.WriteLine("ERROR PARSING " + match.Groups["season"].Value);
        }

      if (!string.IsNullOrEmpty(match.Groups["episode"].Value))
        if (!int.TryParse(match.Groups["episode"].Value, out episode)) {
          Trace.WriteLine(name);
          Trace.WriteLine("ERROR PARSING " + match.Groups["episode"].Value);
        }

      result= new NiceSeriesName {
        Name = cleanstr.Replace(nicename.StemNameBase(), string.Empty),
        Episode = episode,
        Season = season,
        Resolution = resolution,
        Releaser = releaser
      };
      return true;
    }
    
    private static bool _TryParseMove(string name, out NiceName result, string releaserText, string resolutionText) {
      Match match = null;
      foreach (var regex in movieclear) {
        match = regex.Match(name);
        if (match.Success)
          break;
      }

      if (match == null || !match.Success) {
        result = null;
        return false;
      }

      var nicename=string.Empty;
      var year=0;

      if (!string.IsNullOrEmpty(match.Groups["title"].Value))
        nicename = match.Groups["title"].Value;
      else {
        Trace.WriteLine(name);
        Trace.WriteLine("ERROR PARSING " + match.Groups["title"].Value);
      }

      if (!string.IsNullOrEmpty(match.Groups["year"].Value))
        year = int.Parse(match.Groups["year"].Value);
      else {
        Trace.WriteLine(name);
        Trace.WriteLine("ERROR PARSING " + match.Groups["year"].Value);
      }

      result = new MovieName {
        Name = cleanstr.Replace(nicename.StemNameBase(), ""),
        Year = year,
        Resolution = resolutionText,
        Releaser = releaserText
      };
      return true;
    }

    public static NiceName GetNiceNameOrNull(this string name) {
      var releaser = releaserRegex.Match(name);
      var releaserText = string.Empty;
      if (releaser.Success)
        releaserText = releaser.Groups[1].Value;

      var resolution = resolionRegex.Match(name);
      var resolutionText = string.Empty;
      if (resolution.Success)
        resolutionText = resolution.Groups[1].Value;

      NiceName result;

      if (_TryParseShow(name, out result, releaserText, resolutionText))
        return result;

      if (_TryParseMove(name, out result, releaserText, resolutionText))
        return result;

      return null;
    }


    public static string StemNameBase(this string name) {
      if (name == null)
        throw new ArgumentNullException(nameof(name));

      if (!name.Contains(" ")) {
        name = name.Replace('_', ' ');
        if (!name.Contains(" "))
          name = name.Replace('-', ' ');

        name = respace.Replace(name, " ");
      }

      var ws = name;
      string wsprev;
      do {
        wsprev = ws;
        ws = trim.Replace(wsprev.Trim(), " ").Trim();
      } while (wsprev != ws);

      return string.IsNullOrWhiteSpace(ws) ? name : ws;
    }
  }
}
