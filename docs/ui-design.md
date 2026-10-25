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

## Player

`Views/PlayerPage` on `PlayerViewModel`, after the server's web player (`web/src/player`), with red as the
accent where the web player is white.

- **Window**: the player owns it. The sidebar is hidden completely (not just shrunk), the page reaches under
  the title bar and the caption buttons float over the video; the top 40 px stay the drag area, so the top
  bar starts below them. Full screen (`AppWindow`'s full-screen presenter) shows nothing but the player and
  returns to the window as it was, maximized or not. Leaving the page leaves full screen and stops playback,
  whichever way it is left (Back, `Alt+Left`, `Ctrl+K`, a session change).
- **Layout**: black, the video filling the page. Top bar over a soft scrim: back (glass disc), title,
  subtitle. Bottom bar over a stronger scrim: position, `SeekBar`, then the duration, which a click turns into
  the time left; below, volume (mute and slider) on the left, skip back, the red play/pause disc and skip
  forward in the middle (the skip discs show the profile's intervals), full screen on the right.
- **Seek bar**: 3 px track, red progress. Under the pointer the track grows, a thumb appears and a bubble
  shows the time there. It seeks on click and when a drag is released, never while dragging or when the
  position moves on, and keeps the target until playback has caught up, so the bar does not jump back. Keys:
  Left and Right by the skip intervals, Shift for 5 s, Home, End. Screen readers get a slider whose value
  is the position in words.
- **States**: preparing shows the top bar and a spinner, no transport. Buffering puts a spinner on a dark
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
  Esc leaves full screen and otherwise closes, M mute, Left/Right skip, Up/Down volume ±5. Space wins over
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

## Accounts and permissions

The web client was inspected with an admin account, which shows more (file paths under "Media
locations", the Admin area). Granary renders what the API returns for the signed-in account and hides a
section when its data is absent; nothing is shown because an account is assumed to be an admin.
