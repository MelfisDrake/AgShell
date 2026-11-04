import { useEffect, useRef } from "react";
import { AppState } from "./bridge";
import { SessionName, switcherDetail } from "./store";
import { useSwitcher } from "./switcher";
import { SessionRow } from "./SessionRow";

export function SessionSwitcher({ state }: { state: AppState }) {
  const switcher = useSwitcher();
  const rowsRef = useRef<HTMLDivElement>(null);
  // agterm's scrollTo on every step: ten rows at a large interface size can run past a short window.
  useEffect(() => {
    rowsRef.current?.querySelector(".switcher-row.selected")?.scrollIntoView({ block: "nearest" });
  }, [switcher?.index]);
  if (!switcher) {
    return null;
  }

  return (
    <div className="switcher-backdrop">
      <div className="switcher">
        <div className="switcher-rows" ref={rowsRef}>
          {switcher.candidates.map((id, position) => {
            const workspace = state.workspaces.find((w) => w.sessions.some((session) => session.id === id));
            const session = workspace?.sessions.find((s) => s.id === id);
            if (!workspace || !session) {
              return null;
            }
            return (
              <div key={id} className={"switcher-row" + (position === switcher.index ? " selected" : "")}>
                <SessionRow
                  name={<SessionName session={session} />}
                  remote={session.remoteHost !== null}
                  subtitle={`${workspace.name} · ${switcherDetail(session)}`}
                  status={session.status}
                  statusColor={session.statusColor}
                  statusShape={session.statusShape}
                  settings={state.settings}
                />
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
