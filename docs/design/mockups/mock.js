// Mockup helpers: theme toggle (follows OS by default) and tiny inline icon set.
const ICONS = {
  look: '<path d="M12 3a9 9 0 100 18 9 9 0 000-18zm0 2v14a7 7 0 010-14z"/>',
  frame: '<path d="M4 4h16v16H4zM7 7v10h10V7z" fill-rule="evenodd"/>',
  export: '<path d="M12 3v11m0 0l-4-4m4 4l4-4M5 17v3h14v-3" fill="none" stroke="currentColor" stroke-width="2"/>',
  undo: '<path d="M9 7L4 12l5 5M4 12h10a6 6 0 010 8" fill="none" stroke="currentColor" stroke-width="2"/>',
  redo: '<path d="M15 7l5 5-5 5M20 12H10a6 6 0 000 8" fill="none" stroke="currentColor" stroke-width="2"/>',
  more: '<circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/>',
  plus: '<path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="2"/>',
  camera: '<path d="M4 7h4l2-2h4l2 2h4v12H4z" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="12" cy="13" r="3.5"/>',
  folder: '<path d="M3 6h7l2 2h9v11H3z" fill="none" stroke="currentColor" stroke-width="2"/>',
  grid: '<path d="M4 4h16v16H4zM4 10h16M4 15h16M10 4v16M15 4v16" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  adjust: '<path d="M4 8h10M18 8h2M4 16h2M10 16h10" stroke="currentColor" stroke-width="2"/><circle cx="16" cy="8" r="2"/><circle cx="8" cy="16" r="2"/>',
};
document.querySelectorAll("[data-i]").forEach(el => {
  el.innerHTML = '<svg viewBox="0 0 24 24" width="20" height="20" fill="currentColor" aria-hidden="true">' + ICONS[el.dataset.i] + "</svg>";
  el.style.display = "inline-grid";
});
const root = document.documentElement;
const saved = (() => { try { return localStorage.getItem("mock-theme"); } catch { return null; } })();
root.dataset.theme = new URLSearchParams(location.search).get("theme") || saved || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
document.querySelector(".toggle-theme")?.addEventListener("click", () => {
  root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
  try { localStorage.setItem("mock-theme", root.dataset.theme); } catch {}
});
// pressed/selected demo behaviour
document.querySelectorAll(".chips").forEach(g => g.addEventListener("click", e => {
  const c = e.target.closest(".chip"); if (!c) return;
  g.querySelectorAll(".chip").forEach(x => x.setAttribute("aria-pressed", String(x === c)));
}));
document.querySelectorAll(".grid3").forEach(g => g.addEventListener("click", e => {
  const t = e.target.closest(".tile"); if (!t) return;
  g.querySelectorAll(".tile").forEach(x => x.setAttribute("aria-selected", String(x === t)));
}));
