import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./styles.css";
import { THEME_KEY, parseThemePreference, resolveTheme } from "./theme";

// Farbschema vor dem ersten Zeichnen setzen, damit kein heller Blitz entsteht.
try {
  const preference = parseThemePreference(window.localStorage.getItem(THEME_KEY));
  document.documentElement.dataset.theme = resolveTheme(preference, window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false);
} catch {
  document.documentElement.dataset.theme = "light";
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);