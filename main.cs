using System;
using System.IO;

namespace HalBot
{

sealed class App
{ static void Main()
  { IrcBot bot = new IrcBot();

    if(File.Exists("default.trn"))
    { StreamReader sr = new StreamReader("default.trn");
      Console.WriteLine("Learning from default.trn...");
      bot.Brain.Learn(sr);
      sr.Close();
      Console.WriteLine("Brain initialized.");
    }
    else Console.WriteLine("WARNING: no brain");
    bot.Connect("irc.esper.net", 6667);
    bot.LogFile = "irc.log";
    bot.Nick = "limpu";

    while(true)
    { Console.Write("> ");
      string line = Console.ReadLine();
      if(line==null) break;
      line = line.Trim();
      if(line=="") continue;

      try
      { string[] bits = line.Split(null);
        switch(bits[0].ToLower())
        { case "autolearn":
            if(bits.Length>1) bot.AutoLearn = int.Parse(bits[1]) != 0;
            Console.WriteLine("Autolearn {0}.", bot.AutoLearn ? "enabled" : "disabled");
            break;
          case "autoreply":
            if(bits.Length>1) bot.AutoReply = int.Parse(bits[1]) != 0;
            Console.WriteLine("Autoreply is {0}.", bot.AutoReply ? "enabled" : "disabled");
            break;
          case "chatchance":
            if(bits.Length==1) Console.WriteLine("Chat chance is {0}%", bot.ChatChance);
            else bot.ChatChance = int.Parse(bits[1]);
            break;
          case "clear":
            lock(bot)
              if(bits.Length==1) bot.Messages.Clear();
              else bot.Messages.RemoveAt(int.Parse(bits[1]));
            break;
          case "clearlast":
            lock(bot) if(bot.Messages.Count!=0) bot.Messages.RemoveAt(bot.Messages.Count-1);
            break;
          case "delay":
            if(bits.Length==1) Console.WriteLine("Type delay is {0} ms/char", bot.TypeDelay);
            else bot.TypeDelay = int.Parse(bits[1]);
            break;
          case "flush":
            lock(bot) foreach(IrcBot.Message m in bot.Messages) m.Delay = DateTime.Now;
            break;
          case "ignorantize":
            lock(bot) bot.Brain.Clear();
            break;
          case "learn":
            line = bits.Length>1 ? string.Join(" ", bits, 1, bits.Length-1) : null;
            if(line!=null) bot.Brain.Learn(line, false);
            else
              while((line=Console.ReadLine())!=null)
              { line = line.Trim();
                if(line=="") break;
                bot.Brain.Learn(line, false);
              }
            Console.WriteLine("Learned.");
            break;
          case "learnfile":
            line = bits.Length>1 ? string.Join(" ", bits, 1, bits.Length-1) : Console.ReadLine();
            if(line!=null) line = line.Trim();
            if(line!="")
            { StreamReader sr = new StreamReader(line);
              Console.WriteLine("Learning...");
              bot.Brain.Learn(sr);
              sr.Close();
            }
            Console.WriteLine("Learned.");
            break;
          case "logfile":
            if(bits.Length>1)
            { line = string.Join(" ", bits, 1, bits.Length-1);
              bot.LogFile = line=="off" ? null : line;
            }
            Console.WriteLine("Logging {0}.", bot.LogFile==null ? "is off" : "to "+bot.LogFile);
            break;
          case "join": bot.Join(bits[1]); break;
          case "msgs":
            lock(bot) foreach(IrcBot.Message m in bot.Messages) Console.WriteLine("** To {0}: {1}", m.To, m.Text);
            break;
          case "nick":
            if(bits.Length==1) Console.WriteLine("Nick is '{0}'", bot.Nick);
            else bot.Nick=bits[1];
            break;
          case "order":
            if(bits.Length==1) Console.WriteLine("Order is {0}.", bot.Brain.WindowSize);
            else bot.Brain.WindowSize=int.Parse(bits[1]);
            break;
          case "part": bot.Part(bits[1]); break;
          case "reply":
            line = bits.Length>1 ? string.Join(" ", bits, 1, bits.Length-1) : Console.ReadLine();
            line = bot.Brain.GetResponse(line);
            if(line==null) line = bot.Brain.GetRandomResponse();
            Console.WriteLine(line);
            break;
          case "say":
            line = bits.Length>2 ? string.Join(" ", bits, 2, bits.Length-2) : Console.ReadLine();
            bot.SendDelayedMessage(bits[1], line);
            break;
          case "saynow":
            line = bits.Length>2 ? string.Join(" ", bits, 2, bits.Length-2) : Console.ReadLine();
            bot.SendMessage(bits[1], line);
            break;
          case "quit":
            line = bits.Length>1 ? string.Join(" ", bits, 1, bits.Length-1) : "later!";
            bot.Disconnect(line);
            goto done;
          case "quote":
            line = bits.Length>1 ? string.Join(" ", bits, 1, bits.Length-1) : Console.ReadLine();
            if(line!=null && line!="") bot.SendRaw(line);
            break;
          case "verbose":
            if(bits.Length>1) bot.Verbose = int.Parse(bits[1]) != 0;
            Console.WriteLine("Verbose mode is {0}.", bot.Verbose ? "enabled" : "disabled");
            break;
          default: Console.WriteLine("Unknown command: "+bits[0]); break;
        }
      }
      catch(Exception e) { Console.WriteLine("ERROR {0}: {1}", e.GetType().Name, e.Message); }
    }

    bot.Disconnect("later!");
    done:;
    bot.LogFile = null;
  }
}

} // namespace HalBot
