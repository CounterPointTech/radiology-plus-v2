# Static assets

Drop a short notification sound at `public/ding.mp3` to wire the
"cooking complete" ding. The cooking-progress component will try to
play it on the final Do-the-Do `Succeeded` event and silently fall
back if the file is missing or the browser blocks autoplay.

Recommended: 300–600 ms, ~−6 dBFS, mono, MP3 or M4A renamed to .mp3.
