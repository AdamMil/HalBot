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
using System.Text;
using System.Text.RegularExpressions;
using AdamMil.Collections;
using AdamMil.IO;
using AdamMil.Mathematics;
using AdamMil.Mathematics.Combinatorics;

namespace HalBot
{

sealed class Brain
{
  public Brain() : this(null) { }

  public Brain(Brain parentBrain)
  {
    this.parentBrain    = parentBrain;
    this.MarkovOrder    = 3;
    this.MaxBlendChance = 0.75f; // TODO: tune this
  }

  static Brain()
  {
    swaps       = LoadDictionary("swaps.txt", true);
    spellings   = LoadDictionary("corrections.txt", false);
    badKeywords = LoadKeywordSet("badKeywords.txt");
    greetings   = LoadKeywordSet("greetingWords.txt");
  }

  public delegate float ResponseEvaluator(Utterance response);

  #region Utterance
  public sealed class Utterance
  {
    public Utterance(string keyword, string[] words, float averageProbability)
    {
      Keyword     = keyword;
      Words       = words;
      AverageWordProbability = averageProbability;
    }

    public string Text
    {
      get
      {
        if(text == null) text = WordsToString(Words);
        return text;
      }
    }

    public override string ToString()
    {
      return "(" + Keyword + "," + AverageWordProbability.ToString(CultureInfo.InvariantCulture) + ") " + Text;
    }

    public readonly string Keyword;
    public readonly string[] Words;
    public readonly float AverageWordProbability;
    string text;
  }
  #endregion

  /// <summary>Gets whether any information has been stored in the brain.</summary>
  public bool IsEmpty
  {
    get { return totalRootCount == 0; }
  }

  /// <summary>Gets the maximum probability that the parent brain will be used rather than this brain. The probability
  /// is based on the number of words in this brain compared to the number of words in the parent brain, but cannot
  /// be higher than this value. The default is 0.75, representing 75%.
  /// </summary>
  public float MaxBlendChance
  {
    get { return _blendFactor; }
    set
    {
      if(value < 0 || value > 1) throw new ArgumentOutOfRangeException();
      _blendFactor = value;
    }
  }

  // TODO: make sure changing this after stuff has been learned doesn't cause crashes or incorrect behavior
  public int MarkovOrder
  {
    get { return _markovOrder; }
    set
    {
      if(value < 1) throw new ArgumentOutOfRangeException();
      _markovOrder = value;
    }
  }

  public void Clear()
  {
    forwardModel.Clear();
    backwardModel.Clear();
    totalRootCount = 0;
  }

  public string GetRandomUtterance()
  {
    return GetRandomUtterance(true);
  }

  public string GetRandomUtterance(bool blendWithParent)
  {
    if(forwardModel.Count == 0) return parentBrain != null ? parentBrain.GetRandomUtterance(blendWithParent) : null;

    // choose a keyword based on frequency (it should be relatively rare, as that would indicate a word like "a", but
    // not too rare, because that could indicate a misspelled word or something)
    
    // TODO: GetRandomUtterance() only uses keywords from this brain, while GetResponse() uses keywords from all brains. is that
    // correct?

    int totalCount = 0, maxCount = 0; // calculate the total and maximum top-level word counts
    List<MarkovNode> validNodes = new List<MarkovNode>();
    foreach(MarkovNode node in forwardModel.Values)
    {
      if(!IsBadKeyword(node.Word))
      {
        validNodes.Add(node);
        totalCount += node.Count;
        if(node.Count > maxCount) maxCount = node.Count;
      }
    }

    if(validNodes.Count == 0)
    {
      return blendWithParent && parentBrain != null ? parentBrain.GetRandomUtterance(blendWithParent) : null;
    }

    Permutations.RandomlyPermute(validNodes, rand);

    // generate a response using random rare-ish keywords
    for(int tries=0,index=rand.Next(validNodes.Count); tries < 10; tries++)
    {
      // choose a keyword with a relatively low count
      int randomCount = rand.Next(totalCount);
      MarkovNode keywordNode;
      while(true)
      {
        keywordNode = validNodes[index];
        if(++index == validNodes.Count) index = 0;
        randomCount -= maxCount - keywordNode.Count + 1;
        if(randomCount <= 0) break;
      }

      Utterance response = GenerateUtterance(keywordNode.Word, blendWithParent);
      if(response != null) return response.Text;
    }

    return null;
  }

