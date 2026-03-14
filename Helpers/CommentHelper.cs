using System.Buffers;
using System.Text;

namespace AkariApi.Helpers
{
	public static class CommentHelper
	{
		private const string BannedWordsFileName = "comment_banned_words.txt";
		private static readonly Lazy<BannedContentMatcher> _matcher = new(LoadMatcher, LazyThreadSafetyMode.ExecutionAndPublication);

		private static readonly IReadOnlyDictionary<char, string> LeetVariants = new Dictionary<char, string>
		{
			['a'] = "a4@",
			['b'] = "b8",
			['c'] = "c(",
			['e'] = "e3",
			['g'] = "g9",
			['i'] = "i1!|",
			['l'] = "l1|",
			['o'] = "o0",
			['s'] = "s5$",
			['t'] = "t7+",
			['z'] = "z2"
		};

		public static bool ContainsBannedContent(string? content)
		{
			if (string.IsNullOrWhiteSpace(content))
			{
				return false;
			}

			return _matcher.Value.Contains(content);
		}

		private static BannedContentMatcher LoadMatcher()
		{
			var path = Path.Combine(AppContext.BaseDirectory, BannedWordsFileName);
			if (!File.Exists(path))
			{
				return BannedContentMatcher.Empty;
			}

			var words = new List<string>();
			foreach (var line in File.ReadLines(path))
			{
				var trimmed = line.Trim();
				if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
				{
					continue;
				}

				var normalized = NormalizeWord(trimmed);
				if (normalized.Length != 0)
				{
					words.Add(normalized);
				}
			}


			return BannedContentMatcher.Create(words, LeetVariants);
		}

		private static string NormalizeWord(string word)
		{
			var sb = new StringBuilder(word.Length);
			foreach (var c in word)
			{
				var lower = char.ToLowerInvariant(c);
				if (char.IsAsciiLetterOrDigit(lower))
				{
					sb.Append(lower);
				}
			}

			return sb.ToString();
		}

		private sealed class BannedContentMatcher
		{
			public static readonly BannedContentMatcher Empty = new(new RadixNode(false), CreateEmptyCanonicalOptions());

			private readonly RadixNode _root;
			private readonly string?[] _canonicalOptionsByAscii;

			private BannedContentMatcher(RadixNode root, string?[] canonicalOptionsByAscii)
			{
				_root = root;
				_canonicalOptionsByAscii = canonicalOptionsByAscii;
			}

			public static BannedContentMatcher Create(IEnumerable<string> words, IReadOnlyDictionary<char, string> leetVariants)
			{
				var trieRoot = new TrieNode();
				var hasWords = false;
				foreach (var word in words)
				{
					hasWords = true;
					InsertWord(trieRoot, word);
				}

				if (!hasWords)
				{
					return Empty;
				}

				return new BannedContentMatcher(CompressTrie(trieRoot), BuildCanonicalOptions(leetVariants));
			}

			public bool Contains(string content)
			{
				if (_root.Edges.Count == 0)
				{
					return false;
				}

				var currentStates = ArrayPool<MatchState>.Shared.Rent(32);
				var nextStates = ArrayPool<MatchState>.Shared.Rent(32);

				try
				{
					var currentCount = 0;
					var previousIsBoundary = true;
					var text = content.AsSpan();

					for (var index = 0; index < text.Length; index++)
					{
						var currentChar = ToSearchChar(text[index]);
						var currentIsBoundary = !char.IsAsciiLetterOrDigit(currentChar);
						var nextIsBoundary = index + 1 >= text.Length || !char.IsAsciiLetterOrDigit(ToSearchChar(text[index + 1]));
						var canonicalOptions = GetCanonicalOptions(currentChar);
						var nextCount = 0;

						for (var stateIndex = 0; stateIndex < currentCount; stateIndex++)
						{
							if (AdvanceState(currentStates[stateIndex], currentIsBoundary, canonicalOptions, nextIsBoundary, ref nextStates, ref nextCount))
							{
								return true;
							}
						}

						if (previousIsBoundary && canonicalOptions is not null && AdvanceNode(_root, canonicalOptions, nextIsBoundary, ref nextStates, ref nextCount))
						{
							return true;
						}

						(currentStates, nextStates) = (nextStates, currentStates);
						currentCount = nextCount;
						previousIsBoundary = currentIsBoundary;
					}

					return false;
				}
				finally
				{
					ArrayPool<MatchState>.Shared.Return(currentStates, clearArray: true);
					ArrayPool<MatchState>.Shared.Return(nextStates, clearArray: true);
				}
			}

			private static void InsertWord(TrieNode root, string word)
			{
				var node = root;
				foreach (var c in word)
				{
					if (!node.Children.TryGetValue(c, out var child))
					{
						child = new TrieNode();
						node.Children[c] = child;
					}

					node = child;
				}

				node.IsTerminal = true;
			}

			private static RadixNode CompressTrie(TrieNode trieNode)
			{
				var edges = new Dictionary<char, RadixEdge>(trieNode.Children.Count);
				foreach (var (firstChar, childTrieNode) in trieNode.Children)
				{
					var label = new StringBuilder();
					label.Append(firstChar);

					var cursor = childTrieNode;
					while (!cursor.IsTerminal && cursor.Children.Count == 1)
					{
						foreach (var (nextChar, nextChild) in cursor.Children)
						{
							label.Append(nextChar);
							cursor = nextChild;
							break;
						}
					}

					edges[firstChar] = new RadixEdge(label.ToString(), CompressTrie(cursor));
				}

				return new RadixNode(trieNode.IsTerminal, edges);
			}

			private static string?[] CreateEmptyCanonicalOptions()
			{
				return new string?[128];
			}

