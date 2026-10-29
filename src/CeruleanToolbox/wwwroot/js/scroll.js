// Brings an element into view after Blazor has rendered it (used when a detail panel opens below a long list).
export function scrollToId(id) {
    const element = document.getElementById(id);
    if (!element) return;
    const reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    element.scrollIntoView({ behavior: reduce ? "auto" : "smooth", block: "start" });
}
