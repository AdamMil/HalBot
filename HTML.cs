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
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using AdamMil.Collections;

namespace HalBot
{

#region HtmlDocument
sealed class HtmlDocument
{
  public HtmlNode Root
  {
    get; private set;
  }

  public void Load(string filePath)
  {
    LoadHtml(File.ReadAllText(filePath));
  }

  public void LoadHtml(string html)
  {
    if(html == null) throw new ArgumentNullException();

    StringBuilder content = new StringBuilder();
    AdamMil.Collections.Stack<HtmlNode> ancestors = new AdamMil.Collections.Stack<HtmlNode>();
    ancestors.Push(new HtmlNode(HtmlNodeType.Document, null, null, null));

    for(int i=0; i<html.Length; )
    {
      char c = html[i];

      if(c == '<' && i<html.Length-2)
      {
        char next = html[i+1];
        if(next == '/' || char.IsLetter(next)) // it looks like a tag
        {
          string tagName;
          Dictionary<string, string> attributes;
          bool closingTag = next == '/', emptyTag;
          int index = i + (closingTag ? 2 : 1);
          if(FinishParsingTag(html, ref index, out tagName, out emptyTag, out attributes))
          {
            if(closingTag) // if it's a closing tag, pop ancestors until we get to one with the same name
            {
              int popUntil = ancestors.Count-1;
              for(; popUntil != 0; popUntil--)
              {
                if(string.Equals(ancestors[popUntil].TagName, tagName, StringComparison.OrdinalIgnoreCase))
                {
                  break;
                }
              }

              if(popUntil != 0) // but we don't want to pop everything if no match was found...
              {
                AddTextContent(ancestors, content);
                while(ancestors.Count > popUntil) ancestors.Pop();
              }
            }
            else // otherwise, it's an open tag, so add the new item
            {
              HtmlNode node = new HtmlNode(HtmlNodeType.Element, tagName, attributes, null);
              if(node.ShouldBeEmpty) emptyTag = true;
              AddNode(ancestors, node, content, !emptyTag);
            }

            i = index;
            continue;
          }
        }
        else if(next == '!') // it looks like a comment, CDATA block, or DOCTYPE tag
        {
          next = html[i+2];

          if(next == '-') // it looks like a comment
          {
            string text;
            int index = i+3;
            if(FinishParsingComment(html, ref index, out text))
            {
              AddNode(ancestors, new HtmlNode(HtmlNodeType.Comment, text, null), content, false);
              i = index;
              continue;
            }
          }
          else if(next == '[') // it looks like a CDATA block
          {
            string text;
            int index = i+3;
            if(FinishParsingCDATA(html, ref index, out text))
            {
              AddNode(ancestors, new HtmlNode(HtmlNodeType.Text, null, text), content, false);
              i = index;
              continue;
            }
          }

          if(!char.IsWhiteSpace(next)) // the comment or CDATA block failed to parse. it might be s some other <! tag
          {
            i = SkipUntil(html, '>', i+2) + 1; // so skip it
            continue;
          }
        }
      }

      content.Append(c);
      i++;
    }

    AddTextContent(ancestors, content);
    Root = ancestors[0];
  }

  public string ToUnformattedText()
  {
    StringBuilder sb = new StringBuilder();
    ToUnformattedText(Root, sb);
    return sb.ToString();
  }

  public XmlDocument ToXml()
  {
    XmlDocument xmlDocument = new XmlDocument();
    if(Root != null)
    {
      foreach(HtmlNode node in Root.Children) AddNodes(xmlDocument, xmlDocument, node);
    }
    return xmlDocument;
  }

  public static string ConvertToUnformattedText(string html)
  {
    HtmlDocument htmlDocument = new HtmlDocument();
    htmlDocument.LoadHtml(html);
    return htmlDocument.ToUnformattedText();
  }

  public static XmlDocument ConvertToXml(string html)
  {
    HtmlDocument htmlDocument = new HtmlDocument();
    htmlDocument.LoadHtml(html);
    return htmlDocument.ToXml();
  }

  void ToUnformattedText(HtmlNode node, StringBuilder sb)
  {
    if(node.IsVisible)
    {
      if(node.Type == HtmlNodeType.Text)
      {
        sb.Append(NormalizeWhitespace(node.Value));
      }
      else
      {
        foreach(HtmlNode child in node.Children) ToUnformattedText(child, sb);
        if(node.IsBlockElement) sb.Append('\n');
      }
    }
  }

  static void AddNode(AdamMil.Collections.Stack<HtmlNode> ancestors, HtmlNode node, StringBuilder textContent,
                      bool pushNode)
  {
    AddTextContent(ancestors, textContent);
    ancestors.Peek().AddChild(node);
    if(pushNode) ancestors.Push(node);
  }

