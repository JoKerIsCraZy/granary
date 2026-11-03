# Changelog

All notable changes to Granary are listed here, newest first, as [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
writes them. Granary follows [Semantic Versioning](https://semver.org/). The app shows this file in Settings → About, and a
release's section is what its update says it changes (docs/release.md).

## [Unreleased]

### Added

- A movie's or episode's page lets you choose what to play before you press Play, as on Silo's web: Version, Audio and
  Subs under the buttons. Version lists the title's files when it has more than one ("2160p · HEVC · HDR10 · DTS",
  with size and source such as "38.9 GB · Remux"); Audio and Subs list the chosen file's tracks, with Auto and Off.
  Each shows what would play anyway until you choose. Play, Resume and Start over then play that file with those
  tracks, and the server keeps to the file you chose. Your audio and subtitle choices are remembered for the movie or
  the whole series, and Auto goes back to your profile's settings; if a choice cannot be saved, the page says so and
  goes back to the choice that was kept. The pickers work with the keyboard too.
- Settings → Playback → This PC: **Direct play without hardware decoding**. On, Granary plays HEVC, VP9, AV1 and HDR
  files directly and decodes them on the processor where the graphics card can't, instead of having your Silo server
  convert them: the picture keeps its full quality and the server does less work, but slower PCs can stutter. Off by
  default; it is kept on this PC and counts from the next title you play.
- Settings → Playback → This PC: **mpv configuration**. Granary plays with your own `mpv.conf` from the `mpv` folder in
  its data directory, in mpv's own syntax with profiles, `profile-cond` and `include`, and `~~/` for that folder, for
  shaders, scaling, debanding, tone mapping and the like. **Open mpv.conf** makes the file and opens it in your editor;
  **Open folder** is there for shaders. Changes apply after you restart Granary. Granary keeps the options it needs to
  show the video in its window and follow your Silo server's choices, and the log names the options of yours it leaves
  out and any option mpv can't use. A file that sets an option that could take the stream's address out, such as mpv's
  own log, a proxy, scripts or commands, or one that would turn Granary's options off, its includes too, plays as if it
  were not there, and the log says why. If one of its profiles keeps changing Granary's options while a title plays,
  Granary stops that title and plays on without the file. The row says when your file was not used, or only in part,
  and why, with a link to the log. Without the file, playback is as before.
- Settings → Playback → This PC → mpv configuration: **Edit** opens `mpv.conf` in an editor inside Granary, usable
  with the keyboard or a TV remote. It holds the file exactly as written, profiles, includes and comments too, and
  checks the text as you type: which options Granary keeps for itself, and whether the player would not use the file
  at all. Save writes the file in one piece and goes back to Settings, which offers to apply it;
  Cancel, Esc or the remote's Back close it, asking first when there are unsaved changes.
- Settings → Playback → This PC: **mpv presets**. **Save as preset…** keeps your `mpv.conf` under a name, as a file
  in the `presets` folder next to it that you can see and back up; a name already taken is replaced only after asking.
  Each preset has **Load**, which replaces `mpv.conf` with it after asking, and **Delete**. Names are made safe for
  Windows as you type them. Settings names the preset in use, and says "(changed)" once you edit it; **Unload** goes
  back to your own `mpv.conf` from before you loaded any preset, which Granary keeps aside on the first load (or removes
  `mpv.conf` again if you had none).
- Settings → Playback → This PC: **Apply now** after you save `mpv.conf` in Granary, load a preset or unload one. The
  player starts mpv again with the new file without restarting Granary, and the row then says how the file fared.
  Playback is never cut: while a title plays, Settings says "Applies to the next title." and the new file counts from
  the next title, or once the player closes. Volume, speed, subtitles and the picture's fill carry over. If mpv cannot
  start again, Settings says so and Granary plays on with the mpv it had; Apply now can be tried again.
- The log says why a title is converted: what this PC's graphics card decodes, what Granary told the server it plays,
  and the reason the server gave for its choice.
- More filters for a library, and for Favorites, Watchlist, History and collections: several genres at once, decades,
  4K, HDR and Dolby Vision, the original, audio and subtitle languages, and Watch Status (Unwatched, In Progress,
  Watched). The Filters panel scrolls when it gets long, and a library opens with these filters as you left them.
  The filters you set show as badges under the bar, as on Silo's web app; each badge's ✕ removes just that filter.
- More ways to sort a library: Latest Episode Air Date and Latest Episode Added in a series library, Content Rating,
  TMDB Rating, the Rotten Tomatoes critic and audience ratings while your server shows them, Resolution, Bitrate,
  Progress, Date Viewed and Plays.
- A TV remote on a media PC, or the keyboard alone: the arrows move focus on every page and between the sidebar and
  the page, the focused card comes to the middle, and Back (Esc, Backspace or the remote's Back button) goes back to the
  card you came from, then to the section's sidebar entry, then Home. Menus and dialogs close on the remote's Back too.
  The focus ring is thicker and goes round the whole card, artwork and text.
  A title opened with OK starts on Play, and the remote's Play button plays the card, episode or title that has focus;
  the Menu key opens a card's menu. In the player OK skips the intro (or recap, credits) while its pill shows, else
  brings focus to the controls, also when an air mouse already shows them; the arrows then move to subtitles, audio and the settings, and Back closes their menus and
  panels, hides the controls, then closes the player, with focus back where it was. The minimized player is reachable
  with Down from the page.

### Changed

- The sorts are named as on Silo's web app: Date Added, Release Date, IMDb Rating, and Duration in place of Runtime.
- In the player, Up and Down no longer change the volume by default, as on Silo's TV app: Up shows the controls with
  focus on Play/Pause, and Down opens the audio menu (or subtitles, or the playback settings). Settings → Playback →
  This PC → Up and Down in the player → Volume brings back the volume keys. A remote's own volume buttons,
  M to mute and the volume slider set it either way. Left and Right still skip.

### Fixed

- Switching quickly between sections and scrolling freezes the app less often and for shorter, most of all in a large
  window: the full memory clean-ups that Windows' UI framework asks for now and then no longer go through every loading
  placeholder of every grid again and again. Pages you left no longer stay in memory.

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
