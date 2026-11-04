import { createRoot } from "react-dom/client";
import "@xterm/xterm/css/xterm.css";
import "./style.css";
import { App } from "./App";
import { startQuickActions } from "./actions";
import { QuickApp } from "./QuickTerminal";
import { SettingsApp } from "./Settings";
import { reportTerminalFocus } from "./bridge";
import { startHostConnection } from "./store";
import { startMenuBar } from "./menuBar";
import { startSwitcherListeners } from "./switcher";
import { startShortcutMonitoring } from "./shortcuts";

// The quick panel and the settings window load the same page as their own windows and show only their part.
createRoot(document.getElementById("root")!).render(location.hash === "#quick" ? <QuickApp /> : location.hash === "#settings" ? <SettingsApp /> : <App />);
startSwitcherListeners();
startHostConnection();
startShortcutMonitoring();
startQuickActions();
if (location.hash === "") {
  startMenuBar();
}
reportTerminalFocus();
