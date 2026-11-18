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
    var reduced = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

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
        // The final value, or a frame this script wrote itself: nothing new.
        if (prev && (target === prev.target || target === prev.written)) return;
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
            node.nodeValue = state.written = t < 1 ? format(p, state.current) : target;
            if (t < 1) state.raf = requestAnimationFrame(frame);
            else { cancelAnimationFrame(state.raf); clearTimeout(state.timer); }
        }
        node.nodeValue = state.written = format(p, from);
        state.raf = requestAnimationFrame(frame);
        // Frames are paused while the view is hidden: the true value is
        // always written at the end, whatever happened in between.
        state.timer = setTimeout(function () { start = -1e9; frame(); }, dur + 100);
    }

    function scan(root) {
        if (reduced || !root.querySelectorAll) return;
        if (root.matches && root.matches(SELECTOR)) countUp(root);
        var list = root.querySelectorAll(SELECTOR);
        for (var i = 0; i < list.length; i++) countUp(list[i]);
    }

    var observer = new MutationObserver(function (records) {
        for (var i = 0; i < records.length; i++) {
            var r = records[i];
            if (r.type === "characterData") {
                var el = r.target.parentNode;
                if (el && el.matches && el.matches(SELECTOR)) countUp(el);
            } else {
                for (var j = 0; j < r.addedNodes.length; j++) {
                    var n = r.addedNodes[j];
                    if (n.nodeType === 1) scan(n);
                    else if (n.nodeType === 3 && n.parentNode && n.parentNode.matches && n.parentNode.matches(SELECTOR)) countUp(n.parentNode);
                }
            }
        }
    });

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

    // ---------- click ripple ----------
    document.addEventListener("pointerdown", function (e) {
        if (reduced) return;
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
