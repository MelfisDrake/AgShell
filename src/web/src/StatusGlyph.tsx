import { AgentStatus, SessionView, SettingsView, StatusShape } from "./bridge";
import { t, tr } from "./i18n";

/** agterm's tooltipText: "Agent status: " and the state's capitalized name, only on the sidebar's glyph. */
const statusTooltips: Record<string, string> = {
  active: tr("Статус агента: работает"),
  completed: tr("Статус агента: закончил"),
  blocked: tr("Статус агента: ждёт ответа"),
};

interface GlyphProps {
  status: AgentStatus;
  settings: SettingsView;
  blink?: boolean;
  color?: string | null;
  shape?: StatusShape | null;
  tooltip?: boolean;
}

/** The agent status glyph: the settings' shape for the state, or the one a status call gave, in its own color if it gave one. */
export function Glyph({ status, settings, blink, color, shape, tooltip }: GlyphProps) {
  if (status === "idle") {
    return null;
  }
  const fallback = status === "active" ? settings.activeStatusShape : status === "blocked" ? settings.blockedStatusShape : settings.completedStatusShape;
  return (
    <span
      className={`status agent-${status} shape-${shape ?? fallback}` + (blink ? " blink" : "")}
      style={color ? { background: color } : undefined}
      title={tooltip ? t(statusTooltips[status]) : undefined}
    />
  );
}

export function StatusGlyph({ session, settings }: { session: SessionView; settings: SettingsView }) {
  return <Glyph status={session.status} settings={settings} blink={session.blink} color={session.statusColor} shape={session.statusShape} tooltip />;
}
