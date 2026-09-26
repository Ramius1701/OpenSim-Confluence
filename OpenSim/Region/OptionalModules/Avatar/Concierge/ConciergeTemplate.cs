/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Text;

namespace OpenSim.Region.OptionalModules.Avatar.Concierge
{
    /// <summary>
    /// Who an arriving avatar is, as far as picking a greeting goes.
    /// </summary>
    public enum ConciergeAudience
    {
        /// <summary>A returning resident of this grid.</summary>
        Resident,
        /// <summary>A local account younger than the "new resident" window.</summary>
        New,
        /// <summary>A local account carrying the Trial Member badge.</summary>
        Trial,
        /// <summary>A visitor from another grid.</summary>
        Visitor
    }

    /// <summary>
    /// Welcome/rules text handling, kept free of any OpenSim types so it can
    /// be exercised on its own.
    ///
    /// A template is plain text, one chat line per line. It may be split into
    /// audience sections with header lines of the form <c>[default]</c>,
    /// <c>[new]</c>, <c>[trial]</c> or <c>[hg]</c>; text before the first
    /// header belongs to <c>default</c>, so a template with no headers at all
    /// (every pre-existing welcome file) behaves exactly as before. Any other
    /// <c>[something]</c> line is ordinary text.
    /// </summary>
    public static class ConciergeTemplate
    {
        // A chat line the viewer accepts tops out at 1023 bytes; stay under it
        // even after multi-byte characters.
        public const int MaxLineChars = 900;

        private static readonly HashSet<string> s_sections =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default", "new", "trial", "hg" };

        /// <summary>
        /// Section names to try, most specific first, for an audience.
        /// </summary>
        public static string[] SectionsFor(ConciergeAudience audience)
        {
            switch (audience)
            {
                case ConciergeAudience.Visitor: return new[] { "hg", "default" };
                case ConciergeAudience.Trial:   return new[] { "trial", "new", "default" };
                case ConciergeAudience.New:     return new[] { "new", "default" };
                default:                        return new[] { "default" };
            }
        }

        public static Dictionary<string, List<string>> ParseSections(string text)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text))
                return result;

            string current = "default";
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();

                if (line.Length > 2 && line[0] == '[' && line[line.Length - 1] == ']')
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (s_sections.Contains(name))
                    {
                        current = name.ToLowerInvariant();
                        continue;
                    }
                }

                if (line.Length == 0)
                    continue;

                if (!result.TryGetValue(current, out List<string> lines))
                {
                    lines = new List<string>();
                    result[current] = lines;
                }
                lines.Add(line);
            }

            return result;
        }

        /// <summary>
        /// The lines to send an audience: the first of its sections that has
        /// any text, or nothing.
        /// </summary>
        public static List<string> SelectLines(string text, ConciergeAudience audience)
        {
            Dictionary<string, List<string>> sections = ParseSections(text);
            foreach (string name in SectionsFor(audience))
            {
                if (sections.TryGetValue(name, out List<string> lines) && lines.Count > 0)
                    return lines;
            }

            return new List<string>();
        }

        /// <summary>
        /// Replace <c>{token}</c> occurrences using <paramref name="lookup"/>,
        /// which returns null for a name it does not know. Unknown tokens and
        /// stray braces are left exactly as written, so free text containing
        /// braces can never make a greeting fail (the old format-string
        /// approach threw on them).
        /// </summary>
        public static string Expand(string line, Func<string, string> lookup)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf('{') < 0)
                return Limit(line);

            var sb = new StringBuilder(line.Length + 32);
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '{')
                {
                    int end = line.IndexOf('}', i + 1);
                    if (end > i + 1)
                    {
                        string value = lookup(line.Substring(i + 1, end - i - 1).Trim());
                        if (value != null)
                        {
                            sb.Append(value);
                            i = end + 1;
                            continue;
                        }
                    }
                }

                sb.Append(c);
                i++;
            }

            return Limit(sb.ToString());
        }

        public static string Limit(string line)
        {
            if (line == null)
                return string.Empty;

            return line.Length <= MaxLineChars ? line : line.Substring(0, MaxLineChars);
        }
    }
}
