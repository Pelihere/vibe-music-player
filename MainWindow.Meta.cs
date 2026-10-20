using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace PortablePlayer;

/// <summary>The "Edit Metadata" dialog: an in-app overlay (no extra window) on top of TrackMetadata.</summary>
public partial class MainWindow
{
    Track _metaTrack;
    string _metaNewCover;
    bool _metaRemoveCover;

    void InitMeta()
    {
        BtnMetaCancel.Click += (_, _) => CloseMetaEditor();
        BtnMetaSave.Click += async (_, _) => await SaveMeta();
        BtnMetaCover.Click += (_, _) => PickCover();
        BtnMetaCoverClear.Click += (_, _) =>
        {
            _metaNewCover = null; _metaRemoveCover = true;
            MetaCoverArt.Background = null;
            MetaCoverNote.Text = "The cover will be removed when you save.";
        };
        MetaOverlay.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseMetaEditor(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is System.Windows.Controls.TextBox) { _ = SaveMeta(); e.Handled = true; }
        };
        MetaOverlay.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, MetaOverlay)) CloseMetaEditor(); };
    }

    void OpenMetaEditor(Track t)
    {
        _metaTrack = t;
        _metaNewCover = null; _metaRemoveCover = false;

        MetaTitle.Text = t.Title;
        MetaArtist.Text = t.Artist == "Unknown Artist" ? "" : t.Artist;
        MetaAlbum.Text = t.Album == "Unknown Album" ? "" : t.Album;
        MetaAlbumArtist.Text = t.AlbumArtist;
        MetaGenre.Text = t.Genre;
        MetaTrackNo.Text = t.No > 0 ? t.No.ToString("00") : "";
        MetaDisc.Text = t.Disc > 0 ? t.Disc.ToString("00") : "";
        MetaYear.Text = t.Year > 0 ? t.Year.ToString() : "";
        MetaFile.Text = Path.GetFileName(t.FilePath) + (t.Edited ? "   ·   edited in Vibe" : "");
        MetaError.Text = "";
        MetaCoverNote.Text = "";
        try
        {
            var art = ArtworkService.Get(t).art;
            MetaCoverArt.Background = art == null ? null : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        }
        catch { MetaCoverArt.Background = null; }

        MetaOverlay.Visibility = Visibility.Visible;
        if (S.Animations != AnimMode.Off)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            MetaOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            if (S.Animations == AnimMode.Full)
            {
                MetaScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                MetaScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            }
        }
        MetaTitle.Focus();
        MetaTitle.SelectAll();
    }

    void CloseMetaEditor()
    {
        if (MetaOverlay.Visibility != Visibility.Visible) return;
        _metaTrack = null;
        if (S.Animations == AnimMode.Off) { MetaOverlay.Visibility = Visibility.Collapsed; return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => { if (_metaTrack == null) MetaOverlay.Visibility = Visibility.Collapsed; };
        MetaOverlay.BeginAnimation(OpacityProperty, fade);
    }

    void PickCover()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a cover image",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 240;
            bi.UriSource = new Uri(dlg.FileName);
            bi.EndInit();
            bi.Freeze();
            MetaCoverArt.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
            _metaNewCover = dlg.FileName;
            _metaRemoveCover = false;
            MetaCoverNote.Text = Path.GetFileName(dlg.FileName);
            MetaError.Text = "";
        }
        catch { MetaError.Text = "That file isn't an image Vibe can read."; }
    }

    static bool TryNumber(string text, int max, out int value)
    {
        value = 0;
        text = (text ?? "").Trim();
        int slash = text.IndexOf('/');                       // "3/12" style track numbers
        if (slash >= 0) text = text.Substring(0, slash).Trim();
        if (text.Length == 0) return true;
        return int.TryParse(text, out value) && value >= 0 && value <= max;
    }

    async Task SaveMeta()
    {
        var t = _metaTrack;
        if (t == null || !BtnMetaSave.IsEnabled) return;

        if (string.IsNullOrWhiteSpace(MetaTitle.Text)) { MetaError.Text = "The title can't be empty."; return; }
        if (!TryNumber(MetaTrackNo.Text, 999, out int no) || !TryNumber(MetaDisc.Text, 99, out int disc) || !TryNumber(MetaYear.Text, 2999, out int year))
        { MetaError.Text = "Track number, disc number and year must be numbers."; return; }

        var edit = new TrackEdit
        {
            Title = MetaTitle.Text, Artist = MetaArtist.Text, Album = MetaAlbum.Text, AlbumArtist = MetaAlbumArtist.Text,
            Genre = MetaGenre.Text, TrackNo = no, Disc = disc, Year = year,
            NewCoverFile = _metaNewCover, RemoveCover = _metaRemoveCover
        };

        BtnMetaSave.IsEnabled = false;
        try
        {
            var result = await TrackMetadata.ApplyAsync(t, edit);
            CloseMetaEditor();
            Toast("✎", result switch
            {
                MetaSaveResult.FileAndLibrary => "Metadata saved",
                MetaSaveResult.LibraryFilePending => "Saved - the file updates when it isn't playing",
                _ => "Saved in Vibe"
            });
        }
        catch (Exception ex)
        {
            Paths.Log(ex);
            MetaError.Text = "Couldn't save: " + ex.Message;
        }
        finally { BtnMetaSave.IsEnabled = true; }
    }
}
