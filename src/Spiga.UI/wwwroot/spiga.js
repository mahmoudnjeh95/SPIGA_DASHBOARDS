// SPIGA Suivi Production: small motion helpers, no dependency (the plant
// PCs may be offline). Loaded by both hosts after spiga.css.
//  - KPI figures count up to their value when they appear or change.
//  - Cards get a soft spotlight that follows the pointer (--mx / --my).
//  - Buttons get a click ripple (data-ripple + --rx / --ry).
// Blazor owns the DOM: this file never adds or removes nodes inside
// components, it only changes the value of the existing text node (so
// Blazor's next update still finds it) and sets attributes it does not use.
(function () {
    "use strict";
    // Motion plays even with Windows animation effects off (reduced motion):
    // the servers and remote sessions have them off by default.

    // ---------- count-up ----------
    // "65,3 %", "1 234 kg", "+1,2 %", "12,5 t" -> animate the number only,
    // keep the prefix, suffix, decimals and thousands separator.
    var NUM = /[-+−]?\d{1,3}(?:[   ]\d{3})+(?:,\d+)?|[-+−]?\d+(?:,\d+)?/;
    var SELECTOR = ".kpi .v, .mini .v";
    var running = new WeakMap();

    function parse(text) {
        var m = NUM.exec(text);
        if (!m) return null;
        var raw = m[0];
        var sep = (/[   ]/.exec(raw) || [""])[0];
        var dec = raw.indexOf(",") >= 0 ? raw.length - raw.indexOf(",") - 1 : 0;
        var sign = raw.charAt(0) === "+" ? "+" : "";
        var minus = raw.charAt(0) === "−" ? "−" : "-";
        var value = parseFloat(raw.replace(/[   +]/g, "").replace("−", "-").replace(",", "."));
        if (!isFinite(value)) return null;
        return { pre: text.slice(0, m.index), post: text.slice(m.index + raw.length), sep: sep, dec: dec, sign: sign, minus: minus, value: value };
    }

    function format(p, v) {
        var s = Math.abs(v).toFixed(p.dec).split(".");
        if (p.sep) s[0] = s[0].replace(/\B(?=(\d{3})+(?!\d))/g, p.sep);
        var out = s.join(",");
        if (v < 0 && Number(out.replace(/\D/g, "")) !== 0) out = p.minus + out;
        else if (p.sign && v > 0) out = "+" + out;
        return p.pre + out + p.post;
    }

    function countUp(el) {
        var node = el.firstChild;
        if (!node || node.nodeType !== 3 || el.childNodes.length !== 1) return;
        var target = node.nodeValue;
        var prev = running.get(el);
        // The value it is already counting to: nothing new. (Its own frames
        // never come back here: write() drops their mutation records. Telling
        // them apart by text did not work: a new "0" from Blazor looked like
        // the first frame of the old count, which then went on to the old value.)
        if (prev && target === prev.target) return;
        if (prev) { cancelAnimationFrame(prev.raf); clearTimeout(prev.timer); }
        var p = parse(target);
        if (!p || p.value === 0) { running.delete(el); return; }
        var from = prev && prev.p ? prev.current : 0;
        var start = performance.now(), dur = 900 + Math.min(600, Math.abs(p.value - from) > 999 ? 400 : 0);
        var state = { target: target, p: p, current: from, written: null, raf: 0, timer: 0 };
        running.set(el, state);
        function frame() {
            if (node.parentNode !== el) { running.delete(el); return; }   // Blazor replaced it
            var t = Math.max(0, Math.min(1, (performance.now() - start) / dur));
            var e = 1 - Math.pow(1 - t, 4);
            state.current = from + (p.value - from) * e;
            write(node, state.written = t < 1 ? format(p, state.current) : target);
            if (t < 1) state.raf = requestAnimationFrame(frame);
            else { cancelAnimationFrame(state.raf); clearTimeout(state.timer); }
        }
        write(node, state.written = format(p, from));
        state.raf = requestAnimationFrame(frame);
        // Frames are paused while the view is hidden: the true value is
        // always written at the end, whatever happened in between.
        state.timer = setTimeout(function () { start = -1e9; frame(); }, dur + 100);
    }

    function scan(root) {
        if (!root.querySelectorAll) return;
        if (root.matches && root.matches(SELECTOR)) countUp(root);
        var list = root.querySelectorAll(SELECTOR);
        for (var i = 0; i < list.length; i++) countUp(list[i]);
    }

    // A frame of the count: written, and its mutation record dropped so the
    // observer only ever sees Blazor's own changes.
    function write(node, text) {
        node.nodeValue = text;
        var others = observer.takeRecords().filter(function (r) { return r.target !== node; });
        if (others.length) handle(others);
    }

    var observer = new MutationObserver(handle);

    // Each figure once per batch, after the whole batch is read: Blazor
    // inserts a tile and sets its text in the same batch, and the second
    // record, read after the count had started, found the count's own first
    // frame ("0") as the new value, stopped the count and left the "0".
    function handle(records) {
        var els = [];
        function add(el) { if (el && el.matches && el.matches(SELECTOR) && els.indexOf(el) < 0) els.push(el); }
        for (var i = 0; i < records.length; i++) {
            var r = records[i];
            if (r.type === "characterData") {
                add(r.target.parentNode);
            } else {
                for (var j = 0; j < r.addedNodes.length; j++) {
                    var n = r.addedNodes[j];
                    if (n.nodeType === 1) {
                        add(n);
                        if (n.querySelectorAll) {
                            var list = n.querySelectorAll(SELECTOR);
                            for (var k = 0; k < list.length; k++) add(list[k]);
                        }
                    }
                    else if (n.nodeType === 3) add(n.parentNode);
                }
            }
        }
        for (var e = 0; e < els.length; e++) countUp(els[e]);
    }

    function start() {
        observer.observe(document.body, { childList: true, subtree: true, characterData: true });
        scan(document.body);
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
    else start();

    // ---------- card spotlight ----------
    var raf = 0, lastEvt = null;
    document.addEventListener("pointermove", function (e) {
        lastEvt = e;
        if (raf) return;
        raf = requestAnimationFrame(function () {
            raf = 0;
            var card = lastEvt.target && lastEvt.target.closest && lastEvt.target.closest(".spiga-root .card");
            if (!card) return;
            var r = card.getBoundingClientRect();
            card.style.setProperty("--mx", (lastEvt.clientX - r.left) + "px");
            card.style.setProperty("--my", (lastEvt.clientY - r.top) + "px");
        });
    }, { passive: true });

    // ---------- Escape closes an enlarged chart ----------
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape") return;
        var close = document.querySelector(".spiga-root .chart-modal .chart-close");
        if (close) close.click();
    });

    // The wheel zooms an enlarged chart (SpigaChart): the page must not scroll.
    document.addEventListener("wheel", function (e) {
        if (e.target && e.target.closest && e.target.closest(".spiga-root .chart-zoom")) e.preventDefault();
    }, { passive: false });

    // ---------- click ripple ----------
    document.addEventListener("pointerdown", function (e) {
        var b = e.target && e.target.closest && e.target.closest(".spiga-root .bar, .spiga-root .seg, .spiga-root .tab, .spiga-root .rolebtn");
        if (!b || b.disabled) return;
        var r = b.getBoundingClientRect();
        b.style.setProperty("--rx", (e.clientX - r.left) + "px");
        b.style.setProperty("--ry", (e.clientY - r.top) + "px");
        b.removeAttribute("data-ripple");
        void b.offsetWidth; // restart the animation
        b.setAttribute("data-ripple", "");
        clearTimeout(b.__spRipple);
        b.__spRipple = setTimeout(function () { b.removeAttribute("data-ripple"); }, 650);
    }, { passive: true });
})();

