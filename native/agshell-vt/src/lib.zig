//! agshell-vt: the live daemon's terminal state, as zmx's daemon keeps it. serializeTerminalState, rewritePromptRedraw,
//! resizeTerm and their tests are zmx's (src/util.zig and src/loop.zig at 8bab1f0, MIT, Copyright (c) 2025 Eric Bower),
//! with zmx 186393f's scrollback fix and synchronized output defer and 770eb5f's cursor style in serializeTerminalState,
//! and their tests (src/terminal.zig);
//! isUserInput and its tests are zmx main 2626f17's (src/terminal.zig, src/util.zig);
//! serializeScreenText, encodeTyped and their tests are agterm's zmx patch 0001 (scripts/zmx-patches, MIT, Copyright (c) 2026 Umputun, patches\ghostty\LICENSE-agterm);
//! the exports below are the C ABI agshell-live calls.
const std = @import("std");
const builtin = @import("builtin");
const ghostty_vt = @import("ghostty-vt");
const testing = std.testing;

const c_alloc = std.heap.c_allocator;

/// zmx's cfg.zig default.
pub const default_max_scrollback_lines: usize = 10_000;

const Vt = struct {
    threaded: std.Io.Threaded,
    term: ghostty_vt.Terminal,
    stream: ghostty_vt.TerminalStream,
};

/// zmx's initTerminal: the line limit controls scrollback.
export fn agshell_vt_new(cols: u16, rows: u16, max_scrollback_lines: usize) ?*Vt {
    const vt = c_alloc.create(Vt) catch return null;
    vt.threaded = .init_single_threaded;
    vt.term = ghostty_vt.Terminal.init(vt.threaded.io(), c_alloc, .{
        .cols = cols,
        .rows = rows,
        .max_scrollback_lines = if (max_scrollback_lines == 0) default_max_scrollback_lines else max_scrollback_lines,
        .max_scrollback_bytes = null,
    }) catch {
        vt.threaded.deinit();
        c_alloc.destroy(vt);
        return null;
    };
    vt.stream = vt.term.vtStream();
    return vt;
}

export fn agshell_vt_free(vt: ?*Vt) void {
    const v = vt orelse return;
    v.stream.deinit();
    v.term.deinit(c_alloc);
    v.threaded.deinit();
    c_alloc.destroy(v);
}

/// The pty's output, fed to the terminal as zmx's daemon loop does.
export fn agshell_vt_write(vt: *Vt, data: [*]const u8, len: usize) void {
    vt.stream.nextSlice(data[0..len]);
}

export fn agshell_vt_resize(vt: *Vt, cols: u16, rows: u16) bool {
    resizeTerm(c_alloc, &vt.term, cols, rows) catch return false;
    return true;
}

/// zmx's re-attach replay: the terminal state with OSC 133;A rewritten to redraw=0. Null when there is nothing to send;
/// the caller frees the bytes with agshell_vt_bytes_free.
export fn agshell_vt_snapshot(vt: *Vt, out_len: *usize) ?[*]u8 {
    const term_output = serializeTerminalState(c_alloc, &vt.term) orelse return null;
    const restore_data = rewritePromptRedraw(c_alloc, term_output) orelse {
        out_len.* = term_output.len;
        return @constCast(term_output.ptr);
    };
    c_alloc.free(term_output);
    out_len.* = restore_data.len;
    return @constCast(restore_data.ptr);
}

/// agterm's zmx ScreenHeader without the revision, which the daemon keeps.
pub const ScreenHeader = extern struct {
    cols: u16,
    rows: u16,
    /// zero-based, in the active screen
    cursor_x: u16,
    cursor_y: u16,
    alternate: u8,
};

/// agterm's zmx handleScreen: the plain text of the active screen, or with `all` of scrollback plus the active screen,
/// and the geometry and cursor. False when the read failed; on success the caller frees the bytes with
/// agshell_vt_bytes_free, an empty text included.
export fn agshell_vt_screen(vt: *Vt, all: bool, out_ptr: *?[*]u8, out_len: *usize, out_header: *ScreenHeader) bool {
    const text = serializeScreenText(c_alloc, &vt.term, all) catch return false;
    out_header.* = screenHeader(&vt.term);
    out_ptr.* = text.ptr;
    out_len.* = text.len;
    return true;
}

fn screenHeader(term: *ghostty_vt.Terminal) ScreenHeader {
    const screen = term.screens.active;
    var header = std.mem.zeroes(ScreenHeader);
    header.cols = @intCast(term.cols);
    header.rows = @intCast(term.rows);
    header.cursor_x = @intCast(screen.cursor.x);
    header.cursor_y = @intCast(screen.cursor.y);
    header.alternate = @intFromBool(term.screens.active_key == .alternate);
    return header;
}

/// agterm's zmx handleType: `typed` as the keystrokes the program's keyboard mode asks for, each CR a Return key. Null
/// when encoding failed; the caller frees the bytes with agshell_vt_bytes_free, an empty result included.
export fn agshell_vt_encode_typed(vt: *Vt, typed: [*]const u8, len: usize, out_len: *usize) ?[*]u8 {
    const keys = encodeTyped(c_alloc, &vt.term, typed[0..len]) catch return null;
    out_len.* = keys.len;
    return keys.ptr;
}

/// zmx's setPwd source, `term.getPwd()`: the working directory the program last reported with OSC 7, as reported. Null
/// when there is none; the bytes are the terminal's and hold until its next write.
export fn agshell_vt_pwd(vt: *Vt, out_len: *usize) ?[*]const u8 {
    const pwd = vt.term.getPwd() orelse return null;
    out_len.* = pwd.len;
    return pwd.ptr;
}

/// zmx's isUserInput: whether a client's input is a person typing (text, Return, a key) rather than its terminal's
/// replies, mouse or focus reports; an unmanaged client that types takes the lead.
export fn agshell_vt_is_user_input(data: [*]const u8, len: usize) bool {
    return isUserInput(data[0..len]);
}

export fn agshell_vt_bytes_free(ptr: ?[*]u8, len: usize) void {
    const p = ptr orelse return;
    c_alloc.free(p[0..len]);
}

