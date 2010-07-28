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
using IrcLib;

namespace HalBot
{

sealed class HalBot : IrcClient, IDisposable
{
  public HalBot()
  {
    AutoLearn     = true;
    AutoReconnect = true;
    BabbleTime    = 30;
    Brain         = new Brain();
    TypingDelay   = 70;
    RejoinOnKick  = true;
    RssFeeds      = new NonNullCollection<HalBotRssFeed>();
    UsersToIgnore = new HashSet<string>(NameComparer.Instance);
  }

  const int Version = 1, MaxConversationsPerForum = 10, EndConversationAfterMinutes = 15, RemoveForumAfterDays = 7;

  public bool AutoLearn
  {
    get; set;
  }

  /// <summary>Gets or sets whether the bot will try to reconnect when it gets disconnected.</summary>
  public bool AutoReconnect
  {
    get; set;
  }

  /// <summary>Gets or sets the number of minutes between times when the bot will attempt to say something random. Setting this
  /// property to zero disables babbling.
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

  public Brain Brain
  {
    get; private set;
  }

  public string DesiredNickname
  {
    get; set;
  }

  /// <summary>Gets or sets whether the bot will automatically rejoin a channel when kicked.</summary>
  public bool RejoinOnKick
  {
    get; set;
  }

  public NonNullCollection<HalBotRssFeed> RssFeeds
  {
    get; private set;
  }

  /// <summary>Gets or sets the simulated typing delay in milliseconds per character.</summary>
  public int TypingDelay
  {
    get { return _typingDelay; }
    set
    {
      if(value < 0) throw new ArgumentOutOfRangeException();
      _typingDelay = value;
    }
  }

  public HashSet<string> UsersToIgnore
  {
    get; private set;
  }

  public bool Verbose
  {
    get; set;
  }

  public void Dispose()
  {
    foreach(RssFeed feed in RssFeeds) feed.Dispose();
  }

  public void LoadFeeds(string xmlFeedFile)
  {
    XmlDocument doc = new XmlDocument();
    XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
    ns.AddNamespace("f", "http://adammil.net/HalBot/feeds.xsd");
    doc.Load(xmlFeedFile);
    foreach(XmlElement feed in doc.DocumentElement.SelectNodes("f:feed", ns)) RssFeeds.Add(new HalBotXmlRssFeed(Brain, feed));
  }

  protected override void OnConnect()
  {
    base.OnConnect();

    SetNickname(DesiredNickname);

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
    base.OnCTCPMessage(from, to, command, args);

    if(AreNamesEqual(to, Nickname))
    {
      if(string.Equals(command, "VERSION", StringComparison.OrdinalIgnoreCase))
      {
        SendCTCPNotice(from, "VERSION Hal " + (9000 + Version).ToString(CultureInfo.InvariantCulture));
      }
    }
  }

