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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using AdamMil.Collections;
using AdamMil.Mathematics;
using AdamMil.Utilities;
using IrcLib;

namespace HalBot
{

sealed class HalBot : IrcClient, IDisposable
{
  public HalBot()
  {
    AutoLearn       = true;
    AutoReconnect   = true;
    BabbleTime      = 30;
    Brain           = new Brain();
    FlushLog        = true;
    Interject       = true;
    InterjectChance = 4;
    LogToConsole    = true;
    RejoinOnKick    = true;
    RssFeeds        = new NonNullCollection<HalBotRssFeed>();
    TypingDelay     = 70;
    UsersToIgnore   = new HashSet<string>(NameComparer.Instance);
  }

  /// <summary>Gets or sets whether the bot will learn from things that people say. The default is true.</summary>
  public bool AutoLearn
  {
    get; set;
  }

  /// <summary>Gets or sets whether the bot will try to reconnect when it gets disconnected. The default is true.</summary>
  public bool AutoReconnect
  {
    get; set;
  }

  /// <summary>Gets or sets the number of minutes between times when the bot will attempt to say something random. Setting this
  /// property to zero disables babbling. The default is 30 minutes.
  /// </summary>
  public int BabbleTime
  {
    get { return _babbleTime; }
    set
    {
      if(value < 0) throw new ArgumentOutOfRangeException();
      _babbleTime = value;
    }
  }

  /// <summary>Gets the bot's <see cref="Brain"/>.</summary>
  public Brain Brain
  {
    get; private set;
  }

  /// <summary>Gets or sets the bot's desired nickname. The bot will attempt to acquire and maintain the given nickname. If not
  /// set, a random nickname will be used.
  /// </summary>
  public string DesiredNickname
  {
    get; set;
  }

  /// <summary>Gets or sets whether log entries will flushed to disk immediately. The default is true.</summary>
  public bool FlushLog
  {
    get; set;
  }

  /// <summary>Gets or sets whether the bot will interject into conversations (i.e. speak when not spoken to). The default is
  /// true.
  /// </summary>
  public bool Interject
  {
    get; set;
  }

  /// <summary>Gets or sets the base chance that the bot will interject into conversations (i.e. speak when not spoken to) as
  /// a percentage. The default is 4%.
  /// </summary>
  public int InterjectChance
  {
    get { return _interjectChance; }
    set
    {
      if(value < 0 || value > 100) throw new ArgumentOutOfRangeException();
      _interjectChance = value;
    }
  }

  /// <summary>Gets or sets the path to the log file. If set to a relative path, the log file will be placed relative to the
  /// HalBot log directory. If set to null or an empty string, logging will be disabled. The default is null.
  /// </summary>
  public string LogFile
  {
    get { return _logFile; }
    set
    {
      if(!string.Equals(value, LogFile, StringComparison.Ordinal))
      {
        Utility.Dispose(ref log);
        _logFile = null;
        if(!string.IsNullOrEmpty(value))
        {
          if(!Path.IsPathRooted(value)) value = Path.Combine(HalBot.LogDirectory, value);
          log = new StreamWriter(value, true);
          _logFile = value;
        }
      }
    }
  }

  /// <summary>Gets or sets whether raw traffic will be logged when <see cref="Verbose"/> is true. The default is false.</summary>
  public bool LogRawTraffic
  {
    get; set;
  }

  /// <summary>Gets or sets whether some log messages will be written to the console in addition to the log file. The default is true.</summary>
  public bool LogToConsole
  {
    get; set;
  }

  /// <summary>Gets or sets whether the bot will automatically rejoin a channel when kicked. The default is true.</summary>
  public bool RejoinOnKick
  {
    get; set;
  }

  /// <summary>Gets a collection of the bot's RSS feeds.</summary>
  public NonNullCollection<HalBotRssFeed> RssFeeds
  {
    get; private set;
  }

  /// <summary>Gets or sets the simulated typing delay in milliseconds per character. The default is 70 ms/character.</summary>
  public int TypingDelay
  {
    get { return _typingDelay; }
    set
    {
      if(value < 0) throw new ArgumentOutOfRangeException();
      _typingDelay = value;
    }
  }