  public string GetResponse(string input, bool blendWithParentBrain, bool correctSpelling)
  {
    return GetResponse(input, 1, blendWithParentBrain, correctSpelling, null);
  }

  public string GetResponse(string input, int responsesToGenerate, bool blendWithParentBrain, bool correctSpelling,
                            ResponseEvaluator evaluator)
  {
    string[] words = Tokenize(input, false);
    if(words.Length == 0) return null;

    if(correctSpelling) CorrectSpelling(words);
    words = GetResponseKeywords(words); // normalize and reverse keywords (eg, why -> because)
    if(words.Length == 0) return null;

    // for each keyword, figure out how many times it has been used (i.e. how common it is)

    // TODO: GetRandomUtterance() only uses keywords from this brain, while GetResponse() uses keywords from all brains. is that
    // correct?

    int maxCount=0, totalCount=0, knownWords=0;
    int[] counts = new int[words.Length];
    for(int i=0; i<counts.Length; i++)
    {
      MarkovNode foreNode, backNode;
      Brain brain = this;
      int usageCount = 0;
      do
      {
        brain.forwardModel.TryGetValue(words[i], out foreNode);
        brain.backwardModel.TryGetValue(words[i], out backNode);
        usageCount += Math.Max(foreNode != null ? foreNode.Count : 0, backNode != null ? backNode.Count : 0);
        brain = brain.parentBrain;
      } while(brain != null && blendWithParentBrain);

      if(usageCount != 0) knownWords++;
      if(usageCount > maxCount) maxCount = usageCount;
      totalCount += usageCount;
      counts[i] = usageCount;
    }

    if(knownWords == 0) return null;

    // generate a number of random responses
    List<Utterance> responses = new List<Utterance>(responsesToGenerate);
    for(int i=0; i < responsesToGenerate; i++)
    {
      var tries = from count in Enumerable.Range(0, 5) // since there's chance involved, try several times if necessary
                  let r = GetResponse(words, counts, maxCount, totalCount, blendWithParentBrain)
                  where r != null select r;

      Utterance response = tries.FirstOrDefault();
      if(response == null)
      {
        if(responses.Count == 0) break; // if we couldn't generate the first response, then give up
      }
      else
      {
        if(evaluator == null) return response.Text; // if there's no evaluator, there's no point in generating more than one
        else responses.Add(response);
      }
    }

    if(responses.Count == 0)
    {
      return null;
    }
    else
    {
      var sortedResponses = from r in responses let value = evaluator(r) where value > 0 orderby value descending select r.Text;
      return sortedResponses.FirstOrDefault(); // return the best response with positive value, or null if none
    }
  }

  public void LearnLine(string line, bool correctSpelling)
  {
    if(line == null) throw new ArgumentNullException();
    if(line.Length == 0) return;

    string[] words = Tokenize(line, true); // true to include the EOF token (null)
    if(!ShouldLearnFrom(words)) return;

    if(correctSpelling) CorrectSpelling(words);

    // learn the forward word associations (exclude the last word because it's the EOF token, null)
    for(int i=0; i < words.Length-1; i++) LearnMarkovModel(forwardModel, words, i);
    
    // then reverse the word order and learn the backward word associations (excluding the EOF token)
    Array.Reverse(words, 0, words.Length-1);
    for(int i=0; i < words.Length-1; i++) LearnMarkovModel(backwardModel, words, i);
  }

  public void LearnLines(TextReader reader, bool correctSpelling)
  {
    ProcessLines(reader, line => LearnLine(line, correctSpelling));
  }

  public static bool IsBadKeyword(string word)
  {
    return !char.IsLetterOrDigit(word[0]) || badKeywords.Contains(word) || IsUrl(word);
  }

