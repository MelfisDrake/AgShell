import { CSSProperties, useEffect, useLayoutEffect, useRef, useState } from "react";
import { command, OverlayView, Pane, PageInfo, PageView, send } from "./bridge";
import { setHole } from "./holes";
import { reportOverlayGeometry } from "./overlays";
import { identityOf, pageInfo, registerPage } from "./pages";
import { t } from "./i18n";
import { CloseIcon, PageStripIcon } from "./icons";
import { MiddleTruncated } from "./MiddleTruncated";

interface Props {
  sessionId: string;
  slot: Pane;
  overlay: OverlayView;
  page: PageView;
  style: CSSProperties;
  visible: boolean;
  floating: boolean;
  /** Something of agshell's own (a palette, a menu) is over the page; the native page goes under the interface for it. */
  covered: boolean;
  /** The page is where the user works: as agterm's, only then does it take the keyboard when it shows. */
  active: boolean;
}

/**
 * A local HTML file or a web page over a session or one of its panes, under a strip naming it, as agterm shows them.
 * Both run in a native browser the host places over the room left here, as agterm's WKWebView: a local page is served
 * from its own host with the bridge script and, without --js, a Content-Security-Policy; a web page is a top-level page,
 * so any site opens, logins work and history moves both ways.
 */
export function PageOverlay({ sessionId, slot, overlay, page, style, visible, floating, covered, active }: Props) {
  const roomRef = useRef<HTMLDivElement>(null);
  // agterm's label: the name keeps its width before the title does, so it is cut only to what the label holds less the dot.
  const labelRef = useRef<HTMLSpanElement>(null);
  const [labelWidth, setLabelWidth] = useState<number | undefined>(undefined);
  useLayoutEffect(() => {
    const label = labelRef.current;
    if (!label) {
      return;
    }
    const measure = () => setLabelWidth(label.clientWidth);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(label);
    return () => observer.disconnect();
  }, [page.chromeless]);
  const focusedRef = useRef(false);
  const [info, setInfo] = useState<PageInfo | undefined>(() => pageInfo(overlay.id));
  const failed = info?.state === "failed";
  const [holed, setHoled] = useState(false);

  const navigate = (action: "back" | "forward" | "reload") => send({ type: "pageNav", overlayId: overlay.id, action });

  useEffect(() => {
    setInfo(pageInfo(overlay.id));
    return registerPage(overlay.id, sessionId, slot, { info: setInfo });
  }, [overlay.id, sessionId, slot]);

  // Tell the host where the native browser goes, whenever that room moves, resizes, shows or hides.
  useLayoutEffect(() => {
    const report = () => {
      const room = roomRef.current;
      if (!room) {
        return;
      }
      const rect = room.getBoundingClientRect();
      // A failed page steps aside for the panel that says so, as agterm's, so it never reads as a blank or stale one.
      const shown = visible && !failed && rect.width > 0 && rect.height > 0;
      // As agterm's, an overlay leaves the page on screen under it; the interface's browser takes the mouse meanwhile.
      const raised = shown && !covered;
      setHole(overlay.id, shown ? rect : null);
      setHoled(shown);
      // Once per activation, as agterm's: closing what covered the page gives it the keyboard back, as focusActiveSession.
      const focus = raised && active && !focusedRef.current;
      focusedRef.current = raised && active;
      // agterm clips a floating panel, its page included, to the panel's radius of 12, inside its border.
      const radius = floating ? 11 : 0;
      send({ type: "nativePage", overlayId: overlay.id, x: rect.left, y: rect.top, width: rect.width, height: rect.height, visible: shown, raised, focus, radius, roundTop: page.chromeless });
      if (floating && shown) {
        reportOverlayGeometry(overlay.id, rect.width, rect.height);
      }
    };
    report();
    const observer = new ResizeObserver(report);
    observer.observe(roomRef.current!);
    window.addEventListener("resize", report);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", report);
    };
  }, [overlay.id, visible, covered, failed, active, floating, style.left, style.top, style.right, style.bottom]);

  useEffect(() => {
    return () => {
      setHole(overlay.id, null);
      send({ type: "nativePage", overlayId: overlay.id, x: 0, y: 0, width: 0, height: 0, visible: false, raised: false, focus: false, radius: 0, roundTop: false });
    };
  }, [overlay.id]);

  const closeSlot = slot === "overlay" ? undefined : slot;
  return (
    <div className={"page-overlay" + (floating ? " floating" : "") + (visible ? "" : " hidden") + (holed ? " holed" : "")} style={style}>
      {!page.chromeless && (
        <div className="page-strip">
          {page.navigation && (
            <span className="page-actions">
              <button type="button" className="page-button" title={t("Назад")} disabled={!info?.canGoBack} onClick={() => navigate("back")}>
                <PageStripIcon name="back" />
              </button>
              <button type="button" className="page-button" title={t("Вперёд")} disabled={!info?.canGoForward} onClick={() => navigate("forward")}>
                <PageStripIcon name="forward" />
              </button>
              <button type="button" className="page-button" title={t("Обновить")} onClick={() => navigate("reload")}>
                <PageStripIcon name="reload" />
              </button>
            </span>
          )}
          <span className="page-label" ref={labelRef}>
            <span className="page-name" title={info?.page ?? page.source}>
              <MiddleTruncated text={identityOf(page, info)} limit={labelWidth === undefined ? undefined : Math.max(0, labelWidth - (info?.title ? 24 : 0))} />
            </span>
            {info?.title && (
              <>
                <span className="page-dot">·</span>
                <span className="page-title" title={info.title}>
                  {info.title}
                </span>
              </>
            )}
          </span>
          <span className="page-actions">
            {page.navigation && !page.remoteFile && (
              <>
                <button type="button" className="page-button" title={t("Открыть в браузере")} onClick={() => send({ type: "pageOpenCurrent", overlayId: overlay.id })}>
                  <PageStripIcon name="browser" />
                </button>
                {page.isFile ? (
                  <button type="button" className="page-button" title={t("Показать в проводнике")} onClick={() => send({ type: "pageReveal", overlayId: overlay.id })}>
                    <PageStripIcon name="external" />
                  </button>
                ) : (
                  <button type="button" className="page-button" title={t("Копировать ссылку")} onClick={() => send({ type: "pageCopyLink", overlayId: overlay.id })}>
                    <PageStripIcon name="link" />
                  </button>
                )}
              </>
            )}
            <button
              type="button"
              className="page-button"
              title={t("Скрыть, не закрывая")}
              onClick={() => command({ command: "overlayVisibility", sessionId, slot: slot === "overlay" ? undefined : slot, mode: "hide" })}
            >
              <PageStripIcon name="minus" />
            </button>
            <button type="button" className="page-button" title={t("Закрыть (Ctrl+Shift+W)")} onClick={() => command({ command: "closeOverlay", sessionId, slot: closeSlot })}>
              <CloseIcon />
            </button>
          </span>
        </div>
      )}
      <div ref={roomRef} className="page-frame" style={overlay.backgroundColor && !holed ? { background: overlay.backgroundColor } : undefined}>
        {failed && (
          <div className="page-failure">
            <div className="page-failure-sign">⚠</div>
            <div className="page-failure-head">{t("Страницу не удалось загрузить")}</div>
            {info?.error && <div className="page-failure-error">{info.error}</div>}
          </div>
        )}
      </div>
    </div>
  );
}
