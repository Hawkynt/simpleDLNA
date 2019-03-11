using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using log4net;
using TagLib.Riff;
using File = System.IO.File;

namespace NMaier.SimpleDlna.FileMediaServer.Files {
  /// <summary>
  /// Interacts with NFO files on the storage media.
  /// </summary>
  public static class NFOFileReader {

    #region nested types

    #region interface

    public interface IShowNfo {
      string TheTVDBId { get; }
      string Title { get; }
      string IMDBId { get;  }
      int Year { get; }
      string Plot { get; }
      string[] Genres { get; }
      string[] Actors { get; }
    }

    public interface IEpisodeNfo {
      string ShowTitle { get;}
      string Title { get;  }
      int Season { get; }
      int Episode { get; }
      string Plot { get; }
      TimeSpan? Duration { get; }
      string Director { get; }

    }

    public interface IMovieNfo {
      string IMDBId { get;}
      int Year { get; }
      string Title { get; }
      string OriginalTitle { get; }
      string Plot { get; }
      TimeSpan Duration { get; }
      string Director { get; }
      string[] Genres { get; }
      string[] Actors { get; }
    }

    #endregion

    #region xml-serialization

    [XmlRoot("tvshow")]
    public class ShowNfoFile:IShowNfo {

      [XmlElement("id")]
      public string TheTVDBId { get; set; }

      [XmlElement("year")]
      public string YearText { get; set; }

      [XmlIgnore]
      public int Year => int.Parse(this.YearText);

      [XmlElement("plot")]
      public string Plot { get; set; }

      [XmlElement("genre")]
      public string[] Genres { get; set; }

      [XmlElement("actor")]
      public Actor[] ActorInstances { get; set; }

      [XmlIgnore]
      public string[] Actors => this.ActorInstances?.Select(i => i.Name).ToArray();

      [XmlElement("title")]
      public string Title { get; set; }

      [XmlElement("imdb")]
      public string IMDBId { get; set; }

    }

    [XmlRoot("episodedetails")]
    public class EpisodeNfoFile : IEpisodeNfo {

      [XmlElement("showtitle")]
      public string ShowTitle { get; set; }

      [XmlElement("title")]
      public string Title { get; set; }

      [XmlElement("season")]
      public string SeasonText { get; set; }

      [XmlIgnore]
      public int Season => int.Parse(this.SeasonText);

      [XmlElement("episode")]
      public string EpisodeText { get; set; }

      [XmlIgnore]
      public int Episode => int.Parse(this.EpisodeText);

      [XmlElement("plot")]
      public string Plot { get; set; }

      [XmlIgnore]
      public TimeSpan? Duration {
        get {
          var seconds = this.FileDetails?.Streams?.Videos?.FirstOrDefault()?.Duration;
          return seconds == null ? (TimeSpan?)null : TimeSpan.FromSeconds(seconds.Value);
        }
      }

      [XmlElement("fileinfo")]
      public FileDetails FileDetails { get; set; }

      [XmlElement("director")]
      public string Director { get; set; }

    }

    [XmlRoot("movie")]
    public class MovieNfoFile:IMovieNfo {

      [XmlElement("id")]
      public string IMDBId { get; set; }

      [XmlElement("year")]
      public string YearText { get; set; }

      public int Year => int.Parse(this.YearText);

      [XmlElement("title")]
      public string Title { get; set; }

      [XmlElement("originaltitle")]
      public string OriginalTitle { get; set; }

      [XmlElement("plot")]
      public string Plot { get; set; }

      [XmlElement("runtime")]
      public string DurationText { get; set; }

      [XmlIgnore]
      public TimeSpan Duration => TimeSpan.FromMinutes(int.Parse(this.DurationText));

      [XmlElement("director")]
      public string Director { get; set; }

      [XmlElement("genre")]
      public string[] Genres { get; set; }

      [XmlElement("actor")]
      public Actor[] ActorInstances { get; set; }

      [XmlIgnore]
      public string[] Actors => this.ActorInstances?.Select(i => i.Name).ToArray();

    }

    public class Actor {

      [XmlElement("name")]
      public string Name { get; set; }
    }

    public class FileDetails {