  public static bool IsGreeting(string word)
  {
    return greetings.Contains(word);
  }

  public static string[] SplitWords(string text, bool correctSpelling)
  {
    if(text == null) throw new ArgumentNullException();
    string[] words = Tokenize(text, false);
    if(correctSpelling) CorrectSpelling(words);
    return words;
  }

  #region MarkovNode
  sealed class MarkovNode
  {
    public MarkovNode(string word)
    {
      Word = word;
    }

    public MarkovNode AddChild(string word)
    {
      int index = Children == null ? ~0 : Array.BinarySearch(Children, 0, NumChildren, word, NodeWordComparer.Instance);

      MarkovNode child;
      if(index >= 0)
      {
        child = Children[index];
      }
      else
      {
        child = new MarkovNode(word);

        // ensure we have space in the array to hold the new word
        if(NumChildren == 0) Children = new MarkovNode[4];
        else if(NumChildren == Children.Length)
        {
          MarkovNode[] newChildren = new MarkovNode[Children.Length*2];
          Array.Copy(Children, newChildren, Children.Length);
          Children = newChildren;
        }

        // now add the child in sorted order
        index = ~index;
        for(int i=NumChildren; i > index; i--) Children[i] = Children[i-1];
        Children[index] = child;
        NumChildren++;
      }

      child.Count++;
      TotalChildCount++;
      return child;
    }

    public MarkovNode GetChild(string word)
    {
      if(NumChildren == 0) return null;
      int index = Array.BinarySearch(Children, 0, NumChildren, word, NodeWordComparer.Instance);
      return index < 0 ? null : Children[index];
    }

    public MarkovNode GetRandomChild(Random rand)
    {
      if(NumChildren == 0) return null;
      for(int n = rand.Next(TotalChildCount); ;)
      {
        if(++randIndex >= NumChildren) randIndex = 0;
        n -= Children[randIndex].Count;
        if(n <= 0) break;
      }
      return Children[randIndex];
    }

    public override string ToString()
    {
      return (Word == null ? "NULL" : Word) + " (" + Count.ToString(CultureInfo.InvariantCulture) + ")";
    }

    public readonly string Word;
    public MarkovNode[] Children;
    /// <summary>The number of times this node has been seen within its parent's context.</summary>
    public int Count;
    /// <summary>The sum of this node's children's <see cref="Count"/> fields.</summary>
    public int TotalChildCount;
    /// <summary>Gets the number of children that this node has.</summary>
    public int NumChildren;

    int randIndex;
  }
  #endregion

  #region NodeWordComparer
  sealed class NodeWordComparer : IComparer<MarkovNode>, System.Collections.IComparer
  {
    NodeWordComparer() { }

    public int Compare(MarkovNode a, MarkovNode b)
    {
      return string.Compare(a.Word, b.Word, StringComparison.Ordinal);
    }

    public int Compare(object a, object b)
    {
      string aStr, bStr;

      if(a == null)
      {
        aStr = null;
      }
      else
      {
        aStr = a as string;
        if(aStr == null) aStr = ((MarkovNode)a).Word;
      }

      if(b == null)
      {
        bStr = null;
      }
      else
      {
        bStr = b as string;
        if(bStr == null) bStr = ((MarkovNode)b).Word;
      }

      return string.Compare(aStr, bStr, StringComparison.Ordinal);
    }

    public static readonly NodeWordComparer Instance = new NodeWordComparer();
  }
  #endregion