  static void AddNodes(XmlDocument xmlDocument, XmlNode parent, HtmlNode htmlNode)
  {
    if(htmlNode.Type == HtmlNodeType.Text)
    {
      parent.AppendChild(xmlDocument.CreateTextNode(htmlNode.Value));
    }
    else if(htmlNode.Type == HtmlNodeType.Comment)
    {
      parent.AppendChild(xmlDocument.CreateComment(htmlNode.Value));
    }
    else
    {
      XmlElement element = xmlDocument.CreateElement(htmlNode.TagName);
      parent.AppendChild(element);
      foreach(KeyValuePair<string, string> pair in htmlNode.Attributes) element.SetAttribute(pair.Key, pair.Value);
      foreach(HtmlNode childNode in htmlNode.Children) AddNodes(xmlDocument, element, childNode);
    }
  }

  static void AddTextContent(AdamMil.Collections.Stack<HtmlNode> ancestors, StringBuilder textContent)
  {
    if(textContent.Length != 0)
    {
      // only add the text if it contains a non-whitespace character
      for(int start=0; start<textContent.Length; start++)
      {
        if(!char.IsWhiteSpace(textContent[start]))
        {
          // find the end of the whitespace
          int end = textContent.Length-1;
          for(; end > start; end--)
          {
            if(!char.IsWhiteSpace(textContent[end])) break;
          }

          // collapse contiguous whitespace from the beginning and end into single spaces
          if(start > 0) textContent[--start] = ' ';
          if(end < textContent.Length-1) textContent[++end] = ' ';

          ancestors.Peek().AddChild(new HtmlNode(HtmlNodeType.Text, textContent.ToString(start, end-start+1), null));
          break;
        }
      }

      textContent.Length = 0;
    }
  }

  static bool FinishParsingCDATA(string html, ref int index, out string text)
  {
    // this method expects 'index' to be pointing at the character after the first '[' in <![CDATA[...]]>
    text = null;

    int start = index;
    if(start < html.Length-5 && html[start] == 'C' && html[start+1] == 'D' && html[start+2] == 'A' &&
       html[start+3] == 'T' && html[start+4] == 'A' && html[start+5] == '[')
    {
      start += 6; // move to the beginning of the content

      int end = start;
      while(end < html.Length)
      {
        int bracket = html.IndexOf(']', end); // try to find the next bracket
        if(bracket == -1 || bracket >= html.Length-3) // if there's no end to the CDATA section
        {
          index = end = html.Length; // then treat the rest of the text as part of it
        }
        else if(html[bracket+1] == ']' && html[bracket+2] == '>') // otherwise, if the CDATA section ends here
        {
          end   = bracket;
          index = bracket+3;
          break;
        }
      }

      text = html.Substring(start, end-start);
      return true;
    }

    return false;
  }

  static bool FinishParsingComment(string html, ref int index, out string text)
  {
    // this method expects 'index' to be pointing at the character after the first dash in the comment block
    text = null;

    int start = index;
    if(start < html.Length && html[start] == '-') // if there's a second dash, then read the rest of the comment
    {
      start++; // skip the second dash
      start = SkipWhitespace(html, start);
      int end = SkipUntil(html, '>', start);

      index = end + 1; // resume parsing after the comment

      // if the comment text ends with two dashes, strip them off
      if(end <= html.Length && html[end-1] == '-') end--;
      if(end <= html.Length && html[end-1] == '-') end--;

      // now strip off trailing whitespace
      while(end <= html.Length && char.IsWhiteSpace(html[end-1])) end--;

      text = start >= end ? "" : html.Substring(start, end-start);
      return true;
    }

    return false;
  }

  static bool FinishParsingTag(string html, ref int index, out string tagName, out bool emptyTag,
                               out Dictionary<string, string> attributes)
  {
    attributes = null;
    tagName    = null;
    emptyTag   = false;

    int start = index;
    if(char.IsLetter(html[start]))
    {
      int end = SkipLetters(html, start);
      tagName = html.Substring(start, end-start);

      // now we need to parse the attributes, if any. we'll first advance to the next non-whitespace character
      start = SkipWhitespace(html, end);

      // now we'll parse attributes and the rest of the tag
      while(start < html.Length)
      {
        char c = html[start];

        if(c == '>') // the tag is finished
        {
          start++; // move past it
          break;
        }
        else if(c == '/') // it looks like it might be an empty tag, so skip to the end
        {
          emptyTag = true;
          start = SkipUntil(html, '>', start+1) + 1; // move to and then past the next '>'
          break;
        }
        else if(!char.IsLetter(c)) // it's garbage, so skip it
        {
          start++;
        }
        else // it looks like it may be an attribute
        {
          end = SkipLetters(html, start+1);
          string attrName = html.Substring(start, end-start), attrValue = null;

          start = SkipWhitespace(html, end);
          if(start < html.Length && html[start] == '=') // if there's an equal sign, read the attribute value
          {
            start = SkipWhitespace(html, start+1);
            if(start < html.Length)
            {
              char delimiter = html[start];
              if(delimiter == '"' || delimiter == '\'') // read until the closing delimiter or '>'
              {
                start++;
                end = start+1;
                while(end < html.Length)
                {
                  c = html[end];
                  if(c == delimiter || c == '>') break;
                  end++;
                }
              }
              else // otherwise, there's no delimiter, so read until whitespace or '>'
              {
                end = start+1;
                while(end < html.Length)
                {
                  c = html[end];
                  if(c == '>' || char.IsWhiteSpace(c)) break;
                  end++;
                }
              }

              attrValue = html.Substring(start, end-start);
              if(c == '>')
              {
                start = end+1;
                break;
              }
              else
              {
                start = SkipWhitespace(html, end+1);
              }
            }
          }

          if(attributes == null) attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
          attributes[attrName] = attrValue == null ? null : HttpUtility.HtmlDecode(attrValue);
        }
      }

      index = start;
      return true;
    }

    return false;
  }

