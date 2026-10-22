(() => {
  'use strict';

  const BASE = [
    {vi:'Thị trấn cổ yên ắng sau nửa đêm. Một ngọn đèn lồng duy nhất hắt sáng trên cây cầu đá.',en:'The old town is quiet after midnight, and a single lantern glows above the stone bridge.',kvi:['đêm','cầu đá'],ken:['night','stone bridge']},
    {vi:'Những con phố hẹp uốn lượn qua các cánh cửa gỗ. Đá lát đường vẫn còn giữ hơi ấm của ban ngày.',en:'Narrow streets curve past wooden doors, and the cobblestones still hold the warmth of the day.',kvi:['phố hẹp','cửa gỗ'],ken:['narrow street','wooden door']},
    {vi:'Một con mèo xám ngồi bên bệ cửa sổ, đôi mắt vàng điềm tĩnh dõi theo quảng trường vắng.',en:'A gray cat sits on a windowsill and watches the empty square with calm yellow eyes.',kvi:['mèo xám','quảng trường'],ken:['gray cat','empty square']},
    {vi:'Đâu đó gần dòng sông, chiếc chuông trên tháp đồng hồ ngân một tiếng. Âm thanh tan dần trên những mái nhà.',en:'Somewhere near the river, a bell in the clock tower rings once and the sound fades over the rooftops.',kvi:['chuông'],ken:['bell']},
    {vi:'Người bộ hành đi ngang tiệm bánh đã đóng cửa. Hương bánh mì mới thoảng qua một ô cửa sổ hé mở.',en:'The walker passes a closed bakery, where the smell of fresh bread drifts through a small open window.',kvi:['tiệm bánh','cửa sổ'],ken:['bakery','open window']},
    {vi:'Cuối con ngõ, trăng lên trên những ngọn đồi, nhuộm bạc các ống khói.',en:'At the end of the lane, the moon rises over the hills and paints the chimneys silver.',kvi:['trăng','ống khói'],ken:['moon','chimney']},
    {vi:'Cửa hiệu bánh mở hé, một vệt sáng vàng trượt dài trên nền đá ướt.',en:'The bakery door is ajar, and a strip of yellow light slides across the wet stone floor.',kvi:['ánh sáng','nền đá'],ken:['lamplight','wet stone']},
    {vi:'Người đàn bà mang chiếc ô khô bước qua cầu mà không để lại dấu chân.',en:'A woman carrying a dry umbrella crosses the bridge without leaving footprints.',kvi:['ô khô','dấu chân'],ken:['dry umbrella','footprints']},
    {vi:'Tiếng chuông thứ mười ba vang lên từ phía bến sông, nơi không có tháp nào.',en:'A thirteenth bell rings from the river landing, where no tower stands.',kvi:['chuông mười ba','bến sông'],ken:['thirteenth bell','river landing']},
    {vi:'Bến đò cũ buộc một chiếc thuyền không người. Nước đen phản chiếu những ô cửa sáng.',en:'An empty boat is tied at the old landing, and black water mirrors the lit windows.',kvi:['bến đò','nước đen'],ken:['ferry landing','black water']},
    {vi:'Đứa trẻ đứng bên cột đèn, tay cầm một mảnh giấy gấp tư.',en:'A child stands beside the lamppost, holding a piece of paper folded in four.',kvi:['đứa trẻ','mảnh giấy'],ken:['child','folded paper']},
    {vi:'Bức thư không tem nằm dưới cánh cửa hiệu, nét chữ run như viết trong gió.',en:'An unstamped letter lies under the shop door, its handwriting shaky as if written in the wind.',kvi:['bức thư'],ken:['letter']},
    {vi:'Căn gác trên cùng vẫn sáng đèn. Một bóng người đứng quay lưng, bất động rất lâu.',en:'The top-floor room is still lit, and a figure stands with its back turned, motionless for a long time.',kvi:['căn gác','bóng người'],ken:['attic','silhouette']},
    {vi:'Mùi hồi và quế trôi ra từ căn bếp khóa kín, mặc dù chủ nhà đã rời đi từ tuần trước.',en:'The smell of anise and cinnamon drifts from a locked kitchen, though the owner left a week ago.',kvi:['hồi','quế'],ken:['anise','cinnamon']},
    {vi:'Con chó vàng nằm chắn giữa ngõ, tai vểnh về phía tiếng chuông vừa tắt.',en:'A yellow dog lies across the lane, ears tipped toward the bell that has just stopped.',kvi:['chó vàng','tiếng chuông'],ken:['yellow dog','bell']},
    {vi:'Tấm gương trong hiệu đồ cổ phản chiếu con phố, nhưng không có người bộ hành.',en:'A mirror in the antique shop reflects the street, but the walker is missing.',kvi:['gương','hiệu đồ cổ'],ken:['mirror','antique shop']},
    {vi:'Người gác đêm đi qua quảng trường, chìa khóa kêu khẽ trong túi áo.',en:'The night watchman crosses the square, keys ticking softly in his coat pocket.',kvi:['người gác đêm','chìa khóa'],ken:['night watchman','keys']},
    {vi:'Chiếc xe đạp dựng trước cổng nhà thờ, yên xe còn ấm dù trời đã lạnh.',en:'A bicycle leans against the church gate, its saddle still warm although the night is cold.',kvi:['xe đạp','cổng nhà thờ'],ken:['bicycle','church gate']},
    {vi:'Dòng chữ trên tường chỉ hiện khi có đèn chiếu ngang: “Đừng quay lại trước khi chuông ngừng.”',en:'Writing on the wall appears only in side light: “Do not turn back before the bells stop.”',kvi:['dòng chữ','bức tường'],ken:['writing','wall']},
    {vi:'Bình minh lên muộn. Sương vẫn bám trên cầu khi tiếng chuông cuối cùng tan vào tiếng chợ.',en:'Dawn arrives late, and mist still clings to the bridge as the last bell dissolves into the market noise.',kvi:['bình minh','sương'],ken:['dawn','mist']}
  ];

  const CYCLES = [
    {vi:'',en:''},
    {vi:'Về khuya, ',en:'Late that night, '},
    {vi:'Sau cơn mưa, ',en:'After the rain, '},
    {vi:'Gần sáng, ',en:'Near dawn, '}
  ];

  const FLAGS = {
    40:[5,12,19,26,34,40],
    80:[5,12,19,26,34,40,47,54,61,68,74,80]
  };
  const NOTES = {40:[5,17,29],80:[5,17,29,45,57,69]};
  const ISSUES = {40:[8,22],80:[8,22,42,64]};

  const notes = {
    5:{vi:'Tiệm bánh đã đóng nhưng vẫn có bánh mới. Có thể làm rõ mẻ bánh cho sáng hôm sau.',en:'The bakery is closed but fresh bread is present. Consider clarifying that it is the next morning’s batch.'},
    17:{vi:'Nhịp câu dài ở đây làm chậm cảnh. Cân nhắc tách thành hai câu ngắn.',en:'The long sentence slows the scene. Consider splitting it into two shorter sentences.'},
    29:{vi:'Từ “chuông” lặp ba lần trong một đoạn. Giữ một lần để tăng hiệu quả.',en:'The word “bell” appears three times in one paragraph. Keep one for impact.'},
    45:{vi:'Chi tiết thời tiết chưa khớp với cảnh trước. Kiểm tra lại mốc thời gian.',en:'The weather detail does not match the previous scene. Check the time marker.'},
    57:{vi:'Có thể thêm một giác quan khác ngoài thị giác để cảnh dày hơn.',en:'Consider adding a sense beyond sight to deepen the scene.'},
    69:{vi:'Câu kết của đoạn đã đủ mạnh; phần giải thích phía sau có thể bỏ.',en:'The paragraph already ends strongly; the explanation that follows could be removed.'}
  };
  const issues = {
    8:{vi:'Mốc thời gian chưa khớp: người bộ hành vừa qua cầu nhưng dấu chân đã khô.',en:'Timeline issue: the walker has just crossed the bridge, yet the footprints are already dry.'},
    22:{vi:'Mô-típ trùng với tập trước. Cần một chi tiết khác để phân biệt cảnh này.',en:'The motif repeats an earlier episode. Add a distinct detail to separate this scene.'},
    42:{vi:'Tên nhân vật đổi cách gọi giữa hai câu. Thống nhất một cách gọi.',en:'The character’s name changes form between sentences. Use one form consistently.'},
    64:{vi:'Lời thoại không khớp với giọng kể ở đoạn trước. Kiểm tra lại ngôi kể.',en:'The dialogue does not match the previous narration. Check the point of view.'}
  };
  const flagNotes = {
    12:{vi:'Cờ nội dung: bức thư không tem có thể gợi ý một tuyến truyện chưa được giải thích.',en:'Content flag: the unstamped letter may imply an unexplained story thread.'},
    19:{vi:'Cờ liên tục: dòng chữ trên tường chưa được nhắc lại ở đoạn kết.',en:'Continuity flag: the writing on the wall is not revisited in the ending.'},
    26:{vi:'Cờ nhịp: chuỗi hành động dồn quá nhanh so với các cảnh trước.',en:'Pacing flag: the action sequence moves faster than the earlier scenes.'},
    34:{vi:'Cờ an toàn: hình ảnh người đứng bất động có thể gây lo ngại; cân nhắc giảm cường độ.',en:'Safety flag: the motionless figure may be unsettling; consider softening it.'},
    40:{vi:'Cờ cấu trúc: đoạn này mở một tuyến mới ở cuối chương.',en:'Structure flag: this paragraph opens a new thread at the end of the chapter.'},
    47:{vi:'Cờ nội dung: chi tiết chưa được thiết lập trước đó.',en:'Content flag: the detail has not been established earlier.'},
    54:{vi:'Cờ liên tục: địa điểm đổi mà không có chuyển cảnh.',en:'Continuity flag: the location changes without a transition.'},
    61:{vi:'Cờ nhịp: hai đoạn liên tiếp cùng một nhịp điệu.',en:'Pacing flag: two consecutive paragraphs share the same rhythm.'},
    68:{vi:'Cờ an toàn: cần kiểm tra lại mô tả âm thanh đột ngột.',en:'Safety flag: recheck the sudden sound description.'},
    74:{vi:'Cờ cấu trúc: đoạn kết sớm hơn cao trào.',en:'Structure flag: the paragraph resolves earlier than the climax.'},
    80:{vi:'Cờ nội dung: câu cuối có thể tạo kỳ vọng chưa được trả lời trong chương.',en:'Content flag: the final line may set an expectation the chapter does not answer.'}
  };

  const svg = {
    scene:'<svg aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" viewBox="0 0 24 24"><path d="M4 19V7l8-3 8 3v12"/><path d="M8 19v-6h8v6"/></svg>',
    flag:'<svg aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" viewBox="0 0 24 24"><path d="M12 3 22 21H2L12 3Z"/><path d="M12 9v5M12 17v.1"/></svg>',
    note:'<svg aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" viewBox="0 0 24 24"><path d="M4 4h16v12H9l-5 4V4Z"/><path d="M8 8h8M8 12h5"/></svg>',
    issue:'<svg aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 7v6M12 17v.1"/></svg>',
    check:'<svg aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="2.2" viewBox="0 0 24 24"><path d="m5 12 4 4L19 6"/></svg>',
    search:'<svg aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/></svg>',
    close:'<svg aria-hidden="true" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" viewBox="0 0 24 24"><path d="m6 6 12 12M18 6 6 18"/></svg>'
  };

  const esc = s => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
  const lang = () => (document.getElementById('locale')?.value === 'en' ? 'en' : 'vi');
  const pair = (vi,en) => `<span class="vi" lang="vi">${esc(vi)}</span><span class="en" lang="en">${esc(en)}</span>`;
  const lowerFirst = s => s ? s.charAt(0).toLocaleLowerCase('vi') + s.slice(1) : s;
  const words = (s,n=8) => s.split(/\s+/).slice(0,n).join(' ') + (s.split(/\s+/).length>n?'…':'');

  function makeData(count){
    const flags = FLAGS[count] || FLAGS[40];
    const noteList = NOTES[count] || NOTES[40];
    const issueList = ISSUES[count] || ISSUES[40];
    return Array.from({length:count},(_,i) => {
      const n = i+1;
      const base = BASE[i % BASE.length];
      const cycle = Math.floor(i / BASE.length) % CYCLES.length;
      const prefix = CYCLES[cycle];
      const vi = cycle ? prefix.vi + lowerFirst(base.vi) : base.vi;
      const en = cycle ? prefix.en + lowerFirst(base.en) : base.en;
      return {
        id:n, vi, en, kvi:base.kvi, ken:base.ken,
        flag:flags.includes(n), note:noteList.includes(n), issue:issueList.includes(n),
        read:i < Math.round(count*(count===80?0.34:0.3)),
        annotation: notes[n] || issues[n] || flagNotes[n] || null
      };
    });
  }

  const iconBtn = (labelVi,labelEn,cls='') => `<button type="button" class="btn ghost btn-sm icon-btn ${cls}" aria-label="${esc(labelVi)}" data-aria-vi="${esc(labelVi)}" data-aria-en="${esc(labelEn)}">${svg.close}<span class="icon-tooltip" role="tooltip">${pair(labelVi,labelEn)}</span></button>`;

  function indexHTML(count){
    return `<aside class="sr-index" aria-label="${esc('Mục lục đoạn')}" data-aria-vi="Mục lục đoạn" data-aria-en="Segment index">
      <div class="sr-index-head">
        <div class="sr-index-title">${pair('Mục lục','Index')} <span class="sr-index-count" data-sr-total>${count}</span></div>
        ${iconBtn('Đóng mục lục','Close index','sr-index-close')}
      </div>
      <div class="sr-index-tools">
        <div class="control-search">
          ${svg.search}
          <input type="search" data-sr-search data-ph-vi="Tìm trong kịch bản" data-ph-en="Search script" placeholder="Tìm trong kịch bản" aria-label="Tìm trong kịch bản" autocomplete="off"/>
        </div>
        <div class="sr-filter">
          <div class="segmented" role="group" aria-label="Lọc mục lục" data-aria-vi="Lọc mục lục" data-aria-en="Filter index">
            <label><input type="radio" name="sr-filter-${count}-main" value="all" checked/><span>${pair('Tất cả','All')}</span></label>
            <label><input type="radio" name="sr-filter-${count}-main" value="flag"/><span>${pair('Có cờ','Flagged')}</span></label>
            <label><input type="radio" name="sr-filter-${count}-main" value="note"/><span>${pair('Có ghi chú','Notes')}</span></label>
            <label><input type="radio" name="sr-filter-${count}-main" value="unread"/><span>${pair('Chưa đọc','Unread')}</span></label>
          </div>
        </div>
      </div>
      <div class="sr-index-progress"><span>${pair('Đã đọc','Read')} <strong data-sr-read>0/${count}</strong></span><span data-sr-unread></span></div>
      <div class="sr-quick" data-sr-quick><span>${pair('Đang lọc: Vấn đề','Filtering: Issues')}</span><button type="button" class="textlink" data-sr-quick-clear>${pair('Bỏ lọc','Clear')}</button></div>
      <div class="sr-index-cols"><span>#</span><span>${pair('Đoạn','Segment')}</span><span>${pair('Dấu','Marks')}</span></div>
      <div class="sr-index-list" data-sr-list role="listbox" aria-label="Danh sách đoạn" data-aria-vi="Danh sách đoạn" data-aria-en="Segment list"></div>
      <div class="sr-index-empty" data-sr-empty></div>
      <div class="sr-index-foot">
        <button type="button" class="textlink" data-sr-all-read>${pair('Đánh dấu tất cả đã đọc','Mark all read')}</button>
        <span class="sr-order-hint" data-sr-order-hint hidden>${pair('Kéo hoặc Alt+↑/↓ để đổi thứ tự','Drag or Alt+↑/↓ to reorder')}</span>
      </div>
    </aside>`;
  }

  function readerHTML(count, mode){
    return `<section class="sr-reader" aria-label="${esc(mode==='editor'?'Khu biên tập':'Khu đọc')}" data-aria-vi="${esc(mode==='editor'?'Khu biên tập':'Khu đọc')}" data-aria-en="${esc(mode==='editor'?'Editing pane':'Reading pane')}">
      <header class="sr-reader-head">
        <div>
          <p class="eyebrow">${pair('CHUYỆN KỂ SAU NỬA ĐÊM · BẢN V3','STORIES AFTER MIDNIGHT · V3')}</p>
          <h2>${pair('Thị trấn sau nửa đêm','The town after midnight')}</h2>
          <p class="sr-reader-sub" data-sr-sub></p>
        </div>
        <div class="sr-reader-tools">
          <button type="button" class="btn ghost btn-sm sr-index-toggle" data-sr-index-open aria-expanded="false">
            ${pair('Mục lục','Index')} (<span data-sr-toggle-count>${count}</span>)
          </button>
          <span class="sr-kbd-hint"><kbd>J</kbd>/<kbd>K</kbd> ${pair('chuyển đoạn','move')} · <kbd>N</kbd> ${pair('đoạn có cờ','next flagged')}</span>
        </div>
      </header>
      <div class="sr-doc" data-sr-doc tabindex="0" aria-label="Nội dung kịch bản" data-aria-vi="Nội dung kịch bản" data-aria-en="Script content"><div class="sr-doc-inner" data-sr-doc-inner></div></div>
      <div class="sr-loadmore" data-sr-loadmore>
        <button type="button" class="btn ghost btn-sm" data-sr-more></button>
        <span class="sr-loaded" data-sr-loaded></span>
      </div>
      <div class="sr-hint-line">${pair('J/K chuyển đoạn · N đoạn có cờ · Nhấn vào mục lục để nhảy','J/K move · N next flagged · Use the index to jump')}</div>
    </section>`;
  }

  function countsHTML(){
    return `<div class="sr-counts">
      <button type="button" class="sr-count" data-sr-count="flag"><strong data-sr-count-value="flag">0</strong><span>${pair('Cờ','Flags')}</span></button>
      <button type="button" class="sr-count" data-sr-count="note"><strong data-sr-count-value="note">0</strong><span>${pair('Ghi chú','Notes')}</span></button>
      <button type="button" class="sr-count" data-sr-count="issue"><strong data-sr-count-value="issue">0</strong><span>${pair('Vấn đề','Issues')}</span></button>
    </div>`;
  }

  function sideHTML(mode){
    if (mode==='editor') return `<aside class="review-rail sr-side" aria-label="Chỉnh sửa &amp; quyết định" data-aria-vi="Chỉnh sửa &amp; quyết định" data-aria-en="Editing &amp; decision">
      ${iconBtn('Đóng bảng quyết định','Close decision panel','sr-side-close')}
      <section class="panel stack">
        <h2>${pair('Bản v3 · Có thay đổi chưa lưu','v3 · Unsaved changes')}</h2>
        <span class="chip warn">${svg.issue}${pair('Đang chỉnh sửa','Editing')}</span>
        <p>${pair('Không tự lưu. Nhấn “Lưu phiên bản” để tạo bản mới.','Autosave is off. Save explicitly to create a new version.')}</p>
        <div class="decision-choices">
          <a class="btn primary" href="#saving">${pair('Lưu phiên bản','Save version')}</a>
          <a class="btn" href="#dirty-guard">${pair('Rời trang','Leave page')}</a>
        </div>
      </section>
      <section class="panel stack">
        <h2>${pair('Ghi chú &amp; cờ','Notes &amp; flags')}</h2>
        ${countsHTML()}
        <p class="small muted">${pair('Mở đoạn liên quan trước khi lưu hoặc duyệt.','Open linked segments before saving or approving.')}</p>
        <a class="textlink" href="#segment-5">${pair('Đến đoạn cần xem','Go to flagged segment')}</a>
      </section>
    </aside>`;
    return `<aside class="review-rail sr-side" aria-label="${esc('Quyết định')}" data-aria-vi="Quyết định" data-aria-en="Decision">
      ${iconBtn('Đóng bảng quyết định','Close decision panel','sr-side-close')}
      <section class="panel stack decision-panel">
        <h2>${pair('Quyết định của bạn','Your decision')}</h2>
        <span class="chip warn">${svg.issue}${pair('Chờ duyệt kịch bản','Story review')}</span>
        <p>${pair('Bạn sẽ duyệt bản V3 đã lưu. Các cờ và ghi chú không chặn quyết định.','You are approving saved V3. Flags and notes do not block this decision.')}</p>
        <div class="decision-choices">
          <a class="btn primary" href="#approve">${pair('Duyệt kịch bản','Approve story')}</a>
          <a class="btn" href="#changes">${pair('Yêu cầu chỉnh sửa','Request changes')}</a>
          <a class="btn ghost" href="#reject">${pair('Từ chối','Reject')}</a>
        </div>
      </section>
      <section class="panel stack">
        <h2>${pair('Chất lượng &amp; an toàn','Quality &amp; safety')}</h2>
        ${countsHTML()}
        <p class="small muted">${pair('Cờ và ghi chú không chặn duyệt; hãy mở đoạn liên quan trước khi quyết định.','Flags and notes do not block approval; open the linked segments before deciding.')}</p>
        <a class="textlink" href="#segment-5">${pair('Đến đoạn cần xem','Go to flagged segment')}</a>
        <a class="textlink" href="#blocked">${pair('Xem trường hợp bị chặn','View a blocking case')}</a>
      </section>
    </aside>`;
  }

  function barHTML(mode){
    const primary = mode==='editor'
      ? `<a class="btn primary" href="#saving">${pair('Lưu phiên bản','Save version')}</a>`
      : `<a class="btn primary" href="#approve">${pair('Duyệt','Approve')}</a>`;
    return `<div class="sr-decision-bar">
      <div class="sr-decision-summary"><strong>V3</strong><span data-sr-bar-count></span></div>
      <button type="button" class="btn ghost btn-sm" data-sr-side-open aria-expanded="false">${pair('Quyết định','Decision')}</button>
      ${primary}
    </div>`;
  }

  function ensureShell(root){
    const count = +(root.dataset.count || 40);
    const mode = root.dataset.mode || 'review';
    if (!root.querySelector('.sr-index')) root.insertAdjacentHTML('afterbegin', indexHTML(count));
    if (!root.querySelector('.sr-reader')) root.querySelector('.sr-index').insertAdjacentHTML('afterend', readerHTML(count, mode));
    if (!root.querySelector('.sr-side')) root.insertAdjacentHTML('beforeend', sideHTML(mode));
    root.querySelector('.sr-side').classList.add('sr-side');
    if (!root.querySelector('.sr-decision-bar')) root.insertAdjacentHTML('beforeend', barHTML(mode));
    if (!root.querySelector('.sr-backdrop')) root.insertAdjacentHTML('beforeend', `<button type="button" class="sr-backdrop" aria-label="Đóng" data-sr-backdrop></button>`);
    if (mode==='editor') {
      root.classList.add('is-editor');
      const hint = root.querySelector('[data-sr-order-hint]'); if (hint) hint.hidden = false;
    }
  }

  function filterMatch(seg, st, q){
    if (q) {
      const hay = [seg.vi, seg.en, ...seg.kvi, ...seg.ken].join(' ').toLocaleLowerCase('vi');
      if (!hay.includes(q)) return false;
    }
    if (st.quick === 'issue') return seg.issue;
    if (st.filter === 'flag') return seg.flag;
    if (st.filter === 'note') return seg.note;
    if (st.filter === 'unread') return !seg.read;
    return true;
  }

  function mount(root, key){
    ensureShell(root);
    const mode = root.dataset.mode || 'review';
    const count = +(root.dataset.count || 40);
    const data = makeData(count);
    const st = {
      root, key, mode, count, data, current:1, loaded:Math.min(20,count),
      filter:'all', query:'', quick:'', dragging:null
    };
    const q = sel => root.querySelector(sel);
    const qa = sel => [...root.querySelectorAll(sel)];
    const list = q('[data-sr-list]');
    const empty = q('[data-sr-empty]');
    const doc = q('[data-sr-doc]');
    const inner = q('[data-sr-doc-inner]');
    const more = q('[data-sr-more]');
    const loadedEl = q('[data-sr-loaded]');
    const readEl = q('[data-sr-read]');
    const unreadEl = q('[data-sr-unread]');
    const allRead = q('[data-sr-all-read]');
    const search = q('[data-sr-search]');
    const quick = q('[data-sr-quick]');
    const orderHint = q('[data-sr-order-hint]');
    const barCount = q('[data-sr-bar-count]');

    const readCount = () => st.data.filter(s=>s.read).length;
    const counts = () => ({
      flag: st.data.filter(s=>s.flag).length,
      note: st.data.filter(s=>s.note).length,
      issue: st.data.filter(s=>s.issue).length
    });
    const active = () => st.data.find(s=>s.id===st.current) || st.data[0];
    const idxOf = id => st.data.findIndex(s=>s.id===id);

    function updateMeta(){
      const c = counts();
      qa('[data-sr-count-value]').forEach(el=>{ el.textContent = c[el.dataset.srCountValue] || 0; });
      const read = readCount();
      if (readEl) readEl.textContent = `${read}/${st.count}`;
      if (unreadEl) unreadEl.textContent = lang()==='en' ? `${st.count-read} unread` : `${st.count-read} chưa đọc`;
      if (barCount) barCount.textContent = lang()==='en' ? `${c.flag} flags · ${c.issue} issues` : `${c.flag} cờ · ${c.issue} vấn đề`;
      const sub = q('[data-sr-sub]');
      if (sub) sub.textContent = lang()==='en' ? `${st.count} segments · unapproved draft` : `${st.count} đoạn · bản nháp chưa duyệt`;
      if (allRead) {
        const done = read === st.count;
        allRead.disabled = done;
        allRead.innerHTML = done ? pair('Tất cả đã đọc','All read') : pair('Đánh dấu tất cả đã đọc','Mark all read');
      }
      const totalEl = q('[data-sr-total]'); if (totalEl) totalEl.textContent = st.count;
      const toggleCount = q('[data-sr-toggle-count]'); if (toggleCount) toggleCount.textContent = st.count;
    }

    function rowHTML(seg, pos){
      const shortVi = words(seg.vi), shortEn = words(seg.en);
      const l = lang();
      return `<button type="button" class="sr-row${seg.id===st.current?' is-current':''}" role="option" aria-selected="${seg.id===st.current}" data-id="${seg.id}" title="${esc(l==='en'?seg.en:seg.vi)}" draggable="${mode==='editor'}">
        <span class="sr-drag" aria-hidden="true">⠿</span>
        <span class="sr-num">${String(pos).padStart(2,'0')}</span>
        <span class="sr-first">${pair(shortVi,shortEn)}</span>
        <span class="sr-row-meta">
          <span class="sr-mark" title="${seg.kvi.length} ${lang()==='en'?'scenes':'cảnh'}">${svg.scene}${seg.kvi.length}</span>
          ${seg.flag?`<span class="sr-mark is-flag" title="${lang()==='en'?'Flag':'Có cờ'}">${svg.flag}1</span>`:''}
          ${seg.note?`<span class="sr-mark is-note" title="${lang()==='en'?'Note':'Ghi chú'}">${svg.note}1</span>`:''}
          ${seg.issue?`<span class="sr-mark is-issue" title="${lang()==='en'?'Issue':'Vấn đề'}">${svg.issue}1</span>`:''}
          <span class="sr-read${seg.read?' is-read':''}" title="${seg.read?(lang()==='en'?'Read':'Đã đọc'):(lang()==='en'?'Unread':'Chưa đọc')}">${seg.read?'✓':'·'}</span>
        </span>
      </button>`;
    }

    function renderIndex(){
      const l = lang();
      const qv = st.query.trim().toLocaleLowerCase('vi');
      let pos = 0;
      const html = [];
      for (const seg of st.data) {
        pos++;
        if (!filterMatch(seg, st, qv)) continue;
        html.push(rowHTML(seg,pos));
      }
      list.innerHTML = html.join('');
      const none = html.length===0;
      empty.classList.toggle('is-on', none);
      if (none) {
        const copy = st.query.trim()
          ? (l==='en'?'No segments match your search.':'Không tìm thấy đoạn nào phù hợp với tìm kiếm.')
          : st.filter==='unread' && readCount()===st.count
            ? (l==='en'?'All segments are read.':'Tất cả đoạn đã được đọc.')
            : (l==='en'?'No segments match this filter.':'Không có đoạn phù hợp với bộ lọc.');
        empty.innerHTML = `<p>${esc(copy)}</p>`;
      }
      quick.classList.toggle('is-on', st.quick==='issue');
      updateMeta();
    }

    function annotationHTML(seg){
      if (!seg.annotation) return '';
      const type = seg.issue ? 'issue' : '';
      const icon = seg.issue ? svg.issue : svg.note;
      const label = seg.issue ? pair('Vấn đề','Issue') : pair('Gợi ý · sở thích','Advisory · taste');
      return `<aside class="sr-annotation${type?' is-issue':''}">
        <span class="sr-ann-head">${icon}${label}</span>
        <p>${pair(seg.annotation.vi,seg.annotation.en)}</p>
        <a class="textlink" href="#changes">${pair('Chỉnh đoạn này','Edit this segment')}</a>
      </aside>`;
    }

    function segmentHTML(seg,pos){
      const l = lang();
      const markers = [
        seg.flag?`<span class="sr-gutter-mark is-flag" title="${l==='en'?'Flag':'Có cờ'}">${svg.flag}1</span>`:'',
        seg.note?`<span class="sr-gutter-mark is-note" title="${l==='en'?'Note':'Ghi chú'}">${svg.note}1</span>`:'',
        seg.issue?`<span class="sr-gutter-mark is-issue" title="${l==='en'?'Issue':'Vấn đề'}">${svg.issue}1</span>`:''
      ].join('');
      const kw = (l==='en'?seg.ken:seg.kvi).map(k=>`<span class="sr-kw">${svg.scene}${esc(k)}</span>`).join('');
      const id = mode==='editor' ? `editor-segment-${seg.id}` : (root.dataset.ws==='stress'?`stress-segment-${seg.id}`:`segment-${seg.id}`);
      const text = mode==='editor'
        ? `<textarea class="sr-edit-text" data-edit-id="${seg.id}" aria-label="${esc((l==='en'?'Paragraph ':'Đoạn ')+pos)}">${esc(l==='en'?seg.en:seg.vi)}</textarea>`
        : `<p>${pair(seg.vi,seg.en)}</p>`;
      return `<article class="sr-seg${seg.id===st.current?' is-current':''}" id="${id}" data-id="${seg.id}" data-pos="${pos}" tabindex="-1">
        <div class="sr-gutter"><span class="sr-seq">${String(pos).padStart(2,'0')}</span><span class="sr-gutter-marks">${markers}</span></div>
        <div class="sr-text">
          ${text}
          <div class="sr-edit-meta">
            <div class="sr-kws" aria-label="${esc(l==='en'?'Scene keywords':'Từ khoá cảnh')}">${kw}</div>
            ${mode==='editor'?`<a class="textlink" href="#scene-edit">${pair('Từ khoá cảnh','Scene keywords')}</a>`:''}
          </div>
          ${mode==='review'?annotationHTML(seg):''}
        </div>
      </article>`;
    }

    function renderDoc(){
      const top = doc.scrollTop;
      const maxPos = Math.min(st.loaded, st.count);
      inner.innerHTML = st.data.slice(0,maxPos).map((seg,i)=>segmentHTML(seg,i+1)).join('');
      more.innerHTML = maxPos < st.count
        ? pair(`Hiện thêm ${Math.min(20,st.count-maxPos)} đoạn`,`Show ${Math.min(20,st.count-maxPos)} more`)
        : pair('Đã tải toàn bộ','All segments loaded');
      more.disabled = maxPos >= st.count;
      loadedEl.textContent = lang()==='en' ? `${maxPos} of ${st.count} loaded` : `Đã tải ${maxPos}/${st.count}`;
      doc.scrollTop = top;
      const cur = inner.querySelector(`.sr-seg[data-id="${st.current}"]`);
      if (cur) cur.classList.add('is-current');
    }

    function setCurrent(id,{scroll=false,mark=true}={}){
      if (!st.data.some(s=>s.id===id)) return;
      st.current = id;
      qa('.sr-row').forEach(r=>{
        const on = +r.dataset.id===id;
        r.classList.toggle('is-current',on);
        r.setAttribute('aria-selected',String(on));
      });
      qa('.sr-seg').forEach(s=>s.classList.toggle('is-current',+s.dataset.id===id));
      if (mark) markRead(id);
      if (scroll) scrollToSegment(id);
    }

    function markRead(id){
      const seg = st.data.find(s=>s.id===id);
      if (!seg || seg.read) return;
      seg.read = true;
      const row = list.querySelector(`.sr-row[data-id="${id}"]`);
      if (row) {
        const r = row.querySelector('.sr-read');
        if (r) { r.classList.add('is-read'); r.textContent='✓'; }
      }
      updateMeta();
      if (st.filter==='unread') renderIndex();
    }

    function ensureLoaded(id){
      const pos = idxOf(id) + 1;
      if (pos > st.loaded) {
        st.loaded = Math.min(st.count, Math.ceil(pos/20)*20);
        renderDoc();
      }
    }

    function scrollToSegment(id){
      ensureLoaded(id);
      requestAnimationFrame(()=>{
        const el = inner.querySelector(`.sr-seg[data-id="${id}"]`);
        if (!el) return;
        doc.scrollTo({top:Math.max(0,el.offsetTop-8),behavior:'auto'});
        el.focus({preventScroll:true});
      });
    }

    function step(delta){
      const i = idxOf(st.current);
      const next = Math.min(st.count, Math.max(1, i+1+delta));
      if (next !== st.current) setCurrent(next,{scroll:true});
    }

    function nextFlagged(){
      let i = idxOf(st.current);
      for (let k=1;k<=st.count;k++) {
        const seg = st.data[(i+k)%st.count];
        if (seg.flag || seg.issue) { setCurrent(seg.id,{scroll:true}); return; }
      }
    }

    list.addEventListener('click', e=>{
      const row = e.target.closest('.sr-row');
      if (!row) return;
      setCurrent(+row.dataset.id,{scroll:true});
      if (window.matchMedia('(max-width:1000px)').matches) {
        root.classList.remove('is-index-open');
        q('[data-sr-index-open]').setAttribute('aria-expanded','false');
      }
    });

    list.addEventListener('keydown', e=>{
      if (mode!=='editor' || !e.altKey) return;
      if (e.key!=='ArrowUp' && e.key!=='ArrowDown') return;
      const row = e.target.closest('.sr-row');
      if (!row) return;
      e.preventDefault();
      const id = +row.dataset.id;
      const from = idxOf(id);
      const to = e.key==='ArrowUp' ? from-1 : from+1;
      if (to<0 || to>=st.data.length) return;
      move(from,to,id);
    });

    function move(from,to,id){
      const [item] = st.data.splice(from,1);
      st.data.splice(to,0,item);
      renderIndex(); renderDoc();
      const row = list.querySelector(`.sr-row[data-id="${id}"]`);
      if (row) row.focus();
      const live = q('[data-sr-order-hint]');
      if (live) live.textContent = lang()==='en' ? 'Draft order changed' : 'Đã đổi thứ tự bản nháp';
    }

    let dragId = null;
    list.addEventListener('dragstart', e=>{
      const row = e.target.closest('.sr-row');
      if (mode!=='editor' || !row) return;
      dragId = +row.dataset.id;
      row.classList.add('is-dragging');
      e.dataTransfer.effectAllowed = 'move';
      try { e.dataTransfer.setData('text/plain',String(dragId)); } catch (_) {}
    });
    list.addEventListener('dragover', e=>{
      if (mode!=='editor') return;
      const row = e.target.closest('.sr-row');
      if (!row || dragId===null) return;
      e.preventDefault();
      qa('.sr-row').forEach(r=>r.classList.remove('is-drop-target'));
      row.classList.add('is-drop-target');
    });
    list.addEventListener('drop', e=>{
      if (mode!=='editor') return;
      const row = e.target.closest('.sr-row');
      if (!row || dragId===null) return;
      e.preventDefault();
      const to = idxOf(+row.dataset.id);
      const from = idxOf(dragId);
      qa('.sr-row').forEach(r=>r.classList.remove('is-drop-target','is-dragging'));
      if (from>=0 && to>=0 && from!==to) move(from,to,dragId);
      dragId = null;
    });
    list.addEventListener('dragend', ()=>{ dragId=null; qa('.sr-row').forEach(r=>r.classList.remove('is-drop-target','is-dragging')); });

    let scrollTick = false;
    doc.addEventListener('scroll', ()=>{
      if (scrollTick) return;
      scrollTick = true;
      requestAnimationFrame(()=>{
        scrollTick = false;
        const segs = [...inner.querySelectorAll('.sr-seg')];
        let found = segs[0]?.dataset.id;
        for (const el of segs) {
          if (el.offsetTop - doc.scrollTop <= 40) found = el.dataset.id; else break;
        }
        if (found && +found !== st.current) setCurrent(+found,{mark:true});
      });
    });

    search.addEventListener('input', ()=>{
      st.query = search.value;
      st.quick = '';
      renderIndex();
    });
    qa('.sr-filter input').forEach(input=>{
      input.addEventListener('change', ()=>{
        if (!input.checked) return;
        st.filter = input.value;
        st.quick = '';
        renderIndex();
      });
    });
    q('[data-sr-quick-clear]')?.addEventListener('click', ()=>{
      st.quick=''; st.filter='all';
      const all = q('.sr-filter input[value="all"]'); if (all) all.checked = true;
      renderIndex();
    });
    qa('[data-sr-count]').forEach(btn=>{
      btn.addEventListener('click', ()=>{
        const kind = btn.dataset.srCount;
        st.query=''; if (search) search.value='';
        if (kind==='issue') { st.quick='issue'; st.filter='all'; }
        else { st.quick=''; st.filter=kind; }
        const radio = q(`.sr-filter input[value="${kind}"]`);
        if (radio) radio.checked = true;
        renderIndex();
        list.scrollTop = 0;
        if (window.matchMedia('(max-width:1000px)').matches) {
          root.classList.remove('is-side-open');
          q('[data-sr-side-open]').setAttribute('aria-expanded','false');
        }
      });
    });
    allRead?.addEventListener('click', ()=>{
      st.data.forEach(s=>s.read=true);
      renderIndex();
      if (st.filter==='unread') renderIndex();
    });
    more.addEventListener('click', ()=>{
      st.loaded = Math.min(st.count, st.loaded+20);
      renderDoc();
    });

    // Editor text edits
    inner.addEventListener('input', e=>{
      const ta = e.target.closest('[data-edit-id]');
      if (!ta) return;
      const seg = st.data.find(s=>s.id===+ta.dataset.editId);
      if (!seg) return;
      if (lang()==='en') seg.en = ta.value; else seg.vi = ta.value;
      const row = list.querySelector(`.sr-row[data-id="${seg.id}"]`);
      if (row) row.classList.add('is-dirty');
    });
    inner.addEventListener('click', e=>{
      const seg = e.target.closest('.sr-seg');
      if (!seg) return;
      setCurrent(+seg.dataset.id,{mark:true});
    });

    // Mobile index drawer and decision sheet
    const indexToggle = q('[data-sr-index-open]');
    const closeIndex = q('.sr-index-close');
    const sideOpen = q('[data-sr-side-open]');
    const sideClose = q('.sr-side-close');
    const backdrop = q('[data-sr-backdrop]');
    const setIndex = open => {
      root.classList.toggle('is-index-open',open);
      if (indexToggle) indexToggle.setAttribute('aria-expanded',String(open));
    };
    const setSide = open => {
      root.classList.toggle('is-side-open',open);
      if (sideOpen) sideOpen.setAttribute('aria-expanded',String(open));
    };
    indexToggle?.addEventListener('click', ()=>setIndex(!root.classList.contains('is-index-open')));
    closeIndex?.addEventListener('click', ()=>setIndex(false));
    sideOpen?.addEventListener('click', ()=>setSide(!root.classList.contains('is-side-open')));
    sideClose?.addEventListener('click', ()=>setSide(false));
    backdrop?.addEventListener('click', ()=>{setIndex(false);setSide(false);});
    document.addEventListener('keydown', e=>{
      if (e.key==='Escape') {setIndex(false);setSide(false);}
    });

    // Keyboard reading shortcuts for the visible workspace
    document.addEventListener('keydown', e=>{
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      if (e.target.closest('input,textarea,select,[contenteditable="true"]')) return;
      const ws = [...document.querySelectorAll('[data-story-workspace]')].find(w=>w.offsetParent!==null);
      if (ws!==root) return;
      if (e.key==='j' || e.key==='J') { e.preventDefault(); step(1); }
      else if (e.key==='k' || e.key==='K') { e.preventDefault(); step(-1); }
      else if (e.key==='n' || e.key==='N') { e.preventDefault(); nextFlagged(); }
    });

    // Existing links such as the quality panel's "Go to flagged segment"
    document.addEventListener('click', e=>{
      const a = e.target.closest('a[href^="#segment-"]');
      if (!a) return;
      const id = +a.getAttribute('href').replace('#segment-','');
      if (!st.data.some(s=>s.id===id)) return;
      e.preventDefault();
      setCurrent(id,{scroll:true});
    });

    function applyLocale(){
      const l = lang();
      if (search) {
        search.placeholder = search.dataset[l==='en'?'phEn':'phVi'] || '';
        search.setAttribute('aria-label',search.placeholder);
      }
      qa('[data-aria-vi]').forEach(el=>{
        const v = el.dataset[l==='en'?'ariaEn':'ariaVi'];
        if (v) el.setAttribute('aria-label',v);
      });
      renderIndex();
      renderDoc();
      updateMeta();
      if (st.filter==='unread') renderIndex();
    }

    document.getElementById('locale')?.addEventListener('change', ()=>setTimeout(applyLocale,0));

    renderIndex();
    renderDoc();
    applyLocale();
    updateMeta();

    // If a quality link has already pointed at a segment before mount
    const hash = location.hash.match(/^#(?:stress-|editor-)?segment-(\d+)$/);
    if (hash) {
      const id = +hash[1];
      if (st.data.some(s=>s.id===id)) setTimeout(()=>setCurrent(id,{scroll:true}),0);
    }
  }

  function syncStateDefaults(){
    const targets = [...document.querySelectorAll('.p7-state:target, .view:target')];
    document.querySelectorAll('.p7-default, .view.default').forEach(def=>{
      def.classList.toggle('sr-state-hidden', targets.some(t=>t!==def && !def.contains(t)));
    });
  }

  function init(){
    syncStateDefaults();
    addEventListener('hashchange', syncStateDefaults);
    let i = 0;
    document.querySelectorAll('[data-story-workspace]').forEach(root=>{
      const key = root.dataset.ws || `ws-${++i}`;
      if (!root.dataset.ws) root.dataset.ws = key;
      mount(root,key);
    });
  }

  if (document.readyState==='loading') document.addEventListener('DOMContentLoaded',init);
  else init();
})();
