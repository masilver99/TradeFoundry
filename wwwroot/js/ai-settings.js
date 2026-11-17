(() => {
    // Poll the in-flight ChatGPT sign-in and reload once it finishes.
    const pending = document.querySelector("[data-ai-signin-pending]");
    if (pending) {
        const url = pending.dataset.statusUrl;
        const poll = async () => {
            try {
                const response = await fetch(url, { headers: { Accept: "application/json" }, cache: "no-store" });
                if (response.ok) {
                    const status = await response.json();
                    if (status.state !== "pending") {
                        window.location.reload();
                        return;
                    }
                }
            } catch {
                // Keep polling; the server may be busy finishing sign-in.
            }
            window.setTimeout(poll, 2000);
        };
        window.setTimeout(poll, 2000);
    }

    // Keep the reasoning options in step with the selected model.
    const model = document.querySelector("[data-ai-model]");
    const effort = document.querySelector("[data-ai-effort]");
    if (model && effort) {
        model.addEventListener("change", () => {
            const option = model.selectedOptions[0];
            const levels = (option?.dataset.levels || "").split(" ").filter(Boolean);
            const fallback = option?.dataset.defaultLevel;
            const previous = effort.value;
            effort.replaceChildren();
            const defaultOption = document.createElement("option");
            defaultOption.value = "";
            defaultOption.textContent = fallback ? `Model default (${fallback})` : "Model default";
            effort.append(defaultOption);
            for (const level of levels) {
                const item = document.createElement("option");
                item.value = level;
                item.textContent = level;
                item.selected = level === previous;
                effort.append(item);
            }
        });
    }
})();
