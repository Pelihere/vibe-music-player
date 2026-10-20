using System.Collections.Generic;
using System.Linq;

namespace PortablePlayer;

/// <summary>Sorting of song lists. Text keys compare ignoring case; "Default" keeps the list's own order.</summary>
public static class Sorting
{
    public static IEnumerable<Track> Apply(IEnumerable<Track> src, string key, bool desc)
    {
        IOrderedEnumerable<Track> o;
        switch (key)
        {
            case "Title":
                o = desc ? src.OrderByDescending(t => t.Title, Cmp.Ic) : src.OrderBy(t => t.Title, Cmp.Ic);
                return o.ThenBy(t => t.Artist, Cmp.Ic);
            case "Artist":
                o = desc ? src.OrderByDescending(t => t.Artist, Cmp.Ic) : src.OrderBy(t => t.Artist, Cmp.Ic);
                return o.ThenBy(t => t.Album, Cmp.Ic).ThenBy(t => t.Disc).ThenBy(t => t.No).ThenBy(t => t.Title, Cmp.Ic);
            case "Album":
                o = desc ? src.OrderByDescending(t => t.Album, Cmp.Ic) : src.OrderBy(t => t.Album, Cmp.Ic);
                return o.ThenBy(t => t.Disc).ThenBy(t => t.No).ThenBy(t => t.Title, Cmp.Ic);
            case "Added":
                o = desc ? src.OrderByDescending(t => t.Added) : src.OrderBy(t => t.Added);
                return o.ThenBy(t => t.Title, Cmp.Ic);
            case "Duration":
                o = desc ? src.OrderByDescending(t => t.DurationSec) : src.OrderBy(t => t.DurationSec);
                return o.ThenBy(t => t.Title, Cmp.Ic);
            default:
                return src;
        }
    }

    /// <summary>Label of the direction items for a key.</summary>
    public static (string Asc, string Desc) Directions(string key) => key switch
    {
        "Title" or "Artist" or "Album" => ("A → Z", "Z → A"),
        "Popularity" => ("Most popular first", "Least popular first"),
        _ => ("Ascending", "Descending")
    };
}
