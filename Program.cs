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
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using AdamMil.Collections;
using AdamMil.IO;
using IrcLib;

/*
 X Add recent news via RSS feeds
 X  Say something about new stories?
 X Respond to messages
 *  Either choose a response based on the quality of the response
 *    Use multiple methods of evaluation?
 *      Least likely (most surprising)
 *      Includes more keywords
 *      Includes better keywords (eg, less likely ones, and/or ones found in recent conversations)
 *  Or choose a response based on a fixed keyword
 *    Use multiple methods of choosing the keyword
 *      Least likely, found in recent conversation, etc.
 X Make random statements while the room is active but the bot is not in conversation already
 X  Comment on recent news
 *  Analyze conversation being had
 X Reconnect after disconnect
 X Rejoin after kicked
 X Keep a desired nickname
 X don't learn from or reply to other bots
 * Taunt kickers?
 */

namespace HalBot
{
  class Program
  {
    static HalBot irc = new HalBot();
    static IdentServer ident = new IdentServer();
    static Thread ircThread = new Thread(ThreadFunc);
    static Thread identThread = new Thread(IdentFunc);
    static bool quitting;

    static string[] GetMatches(MatchCollection mc)
    {
      string[] values = new string[mc.Count];
      for(int i=0; i<values.Length; i++) values[i] = mc[i].Value;
      return values;
    }

    static string Arg(string[] args, int i)
    {
      return i < args.Length ? args[i] : null;
    }

    static string Rest(string[] args, int i)
    {
      if(i < args.Length)
      {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for(; i < args.Length; i++)
        {
          if(sb.Length != 0) sb.Append(' ');
          sb.Append(args[i]);
        }
        return sb.ToString();
      }
      else return null;
    }

    static void Main()
    {
      //ident.Listen();
      //identThread.Start();

      ircThread.Start();
      irc.BabbleTime = 2;
      irc.DesiredNickname = "MuBot";
      irc.Verbose = true;
      irc.Brain.LearnLines(File.OpenText("d:/adammil/code/halbot/foo.trn"), true);
      irc.LoadFeeds(Path.Combine(HalBot.DataDirectory, "feeds.xml"));
      irc.Connect("efnet.xs4all.nl");
      irc.UsersToIgnore.Add("AdamMil");

      while(true)
      {
        while(!Console.KeyAvailable) Thread.Sleep(50);
        Console.Write("> ");
        string line = Console.ReadLine();

        if(line == null)
        {
          lock(irc) irc.Disconnect();
          break;
        }
        else
        {
          Match m = cmdRe.Match(line);
          if(!m.Success) continue;

          string command = m.Groups["cmd"].Value;
          string[] args = GetMatches(paramRe.Matches(m.Groups["args"].Value));

          if(command == "join")
          {
            if(args.Length != 0)
            {
              lock(irc)
              {
                irc.Join(args[0].Split(','), Arg(args, 1), delegate(JoinReply r)
                {
                  Console.WriteLine(r.Error == IrcResponseCodes.None ? "Joined " + r.Channel.Name :
                                                                       "Can't join " + r.Channel.Name + ": " + r.Error);
                });
              }
            }
          }
          else if(command == "part")
          {
            if(args.Length != 0)
            {
              lock(irc) irc.Part(args[0].Split(','));
            }
          }
          else if(command == "quote")
          {
            lock(irc) irc.SendRawCommand(Rest(args, 0));
          }
          else if(command == "reply")
          {
            lock(irc)
            {
              string response = irc.Brain.GetResponse(Rest(args, 0), true, false);
              Console.WriteLine(response ?? "I have nothing to say to that.");
            }
          }
          else if(command == "quit" || command == "exit")
          {
            lock(irc) irc.Disconnect(Rest(args, 0));
            break;
          }
          else
          {
            Console.WriteLine("Unknown command: " + command);
          }
        }
      }

      quitting = true;
      ircThread.Join();
      //identThread.Join();

      irc.Dispose();
    }

    static void ThreadFunc()
    {
      while(!quitting)
      {
        if(irc.CanProcessData)
        {
          while(true)
          {
            lock(irc)
            {
              if(!irc.ProcessData(100)) break;
            }
          }
        }
        else
        {
          Thread.Sleep(100);
        }
      }
    }

    static void IdentFunc()
    {
      while(!quitting)
      {
        ident.ProcessRequests(100);
      }
    }

    static readonly Regex cmdRe = new Regex(@"^\s*(?<cmd>\w+)(?:\s+(?<args>.*?))?\s*$", RegexOptions.Singleline);
    static readonly Regex paramRe = new Regex(@"\S+", RegexOptions.Singleline);
  }
}
