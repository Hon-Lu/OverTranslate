/* OverTranslate — site behaviour.
   No dependencies. Motion is spring-based so every animation can be grabbed
   and redirected mid-flight instead of playing out on a fixed timeline. */
(function () {
  'use strict';

  var root = document.documentElement;
  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

  /* ---------------- springs ----------------
     Apple's designer-facing parameters: `bounce` (0 = critically damped) and
     `duration` (response, in seconds) rather than mass/stiffness/damping. */

  function spring(opts) {
    var x = opts.from;
    var target = opts.to;
    var v = opts.velocity || 0;
    var zeta = 1 - (opts.bounce || 0);
    var omega = (2 * Math.PI) / (opts.duration || 0.4);
    var onUpdate = opts.onUpdate;
    var onRest = opts.onRest;
    var epsilon = opts.epsilon || 0.01;
    var raf = 0;
    var last = 0;
    var dead = false;

    function step(now) {
      if (dead) return;
      var dt = last ? Math.min((now - last) / 1000, 1 / 30) : 1 / 60;
      last = now;

      // fixed substeps keep the integration stable at any frame rate
      var steps = Math.max(1, Math.ceil(dt / (1 / 240)));
      var h = dt / steps;
      for (var i = 0; i < steps; i++) {
        var a = -omega * omega * (x - target) - 2 * zeta * omega * v;
        v += a * h;
        x += v * h;
      }

      onUpdate(x);
      if (Math.abs(x - target) < epsilon && Math.abs(v) < epsilon * 12) {
        x = target;
        onUpdate(x);
        dead = true;
        if (onRest) onRest();
        return;
      }
      raf = requestAnimationFrame(step);
    }

    raf = requestAnimationFrame(step);

    return {
      stop: function () {
        dead = true;
        cancelAnimationFrame(raf);
      },
      // retarget without losing velocity — no "brick wall" on a reversal
      retarget: function (next) { target = next; },
      value: function () { return x; },
      velocity: function () { return v; }
    };
  }

  /* Apple's momentum projection (exponential decay), not v^2 / 2a. */
  function project(velocity, decelerationRate) {
    var d = decelerationRate || 0.998;
    return (velocity / 1000) * d / (1 - d);
  }

  function clamp(n, lo, hi) { return n < lo ? lo : n > hi ? hi : n; }

  /* ---------------- press feedback (on pointer-down, never on release) ---------------- */

  document.addEventListener('pointerdown', function (e) {
    var el = e.target.closest ? e.target.closest('[data-press]') : null;
    if (el) el.classList.add('is-pressed');
  });
  ['pointerup', 'pointercancel', 'pointerleave', 'blur'].forEach(function (type) {
    document.addEventListener(type, function () {
      var pressed = document.querySelectorAll('[data-press].is-pressed');
      for (var i = 0; i < pressed.length; i++) pressed[i].classList.remove('is-pressed');
    }, true);
  });

  /* ---------------- sticky chrome ---------------- */

  var chrome = document.getElementById('chrome');
  function onScroll() {
    if (chrome) chrome.classList.toggle('is-stuck', window.scrollY > 8);
  }
  onScroll();
  window.addEventListener('scroll', onScroll, { passive: true });

  /* ---------------- reveal on scroll ---------------- */

  var revealables = Array.prototype.slice.call(document.querySelectorAll('.reveal'));

  function revealAll() {
    revealables.forEach(function (el) { el.classList.add('is-in'); });
  }

  // Anything already on screen is shown straight away, so content is never left
  // invisible waiting for an observer callback.
  function revealVisible() {
    var h = window.innerHeight || document.documentElement.clientHeight;
    revealables.forEach(function (el) {
      var rect = el.getBoundingClientRect();
      if (rect.top < h && rect.bottom > 0) el.classList.add('is-in');
    });
  }

  if ('IntersectionObserver' in window) {
    var revealObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-in');
        revealObserver.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.06 });
    revealables.forEach(function (el) { revealObserver.observe(el); });
    revealVisible();
    window.addEventListener('load', revealVisible);

    // Belt and braces: if the observer ever fails to deliver, scrolling still
    // brings content in. Hidden content is far worse than a missed animation.
    var ticking = false;
    var onScrollReveal = function () {
      if (ticking) return;
      ticking = true;
      requestAnimationFrame(function () {
        ticking = false;
        revealVisible();
        var pending = revealables.some(function (el) { return !el.classList.contains('is-in'); });
        if (!pending) window.removeEventListener('scroll', onScrollReveal);
      });
    };
    window.addEventListener('scroll', onScrollReveal, { passive: true });
  } else {
    revealAll();
  }

  /* ---------------- before / after compare ---------------- */

  function setupCompare(el) {
    var handle = el.querySelector('[data-compare-handle]');
    var p = 50;             // percent, 0 = all "before", 100 = all "after"
    var anim = null;
    var dragging = false;
    var touched = false;
    var history = [];       // recent {t, p} for velocity at release
    var grabOffset = 0;

    function markTouched() {
      if (touched) return;
      touched = true;
      el.classList.add('is-touched');
    }

    function paint(next) {
      p = clamp(next, 0, 100);
      el.style.setProperty('--p', p + '%');
      el.style.setProperty('--pn', (p / 100).toFixed(4));
      if (handle) handle.setAttribute('aria-valuenow', Math.round(p));
    }
    paint(50);

    function stopAnim() {
      if (anim) { anim.stop(); anim = null; }
    }

    function pctFromEvent(e) {
      var rect = el.getBoundingClientRect();
      return ((e.clientX - rect.left) / rect.width) * 100;
    }

    function track(value) {
      var now = performance.now();
      history.push({ t: now, p: value });
      while (history.length > 6 && now - history[0].t > 120) history.shift();
    }

    el.addEventListener('pointerdown', function (e) {
      if (e.button !== undefined && e.button !== 0) return;
      markTouched();
      stopAnim();
      dragging = true;
      el.classList.add('is-dragging');
      el.setPointerCapture(e.pointerId);

      var at = pctFromEvent(e);
      // grabbing the handle keeps its offset; pressing elsewhere springs over to the press
      var onHandle = handle && e.target.closest('[data-compare-handle]');
      grabOffset = onHandle ? p - at : 0;
      history.length = 0;
      track(p);
      if (!onHandle) paint(at);
      e.preventDefault();
    });

    el.addEventListener('pointermove', function (e) {
      if (!dragging) return;
      var next = pctFromEvent(e) + grabOffset;
      paint(next);
      track(p);
    });

    function release(e) {
      if (!dragging) return;
      dragging = false;
      el.classList.remove('is-dragging');
      if (e && e.pointerId !== undefined && el.hasPointerCapture && el.hasPointerCapture(e.pointerId)) {
        el.releasePointerCapture(e.pointerId);
      }

      // velocity in percent per second, from the last few samples
      var velocity = 0;
      if (history.length > 1) {
        var first = history[0];
        var last = history[history.length - 1];
        var dt = (last.t - first.t) / 1000;
        if (dt > 0.008) velocity = (last.p - first.p) / dt;
      }
      if (reduceMotion.matches || Math.abs(velocity) < 40) return;

      var landing = clamp(p + project(velocity), 0, 100);
      anim = spring({
        from: p, to: landing, velocity: velocity,
        bounce: 0, duration: 0.45, epsilon: 0.05,
        onUpdate: paint
      });
    }

    el.addEventListener('pointerup', release);
    el.addEventListener('pointercancel', release);

    if (handle) {
      handle.addEventListener('keydown', function (e) {
        var step = e.shiftKey ? 10 : 4;
        var next = null;
        if (e.key === 'ArrowLeft') next = p - step;
        else if (e.key === 'ArrowRight') next = p + step;
        else if (e.key === 'Home') next = 0;
        else if (e.key === 'End') next = 100;
        if (next === null) return;
        e.preventDefault();
        markTouched();
        stopAnim();
        paint(next);
      });
    }

    // One-time hint so the handle reads as draggable, only if untouched.
    if (el.hasAttribute('data-compare-hint') && 'IntersectionObserver' in window) {
      var hinted = false;
      var hintObserver = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting || hinted || touched || reduceMotion.matches) return;
          hinted = true;
          hintObserver.disconnect();
          setTimeout(function () {
            if (touched) return;
            anim = spring({
              from: p, to: 64, bounce: 0, duration: 0.65, onUpdate: paint,
              onRest: function () {
                if (touched) return;
                anim = spring({
                  from: p, to: 36, bounce: 0, duration: 0.8, onUpdate: paint,
                  onRest: function () {
                    if (touched) return;
                    anim = spring({ from: p, to: 50, bounce: 0.1, duration: 0.7, onUpdate: paint });
                  }
                });
              }
            });
          }, 700);
        });
      }, { threshold: 0.45 });
      hintObserver.observe(el);
    }
  }

  var compares = document.querySelectorAll('[data-compare]');
  for (var c = 0; c < compares.length; c++) setupCompare(compares[c]);

  /* ---------------- tabs ---------------- */

  function setupTabs(wrapper) {
    var buttons = Array.prototype.slice.call(wrapper.querySelectorAll('[data-tab]'));
    var ink = wrapper.querySelector('[data-tabs-ink]');
    var xAnim = null;
    var wAnim = null;
    var x = 0;
    var w = 0;

    function drawInk() {
      if (!ink) return;
      ink.style.transform = 'translateX(' + x + 'px)';
      ink.style.width = w + 'px';
    }

    function moveInk(target, animate) {
      if (!ink) return;
      var barRect = ink.parentElement.getBoundingClientRect();
      var rect = target.getBoundingClientRect();
      var nextX = rect.left - barRect.left;
      var nextW = rect.width;

      if (!animate || reduceMotion.matches) {
        if (xAnim) xAnim.stop();
        if (wAnim) wAnim.stop();
        x = nextX; w = nextW;
        drawInk();
        return;
      }
      // X and W get their own springs so they never desync
      var vx = xAnim ? xAnim.velocity() : 0;
      var vw = wAnim ? wAnim.velocity() : 0;
      if (xAnim) xAnim.stop();
      if (wAnim) wAnim.stop();
      xAnim = spring({ from: x, to: nextX, velocity: vx, bounce: 0, duration: 0.38, epsilon: 0.1, onUpdate: function (v) { x = v; drawInk(); } });
      wAnim = spring({ from: w, to: nextW, velocity: vw, bounce: 0, duration: 0.38, epsilon: 0.1, onUpdate: function (v) { w = v; drawInk(); } });
    }

    function select(button, animate, focus) {
      buttons.forEach(function (b) {
        var on = b === button;
        b.setAttribute('aria-selected', on ? 'true' : 'false');
        b.tabIndex = on ? 0 : -1;
        var panel = document.getElementById(b.getAttribute('aria-controls'));
        if (panel) panel.hidden = !on;
      });
      moveInk(button, animate);
      if (focus) button.focus();
    }

    buttons.forEach(function (button, index) {
      button.addEventListener('click', function () { select(button, true, false); });
      button.addEventListener('keydown', function (e) {
        var delta = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
        if (!delta) return;
        e.preventDefault();
        select(buttons[(index + delta + buttons.length) % buttons.length], true, true);
      });
    });

    var initial = buttons.filter(function (b) { return b.getAttribute('aria-selected') === 'true'; })[0] || buttons[0];
    requestAnimationFrame(function () { select(initial, false, false); });
    window.addEventListener('resize', function () {
      var current = buttons.filter(function (b) { return b.getAttribute('aria-selected') === 'true'; })[0];
      if (current) moveInk(current, false);
    });
  }

  Array.prototype.slice.call(document.querySelectorAll('[data-tabs]')).forEach(setupTabs);

  /* ---------------- image zoom ----------------
     Opens from the thumbnail's own rectangle, so the overlay clearly comes
     from the thing that was tapped, and returns the same way. */

  (function setupZoom() {
    var layer = document.querySelector('[data-zoom-layer]');
    if (!layer) return;
    var big = layer.querySelector('[data-zoom-img]');
    var closeBtn = layer.querySelector('[data-zoom-close]');
    var source = null;
    var lastFocus = null;

    function place() {
      if (!source) return;
      var from = source.getBoundingClientRect();
      var to = big.getBoundingClientRect();
      if (!to.width || !to.height) return;
      var scale = from.width / to.width;
      var dx = (from.left + from.width / 2) - (to.left + to.width / 2);
      var dy = (from.top + from.height / 2) - (to.top + to.height / 2);
      return { scale: scale, dx: dx, dy: dy };
    }

    function open(img) {
      source = img;
      lastFocus = document.activeElement;
      big.src = img.currentSrc || img.src;
      big.alt = img.alt || '';
      layer.hidden = false;

      requestAnimationFrame(function () {
        var f = place();
        if (f && !reduceMotion.matches) {
          big.style.transition = 'none';
          big.style.transform = 'translate(' + f.dx + 'px,' + f.dy + 'px) scale(' + f.scale + ')';
          requestAnimationFrame(function () {
            big.style.transition = 'transform 420ms cubic-bezier(0.22, 0.9, 0.24, 1)';
            big.style.transform = 'none';
          });
        } else {
          big.style.transition = 'none';
          big.style.transform = 'none';
        }
        layer.classList.add('is-open');
      });
      if (closeBtn) closeBtn.focus();
      document.body.style.overflow = 'hidden';
    }

    function close() {
      if (layer.hidden) return;
      var f = place();
      layer.classList.remove('is-open');
      if (f && !reduceMotion.matches) {
        big.style.transition = 'transform 320ms cubic-bezier(0.32, 0.72, 0.3, 1)';
        big.style.transform = 'translate(' + f.dx + 'px,' + f.dy + 'px) scale(' + f.scale + ')';
      }
      window.setTimeout(function () {
        layer.hidden = true;
        big.removeAttribute('src');
        big.style.transform = 'none';
        source = null;
      }, reduceMotion.matches ? 0 : 300);
      document.body.style.overflow = '';
      if (lastFocus && lastFocus.focus) lastFocus.focus();
    }

    document.addEventListener('click', function (e) {
      var img = e.target.closest ? e.target.closest('[data-zoom]') : null;
      if (!img) return;
      e.preventDefault();
      open(img);
    });
    layer.addEventListener('click', close);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
    window.addEventListener('scroll', function () { if (!layer.hidden) close(); }, { passive: true });
  })();

  /* ---------------- theme ---------------- */

  (function setupTheme() {
    var toggle = document.querySelector('[data-theme-toggle]');
    if (!toggle) return;
    toggle.addEventListener('click', function () {
      var current = root.getAttribute('data-theme');
      if (!current) {
        current = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
      }
      var next = current === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem('ot-theme', next); } catch (e) {}
    });
  })();

  /* ---------------- language ---------------- */

  var LANGS = {
    'zh-TW': { html: 'zh-Hant', label: '繁體中文', imgSuffix: '', statcard: 'overtranslate-downloads-history.zh-TW.svg', readme: 'README.md', ollama: 'docs/guides/OLLAMA_GUIDE.md' },
    'zh-Hans': { html: 'zh-Hans', label: '简体中文', imgSuffix: '_zh-Hans', statcard: 'overtranslate-downloads-history.svg', readme: 'docs/README.zh-Hans.md', ollama: 'docs/guides/OLLAMA_GUIDE.zh-Hans.md' },
    'en': { html: 'en', label: 'English', imgSuffix: '_en', statcard: 'overtranslate-downloads-history.svg', readme: 'docs/README.en.md', ollama: 'docs/guides/OLLAMA_GUIDE.en.md' },
    'ja': { html: 'ja', label: '日本語', imgSuffix: '_jp', statcard: 'overtranslate-downloads-history.svg', readme: 'docs/README.ja.md', ollama: 'docs/guides/OLLAMA_GUIDE.ja.md' },
    'ko': { html: 'ko', label: '한국어', imgSuffix: '_ko', statcard: 'overtranslate-downloads-history.svg', readme: 'docs/README.ko.md', ollama: 'docs/guides/OLLAMA_GUIDE.ko.md' }
  };
  var BASE_LANG = 'zh-TW';      // HTML 內建的那份文字，也是缺字時的回退來源
  var FALLBACK_LANG = 'en';     // 瀏覽器語言都對不上時給英文，通用度最高
  var REPO = 'https://github.com/asd880921/OverTranslate/blob/main/';
  var CARDS = 'https://raw.githubusercontent.com/asd880921/github-statcards/main/cards/';

  var dictionaries = {};
  var pending = {};
  var currentLang = BASE_LANG;

  // The markup itself is the zh-TW source of truth; snapshot it before anything changes.
  (function snapshotBase() {
    var base = {};
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      base[el.getAttribute('data-i18n')] = el.textContent;
    });
    document.querySelectorAll('[data-i18n-html]').forEach(function (el) {
      base[el.getAttribute('data-i18n-html')] = el.innerHTML;
    });
    document.querySelectorAll('[data-i18n-attr]').forEach(function (el) {
      el.getAttribute('data-i18n-attr').split(';').forEach(function (pair) {
        var parts = pair.split('|');
        if (parts.length === 2) base[parts[1].trim()] = el.getAttribute(parts[0].trim()) || '';
      });
    });
    base['meta.title'] = document.title;
    var desc = document.querySelector('meta[name="description"]');
    base['meta.description'] = desc ? desc.getAttribute('content') : '';
    dictionaries[BASE_LANG] = base;
  })();

  window.otLocale = function (lang, dict) {
    dictionaries[lang] = dict;
    var waiting = pending[lang];
    delete pending[lang];
    if (waiting) waiting.forEach(function (fn) { fn(); });
  };

  function loadLocale(lang, done) {
    if (dictionaries[lang]) { done(); return; }
    if (pending[lang]) { pending[lang].push(done); return; }
    pending[lang] = [done];
    var script = document.createElement('script');
    script.src = 'site/i18n/' + lang + '.js';
    script.onerror = function () {
      delete pending[lang];
      dictionaries[lang] = dictionaries[BASE_LANG];
      done();
    };
    document.head.appendChild(script);
  }

  function applyLanguage(lang) {
    var meta = LANGS[lang] || LANGS[BASE_LANG];
    var dict = dictionaries[lang] || dictionaries[BASE_LANG];
    var base = dictionaries[BASE_LANG];
    function t(key) {
      return Object.prototype.hasOwnProperty.call(dict, key) ? dict[key] : base[key];
    }

    currentLang = lang;
    root.setAttribute('lang', meta.html);
    root.setAttribute('data-lang', lang);

    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var value = t(el.getAttribute('data-i18n'));
      if (value !== undefined) el.textContent = value;
    });
    document.querySelectorAll('[data-i18n-html]').forEach(function (el) {
      var value = t(el.getAttribute('data-i18n-html'));
      if (value !== undefined) el.innerHTML = value;
    });
    document.querySelectorAll('[data-i18n-attr]').forEach(function (el) {
      el.getAttribute('data-i18n-attr').split(';').forEach(function (pair) {
        var parts = pair.split('|');
        if (parts.length !== 2) return;
        var value = t(parts[1].trim());
        if (value !== undefined) el.setAttribute(parts[0].trim(), value);
      });
    });

    // screenshots that exist per interface language
    document.querySelectorAll('[data-loc-img]').forEach(function (img) {
      img.src = 'images/' + img.getAttribute('data-loc-img') + meta.imgSuffix + '.png';
    });
    document.querySelectorAll('[data-loc-src]').forEach(function (img) {
      if (img.getAttribute('data-loc-src') === 'statcard') img.src = CARDS + meta.statcard;
    });
    document.querySelectorAll('[data-loc-href]').forEach(function (a) {
      var kind = a.getAttribute('data-loc-href');
      a.href = REPO + (kind === 'ollama' ? meta.ollama : meta.readme);
    });

    var title = t('meta.title');
    if (title) document.title = title;
    var desc = document.querySelector('meta[name="description"]');
    var descText = t('meta.description');
    if (desc && descText) desc.setAttribute('content', descText);
    var ogTitle = document.querySelector('meta[property="og:title"]');
    if (ogTitle && title) ogTitle.setAttribute('content', title);

    var label = document.querySelector('[data-lang-label]');
    if (label) label.textContent = meta.label;

    document.querySelectorAll('[data-set-lang]').forEach(function (el) {
      var on = el.getAttribute('data-set-lang') === lang;
      if (el.getAttribute('role') === 'menuitemradio') el.setAttribute('aria-checked', on ? 'true' : 'false');
      if (el.tagName === 'A') el.setAttribute('aria-current', on ? 'true' : 'false');
    });

    var canonical = document.querySelector('link[rel="canonical"]');
    if (canonical) {
      canonical.href = 'https://asd880921.github.io/OverTranslate/' + (lang === BASE_LANG ? '' : '?lang=' + lang);
    }
  }

  /* `explicit` = 使用者自己從選單挑的，而不是我們猜的。
     只有主動選擇才寫進網址與 localStorage：乾淨網址代表「依你的環境決定」，
     分享出去時對方看到的是對方的語言；帶 ?lang= 才是刻意指定。 */
  function setLanguage(lang, explicit) {
    if (!LANGS[lang]) lang = FALLBACK_LANG;
    loadLocale(lang, function () {
      applyLanguage(lang);
      if (!explicit) return;
      try { localStorage.setItem('ot-lang', lang); } catch (e) {}
      try {
        var url = new URL(window.location.href);
        url.searchParams.set('lang', lang);
        history.replaceState(null, '', url.toString());
      } catch (e) {
        // file:// 這類 opaque origin 會拒絕 replaceState，頁面本身不受影響
      }
    });
  }

  function detectLanguage() {
    var boot = root.getAttribute('data-lang-boot');
    if (boot && LANGS[boot]) return boot;
    var tags = navigator.languages || [navigator.language || ''];
    for (var i = 0; i < tags.length; i++) {
      var tag = String(tags[i]).toLowerCase();
      if (!tag) continue;
      if (tag.indexOf('zh') === 0) {
        return /hant|tw|hk|mo/.test(tag) ? 'zh-TW' : 'zh-Hans';
      }
      if (tag.indexOf('ja') === 0) return 'ja';
      if (tag.indexOf('ko') === 0) return 'ko';
      if (tag.indexOf('en') === 0) return 'en';
    }
    return FALLBACK_LANG;
  }

  document.addEventListener('click', function (e) {
    var setter = e.target.closest ? e.target.closest('[data-set-lang]') : null;
    if (!setter) return;
    e.preventDefault();
    setLanguage(setter.getAttribute('data-set-lang'), true);
    closeMenu();
  });

  /* language menu popover */

  var menu = document.querySelector('[data-menu]');
  var menuTrigger = menu && menu.querySelector('[data-menu-trigger]');
  var menuPop = menu && menu.querySelector('[data-menu-pop]');

  function closeMenu() {
    if (!menuPop || menuPop.hidden) return;
    menuPop.style.transition = 'opacity 140ms ease, transform 140ms ease';
    menuPop.style.opacity = '0';
    menuPop.style.transform = 'scale(0.94) translateY(-4px)';
    menuTrigger.setAttribute('aria-expanded', 'false');
    window.setTimeout(function () { menuPop.hidden = true; }, reduceMotion.matches ? 0 : 130);
  }

  function openMenu() {
    if (!menuPop) return;
    menuPop.hidden = false;
    menuTrigger.setAttribute('aria-expanded', 'true');
    if (reduceMotion.matches) {
      menuPop.style.opacity = '1';
      menuPop.style.transform = 'none';
      return;
    }
    // materialise: scale and blur arrive together, anchored at the trigger
    menuPop.style.transition = 'none';
    menuPop.style.opacity = '0';
    menuPop.style.transform = 'scale(0.9) translateY(-6px)';
    requestAnimationFrame(function () {
      menuPop.style.transition = 'opacity 200ms ease, transform 320ms cubic-bezier(0.2, 0.9, 0.25, 1)';
      menuPop.style.opacity = '1';
      menuPop.style.transform = 'none';
    });
  }

  if (menuTrigger) {
    menuTrigger.addEventListener('click', function (e) {
      e.stopPropagation();
      if (menuPop.hidden) openMenu(); else closeMenu();
    });
    document.addEventListener('click', function (e) {
      if (menu.contains(e.target)) return;
      closeMenu();
    });
    document.addEventListener('keydown', function (e) {
      if (e.key !== 'Escape' || menuPop.hidden) return;
      closeMenu();
      menuTrigger.focus();
    });
  }

  setLanguage(detectLanguage(), false);
})();