// zmx main 2626f17 src/terminal.zig and src/util.zig: isUserInput, with the fixes that keep terminal replies from taking the lead
/// True when a CSI carries a private marker (`?`, `>`, `=`, `<`). Keyboard
/// encodings never use one, but the replies a terminal sends on its own do:
/// kitty keyboard flags (`CSI ? flags u`), DA1 (`CSI ? 62;22 c`), DA2
/// (`CSI > 1;10;0 c`), DECRPM (`CSI ? 2026;2 $ y`), color scheme (`CSI ? 997;1 n`).
fn hasPrivateMarker(csi: ghostty_vt.Parser.Action.CSI) bool {
    for (csi.intermediates) |b| {
        if (b == '?' or b == '>' or b == '=' or b == '<') return true;
    }
    return false;
}

/// Detect if the payload contains user input that should be printed to the screen or
/// is a key combination like up-arrow, backspace, enter, ctrl+f, etc.
///
/// Terminal-generated replies must not count: every attached client's terminal
/// answers the queries the app broadcasts, so a reply from a passive viewer
/// would otherwise make it leader and resize the session to its window.
pub fn isUserInput(payload: []const u8) bool {
    var parser = ghostty_vt.Parser.init();
    var i: usize = 0;
    while (i < payload.len) {
        if (payload[i] == 0x1b and i + 2 < payload.len and payload[i + 1] == '[') {
            if (parseKittyCsiU(payload[i + 2 ..])) |kitty| {
                if (kitty.event_type != 3) return true;
                i += 2 + kitty.consumed;
                continue;
            }
        }

        const actions = parser.next(payload[i]);
        for (actions) |action_opt| {
            const action = action_opt orelse continue;
            switch (action) {
                .print => return true, // printable characters
                .csi_dispatch => |csi| {
                    // A reply, not a key: skip it and keep scanning, since a
                    // real keystroke can arrive in the same read.
                    if (hasPrivateMarker(csi)) continue;
                    // kitty keyboard: CSI ... u or CSI ... ~
                    // legacy modified keys: CSI 27 ; ... ~
                    // arrow/function keys with modifiers: CSI 1 ; <mod> A-D
                    // CSI ? flags u is the terminal's reply to a kitty keyboard status
                    // query, not a key press; skip it and keep scanning the payload
                    const status_reply = csi.final == 'u' and csi.intermediates.len > 0 and csi.intermediates[0] == '?';
                    if (!status_reply and (csi.final == 'u' or csi.final == '~')) return true;
                    // modified arrow keys (e.g., Ctrl+F sends CSI 1;5C in legacy mode)
                    if (csi.final >= 'A' and csi.final <= 'D' and csi.params.len > 1) return true;
                    // mouse events: CSI M (basic) or CSI < (SGR extended) - EXCLUDE these
                    // only intentional keyboard input should trigger leader switch
                    if (csi.final == 'M' or csi.final == '<') return false;
                    // focus events: CSI I (focus in) or CSI O (focus out) - EXCLUDE these
                    // these are automatic terminal events, not user typing
                    if (csi.final == 'I' or csi.final == 'O') return false;
                },
                .execute => |code| {
                    // looking for CR, LF, tab, and backspace
                    if (code == 0x0D or code == 0x0A or code == 0x09 or code == 0x08) return true;
                },
                else => {},
            }
        }
        i += 1;
    }
    return false;
}

pub const KittyCsiU = struct {
    key_code: u32,
    modifiers: u32,
    event_type: u32,
    consumed: usize,
};

pub fn parseKittyCsiU(buf: []const u8) ?KittyCsiU {
    var pos: usize = 0;

    // 1. Parse key code.
    const key_code = parseDecimal(buf, &pos) orelse return null;

    // 2. Skip any ':alternate-key' sub-fields (shifted key, base layout key).
    while (pos < buf.len and buf[pos] == ':') {
        pos += 1; // consume ':'
        _ = parseDecimal(buf, &pos); // consume digits (may be empty for ::base)
    }

    // 3. Expect ';' separator before modifiers.
    if (pos >= buf.len or buf[pos] != ';') return null;
    pos += 1;

    // 4. Parse modifier value. Kitty encodes as 1 + bitfield.
    const mod_encoded = parseDecimal(buf, &pos) orelse return null;
    if (mod_encoded < 1) return null;
    const mod_raw = mod_encoded - 1;

    var event_type: u32 = 1;
    // 5. Parse optional event type after ':'.
    if (pos < buf.len and buf[pos] == ':') {
        pos += 1;
        event_type = parseDecimal(buf, &pos) orelse return null;
    }

    // 6. Skip optional ';text-codepoints' section.
    if (pos < buf.len and buf[pos] == ';') {
        pos += 1;
        // Consume remaining digits and colons until 'u'.
        while (pos < buf.len and (std.ascii.isDigit(buf[pos]) or buf[pos] == ':')) {
            pos += 1;
        }
    }

    // 7. Expect terminal 'u'.
    if (pos >= buf.len or buf[pos] != 'u') return null;
    pos += 1;

    return .{
        .key_code = key_code,
        .modifiers = mod_raw,
        .event_type = event_type,
        .consumed = pos,
    };
}

/// Parse a decimal integer from buf starting at pos, advancing pos past the
/// consumed digits. Returns null if no digits are present.
fn parseDecimal(buf: []const u8, pos: *usize) ?u32 {
    const start = pos.*;
    var value: u32 = 0;
    while (pos.* < buf.len and std.ascii.isDigit(buf[pos.*])) {
        value = value *% 10 +% (buf[pos.*] - '0');
        pos.* += 1;
    }
    if (pos.* == start) return null;
    return value;
}

// zmx src/loop.zig
/// Resize the daemon's terminal with prompt_redraw disabled. On resize the
/// terminal would clear prompt lines expecting the shell to redraw them,
/// but the shell's redraw goes to the PTY (forwarded to clients), not to
/// this terminal, so the clearing only corrupts our snapshot state.
fn resizeTerm(gpa: std.mem.Allocator, term: *ghostty_vt.Terminal, cols: u16, rows: u16) !void {
    const saved = term.flags.shell_redraws_prompt;
    term.flags.shell_redraws_prompt = .false;
    defer term.flags.shell_redraws_prompt = saved;
    try term.resize(gpa, .{ .cols = cols, .rows = rows });
}

// zmx src/util.zig

/// OSC 133;A (prompt start) marker.
const OSC_133_A = "\x1b]133;A";