  /// <summary>Gets a set of users to ignore, by name.</summary>
  public HashSet<string> UsersToIgnore
  {
    get; private set;
  }

  /// <summary>Gets or sets whether logging will be verbose. The default is false.</summary>
  public bool Verbose
  {
    get; set;
  }

  /// <summary>Sends a random message if <paramref name="force"/> is true or the bot hasn't done so recently. If
  /// <paramref name="to"/> is null or empty, the message will be sent to a random channel that the bot is in. Otherwise, it will
  /// be sent to the user or channel named.
  /// </summary>
  public void Babble(string to, bool force)
  {
    if(string.IsNullOrEmpty(to))
    {
      if(JoinedChannels.Count == 0) return;
      else to = JoinedChannels.Values.ToArray().SelectRandom(rand).Name; // TODO: it'd be nice if the collection supported direct indexing...
    }

    // if we haven't babbled for half an hour and we're in at least one channel...
    if(force || BabbleTime != 0 && (DateTime.Now - lastBabbleTime).TotalMinutes >= BabbleTime && JoinedChannels.Count != 0)
    {
      // if any conversation has happened in the channel in the past 5 minutes...
      List<Conversation> conversations;
      if(force || forums.TryGetValue(to, out conversations) && conversations.Count != 0 &&
                  (DateTime.Now - conversations.Last().LastActivity).TotalMinutes <= 5)
      {
        string message = null;

        // now we need to choose what to talk about. first we'll see if we have anything new from the RSS feeds
        var updatedFeeds = from f in RssFeeds
                           where (DateTime.Now - f.LastChange).TotalHours < 12
                           orderby f.LastChange descending
                           select f;

        // if there's an updated feed, try that first
        HalBotRssFeed feed = updatedFeeds.FirstOrDefault();
        if(feed != null) lock(feed.Brain) message = feed.Brain.GetRandomUtterance();

        if(message == null) // if there was no feed, or there was nothing to say about it...
        {
          if(rand.NextBoolean()) // there's a 50% chance of talking about something from a random RSS feed
          {
            feed = RssFeeds.SelectRandom(rand);
            lock(feed.Brain) message = feed.Brain.GetRandomUtterance();
          }

          if(message == null) message = Brain.GetRandomUtterance();
        }

        if(message != null)
        {
          message = NormalizeTextForIrc(message);
          SendDelayedMessage(to, message);
          lastBabbleTime = DateTime.Now;
        }
      }
    }
  }

  /// <summary>Disposes the bot by disconnecting it and closing all open files.</summary>
  public void Dispose()
  {
    Disconnect();
    foreach(RssFeed feed in RssFeeds) feed.Dispose();
    Utility.Dispose(log);
  }

  /// <summary>Loads RSS feeds from the given file, which is expected to be an XML file that conforms to the <c>feeds.xsd</c>
  /// schema.
  /// </summary>
  public void LoadRssFeeds(string xmlFeedFile)
  {
    XmlDocument doc = new XmlDocument();
    XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
    ns.AddNamespace("f", "http://adammil.net/HalBot/feeds.xsd");
    doc.Load(xmlFeedFile);
    foreach(XmlElement feed in doc.DocumentElement.SelectNodes("f:feed", ns)) RssFeeds.Add(new HalBotXmlRssFeed(Brain, feed));
  }

  /// <summary>Adds a message to the log.</summary>
  public void Log(string format, params object[] args)
  {
    Log(string.Format(format, args));
  }

  /// <summary>Adds a message to the log.</summary>
  public void Log(string line)
  {
    if(log != null)
    {
      log.WriteLine(line);
      if(FlushLog) log.Flush();
    }

    if(LogToConsole) Console.WriteLine(line);
  }

  /// <summary>Adds an error message to the log based on an exception.</summary>
  public void LogError(Exception ex)
  {
    Log("ERROR: {0}: {1}", ex.GetType().Name, ex.Message);
  }

