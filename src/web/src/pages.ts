import { PageInfo, PageView, send } from "./bridge";
import { boundAction, handleAppShortcut } from "./shortcuts";

// Routes messages between page overlays and the host, by overlay id. Every page runs in a native browser the host places
// over the room the interface leaves for it.

export interface PageHandle {
  info(info: PageInfo): void;
}

const handles = new Map<string, PageHandle>();
// Kept apart from the views, so a page whose overlay remounts shows at once what its browser last said.
const infos = new Map<string, PageInfo>();
const bySlot = new Map<string, string>();

let zoom = 1;
const zoomListeners = new Set<() => void>();

export function registerPage(overlayId: string, sessionId: string, slot: string, handle: PageHandle): () => void {
  handles.set(overlayId, handle);
  bySlot.set(`${sessionId}:${slot}`, overlayId);
  return () => {
    if (handles.get(overlayId) === handle) {
      handles.delete(overlayId);
    }
    if (bySlot.get(`${sessionId}:${slot}`) === overlayId) {
      bySlot.delete(`${sessionId}:${slot}`);
    }
  };
}

const infoListeners = new Set<() => void>();

export function deliverPageInfo(overlayId: string, info: PageInfo): void {
  infos.set(overlayId, info);
  handles.get(overlayId)?.info(info);
  infoListeners.forEach((listener) => listener());
}

/** Any page's info changed, for views that show pages without hosting them, as the dashboard. */
export function onPageInfo(listener: () => void): () => void {
  infoListeners.add(listener);
  return () => infoListeners.delete(listener);
}

/** The origin as a user compares it: scheme and host, and the port only when it is not the scheme's own. */
function originOf(address: string): string | null {
  try {
    const url = new URL(address);
    return url.protocol === "http:" || url.protocol === "https:" ? url.origin : null;
  } catch {
    return null;
  }
}

/**
 * agterm's identity: named from what the app loaded, never from the page's title, which the page writes. The file shown
 * now; the site a web page is kept to; for a page with --browse, the site of the document it shows now.
 */
export function identityOf(page: PageView, info: PageInfo | undefined): string {
  if (page.isFile) {
    const shown = info?.page ?? page.source;
    return shown.split(/[\\/]/).pop() ?? shown;
  }
  if (page.browse && info?.page) {
    return originOf(info.page) ?? info.page;
  }
  // navigate --url (ours alone, К101) moves the page to another site: the strip names the one it is kept to now.
  return originOf(info?.site ?? page.source) ?? page.source;
}


export function pageInfo(overlayId: string): PageInfo | undefined {
  return infos.get(overlayId);
}

/** Forgets a page whose browser closed. */
export function forgetPage(overlayId: string): void {
  infos.delete(overlayId);
}

/** A shortcut pressed inside a page: as in agterm, the font size keys zoom the pages, the rest are agshell's own. */
export function handlePageKey(key: { code: string; key: string; ctrlKey: boolean; shiftKey: boolean; altKey: boolean }): void {
  const event = new KeyboardEvent("keydown", key);
  const action = boundAction(event, ["increase_font_size", "decrease_font_size", "reset_font_size"]);
  if (action) {
    changePageZoom(action);
    return;
  }
  handleAppShortcut(event);
}

/** agterm's html overlay zoom, a setting: the font size keys in a page step it, and every page shows at it. */
export function pageZoom(): number {
  return zoom;
}

/** agterm's ladder of page zooms; a factor off it steps to its neighbour. */
const zoomSteps = [0.5, 0.75, 0.85, 1, 1.15, 1.25, 1.5, 1.75, 2, 2.5, 3];

export function changePageZoom(action: string): void {
  const next =
    action === "increase_font_size"
      ? (zoomSteps.find((step) => step > zoom) ?? 3)
      : action === "decrease_font_size"
        ? ([...zoomSteps].reverse().find((step) => step < zoom) ?? 0.5)
        : 1;
  setPageZoom(next);
  send({ type: "pageZoom", factor: next });
}

/** The zoom the settings hold, as every window's state brings it. */
export function setPageZoom(factor: number): void {
  if (factor !== zoom) {
    zoom = factor;
    zoomListeners.forEach((listener) => listener());
  }
}

export function onPageZoom(listener: () => void): () => void {
  zoomListeners.add(listener);
  return () => zoomListeners.delete(listener);
}