  void AddWords(List<string> words, Brain nodeBrain, MarkovNode node, bool forward, bool blendWithParent, float blendChance,
                ref float probabilitySum)
  {
    bool tryToMatch = words.Count > 1; // if the list already has words, we'll try to use a node that matches up
    while(true)                        // with the list before using the passed-in, top-level node
    {
      MarkovNode nextNode = tryToMatch ? null : node.GetRandomChild(rand); // choose a random continuation
      // if we've reached the end of this chain due to it being truncated, we'll try to choose a new chain.
      if(nextNode == null)
      {
        Brain testBrain = this;
        MarkovNode testNode = FindContinuation(words, ref testBrain, blendWithParent, forward);

        // if we're blending brains, try choosing a continuation from a random ancestor
        while(testNode != null && testBrain.parentBrain != null && blendWithParent && rand.NextDouble() < blendChance)
        {
          Brain blendBrain = testBrain.parentBrain;
          MarkovNode blendNode = FindContinuation(words, ref blendBrain, blendWithParent, forward);
          if(blendNode == null) break;

          testNode  = blendNode;
          testBrain = blendBrain;
        }

        if(tryToMatch && testNode == null) // if we were trying to avoid using 'node' but failed...
        {
          testNode  = node; // fall back on using 'node' and 'nodeBrain'
          testBrain = nodeBrain;
        }
        else // we either found a continuation, or didn't and don't have anything to fall back on, so use 'testNode' as our value
        {
          node      = testNode;
          nodeBrain = testBrain;
        }

        // if we found a chain that matches the end of the list, continue from there
        if(node != null) nextNode = node.GetRandomChild(rand);
        if(nextNode == null) break; // if there's still no continuation, then we're done
      }

      if(nextNode.Word == null) break; // if we've reached the natural end of the chain, then we're done

      if(!IsBadKeyword(nextNode.Word))
      {
        // update the probability. this isn't the joint probability of the whole chain (ie, the product of all the
        // word probabilities), since that would bias heavily for or against long replies. instead, we'll calculate
        // the sum of the probabilities of each word and use that later to compute the average word probability
        probabilitySum += nodeBrain.GetWordProbability(nextNode.Word, blendWithParent);
      }

      words.Add(nextNode.Word);
      node = nextNode;
      tryToMatch = false;
    }
  }

  MarkovNode AddRootNode(Dictionary<string, MarkovNode> model, string word)
  {
    MarkovNode node;
    if(!model.TryGetValue(word, out node)) model[word] = node = new MarkovNode(word);
    node.Count++;
    totalRootCount++;
    return node;
  }

  MarkovNode ChooseNode(string keyword, bool forward, bool blendWithParent, float parentChance, out Brain brainUsed)
  {
    MarkovNode node = null, testNode = null;
    Brain testBrain = this;
    brainUsed = null;
    do
    {
      if(testBrain.GetModel(forward).TryGetValue(keyword, out testNode))
      {
        node      = testNode;
        brainUsed = testBrain;
      }

      if(!blendWithParent || testNode != null && rand.NextDouble() >= parentChance) break;
      testBrain = testBrain.parentBrain;
    } while(testBrain != null);

    return node;
  }

  bool Coinflip()
  {
    return (rand.Next() & 1) != 0;
  }

  MarkovNode FindContinuation(List<string> words, ref Brain brain, bool searchAncestors, bool forward)
  {
    MarkovNode node;
    do
    {
      Dictionary<string, MarkovNode> model = brain.GetModel(forward);
      // if the list is current A B C D E, with order 3, then we'll find a global chain that goes D -> E -> ? and
      // continue from there
      int i = Math.Max(0, words.Count - brain.MarkovOrder + 1); // point to D in our example (or the first word)
      if(model.TryGetValue(words[i], out node))
      {
        while(node != null && ++i < words.Count) node = node.GetChild(words[i]);
      }

      if(node != null || !searchAncestors) break;

      brain = brain.parentBrain;
    } while(brain != null);
    return node;
  }