/// Rewrite OSC 133;A sequences to include `redraw=0`, which tells the outer
/// terminal not to clear prompt lines on resize. This is necessary because
/// zmx sits between the shell and the outer terminal: from the outer terminal's
/// perspective, the foreground process (zmx client) cannot redraw prompts.
/// Without this, the outer terminal clears the prompt on resize expecting the
/// shell to redraw it, but the shell's redraw goes through zmx's IPC path with
/// cursor coordinates relative to the inner PTY, causing a cursor desync that
/// makes the prompt invisible.
/// See: https://github.com/neurosnap/zmx/issues/111
pub fn rewritePromptRedraw(alloc: std.mem.Allocator, data: []const u8) ?[]const u8 {
    // Fast-path: most PTY output has no escape sequences at all. A scalar
    // byte scan for ESC is cheaper than the full string indexOf below.
    if (std.mem.indexOfScalar(u8, data, '\x1b') == null) return null;
    if (std.mem.indexOf(u8, data, OSC_133_A) == null) return null;

    var result = std.ArrayList(u8).initCapacity(alloc, data.len + 200) catch return null;
    errdefer result.deinit(alloc);
    result.appendSlice(alloc, data) catch return null;

    // Work backwards so index shifts don't invalidate later positions.
    var search_from: usize = result.items.len;
    while (search_from > 0) {
        const haystack = result.items[0..search_from];
        const pos = std.mem.lastIndexOf(u8, haystack, OSC_133_A) orelse break;
        search_from = pos;

        const after = pos + OSC_133_A.len;
        if (after >= result.items.len) continue;

        // Find the string terminator (BEL \x07 or ST \x1b\\).
        var term_pos: ?usize = null;
        var j = after;
        while (j < result.items.len) : (j += 1) {
            if (result.items[j] == '\x07') {
                term_pos = j;
                break;
            }
            if (result.items[j] == '\x1b' and j + 1 < result.items.len and result.items[j + 1] == '\\') {
                term_pos = j;
                break;
            }
        }
        const end = term_pos orelse continue;

        // Check the parameter region between OSC_133_A and the terminator.
        const params = result.items[after..end];

        // If redraw=0 already present, skip.
        if (std.mem.indexOf(u8, params, "redraw=0") != null) continue;

        // If redraw= exists with a different value, replace it.
        if (std.mem.indexOf(u8, params, "redraw=")) |rdw_offset| {
            const abs_rdw = after + rdw_offset;
            const value_start = abs_rdw + "redraw=".len;
            var value_end = value_start;
            while (value_end < end and result.items[value_end] != ';') : (value_end += 1) {}
            result.replaceRange(alloc, value_start, value_end - value_start, "0") catch return null;
            continue;
        }

        // No redraw= present. Insert ;redraw=0 before the terminator.
        result.replaceRange(alloc, end, 0, ";redraw=0") catch return null;
    }

    // If nothing changed, free and return null.
    if (std.mem.eql(u8, result.items, data)) {
        result.deinit(alloc);
        return null;
    }

    return result.toOwnedSlice(alloc) catch null;
}

fn writePwd(writer: *std.Io.Writer, term: *const ghostty_vt.Terminal) void {
    const pwd = term.getPwd() orelse return;
    if (pwd.len == 0) return;
    writer.print("\x1b]7;{s}\x1b\\", .{pwd}) catch |err| {
        std.log.warn("failed to format pwd err={s}", .{@errorName(err)});
    };
}

fn writeDynamicColor(
    writer: *std.Io.Writer,
    kind: ghostty_vt.color.Dynamic,
    dynamic: ghostty_vt.color.DynamicRGB,
) void {
    const c = dynamic.override orelse return;
    writer.print("\x1b]{d};rgb:{x:0>2}/{x:0>2}/{x:0>2}\x1b\\", .{
        @intFromEnum(kind), c.r, c.g, c.b,
    }) catch |err| {
        std.log.warn("failed to format dynamic color err={s}", .{@errorName(err)});
    };
}

fn writeColorOverrides(writer: *std.Io.Writer, term: *const ghostty_vt.Terminal) void {
    const colors = &term.colors;
    writeDynamicColor(writer, .foreground, colors.foreground);
    writeDynamicColor(writer, .background, colors.background);
    writeDynamicColor(writer, .cursor, colors.cursor);
}

/// The VT formatter does not emit the cursor shape (DECSCUSR).
fn writeCursorStyle(writer: *std.Io.Writer, term: *ghostty_vt.Terminal) !void {
    if (term.cursor.is_default) return writer.writeAll("\x1b[0 q");
    const blink = term.modes.get(.cursor_blinking);
    const style: u8 = switch (term.screens.active.cursor.cursor_style) {
        .block, .block_hollow => if (blink) 1 else 2,
        .underline => if (blink) 3 else 4,
        .bar => if (blink) 5 else 6,
    };
    try writer.print("\x1b[{d} q", .{style});
}

