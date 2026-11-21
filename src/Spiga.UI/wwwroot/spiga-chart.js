// SPIGA Suivi Production: every chart of the web screens (SpigaChart),
// drawn by ECharts (lib/echarts, shipped with the site: no internet needed).
// SpigaChart sends a small spec, its series already filtered and its values
// already formatted in C#; this builds the ECharts option from it with the
// colours of the current theme, and draws it again when the theme changes.
window.spigaChart = (function () {
    "use strict";
    var live = new Set();

    function color(el, value) {
        if (!value || value.indexOf("var(") !== 0) return value;
        var name = value.slice(4, -1).split(",")[0].trim();
        return getComputedStyle(el).getPropertyValue(name).trim() || "#3987E5";
    }
    var ESC = { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;" };
    function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return ESC[c]; }); }
    function two(n) { return (n < 10 ? "0" : "") + n; }
    function timeText(ms, withDay) {
        var d = new Date(ms);
        var hm = two(d.getUTCHours()) + ":" + two(d.getUTCMinutes());
        return withDay ? two(d.getUTCDate()) + "/" + two(d.getUTCMonth() + 1) + " " + hm : hm;
    }

    function option(el, spec) {
        var css = getComputedStyle(el);
        var v = function (n, f) { return css.getPropertyValue(n).trim() || f; };
        var fg = v("--fg", "#111"), fg2 = v("--fg2", "#555"), fg3 = v("--fg3", "#777"), line = v("--line", "#ddd"), tile = v("--tile", "#fff");
        var font = v("--font", "Segoe UI, sans-serif");
        var multiDay = false;
        if (spec.times) {
            var lo = Infinity, hi = -Infinity;
            spec.series.forEach(function (s) { s.data.forEach(function (d) { lo = Math.min(lo, d[0]); hi = Math.max(hi, d[0]); }); });
            multiDay = hi - lo > 36 * 3600e3;
        }
        var yAxes = [{
            type: "value", min: spec.min, max: spec.max, interval: spec.interval,
            axisLine: { show: false }, axisTick: { show: false },
            splitLine: { lineStyle: { color: line, type: [3, 4] } },
            axisLabel: {
                color: fg3, fontWeight: 600, fontSize: 12,
                formatter: function (x) { var i = Math.round((x - spec.min) / spec.interval); return spec.ticks[i] != null ? spec.ticks[i] : ""; }
            }
        }];
        var series = spec.series.map(function (s) {
            var c = color(el, s.color), yi = 0;
            if (s.own != null) { yAxes.push({ type: "value", min: 0, max: s.own, show: false }); yi = yAxes.length - 1; }
            var data = s.data.map(function (d) {
                var item = { value: spec.times ? [d[0], d[1]] : d[1], f: d[2] };
                if (d[3]) item.label = { show: false };
                return item;
            });
            var o = {
                name: s.name, data: data, yAxisIndex: yi, itemStyle: { color: c }, emphasis: { focus: "series" },
                animationDuration: 700, animationDelay: function (i) { return Math.min(i, 40) * 20; }
            };
            var label = { show: !!spec.labels && s.own == null, color: fg2, fontWeight: 700, fontSize: 12, formatter: function (p) { return p.data.f; } };
            if (s.kind === "ref") {
                o.type = "line"; o.symbol = "none"; o.silent = true; o.z = 1;
                o.lineStyle = { color: c, width: 2, type: [7, 5] };
                o.tooltip = { show: false };
            } else if (s.kind === "bar") {
                o.type = "bar"; o.barMaxWidth = spec.big ? 70 : 42; o.barGap = "12%";
                if (s.stack) { o.stack = s.stack; label.position = "inside"; label.color = "#fff"; label.fontSize = 11; }
                else { label.position = "top"; o.itemStyle.borderRadius = [5, 5, 0, 0]; }
                o.label = label;
            } else {
                o.type = s.kind === "points" ? "scatter" : "line";
                o.symbolSize = s.kind === "points" ? 11 : 7;
                if (o.type === "line") {
                    o.showSymbol = s.data.length <= 60;
                    o.lineStyle = { width: 3, color: c };
                    if (s.step) o.step = "end";
                    if (s.kind === "area") o.areaStyle = { opacity: .18 };
                }
                label.position = "top"; o.label = label;
            }
            return o;
        });
        var x = spec.times
            ? {
                type: "time", axisLine: { lineStyle: { color: line } }, axisTick: { show: false }, splitLine: { show: false },
                axisLabel: { color: fg3, fontWeight: 600, fontSize: 12, hideOverlap: true, formatter: function (ms) { return timeText(ms, multiDay); } }
            }
            : {
                type: "category", data: spec.cats, axisLine: { lineStyle: { color: line } }, axisTick: { show: false },
                axisLabel: {
                    color: fg3, fontWeight: 600, fontSize: 12, hideOverlap: !spec.slant, interval: spec.slant ? 0 : "auto", rotate: spec.slant ? 30 : 0,
                    formatter: function (t) { return t.length > 22 ? t.slice(0, 21) + "…" : t; }
                }
            };
        var byItem = spec.times && spec.series.some(function (s) { return s.kind === "points"; });
        return {
            animation: !(window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches),
            useUTC: true, // the times are the plant's wall-clock times, sent as UTC
            textStyle: { fontFamily: font },
            grid: { left: 8, right: 14, top: 26, bottom: spec.big ? 44 : 6, containLabel: true },
            legend: { show: false },
            tooltip: {
                trigger: byItem ? "item" : "axis", confine: true,
                backgroundColor: tile, borderColor: line, textStyle: { color: fg, fontSize: 13 },
                axisPointer: { type: spec.times ? "line" : "shadow", shadowStyle: { color: "rgba(127,127,127,.08)" } },
                formatter: function (ps) {
                    ps = Array.isArray(ps) ? ps : [ps];
                    if (!ps.length) return "";
                    var head = spec.times ? timeText(ps[0].value[0], multiDay) : ps[0].name;
                    return "<b>" + esc(head) + "</b>" + ps.filter(function (p) { return p.data && p.data.f != null; }).map(function (p) {
                        return "<br/>" + p.marker + esc(p.seriesName) + " : <b>" + esc(p.data.f) + "</b>";
                    }).join("");
                }
            },
            dataZoom: spec.big
                ? [{ type: "inside", filterMode: "none" },
                   { type: "slider", height: 20, bottom: 8, filterMode: "none", borderColor: line, textStyle: { color: fg3 },
                     fillerColor: "rgba(127,127,127,.15)", labelFormatter: spec.times ? function (ms) { return timeText(ms, multiDay); } : undefined }]
                : [],
            xAxis: x, yAxis: yAxes, series: series
        };
    }

    function draw(el) {
        var chart = echarts.getInstanceByDom(el) || echarts.init(el, null, { renderer: "svg" });
        chart.setOption(option(el, el.__spSpec), true);
    }

    // The theme is a data-theme attribute on .spiga-root.
    new MutationObserver(function () {
        live.forEach(function (el) { if (el.isConnected) draw(el); else live.delete(el); });
    }).observe(document.documentElement, { attributes: true, subtree: true, attributeFilter: ["data-theme"] });

    return {
        set: function (el, json) {
            if (!el || !window.echarts) return;
            el.__spSpec = JSON.parse(json);
            draw(el);
            if (!el.__spResize) {
                el.__spResize = new ResizeObserver(function () { var c = echarts.getInstanceByDom(el); if (c) c.resize(); });
                el.__spResize.observe(el);
            }
            live.add(el);
        },
        resetZoom: function (el) {
            var c = el && window.echarts && echarts.getInstanceByDom(el);
            if (c) c.dispatchAction({ type: "dataZoom", start: 0, end: 100 });
        },
        dispose: function (el) {
            if (!el) return;
            live.delete(el);
            if (el.__spResize) { el.__spResize.disconnect(); el.__spResize = null; }
            var c = window.echarts && echarts.getInstanceByDom(el);
            if (c) c.dispose();
        }
    };
})();
