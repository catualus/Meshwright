using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Meshwright
{
    /// <summary>
    /// Reads a mod's <c>gameinfo.txt</c> for the directories and archives it mounts content from.
    ///
    /// **This is the engine's own answer to a question that was previously guessed at.** Where a mod
    /// inherits its content from is not derivable from the directory layout - it is declared, in the
    /// <c>SearchPaths</c> block, and a mod is free to name anything it likes there. Without reading it
    /// the only option is a hardcoded list of the base games a mod is *likely* to inherit from, which
    /// is right for the common cases and silently wrong for the rest: a model that lives in a game not
    /// on the list resolves nowhere, contributes no collision, and leaves the mesh floating over
    /// whatever prop used it. Nothing reports that as an error, because "model not found" is a normal
    /// thing to say about a map built against content you do not have.
    ///
    /// Only the paths that carry content are taken. A search path entry's key is a set of roles joined
    /// with <c>+</c> - <c>game+mod</c>, <c>mod+mod_write+default_write_path</c>, <c>gamebin</c> - and
    /// only <c>game</c>, <c>mod</c> and <c>platform</c> name places a model can be read from. The write
    /// paths and the binary paths are about where the engine puts things, not where content lives.
    /// </summary>
    public static class GameInfo
    {
        /// <summary>The file, by the name the engine looks for.</summary>
        public const string FileName = "gameinfo.txt";

        /// <summary>
        /// A single mounted location: a directory to search, or a VPK to index.
        ///
        /// The distinction matters to the caller rather than here - a directory is added as a content
        /// root and a VPK is opened as an archive - and the file does not label them, so it is decided
        /// by the extension exactly as the engine decides it.
        /// </summary>
        public readonly record struct Mount(string Path, bool IsArchive);

        /// <summary>
        /// Every content location <paramref name="modDirectory"/>'s gameinfo declares, resolved to
        /// absolute paths, in the order the file lists them.
        ///
        /// Order is preserved because it is priority: the engine searches the first match and a mod that
        /// overrides a stock asset relies on being listed first. Returns nothing at all when there is no
        /// gameinfo to read or it cannot be parsed, which leaves the caller on whatever it did before -
        /// this is meant to improve on a guess, not to replace it with a failure.
        /// </summary>
        public static IReadOnlyList<Mount> Read(string modDirectory)
        {
            string path = Path.Combine(modDirectory, FileName);

            if (!File.Exists(path))
                return [];

            try
            {
                return Parse(File.ReadAllText(path), modDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        /// <summary>Exposed for tests; <see cref="Read"/> is what callers want.</summary>
        internal static IReadOnlyList<Mount> Parse(string text, string modDirectory)
        {
            var tokens = Tokenise(text);
            var mounts = new List<Mount>();

            // The mod directory, and the install root it sits in. These are what the two path macros
            // expand to, and what a bare relative entry is relative to.
            string mod = Path.GetFullPath(modDirectory);
            string install = Directory.GetParent(mod)?.FullName ?? mod;

            int at = tokens.FindIndex(t =>
                string.Equals(t, "SearchPaths", StringComparison.OrdinalIgnoreCase));

            if (at < 0 || at + 1 >= tokens.Count || tokens[at + 1] != "{")
                return [];

            for (int i = at + 2; i + 1 < tokens.Count; i += 2)
            {
                if (tokens[i] == "}")
                    break;

                // A brace where a key should be means the block is not shaped the way this expects.
                // Stopping is right: half a search path list is worse than none, because the caller
                // would treat it as complete.
                if (tokens[i] == "{" || tokens[i + 1] == "{" || tokens[i + 1] == "}")
                    break;

                if (!CarriesContent(tokens[i]))
                    continue;

                foreach (string resolved in Expand(tokens[i + 1], mod, install))
                {
                    var mount = new Mount(resolved,
                        resolved.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase));

                    if (!mounts.Contains(mount))
                        mounts.Add(mount);
                }
            }

            return mounts;
        }

        /// <summary>
        /// Whether a search path's roles include one that content is read from.
        ///
        /// Roles are joined with <c>+</c> and there are a dozen of them; the ones that matter here are
        /// <c>game</c>, <c>mod</c> and <c>platform</c>. <c>gamebin</c> is executables and
        /// <c>*_write</c> is where the engine saves, neither of which holds a model.
        /// </summary>
        private static bool CarriesContent(string key)
        {
            foreach (string role in key.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                if (role.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("platform", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Turns one search path value into the absolute paths it names.
        ///
        /// Three things happen here. The two macros the engine defines are substituted:
        /// <c>|gameinfo_path|</c> is the directory the gameinfo itself is in, and
        /// <c>|all_source_engine_paths|</c> is the install root above it. Anything still relative is
        /// taken as relative to the install root, which is what the engine does with a bare entry like
        /// <c>garrysmod</c>. And a trailing <c>*</c> is expanded to the subdirectories it stands for -
        /// that is how <c>garrysmod/addons/*</c> mounts every installed addon as its own root.
        /// </summary>
        private static IEnumerable<string> Expand(string value, string mod, string install)
        {
            string text = value.Trim();
            string root = install;

            if (text.StartsWith("|gameinfo_path|", StringComparison.OrdinalIgnoreCase))
            {
                text = text["|gameinfo_path|".Length..];
                root = mod;
            }
            else if (text.StartsWith("|all_source_engine_paths|", StringComparison.OrdinalIgnoreCase))
            {
                text = text["|all_source_engine_paths|".Length..];
            }

            // An unrecognised macro is left alone deliberately: it would resolve to a path that does not
            // exist, which the caller already skips, where stripping the bars could turn it into one
            // that does and mount something the mod never asked for.
            text = text.Replace('/', Path.DirectorySeparatorChar).Trim();

            if (text is "." or "")
                return [root];

            bool wildcard = text.EndsWith($"{Path.DirectorySeparatorChar}*") || text == "*";

            if (wildcard)
                text = text[..^1].TrimEnd(Path.DirectorySeparatorChar);

            string full;

            try { full = Path.GetFullPath(Path.Combine(root, text)); }
            catch (ArgumentException) { return []; }
            catch (PathTooLongException) { return []; }

            if (!wildcard)
                return [full];

            try
            {
                return Directory.Exists(full)
                    ? Directory.GetDirectories(full)
                    : [];
            }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        }

        /// <summary>
        /// Splits KeyValues text into quoted strings, bare words and braces.
        ///
        /// Enough of the format for this one block, not a general reader. Quoted values are taken
        /// verbatim because a path may contain spaces; <c>//</c> runs to end of line, and this file
        /// genuinely uses them - Garry's Mod's own gameinfo has a comment inside SearchPaths.
        /// </summary>
        private static List<string> Tokenise(string text)
        {
            var tokens = new List<string>();
            var word = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    Flush(tokens, word);
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }

                if (c == '"')
                {
                    Flush(tokens, word);

                    int end = text.IndexOf('"', i + 1);
                    if (end < 0) break;

                    tokens.Add(text[(i + 1)..end]);
                    i = end;
                    continue;
                }

                if (c is '{' or '}')
                {
                    Flush(tokens, word);
                    tokens.Add(c.ToString());
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    Flush(tokens, word);
                    continue;
                }

                word.Append(c);
            }

            Flush(tokens, word);
            return tokens;
        }

        private static void Flush(List<string> tokens, StringBuilder word)
        {
            if (word.Length == 0) return;

            tokens.Add(word.ToString());
            word.Clear();
        }
    }
}