pub fn serializeTerminalState(alloc: std.mem.Allocator, term: *ghostty_vt.Terminal) ?[]const u8 {
    var builder: std.Io.Writer.Allocating = .init(alloc);
    defer builder.deinit();

    // Synchronized output (DECSET 2026) is a transient rendering handshake
    // between a program and its current terminal client. Replaying it to a
    // newly attached client can leave that client deferring renders until its
    // local timeout fires, so temporarily exclude it from restored state and
    // restore the original mode before returning.
    const had_synchronized_output = term.modes.get(.synchronized_output);
    if (had_synchronized_output) {
        term.modes.set(.synchronized_output, false);
    }
    defer if (had_synchronized_output) term.modes.set(.synchronized_output, true);

    // If state contains color override, restore it.
    writeColorOverrides(&builder.writer, term);

    const pages = &term.screens.active.pages;
    const screen_top = pages.getTopLeft(.screen);
    const active_top = pages.getTopLeft(.active);
    const has_scrollback = !screen_top.eql(active_top);

    // Two-phase serialization to preserve scrollback without corrupting
    // cursor positions. This matters for nested zmx sessions (zmx→SSH→zmx)
    // where the outer daemon's ghostty-vt accumulates inner session scrollback.
    //
    // Phase 1: Emit scrollback content (plain text with styles, no terminal extras).
    // These lines scroll past the visible area into the terminal's scrollback buffer.
    // Phase 2: Clear visible screen, then emit visible content with full extras.
    // The clear ensures visible content starts from a clean slate regardless of
    // how much scrollback preceded it. CUP cursor positioning is then correct.
    //
    // See: https://github.com/neurosnap/zmx/issues/31

    // Phase 1: scrollback only (if any exists)
    if (has_scrollback) {
        if (active_top.up(1)) |sb_bottom_row| {
            var sb_bottom = sb_bottom_row;
            sb_bottom.x = @intCast(pages.cols - 1);

            var scroll_fmt = ghostty_vt.formatter.TerminalFormatter.init(term, .vt);
            // A soft-wrapped line goes as one, so the client wraps it again and keeps it one line (zmx ends each row).
            scroll_fmt.opts.unwrap = true;
            scroll_fmt.content = .{
                .selection = ghostty_vt.Selection.init(
                    screen_top,
                    sb_bottom,
                    false,
                ),
            };
            scroll_fmt.extra = .none; // no modes, cursor, keyboard — just content
            scroll_fmt.format(&builder.writer) catch |err| {
                std.log.warn("failed to format scrollback err={s}", .{@errorName(err)});
            };
        }

        // The formatter leaves its final history rows in the viewport. Move
        // every one into scrollback before clearing that viewport, including
        // when the entire history is shorter than the terminal height.
        for (0..pages.rows) |_| builder.writer.writeAll("\r\n") catch |err| {
            std.log.warn("failed to preserve scrollback err={s}", .{@errorName(err)});
            return null;
        };

        // Clear visible screen after scrollback. \x1b[2J clears only the visible
        // rows (not the scrollback buffer). \x1b[H homes the cursor. \x1b[0m resets
        // SGR style so phase 1 styles don't bleed into phase 2.
        builder.writer.writeAll("\x1b[2J\x1b[H\x1b[0m") catch {};
    }

    // Phase 2: visible screen with full extras (modes, cursor, keyboard, etc.)
    var vis_fmt = ghostty_vt.formatter.TerminalFormatter.init(term, .vt);
    vis_fmt.opts.unwrap = true;

    // Restrict content to the active viewport only
    const active_tl = pages.pin(.{ .active = .{ .x = 0, .y = 0 } });
    const active_br = pages.pin(.{
        .active = .{
            .x = @intCast(pages.cols - 1),
            .y = @intCast(pages.rows - 1),
        },
    });

    if (active_tl != null and active_br != null) {
        vis_fmt.content = .{
            .selection = ghostty_vt.Selection.init(
                active_tl.?,
                active_br.?,
                false,
            ),
        };
    }
    // Fallback: if pins are somehow invalid, use null selection (all content)

    vis_fmt.extra = .{
        .palette = false,
        .modes = true,
        .scrolling_region = true,
        .tabstops = false, // tabstop restoration moves cursor after CUP, corrupting position
        .pwd = false, // emitted below without the sentinel the formatter includes
        .keyboard = true,
        .screen = .all,
    };

    vis_fmt.format(&builder.writer) catch |err| {
        std.log.warn("failed to format terminal state err={s}", .{@errorName(err)});
        return null;
    };

    writePwd(&builder.writer, term);

    // The formatter has no title extra and never emits OSC 0/1/2, so the title
    // has to be replayed separately or an attaching client shows whatever its
    // terminal defaults to, usually the client process name. OSC 2 does not
    // move the cursor, so this is safe to append after the content.
    if (term.getTitle()) |title| {
        builder.writer.print("\x1b]2;{s}\x07", .{title}) catch |err| {
            std.log.warn("failed to format title err={s}", .{@errorName(err)});
        };
    }

    writeCursorStyle(&builder.writer, term) catch |err| {
        std.log.warn("failed to restore cursor style err={s}", .{@errorName(err)});
    };

    const output = builder.writer.buffered();
    if (output.len == 0) return null;

    return alloc.dupe(u8, output) catch |err| {
        std.log.warn("failed to allocate terminal state err={s}", .{@errorName(err)});
        return null;
    };
}

// agterm scripts/zmx-patches/0001-explicit-leadership.patch, src/util.zig

/// The bytes a terminal sends for `typed`, as keystrokes rather than as a paste:
/// each carriage return is a Return key press and release, and every run between
/// them is text a key produced. Encoded against `term`'s live keyboard mode, so
/// a program that asked for the kitty protocol gets `CSI 13 u` where a shell gets
/// a bare CR. Caller frees.
pub fn encodeTyped(alloc: std.mem.Allocator, term: *const ghostty_vt.Terminal, typed: []const u8) ![]u8 {
    var builder: std.Io.Writer.Allocating = .init(alloc);
    defer builder.deinit();
    const opts = ghostty_vt.input.KeyEncodeOptions.fromTerminal(term);

    var rest = typed;
    while (rest.len > 0) {
        const run = std.mem.indexOfScalar(u8, rest, '\r') orelse rest.len;
        if (run > 0) try ghostty_vt.input.encodeKey(&builder.writer, .{ .utf8 = rest[0..run] }, opts);
        if (run == rest.len) break;
        try ghostty_vt.input.encodeKey(&builder.writer, .{ .key = .enter }, opts);
        try ghostty_vt.input.encodeKey(&builder.writer, .{ .key = .enter, .action = .release }, opts);
        rest = rest[run + 1 ..];
    }
    return alloc.dupe(u8, builder.writer.buffered());
}

/// Plain text of the active screen, or of scrollback plus the active screen,
/// read the way a terminal copies a selection: soft-wrapped rows joined and
/// trailing blanks trimmed. Empty for a blank screen. Caller frees.
pub fn serializeScreenText(
    alloc: std.mem.Allocator,
    term: *ghostty_vt.Terminal,
    all: bool,
) ![]u8 {
    var builder: std.Io.Writer.Allocating = .init(alloc);
    defer builder.deinit();

    const pages = &term.screens.active.pages;
    var opts: ghostty_vt.formatter.Options = .plain;
    opts.unwrap = true;
    var term_formatter = ghostty_vt.formatter.TerminalFormatter.init(term, opts);
    term_formatter.extra = .none;
    term_formatter.content = .{ .selection = if (all) null else sel: {
        const end = pages.getBottomRight(.active) orelse break :sel null;
        break :sel ghostty_vt.Selection.init(pages.getTopLeft(.active), end, false);
    } };

    try term_formatter.format(&builder.writer);
    return alloc.dupe(u8, builder.writer.buffered());
}

