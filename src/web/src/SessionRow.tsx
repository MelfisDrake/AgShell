import { ReactNode } from "react";
import { AgentStatus, SettingsView, StatusShape } from "./bridge";
import { CloudIcon } from "./icons";
import { Glyph } from "./StatusGlyph";
import { MiddleTruncated } from "./MiddleTruncated";

interface Props {
  name: ReactNode;
  remote: boolean;
  subtitle: string;
  status: AgentStatus;
  statusColor: string | null;
  statusShape: StatusShape | null;
  settings: SettingsView;
}

/**
 * agterm's SessionSwitcherRow, shared by the Ctrl+Tab switcher and the title bar's recent and attention lists: the name,
 * led by a cloud for an attached session, over its subtitle, and the status glyph at the end.
 */
export function SessionRow({ name, remote, subtitle, status, statusColor, statusShape, settings }: Props) {
  return (
    <>
      <div className="session-row-text">
        <div className="switcher-title">
          {remote && <CloudIcon />}
          <span className="session-row-name">{name}</span>
        </div>
        <div className="switcher-subtitle">
          <MiddleTruncated text={subtitle} />
        </div>
      </div>
      <Glyph status={status} settings={settings} color={statusColor} shape={statusShape} />
    </>
  );
}
