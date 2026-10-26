# UI design: the Silo look

Granary looks like Silo's web client, not like a stock Windows app (decided 2026-10-24). The values
below were read from the web client of a Silo server. Light mode is not offered: Silo itself is dark.
Window chrome stays Windows: caption buttons, keyboard and focus behaviour, accessibility.

## Tokens (`src/Granary.App/Themes/Silo.xaml`)

| Token | Value | Use |
|---|---|---|
| `SiloBackgroundBrush` | `#141417` | Window, pages, sidebar |
| `SiloSurfaceBrush` | `#1C1C20` | Card and artwork placeholders, inputs |
| `SiloSurfaceRaisedBrush` | `#232328` | Hover and selected surfaces |
| `SiloBorderBrush` | `#2A2A2F` at 70 % | Dividers, outlines |
| `SiloForegroundBrush` | `#E8E8EC` | Text |
| `SiloMutedBrush` | `#A4A4AE` | Secondary text, overlines, section labels |
| `SiloPrimaryBrush` | `#EF4444` | The only accent: Play, progress, selection, focus |
| `SiloBadgeBackgroundBrush` / `SiloBadgeBorderBrush` | `#14141E` at 70 % / white at 15 % | Pills on artwork |
| `SiloCardCornerRadius` | 16 | Cards, artwork, sidebar items |
| `SiloPillCornerRadius` | 999 | Badges, chips, buttons |

The Fluent theme brushes (accent, text fills, page and navigation backgrounds) point at these values,
so stock controls fit without per-page styling.

Typeface: Plus Jakarta Sans (SIL OFL 1.1), one file per weight in `Assets/Fonts`. XAML only loads the
face of the file it names, so use the `SiloFont<Weight>` resources together with the matching
`FontWeight`; never fake a weight on the regular face.

| Style | Font | Use |
|---|---|---|
| `SiloDisplayTextStyle` | 56 / ExtraBold / tight | Title fallback on detail pages (when there is no logo), "Results for …" |
| `SiloPageTitleTextStyle` | 32 / Bold | Page titles |
| `SiloRowTitleTextStyle` | 20 / SemiBold | Row titles ("Continue Watching") |
| `SiloBodyTextStyle` | 15 / Regular, line height 26 | Overviews |
| `SiloCardTitleTextStyle` | 14 / SemiBold, one line | Card titles |
| `SiloOverlineTextStyle` | 11 / Medium, spaced, muted | Card subtitles ("2021 · SERIES"); text arrives upper-cased |
| `SiloSectionLabelTextStyle` | 11 / SemiBold, wide spacing, muted | "LIBRARIES", "EXPLORE ALL", breadcrumbs |
| `SiloCaptionTextStyle` | 12 / Medium, muted | Third lines, meta text |
| `SiloBadgeTextStyle` | 10 / SemiBold, white | Badge text |

Buttons: `SiloPrimaryButtonStyle` (red pill: Play, Resume), `SiloSecondaryButtonStyle` (dark pill:
Mark watched), `SiloIconButtonStyle` (round: favourite, more), `SiloLinkButtonStyle` ("EXPLORE ALL").
Chips: `SiloChipStyle` (year, rating, runtime), `SiloAccentChipStyle` (1080P, EAC3).

The player draws on the picture, not on these surfaces: `SiloPlayer*` brushes (white text, scrims, the
seek track), `SiloPlayerPlayButtonStyle` (red disc), `SiloPlayerDiscButtonStyle` (glass disc: back,
skips), `SiloPlayerIconButtonStyle` (bare icon: mute, full screen) and `SiloPlayerTextButtonStyle`.

## Components (`src/Granary.App/Controls`)

- `MediaCardView` draws one `MediaCard` (`Granary.Core.ViewModels.Cards`): 2:3 poster or 16:9
  landscape artwork with radius 16, resolution badge top left, audio and status badges top right,
  rating bottom left, a watched mark bottom right, a 3 px red progress bar, then title, subtitle and an
  optional caption. Without artwork, or when it fails (expired signature), the title stands in.
- `MediaRow`: row title, optional "EXPLORE ALL", horizontally scrolling cards (posters 185 px,
  landscape 314 px, 20 px apart), arrows for mouse users. Sideways rows (`MediaRow`, `DetailShelf`, search's
  people) are `ScrollView`s that ignore the mouse wheel (`WheelScrollsPage`): the wheel scrolls the page, Shift or a
  tilting wheel the row. A `ScrollViewer` row that only caught the wheel's routed event still took quick turns
  sideways: WinUI handles the wheel as an inertial manipulation.
