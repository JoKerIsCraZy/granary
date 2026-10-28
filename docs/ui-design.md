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
| `SiloPrimaryBrush` | `#EF4444`, or the server's accent | The only accent: Play, progress, selection, focus. The server's branding accent replaces it, as on the web (`ServerAccent`, `AccentPainter`); its shades (`SiloPrimaryHoverBrush`, `SiloPrimaryPressedBrush`), tints (`SiloPrimaryTint15/60/75Brush`), labels (`SiloOnPrimaryBrush` dark on buttons, `SiloOnPrimaryBadgeBrush` white on badges) and the sign-in glow follow it. Errors stay red. |
| `SiloBadgeBackgroundBrush` / `SiloBadgeBorderBrush` | `#14141E` at 70 % / white at 15 % | Pills on artwork |
| `SiloAttentionBrush` / `SiloAttentionDotBrush` | `#FCD34D` / `#FBBF24` | A watchlist title TMDB lost: its caption, the tab's dot |
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
  landscape artwork with radius 16, the profile's badges in the four corners, the card's own status ("NEW", "APPROVED")
  and a watched mark top right, a 3 px red progress bar, then title, subtitle and an optional caption. In a row or grid
  the lines under the artwork follow the profile's caption choice ([poster size and caption](api-contract.md#662-poster-size-and-caption)):
  all of them, the title only, or none (then nothing under the artwork, not even its 11 px gap); a card elsewhere (an
  episode on a title's page) keeps them all, as the web's. The badges are
  the web's CardOverlays (`Granary.Core.Overlays`): which ones, their corner, colour, icon and style come from the
  profile's `ui.card_overlays` (or the server's defaults), at most three a corner (`CardBadges`), drawn by
  `CardBadgeViews` in the five styles (Minimal, Classic, Vibrant, Pill, Square; colours mixed as CSS `color-mix`). An
  edge's two corners share its width only when they must (`BadgeEdge`, the web's `justify-between`), a label then ending
  in "…". A poster lays them out at the web's 185 px and scales them, insets and all, to its width; a wide card keeps
  them. Over a progress bar the bottom row rises 8 px clear of it. Icons are Segoe Fluent glyphs; HDR10, Atmos and AV1
  are heavy words, as Silo writes them. Cards show no badges until the profile's are known, so no defaults flash. Without artwork, or when it fails (expired signature), the title stands in. An episode's poster
  reads as the web's grid card (`lib/episodeCardLabels.ts`): the series ("Tracker"), the episode's own title
  ("Safe Harbor"), then "S04E01". The play disc, the card's buttons and its menu are made on first need (the pointer,
  focus on the card's button, the context menu), the progress bar only for a card with progress (`x:Load`): made for
  every card, they cost a row of cards scrolling in 26–75 ms, now 10–23 ms, and most cards are never hovered.
  Artwork comes as on the Android app (`ThumbhashImage.kt`): the server's thumbhash (`poster_thumbhash`,
  `backdrop_thumbhash`, `still_thumbhash`) shows at once as a 32 px picture blurred over the box (`ThumbHash`,
  `Thumbhashes`), the title only without one; the artwork, decoded as large as its box shows it, fades in over it in
  300 ms, or shows at once when it came within 100 ms. While a page or row scrolls faster than about three rows of posters
  a second (`ScrollMotion`), cards hold their artwork back until it slows down or stops: decoding every poster that
  rushed past was half of what made a wide window stutter. The app collects garbage in `SustainedLowLatency` mode, so a
  full blocking collection does not land in a scroll.
- `MediaRow`: row title, optional "EXPLORE ALL" (on every Home and library row, as on the web, also when the row
  already holds all of it; For You and Requests rows only when a page lists more), horizontally scrolling cards 20 px apart
  as wide as the web's for the profile's poster size at the window's breakpoint (posters 120 to 220 px, standard 185 from
  1024 px; wide cards 260 px, 315 from 640), arrows for mouse users. Sideways rows (`MediaRow`, `DetailShelf`, search's
  people) are `ScrollView`s that ignore the mouse wheel (`WheelScrollsPage`): the wheel scrolls the page, Shift or a
  tilting wheel the row. A `ScrollViewer` row that only caught the wheel's routed event still took quick turns
  sideways: WinUI handles the wheel as an inertial manipulation.
- `MediaGrid`: cards filling the width, posters in the web's columns for the profile's poster size at the window's
  breakpoint (standard: 3, 4 from 640 px, 5 from 768, 7 from 1024, 8 from 1280), 12 px apart across and down (16 for
  large), and never narrower than 64 px: a window too narrow for the columns beside the sidebar has one less. The basis
  is the window's width, as the web's breakpoints are its viewport's, sidebar included, so both show the same density at
  the same size; the columns change, and the realized cards are told, only when a resize crosses a breakpoint
  (`CardLook`, taken as the grid measures). Past that, a grid whose posters would be wider than a row's of the same size
  (160, 185 or 220 px from 1024) takes more columns (`CardPresentation.GridColumns(at, width)`): the web stops at `xl`
  and its posters keep growing, which on a wide or ultrawide window makes them several times a row's size. Landscape cards are
  at least 280 px. The grid is meant to sit
  in the page's own `ScrollViewer`; `LoadMoreCommand` runs near the end for cursor paging. A list loaded by
  window instead (the library and collection grids) holds `MediaCard.Placeholder` for titles not loaded
  yet, drawn as the web's pulsing skeleton (`MediaCardSkeleton`); the grid tells what it shows through
  `ViewportCommand` (a `GridViewport`, at most every 50 ms while it scrolls) and `ScrollToIndex` puts a
  card's row at the top. A uniform grid sizes every row by one freshly made first card when that one is
  far out of view, so `MediaCardView` sizes its artwork at measure time: a card that only learns its
  height after arrange shrinks every row and throws the scroll position off. For the same reason, once an episode's
  poster with its extra line is among the cards, every card keeps that line free at its foot, as the web's grid grows
  its rows to the tallest card.