pub const HistoryFormat = enum(u8) {
    plain = 0,
    vt = 1,
    html = 2,
};

test "rewritePromptRedraw: no OSC 133;A returns null" {
    const result = rewritePromptRedraw(std.testing.allocator, "hello world");
    try std.testing.expect(result == null);
}

test "rewritePromptRedraw: injects redraw=0 with BEL terminator" {
    const input = "\x1b]133;A\x07";
    const result = rewritePromptRedraw(std.testing.allocator, input).?;
    defer std.testing.allocator.free(result);
    try std.testing.expectEqualStrings("\x1b]133;A;redraw=0\x07", result);
}

test "rewritePromptRedraw: injects redraw=0 with ST terminator" {
    const input = "\x1b]133;A\x1b\\";
    const result = rewritePromptRedraw(std.testing.allocator, input).?;
    defer std.testing.allocator.free(result);
    try std.testing.expectEqualStrings("\x1b]133;A;redraw=0\x1b\\", result);
}

test "rewritePromptRedraw: replaces existing redraw=1" {
    const input = "\x1b]133;A;redraw=1\x07";
    const result = rewritePromptRedraw(std.testing.allocator, input).?;
    defer std.testing.allocator.free(result);
    try std.testing.expectEqualStrings("\x1b]133;A;redraw=0\x07", result);
}

test "rewritePromptRedraw: replaces existing redraw=last" {
    const input = "\x1b]133;A;redraw=last\x07";
    const result = rewritePromptRedraw(std.testing.allocator, input).?;
    defer std.testing.allocator.free(result);
    try std.testing.expectEqualStrings("\x1b]133;A;redraw=0\x07", result);
}

test "rewritePromptRedraw: preserves redraw=0 (no-op)" {
    const result = rewritePromptRedraw(std.testing.allocator, "\x1b]133;A;redraw=0\x07");
    try std.testing.expect(result == null);
}

test "rewritePromptRedraw: preserves other parameters" {
    const input = "\x1b]133;A;aid=14;cl=line\x07";
    const result = rewritePromptRedraw(std.testing.allocator, input).?;
    defer std.testing.allocator.free(result);
    try std.testing.expectEqualStrings("\x1b]133;A;aid=14;cl=line;redraw=0\x07", result);
}

test "rewritePromptRedraw: handles multiple markers" {
    const input = "before\x1b]133;A\x07middle\x1b]133;A;redraw=1\x07after";
    const result = rewritePromptRedraw(std.testing.allocator, input).?;
    defer std.testing.allocator.free(result);
    try std.testing.expectEqualStrings("before\x1b]133;A;redraw=0\x07middle\x1b]133;A;redraw=0\x07after", result);
}

test "rewritePromptRedraw: does not touch OSC 133;B or 133;C" {
    const input = "\x1b]133;B\x07\x1b]133;C\x07";
    const result = rewritePromptRedraw(std.testing.allocator, input);
    try std.testing.expect(result == null);
}

test "rewritePromptRedraw: embedded in larger output" {
    const input = "some output\r\n\x1b]133;A\x07prompt$ \x1b]133;B\x07";
    const result = rewritePromptRedraw(std.testing.allocator, input).?;
    defer std.testing.allocator.free(result);
    try std.testing.expectEqualStrings("some output\r\n\x1b]133;A;redraw=0\x07prompt$ \x1b]133;B\x07", result);
}

test "serializeTerminalState excludes synchronized output replay" {
    const alloc = testing.allocator;
    const io = testing.io;

    var term = try ghostty_vt.Terminal.init(io, alloc, .{
        .cols = 80,
        .rows = 24,
    });
    defer term.deinit(alloc);

    var stream = term.vtStream();
    defer stream.deinit();

    stream.nextSlice("\x1b[?2004h"); // Bracketed paste
    stream.nextSlice("\x1b[?2026h"); // Synchronized output
    stream.nextSlice("hello");

    try testing.expect(term.modes.get(.bracketed_paste));
    try testing.expect(term.modes.get(.synchronized_output));

    const output = serializeTerminalState(alloc, &term) orelse return error.TestUnexpectedNull;
    defer alloc.free(output);

    // The serialized output should contain bracketed paste (DECSET 2004)
    // but NOT synchronized output (DECSET 2026)
    try testing.expect(std.mem.indexOf(u8, output, "\x1b[?2004h") != null);
    try testing.expect(std.mem.indexOf(u8, output, "\x1b[?2026h") == null);
}

test "serializeTerminalState replays the title" {
    const alloc = testing.allocator;
    const io = testing.io;

    var term = try ghostty_vt.Terminal.init(io, alloc, .{
        .cols = 80,
        .rows = 24,
    });
    defer term.deinit(alloc);

    var stream = term.vtStream();
    defer stream.deinit();

    stream.nextSlice("\x1b]2;my title\x07");
    stream.nextSlice("hello");

    const output = serializeTerminalState(alloc, &term) orelse return error.TestUnexpectedNull;
    defer alloc.free(output);

    try testing.expect(std.mem.indexOf(u8, output, "\x1b]2;my title\x07") != null);
}

test "serializeTerminalState omits the title when none is set" {
    const alloc = testing.allocator;
    const io = testing.io;

    var term = try ghostty_vt.Terminal.init(io, alloc, .{
        .cols = 80,
        .rows = 24,
    });
    defer term.deinit(alloc);

    var stream = term.vtStream();
    defer stream.deinit();

    stream.nextSlice("hello");

    const output = serializeTerminalState(alloc, &term) orelse return error.TestUnexpectedNull;
    defer alloc.free(output);

    try testing.expect(std.mem.indexOf(u8, output, "\x1b]2;") == null);
}

test "serializeTerminalState replays the pwd without a NUL sentinel" {
    const alloc = testing.allocator;
    const io = testing.io;

    var term = try ghostty_vt.Terminal.init(io, alloc, .{
        .cols = 80,
        .rows = 24,
    });
    defer term.deinit(alloc);

    var stream = term.vtStream();
    defer stream.deinit();

    stream.nextSlice("\x1b]7;file://myhost/private/tmp\x1b\\");
    stream.nextSlice("hello");

    const output = serializeTerminalState(alloc, &term) orelse return error.TestUnexpectedNull;
    defer alloc.free(output);

    try testing.expect(std.mem.indexOf(u8, output, "\x1b]7;file://myhost/private/tmp\x1b\\") != null);
    try testing.expectEqual(@as(?usize, null), std.mem.indexOfScalar(u8, output, 0));
}