  protected override void OnDisconnect(bool intentional)
  {
    List<string> channelsToRejoin = intentional ? null : JoinedChannels.Keys.ToList();

    base.OnDisconnect(intentional);
    
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

  protected override void OnKick(string kicker, string kicked, string channelName, string kickText)
  {
    base.OnKick(kicker, kicked, channelName, kickText);
    Console.WriteLine("KICK: {0} by {1} from {2}: {3}", kicked, kicker, channelName, kickText);

    // if the bot was kicked and it should try to rejoin, do so 20 seconds later
    if(AreNamesEqual(kicked, Nickname) && RejoinOnKick) AddDelayedAction(20000, delegate { Join(channelName); });
  }

  protected override void OnMessage(string from, string to, string text)
  {
    base.OnMessage(from, to, text);

    // if it was sent to a channel, then the forum is the channel. otherwise, it's the private conversation with the sender
    Channel channel = IsChannelName(to) ? EnsureChannelKnown(to) : null;
    string forum = channel != null ? to : from;

    string target, message = NormalizeMessage(channel, text, out target);

    User fromUser = EnsureUserKnown(from), toUser;
    if(channel != null)
    {
      toUser = target != null && channel.Users.ContainsKey(target) ? EnsureUserKnown(target) : null;
    }
    else toUser = EnsureUserKnown(to);

    // add the statement to the most recent conversation in this forum, or a new conversation if that one has ended
    List<ConversationInfo> recentConversations;
    if(!forums.TryGetValue(forum, out recentConversations)) forums[forum] = recentConversations = new List<ConversationInfo>();

    ConversationInfo conversation;
    if(recentConversations.Count != 0 && recentConversations.Last().Ended == null)
    {
      conversation = recentConversations.Last();
    }
    else
    {
      conversation = new ConversationInfo(Brain, forum);
      recentConversations.Add(conversation);
      if(recentConversations.Count > MaxConversationsPerForum) recentConversations.RemoveAt(0);
    }

    conversation.AddStatement(fromUser, toUser, message);

    if(!UsersToIgnore.Contains(fromUser.Name))
    {
      if(AutoLearn) Brain.LearnLine(message, true);

      string reply = null;
      int replyChance = 0;
      if(toUser == User) // if the message was sent directly to us (possibly in a channel)...
      {
        replyChance = 100;
        conversation.AddressedMeAt = DateTime.Now;
        conversation.WhoAddressedMe.Add(fromUser);
      }
      else if(toUser == null) // otherwise, if the message was sent to a channel with no explicit addressee...
      {
        replyChance = 4; // the base reply chance // TODO: make this configurable

        // if I was addressed by the speaker in this conversation, increase the reply chance...
        if(conversation.WhoAddressedMe.Contains(fromUser))
        {
          TimeSpan timeSinceIWasAddressed = DateTime.Now - conversation.AddressedMeAt.Value;
          // decide how likely we are to respond based on the characteristics of the message text and timing. generally we'll be
          // more likely to respond the more recently we were addressed, but very quick replies are likely things like "haha" and
          // don't need a response (hence the low chance for messages arriving in less than 5 seconds)
          if(timeSinceIWasAddressed.TotalSeconds <= 5) replyChance = 10;
          else if(timeSinceIWasAddressed.TotalSeconds <= 10) replyChance = 90;
          else if(timeSinceIWasAddressed.TotalSeconds <= 30) replyChance = 75;
          else if(timeSinceIWasAddressed.TotalSeconds <= 60) replyChance = 50;
          else if(timeSinceIWasAddressed.TotalMinutes <= 2) replyChance = 20;

          // if there's only one other person in the conversation, we can assume they're talking to us
          if(conversation.Participants.Count == 1) conversation.AddressedMeAt = DateTime.Now;
        }

        string[] words = Brain.SplitWords(message, true);
        if(words.Length == 0)
        {
          replyChance = 0;
        }
        // if the message contains a greeting and our nickname, assume it's directed at us and always reply
        else if(words.Any(word => Brain.IsGreeting(word)) && words.Any(word => AreNamesEqual(word, User.Name)))
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
        reply = conversation.Brain.GetResponse(message, 5, true, true, delegate(Brain.Utterance u)
        {
          float value = 0.5f; // TODO: a better evaluation function would be nice

          if(string.Equals(u.Text, lastReplyText, StringComparison.Ordinal)) value = 0; // don't repeat ourselves
          else if(string.Equals(u.Text, message, StringComparison.Ordinal)) value *= 0.1f; // try not to repeat the user

          if(u.Text.Length > 300) value *= 0.25f; // penalize long messages
          else if(u.Text.Length > 200) value *= 0.5f;

          return value;
        });

        if(reply == null) reply = conversation.Brain.GetRandomUtterance(true);

        if(reply != null)
        {
          lastReplyText = reply;
          reply = NormalizeTextForIrc(reply);
          AddDelayedAction(CalculateTypingDelay(reply), delegate
          {
            if(channel == null) SendMessage(from, reply);
            else SendMessage(to, from + ": " + reply);
          });
        }
      }
    }
  }

  protected override void OnNick(string oldNick, string newNick)
  {
    base.OnNick(oldNick, newNick);

    foreach(List<ConversationInfo> recentConversations in forums.Values)
    {
      foreach(ConversationInfo conversation in recentConversations) conversation.Participants.OnNameChanged(oldNick, newNick);
    }

    forums.OnNameChanged(oldNick, newNick);
  }

  protected override void OnRawInput(string line)
  {
    if(Verbose) Console.WriteLine("<<< " + line);
    base.OnRawInput(line);
  }

  protected override void OnRawOutput(string line)
  {
    if(Verbose) Console.WriteLine(">>> " + line);
    base.OnRawOutput(line);
  }

  internal static string DataDirectory
  {
    get
    {
      if(_dataDirectory == null) _dataDirectory = GetDirectory("data", false);
      return _dataDirectory;
    }
  }

  internal static string LogDirectory
  {
    get
    {
      if(_logDirectory == null || !Directory.Exists(_logDirectory)) _logDirectory = GetDirectory("logs", true);
      return _logDirectory;
    }
  }

  #region ConversationInfo
  sealed class ConversationInfo
  {
    const int StatementsToKeep = 25;

    public ConversationInfo(Brain parentBrain, string forum)
    {
      Brain   = new Brain(parentBrain);
      Forum   = forum;
      Started = LastActivity = DateTime.Now;
      WhoAddressedMe = new HashSet<User>(UserComparer.Instance);
    }

    public void AddStatement(User from, User to, string message)
    {
      if(LastStatements.Count == LastStatements.Capacity) LastStatements.RemoveFirst();
      LastStatements.Add(new Statement(from, to, message));
      Participants[from.Name] = from;
      if(to != null) Participants[to.Name] = to;

      LastActivity = DateTime.Now;
      Ended        = null;

      Brain.LearnLine(message, true);
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

  void AgeConversations()
  {
    // end conversations that have had no activity for some number of minutes, and remove forums that have had no
    // activity for some number of days
    List<string> deadForums = new List<string>();
    DateTime now = DateTime.Now;
    foreach(var pair in forums.EnumeratePairs())
    {
      bool forumHasRecentConversations = false;
      foreach(ConversationInfo conversation in pair.Value)
      {
        TimeSpan age = now - conversation.LastActivity;
        if(age.TotalDays < RemoveForumAfterDays)
        {
          forumHasRecentConversations = true;
          if(age.TotalMinutes >= EndConversationAfterMinutes)
          {
            if(Verbose) Console.WriteLine("Ending " + conversation.ToString());
            conversation.Ended = now;
          }
        }
      }

      if(!forumHasRecentConversations) deadForums.Add(pair.Key);
    }

    foreach(string forum in deadForums)
    {
      if(Verbose) Console.WriteLine("Removing dead forum " + forum);
      forums.Remove(forum);
    }
  }

  void Babble()
  {
    // if we haven't babbled for half an hour and we're in at least one channel...
    if(BabbleTime != 0 && (DateTime.Now - lastBabbleTime).TotalMinutes >= BabbleTime && JoinedChannels.Count != 0)
    {
      // choose a random channel to talk in
      int randomChannel = rand.Next(JoinedChannels.Count);
      Channel channel = JoinedChannels.Values.ToArray()[randomChannel]; // TODO: it'd be nice if it supported direct indexing...

      // if any conversation has happened in the channel in the past 5 minutes...
      List<ConversationInfo> conversations;
      if(forums.TryGetValue(channel.Name, out conversations) && conversations.Count != 0 &&
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
          AddDelayedAction(CalculateTypingDelay(message), delegate { SendMessage(channel.Name, message); });
          lastBabbleTime = DateTime.Now;
        }
      }
    }
  }

  int CalculateTypingDelay(string message)
  {
    return message.Length * TypingDelay;
  }

  void KillTimerThread()
  {
    if(timerThread != null)
    {
      quitEvent.Set();
      timerEvent.Set();
      if(!timerThread.Join(30000)) timerThread.Abort();
      timerThread = null;
    }
  }

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
          while(delayedActions.Count != 0 && delayedActions.Peek().Time < DateTime.Now)
          {
            try { delayedActions.Dequeue().Action(); }
            catch { } // TODO: log the error
          }
        }
      }