- A row in full (`SectionPage`, the web's catalog page for a section): the row's name, the web's line "Refine the
  archive by type, era, rating, or genre.", its panel "Filters are locked to this source.", and the cards as posters,
  Continue Watching too. No count: a section's total is only a lower bound.
- Cards are buttons (`SiloCardButtonStyle`): Tab and Enter work, screen readers read
  `MediaCard.AccessibleName`. Card commands take the `MediaCard` as parameter. A card's ⋮ menu is the web's
  (`MediaItemMenu.buildMediaItemMenuModel`), in its order: Play from Beginning (a movie or episode with a resume
  point, or watched), Mark Watched/Unwatched, Reset Progress (`DELETE /api/v2/progress/{id}/position`), Favorites,
  Watchlist, Not Interested (`PUT`/`DELETE /api/v2/not-interested/{id}`, movies and series), then "Remove from
  Continue Watching" / "Next Up" when its `MediaRow` offers removal. Right-click, Shift+F10 and the menu key open the
  same menu. The web's admin entries (play history, metadata) are not there yet.
- Page-specific pieces: `PersonCard` (search people, opening the person's page), `AlphabetJumpBar` (library A–Z: "#" and A–Z on a
  26 px round panel, letters without titles at 35 %, the top row's letter red with dark text; a letter
  scrolls the grid to its first title, as the web's `LetterBar`),
  `DetailHero`, `DetailShelf`, `DetailEpisodeCard`, `DetailTrailerCard`, `PersonCreditCard`
  (detail pages; a card with a person opens their page, one without, as a TMDB title's cast, is only a card),
  `SeekBar` and `MpvVideoHost` (player).
- Person (`PersonPage`, the web's `pages/PersonDetail.tsx`): a detail page, so the sidebar narrows to its rail as beside
  a title. The 2:3 photo (180 px, initials without one) beside the name (30 px) with Refresh metadata (an administrator's
  "Refresh now"; "Queueing..." while it is sent), the facts as the detail chips in capitals ("BORN APR 25, 1940",
  "86 YEARS OLD" or "DIED FEB 28, 2005 (AGE 84)", the birthplace), and the bio in muted 14 px at most 672 px wide; a
  line under them says "Person refresh queued", "Person metadata refreshed" or why it failed. Under a hairline
  "Filmography" with All, Movies and Series as the web's small outlined boxes (the picked one lighter), then the posters
  newest first, 60 at a time as the grid scrolls, or "No items found."; titles that fail say why with Retry (only the
  titles are read again), and a later page that fails says so under the grid with Try again (scrolling asks no more
  until then). A read that fails says "Couldn't load this person" with Retry; a person that is gone, "This person isn't
  available".
- `ItemsRepeater` sets no `DataContext` on elements of `x:Bind` templates: a click handler reads its item
  from the control it shows (`MediaCardView.Card`) or from `DataContext="{x:Bind}"` set on the element.
- `x:Bind` function bindings skip null arguments, so a recycled element would keep stale values:
  controls that show optional parts set them in code when their item changes (`MediaCardView`).
- Artwork goes through `Artwork.Load`, which fills the bitmap from the app's artwork cache
  (docs/architecture.md, Artwork). `MediaRow` and `MediaGrid` prefetch their cards' artwork; a grid asks
  for the next page four rows ahead of its last realized card.

View models build `MediaCard`s with the texts already worded; the controls only lay them out.

## Layout

- Sidebar (260 px): server name, Home, Search (`Ctrl+K`), "LIBRARIES" with the libraries the profile browses (its hidden
  ones left out, in its library order: the web's Settings → Libraries), "DISCOVER" (For
  You, Requests while the server takes them, Calendar, Notifications with the unread count in red), "YOUR STUFF" (Favorites, Watchlist, Watch
  Party while the server offers rooms, Collections, History); Admin (an administrator's only, a shield), Settings and the profile menu at the bottom (the servers are in Settings → Servers). Favorites, Watchlist and History are one page (`PersonalListPage`, the web's `pages/Catalog.tsx` for those
  sources): the list's name, what it holds and its count, the media menu (All Media, Movies & Series, Movies, Series,
  Episodes), the list's own order ("List Order", History's "Watch History") or a sort, Filters (the library page's
  `CatalogFiltersButton`: genre, content ratings and a year range, their options read for the list with
  `GET /api/v2/catalog/filters?source=…`; another list starts unfiltered, and filters that leave nothing say "No titles
  match these filters" with Clear filters), and the posters by window. A collection's page has the same Filters.
  While the server keeps watchlist titles the library does not have (`requests_enabled` and `watchlist_titles_supported`),
  the watchlist has the web's line tabs (`SiloLineTabStyle`, as Requests'): In your library, the list; and Not in your
  library yet (`WatchlistTitlesViewModel`, the web's `WatchlistTitlesTab`) with its count in a pill and an amber dot
  while TMDB lost one of them. That tab drops the count, menus and posters for: a panel with the hint (whether adding
  requested them) and Find more in Discover in red; the TMDB posters soonest first (downloading, then by release date,
  awaiting approval, not requested, TMDB lost), each with its state as the badge ("DOWNLOADING 43%", "OUT DEC 18",
  "AWAITING APPROVAL", "NOT REQUESTED", "NOT ON TMDB"), "MOVIE · 2020" and the state in words under it ("Out Dec 18 ·
  approved", amber for a lost one), and the watchlist toggle to take it off; "Could not load these titles." with
  Retry; or, empty, the web's dashed box: a bookmark, "Nothing waiting for the library", what goes there and Browse
  Discover. The titles are read on every visit, every 30 s while one downloads, and after a watchlist change; one that
  left (arrived, or taken off) reloads the library's list. Unlike the web source, the cards offer no Request action:
  requesting is the title page's, as on Discover's cards. History carries the web's Watch History panel: Select turns a
  card's click into a pick (a 32 px dark circle top left, the accent dot with a check once picked), with the count,
  Select Loaded, Clear, the red Remove Selected and Done; Remove asks first in the web's words ("Remove selected watch
  data?") and, done, says "Removed watch data for N items" and loads the list again. Detail pages shrink
  it to icons; the player hides it.
  LIBRARIES, DISCOVER and YOUR STUFF fold away by their headers, a chevron before the capitals as on the web's
  LIBRARIES (down while open, right while folded, brighter under the pointer); unlike the web, all three fold and stay
  so across starts (`sidebar.json` in the data directory, the app's and not a profile's). A folded section still shows
  the entry of the page on screen. Each profile can hide entries and order each section's own (Settings → Navigation &
  Cards → Sidebar; `SidebarLayout`, `sidebar-layout.json`, kept on this PC by server and profile: the server's
  `nav.primary_menu` is shared with the Mac app and knows neither Requests nor the profile's lists). Home, Search,
  Settings and the profile always show; a hidden entry still shows while its page is on screen, and a section with nothing
  left loses its header. A library added since the order was set comes after the ordered ones. The icon-only rail of a detail page shows every entry, as the web's rail does, since
  it has no headers to open them by: the headers become the web's short dividers (20 × 1 px, `#2E2E35`). A mouse resting
  on the rail for 150 ms opens it over the page, which keeps its width, and leaving it closes it again. The sidebar has
  no back arrow, as on the web: a page opened from another (a title, a row in full, a collection, the servers) has the
  web's PageBack, a 36 px glass disc with a chevron at its top left; the sidebar's own pages have none, and Back
  (Alt+Left, the mouse's back button) still leads from them to Home.
  Between a title's page and any other the sidebar changes its width with the page; the old page then goes at once,
  the pane moves over the empty page, and the new page fades in (160 ms) once the pane stands, instead of the old page
  squeezing while the pane moved and the new one sliding in after it.
- Sideways rows (`MediaRow`, `BrandRow`, the detail pages' `DetailShelf`) reach the window's right edge as the web's
  carousels do: the track takes the page's 48 px right gutter and keeps it inside, so the next card shows in the gutter
  rather than stopping at an invisible wall (`RowBleed`; the player's On Deck row has a 32 px gutter). On the left a row
  ends at the page's gutter, no cut-off card beside the sidebar, and the arrows page by as many whole cards as show, so a
  card starts right on the gutter after each click.
- A library (`LibraryPage`) opens on the view it was last left on, Recommended, Library or Collections, as the web's
  "Remember library view" (`ui.remember_library_page_state`, on by default) does: per server, profile and library in
  `library-tabs.json` in the data directory, since the web keeps it per device too. A library never left opens on
  Recommended. The Library view's sort, order and filters are kept with it, per server, profile and library, as the
  Apple app's "Preserve sort & filters" (`BrowsePrefsStore`) does, so the library opens sorted and filtered as it was
  left; a letter jumped to is not kept. A file from before holds only the tab, and the library then opens by title.
- Collections (`CollectionsPage`, the web's `pages/Collections.tsx`): "Your collections" with its count ("Manual ·
  4 titles", a SHARED badge on one other profiles see), "Shared with me" by owner ("by Mara"), and "Server
  collections" with a pill per library (All first) and Load more, which the end of the grid presses by itself. A
  collection opens on the list page with its name and "Collection Order"; the sidebar keeps Collections picked. The
  red New collection at the title (and, with none yet, the dashed card) opens the web's picker ("New collection", what
  decides what's in it, a card each for Manual and Smart with "Good for …"; synced lists are left out), and the card the
  collection editor (`CollectionEditorPage`, the web's `CollectionEditor` for a personal collection): "‹ Collections", the tags
  (Manual, Shared, Not created yet), the name and "3 titles", Open and ⋯ Delete… once it exists; then cards for Name,
  Description and Show only (watch state, content), Titles (search "Add a title" after a 250 ms pause with Add or
  "In this collection"; the list ten at first with Show all, × with "Removed …" and Undo for 6 s, moved by drag or
  Alt+Up/Down), and Where it shows (Show to other profiles, with who loses it when turned off, and Show on the
  Collections tab). The save bar at the foot: "Not created yet" with Cancel and Create collection, or "Name and
  description not saved · Titles are already saved." with Discard and Save. Titles save as they change; the rest at
  the version it was read at, merged when it changed elsewhere, with Use theirs / Keep mine for a field changed in both
  places. Leaving with something not saved asks "Discard unsaved changes?". A Smart collection has Rules instead of
  Show only and Titles, as the web's sentence: "Show [movies] from [all my libraries] that match [all] of these:", one line
  per rule (field, condition, and a value control that fits: text, number with its unit, a range, Yes/No, a list, a
  facet or person with suggestions, a date or "in the last 30 days"), later groups joined by "and" or "or", Add rule and
  Add an "or" group; a rule these controls can't show reads "Not editable here" and stays as saved until removed. Then
  Order (sort, direction, max titles), a saved default sort with Clear, and the live preview under it ("Live preview ·
  40 titles match · showing 24" over posters, ghosts with "No titles match yet", or "The preview didn't load. You can
  still save."). Before Create the save bar says how many match now; the meta line adds "Updates itself as titles are
  added". Last comes Look, while the server takes artwork: closed, one line with the poster's thumbnail, "Poster: a
  collage of its titles" (an image, a new image, a collage once its titles have posters, none) and Change; open, the
  112 px tile with its pencil menu (Upload image… for a JPEG, PNG or WebP of 10 MB at most, Paste a link… with the
  "Poster image link" box, Use the collage or Remove poster), the caption, and "The new poster saves when you press
  Save." with Undo. The poster is part of the draft ("Poster not saved"); it saves after the collection, and one that
  fails stays with "Couldn't save the poster. …" and Retry while the card opens by itself. After a save without a
  poster of its own, the collage is waited for a while. "Rows that show it" comes later.
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
  reads the count again on the way through the app, at most every half minute. Where the server lists them, Series
  joins All and Unread (and "See which series notify you" the Preferences and the empty inbox): the web's words with a
  Preferences link, a search box (after a 300 ms pause), "All reasons" and "Title (A–Z)" as outlined menus, then the
  series that notify ("Any new episode", "Any episode after S2E9") and under "Won't notify" the others, dimmed, with why
  ("Muted", "Watchlist notifications are off") and their reasons as pills, struck through when off; both lists load as
  the foot comes into view. Mute and Unmute sit at a row's right; "Muted Dark" with Undo stands above the lists, where
  the web shows a toast, and the lists are read again, dimmed until they come.
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
  and "Could not load studios." beside Retry. Every TMDB title's card (on the hub, a row in full, a
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
- Request to add (`SearchPage`, `RequestToAddViewModel`, the web's `RequestToAddSection` as its catalog search shows it):
  under Search's titles while the server takes requests, the movies and series TMDB finds for a text of two characters
  or more that the library does not have, following the type switch. A heading like People's, "Nothing in your library
  matches “text”." under it when the library found nothing (its empty grid and "Titles" then go, and "No titles found"
  shows only once TMDB found nothing to add either; the ring turns until it knows), up to 20 TMDB posters, and under
  them Previous, "Page 2 of 9" and Next (TMDB's pages, at most 500). Another page dims the shown one to 60 % until it
  arrives and brings the heading back into view when it was scrolled away. A first page that fails, or that is the only
  page and holds only library titles, leaves the section out; a page of only library titles among others says "Every
  title on this page is already in your library."; a later page that fails says "Couldn’t load page 2." beside Retry
  and a ghost Back to page 1. The cards open the title's page, or the library's title.
- Pages: 48 px side margins, 32 px top; rows 40 px apart.
- What happens on another device shows without a visit (`RealtimeEvents`, docs/api-contract.md 12): a title watched,
  favourited, put on the watchlist or taken off Home there, or a title the server added or changed, reloads Home in
  place once the changes have been quiet for 2 s (rows keep where they were scrolled to; other pages reload on their
  next visit); another device's playback moves Continue Watching at most every 30 s; a new or read notification sets
  the sidebar's count. Nothing shows the connection itself.
- Detail pages: full-bleed backdrop fading into the background, logo (or display title), chips,
  ratings, overview with "More", red Play/Resume pill, secondary actions, then rows (seasons, episodes,
  cast, more like this). The hero takes 60 % of the window's height (a season's 50 %), the web's value below its `lg`
  width: its 72 % above put the title low on a wide screen.
- Watch Party (`WatchPartyPage`, the web's `pages/watchtogether/WatchPartyHub.tsx`): "WATCH PARTY", "Watch together,
  wherever everyone is." and what a party is; beside each other "Start a party" (Create Watch Party opens an empty room
  the host picks for) and "Join a party" (the code in a wide spaced box, Paste, Join Watch Party; a pasted invite link
  of this server joins by its token, a link to anywhere else not at all); an error in the web's red box ("Room not
  found.", "That room is no longer active."); in a room already, a line back to it; "YOUR RECENT PARTIES" (the rooms of
  the last day: the title, Live / Checking… / Status unavailable / Ended, "Your room" or "Joined", the code, and
  Rejoin while it is on); "How it works" in three steps. Invite links opened from outside the app are not there yet.
- The room (`WatchPartyRoomPage`, the web's `WatchTogetherRoomPage.tsx`): the status strip ("● Live · 1 here", "Voting
  · 1 here" or "Playing · 3 here"; the code chip, copied by a click; Invite link; the host's "Host picks · you ▾" menu
  with Everyone votes, a plain "Host picks" / "Everyone votes" chip for guests and while playing; "● Connected",
  Leave, and the host's red End room, asked first). The stage changes with the room: a card chosen from the shelf
  (its facts and "WHO'S SEEN IT" for a movie, a season and episode to pick for a series; "Stage it for the room" in a
  host-pick lobby for its host, "Suggest this" everywhere else, then "Suggested …" as the web's toast); "NOW PLAYING"
  with the room's clock ("12:04 · Playing · 3 here"), Rejoin playback and the host's Stop for everyone; the vote's
  hero ("FIRST SUGGESTION" or "LEADING · 2 OF 3 VOTES", "Evan Allmächtig (2007)", "1h 36m · PG · Fantasy, Komödie ·
  Start the leader, or play any suggestion below", the host's "▶ Play … for everyone"), or "VOTING IS OPEN / No
  suggestions yet"; "UP NEXT" with "2 of 3 ready", the host's "Start for everyone · 2/3 ready" and Change, the guests'
  "I'm ready" / "Ready ✓"; or "ROOM IS OPEN / Nothing on yet" with the host's "Open the floor to votes". Under it the
  suggestions ("SUGGESTIONS · RANKED BY VOTES" with "One vote each · change it any time": a round vote toggle, the
  poster, title and subtitle, who suggested it, LEADING, a vote bar and "2 votes", the host's "Play this" and a remove
  for the host or its suggester; in a host-pick lobby "SUGGESTIONS · 3" with the host's "Queue this"; while playing
  "AFTER THIS · STILL OPEN FOR SUGGESTIONS"), and the invite row ("INVITE", the link without its scheme, Link, Code,
  and the host's "Guests can pause" switch). Then the shelf: "Search movies, series, episodes…" with the pills All,
  Movies, Series, Together, Watchlists and Recent; "CONTINUE WATCHING TOGETHER · in progress for 2+ of you" and "ON YOUR
  WATCHLISTS · anyone in the room" with the members' badges (initials in their colour, two and "+n", the names on
  hover), the episode the room is on and its progress; Home's discovery rows ("TRENDING THIS WEEK"…); and "RECENTLY
  ADDED MOVIES / SERIES". It folds to one line ("Change what's up next", "Suggest something for after") for the host of
  a staged lobby and while playing. At the side, People (initials in the member's colour, name, HOST, (you),
  Connected / Ready ✓ / Watching / Buffering / Paused, a green or amber dot) and Activity ("now · Elena joined",
  "2m · Maya suggested Heat"), with the footer "If you drop, the room waits 2 minutes for you before it ends." or
  "Host: Elena · guests can pause". When the room starts a title the player opens by itself, once per start (leaving
  the player and coming back to the room does not open it again). The room's end ("The room has ended.") or another
  device taking it over (Rejoin Watch Party) replaces the page.
- The player in a room plays the room's file from the start; play, pause and seek go to the room and the player
  follows its commands; a guest's seek, or pause while the host controls playback, shows the web's words as the
  player's notice ("Only the host can seek the room."). The speed and version settings are the room's, not offered.
  No marked part skips itself: the host is offered Skip Intro (a room seek) unless the profile never skips intros,
  guests see no skip pill. When the host stops, the title finishes or the room ends, the player closes onto the room.
- Back: the navigation view's back arrow, `Alt+Left`, and the mouse back button. The sidebar's sections (Home,
  Search, each library) are the apps' tabs: picking one does not stack on the pages visited before, only Home stays
  under it, so Back walks the pages opened within a section and then goes Home (Android's tab switch pops to the
  anchor tab). Settings and Admin open over the page they came from and go back to it.

## Signed-out screens

Adding a server and signing in look like Silo's web login (measured 2026-10-25). `SiloAuthShellStyle` centres a
448 px card (the surface, radius 12, 28 + 24 px inside) on the background with a red glow at the top left and a
light one at the top right. Above it Granary's lockup, its mark (`Assets/Logo/granary-mark.svg`, 48 px) beside its
name (44 / Bold, plain tracking, apart from Silo's ExtraBold wordmark), and "A Silo client for Windows": Granary's
identity leads these screens, as Silo's developers asked (2026-10-26). Silo's mark, the official flat cut from
siloserver.org/brand (`Assets/Brand`, unmodified, 44 px tall), only refers to the server: beside "Connect to your Silo
server" and beside the server's name when signing in (`SiloAuthMarkStyle`; see the NOTICE there). Granary's logo and
app icon must not look like Silo's; Silo's colours and the three-bar motif are free to use in the interface.

Granary's mark (2026-10-26) is a "G" drawn as a ring from red (#EF4444) to gold (#F59E0B) around a light play
triangle: no slanted bars, no stack. The app icon (`Assets/Logo/granary-icon.svg`, rendered to `granary.ico` at 16 to
256 px) sets it on a dark rounded tile; the exe, the window and the taskbar carry it, and the title bar shows the mark
at 16 px before "Granary". The title bar is 32 px high, as Windows' caption buttons, so the title and the buttons share
one middle line; a 1 px line (the content's border colour) runs under it across the whole window, and beside the
sidebar the content keeps only its left edge, square at the top.

The mark moves (2026-10-26; drafts as SVG in `Assets/Logo`: `granary-mark-intro.svg`, `-loading.svg`, `-ambient.svg`).
`GranaryMark` draws it on the compositor, so it stays smooth while the UI thread is busy: the ring is a circle trimmed
to the G (composition's circles start at 12 o'clock, so the trimming is offset to the G's lower end). At start the
window shows a start screen (`MainWindow.ShowStartup`): the intro in the middle, 132 px (the ring draws itself back
from its upper end with a quarter turn, play pops in, a glint runs round, 1.7 s), then the loading loop while the
first page still loads (Home's rows, `IStartupContent`), at most 6 s in all, then the app fades in (300 ms). The
player shows the loading loop (a stretch of the ring runs through the G, play breathes, 1.4 s) on a dark disc while a
title prepares or buffers. With Windows' animations off the mark stands still.

- In the card: the title (`SiloAuthTitleTextStyle`, 30 / ExtraBold) over a muted lead, labelled 36 px fields
  (`SiloAuthTextBoxStyle`, `SiloAuthPasswordBoxStyle`), full-width buttons rounded 10 (`SiloAuthButtonStyle` red,
  `SiloAuthOutlineButtonStyle`), refusals in a red box (`SiloAuthErrorStyle`) and the consent to an unencrypted
  connection in an amber one (`SiloAuthWarningStyle`).
- The pages merge `Themes/SiloAuth.xaml`, which gives WinUI's text boxes the web's field: the page background, a
  faint outline, a red one when focused (instead of Fluent's accent underline), the placeholder muted.
- Add server: "Connect to your server" and "Type the address you use for Silo." as in the apps; progress and
  refusals below the button. Under the address, "Found nearby" as the apps list it once a server on this network is
  confirmed (nothing before): up to four rows in a rounded card with hairlines between them, each a 40 px disc with
  the name's letter, the name, "On this network · 192.168.1.6:8090", an amber open lock with "Not encrypted" (every
  found address is plain HTTP; sign-in's amber consent follows), and a chevron, a spinner while it connects; Cancel
  stops either way of connecting. A found server that matches a saved one, by id or address, is not offered. A typed
  address that is a saved server still signed in opens it again and asks who is watching, as the apps' server screens
  do ("Opening Living room…"), as long as it answers as the server it was saved as. A saved one without a session, or
  one that cannot confirm it is the same server, goes to sign-in as a new one does; one that answers as another server
  has its saved session forgotten first, without telling it, so the old tokens never reach it.
- Sign in: the server's name, a refusal, the password form, then "Use your phone instead". As on the web, the QR code
  shows on "Show QR code" (at once when the server has no password sign-in), framed, with the address and the code
  below it; the page scrolls to it.

## Settings

Opened from the sidebar's Settings; as on Silo's web the app's sidebar gives way to the settings' own (260 px, a shade
darker, a hairline at its edge): "← Back to App", "Settings", the current profile (its initial in a 32 px disc), then
the pages by group: PLAYBACK (the profile's), APPEARANCE (Navigation & Cards) and APP (Servers, About). The page open is white 6 % with red text and a
red mark. Pages scroll on the right in a centred column of at most 1120 px, padded 50 px (a column, not MaxWidth on the
host, which pushed a page of short lines off the right edge): the title (32 px bold) and its line, then sections between hairlines (`SettingsGroup`), their title
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
- **Navigation & Cards** (`InterfaceSettingsView` on `PosterSettingsViewModel` and `BadgeSettingsViewModel`, the web's
  page of that name): Posters first, with Reset beside the title while the desktop apps have their own choice: Size
  (Compact, Standard, Large) and "Under each poster" (Title and details, Title only, Nothing) as the web's choice buttons
  (`SiloSettingsChoiceButtonStyle`: 32 px, outlined, the chosen one filled in the accent). A change shows on every row
  and grid at once and saves for the desktop apps. While it loads the group says "Loading…"; a failed read or an older
  server shows the web's panel instead of the buttons ("Couldn't load your poster settings", "Your server needs an
  update"), and a value of this PC's own its notice with "Match my other computers". Then Badges,
  with Reset beside the title while the profile has its own (asks first: "Reset badges?"). "Show badges on posters";
  while on, Style as five 112 px tiles, each with its "4K" on a dark slate (the chosen one outlined and tinted in the
  accent), then the categories as a flat accordion (Media Info, Ratings & Certifications, Content Metadata, Status &
  Awards), each header saying "None on" or "2 of 5 on" with a chevron, its rows flush with it between hairlines: name,
  description, a muted italic hint (hidden by the combined Resolution + HDR badge, or data still to come), Adjust while
  on, and the switch. Adjust opens a popover (`SiloSettingsPopoverStyle`): the corner on an 80 × 120 poster with four
  buttons, Color as Default and the web's twelve swatches, and Show icon for a badge that has one. Badges the server
  cannot store (an older manifest) or whose rating it does not show (Rotten Tomatoes) are not offered; a server without
  badge settings shows its own, and the page says so. Every change saves to the profile at once. Then Sidebar, with
  Reset beside the title while something is hidden or moved: Libraries, Discover and Your Stuff, each a list between
  hairlines of the entry's icon and name (Requests and Notifications: "Shows while your server offers it."), up and down
  as 32 px ghost buttons (off at the ends), and the switch; a hidden entry is dimmed. Changes show in the sidebar at
  once.
- **Servers** (`ServersSettingsView` on `ServersViewModel`; the apps keep the server under Settings too): the title and
  its line with Add server (outline) on the right; then a line per saved server between hairlines: a 40 px disc, the
  name (15 px semibold) and "https://silo.example · Signed in as alex" (13 px muted, trimmed), the red Active pill or
  Switch, and More (…) with Sign out and Remove… (asks first). The sign-in screen's "Use another server" still opens the
  standalone servers page, which shares the question before removing.
- **About** (`AboutSettingsView` on `AboutViewModel`, as the apps' About with Version and Acknowledgements): Granary's
  mark (64 px), name and "Version 0.2.0", what it is and that it is not an official Silo app. Updates: the state in one
  line ("Granary is up to date.", "Downloading Granary 0.3.0… 43%", "Granary 0.3.0 is ready. It installs when you
  restart or close Granary.", why a copy does not update) over "Last checked today at 14:05", Check for updates (off
  while it looks, or when the copy cannot update) or Restart now once a release is ready, and "What's new in 0.3.0"
  from the release's notes. What's new: CHANGELOG.md as shipped, a release per heading ("0.2.0 · Oct 27, 2026"), its
  groups in small muted capitals and bulleted items. License and source: View opens LICENSE and THIRD_PARTY_NOTICES.md
  in Notepad, Open the repository. Troubleshooting: the data and logs folders in Explorer.

## Admin

For an administrator (`Account.IsAdmin`) the sidebar offers Admin above Settings, as the web's does. As Settings, the app's
sidebar gives way to the area's own (`AdminPage`, 260 px): "← Back to App", "Admin", MONITOR with Dashboard and
Activity (the web's "2 live" in red beside it while streams run), and BUILD at its foot ("1715 · 13ad7a74", "dev build",
"load failed"). Granary has these two pages only; the rest of the web's admin stays on the web. The pages switch inside
the area, not in the app's history, so Back to App leaves it in one step. Pages scroll in a centred column of at most
1400 px, padded 40 px; cards are the surface with a hairline and 16 px corners. Nothing here changes the server except a
stream's commands on Activity.

- **Dashboard** (`AdminDashboardView` on `AdminDashboardViewModel`, the web's default arrangement without Customize or
  Scan): "Dashboard" and its line, "Updated less than 1 minute ago" and Refresh ("Refreshing..."); read again once a
  minute while shown. Six tiles (Active Streams, Egress now, Profiles · 24h, Total Movies, Total Shows, Storage: label and
  icon, the value 28 px extra bold, a muted line, "Unavailable" in red when its source failed), 3 + 3 where six do not
  fit (`ColumnsPanel`). The health strip: Version, Uptime (amber with "restart required"), Postgres and Redis ("OK" and
  the latency, "Not configured" / optional, red "Unreachable"), Nodes (healthy of enabled, amber below all), Errors · 24h
  (red above none, with the warnings). Now Playing: a card a stream, two a row and four at most (the poster dimmed with
  "Paused", the title opening it, "S1 · E3 — Dark", the method tag in the web's colour, client and profile pills, the
  account's initial and name, "5m ago"), "View all 6 streams ›" and "+2 more active streams" opening Activity; "Nothing
  playing right now" when idle. Playback activity · last 24 h: stacked hour columns (direct play `#5F74EE`, direct stream
  `#169E88`, transcode `#BF7F22`, 2 px apart, rounded on top; a column's tooltip counts them) with the legend and the
  session count (`PlaybackChart`). Egress · last 24 h: the rate over gridlines at human numbers, a fill at 12 %, the line
  broken where the sampler wrote nothing, "Total 4.1 GB" and "Peak 133 Mbps" (`RateChart`). Top titles and Most active
  profiles · last 7 d: eight rows each, the rank, the name (a title opens), the plays and watch time over a bar of the
  busiest row's share. Libraries (an icon box, the name, "movies · 1 path", a green or grey dot) and Users (the eight
  most recently active: initial, name, an "owner" or "admin" badge, the email, "Last active", Active or a red Disabled).
  Recent Activity: "alex (Kids) started watching Dark", "5m ago · Granary 0.3.0". Each card says its own failure in red.
- **Activity** (`AdminActivityView` on `AdminActivityViewModel`, the web's Activity): "Activity", "2 active streams across
  1 node", the live badge, Refresh, and "AUTO REFRESH · Every 10 s" where the web says its realtime state (Granary
  reads the streams again every ten seconds). A notice bar for what a command or Refresh answered. IP Lookup (an
  expander): an address, Lookup or Enter, the accounts seen there over 30 days (User, First Seen, Last Seen, Requests),
  Load more. PLAYBACK METHOD: one bar of the methods' shares and their legend, each a toggle that filters (the others
  dim); BY ROUTING NODE with more than one node. The filter box ("Filter by user, media, client, or node..."), Movies,
  Series, Clear filters, "Showing 1 of 2 streams". A card a stream, newest first: poster, title, "S1E3 · Dark", the
  account, profile, method tag, transcode mode and client; "MKV · HEVC · 2160p · TrueHD 7.1"; on the right Pause or
  Resume (a player that takes live commands), Stop and More (Message…, Terminate, which asks first), "Started 5m ago ·
  5:00", the position and its bar. Details (an expander that stays open across reads): container, video, audio, bitrate,
  route, client with its build, and the address with Look up. "No active streams" or "No streams match your filters"
  when nothing shows.

## Player

`Views/PlayerPage` on `PlayerViewModel`, drawn as the server's web player (`web/src/player`, measured below).

- **Window**: the player is not a page of the frame but lies over the whole window (`PlayerHost`, MainWindow.Player.cs,
  `IPlayerOverlay`), the title bar included: the caption buttons float over the video; the top 32 px stay the drag
  area, so the top bar starts below them. The app under it is neither drawn nor reached and keeps the page the title
  was played from. Full screen (`AppWindow`'s full-screen presenter) shows nothing but the player and returns to the
  window as it was, maximized or not. Leaving the player leaves full screen and stops playback, whichever way it is
  left (Exit, Back, `Alt+Left`, `Ctrl+K`, another page, a session change); the page under it shows again.
- **Notices** (the web's PlaybackNoticeOverlay): a message from the server or an administrator (the control lane,
  api-contract 12.1) shows below the top bar, centred, at most 576 px wide: 16 px corners over dark glass, sky blue
  (`#0EA5E9` at 15 %, its edge `#38BDF8` at 50 %) or amber while the server restarts or shuts down, the title 14 px
  semibold and the message 14 px white at 85 %, over black where the web blurs the picture (`PlaybackNoticeView`). It goes
  after eight seconds, a newer one in its place. Minimized, the window shows it above the bar at the bar's left edge, 12 px
  up, where the web keeps it until the player opens again (Granary's bar still plays the picture); the mini view keeps it. An administrator's stop or terminate ends playback with their
  words on the failed screen ("An administrator ended this playback session.").
- **Minimized** (the web's Minimize player, a 44 px glass disc with a chevron, top left; Exit is top right): the player becomes the bar
  at the bottom of the page, beside the sidebar, which goes on down to the window's edge; the page ends above the bar
  and the viewer browses on. Settings and Admin keep their own sidebar down to the window's edge too (`IDockSpace`):
  only their page ends above the bar. Unlike the web's bar, the picture plays on in it: a 160 by 90 box at its left, a click on
  which brings the full player back, its only way there (under the pointer, or the keyboard, the whole picture greys
  and shows a chevron up). Then the title, the episode line and the times; previous episode, skip back,
  play/pause, skip forward and next episode; on the right mute and Stop playback (✕, as the web bar's). Along its top edge, right of the picture, the full player's seek bar (`DockSeekBar`): thin, thicker under
  the pointer with its thumb and the bubble with the picture, time and chapter above the bar; a click or drag seeks. The bar keeps its controls and the pointer, and leaves the keyboard to the page.
  Minimizing leaves full screen and the mini view and closes the menus; a failure brings the full player back to
  say it. The rail of a title's page opens over the page: the bar's left edge moves aside with it (250 ms, easing
  out), so the pane is reached and the picture plays on beside it, and comes back as the rail closes. A new title replaces the one in the bar; Back and other pages work on the page under it; where the full player
  would leave at a title's end, the bar closes (`INavigationService.LeavePlayer`).
- **Layout**: black, the video filling the page. Over a soft scrim at the top the Minimize disc on the left and the
  "✕ Exit" pill on the right. Bottom bar
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
- **Watch Party panel** (the web's WatchTogetherPanel), while the room plays its title and has not ended or gone to
  another device: 224 px, black at 80 %, a white 10 % border, radius 12, under Exit on the right, fading with the
  controls (faded, it takes no clicks and no keyboard focus). "WATCH PARTY", the connection's dot (emerald connected,
  amber connecting, red disconnected) and its word; the room code in a monospace font with "2 viewers"; who controls
  playback ("Only you control playback" or "Guests can pause & resume" for the host, "Host controls playback" or "You
  can pause & resume" for a guest); each viewer, "(you)" after this one, with Buffering, Syncing, Ready or Connected,
  scrolling past 160 px. For a profile that manages the room (`self_can_manage_room`) a row under a line: Invite
  (copies the invite link, "Invite link is not ready yet." until the room has one), Allow Pause or Host Only, then Stop
  (for everyone, no question) and End in red, which asks "End watch party?" / "End the watch party for everyone?" with
  End Party and Cancel. One call at a time: the buttons wait while one is on its way. Invite and the policy say what they
  did as the player's notice in the web's toast words ("Invite copied. Room code ABCD1234", "Guests can now pause and
  resume"); Stop closes the player back to the room's lobby and End goes on to Watch Party, so they only say a failure.
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
