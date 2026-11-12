# Changelog

All notable changes to Granary are listed here, newest first, as [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
writes them. Granary follows [Semantic Versioning](https://semver.org/). The app shows this file in Settings → About, and a
release's section is what its update says it changes (docs/release.md).

## [Unreleased]

### Added

- **Game controller:** browse and watch with an Xbox or PlayStation controller (DualShock 4, DualSense), where Cross
  is A, Circle is B, Square is X and Triangle is Y. The D-pad or the left stick moves, A picks, B goes
  back, X plays the card you are on, Y searches, Menu (or holding A) opens a card's menu, View goes to the sidebar, the
  bumpers switch tabs, the triggers page and the right stick scrolls. In the player A or X plays and pauses, Left and
  Right seek in steps that grow while you hold them, the triggers scrub, the bumpers jump chapters, the right stick sets
  the volume, Y opens audio and subtitles, Menu the settings and View the playback info; A takes Skip Intro and the next
  episode when they show. Granary listens to the pad only while its window is in front, and a pad that falls asleep
  never stops what plays. Typing with the pad (the search box) is not there yet.
- **Server activity:** an administrator sees a button at the top right of every page while something is going on on
  the server, as on Silo's web, with the number of things on it. It opens a panel that counts the streams playing now
  by play method and leads to Admin → Activity, and lists the scheduled tasks that run with their progress, the
  library scans that run or wait, and the downloads being prepared with their progress and time left. In the admin
  area the button is always there. It follows the server live and says so when the connection is away, and when it
  cannot know every scan it says "at least" rather than a number that may be too small. With a keyboard or a TV
  remote, Up at the top of a page reaches it, on a title's page by way of the Go back button.
- **Quality badges:** a movie's or episode's page shows the quality of its file after the facts under the title, as on
  Silo's web: the resolution, the HDR format and the best audio, such as 1080P, DV HDR10 and DD+ ATMOS. They follow the
  Version picker.
- **New Season:** a Continue Watching card for the first episode of a new season says New Season, as on Silo's web.
- **Full screen stays:** leave the player in full screen, and the next title opens in full screen again, also after a
  restart. Granary opens in full screen when you closed it in full screen.
- **Hero size:** Settings → Navigation & Cards → Pages has a new choice. Full makes the picture at the top of Home, For
  You and Discover as tall as on Silo's web, with the rows under it; Compact, as before, shows the first row without
  scrolling.

### Changed

- The lines under a card are links, as on Silo's web: the mouse underlines the line it is on. On an episode's card the
  series' name opens the series, and the lines under it open the episode, as the picture does.
- The details under the logo at the top of Home and For You name the title's IMDb rating (TMDB's without one) and its
  first two genres, as on Silo's web: 2006 · IMDb 8.4 · Drama · Crime · TV-PG.
- A double click on the picture switches full screen also when the Full screen button is hidden in Settings → Playback →
  Player buttons, as F11 does.

### Fixed

- The picture at the top of Home, For You and Discover shows its upper part, as on Silo's web, where it was cut around
  its middle and took the heads off many pictures. With Hero size set to Full it runs over the whole width, as on the
  web.
- That picture is sharp on a wide window: Home and For You fetch it as wide as it is shown, where it was always 1280
  pixels wide.
- A Next Up card says Next Episode, as on Silo's web.
- Playback info names a Dolby Atmos track also when its title only holds a word such as "Atmospheric".
- A Continue Watching card's time left is rounded to the nearest minute, as on Silo's web and apps, where it could say a
  minute more.
- Playback no longer stops to buffer when your server or a proxy in front of it cuts the video connection now and then.
- A title's page shows where you stopped when you come back from the player, where Resume from kept the old time.
- In full screen the mouse cursor hides again after a few seconds, also where it rests at the edge of the screen, and
  also when the mouse lies still. It no longer comes back by itself when the mouse did not move, nor stays when Windows
  draws its arrow over the picture all the same.
- With a TV remote, the arrows in the player go past the volume slider again on a display scaled above 100 %, such as a
  TV at 150 %. There focus stopped on the slider, every arrow changed the volume, and the buttons beside it (audio,
  subtitles and the rest) were out of reach.

## [0.3.1] - 2026-11-09

### Added

- **Volume stays:** the player remembers its volume and mute on this PC, for the next title and after a restart.
- **Window place:** Granary opens where you closed it, at the same size, maximized if it was.

### Fixed

- Playback no longer buffers without end when you press Play after a long pause.

## [0.3.0] - 2026-11-08

### Added

- **Player buttons:** Settings → Playback → Player buttons shows, hides and orders the player's buttons. A hidden
  button's keys are off too, except F11 and the minimized player's own Mute and volume keys.
- **When it ends:** the player shows the clock time the title ends, such as "Ends 22:41".
- **Stream dot:** a dot in the player's gear is green for Direct Play or Direct Stream and amber while your Silo server
  transcodes.
- **Sleep timer:** the player's Sleep button pauses after 5 to 60 minutes, or stops at the end of the episode or movie
  instead of playing the next one.
- **Audio sync offset:** moves the sound up to 5 seconds earlier or later, in Settings → Playback or the player's menu.
- **Chapter keys:** Ctrl+Right and Ctrl+Left jump between chapters.
- **Taskbar preview:** Play/Pause, and Previous and Next episode, in the preview of Granary's taskbar button.
- **Discord status:** Settings → Playback → This PC can show what you're watching in Discord.
- **Theme music:** a title's page can play its theme quietly (Settings → Title Pages), when your Silo server has one.
- **Title art and advisory age:** Settings → Title Pages chooses a title's logo or its name, and can show an advisory
  age such as "Common Sense 13+".
- **Spoilers:** Settings → Title Pages can blur the stills and hide the descriptions of episodes you haven't started.
- **Star ratings:** rate a movie or series from 1 to 5 stars on its page.
- **Box sets:** a film's page lists its box set, which has its own page and shows in Search.
- **Extras:** title pages show the featurettes, deleted scenes and other extras your Silo server has.
- **Missing and upcoming episodes:** a season marks missing episodes and lists the ones still to air.
- **Cast & Crew:** the row marks where the writers and the cast begin.
- **Watch Party from a title's page:** its ⋮ menu starts a party with that title and copies the invite.
- **Home's hero banner:** a Home row set as the hero banner on Silo's web slides through its titles at the top of Home.
- **Title preview switch:** Settings → Navigation & Cards can turn off the large title preview at the top of Home, For
  You and Discover. A hero banner set on Silo's web stays.

### Changed

- **Resume** starts exactly where you stopped instead of 7 seconds earlier. Settings → Playback → This PC → Rewind on
  resume brings a lead-in back.
- **Time left stays:** click the player's time to show the time left, and the player remembers it.
- **Player bar:** Audio and Subtitles sit side by side, and Audio has the sound-wave icon of Silo's web player.
- **More Episodes** shows the whole season, with the episode you're on marked Now Viewing.

### Fixed

- Granary takes your Silo server's accent colour and logo also when the server was out of reach at start, and
  remembers them. Without an accent set on the server it uses Silo's web light accent instead of red.
- The display no longer turns off while something plays.
- HDR and Dolby Vision films play directly on more graphics cards and servers.
- A TV remote works right after start and after picking a profile, its OK switches tabs and choices, and the mouse
  cursor hides over the picture while you use it.
- For You and Discover no longer keep an empty space or an old title where their preview was.
- A card you clicked no longer stays highlighted when you come back with the mouse's Back button.
- Saving mpv.conf or loading a preset no longer fails now and then.
- The Opacity boxes in Subtitle appearance and the badges in a card's right corners are no longer cut off.

## [0.2.1] - 2026-11-05

### Added

- **Still watching prompt:** Settings → Playback → This PC sets how many episodes auto-play in a row before Granary waits
  for you: 2 to 5, Unlimited, or a number of your own up to 20. The player's screen now says "Still watching?" when it
  waits.

### Changed

- Settings → Playback → This PC says that with a fixed Quality, your server can still convert a file, even with Direct
  play without hardware decoding on.

### Fixed

- Playback Info says "transcoded" when your Silo server re-encodes a video to HEVC, where it said "copy".

## [0.2.0] - 2026-11-04

### Added

- **Your own `mpv.conf`:** Settings → Playback → This PC. Edit it inside Granary, with the keyboard or a TV remote, or in
  your own editor. Presets keep configurations under a name, and Unload brings your own file back. A change applies from
  the next title, without a restart. Granary keeps the options it needs to show the video, a file with options that could
  send the stream's address elsewhere isn't used, and Settings and the log say why.
- **Choose what to play:** a movie's or episode's page has Version, Audio and Subs under its buttons, as on Silo's web
  app. Your audio and subtitle choices are remembered for the movie or the whole series.
- **Server logo:** the sidebar shows the logo your Silo server's admin set in place of the server's name, as on Silo's web
  app. Settings → Navigation & Cards → Sidebar → Server logo turns it off.
- **TV remote and keyboard:** the arrows move focus on every page, Back goes back a step at a time, and OK and the remote's
  Play button act on what has focus. In the player OK skips an intro or brings up the controls.
- **More filters** for libraries, Favorites, Watchlist, History and collections: several genres at once, decades, 4K, HDR,
  Dolby Vision, languages and Watch Status. Filters show as badges you can remove one by one, as on Silo's web app.
- **More sorts:** Latest Episode Air Date, Latest Episode Added, Content Rating, TMDB and Rotten Tomatoes ratings,
  Resolution, Bitrate, Progress, Date Viewed and Plays.
- **Direct play without hardware decoding:** Settings → Playback → This PC. Plays HEVC, VP9, AV1 and HDR files directly,
  decoded on the processor where your graphics card can't, rather than converted by your Silo server. Slower PCs can
  stutter, so it's off by default.
- The log says why a title is converted.

### Changed

- In the player, Up shows the controls and Down opens the audio menu, as on Silo's TV app. Settings → Playback → This PC →
  Up and Down in the player brings back the volume keys.
- The sorts are named as on Silo's web app: Date Added, Release Date, IMDb Rating and Duration.

### Fixed

- Switching quickly between sections and scrolling freezes the app less often and for shorter, most of all in a large
  window. Pages you left no longer stay in memory.

## [0.1.1] - 2026-11-01

### Added

- The player's seek bar shows which parts of the video are loaded, lighter on the timeline, as on Silo's web player.
- While the player switches quality, version or a track, or moves to a part the stream does not hold yet, a small
  loading ring shows at the bottom right until the new stream plays and the settings button shows "…" in place of the
  quality. The picture plays on meanwhile; once it stops for the new stream for more than half a second, the spinner in
  the middle shows too. Screen readers hear what the switch does ("Switching quality") once it has lasted half a second,
  also in the mini view and the minimized player.
- Full screen for the whole app: F11 (in a trailer too) or the button at the top right. In full screen the
  top right shows Exit full screen and Close.

### Changed

- The loading animation turns clockwise, at an even pace all the way round, instead of stopping at the G's opening.
- The player's spinner waits half a second before it shows, as on Silo's web, so short stalls no longer flash it. The
  settings button leaves the quality out in a narrow window.
- HDR and Dolby Vision files, 4K among them, play directly from the server when your graphics card decodes 10-bit
  video: Granary maps their picture onto your display itself, so the server sends the file as it is instead of
  converting it to SDR in a transcode.

### Fixed

- Minimizing or leaving the player no longer drops the app out of full screen, and the mini view goes back to full screen
  when it was opened from there.
- Back from full screen or the mini view, the window lands on a display, also when its own was unplugged meanwhile.
- Minimizing the player, bringing it back or switching full screen no longer flashes a corner of the picture for a
  moment.

## [0.1.0] - 2026-11-01

### Added

- Signing in to Silo servers with a password or a code on the phone, profiles with their PINs, and several servers to
  switch between.
- Silo servers on the local network show under "Found nearby" when you add a server, and their adverts give the
  local route at home another way in.
- Home, the libraries, Search with people and Request to add, For You, Calendar, Notifications, Favorites, Watchlist,
  History and Collections, in the look of Silo's web app. Search's Request to add shows TMDB's titles in one grid that
  reads on as you scroll.
- Requests: Discover, studios, networks and genres, the titles from TMDB with their seasons, your requests and the
  watchlist's titles not in the library yet. A title from TMDB can be requested straight from its card: Request shows
  in the middle of the poster under the pointer.
- The player, built on mpv: direct play and the server's streams, audio and subtitle tracks with their look, intro and
  credits skipping, the next episode, chapters, trickplay previews and a mini view.
- Settings for playback and subtitles, the servers, and About with Check for updates.
- Posters with your profile's badges, as on Silo's web, chosen under Settings → Navigation & Cards; Granary takes the
  server's accent colour.
- The sidebar's entries can be hidden and put in another order, for each profile, under Settings → Navigation & Cards.
- Posters in three sizes, Compact, Standard and Large, as many to a row as on Silo's web at the same window size, with
  their title and details, the title only, or nothing under them; under Settings → Navigation & Cards → Posters. On a
  wide or ultrawide window a grid takes more columns, so its posters stay the size they are in the rows.
- Installing and updating with Velopack: a setup without administrator rights, and updates in the background. With
  automatic updates turned off in Settings → About, Granary checks only when you ask and installs once you choose to.
- The player minimizes to a bar at the bottom of the window that keeps playing while you browse.
- An administrator's pause, stop and messages reach Granary's player.
- Admin: the server's Dashboard and Activity, with the streams playing now, for its administrators.
- History: Select titles and remove their watch data.
- Notifications: the Series view shows which series notify you and why, with search, reasons, sort and Mute.
- Libraries a profile hides on the web are hidden in Granary too, in the profile's order.
- Home and the unread count follow what happens on your other devices, live.
- Collections of your own: make a manual collection, name it, fill it from a search, put its titles in order, share it
  with the other profiles on your account, and delete it; and Add to Collection from a movie's or series' page.
- A person's page from cast, crew and Search: their photo, life, and the titles they're in.
- Smart collections: titles that match your rules, with a live preview of what they find.
- Filters on Favorites, Watchlist, History and a collection's page; a library keeps its sort and filters.
- Watch Party: open a room in which the host picks or everyone votes, share its code or invite link, and play, pause and
  seek together.
- Watch Party in the player: a panel with the room's code, its viewers and who controls playback, and for the host
  Invite, Allow Pause, Stop and End, as on Silo's web player.
- The keyboard's media keys and Windows' media overlay control the player: play and pause, skip, seek, and the previous
  and next episode, with the title and its poster shown; in a Watch Party they go to the room.
- Subtitle timing in the player: Sync to audio and Reset timing in the subtitle menu, each track's timing under its
  name, and a card that follows a sync until the new timing shows, as on Silo's web player.
- Home, For You and Requests' Discover open with a large picture of a title, its logo (or name), details and overview,
  and follow the card under the mouse or in focus; while you scroll it folds into a slim header that stays at the top.
  On a tall window, such as an ultrawide, the picture grows with it.
- Search shows the result under the mouse or in focus as a dark picture behind its title and search box.
- Title pages: the backdrop stays put behind a large logo, one line of facts, the overview and bigger
  buttons; Info shows the full details, cast, crew and files.
- Settings → Devices: see where your account is signed in and sign out any session, and see the devices your profile
  watches on, rename them, set them back to your profile settings, or forget them.
- Trailers play in Granary, in YouTube's own player over the title's page; full screen works, Esc and Back close a
  trailer even after a click into the video, and a trailer that YouTube won't play here offers "Open on YouTube". The
  minimized player pauses for it and stays paused, unless it plays a Watch Party.

### Security

- Signed updates: Granary installs an update only when its release is signed with Granary's own key and the download
  is the file the release signed, and the notes it shows for an update are the ones that file carries. Settings → About
  says when an update couldn't be verified, and Granary tries again later.
