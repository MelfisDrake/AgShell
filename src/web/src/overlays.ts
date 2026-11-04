import { command, OverlayView, SessionView } from "./bridge";

/** An overlay that is on screen: agterm's hidden overlay keeps running and holds its slot, but covers nothing. */
export function shownOverlay(overlay: OverlayView | null | undefined): OverlayView | null {
  return overlay && !overlay.hidden ? overlay : null;
}

const reported = new Map<string, string>();

/** Tells the host a floating overlay's content size as laid out, for tree's overlayWidth and overlayHeight; once per change. */
export function reportOverlayGeometry(overlayId: string, width: number, height: number): void {
  const size = `${Math.round(width)}x${Math.round(height)}`;
  if (width > 0 && height > 0 && reported.get(overlayId) !== size) {
    reported.set(overlayId, size);
    command({ command: "overlayGeometry", overlayId, width: Math.round(width), height: Math.round(height) });
  }
}

/** Whether the session has a program or page that can be hidden or shown: a HUD cannot. */
export function hasHideableOverlay(session: SessionView): boolean {
  return (session.overlay !== null && session.overlay.kind !== "hud") || session.paneOverlays.left !== null || session.paneOverlays.right !== null;
}

/** Whether any of those is hidden now: agterm's hiddenOverlayNames, which the title bar button shows struck through. */
export function anyOverlayHidden(session: SessionView): boolean {
  return [session.overlay, session.paneOverlays.left, session.paneOverlays.right].some((overlay) => overlay && overlay.kind !== "hud" && overlay.hidden);
}

/** Whether any of those shows now: what toggling hides. */
export function anyOverlayShown(session: SessionView): boolean {
  return [session.overlay, session.paneOverlays.left, session.paneOverlays.right].some((overlay) => overlay && overlay.kind !== "hud" && !overlay.hidden);
}
