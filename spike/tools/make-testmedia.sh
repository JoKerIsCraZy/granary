#!/usr/bin/env bash
# Generates the spike's test media with ffmpeg (BtbN GPL build, needs libx264 and libx265).
# Usage: make-testmedia.sh <ffmpeg.exe> <output-dir>
set -euo pipefail
FF="$1"
OUT="$2"
mkdir -p "$OUT/hls"
cd "$OUT"

cat > chapters.txt <<'EOF'
;FFMETADATA1
title=Granary SDR test
[CHAPTER]
TIMEBASE=1/1000
START=0
END=10000
title=Opening
[CHAPTER]
TIMEBASE=1/1000
START=10000
END=20000
title=Middle
[CHAPTER]
TIMEBASE=1/1000
START=20000
END=30000
title=Ending
EOF

cat > sub.en.srt <<'EOF'
1
00:00:01,000 --> 00:00:04,000
Embedded English subtitle one

2
00:00:12,000 --> 00:00:16,000
Embedded English subtitle two
EOF

cat > external.en.vtt <<'EOF'
WEBVTT

00:00:02.000 --> 00:00:06.000
External WebVTT subtitle (fetched over HTTP)

00:00:14.000 --> 00:00:18.000
External WebVTT subtitle, second cue
EOF

# 30 s 1080p H.264 with a visible second counter, two audio tracks, an SRT track and chapters.
"$FF" -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc=size=1920x1080:rate=24:duration=30" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=30" \
  -f lavfi -i "sine=frequency=880:sample_rate=48000:duration=30" \
  -i sub.en.srt -i chapters.txt \
  -map 0:v -map 1:a -map 2:a -map 3:s -map_metadata 4 -map_chapters 4 \
  -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p \
  -c:a aac -b:a 128k -c:s srt \
  -metadata:s:a:0 language=eng -metadata:s:a:0 title="English 440 Hz" \
  -metadata:s:a:1 language=ger -metadata:s:a:1 title="Deutsch 880 Hz" \
  -metadata:s:s:0 language=eng sdr.mkv

# 10 s 4K HEVC Main10 HDR10 (PQ, BT.2020, mastering display 1000 nits, MaxCLL 1000/400).
"$FF" -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=3840x2160:rate=24:duration=10" \
  -vf "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv" \
  -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le \
  -x265-params "master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400:hdr10=1:hdr10-opt=1:repeat-headers=1:log-level=error" \
  -tag:v hvc1 hdr10.mkv

# HLS (MPEG-TS segments) from the SDR file, to check that segment requests carry the header.
"$FF" -hide_banner -loglevel error -y -i sdr.mkv -map 0:v -map 0:a:0 -c copy \
  -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename "hls/seg_%03d.ts" hls/index.m3u8