  /// <summary>Sends a message delayed according to the <see cref="TypingDelay"/>.</summary>
  public void SendDelayedMessage(string to, string message)
  {
    if(string.IsNullOrEmpty(to) || string.IsNullOrEmpty(message)) throw new ArgumentException();
    AddDelayedAction(message.Length * TypingDelay, delegate { SendMessage(to, message); });
  }

  /// <summary>Forces RSS feeds to be updated immediately.</summary>
  public void UpdateRssFeeds()
  {
    UpdateRssFeeds(true);
  }

  protected override void OnConnect()
  {
    base.OnConnect();

    Log("CONNECTED");

    TryToGetDesiredNick();

    if(timerThread == null)
    {
      timerEvent.Reset();
      quitEvent.Reset();
      timerThread = new Thread(TimerThreadFunction);
      timerThread.Start();
    }
  }

  protected override void OnCTCPMessage(string from, string to, string command, string[] args)
  {
    if(AreNamesEqual(to, Nickname) && string.Equals(command, "VERSION", StringComparison.OrdinalIgnoreCase))
    {
      SendCTCPNotice(from, "VERSION Hal " + (9000 + Version).ToString(CultureInfo.InvariantCulture));
    }
    else
    {
      base.OnCTCPMessage(from, to, command, args);
    }

    if(Verbose) Log("CTCPM: {0} {1} -> {2}: {3}", command, from, to, StripControlCharacters(string.Join(" ", args)));
  }

  protected override void OnCTCPNotice(string from, string to, string command, string[] args)
  {
    base.OnCTCPNotice(from, to, command, args);
    if(Verbose) Log("CTCPN: {0} {1} -> {2}: {3}", command, from, to, StripControlCharacters(string.Join(" ", args)));
  }

  protected override void OnDisconnect(bool intentional)
  {
    List<string> channelsToRejoin = intentional ? null : JoinedChannels.Keys.ToList();

    base.OnDisconnect(intentional);

    Log("DISCONNECTED");
    
    if(!intentional && AutoReconnect && RemoteEndPoint != null)
    {
      const int BaseDelay = 5000, MaxDelay = 5*60*1000;
      
      // define an action that takes a delay as a parameter, tries to connect, and repeats itself with twice the delay if
      // connection failed
      Action<int> action = null;
      action = delay =>
      {
        if(!IsConnected && RemoteEndPoint != null)
        {
          try
          {
            Log("RECONNECTING...");
            Connect(RemoteEndPoint);
            Join(channelsToRejoin, null, null);
          }
          catch
          {
            delay = Math.Min(MaxDelay, delay*2);
            AddDelayedAction(delay, delegate { action(delay); });
          }
        }
      };

      AddDelayedAction(BaseDelay, delegate { action(BaseDelay); });
    }
    else
    {
      KillTimerThread();
    }
  }

  protected override void OnJoin(string user, string channelName)
  {
    base.OnJoin(user, channelName);
    if(Verbose || AreNamesEqual(user, Nickname)) Log("JOIN: {0} to {1}", user, channelName);
  }

  protected override void OnKick(string kicker, string kicked, string channelName, string kickText)
  {
    base.OnKick(kicker, kicked, channelName, kickText);
    Log("KICK: {0} by {1} from {2}: {3}", kicked, kicker, channelName, kickText);

    // if the bot was kicked and it should try to rejoin, do so 20 seconds later
    if(AreNamesEqual(kicked, Nickname) && RejoinOnKick) AddDelayedAction(20000, delegate { Join(channelName); });
  }

