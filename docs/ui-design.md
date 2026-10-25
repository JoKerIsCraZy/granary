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
  landscape 314 px, 20 px apart), arrows for mouse users.
- `MediaGrid`: cards filling the width (posters at least 185 px, landscape at least 280 px), meant to sit
  in the page's own `ScrollViewer`; `LoadMoreCommand` runs near the end for cursor paging.
- Cards are buttons (`SiloCardButtonStyle`): Tab and Enter work, screen readers read
  `MediaCard.AccessibleName`. Card commands take the `MediaCard` as parameter.
- Page-specific pieces: `PersonCard` (search people), `AlphabetJumpBar` (library A–Z),
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

- Sidebar (260 px): server name, Home, Search (`Ctrl+K`), "LIBRARIES" with the server's libraries;
  Servers and the profile menu at the bottom. Detail pages shrink it to icons; the player hides it.
- Pages: 48 px side margins, 32 px top; rows 40 px apart.
- Detail pages: full-bleed backdrop fading into the background, logo (or display title), chips,
  ratings, overview with "More", red Play/Resume pill, secondary actions, then rows (seasons, episodes,
  cast, more like this).
- Back: the navigation view's back arrow, `Alt+Left`, and the mouse back button.

## Signed-out screens

Adding a server and signing in look like Silo's web login (measured 2026-10-25). `SiloAuthShellStyle` centres a
448 px card (the surface, radius 12, 28 + 24 px inside) on the background with a red glow at the top left and a
light one at the top right, and puts Silo's white wordmark above it. The wordmark (`Assets/Brand`) is shown
unmodified to refer to Silo, as its trademark policy allows; it is not Granary's logo (see the NOTICE there).

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
  profile's intervals); on the right mute, the volume slider, audio, subtitles and full screen.
- **Audio menu** (`AudioTrackOption`, wording in `TrackText` after the web's `AudioTrackMenu`): the button shows
  once the file has a track and opens with two or more. Above it, 280 to 360 px wide: "AUDIO", then a row per
  track with its name (its title, else the embedded title, else language, codec and channels), chips for the
  codec family ("DD+ ATMOS", "TRUEHD", "EAC3") and channels, an outlined DEFAULT, and the language and details
  under it. The playing row is blue with a check and has the focus when the menu opens; a pick closes it.
- **Subtitle menu** (`SubtitleTrackOption`, after the web's `SubtitleMenu`): shown once the plan lists a track;
  220 to 420 px wide, scrolling past 560 px. "Off" first, then the tracks embedded, external, downloaded: the
  language's name (14 px), chips on the right for the format (SRT, ASS, VTT, PGS, DVD, DVB) and the source
  (EMBEDDED, EXTERNAL, DOWNLOADED; 10 px, white 50 % on white 10 %), and the track's own label under it (12 px,
  white 40 %) unless it only repeats the language or the format. The chosen row has a faint light behind it,
  white text and a check; the others are white 70 %. Under a line, DELAY with earlier and later (100 ms steps,
  up to 10 s either way), the value ("+300 ms") and Reset: subtitles move at once, and the profile's
  `player.subtitle_sync_ms` is saved once the nudges settle for a second, or at the stop; a value this device kept
  for itself is cleared, so the profile's counts, as the web does.
- **Subtitle look**: text subtitles follow the profile's `playback.subtitle_appearance` as the Android and Apple
  clients draw it (size at 1080 lines, font, colour and opacity, box, shadow or outline in the outline colour,
  bottom, lower third or top), mapped to mpv's `sub-*` options; styled ASS and image subtitles keep their own.
  While the controls show, subtitles at the bottom move up by the bar's height (`sub-pos`) and back when they fade.
- **Seek bar**: 3 px track, the played part white as on the web, over the marked parts tinted sky (intro), violet
  (recap), amber (credits) and emerald (preview). Under the pointer the track grows, a thumb appears and a bubble
  shows the time there, and above it the part it lies in ("● Intro 0:03-0:12"). It seeks on click and when a drag is released, never while dragging or when the
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
  clicks.
- **Mouse**: a click on the picture plays or pauses (after 250 ms, so a double click does not), a double
  click toggles full screen. On touch a tap shows or hides the controls.
- **Keyboard** (the web player's map, docs/api-contract.md 13.5): Space and K play/pause, F full screen,
  Esc turns away an intro pill, else leaves full screen, else closes, M mute, Left/Right skip, Up/Down volume ±5. Space wins over
  a focused button; arrows go to a focused slider first. Nothing applies while a text field or an open menu
  has focus, or with Ctrl, Alt, Shift or Windows held. The keys are handled on the page, not as keyboard
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