      if((DateTime.Now - lastSlowTimerEvents).TotalSeconds >= 30) // do the slow events every 30 seconds
      {
        lastSlowTimerEvents = DateTime.Now;

        UpdateRssFeeds();

        lock(this)
        {
          AgeConversations();
          TryToGetDesiredNick();
          Babble();
        }
      }
    }
  }

  void TryToGetDesiredNick()
  {
    if(!string.Equals(Nickname, DesiredNickname, StringComparison.Ordinal)) SetNickname(DesiredNickname);
  }

  void UpdateRssFeeds()
  {
    // copy the feeds so they don't get changed by another thread while we're updating them
    List<HalBotRssFeed> feeds;
    lock(this) feeds = new List<HalBotRssFeed>(RssFeeds);

    foreach(RssFeed feed in feeds)
    {
      if(feed.ShouldUpdate) feed.Update();
    }
  }

  readonly Random rand = new Random();
  readonly IrcNameDictionary<List<ConversationInfo>> forums = new IrcNameDictionary<List<ConversationInfo>>();
  readonly PriorityQueue<DelayedAction> delayedActions = new PriorityQueue<DelayedAction>();
  readonly AutoResetEvent timerEvent = new AutoResetEvent(false);
  readonly ManualResetEvent quitEvent = new ManualResetEvent(false);
  Thread timerThread;
  string lastReplyText;
  DateTime lastSlowTimerEvents, lastBabbleTime = DateTime.Now; // don't babble immediately
  int _babbleTime, _typingDelay;

  static string GetDirectory(string directoryName, bool create)
  {
    string exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
    string directory = Path.Combine(exeDirectory, directoryName);
    if(!Directory.Exists(directory)) Directory.CreateDirectory(directory);
    return directory;
  }

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