- `MediaGrid`: cards filling the width (posters at least 185 px, landscape at least 280 px), meant to sit
  in the page's own `ScrollViewer`; `LoadMoreCommand` runs near the end for cursor paging. A list loaded by
  window instead (the library and collection grids) holds `MediaCard.Placeholder` for titles not loaded
  yet, drawn as the web's pulsing skeleton (`MediaCardSkeleton`); the grid tells what it shows through
  `ViewportCommand` (a `GridViewport`, at most every 50 ms while it scrolls) and `ScrollToIndex` puts a
  card's row at the top. A uniform grid sizes every row by one freshly made first card when that one is
  far out of view, so `MediaCardView` sizes its artwork at measure time: a card that only learns its
  height after arrange shrinks every row and throws the scroll position off.
- Cards are buttons (`SiloCardButtonStyle`): Tab and Enter work, screen readers read
  `MediaCard.AccessibleName`. Card commands take the `MediaCard` as parameter. A card's ⋮ menu is the web's
  (`MediaItemMenu.buildMediaItemMenuModel`), in its order: Play from Beginning (a movie or episode with a resume
  point, or watched), Mark Watched/Unwatched, Reset Progress (`DELETE /api/v2/progress/{id}/position`), Favorites,
  Watchlist, Not Interested (`PUT`/`DELETE /api/v2/not-interested/{id}`, movies and series), then "Remove from
  Continue Watching" / "Next Up" when its `MediaRow` offers removal. Right-click, Shift+F10 and the menu key open the
  same menu. The web's admin entries (play history, metadata) are not there yet.
