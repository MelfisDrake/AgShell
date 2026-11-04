/**
 * Where native pages show, the interface leaves a clear hole down to the window: a page covered by a palette or a menu
 * sits under this browser and shows through it, so the overlay draws over the live page as agterm's. The hole is kept
 * while the page is in front too, so swapping the two never shows a frame of what is under the page.
 */
const rooms = new Map<string, DOMRect>();
let pending = 0;

/** Layers that paint under the pages; each is cut where it crosses one. */
const UNDER_PAGES = ".terminal-host, .backdrop-wash, .lead-cover, .remote-note";

export function setHole(id: string, rect: DOMRect | null): void {
  const before = rooms.get(id);
  if (rect === null ? before === undefined : before !== undefined && sameRect(before, rect)) {
    return;
  }
  if (rect === null) {
    rooms.delete(id);
  } else {
    rooms.set(id, rect);
  }
  cutHoles();
}

/** Cuts the holes again on the next frame, after the layers under the pages moved or came and went. */
export function scheduleCut(): void {
  if (pending === 0) {
    pending = requestAnimationFrame(() => {
      pending = 0;
      cutHoles();
    });
  }
}

export function cutHoles(): void {
  const holes = [...rooms.values()];
  const app = document.querySelector<HTMLElement>(".app");
  const pane = document.querySelector<HTMLElement>(".terminal-pane");
  app?.style.setProperty("--holes", clip(holes, 0, 0) ?? "none");
  if (pane) {
    const origin = pane.getBoundingClientRect();
    pane.style.setProperty("--holes", clip(holes, origin.left, origin.top) ?? "none");
  }
  document.querySelectorAll<HTMLElement>(UNDER_PAGES).forEach((element) => {
    const box = element.getBoundingClientRect();
    const crossing = holes.filter((hole) => hole.left < box.right && hole.right > box.left && hole.top < box.bottom && hole.bottom > box.top);
    const value = clip(crossing, box.left, box.top) ?? "";
    if (element.style.clipPath !== value) {
      element.style.clipPath = value;
    }
  });
}

/** Everything but the holes, in the coordinates of a box at (left, top). */
function clip(holes: DOMRect[], left: number, top: number): string | null {
  if (holes.length === 0) {
    return null;
  }
  const cuts = holes.map((hole) => `M${hole.left - left} ${hole.top - top}h${hole.width}v${hole.height}h${-hole.width}Z`).join("");
  return `path(evenodd, "M-100000 -100000H100000V100000H-100000Z${cuts}")`;
}

function sameRect(a: DOMRect, b: DOMRect): boolean {
  return a.left === b.left && a.top === b.top && a.width === b.width && a.height === b.height;
}
