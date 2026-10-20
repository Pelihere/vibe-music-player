using System;
using System.Windows;
using System.Windows.Controls;

namespace PortablePlayer;

public partial class MainWindow
{
    static readonly (string Key, string Label)[] LibrarySortKeys =
    {
        ("Default", "Default order"), ("Title", "Title"), ("Artist", "Artist"), ("Album", "Album"),
        ("Added", "Date Added"), ("Duration", "Duration")
    };

    static readonly (string Key, string Label)[] ArtistSortKeys =
    {
        ("Popularity", "Popularity"), ("Title", "Title"), ("Album", "Album"), ("Duration", "Duration")
    };

    void ShowLibrarySort() =>
        ShowSortMenu(BtnSort, LibrarySortKeys, S.LibrarySort, S.LibrarySortDesc, (k, d) =>
        {
            S.LibrarySort = k; S.LibrarySortDesc = d;
            S.SaveSoon();
            RefreshLibrary();
        });

    void ShowArtistSort() =>
        ShowSortMenu(BtnArtSort, ArtistSortKeys, _artSort, _artSortDesc, (k, d) =>
        {
            _artSort = k; _artSortDesc = d;
            ShowPopular();
        });

    /// <summary>Compact "Sort By" menu: the sort key, then its direction. Same look as every other menu.</summary>
    void ShowSortMenu(Button anchor, (string Key, string Label)[] keys, string curKey, bool curDesc, Action<string, bool> apply)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        StyleMenu(m);

        foreach (var (key, label) in keys)
        {
            string k = key;
            m.Items.Add(Item(k == curKey ? "✓" : null, label, () => apply(k, curDesc)));
        }

        if (curKey != "Default")
        {
            m.Items.Add(Sep());
            var (asc, desc) = Sorting.Directions(curKey);
            m.Items.Add(Item(!curDesc ? "✓" : null, asc, () => apply(curKey, false)));
            m.Items.Add(Item(curDesc ? "✓" : null, desc, () => apply(curKey, true)));
        }
        m.IsOpen = true;
    }
}
