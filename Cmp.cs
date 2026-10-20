using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PortablePlayer;

/// <summary>
/// One place for text matching. Everything that compares names (songs, artists, albums, playlists, search, cache keys)
/// goes through here so capitalisation and stray spaces never create "different" entries.
/// Displayed metadata is never changed - only the comparison is relaxed.
/// </summary>
public static class Cmp
{
    public static readonly StringComparer Ic = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>Trim + collapse inner whitespace (keeps the original capitalisation).</summary>
    public static string Norm(string s) =>
        string.IsNullOrWhiteSpace(s) ? "" : string.Join(' ', s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Stable lower-case key for dictionaries, caches and grouping.</summary>
    public static string Key(string s) => Norm(s).ToLowerInvariant();

    public static bool Eq(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.CurrentCultureIgnoreCase);

    public static bool Has(string s, string q) => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Same artist ignoring case, spacing and a leading "The" ("the beatles" == "Beatles").</summary>
    public static bool SameArtist(string a, string b) => ArtistKey(a) == ArtistKey(b) && ArtistKey(a).Length > 0;

    public static string ArtistKey(string s)
    {
        string k = Key(s);
        return k.StartsWith("the ", StringComparison.Ordinal) ? k.Substring(4) : k;
    }

    static readonly Regex Brackets = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

    /// <summary>Song-title key for matching online titles to local files: lower-case, no (remastered)-style suffixes or punctuation.</summary>
    public static string TitleKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        string t = Brackets.Replace(s, " ");
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && Regex.IsMatch(t.Substring(dash), @"(?i)remaster|version|mix|edit|live|mono|stereo")) t = t.Substring(0, dash);
        var sb = new StringBuilder();
        foreach (char c in t.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
