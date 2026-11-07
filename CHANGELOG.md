# Changelog

All notable changes to Granary are listed here, newest first, as [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
writes them. Granary follows [Semantic Versioning](https://semver.org/). The app shows this file in Settings → About, and a
release's section is what its update says it changes (docs/release.md).

## [Unreleased]

### Fixed

- HDR and Dolby Vision films play directly on more graphics cards, because Granary now checks the film's own codec. A
  card that decodes VP9 only in 8 bits had your Silo server convert every HDR film, HEVC ones too.
- Dolby Vision files with an HDR10, HLG or SDR base layer play directly on a server that wants each base layer named,
  where it converted them.
- Saving mpv.conf or loading a preset no longer fails when Granary happens to be reading the file at that moment.

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