  static string NormalizeWhitespace(string str)
  {
    return whitespaceRe.Replace(str, " "); // replace consecutive whitespace with a single space
  }

  static int SkipLetters(string str, int index)
  {
    while(index < str.Length && char.IsLetter(str[index])) index++;
    return index;
  }

  static int SkipUntil(string str, char c, int index)
  {
    while(index < str.Length && str[index] != c) index++;
    return index;
  }

  static int SkipWhitespace(string str, int index)
  {
    while(index < str.Length && char.IsWhiteSpace(str[index])) index++;
    return index;
  }

  static readonly Regex whitespaceRe = new Regex(@"\s+", RegexOptions.Compiled | RegexOptions.Singleline);
}
#endregion

#region HtmlNode
sealed class HtmlNode
{
  internal HtmlNode(HtmlNodeType type, string encodedValue, string value)
  {
    if(type != HtmlNodeType.Text && type != HtmlNodeType.Comment) throw new ArgumentException();
    Type         = type;
    IsVisible    = type == HtmlNodeType.Text;
    Attributes   = NoAttributes;
    Children     = NoChildren;
    EncodedValue = encodedValue;
    Value        = value;
  }

  internal HtmlNode(HtmlNodeType type, string tagName, IDictionary<string,string> attributes, IList<HtmlNode> children)
  {
    Type    = type;
    TagName = tagName;

    if(tagName == null)
    {
      IsVisible = true;
    }
    else
    {
      string lowerCaseTag = tagName.ToLowerInvariant();
      IsBlockElement = blockElements.Contains(lowerCaseTag);
      ShouldBeEmpty = emptyElements.Contains(lowerCaseTag);
      IsVisible = !ShouldBeEmpty && !invisibleElements.Contains(lowerCaseTag);
    }

    Attributes = attributes == null || attributes.Count == 0 ?
      NoAttributes : new ReadOnlyDictionaryWrapper<string, string>(attributes);

    Children = children == null || children.Count == 0 ?
      NoChildren : new ReadOnlyCollection<HtmlNode>(children);
  }

  static HtmlNode()
  {
    NoAttributes = new ReadOnlyDictionaryWrapper<string, string>(new Dictionary<string, string>());
    NoChildren   = new ReadOnlyCollection<HtmlNode>(new HtmlNode[0]);

    string[] elements = new string[]
    {
      "address", "blockquote", "center", "dd", "dir", "div", "dl", "dt", "fieldset", "form", "frameset", "h1", "h2",
      "h3", "h4", "h5", "h6", "hr", "isindex", "li", "menu", "noframes", "noscript", "ol", "p", "pre", "table",
      "tbody", "td", "th", "thead", "tr", "ul",
    };
    blockElements = new HashSet<string>(StringComparer.Ordinal);
    foreach(string element in elements) blockElements.Add(element);

    elements = new string[]
    {
      "area", "base", "basefont", "br", "col", "frame", "hr", "img", "input", "isindex", "link", "meta", "param"
    };
    emptyElements = new HashSet<string>(StringComparer.Ordinal);
    foreach(string element in elements) emptyElements.Add(element);

    elements = new string[]
    {
      "applet", "frameset", "iframe", "input", "isindex", "map", "noframes", "noscript", "object", "script", "title"
    };
    invisibleElements = new HashSet<string>(StringComparer.Ordinal);
    foreach(string element in elements) invisibleElements.Add(element);
  }

  public IReadOnlyDictionary<string,string> Attributes
  {
    get; private set;
  }

  public ReadOnlyCollection<HtmlNode> Children
  {
    get; private set;
  }

  public string EncodedValue
  {
    get
    {
      if(_encodedValue == null && _value != null) _encodedValue = HttpUtility.HtmlEncode(_value);
      return _encodedValue;
    }
    set { _encodedValue = value; }
  }

