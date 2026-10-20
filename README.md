# Vibe - portable music player (Windows 10 2004+ / 11, 64-bit)

WPF / .NET 8 music player with a shared playback core, a main player window, a floating
desktop panel (vinyl + artwork-based colors), tray icon, global hotkeys and system media controls.

## Build
Requirements: Windows 10 (2004 or newer) or 11, x64 and the .NET SDK 8 or later (the SDK, not just the runtime): https://aka.ms/dotnet-download

    build.bat

Output: `publish\PortableMusicPlayer.exe` (self-contained, no .NET install needed to run it).
The first build needs internet access to restore the TagLibSharp package.

## Portable
Settings, library, queue and the crash log live in a `data` folder next to the exe
(falls back to `%LOCALAPPDATA%\PortableMusicPlayer` if that folder is read-only).
Copy the exe together with `data` to move everything. Paths on the same drive are stored relative.

## What's in the UI
- **Gramophone** (sidebar): a floating record with a tonearm and needle - no turntable base. The needle follows
  currentTime / duration; changing songs lifts the needle, slows the record, swaps records and lowers the needle again.
  The same Gramophone is used by the floating desktop panel. It has a fixed look and is never recolored by themes.
- **Library / Favorites / Artists / Albums / Genres / Playlists**: rows with cover, title, artist, album, duration, a clear
  "playing" state and a **⋮ button** on every row (same menu as right-click: play, queue, playlist, show artist/album,
  **Edit Metadata**, remove). **Sort By** sits next to Play / Shuffle.
- **Artist profile** (click the cover of the playing song, or "Show Artist"): a transparent, waist-up artist cutout stands in
  front of the artist's huge name; vertical navigation on the left (ABOUT / OVERVIEW / RELATED ARTISTS), content on the right.
  ABOUT and RELATED ARTISTS dim and soften the artist so the information leads.
  Overview: popular tracks and "You might also like" - songs you own are tagged LOCAL, the rest ONLINE with their real state
  (PREVIEW / LINK / NO PLAYBACK SOURCE) - plus latest releases. Online songs play as 30-second previews when a provider has one.
  There is no download: none of the free services offers an authorized one.
- **Edit Metadata**: in-app dialog (title, artist, album, album artist, track, disc, genre, year, cover). Edits are always
  kept in the Vibe library and also written into the audio file when possible (a song that is playing is written later).
- **Search** (title bar): songs, artists, albums and playlists, grouped, live while typing, case-insensitive. Esc clears.
- **Up Next**: drag the handle to reorder, X removes, double-click plays; drag songs onto "Up Next" in the sidebar.
- **Mini player**: on every page except the Gramophone. Its cover opens the artist profile; the right-hand button opens the Gramophone.
- **Themes** (main window only, neo-brutalist style: bold black outlines, hard offset shadows, grid-paper background, cross-hatched cards): Dark Teal (default), Dark Blue, Plasma, Terracotta.
- **Settings** is a page inside the main window (General, Playback, Library, Appearance, Desktop Panel, Hotkeys,
  Notifications, Storage, About).
- Minimize = normal minimize (music keeps playing). Close (X) = hide to the tray; the desktop panel then appears.

## Artist pictures (local database first, free APIs as fallback)
The artist page's hero picture comes from a folder you fill yourself (checked FIRST): `data\artist-pictures` (Settings → Library → Artist pictures → Open folder).
- Save one cutout per artist, named after the artist: `Frank Ocean.png`, `The Weeknd.png`, `Arctic Monkeys.png`.
  Transparent PNG / WebP cutouts work best; JPG also works and is shown as a normal picture.
- Vibe only **matches the artist name to the file name** and shows the picture. Capitalization, spacing, punctuation, accents and a leading
  "The" are ignored ("The Weeknd" = "the weeknd" = `Weeknd.png`).
- More pictures of the same artist rotate on the page (up to 5, every 6 s, crossfade): `Frank Ocean (2).png`, `Frank Ocean_3.png`, or a
  sub-folder named `Frank Ocean` with the pictures inside. One picture = no rotation. The rotation runs only while the page is visible.
- Only when the folder has nothing for an artist, Vibe asks the free picture APIs (TheAudioDB, Wikimedia Commons, Wikipedia, Deezer),
  keeps up to 5 big-enough pictures unmodified, and remembers the set for 7 days. Your own files always win.
- Nothing found anywhere: the page shows its typographic fallback and a hint with the exact file name to use (⋯ → Open artist pictures folder).
- Nothing is downloaded or processed for these pictures, so they also work offline and never depend on any music service.
  New files are picked up the next time the artist page is opened.