  protected override void OnMessageReceived(string from, string to, string text)
  {
    base.OnMessageReceived(from, to, text);

    // if it was sent to a channel, then the forum is the channel. otherwise, it's the private conversation with the sender
    Channel channel = IsChannelName(to) ? EnsureChannelKnown(to) : null;
    string target, forum = channel != null ? to : from, message = NormalizeMessage(channel, text, out target);

    User fromUser = EnsureUserKnown(from);
    User toUser = channel == null ? EnsureUserKnown(to) : target != null ? EnsureUserKnown(target) : null;
    bool addressedToMe = toUser == User;

    if(Verbose || addressedToMe) Log("RECV: {0} -> {1}: {2}", from, to, text);

    // add the statement to the most recent conversation in this forum, or a new conversation if that one has ended
    List<Conversation> recentConversations;
    if(!forums.TryGetValue(forum, out recentConversations)) forums[forum] = recentConversations = new List<Conversation>();

    Conversation conversation;
    if(recentConversations.Count != 0 && recentConversations.Last().Ended == null)
    {
      conversation = recentConversations.Last();
    }
    else
    {
      conversation = new Conversation(Brain, forum);
      recentConversations.Add(conversation);
      if(recentConversations.Count > MaxConversationsPerForum) recentConversations.RemoveAt(0);
    }

    conversation.AddStatement(fromUser, toUser, message, AutoLearn);

    if(!UsersToIgnore.Contains(fromUser.Name) && fromUser != User)
    {
      string reply = null;
      int replyChance = 0;
      bool talkingAboutMe = false;

      if(addressedToMe) // if the message was addressed to us (possibly in a channel)...
      {
        replyChance = 100;
        conversation.AddressedMeAt = DateTime.Now;
        conversation.WhoAddressedMe.Add(fromUser);
      }
      else if(toUser == null && Interject) // otherwise, if the message was sent to a channel with no explicit addressee...
      {
        replyChance = InterjectChance; // the base reply chance

        // if I was addressed by the speaker in this conversation, increase the reply chance...
        if(conversation.WhoAddressedMe.Contains(fromUser))
        {
          TimeSpan timeSinceIWasAddressed = DateTime.Now - conversation.AddressedMeAt.Value;
          // decide how likely we are to respond based on the characteristics of the message text and timing. generally we'll be
          // more likely to respond the more recently we were addressed, but very quick replies are likely things like "haha" and
          // don't need a response (hence the low chance for messages arriving in less than 5 seconds)
          if(timeSinceIWasAddressed.TotalSeconds <= 5) replyChance = 20;
          else if(timeSinceIWasAddressed.TotalSeconds <= 10) replyChance = 90;
          else if(timeSinceIWasAddressed.TotalSeconds <= 30) replyChance = 75;
          else if(timeSinceIWasAddressed.TotalSeconds <= 60) replyChance = 50;
          else if(timeSinceIWasAddressed.TotalMinutes <= 2) replyChance = 20;

          // if there's only one other person in the conversation, we can assume they're talking to us
          if(conversation.Participants.Count == 1) conversation.AddressedMeAt = DateTime.Now;
        }

        string[] words = Brain.SplitWords(message, true);
        talkingAboutMe = words.Any(word => AreNamesEqual(word, Nickname));

        if(words.Length == 0)
        {
          replyChance = 0;
        }
        // if the message contains a greeting and our nickname, assume it's directed at us and always reply
        else if(talkingAboutMe && words.Any(word => Brain.IsGreeting(word)))
        {
          conversation.AddressedMeAt = DateTime.Now;
          conversation.WhoAddressedMe.Add(fromUser);
          replyChance = 100;
        }
        else if(message.EndsWith("?", StringComparison.Ordinal)) // increase the chance of replying if the message was a question
        {
          replyChance = Math.Min(100, replyChance*2);
        }
        else if(words.Length < 2 || message.Length < 5) // greatly reduce the chance of replying if the message is really short
        {
          replyChance = Math.Min(10, replyChance);
        }
      }

      if(rand.Next(100) < replyChance)
      {
        // TODO: it's okay to use the conversation brain if it's not empty...
        Brain brainToUse = AutoLearn ? conversation.Brain : Brain;
        reply = brainToUse.GetResponse(message, 5, true, true, delegate(Brain.Utterance u)
        {
          float value = 0.5f; // TODO: a better evaluation function would be nice

          if(string.Equals(u.Text, lastReplyText, StringComparison.Ordinal) || // don't repeat ourselves or the user
             string.Equals(u.Text, message, StringComparison.Ordinal))
          {
            value = 0;
          }

          if(u.Text.Length > 300) value *= 0.25f; // penalize long messages
          else if(u.Text.Length > 200) value *= 0.5f;

          return value;
        });

        if(reply == null) reply = brainToUse.GetRandomUtterance(true);

        if(reply != null)
        {
          lastReplyText = reply;
          reply = NormalizeTextForIrc(reply);
          if(channel == null) SendDelayedMessage(from, reply);
          else SendDelayedMessage(to, from + ": " + reply);
        }
      }

      if(AutoLearn && !talkingAboutMe)
      {
        string toLearn = FilterTextForLearning(message);
        if(toLearn != null)
        {
          Log("LEARN: " + toLearn);
          Brain.LearnLine(toLearn, true);
        }
        else
        {
          Log("NOLEARN: " + message);
        }
      }
    }
  }