// ---------- file export and printing (called by the screens) ----------
// A file built by the screen (CSV...), handed to the browser / WebView2
// download (the desktop app asks where to save it).
window.spigaDownload = function (name, mime, base64) {
    var bytes = atob(base64), buf = new Uint8Array(bytes.length);
    for (var i = 0; i < bytes.length; i++) buf[i] = bytes.charCodeAt(i);
    var url = URL.createObjectURL(new Blob([buf], { type: mime }));
    var a = document.createElement("a");
    a.href = url; a.download = name; a.style.display = "none";
    document.body.appendChild(a); a.click();
    setTimeout(function () { URL.revokeObjectURL(url); a.remove(); }, 1000);
};
window.spigaPrint = function () { window.print(); };

// ---------- planning board (Planification des arrêts) ----------
// On a line's track: a click or a drag on an empty part picks a slot (the
// page asks what to plan in it); a click on a block shows its detail; a
// drag on a planned slot moves it, or its edge moves its start / end. The
// gesture is drawn here and sent to the page once, on release.
window.spigaBoard = {
    attach: function (root, page) {
        if (!root || root.__spBoard) return;
        var state = null;
        function frac(track, x) {
            var r = track.getBoundingClientRect();
            return r.width > 0 ? Math.min(1, Math.max(0, (x - r.left) / r.width)) : 0;
        }
        function down(e) {
            if (e.button !== 0) return;
            var block = e.target.closest(".pb-block");
            var track = e.target.closest(".pb-track");
            if (!track) return;
            if (block) {
                var r = block.getBoundingClientRect();
                var mode = !block.hasAttribute("data-drag") ? "click"
                    : e.clientX - r.left < 8 ? "Start" : r.right - e.clientX < 8 ? "End" : "Move";
                state = { kind: "block", block: block, track: track, mode: mode, x: e.clientX,
                    left: parseFloat(block.style.left), width: parseFloat(block.style.width), moved: false };
            } else {
                var a = frac(track, e.clientX);
                var pick = document.createElement("div");
                pick.className = "pb-picking";
                track.appendChild(pick);
                state = { kind: "slot", track: track, a: a, b: a, pick: pick };
                draw();
            }
            track.setPointerCapture(e.pointerId);
            e.preventDefault();
        }
        function draw() {
            var lo = Math.min(state.a, state.b), hi = Math.max(state.a, state.b);
            state.pick.style.left = (lo * 100) + "%";
            state.pick.style.width = Math.max(0.4, (hi - lo) * 100) + "%";
        }
        function move(e) {
            if (!state) return;
            if (state.kind === "slot") {
                state.b = frac(state.track, e.clientX);
                draw();
                return;
            }
            var dx = e.clientX - state.x;
            if (Math.abs(dx) > 4) state.moved = true;
            if (!state.moved || state.mode === "click") return;
            var d = dx / state.track.getBoundingClientRect().width * 100;
            var s = state.block.style;
            if (state.mode === "Move") s.left = (state.left + d) + "%";
            else if (state.mode === "Start") { s.left = (state.left + d) + "%"; s.width = Math.max(0.5, state.width - d) + "%"; }
            else s.width = Math.max(0.5, state.width + d) + "%";
            state.block.classList.add("dragging");
        }
        function up(e) {
            if (!state) return;
            var s = state;
            state = null;
            try { s.track.releasePointerCapture(e.pointerId); } catch (_) { }
            var machine = s.track.getAttribute("data-machine");
            if (s.kind === "slot") {
                s.pick.remove();
                page.invokeMethodAsync("SlotPicked", machine, s.a, s.b);
            } else if (!s.moved || s.mode === "click") {
                page.invokeMethodAsync("BlockClicked", s.block.getAttribute("data-key"));
            } else {
                var delta = (e.clientX - s.x) / s.track.getBoundingClientRect().width;
                page.invokeMethodAsync("BlockMoved", s.block.getAttribute("data-key"), s.mode, delta);
            }
        }
        function cancel() {
            if (!state) return;
            if (state.pick) state.pick.remove();
            if (state.block) { state.block.style.left = state.left + "%"; state.block.style.width = state.width + "%"; state.block.classList.remove("dragging"); }
            state = null;
        }
        root.addEventListener("pointerdown", down);
        root.addEventListener("pointermove", move);
        root.addEventListener("pointerup", up);
        root.addEventListener("pointercancel", cancel);
        root.__spBoard = function () {
            root.removeEventListener("pointerdown", down);
            root.removeEventListener("pointermove", move);
            root.removeEventListener("pointerup", up);
            root.removeEventListener("pointercancel", cancel);
        };
    },
    detach: function (root) {
        if (root && root.__spBoard) { root.__spBoard(); root.__spBoard = null; }
    }
};

