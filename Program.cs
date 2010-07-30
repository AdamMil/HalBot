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
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using AdamMil.IO;
using AdamMil.Utilities;
using IrcLib;

using System.Linq;

namespace HalBot
{
  class Program
  {
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

    static void Main()
    {
      /*using(StreamReader sr = new StreamReader("d:/adammil/code/halbot/bin/debug/eedata/armastusesaal.txt"))
      {
        System.Collections.Generic.Dictionary<string,string> corrections = new System.Collections.Generic.Dictionary<string,string>();
        sr.ProcessNonEmptyLines(line =>
        {
          string[] words = line.ToLower().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
          foreach(string word in words)
          {
            if(word.ToCharArray().Any(c => char.IsLetter(c)) && !word.ToCharArray().Any(c => char.IsDigit(c) && c != '2' && c != '6' && c != '8'))
            {
              string correction = word.Replace('6', 'õ').Replace('2', 'ä').Replace('y', 'ü').Replace('8', 'ö').Replace("x", "ks").Replace('w', 'v');
              if(correction != word) corrections[word] = correction;
            }
          }
        });
        using(StreamWriter sw = new StreamWriter("d:/adammil/code/halbot/bin/debug/eedata/fixes.txt", true))
        {
          var orderedKeys = from key in corrections.Keys orderby key select key;
          foreach(string key in orderedKeys) sw.WriteLine(key + " " + corrections[key]);
        }
      }*/

      Initialize();

      while(true)
      {
        while(!Console.KeyAvailable) Thread.Sleep(50);
        Console.Write("> ");
        string line = Console.ReadLine();
        if(line == null || !ProcessCommand(line)) break;
      }

      lock(bot) bot.Disconnect();

      quitting = true;
      if(botThread != null) botThread.Join();
      if(ident != null) ident.Shutdown();
      if(identThread != null) identThread.Join();
      bot.Dispose();
    }

    const string PropertyError = "ERROR: Unable to initialize property {0} with value \"{1}\". {2}";

    static string Arg(string[] args, int i)
    {
      return Arg(args, i, null);
    }

    static string Arg(string[] args, int i, string defaultValue)
    {
      return i < args.Length ? args[i] : defaultValue;
    }

    static void BotFunc()
    {
      while(!quitting)
      {
        if(bot.CanProcessData)
        {
          while(true)
          {
            lock(bot)
            {
              if(!bot.ProcessData(100)) break;
            }
          }
        }
        else
        {
          Thread.Sleep(100);
        }
      }
    }

    static bool GetBoolSetting(string propertyName, bool defaultValue)
    {
      string setting = ConfigurationManager.AppSettings[propertyName];
      if(!string.IsNullOrEmpty(setting))
      {
        try { return ParseBool(setting); }
        catch(Exception ex) { Console.WriteLine(PropertyError, propertyName, setting, ex.Message); }
      }
      return defaultValue;
    }