test "serializeTerminalState omits the pwd when none is set" {
    const alloc = testing.allocator;
    const io = testing.io;

    var term = try ghostty_vt.Terminal.init(io, alloc, .{
        .cols = 80,
        .rows = 24,
    });
    defer term.deinit(alloc);

    var stream = term.vtStream();
    defer stream.deinit();

    stream.nextSlice("hello");

    const output = serializeTerminalState(alloc, &term) orelse return error.TestUnexpectedNull;
    defer alloc.free(output);

    try testing.expect(std.mem.indexOf(u8, output, "\x1b]7;") == null);
}

// agterm's zmx patch 0001 test "screen answers with a header even for a blank screen", without the daemon's message
// framing and the unknown mode, which agshell-live's JSON request cannot carry
test "screen answers with a header even for a blank screen" {
    const alloc = testing.allocator;
    const vt = agshell_vt_new(20, 5, 0) orelse return error.TestUnexpectedNull;
    defer agshell_vt_free(vt);

    const blank = try serializeScreenText(alloc, &vt.term, false);
    defer alloc.free(blank);
    try testing.expectEqualStrings("", blank);

    // a line longer than the grid wraps on screen and reads back as one line
    const line = "0123456789abcdefghijKLMNO\r\n> ab";
    agshell_vt_write(vt, line.ptr, line.len);
    var ptr: ?[*]u8 = null;
    var len: usize = 0;
    var header: ScreenHeader = undefined;
    try testing.expect(agshell_vt_screen(vt, false, &ptr, &len, &header));
    defer agshell_vt_bytes_free(ptr, len);
    try testing.expectEqual(@as(u16, 20), header.cols);
    try testing.expectEqual(@as(u16, 4), header.cursor_x);
    try testing.expectEqual(@as(u16, 2), header.cursor_y);
    try testing.expectEqual(@as(u8, 0), header.alternate);
    try testing.expectEqualStrings("0123456789abcdefghijKLMNO\n> ab", ptr.?[0..len]);
}

test "typed input is encoded for the keyboard mode the program asked for" {
    const alloc = testing.allocator;
    const vt = agshell_vt_new(80, 24, 0) orelse return error.TestUnexpectedNull;
    defer agshell_vt_free(vt);

    const legacy = try encodeTyped(alloc, &vt.term, "ls -la\r\rcaf\xc3\xa9");
    defer alloc.free(legacy);
    try testing.expectEqualStrings("ls -la\r\rcaf\xc3\xa9", legacy);

    // every kitty flag: Return is reported as a key, with its release
    const kitty_on = "\x1b[>31u";
    agshell_vt_write(vt, kitty_on.ptr, kitty_on.len);
    const kitty = try encodeTyped(alloc, &vt.term, "ls\r");
    defer alloc.free(kitty);
    try testing.expect(std.mem.startsWith(u8, kitty, "ls"));
    try testing.expect(std.mem.indexOf(u8, kitty, "\x1b[13u") != null);
    try testing.expect(std.mem.indexOf(u8, kitty, "\x1b[13;1:3u") != null);
    try testing.expect(std.mem.indexOfScalar(u8, kitty, '\r') == null);
}

fn testCreateTerminal(alloc: std.mem.Allocator, io: std.Io, cols: u16, rows: u16, vt_data: []const u8) !ghostty_vt.Terminal {
    var term = try ghostty_vt.Terminal.init(io, alloc, .{
        .cols = cols,
        .rows = rows,
        .max_scrollback_lines = 2_000,
    });
    if (vt_data.len > 0) {
        var stream = term.vtStream();
        defer stream.deinit();
        stream.nextSlice(vt_data);
    }
    return term;
}

fn expectCursorAt(term: *ghostty_vt.Terminal, row: usize, col: usize) !void {
    const cursor = &term.screens.active.cursor;
    try testing.expectEqual(col, cursor.x);
    try testing.expectEqual(row, cursor.y);
}

fn serializeRoundtrip(alloc: std.mem.Allocator, io: std.Io, source: *ghostty_vt.Terminal) !ghostty_vt.Terminal {
    const serialized = serializeTerminalState(alloc, source) orelse
        return error.SerializationFailed;
    defer alloc.free(serialized);

    var dest = try ghostty_vt.Terminal.init(io, alloc, .{
        .cols = source.screens.active.pages.cols,
        .rows = source.screens.active.pages.rows,
        .max_scrollback_lines = 2_000,
    });
    var stream = dest.vtStream();
    defer stream.deinit();
    stream.nextSlice(serialized);
    return dest;
}

test "serializeTerminalState allocation failure preserves synchronized output mode" {
    const alloc = testing.allocator;
    var term = try testCreateTerminal(alloc, testing.io, 40, 3, "one\r\ntwo\r\nthree\r\nfour\r\n");
    defer term.deinit(alloc);
    term.modes.set(.synchronized_output, true);
    // the failure is logged as a warning, which the build's test runner counts as a failed run
    const log_level = testing.log_level;
    testing.log_level = .err;
    defer testing.log_level = log_level;
    var failing = testing.FailingAllocator.init(alloc, .{ .fail_index = 0 });
    try testing.expect(serializeTerminalState(failing.allocator(), &term) == null);
    try testing.expect(term.modes.get(.synchronized_output));
}

test "serializeTerminalState replays the cursor style" {
    const alloc = testing.allocator;
    const io = testing.io;
    for ([_][]const u8{ "\x1b[1 q", "\x1b[2 q", "\x1b[3 q", "\x1b[4 q", "\x1b[5 q", "\x1b[6 q", "\x1b[0 q" }) |shape| {
        var term = try testCreateTerminal(alloc, io, 20, 3, "\x1b[6 q");
        defer term.deinit(alloc);
        {
            var stream = term.vtStream();
            defer stream.deinit();
            stream.nextSlice(shape);
        }
        var restored = try testCreateTerminal(alloc, io, 20, 3, "\x1b[4 q");
        defer restored.deinit(alloc);
        const serialized = serializeTerminalState(alloc, &term) orelse return error.SerializationFailed;
        defer alloc.free(serialized);
        {
            var stream = restored.vtStream();
            defer stream.deinit();
            stream.nextSlice(serialized);
        }
        try testing.expectEqual(term.cursor.is_default, restored.cursor.is_default);
        try testing.expectEqual(term.screens.active.cursor.cursor_style, restored.screens.active.cursor.cursor_style);
        try testing.expectEqual(term.modes.get(.cursor_blinking), restored.modes.get(.cursor_blinking));
    }
}

