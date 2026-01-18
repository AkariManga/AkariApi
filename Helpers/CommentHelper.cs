using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;

namespace AkariApi.Helpers
{
	public static class CommentHelper
	{
		private const string BannedWordsFileName = "comment_banned_words.txt";
		private static readonly Lazy<IReadOnlyList<Regex>> _bannedWordRegexes = new(LoadBannedWordRegexes, LazyThreadSafetyMode.ExecutionAndPublication);

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

			var text = content.Trim();
			foreach (var regex in _bannedWordRegexes.Value)
			{
				if (regex.IsMatch(text))
				{
					return true;
				}
			}

			return false;
		}

		private static IReadOnlyList<Regex> LoadBannedWordRegexes()
		{
			var path = Path.Combine(AppContext.BaseDirectory, BannedWordsFileName);
			if (!File.Exists(path))
			{
				return Array.Empty<Regex>();
			}

			var words = File.ReadAllLines(path)
				.Select(line => line.Trim())
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.Where(line => !line.StartsWith("#", StringComparison.Ordinal));

			var regexes = new List<Regex>();
			foreach (var word in words)
			{
				var normalized = NormalizeWord(word);
				if (normalized.Length == 0)
				{
					continue;
				}

				var pattern = BuildLeetPattern(normalized);
				regexes.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
			}

			return regexes;
		}

		private static string NormalizeWord(string word)
		{
			var sb = new StringBuilder();
			foreach (var c in word.ToLowerInvariant())
			{
				if (char.IsLetterOrDigit(c))
				{
					sb.Append(c);
				}
			}

			return sb.ToString();
		}

		private static string BuildLeetPattern(string word)
		{
			var sb = new StringBuilder();
			sb.Append("(?<![a-z0-9])");

			for (var i = 0; i < word.Length; i++)
			{
				var c = word[i];
				var variants = LeetVariants.TryGetValue(c, out var map) ? map : c.ToString();
				sb.Append(BuildCharClass(variants));

				if (i < word.Length - 1)
				{
					sb.Append("[\\s\\W_]*");
				}
			}

			sb.Append("(?![a-z0-9])");
			return sb.ToString();
		}

		private static string BuildCharClass(string variants)
		{
			var sb = new StringBuilder();
			sb.Append('[');

			foreach (var variant in variants)
			{
				sb.Append(EscapeCharClass(variant));
			}

			sb.Append(']');
			return sb.ToString();
		}

		private static string EscapeCharClass(char c)
		{
			return c switch
			{
				'\\' => "\\\\",
				']' => "\\]",
				'-' => "\\-",
				'^' => "\\^",
				_ => c.ToString()
			};
		}
	}
}
