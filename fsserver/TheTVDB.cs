using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using log4net;
using NMaier.SimpleDlna.Utilities;
using Formatting = NMaier.SimpleDlna.Utilities.Formatting;

namespace NMaier {
  internal class TVEpisode {
    public int Season { get; set; }
    public int Episode { get; set; }
    public string Title { get; set; }
    public DateTime FirstAired { get; set; }
    public int AbsoluteNumber { get; set; }
    public int EpisodeId { get; set; }
  }

  internal class TVShowInfo {
    public int ID { get; set; }
    public string Name { get; set; }

    public List<TVEpisode> TVEpisodes { get; set; }

    public long LastUpdated { get; set; }

    public string IMDBID { get; set; }

    public TVShowInfo() {
      this.TVEpisodes = new List<TVEpisode>();
    }

    public string Find(int season, int episode) {
      var altepisode = episode;
      var altseason = season;
      if (season == 0) {
        altepisode = episode % 100;
        altseason = Math.Max((episode - altepisode) / 100, 1);
      }

      var res =
        this.TVEpisodes.Find(
          delegate(TVEpisode ep) {
            if (season != 0 || ep.AbsoluteNumber == 0 || this.TVEpisodes.Count < episode)
              return ep.Episode == altepisode && ep.Season == altseason;
            else
              return ep.AbsoluteNumber == episode;
          }
        );

      var ret = "";
      if (res != null) {
        ret = $"{res.Season}x{res.Episode}: {res.Title}";
        if (season == 0 && res.AbsoluteNumber > 0 && res.Episode != altepisode)
          ret = $"{ret} ({res.AbsoluteNumber})";

      } else {
        ret = $"{altseason}x{altepisode}";
        if (season == 0 && episode != altepisode)
          ret = $"{ret} ({episode})";

      }

      return ret;
    }
  }

  internal class TheTVDB {
    public static readonly ConcurrentDictionary<int, TVShowInfo> cacheshow = new ConcurrentDictionary<int, TVShowInfo>(); // :(
    private static readonly ConcurrentDictionary<string, int> cache = new ConcurrentDictionary<string, int>();
    private static readonly string tvdbkey = ConfigurationSettings.AppSettings["TVShowDBKey"];

    private static readonly ILog logger =LogManager.GetLogger(typeof(TVStore));
    
    public static TVShowInfo GetTVShowDetails(int showid, bool noncache = false) {
      if (cacheshow.TryGetValue(showid, out var entry) && !noncache)
        return entry;

      if (string.IsNullOrWhiteSpace(tvdbkey))
        return null;

      var url = string.Format("http://thetvdb.com/api/{1}/series/{0}/all/en.zip", showid, tvdbkey);
      byte[] xmlData;

      using (var wc = new WebClient())
        xmlData = wc.DownloadData(url);

      var xmlStream = new MemoryStream(xmlData);
      var archive = new ZipArchive(xmlStream, ZipArchiveMode.Read);

      foreach (var zipEntry in archive.Entries)
        if (zipEntry.Name == "en.xml") {
          
          var xmlDoc = new XmlDocument();
          using (var memoryStream = new MemoryStream()) {
            var zipStream = zipEntry.Open();
            zipStream.CopyTo(memoryStream);
            var textBytes = memoryStream.ToArray();
            var xmlStr = Encoding.UTF8.GetString(textBytes);
            xmlDoc.LoadXml(xmlStr);
          }

          entry = new TVShowInfo {
            ID = showid,
            Name = xmlDoc.SelectSingleNode("//SeriesName").InnerText,
            LastUpdated = long.Parse(xmlDoc.SelectSingleNode(".//lastupdated").InnerText),
            IMDBID = xmlDoc.SelectSingleNode("//IMDB_ID").InnerText,
            TVEpisodes = new List<TVEpisode>()
          };

          var airtime = xmlDoc.SelectSingleNode("//Airs_Time").InnerText;

          var episodes = xmlDoc.SelectNodes("//Episode");
          foreach (XmlNode ep in episodes) {
            var seasoninfo = new TVEpisode();
            seasoninfo.FirstAired = new DateTime();

            var epnum = ep.SelectSingleNode(".//EpisodeNumber").InnerText;
            var seasonnum = ep.SelectSingleNode(".//SeasonNumber").InnerText;
            var title = ep.SelectSingleNode(".//EpisodeName").InnerText;
            var firstaired = ep.SelectSingleNode(".//FirstAired").InnerText;
            var absnumber = ep.SelectSingleNode(".//absolute_number").InnerText;

            if (!string.IsNullOrEmpty(firstaired))
              if (DateTime.TryParse(firstaired + " " + airtime, out var faired))
                seasoninfo.FirstAired = faired;

            seasoninfo.Episode = (int) Math.Ceiling(double.Parse(epnum, new CultureInfo("en-US")));
            seasoninfo.Season = (int) Math.Ceiling(double.Parse(seasonnum, new CultureInfo("en-US")));
            seasoninfo.Title = title;
            if (!string.IsNullOrEmpty(absnumber))
              seasoninfo.AbsoluteNumber = int.Parse(absnumber, new CultureInfo("en-US"));

            seasoninfo.EpisodeId = int.Parse(ep.SelectSingleNode(".//EpisodeNumber").InnerText, new CultureInfo("en-US"));

            entry.TVEpisodes.Add(seasoninfo);
          }

          cacheshow.AddOrUpdate(showid, entry, (key, oldvalue) => oldvalue.LastUpdated > entry.LastUpdated ? oldvalue : entry);
        }

      return entry;
    }