  protected override void OnMessageSent(IEnumerable<string> to, string text)
  {
    base.OnMessageSent(to, text);
    Log("SEND: {0} -> {1}: {2}", Nickname, StringUtility.Join(",", to), text);
  }

  protected override void OnNick(string oldNick, string newNick)
  {
    base.OnNick(oldNick, newNick);

    foreach(List<Conversation> recentConversations in forums.Values)
    {
      foreach(Conversation conversation in recentConversations) conversation.Participants.OnNameChanged(oldNick, newNick);
    }

    forums.OnNameChanged(oldNick, newNick);

    if(Verbose) Log("NICK: {0} -> {1}", oldNick, newNick);
  }

  protected override void OnPart(string user, string channelName)
  {
    base.OnPart(user, channelName);
    if(Verbose || AreNamesEqual(user, Nickname)) Log("PART: {0} from {1}", user, channelName);
  }

  protected override void OnRawInput(string line)
  {
    base.OnRawInput(line);
    if(Verbose && LogRawTraffic) Log("<<< " + line);
  }

  protected override void OnRawOutput(string line)
  {
    base.OnRawOutput(line);
    if(Verbose && LogRawTraffic) Log(">>> " + line);
  }

  /// <summary>Gets the directory from where data files should be read.</summary>
  internal static string DataDirectory
  {
    get
    {
      if(_dataDirectory == null) _dataDirectory = GetDirectory("data", false);
      return _dataDirectory;
    }
  }

  /// <summary>Gets the directory where log files should be placed.</summary>
  internal static string LogDirectory
  {
    get
    {
      if(_logDirectory == null || !Directory.Exists(_logDirectory)) _logDirectory = GetDirectory("logs", true);
      return _logDirectory;
    }
  }

  const int Version = 1, MaxConversationsPerForum = 1, EndConversationAfterMinutes = 15, RemoveForumAfterDays = 7;

  #region Conversation
  /// <summary>Represents a conversation.</summary>
  sealed class Conversation
  {
    const int StatementsToKeep = 1;

    public Conversation(Brain parentBrain, string forum)
    {
      Brain   = new Brain(parentBrain);
      Forum   = forum;
      Started = LastActivity = DateTime.Now;
      WhoAddressedMe = new HashSet<User>(UserComparer.Instance);
    }

    public void AddStatement(User from, User to, string message, bool learn)
    {
      if(LastStatements.Count == LastStatements.Capacity) LastStatements.RemoveFirst();
      LastStatements.Add(new Statement(from, to, message));
      Participants[from.Name] = from;
      if(to != null) Participants[to.Name] = to;

      LastActivity = DateTime.Now;
      Ended        = null;

      if(learn)
      {
        message = FilterTextForLearning(message);
        if(message != null) Brain.LearnLine(message, true);
      }
    }

    public override string ToString()
    {
      return "Conversation in/with " + Forum;
    }

    public readonly string Forum;
    public readonly Brain Brain;
    public readonly IrcUserList Participants = new IrcUserList();
    public readonly CircularList<Statement> LastStatements = new CircularList<Statement>(StatementsToKeep, false);
    public readonly HashSet<User> WhoAddressedMe = new HashSet<User>(UserComparer.Instance);
    public DateTime Started, LastActivity;
    public DateTime? Ended, AddressedMeAt;
  }
  #endregion

  #region DelayedAction
  /// <summary>Represents an action to be performed at a later time.</summary>
  sealed class DelayedAction : IComparable<DelayedAction>
  {
    public DelayedAction(DateTime time, Action action)
    {
      Time   = time;
      Action = action;
    }

    public readonly Action Action;
    public readonly DateTime Time;