- Page-specific pieces: `PersonCard` (search people), `AlphabetJumpBar` (library A–Z: "#" and A–Z on a
  26 px round panel, letters without titles at 35 %, the top row's letter red with dark text; a letter
  scrolls the grid to its first title, as the web's `LetterBar`),
  `DetailHero`, `DetailShelf`, `DetailEpisodeCard`, `DetailTrailerCard`, `PersonCreditCard`
  (detail pages), `SeekBar` and `MpvVideoHost` (player).
- `ItemsRepeater` sets no `DataContext` on elements of `x:Bind` templates: a click handler reads its item
  from the control it shows (`MediaCardView.Card`) or from `DataContext="{x:Bind}"` set on the element.
- `x:Bind` function bindings skip null arguments, so a recycled element would keep stale values:
  controls that show optional parts set them in code when their item changes (`MediaCardView`).
- Artwork goes through `Artwork.Load`, which fills the bitmap from the app's artwork cache
  (docs/architecture.md, Artwork). `MediaRow` and `MediaGrid` prefetch their cards' artwork; a grid asks
  for the next page four rows ahead of its last realized card.

View models build `MediaCard`s with the texts already worded; the controls only lay them out.

## Layout

- Sidebar (260 px): server name, Home, Search (`Ctrl+K`), "LIBRARIES" with the server's libraries, "DISCOVER" (For
  You, Requests while the server takes them, Calendar, Notifications with the unread count in red), "YOUR STUFF" (Favorites, Watchlist, Collections,
  History; the web's Watch Party to come); Servers and the profile menu at the bottom. Favorites, Watchlist and History are one page (`PersonalListPage`, the web's `pages/Catalog.tsx` for those
  sources): the list's name, what it holds and its count, the media menu (All Media, Movies & Series, Movies, Series,
  Episodes), the list's own order ("List Order", History's "Watch History") or a sort, and the posters by window. The
  web's watchlist tab "Not in your library yet" comes with Requests, History's select-and-remove later. Detail pages shrink it to icons; the player hides it.
  LIBRARIES, DISCOVER and YOUR STUFF fold away by their headers, a chevron before the capitals as on the web's
  LIBRARIES (down while open, right while folded, brighter under the pointer); unlike the web, all three fold and stay
  so across starts (`sidebar.json` in the data directory, the app's and not a profile's). A folded section still shows
  the entry of the page on screen. The icon-only rail of a detail page shows every entry, as the web's rail does, since
  it has no headers to open them by: the headers become the web's short dividers (20 × 1 px, `#2E2E35`). A mouse resting
  on the rail for 150 ms opens it over the page, which keeps its width, and leaving it closes it again. The sidebar has
  no back arrow, as on the web: a page opened from another (a title, a row in full, a collection, the servers) has the
  web's PageBack, a 36 px glass disc with a chevron at its top left; the sidebar's own pages have none, and Back
  (Alt+Left, the mouse's back button) still leads from them to Home.
- Collections (`CollectionsPage`, the web's `pages/Collections.tsx`): "Your collections" with its count ("Manual ·
  4 titles", a SHARED badge on one other profiles see), "Shared with me" by owner ("by Mara"), and "Server
  collections" with a pill per library (All first) and Load more, which the end of the grid presses by itself. A
  collection opens on the list page with its name and "Collection Order"; the sidebar keeps Collections picked.
  Making, editing, sharing and reordering collections come later.
- For You (`ForYouPage`, the web's `pages/Recommendations.tsx`): the title, "Personalized picks…" only when a row comes
  from the profile's history, the taste profile card on the right (top genres filled, favourite directors outlined),
  then a row per recommendation. A row's Explore all opens it in full (`RecommendationSectionPage`: name, "60 titles",
  the posters); the sidebar keeps For You picked. The sidebar reads the rows again behind the ones shown.
- Calendar (`CalendarPage`, the web's `pages/Calendar.tsx`): the title with the presets Following, Trending, All (the
  chosen one red) and "All Libraries" when there are several; the week strip pinned above the days (arrows, "Oct 19 –
  25, 2026", Today, a button per day with a dot on days with airings, today tinted red, the picked day filled red,
  which scrolls to its row); then a row per day ("Monday, October 19th", TODAY on today's) of airings: "S2 · E1 - TITLE",
  the air time on the viewer's clock, SERIES PREMIERE / SEASON PREMIERE / FINALE, watched ones marked. Unlike the web,
  a week with airings lists its empty days too, each with "Nothing scheduled." in a faint box, so picking one scrolls
  to its place instead of the top; a day picked in an empty week says "Nothing scheduled for …". An airing opens its series (a movie itself); airings have no card menu. An empty week names the
  other presets. The preset is kept while the app runs (the web keeps it in the browser).
- Notifications (`NotificationsPage`, the web's `pages/Notifications.tsx`): one centred column of the web's width with
  the bell and title, Mark all read while anything is unread, Preferences (a flyout with the master switch and
  Favorites, Watchlist, Continue Watching, Next Up, read each time it opens and saved at once), All and "Unread (3)",
  then the entries: poster, a red dot and a lighter background while unread, title, "5m ago", what happened
  ("S4E2 — Blind Rooster"), the reasons as pills, a check button to mark it read; 25 at a time with Load more. An entry
  opens its episode or series and is read from then. The sidebar hides the entry when the server has no inbox and
  reads the count again on the way through the app, at most every half minute. The web's Series view comes later.
- Requests (`RequestsPage`, the web's `pages/Requests.tsx`): the title and what the page is for, Discover and "Yours 2"
  as line tabs (the picked one white with a white bar under it). Discover: rows of TMDB posters with their state
  (AVAILABLE, APPROVED, …, or why they cannot be requested); a title in the library opens there. Yours, up to the web's
  1024 px: "What the statuses mean", then Needs attention, On the way, In your library, Cancelled with their counts,
  each request a row with its poster, title and state badge (the web's variants: Available red, Failed destructive,
  approved and on their way filled, Pending outlined, closed ones outlined and muted), "Series · 2022 · Seasons 1–3",
  the download's bar and "Downloading · 43% · about 12 min left", when it was requested and changed, the error in red
  or the admin's reason, Open in library, and Cancel request behind the web's confirmation. A Discover row of several
  pages has EXPLORE ALL; under the rows come Studios, Networks and Genres as rows of 208 × 112 tiles (a logo on gray-800
  with a gray-700 ring, the name when there is none, a genre's name on its 135° gradient), empty tiles while they load,
  and "Could not load studios." beside Retry. Search comes next. Every TMDB title's card (on the hub, a row in full, a
  studio's page, More like this) has the web's watchlist toggle where library cards keep their menu, while the server
  keeps TMDB titles on the watchlist: the web's bookmark, red with a check when on it (the TMDB title page's Add to
  Watchlist too; a library title keeps + and ✓); an add that requested the title shows the request's state on the card
  at once. Unlike the web source, the server's web shows no Request action on these cards; requesting is the title
  page's.
- A Discover row in full and a studio's, network's or genre's titles (`RequestTitlesPage`, the web's
  `RequestDiscoverSection.tsx` / `RequestBrowse.tsx`): the name (the row's key in words until the server names it), for a
  browse page a 112 × 64 tile with the logo or name and "Studio" under the name, Movies | Series pills for a genre and
  the sort (Popularity, Rating, Release date) on the right; then the poster grid, which reads the next page as its end
  comes into view, with Load more for the keyboard and "Couldn’t load more titles." beside Try again. The web's texts
  for a row that does not exist, TMDB not answering, and nothing matching.
- A title from TMDB (`TitlePage`, the web's `TitleDetail.tsx` / `ExternalTitleContent.tsx`): laid out like a library
  title (backdrop, poster, "MOVIE · DC STUDIOS", chips, the TMDB score with its votes, overview, "Directed by" or
  "Created by", genres), with request actions in place of playback: Request movie / Request series, or the request's
  state greyed ("Requested", "Approved"), Open in library; Add to Watchlist / On Watchlist ("Adding it to your
  watchlist also requests it." when it does), Cancel request for the account's own while withdrawable, Notify me when
  available for someone else's open request, IMDb and TMDB in the browser, the download's progress; then Seasons (with
  "In library", "Requested", "Not aired yet"), Cast and More Like This. A series asks "Request seasons" first: a switch
  per season, the aired missing ones on, the others greyed; Latest season, Upcoming seasons and All seasons; an
  untouched pick leaves the seasons to the server. Discover cards and More Like This open it, or the library's title.
- Pages: 48 px side margins, 32 px top; rows 40 px apart.
- Detail pages: full-bleed backdrop fading into the background, logo (or display title), chips,
  ratings, overview with "More", red Play/Resume pill, secondary actions, then rows (seasons, episodes,
  cast, more like this).
- Back: the navigation view's back arrow, `Alt+Left`, and the mouse back button. The sidebar's sections (Home,
  Search, each library) are the apps' tabs: picking one does not stack on the pages visited before, only Home stays
  under it, so Back walks the pages opened within a section and then goes Home (Android's tab switch pops to the
  anchor tab). Settings and Servers open over the page they came from and go back to it.

## Signed-out screens

Adding a server and signing in look like Silo's web login (measured 2026-10-25). `SiloAuthShellStyle` centres a
448 px card (the surface, radius 12, 28 + 24 px inside) on the background with a red glow at the top left and a
light one at the top right. Above it Granary's own name (44 / ExtraBold) and "A Silo client for Windows": Granary's
identity leads these screens, as Silo's developers asked (2026-10-26). Silo's mark, the official flat cut from
siloserver.org/brand (`Assets/Brand`, unmodified, 44 px tall), only refers to the server: beside "Connect to your Silo
server" and beside the server's name when signing in (`SiloAuthMarkStyle`; see the NOTICE there). Granary's logo and
app icon must not look like Silo's; Silo's colours and the three-bar motif are free to use in the interface.

- In the card: the title (`SiloAuthTitleTextStyle`, 30 / ExtraBold) over a muted lead, labelled 36 px fields
  (`SiloAuthTextBoxStyle`, `SiloAuthPasswordBoxStyle`), full-width buttons rounded 10 (`SiloAuthButtonStyle` red,
  `SiloAuthOutlineButtonStyle`), refusals in a red box (`SiloAuthErrorStyle`) and the consent to an unencrypted
  connection in an amber one (`SiloAuthWarningStyle`).
- The pages merge `Themes/SiloAuth.xaml`, which gives WinUI's text boxes the web's field: the page background, a
  faint outline, a red one when focused (instead of Fluent's accent underline), the placeholder muted.
- Add server: "Connect to your server" and "Type the address you use for Silo." as in the apps; progress and
  refusals below the button.
- Sign in: the server's name, a refusal, the password form, then "Use your phone instead". As on the web, the QR code
  shows on "Show QR code" (at once when the server has no password sign-in), framed, with the address and the code
  below it; the page scrolls to it.

## Settings

Opened from the sidebar's Settings; as on Silo's web the app's sidebar gives way to the settings' own (260 px, a shade
darker, a hairline at its edge): "← Back to App", "Settings", the current profile (its initial in a 32 px disc), then
the pages by group. The page open is white 6 % with red text and a red mark. Pages scroll on the right, 1120 px at
most, padded 50 px: the title (32 px bold) and its line, then sections between hairlines (`SettingsGroup`), their title
(15 px semibold) and description (13 px muted) in a 256 px column and their rows (`SettingsRow`: label 14 px medium,
description 13 px muted, the control on the right, hairlines between rows). Selects are 220 px and 36 px high on the
page background with a faint outline, red when focused; switches are only their 40 px track, red when on
(Themes/SiloSettings.xaml). Every value is the profile's and saves at once; a refused save puts the control back and
says why above the sections.

- **Playback**: Quality (preferred quality, maximum bitrate with "No limit", lower quality when buffering, and this
  connection's learned limit with Reset), Connection (the latest speed reading and what Auto plays without
  converting), Skipping (skip intros, auto-skip recaps and credits), Next episode (start next at preview, auto-play),
  Seek controls (rewind and fast-forward intervals).
- **Connection** (with `connection_speed_v1`): "Connection speed" with the reading as its description ("400 Mbps ·
  measured just now on Ethernet", "Not measured yet") and **Test speed** as an outline button on the right, "Testing…"
  and disabled while it runs (about 10 s). Under the row, in 13 px muted text, what went wrong, in the web's words:
  "Try again in 12 seconds" (a rate limit; it hides itself after those seconds), "Couldn't save the speed test. Try
  again.", "Couldn't measure the connection speed. Try again.". On a cellular or metered link (Windows' metered
  connection) there is no button: the description says "Speed tests are skipped on cellular, and the Cellular quality
  settings apply." or "Speed tests are skipped on metered networks.", and the Auto budget line goes. Leaving Settings
  stops a test that still runs.
- **Language & Subtitles**: spoken and subtitle language ("No preference" / "None" clear the value), subtitle behaviour,
  forced subtitles; a 16:9 preview of the look, edited as a draft with Save Appearance and Discard Changes, Reset back to
  the default; Text (size, family, colour swatches, opacity, outline and its colour), Background & Position (style,
  opacity and colour for the box, position); Timing (the profile's subtitle offset in 100 ms steps, Reset).

## Player

`Views/PlayerPage` on `PlayerViewModel`, drawn as the server's web player (`web/src/player`, measured below).

- **Window**: the player owns it. The sidebar is hidden completely (not just shrunk), the page reaches under
  the title bar and the caption buttons float over the video; the top 40 px stay the drag area, so the top
  bar starts below them. Full screen (`AppWindow`'s full-screen presenter) shows nothing but the player and
  returns to the window as it was, maximized or not. Leaving the page leaves full screen and stops playback,
  whichever way it is left (Back, `Alt+Left`, `Ctrl+K`, a session change).
- **Layout**: black, the video filling the page. Top left over a soft scrim the "✕ Exit" pill. Bottom bar
  over a stronger scrim: the `SeekBar` across the width; below it on the left the title and the upper-case
  line "S1 · E1 — EPISODE · 31:55 / 48:41" (a movie shows its year), whose end time a click turns into the
  time left; in the middle skip back, the white play/pause disc and skip forward (glass discs showing the
  profile's intervals), for an episode with a previous or next one flanked by previous and next episode discs (both
  slots kept while one has an episode, so Play stays in the middle; a movie has none); on the right mute, the volume
  slider, audio, chapters, subtitles, settings, playback info, mini view, fill screen and full screen. A utility
  button that is on (playback info, fill screen, subtitles) turns white with the web's amber dot under it. As the
  web's `1fr auto 1fr` row, the right column is never narrower than its buttons: in a narrow window the transport
  moves left rather than the buttons being cut off. Previous and next start that episode from where the
  profile left it, as the web player does for the viewer; previous stays within the season and steps over episodes
  without a readable file.
- **Audio menu** (`AudioTrackOption`, wording in `TrackText` after the web's `AudioTrackMenu`): the button shows
  once the file has a track and opens with two or more. Above it, 280 to 360 px wide: "AUDIO", then a row per
  track with its name (its title, else the embedded title, else language, codec and channels), chips for the
  codec family ("DD+ ATMOS", "TRUEHD", "EAC3") and channels, an outlined DEFAULT, and the language and details
  under it. The playing row is blue with a check and has the focus when the menu opens; a pick closes it.
- **Chapters menu** (`ChapterOption`, after the web's `ChaptersMenu`): shown when the playing file has chapters;
  280 px and up, scrolling past 560 px. "CHAPTERS", then a row per chapter: its picture (80 by 48, radius 4, a
  placeholder without one), the title (14 px; "Chapter 2" when it has none) and its start (12 px, white 45 %). The
  chapter that plays has a faint light behind it and white text, the others are white 70 %; it has the focus when the
  menu opens. A row seeks to the chapter's start and closes the menu.
- **Subtitle menu** (`SubtitleTrackOption`, after the web's `SubtitleMenu`): the button is there for every server
  title, tracks or not, since the menu also adds subtitles. Captions glyph, struck through while none shows; white
  with the amber dot while one does. C turns the subtitle off and back on to the track that showed last (both are
  picks, saved for the series). 220 to 420 px wide, scrolling past 560 px. "Off" first, then the tracks embedded, external, downloaded: the
  language's name (14 px), chips on the right for the format (SRT, ASS, VTT, PGS, DVD, DVB) and the source
  (EMBEDDED, EXTERNAL, DOWNLOADED; 10 px, white 50 % on white 10 %), and the track's own label under it (12 px,
  white 40 %) unless it only repeats the language or the format. The chosen row has a faint light behind it,
  white text and a check; the others are white 70 %. Under a line, DELAY with earlier and later (100 ms steps,
  up to 10 s either way; disabled while no subtitle shows), the value ("+300 ms") and Reset: subtitles move at once,
  and the profile's `player.subtitle_sync_ms` is saved once the nudges settle for 600 ms, as the web's, or at the
  stop; a value this device kept for itself is cleared, so the profile's counts, as the web does. Under a line
  "Add Subtitles…" and "Appearance…" (palette glyph), white 70 %.
- **Add Subtitles** (`AddSubtitlesPanel`, `AddSubtitlesViewModel`, the web's `SubtitleSearchModal` with the player's
  `SubtitleUploadForm`): a 480 px card (neutral 900, radius 8) on black 80 %, over everything; a click beside it, ✕
  or Esc closes it, Tab stays inside. "Upload subtitle" with the web's help line, a dashed drop zone (white 20 %,
  35 % under the pointer, 50 % while a file is dragged over it: "Drop subtitle file") that also opens the file
  picker; SRT, VTT, ASS, SSA and SUB up to 5 MB, read and sent to `detect-language`, whose answer selects the
  language ("Detected German from filename"; a language the viewer picks is sent as an override); Hearing impaired
  (HI); Upload ("Uploading…"); "Selected: name"; errors in red 900 at 40 %. Then "Search online" (hidden only when
  the server says its providers are off): a language and Search ("Searching…"), the server's warnings in yellow,
  "Select a language and press Search." until there are results, and a row per result: the score badge (green from
  70, yellow from 40, else red), the release, HI, "↓12,345" and the provider's badge (OS yellow, SDL blue, SS red,
  UP violet), a ring while it downloads. The languages chosen stay while the page plays. A stored subtitle closes
  the dialog and shows as the viewer's pick once a new plan lists it; the stream reloads where the viewer is.
- **Subtitle appearance** (`SubtitleAppearancePanel`, the web's `SubtitleAppearancePanelView`): a 640 px glass card
  (`#111114` at 95 %, radius 16, white 10 % edge) on black 70 %; "CAPTIONS" over "Subtitle appearance" and a round
  ✕; a 112 px preview strip ("The quick brown fox / jumps over the lazy dog." at the web player's cue sizes); TEXT
  (Size, Font as pills, the chosen one white; Color swatches; Opacity in %; Outline), BACKGROUND (Style pills; Opacity
  and Color for a box), POSITION (Vertical), scrolling past 52 % of the window; at the bottom "↺ Use fallback style"
  and "CHANGES SAVED AUTOMATICALLY" (or what went wrong). A change shows at once and is saved as the profile's
  `playback.subtitle_appearance`, the latest only while a save is on its way, clearing this device's look as the web
  does; fallback clears both and shows the server's default.
- **Edit markers** (`MarkerEditPanel`, `MarkerEditorViewModel`, the web's `MarkerEditPanel` and `useMarkerEditor`): a tag
  button left of Playback info for an account with `marker_edit`, or an admin acting on the primary profile (or with
  none selected), white with the amber dot while the panel is open. The panel: 352 px, bottom left above the controls
  (neutral 900 at 95 %, radius 16), moved by its header and kept inside the player; "Edit markers" and "Drag the
  timeline handles, or set points to the playhead."; a row per kind (Intro, Recap, Credits / Outro, Preview) with its
  dot (sky, violet, amber, emerald), name and range in mono ("0:03 – 0:12", "Not set"). The open row (white 6 %, a white
  10 % ring; the first kind that has a range at the start) has Set start and Set end at the playhead, Reset to saved
  and Clear. At the bottom the playhead's time, Reset all (while something changed), Cancel and Save (white, a ring
  while it saves). While it is open the controls stay, the seek bar shows the draft (every kind, brighter, 8 px, the
  open one 10 px with a white ring) with a white 6 by 16 px handle at each end of the open kind; dragging one moves that
  end, never past the other (0.5 s apart), with "Intro end 0:50" above it. A new part gets a minute on the other side.
  Esc closes the panel first; another title closes it.
- **Settings menu** (the web's QualityMenu): a gear pill (white 8 %) that names the quality ("Auto", "720p Medium")
  and, at a speed other than 1×, a white badge with it ("1.5×"). SPEED: the value at the right, the speeds 0.5× to 3×
  (0.25 apart up to 2×, then 2.5× and 3×) as stops on a track drawn like the seek bar, the fill from 1× to the
  chosen speed, 0.5×, 1×, 2× and 3× named under it (`SpeedTrack`); it applies at once and is saved as this device's
  `player.playback_speed`. VERSION with more than one file ("2160p HEVC HDR", "Playing" / "Requested" chips): a pick
  plays that file from where the viewer is. QUALITY when the plan offers rungs: Auto ("Auto · limited after buffering"
  under a learned limit), then the rungs with their bitrate muted at the right ("12 Mbps") and a white check on the
  one the plan delivers; a pick asks for a new plan. A pick of a file or quality closes the menu.
- **Playback info** (the web's PlaybackInfoOverlay): 320 px, black at 85 %, radius 8, under Exit; "Playback Info" with a
  close button, then PLAYER (mpv, the play method: Direct Play, Direct Streaming or Transcode, the stream's protocol and
  type, and the file asked for when the server fell back to another), VIDEO INFO (the picture's area and the video in
  pixels, hardware or software decoding, frames dropped by the output and by the decoder), PLAYBACK STREAM INFO (the
  codecs the plan delivers: direct, copy or transcoded, as its transformations say) and CURRENT SOURCE FILE (container,
  size in GiB, bitrates, codec and profile, range type, colour range, the playing audio track's codec, channels and
  sample rate). Labels white 60 %, values white 90 % on the right; read again every second while it shows; it stays
  while the controls fade, until closed.
- **Fill screen** (the web's video fit): the picture fills the page and is cut where it does not fit (mpv `panscan`);
  a new title shows its whole picture again.
- **Mini view** (the web's Picture in Picture, P): the window becomes small (480 by 270), stays above the others
  (CompactOverlay) and shows only the picture, with play/pause in the middle and the way back top left, fading as the
  controls do; the pills, the playback info and the Playing Next screen wait for the full window, the countdown runs
  on. Esc or P returns to the window as it was; full screen goes there from the mini view, and leaving the page
  returns first.
- **Subtitle look**: text subtitles follow the profile's `playback.subtitle_appearance` as the Android and Apple
  clients draw it (size at 1080 lines, font, colour and opacity, box, shadow or outline in the outline colour,
  bottom, lower third or top), mapped to mpv's `sub-*` options; styled ASS and image subtitles keep their own.
  While the controls show, subtitles at the bottom move up by the bar's height (`sub-pos`) and back when they fade.
- **Seek bar**: 3 px track, the played part white as on the web, over the marked parts tinted sky (intro), violet
  (recap), amber (credits) and emerald (preview), and a 10 px tick (white 55 %) where each chapter after the first
  starts. Under the pointer the track grows, a thumb appears and a bubble shows the time there, above it the part it
  lies in ("● Intro 0:03-0:12") and under it the chapter (11 px, white 50 %). With a picture the bubble is the web's
  176 px card (neutral 95 %, white 8 % edge, a caret under it): the trickplay thumbnail of that time, or while its
  sheet loads and without previews the part's or the chapter's picture, or a faint film frame on a gradient; the
  lines sit left under it. The part under the pointer grows to 8 px with a white 80 % ring. It seeks on click and when a drag is released, never while dragging or when the
  position moves on, and keeps the target until playback has caught up, so the bar does not jump back. Keys:
  Left and Right by the skip intervals, Shift for 5 s, Home, End. Screen readers get a slider whose value
  is the position in words.
- **Skip pill** (the web's `IntroSkipButton`): bottom right, 28 px off the corner and above the bar while the
  controls show; 44 px high, black at 75 % with a white 20 % edge, 14 px semibold. "Skip Intro" fills from the left
  in white 15 % over its 5 s; after an automatic skip "Watch Intro" with "Intro skipped" above it (12 px, white
  75 %, shaded so it reads on a bright picture, and read out). "Skip Recap", "Skip Credits" and "Skip Preview"
  stay while inside their part. Esc turns an intro pill away before it does anything else.
- **Next episode** (the web's NextEpisodeOverlay and PlayingNextScreen):
  - At the credits a card replaces the skip pill: black at 80 %, radius 8, "UP NEXT IN 34S" (12 px, white 60 %),
    "S1:E4 — Title" (14 px medium), Play Now (white, black text) and Cancel (outlined), both rounded 4. It counts the
    playback left, as the apps do.
  - 30 s before the end of an episode the "Playing Next" screen takes the page: its still blurred and darkened behind,
    "PLAYING NEXT" (11 px, wide spacing, white 40 %), the still (16:9, radius 16, up to 38 % of the page high), the
    series (18 px bold), "S1:E4 — Title", the air date and runtime, two lines of overview, the red Play Now pill, and
    once the episode has ended a 10 s ring in red with the seconds; "Auto-play is on" switches the profile's
    `playback.auto_play_next`. On Deck (Continue Watching without this series) runs along the bottom; Close (Esc) top
    right. The video plays on in a 320 px window top left (radius 16), a click on it returns to it; it fades out at
    the end. Enter plays the next episode.
  - Without a next episode the screen says "You've finished {series}" with Back. A movie's end goes back.
- **States**: preparing shows Exit and a spinner, no transport. Buffering puts a spinner on a dark
  disc over the picture. Failed shows the message, Retry when it can help and Back; keyboard focus moves
  to them. Ended keeps the controls up.
- **Auto-hide**: during playback the controls fade (and slide a few pixels) after 3 s without pointer
  movement, and the cursor goes with them while it is over the picture. They stay while paused, preparing,
  buffering, failed or ended, while the pointer rests on them, during a seek drag, while a flyout is open or
  a control in them has keyboard focus. Any pointer movement or key brings them back. Hidden controls take no
  clicks. The cursor goes as the page's `ProtectedCursor`, a blank one made at run time (`BlankCursor`):
  `ShowCursor` does not reach WinUI content, whose cursor WinUI's input stack sets per element.
- **Mouse**: a click on the picture plays or pauses (after 250 ms, so a double click does not), a double
  click toggles full screen. On touch a tap shows or hides the controls.
- **Keyboard** (the web player's map, docs/api-contract.md 13.5): Space and K play/pause, F full screen,
  P the mini view, Esc turns away an intro pill, else closes the marker panel, else leaves full screen or the mini
  view, else closes, M mute, C
  subtitles off and on, Left/Right skip, Up/Down volume ±5. Space wins over a focused button; arrows go to a focused
  slider first. Nothing applies while a text field, an open menu or the Add Subtitles or appearance panel has focus
  (Esc closes the panel), or with Ctrl, Alt, Shift or Windows held. The keys are handled on the page, not as keyboard
  accelerators, so no accelerator tooltip appears; the buttons announce them through
  `AutomationProperties.AcceleratorKey`.

### The web player, measured (reference for phase 3d)

Read from the server's web player (`/watch/{id}`, computed styles at 1920x889, 2026-10-25). The Apple and Android
apps decide how the player behaves (track choice, switching, saving picks); the web decides how it looks.

- **Layout**: top left a 44 px "Minimize" disc and an "Exit" pill (`✕ Exit`, 84x36), both black at 60 %. The seek
  bar spans the width above the controls, with marker ticks and the credits as a gold stretch at the end. Bottom
  left the title (16 px semibold, white) and under it "S1 · E1 — SCHULANFANG · 31:55 / 48:41" (10 px, upper case,
  2.2 px tracking, white 55 %). In the middle back 10, play, forward 30 and next episode: 44 px glass discs
  (white 6 %, 1 px white 10 % edge, white 92 % icons of 28 px, 18 px for next) around a 56 px white disc with a
  dark 24 px icon. On the right bare 40 px icons (white 78 %, 18 px): mute and a volume slider, audio tracks,
  chapters, captions, a settings pill with the quality ("⚙ Auto", white 8 %), markers, info, fill screen,
  full screen.
- **Menus** open above their button: black at 90 % with an 8 px blur behind, 12 px corners, white text. A section
  label is 12 px medium, upper case, 0.3 px tracking, white 50 %. Rows are 8x12 px padded, 14 px medium, white
  85 %; the chosen row is blue-400 (`#60A5FA`) with a leading ✓ (in the captions menu: a white 5 % row instead).
- **Audio** (280 px): a row per track: its name, chips for codec and layout (9.5 px semibold, white 75 % on white
  10 %, 4 px corners) and an outlined DEFAULT chip (1 px white 25 %); under it "German · 5.1(side) · 640 kbps ·
  48.000 Hz" (11 px, white 55 %).
- **Captions** (420 px): "Off" first; a row per subtitle with its language, chips on the right for format and
  source ("SRT", "EMBEDDED" / "EXTERNAL", 10 px, white 50 % on white 10 %), and a second line for SDH or the
  file name and "Synced +8.7 s". Then a DELAY row (− 0 ms + and Reset, 12 px), "Add Subtitles…" and
  "Appearance…".
- **Settings**: SPEED with a value ("1x") and a slider from 0.5x to 3x with ticks; QUALITY with "Auto" (a filled
  check) and the plan's qualities, the bitrate right-aligned and muted ("1080p Medium 6 Mbps" … "360p 300 kbps").

### Pills

Silo's web rounds pills with `rounded-full`: the radius is clamped to half the height, so the ends are half
circles and the sides straight. WinUI does not clamp a radius: 999 on a 44 px high button draws an oval.
`controls:Pill.IsEnabled` sets the radius to half the shorter side whenever the element is sized; every style that
uses `SiloPillCornerRadius` sets it, and so must any element that uses the radius directly.

## Accounts and permissions

The web client was inspected with an admin account, which shows more (file paths under "Media
locations", the Admin area). Granary renders what the API returns for the signed-in account and hides a
section when its data is absent; nothing is shown because an account is assumed to be an admin.