      [XmlElement("streamdetails")]
      public StreamDetails Streams { get; set; }

    }

    public class StreamDetails {

      [XmlElement("video")]
      public VideoStreamDetails[] Videos { get; set; }

    }

    public class VideoStreamDetails {

      [XmlElement("durationinseconds")]
      public int Duration { get; set; }

      [XmlElement("width")]
      public int Width { get; set; }

      [XmlElement("height")]
      public int Height { get; set; }
    }

    #endregion

    #endregion

    #region consts

    private static readonly ILog logger = LogManager.GetLogger(typeof(TVStore));
    private static readonly XmlSerializer _SHOW_SERIALIZER=new XmlSerializer(typeof(ShowNfoFile));
    private static readonly XmlSerializer _EPISODE_SERIALIZER = new XmlSerializer(typeof(EpisodeNfoFile));
    private static readonly XmlSerializer _MOVIE_SERIALIZER = new XmlSerializer(typeof(MovieNfoFile));

    #endregion
    
    /// <summary>
    /// Reads a shows nfo file.
    /// </summary>
    /// <param name="directory">The directory of the show</param>
    /// <returns>An <see cref="IShowNfo">IShowInfo</see>-instance or <c>null</c></returns>
    public static IShowNfo ReadShowOrDefault(DirectoryInfo directory)
      =>directory==null||!directory.Exists ? null: _DeserializeOrDefault<ShowNfoFile>(Path.Combine(directory.FullName, "tvshow.nfo"),_SHOW_SERIALIZER)
    ;

    /// <summary>
    /// Reads an episode nfo file.
    /// </summary>
    /// <param name="file">The file of the episode</param>
    /// <returns>An <see cref="IEpisodeNfo">IEpisodeInfo</see>-instance or <c>null</c></returns>
    public static IEpisodeNfo ReadEpisodeOrDefault(FileInfo file)
      => file==null||!file.Exists || !_FileContainsText(Path.ChangeExtension(file.FullName, ".nfo"), "<episodedetails") ? null:_DeserializeOrDefault<EpisodeNfoFile>(Path.ChangeExtension(file.FullName, ".nfo"), _EPISODE_SERIALIZER)
    ;

    /// <summary>
    /// Reads a movie nfo file.
    /// </summary>
    /// <param name="file">The file of the episode</param>
    /// <returns>An <see cref="IMovieNfo">IMovieInfo</see>-instance or <c>null</c></returns>
    public static IMovieNfo ReadMovieOrDefault(FileInfo file)
      => file == null || !file.Exists || !_FileContainsText(Path.ChangeExtension(file.FullName, ".nfo"), "<movie") ? null : _DeserializeOrDefault<MovieNfoFile>(Path.ChangeExtension(file.FullName, ".nfo"), _MOVIE_SERIALIZER)
    ;

    /// <summary>
    /// Deserializes an xml file into an object.
    /// </summary>
    /// <typeparam name="TType">The type of the deserialized object.</typeparam>
    /// <param name="fileName">The filename to parse.</param>
    /// <param name="serializer">The XML serializer instance to use for parsing</param>
    /// <returns>A matching object instance or <c>null</c></returns>
    private static TType _DeserializeOrDefault<TType>(string fileName, XmlSerializer serializer) where TType:class {
      if (!File.Exists(fileName))
        return null;

      try {
        using (var stream = File.OpenRead(fileName))
          return serializer.Deserialize(stream) as TType;
      } catch (Exception e) {
        logger.ErrorFormat($"Failed to parse nfo file for {fileName}",fileName);
        return null;
      }
    }

    private static bool _FileContainsText(string fileName, string text) {
      if (!File.Exists(fileName))
        return false;

      var length = text.Length;
      var buffer = new List<char>();
      using (var stream = File.OpenRead(fileName))
        using(TextReader reader=new StreamReader(stream)){
          while (reader.Peek() >= 0) {
            buffer.Add((char) reader.Read());
            if (buffer.Count == length) {
              var found = true;
              for (var i = 0; i < length; ++i) {
                if (buffer[i] != text[i]) {
                  buffer.RemoveAt(0);
                  found = false;
                  break;
                }
              }

              if(found)
                return true;
            }
          }
        }

      return false;
    }

  }
}