fn dumpUnwrapped(alloc: std.mem.Allocator, term: *ghostty_vt.Terminal) ![]u8 {
    var builder: std.Io.Writer.Allocating = .init(alloc);
    defer builder.deinit();
    var formatter = ghostty_vt.formatter.TerminalFormatter.init(term, .{ .emit = .plain, .unwrap = true });
    try formatter.format(&builder.writer);
    return alloc.dupe(u8, builder.writer.buffered());
}

test "serializeTerminalState keeps soft-wrapped lines one line" {
    const alloc = testing.allocator;
    const io = testing.io;
    const inputs = [_][]const u8{
        // wrapped lines in the scrollback and on the screen, a row that ends with blanks inside a wrapped line
        "AAAAAAAAAABBBBBBBBBBCCCCC\r\nfoo bar   baz qux end\r\n1\r\n2\r\n3\r\n4\r\nEEEEEEEEEEFFF\r\n$ ",
        // a full row that is not wrapped, and wide characters across a wrap
        "0123456789\r\nab\u{4E2D}\u{6587}\u{4E2D}\u{6587}\u{4E2D}\u{6587}\u{4E2D}\u{6587}\r\nx\r\ny\r\nz\r\n$ ",
        // the cursor left in a wrapped line on the screen
        "1\r\n2\r\n3\r\n4\r\n5\r\nGGGGGGGGGGGGGGG",
        // a wrapped line that starts in the scrollback and goes on on the screen
        "1\r\n2\r\nHHHHHHHHHHIIIIIIIIIIJJJJJJJJJJKKKKK",
    };
    for (inputs) |input| {
        var term = try testCreateTerminal(alloc, io, 10, 4, input);
        defer term.deinit(alloc);
        var restored = try serializeRoundtrip(alloc, io, &term);
        defer restored.deinit(alloc);
        const expected = try dumpUnwrapped(alloc, &term);
        defer alloc.free(expected);
        const actual = try dumpUnwrapped(alloc, &restored);
        defer alloc.free(actual);
        try testing.expectEqualStrings(expected, actual);
        try expectCursorAt(&restored, term.screens.active.cursor.y, term.screens.active.cursor.x);
    }
}

test "serializeTerminalState preserves all scrollback rows before clearing viewport" {
    const alloc = testing.allocator;
    const io = testing.io;
    for ([_]u16{ 1, 3, 24 }) |rows| {
        for ([_]usize{ 1, 2, 23, 24, 25, 80 }) |lines| {
            var term = try testCreateTerminal(alloc, io, 40, rows, "");
            defer term.deinit(alloc);
            {
                var stream = term.vtStream();
                defer stream.deinit();
                var buf: [32]u8 = undefined;
                for (0..lines) |index| {
                    stream.nextSlice(try std.fmt.bufPrint(&buf, "ROW_{d}\r\n", .{index}));
                }
                stream.nextSlice("\x1b[2J\x1b[Hvisible");
            }
            var restored = try serializeRoundtrip(alloc, io, &term);
            defer restored.deinit(alloc);
            const expected = try term.screens.active.dumpStringAlloc(alloc, .{ .screen = .{ .x = 0, .y = 0 } });
            defer alloc.free(expected);
            const actual = try restored.screens.active.dumpStringAlloc(alloc, .{ .screen = .{ .x = 0, .y = 0 } });
            defer alloc.free(actual);
            try testing.expectEqualStrings(expected, actual);
            try expectCursorAt(&restored, term.screens.active.cursor.y, term.screens.active.cursor.x);
        }
    }
}

