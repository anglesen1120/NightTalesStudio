(() => {
  'use strict';

  const doc = document;
  const locale = () => (doc.getElementById('locale')?.value === 'en' ? 'en' : 'vi');
  const t = (vi, en) => (locale() === 'en' ? en : vi);

  function number(value, fallback) {
    const n = Number(value);
    return Number.isFinite(n) && n > 0 ? n : fallback;
  }

  function pageSequence(current, total) {
    const pages = new Set([1, total, current - 2, current - 1, current, current + 1, current + 2]);
    const sorted = [...pages].filter(n => n >= 1 && n <= total).sort((a, b) => a - b);
    const out = [];
    let previous = 0;
    sorted.forEach(n => {
      if (previous && n - previous > 1) {
        if (n - previous === 2) out.push(previous + 1);
        else out.push(null);
      }
      out.push(n);
      previous = n;
    });
    return out;
  }

  function cloneRow(row, page, index) {
    const clone = row.cloneNode(true);
    const ids = new Map();
    if (clone.id) {
      const next = `pager-${page}-${index}-${clone.id}`;
      ids.set(clone.id, next);
      clone.id = next;
    }
    clone.querySelectorAll('[id]').forEach(el => {
      const old = el.id;
      const next = `pager-${page}-${index}-${old}`;
      ids.set(old, next);
      el.id = next;
    });
    clone.querySelectorAll('[for]').forEach(el => {
      const ref = el.getAttribute('for');
      if (ids.has(ref)) el.setAttribute('for', ids.get(ref));
    });
    ['aria-describedby', 'aria-labelledby', 'aria-controls', 'aria-owns'].forEach(attr => {
      clone.querySelectorAll(`[${attr}]`).forEach(el => {
        const value = el.getAttribute(attr).split(/\s+/).map(ref => ids.get(ref) || ref).join(' ');
        el.setAttribute(attr, value);
      });
    });
    clone.querySelectorAll('[data-od-id]').forEach(el => el.removeAttribute('data-od-id'));
    clone.querySelectorAll('input[type=checkbox]').forEach(el => el.removeAttribute('checked'));
    return clone;
  }

  function init(nav) {
    if (nav.hasAttribute('data-pager-static')) return;

    const table = nav.dataset.pagerTable
      ? doc.querySelector(nav.dataset.pagerTable)
      : nav.closest('section')?.querySelector('table');
    const tbody = table?.querySelector('tbody') || null;
    const sourceRows = tbody ? [...tbody.querySelectorAll(':scope > tr')] : [];
    const sizeSelect = nav.querySelector('[data-pager-size]');
    const numbers = nav.querySelector('[data-pager-numbers]');
    const compactVi = nav.querySelector('[data-pager-compact-vi]');
    const compactEn = nav.querySelector('[data-pager-compact-en]');
    const rangeVi = nav.querySelector('[data-pager-range-vi]');
    const rangeEn = nav.querySelector('[data-pager-range-en]');
    const empty = nav.parentElement.querySelector('[data-pager-empty]');
    const filterNone = nav.closest('.episode-filter, .list-content')?.querySelector('.filter-none') || null;
    const search = doc.getElementById('collection-search');
    const seriesRadios = [...doc.querySelectorAll('input[name="series-filter"]')];
    const statusSelect = doc.getElementById('status-filter');
    const seriesSelect = doc.getElementById('series-filter');
    const isSeries = nav.dataset.pagerKind === 'series' || seriesRadios.length > 0;
    const baseTotal = number(nav.dataset.total, sourceRows.length);
    let pageSize = number(sizeSelect?.value || nav.dataset.pageSize, 20);
    let page = 1;

    const first = nav.querySelector('[data-page-action="first"]');
    const previous = nav.querySelector('[data-page-action="previous"]');
    const next = nav.querySelector('[data-page-action="next"]');
    const last = nav.querySelector('[data-page-action="last"]');

    function filtered() {
      const query = (search?.value || '').trim().toLowerCase();
      let rows = sourceRows.slice();
      let total = baseTotal;

      if (isSeries) {
        const filter = seriesRadios.find(radio => radio.checked)?.id || 'filter-all';
        if (filter === 'filter-active') rows = rows.filter(row => !row.classList.contains('archived-record'));
        if (filter === 'filter-archived') rows = rows.filter(row => row.classList.contains('archived-record'));
        if (query) {
          rows = rows.filter(row => row.textContent.toLowerCase().includes(query));
          total = rows.length;
        } else if (filter === 'filter-archived') {
          total = number(nav.dataset.totalArchived, rows.length);
        } else if (filter === 'filter-all') {
          total = number(nav.dataset.totalAll, baseTotal);
        }
      } else {
        const status = statusSelect?.value || 'all';
        const series = seriesSelect?.selectedOptions?.[0]?.id || '';
        if (status !== 'all') rows = rows.filter(row => row.dataset.status === status);
        if (series === 'series-night') rows = rows.filter(row => row.dataset.series === 'night');
        else if (series === 'series-crime') rows = rows.filter(row => row.dataset.series === 'crime');
        if (query) {
          rows = rows.filter(row => row.textContent.toLowerCase().includes(query));
          total = rows.length;
        } else if (status !== 'all' || series === 'series-night' || series === 'series-crime') {
          total = rows.length;
        }
      }
      return { rows, total: Math.max(0, total) };
    }

    function setDisabled(button, disabled) {
      if (!button) return;
      button.setAttribute('aria-disabled', disabled ? 'true' : 'false');
    }

    function renderNumbers(totalPages) {
      if (!numbers) return;
      numbers.replaceChildren();
      pageSequence(page, totalPages).forEach(value => {
        if (value === null) {
          const gap = doc.createElement('span');
          gap.className = 'pager-ellipsis';
          gap.setAttribute('aria-hidden', 'true');
          gap.textContent = '…';
          numbers.append(gap);
          return;
        }
        const button = doc.createElement('button');
        button.type = 'button';
        button.className = 'pager-btn pager-num';
        button.dataset.page = String(value);
        button.textContent = String(value);
        button.setAttribute('aria-label', t(`Trang ${value}`, `Page ${value}`));
        if (value === page) button.setAttribute('aria-current', 'page');
        numbers.append(button);
      });
    }

    function render(options = {}) {
      const { rows, total } = filtered();
      const totalPages = Math.max(1, Math.ceil(total / pageSize));
      page = Math.min(Math.max(1, page), totalPages);
      const start = total ? (page - 1) * pageSize + 1 : 0;
      const end = Math.min(total, page * pageSize);

      nav.hidden = total === 0;
      if (empty) empty.hidden = total !== 0;
      if (filterNone) filterNone.style.display = total === 0 ? 'grid' : '';

      nav.querySelector('.pager-range').setAttribute('aria-live', 'polite');
      if (rangeVi) rangeVi.textContent = total ? `Hiển thị ${start}–${end} trên ${total}` : '0 kết quả';
      if (rangeEn) rangeEn.textContent = total ? `Showing ${start}–${end} of ${total}` : '0 results';
      if (compactVi) compactVi.textContent = `Trang ${page} / ${totalPages}`;
      if (compactEn) compactEn.textContent = `Page ${page} of ${totalPages}`;

      setDisabled(first, page <= 1);
      setDisabled(previous, page <= 1);
      setDisabled(next, page >= totalPages);
      setDisabled(last, page >= totalPages);
      renderNumbers(totalPages);
      if (options.focusNumber) {
        numbers?.querySelector(`[data-page="${page}"]`)?.focus({ preventScroll: true });
      }

      if (!tbody) return;
      if (!total) {
        tbody.replaceChildren();
        return;
      }
      const count = Math.min(pageSize, Math.max(0, total - (page - 1) * pageSize));
      const pool = rows.length ? rows : sourceRows;
      const fragment = doc.createDocumentFragment();
      const offset = pool.length ? ((page - 1) * 7) % pool.length : 0;
      for (let i = 0; i < count && pool.length; i += 1) {
        fragment.append(cloneRow(pool[(offset + i) % pool.length], page, i));
      }
      tbody.replaceChildren(fragment);

    }

    function go(action) {
      const { total } = filtered();
      const totalPages = Math.max(1, Math.ceil(total / pageSize));
      if (action === 'first') page = 1;
      if (action === 'previous') page = Math.max(1, page - 1);
      if (action === 'next') page = Math.min(totalPages, page + 1);
      if (action === 'last') page = totalPages;
      render();
    }

    nav.addEventListener('click', event => {
      const button = event.target.closest('button[data-page], button[data-page-action]');
      if (!button || !nav.contains(button)) return;
      if (button.getAttribute('aria-disabled') === 'true') return;
      if (button.dataset.page) {
        page = Number(button.dataset.page);
        render({ focusNumber: true });
        return;
      }
      go(button.dataset.pageAction);
    });

    sizeSelect?.addEventListener('change', () => {
      pageSize = number(sizeSelect.value, 20);
      page = 1;
      render();
    });

    search?.addEventListener('input', () => {
      page = 1;
      render();
    });

    [...seriesRadios, statusSelect, seriesSelect].filter(Boolean).forEach(control => {
      control.addEventListener('change', () => {
        page = 1;
        render();
      });
    });

    doc.getElementById('locale')?.addEventListener('change', () => render());

    setTimeout(() => render(), 0);
  }

  if (doc.readyState === 'loading') {
    doc.addEventListener('DOMContentLoaded', () => doc.querySelectorAll('[data-pager]').forEach(init));
  } else {
    doc.querySelectorAll('[data-pager]').forEach(init);
  }
})();
