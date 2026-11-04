import { ITheme } from "@xterm/xterm";
import { AppState, MenuColors, ThemeView } from "./bridge";

/** agterm's defaults: #DBD9E6, then systemOrange and systemGreen as Apple gives them for the light and the dark appearance. */
function defaultStatusColors(dark: boolean): { active: string; blocked: string; completed: string } {
  return dark ? { active: "#dbd9e6", blocked: "#ff9f0a", completed: "#30d158" } : { active: "#dbd9e6", blocked: "#ff9500", completed: "#34c759" };
}

function channels(hex: string): number[] {
  const value = parseInt(hex.slice(1), 16);
  return [(value >> 16) & 255, (value >> 8) & 255, value & 255];
}

/** Black or white, whichever reads better on `hex` (WCAG contrast). */
export function textOn(hex: string): string {
  const [r, g, b] = channels(hex).map((c) => {
    const v = c / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  const luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  return (luminance + 0.05) / 0.05 > 1.05 / (luminance + 0.05) ? "#000000" : "#ffffff";
}

/** `amount` of `top` over `base`, both #rrggbb. */
function mix(top: string, base: string, amount: number): string {
  const a = channels(top);
  const b = channels(base);
  return "#" + a.map((c, i) => Math.round(c * amount + b[i] * (1 - amount)).toString(16).padStart(2, "0")).join("");
}

/** The opacity the window's background is painted with: below 1 only where Windows 11 has a backdrop to show. */
export function backgroundOpacity(state: AppState): number {
  return state.settings.backdropSupported && !state.settings.transparencyOff && !state.settings.fullscreen && !state.settingsPage ? state.settings.backgroundOpacity : 1;
}

/** A #rrggbb color with an opacity, unchanged when opaque. */
export function withOpacity(hex: string, opacity: number): string {
  return opacity >= 1 ? hex : `rgba(${channels(hex).join(", ")}, ${opacity})`;
}

/** The xterm.js colors of a Ghostty theme. */
export function terminalTheme(theme: ThemeView, opacity = 1): ITheme {
  const names = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];
  const ansi: Record<string, string> = {};
  theme.palette.forEach((color, index) => {
    if (color) {
      const name = names[index % 8];
      ansi[index < 8 ? name : "bright" + name[0].toUpperCase() + name.slice(1)] = color;
    }
  });
  return {
    background: withOpacity(theme.background, opacity),
    foreground: theme.foreground,
    cursor: theme.cursor ?? theme.foreground,
    cursorAccent: theme.cursorText ?? theme.background,
    selectionBackground: theme.selectionBackground ?? mix(theme.foreground, theme.background, 0.25),
    selectionForeground: theme.selectionForeground ?? undefined,
    ...ansi,
  };
}

let applied = "";

/**
 * The menu bar's colors: the bar is the window's background, as the caption above it, its open title the selection's
 * color. The menus themselves are the system's, dark or light with the theme as macOS menus are with the appearance.
 */
export function menuColors(state: AppState): MenuColors {
  const { theme } = state;
  return {
    bar: backgroundOpacity(state) < 1 ? "#00000000" : theme.background,
    text: theme.foreground,
    highlight: theme.selectionBackground ?? mix(theme.foreground, theme.background, 0.18),
    dark: theme.dark,
  };
}

/**
 * Derives the window's colors from the terminal theme, as agterm does: the sidebar is the terminal background washed
 * darker or lighter by the sidebar shift, the selected row takes the theme's selection colors, light or dark chrome
 * follows the background's brightness.
 */
export function applyChrome(state: AppState): void {
  const { theme, settings } = state;
  const shift = (settings.sidebarBackgroundShift - 5) * 0.06;
  const sidebar = shift >= 0 ? mix("#000000", theme.background, shift) : mix("#ffffff", theme.background, -shift);
  const opacity = backgroundOpacity(state);
  const vars: Record<string, string> = {
    "--terminal-bg": withOpacity(theme.background, opacity),
    "--sidebar-bg": withOpacity(sidebar, opacity),
    "--text": theme.foreground,
    "--muted": mix(theme.foreground, theme.background, 0.55),
    "--border": mix(theme.foreground, theme.background, 0.12),
    "--row-hover": mix(theme.foreground, sidebar, 0.07),
    "--panel-bg": theme.background,
    // With a backdrop the page is see-through and each area paints its own translucent background once.
    "--root-bg": opacity < 1 ? "transparent" : theme.background,
    "--row-selected": theme.selectionBackground ?? mix(theme.foreground, theme.background, 0.18),
    // agterm's sidebar selection pill: the theme's selection, at 0.55 in an inactive window; else the foreground at 0.13 / 0.07.
    "--sidebar-selected": theme.selectionBackground ?? `rgb(${channels(theme.foreground).join(" ")} / 0.13)`,
    "--sidebar-selected-inactive": theme.selectionBackground ? `rgb(${channels(theme.selectionBackground).join(" ")} / 0.55)` : `rgb(${channels(theme.foreground).join(" ")} / 0.07)`,
    "--row-selected-text": theme.selectionForeground ?? theme.foreground,
    // agterm's dashboard caption pill: the theme's selection, else the foreground at 0.22.
    "--dashboard-pill": theme.selectionBackground ?? `rgb(${channels(theme.foreground).join(" ")} / 0.22)`,
    "--menu-bg": mix(theme.foreground, theme.background, 0.07),
    // agterm's popoverHoverColor: the theme's selection at half strength, else a wash of the foreground.
    "--popover-hover": theme.selectionBackground ? `rgb(${channels(theme.selectionBackground).join(" ")} / 0.5)` : `rgb(${channels(theme.foreground).join(" ")} / 0.2)`,
    "--accent": theme.cursor ?? theme.palette[3] ?? theme.foreground,
    "--system-accent": settings.systemAccent,
    "--system-accent-text": textOn(settings.systemAccent),
    // agterm's disabledControlTextColor; Windows has no such system color, so Windows 11's TextFillColorDisabled.
    // agterm pins the outline to darkAqua or aqua by the tinted sidebar, so AppKit draws the disclosure triangle in the
    // system's secondary label gray of that appearance.
    "--chevron": theme.sidebarDark ? "rgb(255 255 255 / 0.55)" : "rgb(0 0 0 / 0.5)",
    "--disabled-text": theme.dark ? "rgb(255 255 255 / 0.365)" : "rgb(0 0 0 / 0.361)",
    "--danger": theme.palette[1] ?? "#e2705f",
    // agterm's badge and its destructive gui ask button are systemRed, which differs between light and dark.
    "--system-red": theme.dark ? "#ff453a" : "#ff3b30",
    "--badge-bg": theme.dark ? "#ff453a" : "#ff3b30",
    "--status-active": settings.activeStatusColorHex ?? defaultStatusColors(theme.dark).active,
    "--status-blocked": settings.blockedStatusColorHex ?? defaultStatusColors(theme.dark).blocked,
    "--status-completed": settings.completedStatusColorHex ?? defaultStatusColors(theme.dark).completed,
    "--pane-mute": String(settings.inactivePaneMuteStrength * 0.08),
    "--sidebar-font-size": settings.sidebarFontSize + "px",
    "--sidebar-row-height": Math.round(settings.sidebarFontSize) + 15 + "px",
    "--interface-font-size": settings.interfaceFontSize + "px",
    "--interface-scale": String(settings.interfaceFontSize / 13),
  };
  const key = JSON.stringify(vars) + theme.dark;
  if (key === applied) {
    return;
  }
  applied = key;
  const root = document.documentElement;
  Object.entries(vars).forEach(([name, value]) => root.style.setProperty(name, value));
  root.style.colorScheme = theme.dark ? "dark" : "light";
  root.classList.toggle("translucent", opacity < 1);
}

export { defaultStatusColors };

/** agterm's statusColor(for:override:): a status call's own color, else the one in Settings, else agterm's default. */
export function statusFill(state: AppState, status: "active" | "blocked" | "completed", override: string | null): string {
  return override ?? state.settings[`${status}StatusColorHex`] ?? defaultStatusColors(state.theme.dark)[status];
}
