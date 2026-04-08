# TUI Client TODO

## Log Scrollback Buffer
The current log area overwrites old entries (wraps at `DynamicLastLogRow`).
Add a circular in-memory log buffer and scrollback with PgUp/PgDn keys.

Design notes:
- Keep a `List<(string type, string text)>` of the last N log lines (e.g. 500).
- Track a `scrollOffset` int; 0 = bottom (live view), positive = scrolled back.
- PgUp / PgDn adjust scrollOffset and redraw the log area from the buffer.
- Any new log line while scrolled back should bump a "new messages" indicator
  but not auto-scroll (so the user doesn't lose their place).
- On resize, redraw the log area from the buffer at the new height.
