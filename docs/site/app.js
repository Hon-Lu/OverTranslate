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
      gos.forEach(function (g) { g.classList.toggle('is-on', g.getAttribute('data-tab-go') === button.id); });
      if (focus) button.focus({ preventScroll: true });
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

    // 捲過模式卡片後，浮動膠囊讓人不必捲回上方就能切換；切換後回到模式卡片，從新模式的開頭看起
    // 膠囊放在 .tabs 外面：.tabs 帶淡入的 transform，會讓裡面的 position: fixed 失效
    var floatBar = wrapper.previousElementSibling && wrapper.previousElementSibling.matches('[data-tabs-float]') ? wrapper.previousElementSibling : null;
    var gos = floatBar ? Array.prototype.slice.call(floatBar.querySelectorAll('[data-tab-go]')) : [];
    gos.forEach(function (go) {
      go.addEventListener('click', function () {
        var target = document.getElementById(go.getAttribute('data-tab-go'));
        if (!target || target.getAttribute('aria-selected') === 'true') return;
        select(target, true, true);
        (wrapper.querySelector('.tabs__title') || target).scrollIntoView({ behavior: reduceMotion.matches ? 'auto' : 'smooth', block: 'start' });
      });
    });
    if (floatBar) {
      var bar = wrapper.querySelector('.tabs__bar');
      var ticking = false;
      var hinted = false;
      var updateFloat = function () {
        ticking = false;
        var chromeH = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--chrome-h')) || 60;
        var panel = wrapper.querySelector('[data-tabpanel]:not([hidden])');
        var shown = bar.getBoundingClientRect().bottom < chromeH && panel && panel.getBoundingClientRect().bottom > chromeH + 160;
        floatBar.classList.toggle('is-shown', !!shown);
        if (shown && !hinted) { hinted = true; floatBar.classList.add('is-hint'); }
      };
      window.addEventListener('scroll', function () {
        if (!ticking) { ticking = true; requestAnimationFrame(updateFloat); }
      }, { passive: true });
      window.addEventListener('resize', updateFloat);
    }

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

  /* ---------------- language ----------------
     文字在建置階段就寫進各語言的 HTML，轉址在 <head> 完成，
     這裡只剩一件事：記住使用者主動選過的語言。 */

  // 主動點過語言就記下來，之後回到根目錄直接送去同一個語言
  document.addEventListener('click', function (e) {
    var setter = e.target.closest ? e.target.closest('[data-set-lang]') : null;
    if (!setter) return;
    try { localStorage.setItem('ot-lang', setter.getAttribute('data-set-lang')); } catch (err) {}
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

  /* ---------------- star count ----------------
     數字向 GitHub 要，同一次瀏覽只要一次。要不到就讓它保持收合，
     按鈕本身照常能按，不會留下一塊空白。 */
  var starSlots = document.querySelectorAll('[data-star-count]');
  if (starSlots.length) {
    var paintStars = function (text) {
      for (var i = 0; i < starSlots.length; i++) {
        starSlots[i].textContent = text;
        starSlots[i].classList.add('is-in');
      }
    };
    var cachedStars = null;
    try { cachedStars = sessionStorage.getItem('ot-stars'); } catch (err) {}
    if (cachedStars) {
      paintStars(cachedStars);
    } else if (window.fetch) {
      fetch('https://api.github.com/repos/asd880921/OverTranslate')
        .then(function (res) { return res.ok ? res.json() : null; })
        .then(function (data) {
          if (!data || typeof data.stargazers_count !== 'number') return;
          var text = String(data.stargazers_count);
          try { sessionStorage.setItem('ot-stars', text); } catch (err) {}
          paintStars(text);
        })
        .catch(function () {});
    }
  }

})();
