/* Review chrome for the mockups (not product code).
   - Injects the icon sprite: use <svg aria-hidden="true"><use href="#i-NAME"/></svg>.
   - Adds the review bar: theme (dark/light), language (vi/en), frame width (360/768/1280/all).
   - Choices persist in the URL hash (?theme=&lang=&w=) so a link reproduces the view. */
(function () {
  const ICONS = {
    moon: '<path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5Z"/>',
    lamp: '<path d="M9 3h6l1 4H8l1-4Z"/><path d="M8 7h8v5a4 4 0 0 1-8 0V7Z"/><path d="M12 16v3M9 21h6"/>',
    home: '<path d="M4 11 12 4l8 7v9h-5v-6H9v6H4v-9Z"/>',
    series: '<path d="M5 4h4v16H5zM10 4h4v16h-4zM15.5 5.2l3.8-1 3.5 15.3-3.8 1z"/>',
    episodes: '<path d="M4 6h16M4 12h16M4 18h10"/>',
    channels: '<rect x="3" y="6" width="18" height="13" rx="2"/><path d="M8 3l4 3 4-3"/>',
    billing: '<rect x="3" y="6" width="18" height="13" rx="2"/><path d="M3 10h18M7 15h4"/>',
    account: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
    admin: '<path d="M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6l-8-3Z"/>',
    check: '<path d="m5 12 5 5 9-10"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    alert: '<path d="M12 3 2 20h20L12 3Z"/><path d="M12 10v4M12 17h.01"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5h.01"/>',
    play: '<path d="M7 4v16l13-8L7 4Z"/>',
    pause: '<path d="M7 4h4v16H7zM13 4h4v16h-4z"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3"/>',
    unknown: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14M12 17h.01"/>',
    close: '<path d="M6 6l12 12M18 6 6 18"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    up: '<path d="m6 15 6-6 6 6"/>',
    down: '<path d="m6 9 6 6 6-6"/>',
    drag: '<path d="M9 6h.01M15 6h.01M9 12h.01M15 12h.01M9 18h.01M15 18h.01"/>',
    trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
    edit: '<path d="M4 20h4L19 9l-4-4L4 16v4Z"/>',
    menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
    back: '<path d="m15 18-6-6 6-6"/>',
    next: '<path d="m9 6 6 6-6 6"/>',
    external: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
    download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
    upload: '<path d="M12 20V9M7 14l5-5 5 5M5 4h14"/>',
    link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
    filter: '<path d="M4 5h16l-6 8v6l-4-2v-4L4 5Z"/>',
    lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M17 6l3 3"/>',
    shield: '<path d="M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6l-8-3Z"/><path d="m9 12 2 2 4-4"/>',
    eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
    refresh: '<path d="M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4"/>',
    sparkle: '<path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5 18 18M6 18l2.5-2.5M15.5 8.5 18 6"/>',
    youtube: '<rect x="2.5" y="5.5" width="19" height="13" rx="4"/><path d="m10 9 5 3-5 3V9Z"/>',
    tiktok: '<path d="M14 4v10.5a3.5 3.5 0 1 1-3.5-3.5M14 4c.5 2.4 2.2 4 4.5 4.2"/>',
    calendar: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>',
    mail: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="m3 7 9 6 9-6"/>',
    bell: '<path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4l2-2ZM10 21h4"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.7 2.5 15.3 0 18M12 3c-2.5 2.7-2.5 15.3 0 18"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5 19 19M5 19l1.5-1.5M17.5 6.5 19 5"/>',
    mic: '<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3"/>',
    image: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="m21 16-5-5-9 9"/>',
    film: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 4v16M17 4v16M3 9h4M3 15h4M17 9h4M17 15h4"/>',
    book: '<path d="M4 5a2 2 0 0 1 2-2h13v16H6a2 2 0 0 0-2 2V5Z"/><path d="M19 19v2H6"/>',
    flag: '<path d="M5 21V4M5 4h11l-2 4 2 4H5"/>',
    history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 2"/>',
    qr: '<path d="M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h2v2h-2zM18 14h2v2h-2zM14 18h2v2h-2zM18 18h2v2h-2z"/>',
    server: '<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>',
    chart: '<path d="M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-7"/>',
    user: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
    logout: '<path d="M15 4h4v16h-4M10 8l-4 4 4 4M6 12h11"/>'
  };
  const sprite = '<svg xmlns="http://www.w3.org/2000/svg" style="display:none" aria-hidden="true">' +
    Object.entries(ICONS).map(([k, v]) =>
      `<symbol id="i-${k}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">${v}</symbol>`
    ).join('') + '</svg>';
  document.body.insertAdjacentHTML('afterbegin', sprite);

  const params = new URLSearchParams(location.hash.slice(1));
  const state = {
    theme: params.get('theme') || 'dark',
    lang: params.get('lang') || 'vi',
    w: params.get('w') || '1280'
  };
  const root = document.documentElement;

  const backHref = document.body.dataset.back;
  const title = document.body.dataset.title || document.title;
  const bar = document.createElement('div');
  bar.className = 'rv-bar';
  bar.setAttribute('role', 'toolbar');
  bar.setAttribute('aria-label', 'Review controls');
  bar.innerHTML =
    (backHref ? `<a href="${backHref}">← Index</a>` : '') +
    `<span class="rv-bar__title">${title}</span>` +
    seg('theme', [['dark', 'Dark'], ['light', 'Light']]) +
    seg('lang', [['vi', 'Tiếng Việt'], ['en', 'English']]) +
    (document.querySelector('.frame__view') ? seg('w', [['360', '360'], ['768', '768'], ['1280', '1280'], ['all', 'All 3']]) : '');
  document.body.insertAdjacentElement('afterbegin', bar);

  function seg(key, opts) {
    return `<span class="rv-seg" data-key="${key}">` +
      opts.map(([v, l]) => `<button type="button" data-v="${v}">${l}</button>`).join('') + '</span>';
  }

  bar.addEventListener('click', (e) => {
    const b = e.target.closest('button[data-v]');
    if (!b) return;
    state[b.parentElement.dataset.key] = b.dataset.v;
    apply();
  });

  const originals = new Map();
  function apply() {
    root.setAttribute('data-theme', state.theme);
    root.setAttribute('data-lang', state.lang);
    root.setAttribute('lang', state.lang);
    document.body.dataset.rvWidth = state.w;
    bar.querySelectorAll('.rv-seg').forEach((s) => {
      s.querySelectorAll('button').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.v === state[s.dataset.key])));
    });
    document.querySelectorAll('.frame__view').forEach((view) => {
      if (!originals.has(view)) originals.set(view, view.innerHTML);
      if (state.w === 'all') {
        if (!view.dataset.cloned) {
          const html = originals.get(view);
          view.innerHTML = ['360', '768', '1280'].map((w) => `<div class="rv-clone" style="width:${w}px">${html}</div>`).join('');
          view.dataset.cloned = '1';
        }
        view.style.removeProperty('--rv-width');
      } else {
        if (view.dataset.cloned) { view.innerHTML = originals.get(view); delete view.dataset.cloned; }
        view.style.setProperty('--rv-width', state.w + 'px');
      }
    });
    const p = new URLSearchParams(state);
    history.replaceState(null, '', '#' + p.toString());
  }
  apply();
})();