    int IComparable<DelayedAction>.CompareTo(DelayedAction other)
    {
      return -Time.CompareTo(other.Time); // reverse the order because we want the earliest time to be last in order
    }
  }
  #endregion

  #region Statement
  /// <summary>Represents a statement in a conversation.</summary>
  struct Statement
  {
    public Statement(User from, User to, string text)
    {
      From = from;
      To   = to;
      Text = text;
    }

    public readonly User From, To;
    public readonly string Text;
  }
  #endregion

  /// <summary>Performs an action after a delay.</summary>
  void AddDelayedAction(int delayMs, Action action)
  {
    if(delayMs < 0) throw new ArgumentOutOfRangeException();
    if(delayMs == 0)
    {
      action();
    }
    else
    {
      delayedActions.Enqueue(new DelayedAction(DateTime.Now.AddMilliseconds(delayMs), action));
      timerEvent.Set();
    }
  }

  /// <summary>Ages conversations, removing them if they're no longer relevant.</summary>
  void AgeConversations()
  {
    // end conversations that have had no activity for some number of minutes, and remove forums that have had no
    // activity for some number of days
    List<string> deadForums = new List<string>();
    DateTime now = DateTime.Now;
    foreach(var pair in forums.EnumeratePairs()) // for each forum...
    {
      bool forumHasRecentConversations = false; // see if the forum has conversations with recent activity
      foreach(Conversation conversation in pair.Value)
      {
        TimeSpan age = now - conversation.LastActivity;
        if(age.TotalDays < RemoveForumAfterDays) // if it has a conversation with recent enough activity to keep the forum...
        {
          forumHasRecentConversations = true;
          if(age.TotalMinutes >= EndConversationAfterMinutes) // end the conversation if it hasn't had any recent activity
          {
            if(Verbose) Log("ENDCONV: " + conversation.ToString());
            conversation.Ended = now;
          }
        }
      }

      if(!forumHasRecentConversations) deadForums.Add(pair.Key);
    }

    foreach(string forum in deadForums)
    {
      if(Verbose) Log("ENDFORUM: " + forum);
      forums.Remove(forum);
    }
  }

  /// <summary>Shuts down the timer thread.</summary>
  void KillTimerThread()
  {
    if(timerThread != null)
    {
      quitEvent.Set();
      timerEvent.Set();
      if(!timerThread.Join(45000)) timerThread.Abort();
      timerThread = null;
    }
  }

  /// <summary>Manages background tasks, such as executing delayed actions, updating RSS feeds, babbling, aging conversations,
  /// etc.
  /// </summary>
  void TimerThreadFunction()
  {
    while(!quitEvent.WaitOne(0))
    {
      int timeToSleep = 30000;
      if(delayedActions.Count != 0)
      {
        lock(this)
        {
          timeToSleep = Math.Max(0, Math.Min(timeToSleep, (int)(delayedActions.Peek().Time - DateTime.Now).TotalMilliseconds));
        }
      }

      // wait until there's something to do
      if(timeToSleep > 0) timerEvent.WaitOne(timeToSleep);

      if(delayedActions.Count != 0)
      {
        lock(this) // perform the actions that are due
        {
          while(delayedActions.Count != 0 && delayedActions.Peek().Time < DateTime.Now && !quitEvent.WaitOne(0))
          {
            try { delayedActions.Dequeue().Action(); }
            catch { } // TODO: log the error
          }
        }
      }

      if((DateTime.Now - lastSlowTimerEvents).TotalSeconds >= 30 && !quitEvent.WaitOne(0)) // do the slow events every 30 seconds
      {
        lastSlowTimerEvents = DateTime.Now;

        lock(this)
        {
          UpdateRssFeeds(false);
          TryToGetDesiredNick();
          AgeConversations();
          Babble(null, false);
        }
      }
    }
  }

  /// <summary>Attempts to acquire the desired nickname if we don't already have it.</summary>
  void TryToGetDesiredNick()
  {
    if(!string.IsNullOrEmpty(DesiredNickname) && !string.Equals(Nickname, DesiredNickname, StringComparison.Ordinal))
    {
      SetNickname(DesiredNickname);
    }
  }