  public string InnerHtml
  {
    get
    {
      if(Type == HtmlNodeType.Text)
      {
        return EncodedValue;
      }
      else
      {
        StringBuilder sb = new StringBuilder();
        foreach(HtmlNode child in Children) sb.Append(child.OuterHtml);
        return sb.ToString();
      }
    }
  }

  public string InnerText
  {
    get { return GetInnerText(true); }
  }

  public bool IsBlockElement
  {
    get; private set;
  }

  /// <summary>Gets whether the node's HTML content is normally rendered as part of the document body. Obvious examples
  /// of elements that are not normally rendered are SCRIPT and STYLE, but other elements such as TITLE, APPLET,
  /// OBJECT, FRAMESET, NOSCRIPT, and IFRAME are also included, either because the content is not normally rendered
  /// or the content is not HTML.
  /// </summary>
  public bool IsVisible
  {
    get; private set;
  }

  public string OuterHtml
  {
    get
    {
      if(Type == HtmlNodeType.Text)
      {
        return EncodedValue;
      }
      else
      {
        StringBuilder sb = new StringBuilder();
        GetOuterHtml(sb);
        return sb.ToString();
      }
    }
  }

  public string TagName
  {
    get; private set;
  }

  public HtmlNodeType Type
  {
    get; private set;
  }

  public string Value
  {
    get
    {
      if(_value == null && _encodedValue != null) _value = HttpUtility.HtmlDecode(_encodedValue);
      return _value;
    }
    set { _value = value; }
  }

  public string GetInnerText(bool hideInvisibleNodes)
  {
    if(hideInvisibleNodes && !IsVisible)
    {
      return string.Empty;
    }
    else if(Type == HtmlNodeType.Text)
    {
      return Value;
    }
    else
    {
      StringBuilder sb = new StringBuilder();
      GetInnerText(sb, hideInvisibleNodes);
      return sb.ToString();
    }
  }

  public override string ToString()
  {
    switch(Type)
    {
      case HtmlNodeType.Comment: return "<!-- " + EncodedValue + " -->";
      case HtmlNodeType.Text: return Value;
      case HtmlNodeType.Element: return "<" + TagName + ">";
      default: return "[" + Type.ToString() + "]";
    }
  }

  internal bool ShouldBeEmpty
  {
    get; set;
  }

  internal void AddChild(HtmlNode child)
  {
    if(_children == null) // replace the read-only collection with a writable one
    {
      _children = new List<HtmlNode>(Children);
      Children = new ReadOnlyCollection<HtmlNode>(_children);
    }
    
    _children.Add(child);
  }

  void GetInnerHtml(StringBuilder sb)
  {
    if(Type == HtmlNodeType.Text)
    {
      sb.Append(EncodedValue);
    }
    else
    {
      foreach(HtmlNode child in Children) child.GetOuterHtml(sb);
    }
  }

  void GetInnerText(StringBuilder sb, bool hideInvisibleNodes)
  {
    if(!hideInvisibleNodes || IsVisible)
    {
      if(Type == HtmlNodeType.Text)
      {
        sb.Append(Value);
      }
      else
      {
        foreach(HtmlNode child in Children) child.GetInnerText(sb, hideInvisibleNodes);
      }
    }
  }

  void GetOuterHtml(StringBuilder sb)
  {
    if(Type == HtmlNodeType.Text)
    {
      sb.Append(EncodedValue);
    }
    else if(Type == HtmlNodeType.Comment)
    {
      sb.Append("<!-- ").Append(EncodedValue).Append(" -->");
    }
    else
    {
      if(TagName != null)
      {
        sb.Append('<').Append(TagName);

        if(Attributes.Count != 0)
        {
          foreach(KeyValuePair<string, string> pair in Attributes)
          {
            sb.Append(' ').Append(pair.Key).Append("=\"").Append(HttpUtility.HtmlAttributeEncode(pair.Value))
              .Append('"');
          }
        }
      }

      if(ShouldBeEmpty && Children.Count == 0)
      {
        sb.Append(" />");
      }
      else
      {
        if(TagName != null) sb.Append('>');
        foreach(HtmlNode child in Children) child.GetOuterHtml(sb);
        if(TagName != null) sb.Append("</").Append(TagName).Append('>');
      }
    }
  }

  List<HtmlNode> _children;
  string _encodedValue, _value;

  static readonly ReadOnlyDictionaryWrapper<string, string> NoAttributes;
  static readonly ReadOnlyCollection<HtmlNode> NoChildren;
  static readonly HashSet<string> blockElements, emptyElements, invisibleElements;
}
#endregion

#region HtmlNodeType
public enum HtmlNodeType
{
  Text, Element, Comment, Document
}
#endregion

} // namespace HalBot
