/*
HalBot is an IRC chat bot that is capable of analyzing and learning language
(to a degree).

http://www.adammil.net/
Copyright (C) 2006-2010 Adam Milazzo

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.
This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using AdamMil.IO;
using AdamMil.Utilities;

namespace HalBot
{

#region RssFeed
abstract class RssFeed : IDisposable
{
  protected RssFeed(string feedUrl)
  {
    if(string.IsNullOrEmpty(feedUrl)) throw new ArgumentException("The feed URL cannot be empty.");

    DownloadItems   = false;
    GuidsToMaintain = 100;
    TTL             = new TimeSpan(8, 0, 0);
    Url             = feedUrl;
  }

  #region Item
  public sealed class Item
  {
    internal Item(XmlElement element, XmlNamespaceManager ns)
    {
      Guid           = element.SelectString("guid", null);
      GuidIsLink     = element.SelectSingleNode("guid").GetBoolAttribute("isPermaLink");
      _link          = element.SelectString("link", null);
      Description    = element.SelectString("description", null);
      
      XmlNode contentNode = element.SelectSingleNode("content:encoded", ns);
      string content = contentNode == null ? null : contentNode.InnerText;
      EncodedContent = string.IsNullOrEmpty(content) ? null : content;
    }

    public string Description
    {
      get; private set;
    }

    public string EncodedContent
    {
      get; private set;
    }

    public string Guid
    {
      get; private set;
    }

    public bool GuidIsLink
    {
      get; private set;
    }

    public string Id
    {
      get { return !string.IsNullOrEmpty(Guid) ? Guid : Link; }
    }

    public string Link
    {
      get { return !string.IsNullOrEmpty(_link) ? _link : GuidIsLink && !string.IsNullOrEmpty(Guid) ? Guid : null; }
    }

    readonly string _link;
  }
  #endregion

  public bool DownloadItems
  {
    get; set;
  }

  public int GuidsToMaintain
  {
    get { return _guidsToMaintain; }
    set
    {
      if(value < 0) throw new ArgumentOutOfRangeException();
      _guidsToMaintain = value;
      RemoveOldGuids();
    }
  }

  public DateTime LastChange
  {
    get; private set;
  }

  public DateTime LastUpdate
  {
    get; private set;
  }

  public bool ShouldUpdate
  {
    get { return DateTime.Now >= LastUpdate + TTL; }
  }

  public TimeSpan TTL
  {
    get; set;
  }

  public string Url
  {
    get; set;
  }

  public virtual void Dispose()
  {
  }

  public void Update()
  {
    try
    {
      XmlDocument doc = new XmlDocument();
      XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
      ns.AddNamespace("content", "http://purl.org/rss/1.0/modules/content/");

      LastUpdate = DateTime.Now;
      doc.LoadXml(Download(Url));

      foreach(XmlElement itemElement in doc.DocumentElement.SelectNodes("//item"))
      {
        Item item = new Item(itemElement, ns);
        string itemId = item.Id;
        if(string.IsNullOrEmpty(itemId) || knownGuids.Contains(itemId)) continue;

        LastChange = DateTime.Now;
        AddGuid(itemId);
        ProcessItem(item);
      }
    }
    catch { } // TODO: log this error
  }

  protected void AddGuid(string guid)
  {
    if(!string.IsNullOrEmpty(guid) && !knownGuids.Contains(guid))
    {
      knownGuids.Add(guid);
      RemoveOldGuids();
    }
  }

  protected abstract void ProcessItem(Item item);

  protected string GetItemBody(Item item, out bool? isHtml)
  {
    if(DownloadItems && !string.IsNullOrEmpty(item.Link))
    {
      isHtml = true; // downloaded pages are HTML
      return Download(item.Link);
    }
    else if(item.EncodedContent != null)
    {
      isHtml = true; // encoded content is HTML
      return item.EncodedContent;
    }
    else
    {
      isHtml = null; // description is unknown
      return item.Description;
    }
  }

  void RemoveOldGuids()
  {
    if(knownGuids.Count > GuidsToMaintain) knownGuids.RemoveRange(0, GuidsToMaintain - knownGuids.Count);
  }

  List<string> knownGuids = new List<string>();
  int _guidsToMaintain;

  static string Download(string url)
  {
    HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
    HttpWebResponse response = (HttpWebResponse)request.GetResponse();
    Stream responseStream = null;
    try
    {
      responseStream = response.GetResponseStream();
      return GetEncoding(response).GetString(IOH.ReadAllBytes(responseStream));
    }
    finally
    {
      if(responseStream != null) responseStream.Dispose();
      response.Close();
    }
  }

  static Encoding GetEncoding(HttpWebResponse response)
  {
    if(response != null)
    {
      string encoding = response.ContentEncoding;
      if(!string.IsNullOrEmpty(encoding))
      {
        try { return Encoding.GetEncoding(encoding); }
        catch(ArgumentException) { } // TODO: log this
      }
    }

    return Encoding.UTF8;
  }
}
#endregion

#region HalBotRssFeed
abstract class HalBotRssFeed : RssFeed
{
  protected HalBotRssFeed(Brain parentBrain, string feedUrl) : base(feedUrl)
  {
    Brain = new Brain(parentBrain);
  }

  static HalBotRssFeed()
  {
    LoadAbbreviations();
  }

  public Brain Brain
  {
    get; private set;
  }

  public string LogName
  {
    get { return _logName; }
    set
    {
      if(!string.Equals(value, LogName, StringComparison.Ordinal))
      {
        Utility.Dispose(ref log);
        Utility.Dispose(ref idLog);
        _logName = null;

        if(!string.IsNullOrEmpty(value))
        {
          string logFile = Path.Combine(HalBot.LogDirectory, value + ".log");
          string idFile  = Path.Combine(HalBot.LogDirectory, value + ".ids");
          log      = new StreamWriter(logFile, true);
          idLog    = new StreamWriter(idFile, true);
          _logName = value;
        }
      }
    }
  }

  public override void Dispose()
  {
    if(log != null) log.Close();
    if(idLog != null) idLog.Close();
    base.Dispose();
  }

  protected abstract IEnumerable<string> ProcessBody(string body, bool? isHtml);

  protected override void ProcessItem(Item item)
  {
    bool? isHtml;
    IEnumerable<string> lines = ProcessBody(GetItemBody(item, out isHtml), isHtml);

    lock(Brain)
    {
      foreach(string line in lines)
      {
        if(!string.IsNullOrEmpty(line))
        {
          if(log != null) log.WriteLine(line);
          Brain.LearnLine(line, false);
        }
      }
    }

    if(idLog != null) idLog.WriteLine(item.Id);
  }

  protected static IEnumerable<string> SplitOnLF(string body)
  {
    int start = 0;
    for(Match m = lfRegex.Match(body); m.Success; m = m.NextMatch())
    {
      if(start < m.Index)
      {
        string line = body.Substring(start, m.Index-start);
        yield return line;
        start = m.Index + m.Length;
      }
    }
    if(start < body.Length) yield return body.Substring(start);
  }

  protected static IEnumerable<string> SplitOnPunctuation(string body, bool isTextProperlyCapitalized)
  {
    int start = 0;
    
    while(true)
    {
      // skip leading whitespace
      while(start < body.Length && char.IsWhiteSpace(body[start])) start++;

      if(start >= body.Length) break;

      // we'll assume that the body starts with a sentence. if it it starts with '(', we'll scan for the closing
      // parenthesis and return everything in between. otherwise, we'll scan for a period or question mark and return
      // a single sentence
      if(body[start] == '(')
      {
        start++;
        int end = body.IndexOf(')', start); // TODO: support nested parentheses?
        if(end != -1)
        {
          yield return body.Substring(start, end-start);
          start = end+1;
        }
      }
      else
      {
        int scanFrom = start, end;

        while(true) // TODO: add more support for quotations?
        {
          // find the next period or question mark
          int question = body.IndexOf('?', scanFrom), period = body.IndexOf('.', scanFrom);
          if(question == -1 && period == -1) goto done;

          if(period == -1 || question != -1 && question < period) // if the question mark comes first, return the text up to it
          {
            end = question;
            break;
          }
          else // otherwise, the period comes first
          {
            scanFrom = period+1; // the next scan starts after the period

            // TODO: augment this to handle quotations and sentences in parentheses

            // we need to determine whether this is a sentence-ending period. it's considered to be if:
            // 1. it is followed by a post period character (space or quote or parethesis) or is at the end of the text, and
            // 2a. the next non-whitespace character is a character that looks like an initial sentence character, or
            //     there is no next non-whitespace character, or
            // 2b. the text may not be capitalized correctly and the word (i.e. block of contiguous non-whitespace) of which it's
            //     a part is not a URL, doesn't contain multiple periods, is not all capitals, and isn't on a list of common
            //     abbreviations
            // note that these rules will allow common abbreviations through if there are multiple periods, as in an
            // ellipsis

            if(period == body.Length-1 || IsPostPeriodCharacter(body[period+1]))
            {
              int nextPrintable = period+2; // skip the period and the next character, which is known to be whitespace
              while(nextPrintable < body.Length && char.IsWhiteSpace(body[nextPrintable])) nextPrintable++;

              // check if it's followed by an uppercase letter
              bool isSentenceEndingPeriod = nextPrintable >= body.Length || IsInitialSentenceCharacter(body[nextPrintable]);
              if(!isSentenceEndingPeriod && !isTextProperlyCapitalized)
              {
                int wordStart = period; // scan to identify the complete word in which it appears
                while(wordStart > 0 && !char.IsWhiteSpace(body[wordStart-1])) wordStart--;

                string word = body.Substring(wordStart, period-wordStart); // get the word, excluding the period
                isSentenceEndingPeriod = word.IndexOf('.') == -1 && word.IndexOf("://", StringComparison.Ordinal) == -1 &&
                                         !AllCapitals(word) && !IsKnownAbbreviation(word, isTextProperlyCapitalized);
              }

              // if it seems to be a sentence-ending period, return the text up to and including the period
              if(isSentenceEndingPeriod)
              {
                end = period;
                break;
              }
            }
          }
        }

        // if the character after the sentence end is a closing quotation mark, assume that the sentence was in a quotation and
        // return the quotation mark, too
        if(end < body.Length-1)
        {
          char next = body[end+1];
          if(next == '\"' || next == '”') end++;
        }
        yield return body.Substring(start, end-start+1);
        start = end+1;
      }
    }

    done:
    if(start < body.Length) yield return body.Substring(start);
  }

  protected static IEnumerable<string> SplitOnPunctuationOrLF(string body, bool isTextProperlyCapitalized)
  {
    foreach(string section in SplitOnLF(body))
    {
      foreach(string line in SplitOnPunctuation(section, isTextProperlyCapitalized)) yield return line;
    }
  }

  protected static IEnumerable<string> Unquote(string body)
  {
    return new string[] { body.Trim().Trim(new char[] { '"', '“', '”' }).Trim() };
  }

  StreamWriter log, idLog;
  string _logName;

  static bool AllCapitals(string word)
  {
    for(int i=0; i<word.Length; i++)
    {
      if(!char.IsUpper(word[i]) && char.IsLetter(word[i])) return false;
    }
    return true;
  }

  static bool IsInitialSentenceCharacter(char c)
  {
    return char.IsUpper(c) || c == '"' || c == '(' || c == '“';
  }

  static bool IsKnownAbbreviation(string word, bool isTextProperlyCapitalized)
  {
    if(isTextProperlyCapitalized) // if the text is properly capitalized, then we shouldn't need to worry about
    {                             // ambiguity
      return abbreviations.ContainsKey(word.ToLowerInvariant());
    }
    else // otherwise, we do need to worry about it. if the word is ambiguous, it must match exactly
    {
      bool ambiguous;
      return abbreviations.TryGetValue(word.ToLowerInvariant(), out ambiguous) &&
             (!ambiguous || abbreviations.ContainsKey(word));
    }
  }

  static bool IsPostPeriodCharacter(char c)
  {
    return char.IsWhiteSpace(c) || c == '"' || c == ')' || c == '”';
  }

  static void LoadAbbreviations()
  {
    string file = Path.Combine(HalBot.DataDirectory, "abbreviations.txt");
    if(File.Exists(file))
    {
      using(StreamReader sr = new StreamReader(file))
      {
        sr.ProcessNonEmptyLines(word =>
        {
          if(word[0] != '#') // if the line is not a comment...
          {
            bool ambiguous = word[0] == '*';
            word = ambiguous ? word.Substring(1) : word.ToLowerInvariant();
            if(word.Length != 0 && word[word.Length-1] == '.') word = word.Substring(0, word.Length-1);
            if(word.Length != 0) abbreviations[word] = ambiguous;
          }
        });
      }
    }
  }

  static readonly Regex lfRegex = new Regex(@"(?:\r?\n)+", RegexOptions.Singleline);
  static readonly Dictionary<string, bool> abbreviations = new Dictionary<string, bool>();
}
#endregion

#region HalBotXmlRssFeed
sealed class HalBotXmlRssFeed : HalBotRssFeed
{
  public HalBotXmlRssFeed(Brain parentBrain, XmlElement feedElement)
    : base(parentBrain, feedElement.GetAttributeValue("url"))
  {
    Brain.MaxBlendChance = feedElement.GetSingleAttribute("blendChance", Brain.MaxBlendChance);

    DownloadItems   = feedElement.GetBoolAttribute("downloadItems", DownloadItems);
    GuidsToMaintain = feedElement.GetInt32Attribute("itemsToRemember", GuidsToMaintain);
    IsHtml          = feedElement.GetBoolAttribute("isHtml", true);
    TTL             = feedElement.GetAttribute<TimeSpan>("ttl", s => ParseTimeSpan(s), TTL);

    string processorType = feedElement.GetAttributeValue("processor", "splitOnPunctuationOrLF");
    Processor = (ProcessorType)Enum.Parse(typeof(ProcessorType), processorType, true);

    string logName = feedElement.GetAttributeValue("logName");
    if(!string.IsNullOrEmpty(logName))
    {
      string logFile = Path.Combine(HalBot.LogDirectory, logName + ".log");
      if(File.Exists(logFile))
      {
        using(StreamReader reader = new StreamReader(logFile)) reader.ProcessNonEmptyLines(line => Brain.LearnLine(line, false));
      }

      string idFile = Path.Combine(HalBot.LogDirectory, logName + ".ids");
      if(File.Exists(idFile))
      {
        using(StreamReader reader = new StreamReader(idFile)) reader.ProcessNonEmptyLines(guid => AddGuid(guid));
      }

      LogName = logName; // set the property after reading the files because setting it opens the files (which may lock them)
    }
  }

  public bool IsHtml
  {
    get; set;
  }

  enum ProcessorType
  {
    Passthrough, SplitOnLF, SplitOnPunctuation, SplitOnPunctuationOrLF, Unquote
  }

  ProcessorType Processor
  {
    get; set;
  }

  protected override IEnumerable<string> ProcessBody(string body, bool? isHtml)
  {
    if(IsHtml || isHtml.HasValue && isHtml.Value) body = HtmlDocument.ConvertToUnformattedText(body);

    switch(Processor)
    {
      case ProcessorType.SplitOnLF: return SplitOnLF(body);
      case ProcessorType.SplitOnPunctuation: return SplitOnPunctuation(body, true);
      case ProcessorType.SplitOnPunctuationOrLF: return SplitOnPunctuationOrLF(body, true);
      case ProcessorType.Unquote: return Unquote(body);
      default: return new string[] { body };
    }
  }

  static TimeSpan ParseTimeSpan(string timeSpan)
  {
    Match m = timeSpanRe.Match(timeSpan);
    if(!m.Success) throw new FormatException("Invalid time span: " + timeSpan);

    int hours = int.Parse(m.Groups[1].Value), minutes, seconds;
    int.TryParse(m.Groups[2].Value, out minutes);
    int.TryParse(m.Groups[3].Value, out seconds);
    return new TimeSpan(hours, minutes, seconds);
  }

  static readonly Regex timeSpanRe = new Regex(@"^(\d\d?)(?::(\d\d?)(?::(\d\d?))?)?$", RegexOptions.Singleline);
}
#endregion

} // namespace HalBot
