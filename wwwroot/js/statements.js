(() => {
  document.querySelectorAll("form[data-recon-confirm]").forEach(form => {
    form.addEventListener("submit", event => {
      if (!window.confirm(form.dataset.reconConfirm)) event.preventDefault();
    });
  });
})();
