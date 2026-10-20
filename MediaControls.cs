using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using WMedia = global::Windows.Media;
using WStreams = global::Windows.Storage.Streams;

namespace PortablePlayer;

/// <summary>System media transport controls (media keys, lock screen / volume flyout). Fully optional: any failure is swallowed.</summary>
public sealed class MediaControls : IDisposable
{
    readonly PlaybackCore _core;
    WMedia.SystemMediaTransportControls _smtc;
    MemoryStream _thumb;

    public MediaControls(IntPtr hwnd, PlaybackCore core)
    {
        _core = core;
        try
        {
            _smtc = WMedia.SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += OnButton;
            _core.Changed += OnCore;
            UpdateStatus();
            UpdateTrack();
        }
        catch (Exception ex) { Paths.Log(ex); _smtc = null; }
    }

    void OnButton(WMedia.SystemMediaTransportControls s, WMedia.SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        var btn = e.Button;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (btn)
            {
                case WMedia.SystemMediaTransportControlsButton.Play: _core.Play(); break;
                case WMedia.SystemMediaTransportControlsButton.Pause: _core.Pause(); break;
                case WMedia.SystemMediaTransportControlsButton.Stop: _core.Stop(); break;
                case WMedia.SystemMediaTransportControlsButton.Next: _core.Next(); break;
                case WMedia.SystemMediaTransportControlsButton.Previous: _core.Previous(); break;
            }
        }));
    }

    void OnCore(CoreChange c)
    {
        if (_smtc == null) return;
        try
        {
            if (c == CoreChange.State) UpdateStatus();
            else if (c == CoreChange.Track) UpdateTrack();
        }
        catch (Exception ex) { Paths.Log(ex); }
    }

    void UpdateStatus()
    {
        _smtc.PlaybackStatus = _core.State switch
        {
            PlayState.Playing => WMedia.MediaPlaybackStatus.Playing,
            PlayState.Paused => WMedia.MediaPlaybackStatus.Paused,
            PlayState.Stopped => WMedia.MediaPlaybackStatus.Stopped,
            PlayState.Loading => WMedia.MediaPlaybackStatus.Changing,
            _ => _core.Current == null ? WMedia.MediaPlaybackStatus.Closed : WMedia.MediaPlaybackStatus.Stopped
        };
    }

    void UpdateTrack()
    {
        var t = _core.Current;
        var u = _smtc.DisplayUpdater;
        if (t == null) { u.ClearAll(); u.Update(); return; }
        u.Type = WMedia.MediaPlaybackType.Music;
        u.MusicProperties.Title = t.Title ?? "";
        u.MusicProperties.Artist = t.Artist ?? "";
        u.MusicProperties.AlbumTitle = t.Album ?? "";
        u.Thumbnail = null;
        try
        {
            if (_core.Artwork != null)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(_core.Artwork));
                _thumb?.Dispose();
                _thumb = new MemoryStream();
                enc.Save(_thumb);
                _thumb.Position = 0;
                u.Thumbnail = WStreams.RandomAccessStreamReference.CreateFromStream(_thumb.AsRandomAccessStream());
            }
        }
        catch (Exception ex) { Paths.Log(ex); }
        u.Update();
    }

    public void Dispose()
    {
        try
        {
            _core.Changed -= OnCore;
            if (_smtc != null) { _smtc.ButtonPressed -= OnButton; _smtc.IsEnabled = false; }
        }
        catch { }
        _thumb?.Dispose();
    }
}
