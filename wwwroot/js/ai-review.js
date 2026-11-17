(() => {
    const dialog = document.querySelector("#ai-review-dialog");
    const opener = document.querySelector("[data-ai-review-open]");
    if (dialog && opener) {
        const container = dialog.querySelector("[data-ai-review-container]");
        opener.addEventListener("click", () => {
            dialog.showModal();
            loadPanel(container, opener.dataset.panelUrl);
        });
        dialog.querySelector("[data-ai-review-close]")?.addEventListener("click", () => dialog.close());
    }
    document.querySelectorAll("[data-ai-review-container]").forEach(bind);

    async function loadPanel(container, url) {
        container.innerHTML = '<p class="text-secondary mb-0">Loading…</p>';
        try {
            const response = await fetch(url, { cache: "no-store" });
            container.innerHTML = response.ok ? await response.text() : '<div class="alert tf-alert error">The AI review panel could not be loaded.</div>';
        } catch {
            container.innerHTML = '<div class="alert tf-alert error">The AI review panel could not be loaded.</div>';
        }
        bind(container);
    }

    function bind(container) {
        const panel = container.querySelector("[data-ai-review-panel]");
        if (!panel || panel.dataset.bound) return;
        panel.dataset.bound = "true";
        const base = panel.dataset.basePath;
        const date = panel.dataset.date;
        const form = panel.querySelector("[data-ai-review-form]");
        const startButton = panel.querySelector("[data-ai-review-start]");
        const errorBox = panel.querySelector("[data-ai-review-error]");
        const progress = panel.querySelector("[data-ai-review-progress]");
        const progressText = panel.querySelector("[data-ai-review-progress-text]");
        const runs = panel.querySelector("[data-ai-review-runs]");
        const token = () => panel.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";
        let activeRunId = null;

        const estimate = panel.querySelector("[data-ai-token-estimate]");
        const imageCount = panel.querySelector("[data-ai-image-count]");
        panel.querySelectorAll("[data-ai-image-toggle]").forEach((toggle) => toggle.addEventListener("change", () => {
            const checked = [...panel.querySelectorAll("[data-ai-image-toggle]:checked")];
            if (imageCount) imageCount.textContent = checked.length;
            if (estimate) {
                const total = Number(estimate.dataset.baseTokens) + checked.reduce((sum, item) => sum + Number(item.dataset.tokens || 0), 0);
                estimate.textContent = total.toLocaleString();
            }
        }));

        const promptSelect = panel.querySelector("[data-ai-prompt]");
        const promptDescription = panel.querySelector("[data-ai-prompt-description]");
        promptSelect?.addEventListener("change", () => {
            if (promptDescription) promptDescription.textContent = promptSelect.selectedOptions[0]?.dataset.description ?? "";
        });

        form?.addEventListener("submit", async (event) => {
            event.preventDefault();
            showError(null);
            setRunning(true, "Preparing the review…");
            try {
                const body = new FormData(form);
                if (!body.has("__RequestVerificationToken")) body.append("__RequestVerificationToken", token());
                const response = await fetch(form.action, { method: "POST", body, headers: { Accept: "application/json" } });
                const result = await response.json().catch(() => ({}));
                if (!response.ok || !result.runId) {
                    setRunning(false);
                    const fallback = response.status === 400
                        ? "The review could not start because the page's security token was rejected. Reload the page and try again."
                        : `The review could not start (HTTP ${response.status}).`;
                    showError(result.error || fallback, result.usageLimit);
                    return;
                }
                track(result.runId);
            } catch {
                setRunning(false);
                showError("The review could not start. Check that TradeFoundry is still running.");
            }
        });

        panel.querySelector("[data-ai-review-cancel]")?.addEventListener("click", async () => {
            if (!activeRunId) return;
            await post(`${base}?handler=Cancel&runId=${encodeURIComponent(activeRunId)}`);
        });

        panel.addEventListener("click", async (event) => {
            const article = event.target.closest("[data-ai-review-run]");
            if (!article) return;
            if (event.target.closest("[data-ai-review-copy]")) {
                const text = article.querySelector("[data-ai-review-markdown]")?.value ?? "";
                const button = event.target.closest("[data-ai-review-copy]");
                try {
                    await navigator.clipboard.writeText(text);
                    button.textContent = "Copied";
                } catch {
                    button.textContent = "Copy failed";
                }
                window.setTimeout(() => { button.textContent = "Copy"; }, 2000);
            } else if (event.target.closest("[data-ai-review-delete]")) {
                if (!window.confirm("Delete this AI review? This can't be undone.")) return;
                const response = await post(`${base}?handler=Delete&runId=${encodeURIComponent(article.dataset.aiReviewRun)}`);
                if (response.ok) article.remove();
            }
        });

        if (panel.dataset.activeRun) track(panel.dataset.activeRun);

        function track(runId) {
            activeRunId = runId;
            setRunning(true, "Reviewing…");
            const poll = async () => {
                let status;
                try {
                    const response = await fetch(`${base}?handler=Progress&runId=${encodeURIComponent(runId)}`, { cache: "no-store", headers: { Accept: "application/json" } });
                    status = response.ok ? await response.json() : null;
                } catch {
                    status = null;
                }
                if (status && status.status === "running") {
                    const seconds = Math.round(status.elapsedSeconds);
                    progressText.textContent = status.characters > 0
                        ? `Reviewing… ${seconds}s · receiving the review (${status.characters.toLocaleString()} characters)`
                        : `Reviewing… ${seconds}s · the model is reading your day`;
                    window.setTimeout(poll, 1000);
                    return;
                }
                if (!status) {
                    window.setTimeout(poll, 2000);
                    return;
                }
                activeRunId = null;
                setRunning(false);
                await showRun(runId);
            };
            window.setTimeout(poll, 800);
        }

        async function showRun(runId) {
            const response = await fetch(`${base}?handler=Run&runId=${encodeURIComponent(runId)}&date=${encodeURIComponent(date)}`, { cache: "no-store" });
            if (!response.ok) return;
            const html = await response.text();
            runs.querySelector(`[data-ai-review-run="${CSS.escape(runId)}"]`)?.remove();
            runs.insertAdjacentHTML("afterbegin", html);
            runs.firstElementChild?.scrollIntoView({ behavior: "smooth", block: "nearest" });
        }

        function setRunning(running, message) {
            if (startButton) startButton.disabled = running;
            if (progress) progress.hidden = !running;
            if (message && progressText) progressText.textContent = message;
        }

        function showError(message, usageLimit) {
            if (!errorBox) return;
            errorBox.hidden = !message;
            errorBox.textContent = message ?? "";
            if (message && usageLimit) {
                const link = document.createElement("a");
                link.className = "btn btn-sm btn-primary ms-2";
                link.href = "https://chatgpt.com/settings/usage";
                link.target = "_blank";
                link.rel = "noopener noreferrer";
                link.textContent = "Manage usage";
                errorBox.append(link);
            }
        }

        function post(url) {
            const body = new FormData();
            body.append("__RequestVerificationToken", token());
            return fetch(url, { method: "POST", body, headers: { Accept: "application/json" } });
        }
    }
})();
