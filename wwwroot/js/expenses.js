(() => {
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
