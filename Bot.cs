using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HalBot
{

// TODO: limpu: preserve but collapse whitespace (?)
public class IrcBot
{ public bool Connected { get { return sock!=null && sock.Connected; } }

  public class Message
  { public Message(string to, string text) { To=to; Text=text; }
    public string To, Text;
    public object Delay;
  }

  public Brain Brain { get { return brain; } }
  public string Ident { get { return ident; } set { ident=value; } }
  public ArrayList Messages { get { return messages; } }
  public int TypeDelay { get { return mpc; } set { mpc=value; } }

  public string LogFile
  { get { return logFile; }
    set
    { if(value==logFile) return;
      lock(this)
      { if(log!=null) { log.Close(); log=null; }
        if(value!=null && value!="")
        { log = new System.IO.StreamWriter(value, true);
          logFile = value;
        }
        else logFile=null;
      }
    }
  }

  public int ChatChance=4;
  public bool AutoReply=true, Verbose=false, AutoLearn=false;

  public string Nick
  { get { return nick; }
    set
    { if(Connected) SendRaw("NICK "+value);
      else nick=value;
    }
  }

  public bool UseIdentd
  { get { return identd!=null; }
    set
    { if(UseIdentd==value) return;
      if(value)
      { identd = new TcpListener(IPAddress.Any, 113);
        identd.Start();
      }
      else
      { identd.Stop();
        identd=null;
      }
    }
  }

  public void AddToLog(string format, params object[] args) { AddToLog(string.Format(format, args)); }
  public void AddToLog(string line)
  { if(log!=null)
    { log.WriteLine(line);
      log.Flush();
    }
  }

  public void Connect(string server, int port)
  { if(Connected) Disconnect();
    try
    { sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
      sock.Connect(new IPEndPoint(Dns.GetHostEntry(server).AddressList[0], port));

      if(nick==null || nick=="") nick = GetRandomNick();

      thread = new Thread(new ThreadStart(CheckSocket));
      thread.Start();

      SendRaw("NICK "+nick);
      SendRaw("USER huzzah B C :Hello :-)");

      OnConnect();
    }
    catch(Exception e)
    { Disconnect();
      OnError(e);
    }
  }

  public void Disconnect() { Disconnect(""); }
  public void Disconnect(string reason)
  { lock(this)
    { quitting = true;
      if(Connected)
      { SendRaw(reason==null || reason=="" ? "QUIT" : "QUIT :"+reason);
        sock.Shutdown(SocketShutdown.Send);
        sock.Close();
      }
      if(thread!=null && !thread.Join(1000)) thread.Abort();
      thread=null; sock=null;
    }
  }

  public void Join(string channel) { SendRaw("JOIN "+channel, channel); }
  public void Part(string channel) { SendRaw("PART "+channel, channel); }

  public void SendDelayedMessage(string to, string text)
  { messages.Add(new Message(to, text));
  }

  public void SendMessage(string to, string text)
  { string logtext = string.Format("{0} -> {1}: {2}", nick, to, text);
    if(!Verbose) Console.WriteLine(logtext);
    AddToLog(logtext);
    SendRaw("PRIVMSG {0} :{1}", to, text);
  }

  public void SendRaw(string format, params object[] args) { SendRaw(string.Format(format, args)); }

  public void SendRaw(string text)
  { try
    { lock(this)
      { if(!Connected) throw new InvalidOperationException("SendRaw: Not connected!");
        SendArray(Encoding.ASCII.GetBytes(text), sock);
        SendArray(crlf, sock);
      }
      OnRawData(text, false);
    }
    catch(Exception e) { OnError(e); }
  }

  protected void AddAction(string rawCommand, DateTime sendTime)
  { lock(this) actions.Add(new Action(rawCommand, sendTime));
  }

  protected bool IsChannel(string str)
  { if(str==null || str.Length<=1) return false;
    return str[0]=='#' || str[0]=='&';
  }

  protected virtual void OnConnect()
  { Console.WriteLine("CONNECTED");
  }

  protected virtual void OnCTCP(string from, string to, string command, string text)
  { if(Verbose)
    { text = Strip(text);
      Console.WriteLine("CTCP {0}: {1} -> {2}: {3}", command, from, to, text);
    }
  }

  protected virtual void OnDisconnect()
  { Console.WriteLine("DISCONNECTED");
    Disconnect();
  }

  protected virtual void OnError(Exception e)
  { string logtext = string.Format("{0} ERROR {1}: {2}\n{3}",
                                   DateTime.Now.ToShortTimeString(), e.GetType().Name, e.Message, e);
    Console.WriteLine(logtext);
    if(log!=null) log.WriteLine(logtext);
  }

  protected virtual void OnKick(string kicker, string kicked, string channel, string kickText)
  { Console.WriteLine("KICK: {0} by {1} from {2}: {3}", kicked, kicker, channel, kickText);
    AddAction("JOIN "+channel, DateTime.Now.AddSeconds(20));
    string neener = "PRIVMSG "+channel+" :"+'\x01'+"ACTION ";
    switch(rand.Next(4))
    { case 0: neener += "sticks out his tongue at"+kicker; break;
      case 1: neener += "moons "+kicker; break;
      case 2: neener += "gives "+kicker+" the raspberry"; break;
      case 3: neener += "prays to the coon-god, asking him to condemn "+kicker+" to a life surrounded by "+(rand.Next(140)+5).ToString()+" virginal virgins"; break;
    }
    AddAction(neener+'\x01', DateTime.Now.AddSeconds(25));
  }

  protected virtual void OnMessage(string from, string to, string text)
  { bool privmsg = NickEquals(to, nick);
    bool tome = privmsg || // to me or "NICK[,:] blah"
                text.StartsWith(nick) && text.Length>nick.Length && char.IsPunctuation(text[nick.Length]);
    bool aboutme = text.IndexOf(nick)!=-1;

    text = Strip(text);
    if(Verbose || tome || log!=null)
    { string logtext = string.Format("MSG: {0} -> {1}: {2}", from, to, text);
      if(Verbose || tome) Console.WriteLine(logtext);
      AddToLog(logtext);
    }

    if(AutoReply && !NickEquals(from, nick) &&
       (tome || aboutme && rand.Next(100)<25 || rand.Next(100)>=100-ChatChance) && !badinput.IsMatch(text))
    { if(!tome) Console.WriteLine("Responding to {0}{1}", from, IsChannel(to) ? " on "+to : "", text);
      string resp = Brain.GetResponse(inputstrip.Replace(text, ""));
      if(resp==null) resp = Brain.GetRandomResponse();
      if(resp!=null)
      { if(!privmsg) resp = from.ToLower()+": "+resp;
        to = IsChannel(to) ? to : from;
        SendDelayedMessage(to, resp);
        Console.WriteLine("I'll say: "+resp);
      }
    }
    if(AutoLearn)
    { string learn = !tome && aboutme ? null : FilterInput(text); // don't learn from people just talking about me
      AddToLog("<learn:{0}> {1}", learn==null ? "rejected" : "accepted", learn==null ? text : learn);
      if(learn!=null) Brain.Learn(learn, true);
    }
  }

  protected virtual void OnRawData(string line, bool inbound)
  { if(Verbose) Console.WriteLine("{0} {1}", inbound ? "<<<" : ">>>", line);
    if(!inbound) return;

    Match m = parsere.Match(line);
    if(!m.Success) return;

    string prefix  = m.Groups["prefix"].Value;
    string command = m.Groups["command"].Value;
    string[] parms;
    { MatchCollection ms = parmsre.Matches(m.Groups["params"].Value);
      parms = new string[ms.Count];
      for(int i=0; i<ms.Count; i++) parms[i] = ms[i].Value[0]==':' ? ms[i].Value.Substring(1) : ms[i].Value;
    }

    if(char.IsDigit(command[0]))
      switch(int.Parse(command))
      { case 433: Nick=nick=GetRandomNick(); break;
      }
    else switch(command.ToUpper())
    { case "KICK":
      { string by = nickre.Match(prefix).Groups["nick"].Value;
        OnKick(by, parms[1], parms[0], parms.Length>2 ? string.Join("", parms, 2, parms.Length-2) : "");
        break;
      }

      case "NICK":
      { string from = nickre.Match(prefix).Groups["nick"].Value;
        if(NickCompare(from, nick)==0)
        { nick=parms[0];
          Console.WriteLine("NICK: "+nick);
        }
        break;
      }

      case "PING": SendRaw("PONG {0}", parms[0]); break;

      case "PRIVMSG":
      { string from = nickre.Match(prefix).Groups["nick"].Value;
        string message = parms.Length==2 ? parms[1] : string.Join("", parms, 1, parms.Length-1);

        if(message[0]=='\x01')
        { string cmd;
          int index = message.IndexOf(' ');
          if(index==-1)
          { cmd = message.Substring(1, message.Length-2);
            message = "";
          }
          else
          { cmd = message.Substring(1, index-1);
            message = message.Substring(index+1, message.Length-index-2);
          }
          foreach(string to in parms[0].Split(',')) OnCTCP(from, to, cmd, message);
        }
        else foreach(string to in parms[0].Split(',')) OnMessage(from, to, message);
        break;
      }
    }
  }
  
  class Action
  { public Action(string raw, DateTime sendTime) { RawText=raw; Time=sendTime; }
    public string RawText;
    public DateTime Time;
  }

  void CheckSocket()
  { StringBuilder buffer = new StringBuilder();

    while(!quitting)
    { if(sock.Poll(250000, SelectMode.SelectRead))
        lock(this)
        { byte[] data = new byte[512];
          while(sock.Available!=0)
          { int read = sock.Receive(data, 0, data.Length, SocketFlags.None);
            if(read==0) break;
            int length=buffer.Length;
            buffer.Append(Encoding.ASCII.GetString(data, 0, read));
            for(int i=length; i<buffer.Length; i++)
              if(buffer[i]=='\n')
              { length = i!=0 && buffer[i-1]=='\r' ? i-1 : i;
                string line = buffer.ToString(0, length);
                string leftover = buffer.Length==i+1 ? null : buffer.ToString(i+1, buffer.Length-(i+1));

                try { OnRawData(line, true); }
                catch(Exception e) { OnError(e); }
                
                buffer.Length=0; i=-1;
                buffer.Append(leftover);
              }
          }
        }

      TcpListener srv = identd;
      if(srv!=null && srv.Pending())
      { Socket s = srv.AcceptSocket();
        SendArray(Encoding.ASCII.GetBytes(ident), s);
        s.Close();
      }

      lock(this)
      { if(messages.Count!=0)
        { Message m = (Message)messages[0];
          if(m.Delay==null)
            m.Delay = DateTime.Now.AddMilliseconds(m.Text.Length*mpc + rand.Next(3000, 12000));
          else if(DateTime.Now > (DateTime)m.Delay)
          { SendMessage(m.To, m.Text);
            messages.RemoveAt(0);
          }
        }
        
        for(int i=0; i<actions.Count; i++)
        { Action a = (Action)actions[i];
          if(DateTime.Now >= a.Time)
          { SendRaw(a.RawText);
            actions.RemoveAt(i--);
          }
        }
      }
    }
  }

  string FilterInput(string input)
  { char first = input[0];
    if(first!='"' && first!='\'' && !char.IsLetterOrDigit(first)) return null;
    input = inputstrip.Replace(input, "");

    if(input.StartsWith(nick))
    { int i = nick.Length;
      while(i<input.Length && (char.IsPunctuation(input[i]) || char.IsWhiteSpace(input[i]))) i++;
      input = input.Substring(i);
    }

    int lower=0, upper=0, spaces=0, letters;
    for(int i=0; i<input.Length; i++)
    { char c = input[i];
      if(char.IsLetter(c))
      { if(char.ToLower(c)==c) lower++;
        else upper++;
      }
      else if(char.IsWhiteSpace(c)) spaces++;
    }

    letters = lower+upper;
    if(upper>lower || letters<20 || spaces*100/input.Length>30 || (letters+spaces)*100/input.Length<85) return null;
    return input;
  }

  string ident="1826, 6667 : USERID : UNIX : huzzah", nick, logFile;
  ArrayList messages = new ArrayList(), actions = new ArrayList();
  System.IO.TextWriter log;
  TcpListener identd;
  Socket sock;
  Thread thread;
  Brain brain = new Brain();
  
  int mpc=200;
  bool quitting;

  static string GetRandomNick()
  { Random r = new Random();
    string nick="";
    for(int i=0; i<8; i++) nick += (char)(r.Next(26)+'a');
    return nick;
  }

  static int NickCompare(string a, string b)
  { if(a.Length!=b.Length) return b.Length-a.Length;
    a = a.ToLower();
    b = b.ToLower();
    for(int i=0; i<a.Length; i++)
      if(a[i]!=b[i])
      { char ca=a[i], cb=b[i];
        if(ca=='[' && cb=='{'  || ca=='{'  && cb=='[' ||
           ca==']' && cb=='}'  || ca=='}'  && cb==']' ||
           ca=='|' && cb=='\\' || ca=='\\' && cb=='|')
          continue;
        return cb-ca;
      }
    return 0;
  }

  static bool NickEquals(string a, string b) { return NickCompare(a, b)==0; }

  static void SendArray(byte[] data, Socket socket)
  { int total=0;
    while(total<data.Length)
    { int written = socket.Send(data, total, data.Length-total, SocketFlags.None);
      if(written==0) return; // this probably can't happen, but...indicates something bad i guess... what to do?
      total += written;
    }
  }

  static string Strip(string text)
  { for(int i=0; i<text.Length; i++)
    { char c = text[i];
      if(c<32 || c>126)
      { StringBuilder sb = new StringBuilder(text, 0, i, text.Length);
        for(int j=i+1; j<text.Length; j++)
        { c=text[j];
          if(c>=32 && c<=126) sb.Append(c);
        }
        return sb.ToString();
      }
    }
    return text;
  }

  static readonly Regex parsere = new Regex(@"
    ^(?::(?<prefix>\S+)\x20+)?
     (?<command>\d\d\d|[a-zA-Z]+)
     (?:\x20+(?<params>.*?))?\s*$",
     RegexOptions.Compiled|RegexOptions.Singleline|RegexOptions.IgnorePatternWhitespace);
  static readonly Regex parmsre = new Regex(@":.*|[^\x20\x00\r\n]+", RegexOptions.Compiled|RegexOptions.Singleline);
  static readonly Regex nickre  = new Regex(@"^(?<nick>[\w\-\[\]'`^{}]+)(?:!(?<user>\S+))?(?:@(?<host>[\w\.]+))?", 
                                   RegexOptions.Compiled|RegexOptions.Singleline);
  static readonly Regex badinput = new Regex(@"[a-zA-Z]+://", RegexOptions.Compiled|RegexOptions.Singleline);
  static readonly Regex inputstrip = new Regex(@"^[\w\-]+:\s*", RegexOptions.Compiled|RegexOptions.Singleline);
  static readonly Random rand = new Random();
  static readonly byte[] crlf = { 13, 10 };
}

} // namespace HalBot