// zmx main 2626f17 src/terminal.zig
test "isUserInput: printable characters" {
    // Regular text should be detected as user input
    try testing.expect(isUserInput("hello"));
    try testing.expect(isUserInput("Hello World!"));
    try testing.expect(isUserInput("12345"));
    try testing.expect(isUserInput("!@#$%^&*()"));
}
test "isUserInput: whitespace characters" {
    // Space character is printable
    try testing.expect(isUserInput(" "));
    try testing.expect(isUserInput("   "));
}
test "isUserInput: line feed (LF)" {
    // LF triggers .execute action
    try testing.expect(isUserInput("\n"));
    try testing.expect(isUserInput("test\n"));
}
test "isUserInput: carriage return (CR)" {
    // CR triggers .execute action
    try testing.expect(isUserInput("\r"));
    try testing.expect(isUserInput("test\r"));
}
test "isUserInput: tab" {
    // Tab triggers .execute action
    try testing.expect(isUserInput("\t"));
    try testing.expect(isUserInput("col1\tcol2"));
}
test "isUserInput: backspace" {
    // Backspace triggers .execute action
    try testing.expect(isUserInput("\x08"));
    try testing.expect(isUserInput("test\x08"));
}
test "isUserInput: arrow keys (CSI ~)" {
    // Arrow keys use CSI with ~ - these have params
    try testing.expect(isUserInput("\x1b[3~")); // delete
    try testing.expect(isUserInput("\x1b[5~")); // page up
    try testing.expect(isUserInput("\x1b[6~")); // page down
}
test "isUserInput: modified arrow keys with CSI u" {
    // Modified arrow keys with CSI ... u
    try testing.expect(isUserInput("\x1bOA")); // up with modifier
    try testing.expect(isUserInput("\x1bOB")); // down with modifier
    try testing.expect(isUserInput("\x1bOC")); // right with modifier
    try testing.expect(isUserInput("\x1bOD")); // left with modifier
}
test "isUserInput: up arrow legacy" {
    // Legacy up arrow: CSI A (with params for kitty-style)
    try testing.expect(isUserInput("\x1b[1;1A")); // kitty-style legacy
}
test "isUserInput: up arrow kitty" {
    // Kitty keyboard up arrow: CSI 1;1;1A (no colon format supported by parser)
    try testing.expect(isUserInput("\x1b[1;1;1A")); // kitty up arrow
}
test "isUserInput: arrow keys with modifier params CSI A-D" {
    // Modified arrow keys like Ctrl+Up: CSI 1;5A
    try testing.expect(isUserInput("\x1b[1;5A")); // Ctrl+Up
    try testing.expect(isUserInput("\x1b[1;5B")); // Ctrl+Down
    try testing.expect(isUserInput("\x1b[1;5C")); // Ctrl+Right
    try testing.expect(isUserInput("\x1b[1;5D")); // Ctrl+Left
    try testing.expect(isUserInput("\x1b[1;3A")); // Alt+Up
    try testing.expect(isUserInput("\x1b[1;3B")); // Alt+Down
}
test "isUserInput: function keys with modifiers CSI 27 ; ~" {
    // Legacy modified keys: CSI 27 ; ... ~
    try testing.expect(isUserInput("\x1b[15;2~")); // F4 with modifier
    try testing.expect(isUserInput("\x1b[17;2~")); // F5 with modifier
    try testing.expect(isUserInput("\x1b[18;2~")); // F6 with modifier
}
test "isUserInput: enter key" {
    // Enter is LF (0x0A)
    try testing.expect(isUserInput("\x0A"));
}
test "isUserInput: mixed content" {
    // Mix of printable and control sequences
    try testing.expect(isUserInput("hello\nworld"));
    try testing.expect(isUserInput("\x1b[3~\x1b[6~")); // multiple CSI ~ sequences
    try testing.expect(isUserInput("abc\x1b[3~def")); // text with CSI ~
}
test "isUserInput: non-user input (escape sequences only)" {
    // Cursor movement without user input
    try testing.expect(!isUserInput("\x1b[2;1H")); // CSI H cursor home
    // SGR color set (no printing)
    try testing.expect(!isUserInput("\x1b[0m"));
    // Cursor position report query
    try testing.expect(!isUserInput("\x1b[6n"));
}
test "isUserInput: empty string" {
    try testing.expect(!isUserInput(""));
}
test "isUserInput: only whitespace controls" {
    // Multiple control chars should return true
    try testing.expect(isUserInput("\n\r\t"));
}
test "isUserInput: kitty keyboard sequences" {
    // Kitty keyboard protocol uses CSI u
    try testing.expect(isUserInput("\x1b[11;2u")); // F1 with modifier
    try testing.expect(isUserInput("\x1b[12;2u")); // F2 with modifier
    try testing.expect(isUserInput("\x1b[102;1:1u")); // literal "f" press
    try testing.expect(isUserInput("\x1b[57444;1:1u")); // Kitty functional key press
    try testing.expect(!isUserInput("\x1b[102;1:3u")); // literal "f" release only
    try testing.expect(isUserInput("\x1b[102;1:1u\x1b[67;65;31M")); // key press with mouse noise
}
test "isUserInput: mouse events (CSI M) excluded" {
    // Basic mouse tracking (SGR disabled): CSI M Cb Cx Cy
    // Mouse events should NOT trigger leader switch
    try testing.expect(!isUserInput("\x1b[M@ 0 0")); // button 0, pos 0,0
    try testing.expect(!isUserInput("\x1b[M@ 1 1")); // button 1, pos 1,1
}
test "isUserInput: mouse events SGR mode CSI < excluded" {
    // SGR extended mouse tracking: CSI < Cb;Cx;Y M
    // Mouse events should NOT trigger leader switch
    try testing.expect(!isUserInput("\x1b[<0;1;1M")); // button release
    try testing.expect(!isUserInput("\x1b[<64;1;1M")); // button press
}
test "isUserInput: focus events excluded" {
    // Focus in/out are automatic terminal events, not user typing
    try testing.expect(!isUserInput("\x1b[I")); // focus in
    try testing.expect(!isUserInput("\x1b[O")); // focus out
}
test "isUserInput: terminal query replies excluded" {
    // Replies every attached terminal sends to queries the app broadcasts.
    try testing.expect(!isUserInput("\x1b[?5u")); // kitty keyboard flags
    try testing.expect(!isUserInput("\x1b[?0u"));
    try testing.expect(!isUserInput("\x1b[?62;22;52c")); // DA1
    try testing.expect(!isUserInput("\x1b[>1;10;0c")); // DA2
    try testing.expect(!isUserInput("\x1b[?2026;2$y")); // DECRPM
    try testing.expect(!isUserInput("\x1b[?997;1n")); // color scheme
    try testing.expect(!isUserInput("\x1b[12;40R")); // cursor position
    try testing.expect(!isUserInput("\x1b[6;32;14t")); // cell size
    // XTVERSION + kitty flags + DA1 in one read, as a passive Ghostty
    // client sent it in the wild (it made that viewer the leader).
    try testing.expect(!isUserInput("\x1bP>|ghostty 1.3.2-main+64f5cc175\x1b\\\x1b[?5u\x1b[?62;22;52c"));
}
test "isUserInput: key after a reply in the same read still counts" {
    try testing.expect(isUserInput("\x1b[?5ux"));
    try testing.expect(isUserInput("\x1b[?62;22c\x1b[99;5u")); // reply then kitty ctrl+c
    try testing.expect(isUserInput("\x1b[?5u\x1b[3~")); // reply then delete
}
test "isUserInput: bracketed paste included" {
    // Bracketed paste start/end are user-initiated paste operations
    try testing.expect(isUserInput("\x1b[200~")); // paste start
    try testing.expect(isUserInput("\x1b[201~")); // paste end
    // Content between start/end is also user input
    try testing.expect(isUserInput("\x1b[200~hello\x1b[201~"));
}
test "isUserInput: kitty keyboard status reply is a terminal response" {
    // CSI ? flags u answers the program's CSI ? u query; no key was pressed
    try testing.expect(!isUserInput("\x1b[?1u"));
    try testing.expect(!isUserInput("\x1b[?0u"));
    try testing.expect(!isUserInput("\x1b[?31u"));
    // a real key coalesced with the reply still counts
    try testing.expect(isUserInput("\x1b[?1ua"));
    try testing.expect(isUserInput("\x1b[?1u\r"));
    try testing.expect(isUserInput("\x1b[?1u\x1b[97;1u"));
    try testing.expect(!isUserInput("\x1b[?1u\x1b[97;1:3u"));
}
