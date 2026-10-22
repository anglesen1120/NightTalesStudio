(() => {
  'use strict';

  const multi = new Map([
    ['Tên / Loại Name / Kind', ['Tên, Loại', 'Name, Kind']],
    ['Mô tả / Nội dung Description / Content', ['Mô tả, Nội dung', 'Description, Content']],
    ['Nguồn / Xuất hiện Source / Appearance', ['Nguồn, Xuất hiện', 'Source, Appearance']],
    ['Ngôn ngữ / Khung Language / Aspect', ['Ngôn ngữ, Khung', 'Language, Aspect']],
    ['Lịch / Tự động Schedule / Autopilot', ['Lịch, Tự động', 'Schedule, Autopilot']],
    ['Tác giả / AI Author / AI', ['Tác giả, AI', 'Author, AI']],
    ['Kênh / Lịch Channel / Schedule', ['Kênh, Lịch', 'Channel, Schedule']],
    ['Số mẫu / mục tiêu Evaluated / target', ['Số mẫu, mục tiêu', 'Evaluated, target']]
  ]);

  const hiddenAtTablet = new Set([
    'series', 'channels', 'format', 'account', 'permission', 'connection',
    'topic', 'language', 'aspect', 'voice', 'style', 'corroboration', 'used',
    'words', 'author', 'attempt', 'count', 'detail', 'duration', 'result',
    'started', 'source', 'next', 'updated'
  ]);
  const primaryCols = ['title', 'step', 'code', 'channel', 'version', 'segment', 'metric', 'run', 'episode', 'field', 'page', 'wire', 'factor', 'device'];
  const statusCols = ['status', 'state'];
  const metaCols = ['stage', 'updated', 'created', 'schedule', 'date', 'next', 'count', 'value', 'publisher', 'description', 'started', 'result', 'permission', 'attempt', 'detail', 'duration'];

  let activeTip = null;
  let resizeTimer = 0;

  const locale = () => (document.getElementById('locale')?.value === 'en' ? 'en' : 'vi');

  function splitPair(value, lang) {
    if (!value || !value.includes(' / ')) return null;
    const override = multi.get(value);
    if (override) return override[lang === 'en' ? 1 : 0];
    const parts = value.split(' / ');
    if (parts.length === 2 && parts[0] && parts[1]) return parts[lang === 'en' ? 1 : 0];
    return null;
  }

  function localizeAttributes() {
    const lang = locale();
    document.querySelectorAll('[title],[aria-label],[alt]').forEach(el => {
      if (el.matches('[data-nts-row-title]')) return;
      ['title', 'aria-label', 'alt'].forEach(attr => {
        if (!el.hasAttribute(attr)) return;
        const key = 'nts' + attr.replace(/-/g, '');
        let original = el.dataset[key];
        if (original === undefined) {
          original = el.getAttribute(attr);
          el.dataset[key] = original;
        }
        const pair = splitPair(original, lang);
        if (pair) el.setAttribute(attr, pair);
      });
    });
  }

  function localizeOptions() {
    const lang = locale();
    document.querySelectorAll('select option').forEach(option => {
      if (option.dataset.ntsVi === undefined && option.textContent.includes(' / ')) {
        const parts = option.textContent.split(' / ');
        if (parts.length === 2 && parts[0] && parts[1]) {
          option.dataset.ntsVi = parts[0].trim();
          option.dataset.ntsEn = parts[1].trim();
          option.dataset.ntsValue = option.getAttribute('value') ?? option.value;
        }
      }
      if (option.dataset.ntsVi !== undefined) {
        option.textContent = lang === 'en' ? option.dataset.ntsEn : option.dataset.ntsVi;
        if (!option.hasAttribute('value')) option.setAttribute('value', option.dataset.ntsValue);
      }
    });
  }

  function localizeDescriptions() {
    const lang = locale();
    document.querySelectorAll('[aria-describedby]').forEach(el => {
      const text = cellText(el, lang);
      if (!text) return;
      el.getAttribute('aria-describedby').split(/\s+/).forEach(id => {
        const target = document.getElementById(id);
        if (target && target !== el) target.textContent = text;
      });
    });
  }

  function cellText(cell, lang) {
    const clone = cell.cloneNode(true);
    clone.querySelectorAll('.cell-label,.icon-tooltip,.sr,svg,progress,script,style').forEach(node => node.remove());
    clone.querySelectorAll(lang === 'en' ? '.vi' : '.en').forEach(node => node.remove());
    return clone.textContent.replace(/\s+/g, ' ').trim();
  }

  function localizeCellTitles() {
    const lang = locale();
    document.querySelectorAll('table.collection-table td[title],table.collection-table th[title]').forEach(el => {
      if (el.matches('[data-nts-row-title]')) return;
      if (el.dataset.col === 'actions') {
        el.removeAttribute('title');
        return;
      }
      let text = '';
      if (el.dataset.col === 'title' || el.classList.contains('cmp-primary')) {
        const heading = el.querySelector('h2,h3,.row-open');
        if (heading) text = cellText(heading, lang);
      }
      if (!text) text = cellText(el, lang);
      if (text) el.title = text;
    });
  }

  function enhanceTables() {
    document.querySelectorAll('table.collection-table.one-line-table').forEach(table => {
      const cols = [...table.querySelectorAll('thead th')].map(th => th.dataset.col || '');
      const primary = primaryCols.find(col => cols.includes(col)) || cols.find(col => col && col !== 'select' && col !== 'actions') || '';
      const status = statusCols.find(col => cols.includes(col)) || '';
      const meta = metaCols.find(col => cols.includes(col)) || '';
      const hasActions = cols.includes('actions');
      const tabletHidden = col => hiddenAtTablet.has(col) || (col === 'stage' && cols.includes('channel'));
      table.querySelectorAll('colgroup col').forEach(col => {
        col.classList.toggle('md-hide', tabletHidden(col.dataset.col));
      });
      table.querySelectorAll('thead th').forEach(th => {
        th.classList.toggle('md-hide', tabletHidden(th.dataset.col));
      });
      table.querySelectorAll('tbody tr').forEach(row => {
        if (row.querySelector('.state-message')) return;
        const cells = [...row.children].filter(cell => cell.tagName === 'TD');
        if (cells.length < 2) return;
        row.classList.add('has-compact');
        row.classList.toggle('no-status', !status);
        row.classList.toggle('no-meta', !meta);
        row.classList.toggle('no-actions', !hasActions);
        cells.forEach(cell => {
          const col = cell.dataset.col || '';
          cell.classList.remove('cmp-primary', 'cmp-status', 'cmp-meta', 'cmp-actions', 'cmp-hide', 'md-hide');
          if (col === primary) cell.classList.add('cmp-primary');
          else if (col === status) cell.classList.add('cmp-status');
          else if (col === meta) cell.classList.add('cmp-meta');
          else if (col === 'actions') cell.classList.add('cmp-actions');
          else cell.classList.add('cmp-hide');
          if (tabletHidden(col)) cell.classList.add('md-hide');
        });
      });
    });
  }

  const tabletWidths = {
    select: 30, status: 156, stage: 84, updated: 78, created: 80, date: 84,
    count: 52, next: 90, actions: 128, schedule: 124, publisher: 96,
    value: 68, words: 54, duration: 64, started: 70, result: 84,
    attempt: 90, permission: 100, connection: 84, format: 72, account: 90,
    author: 72, metric: 132, run: 90, code: 100, version: 64, segment: 90,
    wire: 72, step: 150, channel: 140, factor: 90, device: 90, episode: 150,
    field: 90, page: 140, detail: 90, source: 92
  };

  function layoutTables() {
    const tablet = window.matchMedia('(min-width:768px) and (max-width:1000px)').matches;
    document.querySelectorAll('table.collection-table.one-line-table').forEach(table => {
      const cols = [...table.querySelectorAll('colgroup col')];
      if (!cols.length) return;
      if (!tablet) {
        if (!table.dataset.ntsTablet) return;
        cols.forEach(col => {
          if (col.dataset.ntsShare !== undefined) {
            col.style.width = col.dataset.ntsShare;
            if (col.dataset.ntsShareVar) col.style.setProperty('--column-share', col.dataset.ntsShareVar);
            else col.style.removeProperty('--column-share');
            delete col.dataset.ntsShare;
            delete col.dataset.ntsShareVar;
          }
        });
        delete table.dataset.ntsTablet;
        return;
      }
      const width = table.getBoundingClientRect().width;
      if (width < 120) return;
      table.dataset.ntsTablet = '1';
      const visible = cols.filter(col => !col.classList.contains('md-hide'));
      let used = 0;
      const flexible = [];
      visible.forEach(col => {
        if (!col.dataset.ntsShare) {
          col.dataset.ntsShare = col.style.width;
          col.dataset.ntsShareVar = col.style.getPropertyValue('--column-share');
        }
        const fixed = tabletWidths[col.dataset.col];
        if (fixed) used += fixed;
        else flexible.push(col);
      });
      visible.forEach(col => {
        const fixed = tabletWidths[col.dataset.col];
        if (fixed) {
          col.style.width = fixed + 'px';
          col.style.setProperty('--column-share', fixed + 'px');
        }
      });
      let spare = Math.max(0, width - used);
      if (!flexible.length) {
        visible.forEach(col => {
          const fixed = tabletWidths[col.dataset.col] + 'px';
          col.style.width = fixed;
          col.style.setProperty('--column-share', fixed);
        });
        return;
      }
      const share = Math.floor(spare / flexible.length);
      flexible.forEach(col => {
        const px = Math.max(120, share);
        col.style.width = px + 'px';
        col.style.setProperty('--column-share', px + 'px');
        if (share < 120) used += 120;
        else used += share;
      });
      if (used > width) {
        visible.forEach(col => {
          const px = tabletWidths[col.dataset.col] + 'px';
          col.style.width = px;
          col.style.setProperty('--column-share', px);
        });
      }
    });
  }

  function updateRowTitles() {
    const compact = window.innerWidth <= 1000;
    const lang = locale();
    document.querySelectorAll('table.collection-table.one-line-table tbody tr').forEach(row => {
      if (compact) {
        const parts = [...row.children].filter(cell => cell.tagName === 'TD' && cell.dataset.col !== 'select' && cell.dataset.col !== 'actions').map(cell => cellText(cell, lang)).filter(Boolean);
        row.dataset.ntsRowTitle = '1';
        row.title = parts.join(' · ');
      } else if (row.dataset.ntsRowTitle) {
        row.removeAttribute('title');
        delete row.dataset.ntsRowTitle;
      }
    });
  }

  function placeTip(trigger, tip) {
    if (!trigger.isConnected || !tip.isConnected) return;
    if (!tip.classList.contains('is-floating')) return;
    tip.style.left = '0px';
    tip.style.top = '0px';
    const rect = trigger.getBoundingClientRect();
    const vw = document.documentElement.clientWidth;
    const vh = document.documentElement.clientHeight;
    const tw = Math.min(tip.offsetWidth, vw - 16);
    const th = tip.offsetHeight;
    let left = rect.left + rect.width / 2 - tw / 2;
    left = Math.max(8, Math.min(left, vw - tw - 8));
    let top = rect.top - th - 8;
    if (top < 8) top = rect.bottom + 8;
    top = Math.max(8, Math.min(top, vh - th - 8));
    tip.style.left = left + 'px';
    tip.style.top = top + 'px';
  }

  function showTip(trigger) {
    const tip = trigger.querySelector(':scope > .icon-tooltip');
    if (!tip) return;
    if (activeTip && activeTip.tip !== tip) hideTip();
    activeTip = { trigger, tip };
    tip.classList.add('is-floating');
    requestAnimationFrame(() => placeTip(trigger, tip));
  }

  function hideTip() {
    if (!activeTip) return;
    activeTip.tip.classList.remove('is-floating');
    activeTip.tip.style.left = '';
    activeTip.tip.style.top = '';
    activeTip = null;
  }

  function tooltipTrigger(node) {
    return node instanceof Element ? node.closest('.icon-btn,.chip,.stage-inline,.platform-icon') : null;
  }


  // Phase 15 — optional explanation help, one shared behavior.
  let activeHelp = null;

  function updateTriggerLabel(trigger) {
    const vi = trigger.dataset.topicVi || '';
    const en = trigger.dataset.topicEn || vi;
    trigger.setAttribute('aria-label', locale() === 'en' ? ('Details: ' + en) : ('Chi tiết: ' + vi));
  }

  function focusPanel(panel, mode) {
    if (mode === 'drawer') {
      panel.querySelector('.info-drawer-close')?.focus();
      return;
    }
    const first = panel.querySelector('a[href],button:not([data-help-close]),input,select,textarea');
    (first || panel).focus({ preventScroll: true });
  }

  function placePopover(panel, trigger) {
    if (!panel.open || !trigger.isConnected) return;
    panel.style.left = '0px';
    panel.style.top = '0px';
    const r = trigger.getBoundingClientRect();
    const vw = document.documentElement.clientWidth;
    const vh = document.documentElement.clientHeight;
    const pw = Math.min(panel.offsetWidth, vw - 16);
    const ph = panel.offsetHeight;
    let left = r.left + r.width / 2 - pw / 2;
    left = Math.max(8, Math.min(left, vw - pw - 8));
    let top = r.bottom + 8;
    if (top + ph > vh - 8) {
      const above = r.top - ph - 8;
      top = above >= 8 && above + ph <= vh - 8
        ? above
        : Math.max(8, Math.min(top, vh - ph - 8));
    }
    panel.style.left = left + 'px';
    panel.style.top = top + 'px';
  }

  function syncTriggerState(panel, open) {
    document.querySelectorAll('.info-trigger').forEach(t => {
      if (t.getAttribute('aria-controls') === panel.id) t.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
  }

  function closeHelp(refocus = true) {
    if (!activeHelp) return;
    const { panel, trigger, mode } = activeHelp;
    activeHelp = null;
    syncTriggerState(panel, false);
    document.body.classList.remove('info-modal-open');
    if (panel.open) {
      if (mode === 'drawer' && typeof panel.close === 'function') panel.close();
      else if (typeof panel.close === 'function') panel.close();
    }
    hideTip();
    if (refocus && trigger && trigger.isConnected) trigger.focus({ preventScroll: true });
  }

  function openPanel(trigger) {
    const panel = document.getElementById(trigger.dataset.infoPanel || trigger.getAttribute('aria-controls'));
    if (!panel) return;
    if (activeHelp && activeHelp.panel === panel) {
      closeHelp(true);
      return;
    }
    if (activeHelp) closeHelp(false);
    const mode = panel.classList.contains('info-drawer') ? 'drawer' : 'popover';
    if (trigger.offsetParent !== null) {
      const tr = trigger.getBoundingClientRect();
      const vh = document.documentElement.clientHeight;
      if (tr.top < 8 || tr.bottom > vh - 8) {
        const target = window.scrollY + tr.top - Math.max(16, (vh - tr.height) / 2);
        window.scrollTo(0, Math.max(0, target));
      }
    }
    hideTip();
    activeHelp = { panel, trigger, mode };
    syncTriggerState(panel, true);
    if (mode === 'drawer') {
      document.body.classList.add('info-modal-open');
      if (typeof panel.showModal === 'function') panel.showModal();
      else panel.setAttribute('open', '');
    } else {
      if (typeof panel.show === 'function') panel.show();
      else panel.setAttribute('open', '');
      requestAnimationFrame(() => placePopover(panel, trigger));
    }
    requestAnimationFrame(() => focusPanel(panel, mode));
  }

  function updateHelpLabels() {
    document.querySelectorAll('.info-trigger').forEach(updateTriggerLabel);
  }

  function syncAll() {
    document.documentElement.lang = locale();
    localizeAttributes();
    localizeOptions();
    localizeDescriptions();
    enhanceTables();
    layoutTables();
    updateRowTitles();
    localizeCellTitles();
    updateHelpLabels();
  }

  document.addEventListener('click', event => {
    const trigger = event.target instanceof Element ? event.target.closest('.info-trigger') : null;
    if (trigger) {
      event.preventDefault();
      openPanel(trigger);
      return;
    }
    if (event.target instanceof Element && event.target.closest('[data-help-close]')) {
      if (activeHelp) closeHelp(true);
      return;
    }
    if (activeHelp && !activeHelp.panel.contains(event.target) && !activeHelp.trigger.contains(event.target)) {
      closeHelp(true);
    }
  });
  document.addEventListener('keydown', event => {
    const drawer = document.querySelector('dialog.info-drawer[open]');
    if (drawer && event.key === 'Tab') {
      const focusables = [...drawer.querySelectorAll('a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])')]
        .filter(el => el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden');
      if (!focusables.length) {
        event.preventDefault();
        drawer.focus({ preventScroll: true });
        return;
      }
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      if (event.shiftKey && (document.activeElement === first || !drawer.contains(document.activeElement))) {
        event.preventDefault();
        last.focus({ preventScroll: true });
      } else if (!event.shiftKey && (document.activeElement === last || !drawer.contains(document.activeElement))) {
        event.preventDefault();
        first.focus({ preventScroll: true });
      }
    }
    if (event.key === 'Escape' && activeHelp) {
      event.preventDefault();
      closeHelp(true);
    }
  });
  window.addEventListener('scroll', () => {
    if (activeHelp && activeHelp.mode === 'popover') placePopover(activeHelp.panel, activeHelp.trigger);
  }, true);
  window.addEventListener('resize', () => {
    if (activeHelp && activeHelp.mode === 'popover') placePopover(activeHelp.panel, activeHelp.trigger);
  });
  window.addEventListener('hashchange', () => closeHelp(false));

  document.addEventListener('mouseover', event => {
    const trigger = tooltipTrigger(event.target);
    if (!trigger || trigger.contains(event.relatedTarget)) return;
    showTip(trigger);
  });
  document.addEventListener('mouseout', event => {
    const trigger = tooltipTrigger(event.target);
    if (!trigger || trigger.contains(event.relatedTarget)) return;
    hideTip();
  });
  document.addEventListener('focusin', event => {
    const trigger = tooltipTrigger(event.target);
    if (trigger) showTip(trigger);
  });
  document.addEventListener('focusout', event => {
    const trigger = tooltipTrigger(event.target);
    if (!trigger || trigger.contains(event.relatedTarget)) return;
    hideTip();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') hideTip();
  });
  window.addEventListener('scroll', () => {
    if (activeTip) placeTip(activeTip.trigger, activeTip.tip);
  }, true);
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    if (activeTip) hideTip();
    resizeTimer = setTimeout(() => { layoutTables(); updateRowTitles(); }, 120);
  });
  document.getElementById('locale')?.addEventListener('change', syncAll);
  document.addEventListener('toggle', () => { layoutTables(); updateRowTitles(); }, true);

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => { syncAll(); });
  } else {
    syncAll();
  }
})();
