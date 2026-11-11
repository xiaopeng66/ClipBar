(() => {
  document.documentElement.classList.add('js');
  const repository = 'https://github.com/xiaopeng66/ClipBar';
  const menuButton = document.querySelector('.menu-toggle');
  const nav = document.querySelector('#primary-nav');
  const closeMenu = (returnFocus = false) => {
    menuButton.setAttribute('aria-expanded', 'false');
    menuButton.setAttribute('aria-label', '打开导航');
    nav.classList.remove('is-open');
    if (returnFocus) menuButton.focus();
  };
  menuButton.addEventListener('click', () => {
    const open = menuButton.getAttribute('aria-expanded') !== 'true';
    menuButton.setAttribute('aria-expanded', String(open));
    menuButton.setAttribute('aria-label', open ? '关闭导航' : '打开导航');
    nav.classList.toggle('is-open', open);
  });
  nav.addEventListener('click', (event) => {
    if (event.target.closest('a')) closeMenu();
  });
  document.addEventListener('pointerdown', (event) => {
    if (!nav.contains(event.target) && !menuButton.contains(event.target)) closeMenu();
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && menuButton.getAttribute('aria-expanded') === 'true') closeMenu(true);
  });
  window.matchMedia('(min-width: 761px)').addEventListener('change', (event) => {
    if (event.matches) closeMenu();
  });

  let theme = 'light';
  let scene = 'search';
  let imageRequest = 0;
  const screen = document.querySelector('#hero-screen');
  const detail = document.querySelector('#detail-screen');
  const caption = document.querySelector('#detail-caption');
  const themes = [...document.querySelectorAll('[data-theme]')];
  const scenes = [...document.querySelectorAll('[data-scene]')];
  const sceneLabels = {
    search: { alt: 'ClipBar 输入会议后的搜索结果局部', text: '搜索“会议”，留下需要的那一条。' },
    pinned: { alt: 'ClipBar 已固定条目被选中时的实际界面局部', text: '已固定的内容，在历史顶部等你。' },
  };
  const preload = (src) => new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = resolve;
    image.onerror = reject;
    image.src = src;
  });
  const animateImage = (image) => {
    image.classList.remove('is-switching');
    void image.offsetWidth;
    image.classList.add('is-switching');
  };
  const updateScreens = async () => {
    const request = ++imageRequest;
    const base = 'assets/screens/';
    const main = `${base}history-${theme}-420.webp`;
    const mobile = `${base}history-${theme}-360.webp`;
    const detailSource = `${base}${scene}-${theme}-detail.webp`;
    const compact = `${base}compact-${theme}.webp`;
    try {
      await Promise.all([main, mobile, detailSource, compact].map(preload));
      if (request !== imageRequest) return;
      document.querySelector('#hero-mobile-source').srcset = mobile;
      screen.src = main;
      screen.alt = `ClipBar ${theme === 'light' ? '浅色' : '深色'}主面板，展示虚构的剪贴板历史`;
      detail.src = detailSource;
      detail.alt = sceneLabels[scene].alt;
      document.querySelector('#compact-screen').src = compact;
      document.querySelector('#dialog-screen').src = main;
      caption.replaceChildren(document.createTextNode(sceneLabels[scene].text));
      const note = document.createElement('span');
      note.textContent = '真实界面局部 · 示例数据';
      caption.append(note);
      animateImage(screen);
      animateImage(detail);
      themes.forEach((button) => button.setAttribute('aria-pressed', String(button.dataset.theme === theme)));
      scenes.forEach((button) => {
        const active = button.dataset.scene === scene;
        button.setAttribute('aria-pressed', String(active));
        button.classList.toggle('is-active', active);
      });
    } catch {
      if (request === imageRequest) caption.textContent = '这张预览图暂时未加载，仍可查看当前界面或稍后重试。';
    }
  };
  themes.forEach((button) => button.addEventListener('click', () => {
    theme = button.dataset.theme;
    updateScreens();
  }));
  scenes.forEach((button) => button.addEventListener('click', () => {
    scene = button.dataset.scene;
    updateScreens();
  }));

  const floatingButton = document.querySelector('#floating-toggle');
  const compactPreview = document.querySelector('#compact-preview');
  const toggleFloating = (expanded) => {
    floatingButton.setAttribute('aria-expanded', String(expanded));
    floatingButton.setAttribute('aria-label', expanded ? '收起浮动剪贴板演示' : '展开浮动剪贴板演示');
    compactPreview.hidden = !expanded;
    document.querySelector('#floating-status').textContent = expanded
      ? '这是真实紧凑列表的示例图；粘贴请在桌面软件中操作。'
      : '网页只展示交互，不会读取你的剪贴板。';
  };
  let floatingPinned = false;
  floatingButton.addEventListener('click', () => {
    floatingPinned = !floatingPinned;
    toggleFloating(floatingPinned);
  });
  floatingButton.addEventListener('pointerenter', (event) => {
    if (event.pointerType === 'mouse' && window.matchMedia('(hover: hover)').matches) toggleFloating(true);
  });
  floatingButton.addEventListener('pointerleave', () => {
    if (!floatingPinned) toggleFloating(false);
  });
  floatingButton.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      floatingPinned = false;
      toggleFloating(false);
    }
  });

  const dialog = document.querySelector('#screen-dialog');
  const openPreview = document.querySelector('#preview-open');
  openPreview.addEventListener('click', () => {
    document.querySelector('#dialog-screen').src = screen.src;
    dialog.showModal();
  });
  document.querySelector('#dialog-close').addEventListener('click', () => dialog.close());
  dialog.addEventListener('click', (event) => {
    const bounds = dialog.getBoundingClientRect();
    if (event.target === dialog && (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom)) dialog.close();
  });
  dialog.addEventListener('close', () => openPreview.focus({ preventScroll: true }));

  const dock = document.querySelector('#download-dock');
  const visibility = new Map();
  const dockTargets = [document.querySelector('#hero-download'), document.querySelector('#download'), document.querySelector('.site-footer')];
  if ('IntersectionObserver' in window) {
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => visibility.set(entry.target, entry.isIntersecting));
      dock.hidden = window.scrollY < 250 || dockTargets.some((target) => visibility.get(target));
    });
    dockTargets.forEach((target) => observer.observe(target));
  }

  const copyStatus = document.querySelector('#copy-status');
  document.querySelector('#copy-download').addEventListener('click', async () => {
    const url = document.querySelector('#hero-download').href;
    try {
      await navigator.clipboard.writeText(url);
      copyStatus.textContent = '下载链接已复制，可发送到 Windows 电脑。';
    } catch {
      copyStatus.textContent = `浏览器未允许复制，请手动选择这个链接：${url}`;
    }
  });

  const trustedDownload = (url) => typeof url === 'string' && url.startsWith(`${repository}/releases/download/`);
  const megabytes = (bytes) => `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  const applyRelease = (release, freshlyChecked) => {
    if (!release || !/^\d+\.\d+\.\d+$/.test(release.version)) return false;
    const assets = release.assets;
    if (!assets || !['standard', 'lite', 'installer', 'checksums'].every((kind) => trustedDownload(assets[kind]?.url))) return false;
    if (![assets.standard.bytes, assets.lite.bytes, assets.installer.bytes].every((size) => Number.isSafeInteger(size) && size > 0)) return false;
    document.querySelectorAll('[data-download]').forEach((link) => { link.href = assets[link.dataset.download].url; });
    document.querySelectorAll('[data-version]').forEach((label) => { label.textContent = release.version; });
    document.querySelector('#release-detail').textContent = `${freshlyChecked ? '最新版' : '已核验版本'} ${release.version} · ${megabytes(assets.standard.bytes)} · 无需预装 .NET`;
    document.querySelector('#lite-size').textContent = `（${megabytes(assets.lite.bytes)}）`;
    document.querySelector('#installer-size').textContent = `（${megabytes(assets.installer.bytes)}）`;
    document.querySelector('[data-fallback]').href = `${repository}/releases/latest`;
    return true;
  };
  const fetchJson = async (url, timeout = 3500) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    try {
      const response = await fetch(url, { signal: controller.signal, credentials: 'omit' });
      if (!response.ok) throw new Error('Release metadata unavailable');
      return await response.json();
    } finally { clearTimeout(timer); }
  };
  const loadRelease = async () => {
    try { applyRelease(await fetchJson('release.json'), false); } catch { /* Native links remain usable without metadata. */ }
    try {
      const latest = await fetchJson('https://api.github.com/repos/xiaopeng66/ClipBar/releases/latest');
      if (latest.draft || latest.prerelease || !Array.isArray(latest.assets)) return;
      const names = { standard: 'ClipBar-win-x64.exe', lite: 'ClipBar-lite-win-x64.exe', installer: 'ClipBar-Setup-1.0.0.exe', checksums: 'SHA256SUMS.txt' };
      const assets = {};
      for (const [kind, name] of Object.entries(names)) {
        const asset = latest.assets.find((item) => item.name === name && item.state === 'uploaded');
        if (!asset) return;
        assets[kind] = { url: asset.browser_download_url, bytes: asset.size };
      }
      applyRelease({ version: String(latest.tag_name).replace(/^v/, ''), assets }, true);
    } catch { /* API failure keeps the verified, versioned download and honest version label. */ }
  };
  document.querySelectorAll('button[disabled]').forEach((button) => { button.disabled = false; });
  loadRelease();
})();
