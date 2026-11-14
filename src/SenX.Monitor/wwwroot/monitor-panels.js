// Remembers which list panels are open, in this browser only. A panel is a <details data-panel="name">.
const key = name => "monitor.panel." + name;
let watching = false;

function apply(panel) {
    try {
        const saved = localStorage.getItem(key(panel.dataset.panel));
        if (saved !== null)
            panel.open = saved === "1";
    } catch {
        // Storage can be off, as in a private window. Panels then start open.
    }
}

function applyAll(root) {
    if (root.matches && root.matches("details[data-panel]"))
        apply(root);

    if (root.querySelectorAll)
        root.querySelectorAll("details[data-panel]").forEach(apply);
}

export function remember(root) {
    applyAll(root);

    if (watching)
        return;

    watching = true;

    // Toggle doesn't bubble, so this listens on the way down.
    document.addEventListener("toggle", event => {
        const panel = event.target;
        if (!(panel instanceof HTMLDetailsElement) || !panel.dataset.panel)
            return;

        try {
            localStorage.setItem(key(panel.dataset.panel), panel.open ? "1" : "0");
        } catch {
        }
    }, true);

    // Panels come and go as results change, so each new one gets its saved state.
    new MutationObserver(records => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.nodeType === 1)
                    applyAll(node);
            }
        }
    }).observe(document.body, { childList: true, subtree: true });
}