  Utterance GenerateUtterance(string keyword, bool blendWithParent)
  {
    if(parentBrain == null) blendWithParent = false;

    List<string> words = new List<string>();
    float probabilitySum = 0;
    float blendChance = blendWithParent ?
      Math.Min(MaxBlendChance, (float)parentBrain.totalRootCount / (parentBrain.totalRootCount + totalRootCount)) : 0;

    // we'll choose at random whether to do the forward links first or the backward links, the one that happens first has a
    // greater effect on the sentence
    bool forwardFirst = Coinflip();

    // first go one way
    Brain nodeBrain;
    MarkovNode node = ChooseNode(keyword, forwardFirst, blendWithParent, blendChance, out nodeBrain);
    if(node != null)
    {
      words.Add(keyword);
      AddWords(words, nodeBrain, node, forwardFirst, blendWithParent, blendChance, ref probabilitySum);
      words.Reverse(); // now put the words in the opposite order to suit the opposite direction
    }

    // then go the other way
    node = ChooseNode(keyword, !forwardFirst, blendWithParent, blendChance, out nodeBrain);
    if(node != null)
    {
      if(words.Count == 0) words.Add(keyword);
      AddWords(words, nodeBrain, node, !forwardFirst, blendWithParent, blendChance, ref probabilitySum);
    }
    if(forwardFirst) words.Reverse(); // if we just did the backwards links, then we need to put them back in forward order

    if(words.Count <= 1) return null;

    // calculate the average probability, excluding the keyword from the word count
    float averageProbability = probabilitySum /= (words.Count-1);
    return new Utterance(keyword, words.ToArray(), averageProbability);
  }

  Dictionary<string, MarkovNode> GetModel(bool forward)
  {
    return forward ? forwardModel : backwardModel;
  }

  Utterance GetResponse(string[] words, int[] counts, int maxCount, int totalCount, bool blendWithParent)
  {
    // choose the key word that we'll focus on
    string keyword = null;

    // if the input contains a greeting, use that
    foreach(string word in words)
    {
      if(IsGreeting(word))
      {
        Utterance utterance = GenerateUtterance(word, blendWithParent);
        if(utterance != null) return utterance;
      }
    }

    // either it didn't contain a greeting or it couldn't generate a response based on the greeting, so we'll try
    // other keywords based on their rarity
    int index = rand.Next(words.Length), randomCount = rand.Next(totalCount);
    while(true)
    {
      keyword = words[index];
      randomCount -= maxCount - counts[index] + 1;
      if(++index == words.Length) index = 0;
      if(randomCount <= 0) break;
    }

    return GenerateUtterance(keyword, blendWithParent);
  }

  string GetResponseWord(string word)
  {
    if(!char.IsLetterOrDigit(word[0])) return null;
    if(IsBadKeyword(word)) return null;

    string complement;
    if(swaps.TryGetValue(word, out complement) && (rand.Next() & 1) == 0) word = complement;

    return word;
  }

  string[] GetResponseKeywords(string[] words)
  {
    List<string> goodWords = new List<string>(words.Length);
    for(int i=0; i<words.Length; i++)
    {
      string word = GetResponseWord(words[i]);
      if(word != null) goodWords.Add(word);
    }
    return goodWords.ToArray();
  }

  float GetWordProbability(string word, bool blendWithParent)
  {
    Brain brain = this;
    int count = 0, totalCount = 0;
    do
    {
      MarkovNode foreNode, backNode;
      brain.forwardModel.TryGetValue(word, out foreNode);
      brain.backwardModel.TryGetValue(word, out backNode);
      count      += (foreNode != null ? foreNode.Count : 0) + (backNode != null ? backNode.Count : 0);
      totalCount += brain.totalRootCount;
      brain = brain.parentBrain;
    } while(brain != null && blendWithParent);

    return (float)count / totalCount;
  }

  void LearnMarkovModel(Dictionary<string, MarkovNode> model, string[] words, int index)
  {
    MarkovNode node = AddRootNode(model, words[index]);
    for(int i=index+1,end=Math.Min(words.Length, index+MarkovOrder); i < end; i++) node = node.AddChild(words[i]);
  }

  bool ShouldLearnFrom(string[] keywords)
  {
    return keywords.Length > MarkovOrder; // don't learn from very short inputs
  }

  readonly Dictionary<string, MarkovNode> forwardModel = new Dictionary<string, MarkovNode>();
  readonly Dictionary<string, MarkovNode> backwardModel = new Dictionary<string, MarkovNode>();
  readonly Random rand = new Random();
  readonly Brain parentBrain;
  float _blendFactor;
  int totalRootCount, _markovOrder;