// ---------- Écran TV (TvLayout) ----------
// A wall screen: it keeps showing its last slide while the connection to
// the server is down, without the reconnection banner, and reloads the page
// once Blazor gives up (the tab's session comes back with the reload).
window.spigaTv = {
    enable: function () {
        if (window.__spTv) return;
        window.__spTv = true;
        document.documentElement.classList.add("spiga-tv");
        var reloading = false;
        function check() {
            var modal = document.getElementById("components-reconnect-modal");
            if (!modal || reloading) return;
            var c = modal.className || "";
            if (c.indexOf("components-reconnect-failed") >= 0 || c.indexOf("components-reconnect-rejected") >= 0) {
                reloading = true;
                setTimeout(function tryReload() {
                    // The server back? Reload; else try again a bit later.
                    fetch(location.href, { method: "HEAD", cache: "no-store" })
                        .then(function () { location.reload(); })
                        .catch(function () { setTimeout(tryReload, 10000); });
                }, 3000);
            }
        }
        new MutationObserver(check).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ["class"] });
        // A TV has no one to scroll it: the slide is scaled down until it
        // fits the screen (never up). Measured from the slide's natural
        // height (its height / the zoom applied), so no flicker.
        setInterval(function () {
            var view = document.querySelector(".tv-view.tv-fit");
            if (!view) return;
            var current = parseFloat(view.style.zoom) || 1;
            // The blocks of the slide, stacked (the view itself is at least
            // the screen's height, its note bar held at the bottom).
            var kids = view.children, natural = 28;
            for (var i = 0, n = 0; i < kids.length; i++) {
                var h = kids[i].getBoundingClientRect().height;
                if (h === 0 || getComputedStyle(kids[i]).position === "fixed") continue; // the stop pop-up floats
                natural += h / current + (n++ > 0 ? 14 : 0);
            }
            var target = Math.max(0.4, Math.min(1, (window.innerHeight - 6) / natural));
            // Down as soon as it overflows; back up only for a real gain (no hunting).
            if (target < current - 0.003 || target > current + 0.02) view.style.zoom = target.toFixed(3);
        }, 1500);
        // F11: full screen (in the desktop TV window too, where the keys go to the page).
        document.addEventListener("keydown", function (e) {
            if (e.key === "F11") { e.preventDefault(); window.spigaTv.fullscreen(); }
        });
    },
    fullscreen: function () {
        var el = document.documentElement;
        if (document.fullscreenElement) document.exitFullscreen();
        else if (el.requestFullscreen) el.requestFullscreen().catch(function () { });
    }
};
