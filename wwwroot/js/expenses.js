(() => {
  const editor = document.getElementById("expense-editor");
  const add = document.querySelector("[data-expense-open]");
  add?.addEventListener("click", event => {
    if (new URLSearchParams(location.search).has("edit")) return;
    event.preventDefault();
    editor.showModal();
    document.documentElement.classList.add("tf-expense-editor-open");
  });
  document.querySelector("[data-expense-close]")?.addEventListener("click", () => editor.close());
  editor?.addEventListener("close", () => {
    document.documentElement.classList.remove("tf-expense-editor-open");
    add?.focus();
  });
  if (editor?.dataset.open === "true") {
    editor.showModal();
    document.documentElement.classList.add("tf-expense-editor-open");
  }
  const dialog = document.getElementById("expense-categories");
  const opener = document.querySelector("[data-expense-categories-open]");
  opener?.addEventListener("click", () => dialog.showModal());
  document.querySelector("[data-expense-categories-close]")?.addEventListener("click", () => dialog.close());
  dialog?.addEventListener("close", () => opener?.focus());
  if (dialog?.dataset.open === "true") dialog.showModal();
  document.querySelectorAll("[data-expense-confirm]").forEach(form => {
    form.addEventListener("submit", event => {
      if (!window.confirm(form.dataset.expenseConfirm)) event.preventDefault();
    });
  });
})();
