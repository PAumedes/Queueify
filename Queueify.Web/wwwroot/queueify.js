// Small browser helpers: shortcuts, the Play fallback, scrolling to a pick, and the service worker.
(function () {
  'use strict';

  function focusFirst(selector) {
    const el = document.querySelector(selector);
    if (!el) return false;
    el.focus();
    if (el.select) el.select();
    return true;
  }

  // Ctrl+F filters the queue (or searches on Add); Ctrl+K goes to Add; F1 opens Help.
  document.addEventListener('keydown', function (e) {
    if (e.key === 'F1') {
      const help = document.querySelector('a[data-q-nav="help"]');
      if (help) { e.preventDefault(); help.click(); }
    } else if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === 'f' || e.key === 'F')) {
      if (focusFirst('[data-q-filter]')) e.preventDefault();
    } else if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      if (!focusFirst('[data-q-search]')) {
        const add = document.querySelector('.q-rail a[href="add"], .q-rail a[href="/add"]');
        if (add) add.click();
      }
    }
  });

  // Play: try the installed app's link, and open the web player if nothing took over within a second.
  document.addEventListener('click', function (e) {
    const a = e.target.closest && e.target.closest('a[data-app]');
    if (!a || e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) return;
    e.preventDefault();
    const web = a.href;
    let left = false;
    const mark = function () { left = true; };
    window.addEventListener('blur', mark, { once: true });
    document.addEventListener('visibilitychange', mark, { once: true });
    window.location.href = a.getAttribute('data-app');
    setTimeout(function () {
      window.removeEventListener('blur', mark);
      document.removeEventListener('visibilitychange', mark);
      if (!left) window.open(web, '_blank', 'noopener');
    }, 1100);
  });

  // Menus built from <details> close when something else is clicked, or when a link inside them is used.
  document.addEventListener('click', function (e) {
    document.querySelectorAll('details.q-more[open], details.q-menu[open]').forEach(function (d) {
      const insideLink = e.target.closest && d.contains(e.target) && e.target.closest('a');
      if (!d.contains(e.target) || insideLink || (e.target.classList && e.target.classList.contains('q-scrim'))) d.removeAttribute('open');
    });
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') document.querySelectorAll('details.q-more[open], details.q-menu[open]').forEach(function (d) { d.removeAttribute('open'); const s = d.querySelector('summary'); if (s) s.focus(); });
  });
  document.addEventListener('focusout', function (e) {
    const d = e.target.closest && e.target.closest('details.q-more[open], details.q-menu[open]');
    if (d && e.relatedTarget && !d.contains(e.relatedTarget)) d.removeAttribute('open');
  });

  window.queueify = {
    scrollTo: function (id) {
      const el = document.getElementById(id);
      if (el) el.scrollIntoView({ block: 'center', behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
    },
    focus: function (selector) { return focusFirst(selector); },
    theme: function () { return document.documentElement.dataset.qTheme || 'system'; },
    get: function (key) { try { return localStorage.getItem(key); } catch (_) { return null; } },
    set: function (key, value) { try { localStorage.setItem(key, value); } catch (_) { } },
  };

  if ('serviceWorker' in navigator) {
    window.addEventListener('load', function () {
      navigator.serviceWorker.register('sw.js').catch(function () { });
    });
  }
})();