## Discovery data (free services, no scraping)
- **MusicBrainz**: artist / recording identification (MBIDs), type, country, years, tags. **Wikipedia / Wikidata**: biography fallback, portrait photos.
- **Last.fm** (free key in Settings → Library): `track.getSimilar` (by MBID, then names, then autocorrected / normalized), biographies, top tracks, similar artists.
- **ListenBrainz** similarity by MBID, **Deezer** (no key: top tracks, releases, related artists, artist radio, 30 s previews) as secondary sources.
- **Recommendations never depend on an audio provider.** They are metadata. Pipeline: normalize → resolve MBIDs → Last.fm → MusicBrainz/ListenBrainz
  → Deezer → saved results → songs from your library. Playback is a separate, optional step.
- Errors are specific (offline / can't reach a service / key rejected / rate limit / nothing found) and every request is written to
  `data\discovery.log` (Settings → Storage → Open log). Settings → Library → "Test recommendations" runs a known song through every provider.
- Results are cached in `data\cache` (profiles 7 days, recommendations 3 days, pictures 30 days, cutouts 60 days); Settings → Storage clears them.

## Use
- Add music: sidebar "Add music folder", or drop files / folders onto the window.
- Closing or minimizing the player sends it to the tray. While it is hidden the desktop panel shows.
- Panel: drag to move (unless locked), mouse wheel = volume, right-click for the menu.
  Dock, size, opacity (20-100%), lock, always-on-top, auto-hide and animation mode are in Settings.
- Formats: mp3, wav, wma, aac/m4a, flac, aiff (Windows Media codecs; no ogg/opus).

## Default global hotkeys (changeable in Settings)
| Action | Hotkey |
|---|---|
| Show / hide desktop panel | Ctrl+Alt+D |
| Play / pause | Ctrl+Alt+Space |
| Previous / next | Ctrl+Alt+Left / Right |
| Show / hide main player | Ctrl+Alt+M |
| Lock / unlock panel | Ctrl+Alt+L |

## Known limits
No manual panel resize (size presets only), no real acrylic blur, no remove-from-queue,
no file-argument forwarding between instances, mixed-DPI placement is approximate.

## Themes
Dark Teal (default), Dark Blue, Plasma, Terracotta.
Settings -> Appearance shows them as colour cards.

## Online previews
Clicking an online song in Popular Tracks / You might also like downloads its 30 s preview (cache\previews) and plays that file.
If it fails you get a message and your current playback stays untouched.

## Fallback sources and SoundCloud
- When Deezer is unreachable or doesn't know an artist: popular tracks, releases and 30 s previews come from **iTunes Search** (no key);
  missing biography / genre / start year come from **TheAudioDB** (free key built in). Each source fails on its own.
- **SoundCloud** (optional): enter your own app's client ID + secret (Settings -> Library -> SoundCloud, from soundcloud.com/you/apps).
  Then SoundCloud uploaders get their profile text and public tracks (links only; no audio is played or downloaded from SoundCloud).
- New songs named "Artist - Title" without an artist tag (typical for SoundCloud downloads) are split into artist and title when added.

## Statistics
Colour tiles (songs, library length, artists, albums, favorites, plays) and cards with bar charts: most played, top artists, genres,
decades, library growth (6 months) and recently played. Songs and artists in the cards are clickable.

## Lyrics
Sidebar -> **Lyrics** shows the lyrics of the current song. Timed lyrics (LRC) follow the music: the current line is highlighted and kept in the middle,
clicking a line jumps there (scrolling by hand pauses the auto-follow for 4 s). Lyrics without times are shown as text.
Sources, in this order (local always wins):
1. Lyrics you imported (button "Import .lrc / .txt") - copied to `data\lyrics`, never into your music folder.
2. A `.lrc` or `.txt` with the same name next to the song.
3. Lyrics embedded in the file's tag.
4. Online cache (`data\cache\lyrics`), then LRCLIB (lrclib.net, free, no key) - only when online info is on in Settings. "Find online" forces a new lookup.
"Remove mine" deletes only your imported copy.

## Musical hover effect ("score")
While the pointer (or keyboard focus) is on a navigation item, button, tab, segment, track/playlist/search/artist row or card, a soft layer of
little twinkling star sparkles and slowly flowing notes fades in BEHIND its content, all at once (no sweep, no staff lines). When the pointer leaves,
the whole layer fades out.
- Shared implementation: `ScoreHover.cs` -> `ScoreLayer`, placed in the control templates before the content (text and icons stay on top, readable).
  It is never hit-testable, so clicks, drag and drop, selection and keyboard navigation are untouched.
- Settings -> Appearance -> **Musical hover effect**: Off / Slow (default) / Medium / Fast (speed of the flowing notes and the twinkle).
- Colour = theme accent (the "on accent" colour on accent-filled buttons). Note size follows the element height; width changes note count and spacing.
- Animations Off/Reduced or Windows "show animations" off: no flowing or twinkling; the layer just appears (Off) / fades quickly (Reduced).
- Not shown in pop-up menus and on small icon buttons.