  static void CorrectSpelling(string[] words)
  {
    for(int i=0; i<words.Length; i++)
    {
      string word = words[i], newSpelling;
      if(word != null && char.IsLetter(word[0]) && spellings.TryGetValue(word, out newSpelling)) words[i] = newSpelling;
    }
  }

  static bool IsUrl(string keyword)
  {
    return keyword.IndexOf("://", StringComparison.Ordinal) != -1; // assume words containing :// are URLs
  }

  static Dictionary<string, string> LoadDictionary(string file, bool bidirectional)
  {
    Dictionary<string,string> dict = new Dictionary<string,string>();
    ProcessLines(file, line =>
    {
      string[] bits = line.ToLowerInvariant().Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
      if(bits.Length == 2)
      {
        if(bidirectional)
        {
          if(bits[0][0] == '*') bits[0] = bits[0].Substring(1).Trim(); // if it starts with '*', then it's unidirectional
          else dict[bits[1]] = bits[0];
        }
        dict[bits[0]] = bits[1];
      }
    });
    return dict;
  }

  static HashSet<string> LoadKeywordSet(string file)
  {
    HashSet<string> keywords = new HashSet<string>();
    ProcessLines(file, keyword => keywords.Add(keyword.ToLowerInvariant()));
    return keywords;
  }

  static void ProcessLines(string file, Action<string> action)
  {
    file = Path.Combine(Program.DataDirectory, file);
    if(File.Exists(file))
    {
      using(StreamReader reader = new StreamReader(file)) ProcessLines(reader, action);
    }
  }

  static void ProcessLines(TextReader reader, Action<string> action)
  {
    if(reader == null || action == null) throw new ArgumentNullException();
    reader.ProcessNonEmptyLines(line =>
    {
      if(line[0] != '#') action(line); // ignore comments
    });
  }

  static string[] Tokenize(string text, bool includeEOF)
  {
    MatchCollection matches = wordRe.Matches(text.ToLowerInvariant());
    string[] words = new string[matches.Count + (includeEOF ? 1 : 0)];
    for(int i=0; i<matches.Count; i++) words[i] = matches[i].Value;
    return words;
  }

  static string WordsToString(string[] words)
  {
    StringBuilder sb = new StringBuilder();
    bool quote = false, spaceNext = false;
    int parens = 0;

    // TODO: handle smileys that mix letters and punctuation better, especially :D and :P
    // TODO: perhaps text tokenizing and recombining should be moved out of the Brain class?

    foreach(string s in words)
    {
      char firstChar = s[0];
      if(firstChar == '"') // if it's a quote that could be opening or closing...
      {
        if(!quote) { sb.Append(' '); spaceNext = true; } // if it's an opening quote, add a space before it
        quote = !quote;
      }
      else if(firstChar == '“') // it's specifically an open quote...
      {
        sb.Append(' '); // so put a space before it
        spaceNext = true;
      }
      else if(firstChar == '(')
      {
        sb.Append(' '); // if it's an opening paren, add a space before it
        spaceNext = true;
        parens++;
      }
      else if(firstChar == ')')
      {
        if(parens != 0) parens--;
      }
      else if(firstChar == '$' || firstChar == '#' || firstChar == '*' || firstChar == '&') // add spaces before these
      {
        sb.Append(' ');
        spaceNext = true;
      }
      else if(firstChar == '/') spaceNext = true;
      else if(spaceNext) spaceNext = false;
      else if((char.IsLetterOrDigit(firstChar) || string.Equals(s, "--", StringComparison.Ordinal)) &&
              sb.Length != 0)
      {
        sb.Append(' ');
      }

      sb.Append(s);
    }

    return sb.ToString();
  }

  static readonly Dictionary<string, string> spellings, swaps;
  static readonly HashSet<string> badKeywords, greetings;

  static readonly Regex wordRe = new Regex(@"\w+(?:://\S+|[\w\-'’]*)|[`~!@#$%^&*()+=\-\[\]{};:""“”,.<>/\\?|]+",
      RegexOptions.Singleline | RegexOptions.Compiled);
}


} // namespace HalBot