  /// <summary>Updates the RSS feeds. If <paramref name="force"/> is true, all feeds will be updated even if they were already
  /// updated recently.
  /// </summary>
  void UpdateRssFeeds(bool force)
  {
    foreach(RssFeed feed in RssFeeds)
    {
      if(feed.ShouldUpdate || force) feed.Update();
    }
  }

  readonly Random rand = new Random();
  readonly IrcNameDictionary<List<Conversation>> forums = new IrcNameDictionary<List<Conversation>>();
  readonly PriorityQueue<DelayedAction> delayedActions = new PriorityQueue<DelayedAction>();
  readonly AutoResetEvent timerEvent = new AutoResetEvent(false);
  readonly ManualResetEvent quitEvent = new ManualResetEvent(false);
  StreamWriter log;
  Thread timerThread;
  string lastReplyText, _logFile;
  DateTime lastSlowTimerEvents, lastBabbleTime = DateTime.Now; // don't babble immediately
  int _babbleTime, _interjectChance, _typingDelay;

  /// <summary>Filters a message to normalize it for learning. If null is returned, the message should not be learned from.</summary>
  static string FilterTextForLearning(string message)
  {
    char c = message[message[0] == '"' && message.Length > 1 ? 1 : 0];

    // if the first character (skipping quotation marks) is not a letter or digit, don't learn from it
    if(!char.IsLetterOrDigit(c)) return null;

    // analyze the characters in the text
    int lower=0, upper=0, spaces=0, other, letters;
    for(int i=0; i<message.Length; i++)
    {
      c = message[i];
      if(char.IsLower(c)) lower++;
      else if(char.IsUpper(c)) upper++;
      else if(char.IsWhiteSpace(c)) spaces++;
    }
    letters = lower+upper;
    other   = message.Length - letters - spaces;

    // if there are fewer than 20 letters, more uppercase than lowercase letters, more than 30% spaces, or more than 15%
    // other characters (e.g. punctuation), then don't learn from it
    if(letters < 20 || upper > lower || spaces*100/message.Length > 30 || other*100/message.Length > 15) return null;
    return message;
  }

  /// <summary>Gets the path to a directory relative to the program directory, optionally creating it.</summary>
  static string GetDirectory(string directoryName, bool create)
  {
    string exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
    string directory = Path.Combine(exeDirectory, directoryName);
    if(create && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
    return directory;
  }

  /// <summary>Normalizes a received message by looking for a user name in the message that indicates its addressee (according to
  /// IRC conventions), and placing it in <paramref name="target"/> if found. The message without the address is returned.
  /// </summary>
  static string NormalizeMessage(Channel channel, string line, out string target)
  {
    line = StripControlCharacters(line);

    // if the message was sent to a channel, try to extract the target user from the message according to conventions
    if(channel != null)
    {
      Match m = targetRe.Match(line); // see if the message looks like it might be addressed to somebody
      if(m.Success && channel.Users.ContainsKey(m.Groups["to"].Value)) // if the apparent addressee is in the channel...
      {
        target = m.Groups["to"].Value;
        return m.Groups["message"].Value; // strip his name out from the message
      }
    }

    target = null;
    return line;
  }

  /// <summary>Normalizes text by converting special character (such as curly quotation marks and mdashes) into ASCII.</summary>
  static string NormalizeTextForIrc(string text)
  {
    char[] chars = text.ToCharArray();
    for(int i=0; i<chars.Length; i++) // convert certain characters outside the code page to suitable ones inside
    {
      // TODO: do something about mdash and ndash
      char c = chars[i];
      if(c == '’') chars[i] = '\'';
      else if(c == '“' || c == '”') chars[i] = '"';
    }
    return new string(chars);
  }

  static readonly Regex targetRe = new Regex(@"^(?<to>[a-zA-Z\-\[\]\\`^{}][a-zA-Z0-9\-\[\]\\`^{}]*)[:,]\s+(?<message>.*)$",
                                             RegexOptions.CultureInvariant | RegexOptions.Singleline);
  static string _dataDirectory, _logDirectory;
}

} // namespace HalBot