    public static int? GetTVShowID(string path) {
      try {
        if (path.ToLower().Contains("movies"))
          return null;

        var p = Directory.GetParent(path);
        string hit;
        
        var showName = p.Name.GetNiceNameOrNull();
        if (showName is Formatting.NiceSeriesName == false || string.IsNullOrEmpty(showName.Name))
          showName = Path.GetFileNameWithoutExtension(path).GetNiceNameOrNull();

        if (showName is Formatting.NiceSeriesName)
          hit = showName.Name;
        else
          return null;


        if (cache.TryGetValue(hit, out var entry))
          return entry;

        var url = $"http://thetvdb.com/api/GetSeries.php?seriesname={hit}";
        string xmlStr;
        using (var wc = new WebClient())
          xmlStr = wc.DownloadString(url);

        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(xmlStr);
        
        var seriesidText = xmlDoc.SelectSingleNode("//seriesid");
        if (seriesidText != null) {
          entry = int.Parse(xmlDoc.SelectSingleNode("//seriesid").InnerText);
          cache.TryAdd(hit, entry);
        } else {
          cache.TryAdd(hit, -1);
          logger.InfoFormat("TVDB: Cant find in database {0} -- {1}", path, hit);
          return 0;
        }

        return entry;
      } catch (Exception e) {
        logger.Error($"TV: Failed to get TVShowID for {path}", e);
        return null;
      }
    }

    public class UpdateInfo {
      public int[] Series { get; set; }
      public int[] Episodes { get; set; }
    }

    public static UpdateInfo FetchUpdate(string timeframe) {
      if (string.IsNullOrEmpty(timeframe))
        timeframe = "month"; // day, week, all
      var url = string.Format("http://thetvdb.com/api/{0}/updates/updates_{1}.zip", tvdbkey, timeframe);
      byte[] xmlData;

      using (var wc = new WebClient()) {
        xmlData = wc.DownloadData(url);
      }

      var xmlStream = new MemoryStream(xmlData);
      var archive = new ZipArchive(xmlStream, ZipArchiveMode.Read);

      foreach (var a in archive.Entries)
        if (a.Name == "updates_" + timeframe + ".xml") {
          var info = new UpdateInfo();
          var memoryStream = new MemoryStream();
          var x = a.Open();
          x.CopyTo(memoryStream);
          var t = memoryStream.ToArray();

          var xmlStr = Encoding.UTF8.GetString(t);
          var xmlDoc = new XmlDocument();
          xmlDoc.LoadXml(xmlStr);

          var s = xmlDoc.SelectNodes("//Series").OfType<XmlNode>();
          info.Series = (from n in s
            select int.Parse(n.SelectSingleNode("//id").InnerText)).ToArray();

          var e = xmlDoc.SelectNodes("//Episode").OfType<XmlNode>();
          info.Episodes = (from n in e
            select int.Parse(n.SelectSingleNode("//id").InnerText)).ToArray();

          return info;
        }

      return null;
    }

    public static UpdateInfo UpdatesSince(long time) {
      var info = new UpdateInfo();
      var url = string.Format("http://thetvdb.com/api/Updates.php?type=all&time={0}", time);
      string xmlStr;
      using (var wc = new WebClient()) {
        xmlStr = wc.DownloadString(url);
      }

      var xmlDoc = new XmlDocument();
      xmlDoc.LoadXml(xmlStr);

      var s = xmlDoc.SelectNodes("//Series").OfType<XmlNode>();
      info.Series = (from n in s
        select int.Parse(n.InnerText)).ToArray();

      var e = xmlDoc.SelectNodes("//Episode").OfType<XmlNode>();
      info.Episodes = (from n in e
        select int.Parse(n.InnerText)).ToArray();

      return info;
    }
  }
}