			private static string?[] BuildCanonicalOptions(IReadOnlyDictionary<char, string> leetVariants)
			{
				var mappings = new HashSet<char>[128];

				for (var c = 'a'; c <= 'z'; c++)
				{
					AddCanonicalMapping(mappings, c, c);
				}

				for (var c = '0'; c <= '9'; c++)
				{
					AddCanonicalMapping(mappings, c, c);
				}

				foreach (var (canonical, variants) in leetVariants)
				{
					foreach (var variant in variants)
					{
						var searchChar = ToSearchChar(variant);
						AddCanonicalMapping(mappings, searchChar, canonical);
					}
				}

				var result = new string?[128];
				for (var i = 0; i < mappings.Length; i++)
				{
					if (mappings[i] is null)
					{
						continue;
					}

					var ordered = mappings[i]!
						.OrderBy(static c => c)
						.ToArray();

					result[i] = new string(ordered);
				}

				return result;
			}

			private static void AddCanonicalMapping(HashSet<char>[] mappings, char input, char canonical)
			{
				if (input >= mappings.Length)
				{
					return;
				}

				mappings[input] ??= new HashSet<char>();
				mappings[input]!.Add(canonical);
			}

			private static bool AdvanceState(MatchState state, bool currentIsBoundary, string? canonicalOptions, bool nextIsBoundary, ref MatchState[] nextStates, ref int nextCount)
			{
				if (currentIsBoundary)
				{
					AddState(ref nextStates, ref nextCount, state);
				}

				if (canonicalOptions is null)
				{
					return false;
				}

				var edge = state.Edge;
				if (edge is null)
				{
					return AdvanceNode(state.Node, canonicalOptions, nextIsBoundary, ref nextStates, ref nextCount);
				}

				var expected = edge.Label[state.Offset];
				if (!ContainsCanonicalOption(canonicalOptions, expected))
				{
					return false;
				}

				if (state.Offset + 1 == edge.Label.Length)
				{
					var nodeState = MatchState.ForNode(edge.Child);
					AddState(ref nextStates, ref nextCount, nodeState);
					return edge.Child.IsTerminal && nextIsBoundary;
				}

				AddState(ref nextStates, ref nextCount, state.Advance());
				return false;
			}

			private static bool AdvanceNode(RadixNode node, string canonicalOptions, bool nextIsBoundary, ref MatchState[] nextStates, ref int nextCount)
			{
				foreach (var canonical in canonicalOptions)
				{
					if (!node.Edges.TryGetValue(canonical, out var edge))
					{
						continue;
					}

					if (edge.Label.Length == 1)
					{
						var nodeState = MatchState.ForNode(edge.Child);
						AddState(ref nextStates, ref nextCount, nodeState);
						if (edge.Child.IsTerminal && nextIsBoundary)
						{
							return true;
						}
						continue;
					}

					AddState(ref nextStates, ref nextCount, MatchState.ForEdge(edge, 1));
				}

				return false;
			}

			private string? GetCanonicalOptions(char c)
			{
				return c < _canonicalOptionsByAscii.Length ? _canonicalOptionsByAscii[c] : null;
			}

			private static bool ContainsCanonicalOption(string canonicalOptions, char expected)
			{
				foreach (var canonical in canonicalOptions)
				{
					if (canonical == expected)
					{
						return true;
					}
				}

				return false;
			}

			private static void AddState(ref MatchState[] buffer, ref int count, MatchState state)
			{
				for (var i = 0; i < count; i++)
				{
					if (buffer[i].Equals(state))
					{
						return;
					}
				}

				if (count == buffer.Length)
				{
					GrowStateBuffer(ref buffer);
				}

				buffer[count++] = state;
			}

			private static void GrowStateBuffer(ref MatchState[] buffer)
			{
				var larger = ArrayPool<MatchState>.Shared.Rent(buffer.Length * 2);
				Array.Copy(buffer, larger, buffer.Length);
				ArrayPool<MatchState>.Shared.Return(buffer, clearArray: true);
				buffer = larger;
			}

			private static char ToSearchChar(char c)
			{
				return c <= 127 ? char.ToLowerInvariant(c) : c;
			}

			private sealed class TrieNode
			{
				public bool IsTerminal { get; set; }
				public Dictionary<char, TrieNode> Children { get; } = new();
			}

			private sealed class RadixNode
			{
				public RadixNode(bool isTerminal)
					: this(isTerminal, new Dictionary<char, RadixEdge>())
				{
				}

				public RadixNode(bool isTerminal, Dictionary<char, RadixEdge> edges)
				{
					IsTerminal = isTerminal;
					Edges = edges;
				}

				public bool IsTerminal { get; }
				public Dictionary<char, RadixEdge> Edges { get; }
			}

			private sealed class RadixEdge
			{
				public RadixEdge(string label, RadixNode child)
				{
					Label = label;
					Child = child;
				}

				public string Label { get; }
				public RadixNode Child { get; }
			}

			private readonly struct MatchState : IEquatable<MatchState>
			{
				private MatchState(RadixNode node, RadixEdge? edge, int offset)
				{
					Node = node;
					Edge = edge;
					Offset = offset;
				}

				public RadixNode Node { get; }
				public RadixEdge? Edge { get; }
				public int Offset { get; }

				public static MatchState ForNode(RadixNode node)
				{
					return new MatchState(node, null, 0);
				}

				public static MatchState ForEdge(RadixEdge edge, int offset)
				{
					return new MatchState(edge.Child, edge, offset);
				}

				public MatchState Advance()
				{
					return new MatchState(Node, Edge, Offset + 1);
				}

				public bool Equals(MatchState other)
				{
					return ReferenceEquals(Node, other.Node)
						&& ReferenceEquals((object?)Edge, other.Edge)
						&& Offset == other.Offset;
				}
			}
		}
	}
}