    /// <summary>Gets the path to a directory relative to the program directory, optionally creating it.</summary>
    static string GetDirectory(string directoryName, bool create)
    {
      string exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
      string directory = Path.Combine(exeDirectory, directoryName);
      if(create && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
      return directory;
    }

    static string[] GetMatches(MatchCollection mc)
    {
      string[] values = new string[mc.Count];
      for(int i=0; i<values.Length; i++) values[i] = mc[i].Value;
      return values;
    }

    static void IdentFunc()
    {
      while(!quitting)
      {
        ident.ProcessRequests(100);
      }
    }

    static void Initialize()
    {
      Console.WriteLine("Initializing...");
      foreach(PropertyInfo property in bot.GetType().GetProperties())
      {
        if(property.CanWrite && property.GetSetMethod() != null)
        {
          object value = ConfigurationManager.AppSettings[property.Name];
          if(value != null)
          {
            try
            {
              property.GetSetMethod().Invoke(bot, new object[] { TypeUtility.ChangeType(property.PropertyType, value) });
            }
            catch(Exception ex)
            {
              Console.WriteLine(PropertyError, property.Name, value, ex.Message);
            }
          }
        }
      }

      correctSpelling = GetBoolSetting("CorrectSpelling", correctSpelling);
      enableIdent     = GetBoolSetting("EnableIdent", enableIdent);

      string learnFile = ConfigurationManager.AppSettings["LearnFile"];
      if(!string.IsNullOrEmpty(learnFile))
      {
        if(!Path.IsPathRooted(learnFile)) learnFile = Path.Combine(Program.DataDirectory, learnFile);
        if(File.Exists(learnFile))
        {
          try
          {
            using(StreamReader reader = new StreamReader(learnFile)) LearnFile(reader);
          }
          catch(Exception ex)
          {
            Console.WriteLine("ERROR: Unable to read file {0}. {1}", learnFile, ex.Message);
          }
        }
        else
        {
          Console.WriteLine("WARNING: File {0} doesn't exist.", learnFile);
        }
      }

      string rssFeeds = ConfigurationManager.AppSettings["RssFeeds"];
      if(!string.IsNullOrEmpty(rssFeeds))
      {
        if(!Path.IsPathRooted(rssFeeds)) rssFeeds = Path.Combine(Program.DataDirectory, rssFeeds);
        if(File.Exists(rssFeeds))
        {
          try { bot.LoadRssFeeds(rssFeeds); }
          catch(Exception ex) { Console.WriteLine("ERROR: Unable to read file {0}. {1}", rssFeeds, ex.Message); }
        }
        else
        {
          Console.WriteLine("WARNING: File {0} doesn't exist.", rssFeeds);
        }
      }

      
      if(enableIdent)
      {
        try
        {
          Console.Write("Starting ident service...");
          ident = new IdentServer();
          ident.Listen();
          identThread = new Thread(IdentFunc);
          identThread.Start();
          Console.WriteLine(" started.");
        }
        catch(Exception ex)
        {
          Console.WriteLine("WARNING: Unable to start ident service. " + ex.Message);
        }
      }

      botThread = new Thread(BotFunc);
      botThread.Start();

      string server = ConfigurationManager.AppSettings["Server"], port = ConfigurationManager.AppSettings["Port"];
      if(!string.IsNullOrEmpty(server))
      {
        int portNumber;
        try
        {
          portNumber = string.IsNullOrEmpty(port) ? 6667 : int.Parse(port);

          try
          {
            Console.WriteLine("Connecting...");
            bot.Connect(server, portNumber);
            Console.WriteLine("Connected.");
          }
          catch(Exception ex)
          {
            Console.WriteLine("ERROR: Unable to connect to server {0}:{1}. {2}", server, portNumber, ex.Message);
          }
        }
        catch(Exception ex)
        {
          Console.WriteLine(PropertyError, "Port", port, ex.Message);
        }
      }

      if(bot.IsConnected)
      {
        string channels = ConfigurationManager.AppSettings["InitialChannels"];
        if(!string.IsNullOrEmpty(channels)) bot.Join(Split(channels), null, null);
      }
    }

    static string Join(string[] args)
    {
      return Join(args, 0);
    }

    static string Join(string[] args, int start)
    {
      if(start < args.Length)
      {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for(; start<args.Length; start++)
        {
          if(sb.Length != 0) sb.Append(' ');
          sb.Append(args[start]);
        }
        return sb.ToString();
      }
      else
      {
        return null;
      }
    }

    static void LearnFile(StreamReader reader)
    {
      reader.ProcessNonEmptyLines(line =>
      {
        if(line[0] != '#') bot.Brain.LearnLine(line, correctSpelling);
      });
    }

    static bool ParseBool(string str)
    {
      int intValue;

      str = str.ToLowerInvariant();
      if(string.Equals(str, "true", StringComparison.Ordinal) || str.Equals("on", StringComparison.Ordinal)) return true;
      else if(string.Equals(str, "false", StringComparison.Ordinal) || str.Equals("off", StringComparison.Ordinal)) return false;
      else if(int.TryParse(str, out intValue)) return intValue != 0;
      else return bool.Parse(str);
    }

    static bool ProcessCommand(string line)
    {
      Match m = cmdRe.Match(line);
      if(!m.Success) return true;

      string command = m.Groups["cmd"].Value.ToLowerInvariant(), argText = m.Groups["args"].Value;
      string[] args = GetMatches(paramRe.Matches(argText));

      try
      {
        // handle commands that require user input outside of a lock
        if(command == "learn")
        {
          string text;
          if(args.Length != 0)
          {
            text = argText;
          }
          else
          {
            Console.Write("Enter text to learn: ");
            text = Console.ReadLine();
          }

          if(!string.IsNullOrEmpty(text))
          {
            lock(bot) bot.Brain.LearnLine(text, false);
            Console.WriteLine("Learned.");
          }
        }
        else if(command == "learnfile")
        {
          string filename;
          if(args.Length != 0)
          {
            filename = argText;
          }
          else
          {
            Console.Write("Enter filename: ");
            filename = Console.ReadLine();
          }

          if(!string.IsNullOrEmpty(filename))
          {
            using(StreamReader reader = new StreamReader(filename))
            {
              Console.Write("Learning...");
              lock(bot) LearnFile(reader);
              Console.WriteLine(" done.");
            }
          }
        }
        else // then handle quick commands inside of a lock so we don't have to lock during every command
        {
          lock(bot)
          {
            switch(command.ToLowerInvariant())
            {
              case "autolearn":
                if(args.Length != 0) bot.AutoLearn = ParseBool(args[0]);
                WriteBoolOption("Autolearn", bot.AutoLearn);
                break;
              case "babble":
                bot.Babble(Arg(args, 0), true);
                break;
              case "babbletime":
                if(args.Length != 0) bot.BabbleTime = int.Parse(args[0]);
                Console.WriteLine("Babble time is {0} minutes.", bot.BabbleTime);
                break;
              case "convbrains":
                if(args.Length != 0) bot.UseConversationBrains = ParseBool(args[0]);
                WriteBoolOption("UseConversationBrains", bot.UseConversationBrains);
                break;
              case "correctspelling":
                if(args.Length != 0) correctSpelling = ParseBool(args[0]);
                WriteBoolOption("Spellcheck", correctSpelling);
                break;
              case "delay":
                if(args.Length != 0) bot.TypingDelay = int.Parse(args[0]);
                Console.WriteLine("Typing delay is {0} ms/character.", bot.TypingDelay);
                break;
              case "exit": case "quit":
                bot.Disconnect(Join(args));
                return false;
              case "forget":
                bot.Brain.Clear();
                Console.WriteLine("Brain wiped.");
                break;
              case "greetchance":
                if(args.Length != 0) bot.GreetChance = int.Parse(args[0]);
                Console.WriteLine("Greet chance is {0}%.", bot.GreetChance);
                break;
              case "ignore":
                if(args.Length != 0)
                {
                  foreach(string user in Split(args[0])) bot.UsersToIgnore.Add(user);
                }
                if(bot.UsersToIgnore.Count == 0) Console.WriteLine("Not ignoring anybody.");
                else Console.WriteLine("Ignoring {0}.", StringUtility.Join(", ", bot.UsersToIgnore));
                break;
              case "interject":
                if(args.Length != 0) bot.Interject = ParseBool(args[0]);
                WriteBoolOption("Interject", bot.Interject);
                break;
              case "interjectchance":
                if(args.Length != 0) bot.InterjectChance = int.Parse(args[0]);
                Console.WriteLine("Interject chance is {0}%.", bot.InterjectChance);
                break;
              case "join":
                if(args.Length != 0)
                {
                  bot.Join(Split(args[0]), Arg(args, 1), delegate(JoinReply r)
                  {
                    if(r.Error != IrcResponseCodes.None)
                    {
                      Console.WriteLine("Can't join {0}: {1}", r.Channel.Name, r.Error.ToString());
                    }
                  });
                }
                else
                {
                  Console.WriteLine("ERROR: Format is 'join <channel>[,<channel>,...] [<password>].");
                }
                break;
              case "logfile":
                if(args.Length != 0) bot.LogFile = argText;
                if(string.IsNullOrEmpty(bot.LogFile)) Console.WriteLine("Logging disabled.");
                else Console.WriteLine("Logging to {0}.", bot.LogFile);
                break;
              case "lograw":
                if(args.Length != 0) bot.LogRawTraffic = ParseBool(args[0]);
                WriteBoolOption("LogRawTraffic", bot.LogRawTraffic);
                break;
              case "nick":
                if(args.Length != 0)
                {
                  bot.SetNickname(args[0]);
                  bot.DesiredNickname = args[0];
                }
                Console.WriteLine("Desired nick is {0}.", bot.DesiredNickname);
                break;
              case "order":
                if(args.Length != 0) bot.Brain.MarkovOrder = int.Parse(args[0]);
                Console.WriteLine("Markov order is {0}.", bot.Brain.MarkovOrder);
                break;
              case "part":
                if(args.Length != 0) bot.Part(Split(args[0]));
                else Console.WriteLine("ERROR: Format is 'part [<channel>,<channel>,...]'.");
                break;
              case "quote":
                if(args.Length != 0) bot.SendRawCommand(Join(args));
                break;
              case "reply":
                if(args.Length != 0) bot.Reply = ParseBool(args[0]);
                WriteBoolOption("Reply", bot.Reply);
                break;
              case "respond":
                Console.WriteLine(bot.Brain.GetResponse(Join(args), true, false) ?? "I have nothing to say to that.");
                break;
              case "say":
                if(args.Length >= 2) bot.SendDelayedMessage(args[0], Join(args, 1));
                else Console.WriteLine("ERROR: Format is 'say <destination> <message>'.");
                break;
              case "saynow":
                if(args.Length >= 2) bot.SendMessage(args[0], Join(args, 1));
                else Console.WriteLine("ERROR: Format is 'saynow <destination> <message>'.");
                break;
              case "server":
                if(args.Length != 0)
                {
                  Console.WriteLine("Connecting...");
                  bot.Connect(args[0], int.Parse(Arg(args, 1, "6667")));
                  Thread.Sleep(1000); // wait for the MOTD to scroll by
                }
                if(bot.RemoteEndPoint == null || !bot.IsConnected) Console.WriteLine("Not connected.");
                else Console.WriteLine("Connected to {0}.", bot.RemoteEndPoint);
                break;
              case "stopignoring":
                if(args.Length != 0)
                {
                  foreach(string user in Split(args[0])) bot.UsersToIgnore.Remove(user);
                }
                else
                {
                  bot.UsersToIgnore.Clear();
                }
                if(bot.UsersToIgnore.Count == 0) Console.WriteLine("Not ignoring anybody.");
                else Console.WriteLine("Ignoring {0}.", StringUtility.Join(", ", bot.UsersToIgnore));
                break;
              case "updaterss":
                Console.Write("Updating RSS feeds...");
                bot.UpdateRssFeeds();
                Console.WriteLine(" done.");
                break;
              case "verbose":
                if(args.Length != 0) bot.Verbose = ParseBool(args[0]);
                WriteBoolOption("Verbose mode", bot.Verbose);
                break;
              default:
                Console.WriteLine("ERROR: Unknown command \"{0}\".", command);
                break;
            }
          }
        }
      }
      catch(Exception ex)
      {
        Console.WriteLine("ERROR: {0}: {1}", ex.GetType().Name, ex.Message);
      }

      return true;
    }

    static string[] Split(string str)
    {
      return str.Split(',', s => s.Trim(), StringSplitOptions.RemoveEmptyEntries);
    }

    static void WriteBoolOption(string name, bool value)
    {
      Console.WriteLine("{0} is {1}.", name, value ? "enabled" : "disabled");
    }

    static readonly HalBot bot = new HalBot();
    static IdentServer ident;
    static Thread botThread, identThread;
    static bool correctSpelling=true, enableIdent=true, quitting;

    static readonly Regex cmdRe = new Regex(@"^\s*(?<cmd>\w+)(?:\s+(?<args>.*?))?\s*$", RegexOptions.Singleline);
    static readonly Regex paramRe = new Regex(@"\S+", RegexOptions.Singleline);
    static string _dataDirectory, _logDirectory;
  }
}
