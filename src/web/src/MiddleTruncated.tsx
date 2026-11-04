import { useLayoutEffect, useRef, useState } from "react";

/**
 * Text cut in the middle, not at its end, so both ends of a long value stay readable: agterm's RemoteHostTextLayout
 * for the ssh host, and SwiftUI's truncationMode(.middle) for a palette subtitle. Without a limit it fits the width
 * of its parent and follows it as that changes.
 */
export function MiddleTruncated({ text, limit }: { text: string; limit?: number }) {
  const ref = useRef<HTMLSpanElement>(null);
  const [shown, setShown] = useState(text);
  useLayoutEffect(() => {
    const element = ref.current;
    const context = document.createElement("canvas").getContext("2d");
    if (!element || !context) {
      setShown(text);
      return;
    }
    const parent = element.parentElement;
    const fit = () => {
      const width = limit ?? parent?.clientWidth ?? 0;
      context.font = getComputedStyle(element).font;
      if (context.measureText(text).width <= width) {
        setShown(text);
        return;
      }
      let keep = text.length - 1;
      let candidate = text;
      while (keep > 1) {
        const head = Math.ceil(keep / 2);
        candidate = text.slice(0, head) + "…" + text.slice(text.length - (keep - head));
        if (context.measureText(candidate).width <= width) {
          break;
        }
        keep--;
      }
      setShown(candidate);
    };
    fit();
    if (limit !== undefined || !parent) {
      return;
    }
    const observer = new ResizeObserver(fit);
    observer.observe(parent);
    return () => observer.disconnect();
  }, [text, limit]);
  return (
    <span ref={ref} title={text}>
      {shown}
    </span>
  );
}
