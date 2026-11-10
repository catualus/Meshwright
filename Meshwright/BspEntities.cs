using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// The BSP's entity lump, read into one set of keyvalues per entity, in lump order.
    ///
    /// Quote-aware, which is the reason it exists. The lump was split with the regex
    /// <c>\{(.*?)\}</c>, which ends an entity at the first closing brace anywhere, including one inside
    /// a quoted value. Garry's Mod maps put Lua in keyvalues - lua_run's Code above all - and Lua writes
    /// every table in braces, so an entity carrying any was cut short there and the rest of it read as
    /// though it sat between entities.
    ///
    /// Tokenised the way the engine's own entity parser does it: whitespace and <c>//</c> comments
    /// between tokens are skipped, a quoted string runs to the next quote with no escapes, and a brace
    /// outside quotes is a token of its own. A broken entity - one never closed, or a key left without
    /// a value - costs only itself rather than throwing, because a nav build should not stop over a
    /// map the game may load anyway.
    /// </summary>
    internal static class BspEntities
    {
        /// <summary>
        /// Every entity in <paramref name="lump"/>. Keys compare without regard to case, as the engine
        /// treats them, and a key given twice keeps its last value.
        /// </summary>
        public static List<Dictionary<string, string>> Parse(string lump)
        {
            var entities = new List<Dictionary<string, string>>();
            Dictionary<string, string>? current = null;
            string? key = null;
            int at = 0;

            while (Next(lump, ref at, out string token, out bool quoted))
            {
                if (!quoted && token == "{")
                {
                    // An opening brace inside an entity means the previous one was never closed. It is
                    // dropped rather than merged into this one, which would mix two entities' keys.
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    key = null;
                    continue;
                }

                if (!quoted && token == "}")
                {
                    if (current is not null)
                        entities.Add(current);

                    current = null;
                    key = null;
                    continue;
                }

                // Anything between entities is not part of one.
                if (current is null)
                    continue;

                if (key is null)
                {
                    key = token;
                }
                else
                {
                    current[key] = token;
                    key = null;
                }
            }

            return entities;
        }

        /// <summary>
        /// The next token at or after <paramref name="at"/>, and whether it was quoted - which is what
        /// tells a brace that ends an entity from a value that happens to be one.
        /// </summary>
        private static bool Next(string text, ref int at, out string token, out bool quoted)
        {
            token = string.Empty;
            quoted = false;

            while (true)
            {
                while (at < text.Length && text[at] <= ' ')
                    at++;

                if (at >= text.Length)
                    return false;

                if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '/')
                {
                    while (at < text.Length && text[at] != '\n')
                        at++;

                    continue;
                }

                break;
            }

            char c = text[at];

            if (c == '"')
            {
                int start = ++at;

                while (at < text.Length && text[at] != '"')
                    at++;

                token = text[start..at];
                quoted = true;

                // Past the closing quote, when there is one. A lump that ends inside a string keeps
                // what it has, as the engine does.
                if (at < text.Length)
                    at++;

                return true;
            }

            if (c is '{' or '}')
            {
                token = c.ToString();
                at++;
                return true;
            }

            int begin = at;

            while (at < text.Length && text[at] > ' ' && text[at] is not ('{' or '}' or '"'))
                at++;

            token = text[begin..at];
            return true;
        }
    }
}
