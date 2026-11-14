// A full board in its own window. Quasar and the Torch plugin serve this same page, so both get the same charts.
// The server decides which panels there are, and this only draws what it's sent. Time series use uPlot, which is what
// Grafana's panels use. Gauges and donuts are SVG, and heatmaps and treemaps are plain canvas: see docs/ChartBenchmark.md.
// Names come from players, so anything put into HTML goes through escape().
(() => {
    const PALETTE = {
        "monitor-c-physics": "#f2495c",
        "monitor-c-network": "#ff9830",
        "monitor-c-session": "#5794f2",
        "monitor-c-other": "#b877d9",
        "monitor-c-idle": "#73bf69",
        "monitor-c-0": "#8ab8ff",
        "monitor-c-1": "#fade2a",
        "monitor-c-2": "#37c9a4",
        "monitor-c-3": "#e0589f",
        "monitor-c-4": "#c4d44f",
        "monitor-c-5": "#a3a9b5",
        "monitor-c-6": "#6f7ff2",
        "monitor-c-7": "#ff7383",
    };

    const AXIS_FONT = '11px "Segoe UI", Roboto, Helvetica, Arial, sans-serif';
    const AXIS_TEXT = "#8e8e9e";
    const GRID_LINE = "#24272d";
    const SYNC_KEY = "monitor-board";
    const LIVE_MS = 3000;
    const SLOW_MS = 30000;
    const SLOWEST_MS = 120000;
    const SHORT_RANGE_MINUTES = 60;
    const MIDDLE_RANGE_MINUTES = 360;
    const LEGEND_ROW = 17;
    const LEGEND_ROWS_INCLUDED = 4;
    const LEGEND_ROWS_MOST = 13;
    const LEAST_ZOOM_PIXELS = 8;
    const MARKER_REACH = 0.01;
    const CLOSED_KEY = "monitor-board-closed";

    const RANGES = [
        ["5 minutes", 5],
        ["15 minutes", 15],
        ["1 hour", 60],
        ["6 hours", 360],
        ["24 hours", 1440],
        ["3 days", 4320],
        ["7 days", 10080],
    ];

    const query = new URLSearchParams(location.search);
    const api = document.body.dataset.api;
    const root = document.getElementById("board");
    const views = new Map();
    const sections = new Map();

    const state = {
        server: query.get("server") ?? null,
        minutes: Number(query.get("minutes")) || 60,
        from: null,
        to: null,
        live: true,
        markers: [],
        request: 0,
        timer: 0,
    };

    let closed = new Set();

    try {
        closed = new Set(JSON.parse(localStorage.getItem(CLOSED_KEY) ?? "[]"));
    } catch {
        // Nothing saved, or it can't be read. Every section starts open.
    }

    function escape(text) {
        return String(text).replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`);
    }

    function colorOf(name) {
        return PALETTE[name] ?? AXIS_TEXT;
    }

    function number(value, decimals) {
        if (value === null || value === undefined) {
            return "-";
        }

        const size = Math.abs(value);
        const places = decimals ?? (size >= 100 ? 0 : size >= 10 ? 1 : 2);
        return value.toLocaleString(undefined, { minimumFractionDigits: places, maximumFractionDigits: places });
    }

    function withUnit(value, unit, decimals) {
        return value === null || value === undefined ? "-" : number(value, decimals) + (unit ?? "");
    }

    // Axes have little room, so big numbers shorten there and only there.
    function axisNumber(value) {
        const size = Math.abs(value);

        if (size >= 1e6) {
            return `${+(value / 1e6).toFixed(1)}M`;
        }

        if (size >= 1e4) {
            return `${+(value / 1e3).toFixed(1)}k`;
        }

        return `${+value.toFixed(2)}`;
    }

    function bandColor(bands, value) {
        if (!bands || value === null || value === undefined) {
            return bands ? AXIS_TEXT : PALETTE["monitor-c-session"];
        }

        return colorOf(bands.find(band => value <= band.limit)?.color);
    }

    function when(seconds, withSeconds) {
        const date = new Date(seconds * 1000);
        return date.toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", second: withSeconds ? "2-digit" : undefined });
    }

    // ---- The one tooltip, on top of everything so no panel clips it

    const tip = document.createElement("div");
    tip.className = "tip";
    document.body.appendChild(tip);

    let mouseX = 0;
    let mouseY = 0;

    document.addEventListener("mousemove", event => {
        mouseX = event.clientX;
        mouseY = event.clientY;
    }, { passive: true });

    function showTip(html) {
        tip.innerHTML = html;
        tip.style.display = "block";

        const box = tip.getBoundingClientRect();
        const x = mouseX + 14 + box.width > innerWidth ? mouseX - box.width - 14 : mouseX + 14;
        const y = Math.min(mouseY + 14, innerHeight - box.height - 6);

        tip.style.left = `${Math.max(x, 4)}px`;
        tip.style.top = `${Math.max(y, 4)}px`;
    }

    function hideTip() {
        tip.style.display = "none";
    }

    function tipRow(color, name, value) {
        return `<div><span><i style="background:${color}"></i>${escape(name)}</span><span>${escape(value)}</span></div>`;
    }

    // ---- Time series

    function shownLines(chart) {
        const shown = chart.lines.filter(line => line.overlay || line.values.some(value => (value ?? 0) >= chart.minimumToShow));
        return shown.filter(line => isBand(chart, line)).concat(shown.filter(line => !isBand(chart, line)));
    }

    function isBand(chart, line) {
        return chart.stacked && !line.overlay;
    }

    function stacked(bands) {
        const totals = [];
        let below = null;

        for (const line of bands) {
            const total = line.values.map((value, index) => value === null ? null : value + (below?.[index] ?? 0));
            totals.push(total);
            below = total;
        }

        return totals;
    }

    function columns(chart, lines) {
        const bands = lines.filter(line => isBand(chart, line));
        return [chart.times, ...stacked(bands), ...lines.filter(line => !isBand(chart, line)).map(line => line.values)];
    }

    function gradient(plot, color) {
        // uPlot asks once while it starts, before the plot has a size.
        if (!Number.isFinite(plot.bbox.top)) {
            return color;
        }

        const fill = plot.ctx.createLinearGradient(0, plot.bbox.top, 0, plot.bbox.top + plot.bbox.height);
        fill.addColorStop(0, `${color}55`);
        fill.addColorStop(1, `${color}05`);
        return fill;
    }

    function drawLevels(plot, levels) {
        const context = plot.ctx;
        const { left, top, width, height } = plot.bbox;
        const ratio = uPlot.pxRatio;

        for (const level of levels ?? []) {
            const y = plot.valToPos(level.value, "y", true);

            if (y >= top && y <= top + height) {
                context.save();
                context.strokeStyle = colorOf(level.color);
                context.lineWidth = ratio;
                context.setLineDash([6 * ratio, 4 * ratio]);
                context.beginPath();
                context.moveTo(left, y);
                context.lineTo(left + width, y);
                context.stroke();
                context.restore();
            }
        }
    }

    function drawMarkers(plot) {
        const context = plot.ctx;
        const { left, top, width, height } = plot.bbox;
        const ratio = uPlot.pxRatio;

        context.save();
        context.lineWidth = ratio;
        context.setLineDash([3 * ratio, 3 * ratio]);

        for (const marker of state.markers) {
            const x = plot.valToPos(marker.time, "x", true);

            if (x >= left && x <= left + width) {
                context.strokeStyle = colorOf(marker.color);
                context.beginPath();
                context.moveTo(x, top);
                context.lineTo(x, top + height);
                context.stroke();
            }
        }

        context.restore();
    }

    function axes(chart, secondScale) {
        const base = { stroke: AXIS_TEXT, font: AXIS_FONT, grid: { stroke: GRID_LINE, width: 1 }, ticks: { show: false } };

        const all = [
            { ...base, size: 42 },
            { ...base, size: 52, values: (plot, ticks) => ticks.map(axisNumber) },
        ];

        if (secondScale) {
            all.push({ ...base, scale: "ratio", side: 1, size: 34, grid: { show: false }, values: (plot, ticks) => ticks.map(tick => tick.toFixed(1)) });
        }

        return all;
    }

    function seriesFor(chart, lines) {
        return lines.map(line => {
            const color = colorOf(line.color);
            const band = isBand(chart, line);

            return {
                label: line.name,
                scale: line.secondScale ? "ratio" : "y",
                stroke: color,
                width: band ? 1 : 1.5,
                dash: line.dashed ? [6, 4] : undefined,
                points: { show: false },
                paths: line.dashed ? undefined : uPlot.paths.spline(),
                fill: band ? `${color}55` : line.overlay ? undefined : plot => gradient(plot, color),
            };
        });
    }

    function bandsFor(chart, lines) {
        const count = lines.filter(line => isBand(chart, line)).length;
        const bands = [];

        for (let index = 1; index < count; index++) {
            bands.push({ series: [index + 1, index], fill: `${colorOf(lines[index].color)}55` });
        }

        return bands;
    }

    function tooltip(view, plot) {
        const { idx } = plot.cursor;

        if (!view.hovered || idx === null || idx === undefined) {
            if (view.hovered) {
                hideTip();
            }

            return;
        }

        const { chart, lines, panel } = view;
        const time = chart.times[idx];
        const rows = lines
            .map((line, index) => ({ line, value: line.values[idx], shown: plot.series[index + 1].show }))
            .filter(row => row.shown && row.value !== null)
            .sort((a, b) => (b.line.secondScale ? -1 : 0) - (a.line.secondScale ? -1 : 0) || (b.value - a.value));

        const span = plot.scales.x.max - plot.scales.x.min;
        const near = state.markers.filter(marker => Math.abs(marker.time - time) <= span * MARKER_REACH);

        showTip(`<b>${escape(when(time, true))}</b>`
            + rows.map(row => tipRow(colorOf(row.line.color), row.line.name, chart.rows ? "" : withUnit(row.value, row.line.unit ?? panel.unit))).join("")
            + near.map(marker => `<p>${escape(marker.text)}</p>`).join(""));
    }

    function timeView(element, body, legend) {
        const view = { hovered: false, key: "", plot: null, chart: null, lines: [], panel: null };

        function destroyPlot() {
            view.plot?.destroy();
            view.plot = null;
            view.key = "";
        }

        function create(panel, chart, lines) {
            const secondScale = lines.some(line => line.secondScale);
            const timeline = !!chart.rows;

            const options = {
                width: Math.max(body.clientWidth, 100),
                height: Math.max(body.clientHeight, 60),
                legend: { show: false },
                padding: [8, secondScale ? 0 : 12, 0, 0],
                cursor: { y: false, sync: { key: SYNC_KEY }, points: { size: 6 }, drag: { x: true, y: false, setScale: false } },
                scales: {
                    x: { time: true },
                    y: { range: timeline ? [-0.6, chart.rows.length - 0.4] : (plot, min, max) => [0, chart.maximum ?? (max > 0 ? max * 1.1 : 1)] },
                    ratio: { range: [0, 1.05] },
                },
                axes: axes(chart, secondScale),
                series: [{}, ...seriesFor(chart, lines)],
                bands: chart.stacked ? bandsFor(chart, lines) : undefined,
                hooks: {
                    draw: [plot => {
                        drawLevels(plot, view.panel.levels);
                        drawMarkers(plot);
                    }],
                    setCursor: [plot => tooltip(view, plot)],
                    setSelect: [plot => {
                        if (plot.select.width >= LEAST_ZOOM_PIXELS) {
                            const start = plot.posToVal(plot.select.left, "x");
                            const end = plot.posToVal(plot.select.left + plot.select.width, "x");
                            plot.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false);
                            zoom(start, end);
                        }
                    }],
                },
            };

            if (timeline) {
                options.series = options.series.map((series, index) => index === 0 ? series : {
                    ...series,
                    width: 10,
                    paths: undefined,
                    fill: undefined,
                    points: { show: true, size: 9, fill: series.stroke, stroke: series.stroke },
                });
                options.axes[1] = {
                    stroke: "#ccccdc",
                    font: AXIS_FONT,
                    grid: { show: false },
                    ticks: { show: false },
                    size: 130,
                    splits: () => chart.rows.map((name, index) => index),
                    values: (plot, ticks) => ticks.map(tick => chart.rows[tick] ?? ""),
                };
            }

            view.plot = new uPlot(options, columns(chart, lines), body);
            view.plot.over.addEventListener("mouseenter", () => { view.hovered = true; });
            view.plot.over.addEventListener("mouseleave", () => {
                view.hovered = false;
                hideTip();
            });
            view.plot.over.addEventListener("dblclick", resetZoom);
        }

        function drawLegend(panel, chart, lines) {
            if (!legend) {
                return;
            }

            const rows = lines.map((line, index) => {
                const values = line.values.filter(value => value !== null);
                const unit = line.unit ?? panel.unit;
                const hidden = view.plot?.series[index + 1]?.show === false ? ' class="off"' : "";
                const average = values.length === 0 ? null : values.reduce((sum, value) => sum + value, 0) / values.length;

                return `<tr data-series="${index + 1}"${hidden}><td><i style="background:${colorOf(line.color)}"></i>${escape(line.name)}</td>`
                    + `<td>${escape(withUnit(values.length ? Math.min(...values) : null, unit))}</td>`
                    + `<td>${escape(withUnit(values.length ? Math.max(...values) : null, unit))}</td>`
                    + `<td>${escape(withUnit(average, unit))}</td>`
                    + `<td>${escape(withUnit(values.at(-1) ?? null, unit))}</td></tr>`;
            });

            legend.innerHTML = `<table><tr><th>Name</th><th>Min</th><th>Max</th><th>Avg</th><th>Current</th></tr>${rows.join("")}</table>`;

            const shownRows = Math.min(lines.length + 1, LEGEND_ROWS_MOST);
            legend.style.height = `${shownRows * LEGEND_ROW + 4}px`;
            element.style.minHeight = `${panel.height + Math.max(shownRows - LEGEND_ROWS_INCLUDED, 0) * LEGEND_ROW}px`;
        }

        if (legend) {
            legend.addEventListener("click", event => {
                const row = event.target.closest("tr[data-series]");

                // Hiding one band would leave those above it floating, so bands stay.
                if (row && view.plot && !view.chart.stacked) {
                    const series = Number(row.dataset.series);
                    view.plot.setSeries(series, { show: !view.plot.series[series].show });
                    row.classList.toggle("off", !view.plot.series[series].show);
                }
            });
        }

        return {
            update(panel) {
                const chart = panel.chart;
                const lines = chart ? shownLines(chart) : [];
                const empty = lines.length === 0 || chart.times.length === 0;

                view.panel = panel;
                view.chart = chart;
                view.lines = lines;
                setEmpty(body, empty);

                if (empty) {
                    destroyPlot();

                    if (legend) {
                        legend.textContent = "";
                        legend.style.height = "0";
                    }

                    element.style.minHeight = `${panel.height}px`;
                    return;
                }

                const key = JSON.stringify([lines.map(line => [line.name, line.color]), chart.stacked]);

                drawLegend(panel, chart, lines);

                if (view.key === key) {
                    view.plot.setData(columns(chart, lines), true);
                } else {
                    destroyPlot();
                    create(panel, chart, lines);
                    view.key = key;
                }
            },
            resize(width, height) {
                view.plot?.setSize({ width, height });
            },
            destroy: destroyPlot,
        };
    }

    // ---- Stat tiles

    function statView(body) {
        const value = document.createElement("div");
        value.className = "stat-value";
        body.appendChild(value);

        let plot = null;
        let key = "";

        function destroyPlot() {
            plot?.destroy();
            plot = null;
            key = "";
        }

        return {
            update(panel) {
                const color = bandColor(panel.bands, panel.value);
                const line = panel.chart?.lines[0];

                value.style.color = color;
                value.innerHTML = panel.value === null || panel.value === undefined
                    ? "-"
                    : `${escape(number(panel.value, panel.decimals))}<span class="stat-unit">${escape(panel.unit ?? "")}</span>`;

                if (!line) {
                    destroyPlot();
                    return;
                }

                const data = [panel.chart.times, line.values];

                if (key === color) {
                    plot.setData(data, true);
                    return;
                }

                destroyPlot();
                key = color;

                plot = new uPlot({
                    width: Math.max(body.clientWidth, 60),
                    height: Math.max(body.clientHeight, 40),
                    legend: { show: false },
                    cursor: { show: false },
                    padding: [38, 0, 0, 0],
                    scales: { x: { time: true }, y: { range: (self, min, max) => min === max ? [0, (max || 1) * 1.2] : [min - (max - min) * 0.2, max + (max - min) * 0.1] } },
                    axes: [{ show: false }, { show: false }],
                    series: [{}, { stroke: color, width: 1.5, points: { show: false }, paths: uPlot.paths.spline(), fill: self => gradient(self, color) }],
                }, data, body);
            },
            resize(width, height) {
                plot?.setSize({ width, height });
            },
            destroy: destroyPlot,
        };
    }

    // ---- Gauges and donuts

    const SVG = "http://www.w3.org/2000/svg";

    function svgElement(name, attributes) {
        const element = document.createElementNS(SVG, name);

        for (const [key, value] of Object.entries(attributes)) {
            element.setAttribute(key, value);
        }

        return element;
    }

    function arcPath(radius, centreY, from, to) {
        const point = angle => `${50 + radius * Math.cos(angle)} ${centreY + radius * Math.sin(angle)}`;
        return `M ${point(from)} A ${radius} ${radius} 0 ${to - from > Math.PI ? 1 : 0} 1 ${point(to)}`;
    }

    function gaugeView(body) {
        const svg = svgElement("svg", { viewBox: "0 0 100 92", width: "100%", height: "100%" });
        body.appendChild(svg);

        const start = Math.PI * 0.75;
        const sweep = Math.PI * 1.5;
        const centre = 54;

        function arc(radius, from, to, color, width) {
            if (to > from) {
                svg.appendChild(svgElement("path", { d: arcPath(radius, centre, from, to), fill: "none", stroke: color, "stroke-width": width }));
            }
        }

        return {
            update(panel) {
                const span = panel.maximum - panel.minimum;
                const at = value => start + sweep * (Math.min(Math.max(value, panel.minimum), panel.maximum) - panel.minimum) / span;
                const known = panel.value !== null && panel.value !== undefined;
                const color = bandColor(panel.bands, panel.value);
                let from = start;

                svg.textContent = "";

                for (const band of panel.bands) {
                    const to = at(band.limit);
                    arc(44, from, to, colorOf(band.color), 2);
                    from = to;
                }

                arc(36, start, start + sweep, "#2a2d33", 10);

                if (known) {
                    arc(36, start, at(panel.value), color, 10);
                }

                const text = svgElement("text", { x: 50, y: 60, fill: color, "font-size": 14, "font-weight": 500, "text-anchor": "middle" });
                text.textContent = withUnit(panel.value, panel.unit, panel.decimals);
                svg.appendChild(text);
            },
            resize() {},
            destroy() {},
        };
    }

    function donutView(body, legend) {
        const svg = svgElement("svg", { viewBox: "0 0 100 100", width: "100%", height: "100%" });
        body.appendChild(svg);

        return {
            update(panel) {
                const slices = panel.slices ?? [];
                const total = slices.reduce((sum, slice) => sum + slice.value, 0);
                let at = -Math.PI / 2;

                setEmpty(body, total === 0);
                svg.textContent = "";

                for (const slice of slices) {
                    const to = at + (slice.value / total) * Math.PI * 1.999;
                    const path = svgElement("path", { d: arcPath(34, 50, at + 0.012, to - 0.012), fill: "none", stroke: colorOf(slice.color), "stroke-width": 15 });
                    const label = `${slice.name}: ${withUnit(slice.value, panel.unit)}`;

                    path.addEventListener("mousemove", () => showTip(`<div><span>${escape(label)}</span></div>`));
                    path.addEventListener("mouseleave", hideTip);
                    svg.appendChild(path);
                    at = to;
                }

                legend.style.height = "auto";
                legend.innerHTML = "<table><tr><th>Name</th><th>Value</th><th>Percent</th></tr>"
                    + slices.map(slice => `<tr><td><i style="background:${colorOf(slice.color)}"></i>${escape(slice.name)}</td>`
                        + `<td>${escape(withUnit(slice.value, panel.unit))}</td><td>${(slice.value / total * 100).toFixed(1)}%</td></tr>`).join("")
                    + "</table>";
            },
            resize() {},
            destroy() {},
        };
    }

    // ---- Canvas panels

    function canvasFor(body) {
        const canvas = document.createElement("canvas");
        const context = canvas.getContext("2d");
        body.appendChild(canvas);

        return {
            canvas,
            context,
            size(width, height) {
                const ratio = devicePixelRatio;

                canvas.width = width * ratio;
                canvas.height = height * ratio;
                canvas.style.width = `${width}px`;
                canvas.style.height = `${height}px`;
                context.setTransform(ratio, 0, 0, ratio, 0, 0);
                context.font = AXIS_FONT;
                context.textBaseline = "middle";
            },
        };
    }

    const STOPS = [[0, [22, 30, 80]], [0.25, [32, 110, 170]], [0.5, [50, 180, 110]], [0.72, [245, 205, 40]], [0.87, [250, 130, 30]], [1, [230, 40, 50]]];

    const SHADES = Array.from({ length: 64 }, (_, index) => {
        const value = index / 63;
        let stop = 0;

        while (stop < STOPS.length - 2 && value > STOPS[stop + 1][0]) {
            stop++;
        }

        const [low, from] = STOPS[stop];
        const [high, to] = STOPS[stop + 1];
        const share = (value - low) / (high - low);
        return `rgb(${from.map((part, i) => Math.round(part + (to[i] - part) * share)).join(",")})`;
    });

    function heatView(element, body) {
        const surface = canvasFor(body);
        const { canvas, context } = surface;
        let panel = null;
        let width = 0;
        let height = 0;
        let area = null;

        function draw() {
            const heat = panel?.heat;

            if (!heat || width < 80 || height < 60) {
                return;
            }

            surface.size(width, height);
            context.textAlign = "right";

            const longest = Math.max(...heat.names.map(name => context.measureText(name).width));
            const left = Math.min(Math.ceil(longest) + 12, width * 0.3);
            area = { left, top: 6, right: width - 60, bottom: height - 24 };

            const columns = heat.columns;
            const rows = heat.names.length;
            const cellWidth = (area.right - area.left) / columns;
            const cellHeight = (area.bottom - area.top) / rows;
            const scale = heat.maximum > 0 ? heat.maximum : 1;

            for (let row = 0; row < rows; row++) {
                for (let column = 0; column < columns; column++) {
                    const value = heat.values[row * columns + column];

                    if (value !== null) {
                        // Square root, so one huge spike doesn't turn everything else the same colour.
                        context.fillStyle = SHADES[Math.round(Math.sqrt(value / scale) * 63)];
                        context.fillRect(area.left + column * cellWidth, area.top + row * cellHeight, cellWidth + 0.6, Math.max(cellHeight - 1, 1));
                    }
                }
            }

            context.fillStyle = "#ccccdc";
            heat.names.forEach((name, row) => context.fillText(name, area.left - 6, area.top + (row + 0.5) * cellHeight, area.left - 8));

            const ticks = Math.max(2, Math.min(7, Math.floor((area.right - area.left) / 120)));
            const span = heat.step * columns;
            const showDate = span > 86400;

            context.fillStyle = AXIS_TEXT;
            context.textAlign = "center";

            for (let tick = 0; tick < ticks; tick++) {
                const share = (tick + 0.5) / ticks;
                const date = new Date((heat.start + share * span) * 1000);
                const label = date.toLocaleString(undefined, showDate
                    ? { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" }
                    : { hour: "2-digit", minute: "2-digit" });

                context.fillText(label, area.left + share * (area.right - area.left), height - 10);
            }

            for (let shade = 0; shade < 64; shade++) {
                context.fillStyle = SHADES[63 - shade];
                context.fillRect(width - 52, area.top + (shade / 64) * (area.bottom - area.top), 10, (area.bottom - area.top) / 64 + 1);
            }

            context.fillStyle = AXIS_TEXT;
            context.textAlign = "left";
            context.fillText(axisNumber(heat.maximum), width - 38, area.top + 6);
            context.fillText(axisNumber(heat.maximum / 4), width - 38, (area.top + area.bottom) / 2);
            context.fillText("0", width - 38, area.bottom - 6);
        }

        function move(event) {
            const heat = panel?.heat;

            if (!heat || !area) {
                return;
            }

            const box = canvas.getBoundingClientRect();
            const column = Math.floor((event.clientX - box.left - area.left) / ((area.right - area.left) / heat.columns));
            const row = Math.floor((event.clientY - box.top - area.top) / ((area.bottom - area.top) / heat.names.length));

            if (column < 0 || row < 0 || column >= heat.columns || row >= heat.names.length) {
                hideTip();
                return;
            }

            const value = heat.values[row * heat.columns + column];
            showTip(`<b>${escape(heat.names[row])}</b>${tipRow("transparent", when(heat.start + column * heat.step), value === null ? "Not recorded" : withUnit(value, panel.unit))}`);
        }

        canvas.addEventListener("mousemove", move);
        canvas.addEventListener("mouseleave", hideTip);

        return {
            update(next) {
                panel = next;
                setEmpty(body, !next.heat);

                // Rows shouldn't stretch when there are few of them.
                const rows = next.heat?.names.length ?? 0;
                element.style.minHeight = `${rows === 0 ? 150 : Math.max(150, Math.min(next.height + 60, rows * 19 + 62))}px`;
                draw();
            },
            resize(nextWidth, nextHeight) {
                width = nextWidth;
                height = nextHeight;
                draw();
            },
            destroy() {},
        };
    }

    // Squarified, which keeps boxes close to square and so readable.
    function squarify(nodes, box) {
        const total = nodes.reduce((sum, node) => sum + node.value, 0);

        if (total <= 0 || box.w <= 0 || box.h <= 0) {
            return [];
        }

        const scale = (box.w * box.h) / total;
        const items = [...nodes].sort((a, b) => b.value - a.value).map(node => ({ node, area: node.value * scale }));
        const placed = [];
        let { x, y, w, h } = box;

        while (items.length > 0) {
            const side = Math.min(w, h);
            const row = [];
            let sum = 0;
            let worst = Infinity;

            while (items.length > 0) {
                const area = items[0].area;
                const nextSum = sum + area;
                const low = Math.min(area, ...row.map(item => item.area));
                const high = Math.max(area, ...row.map(item => item.area));
                const nextWorst = Math.max((side * side * high) / (nextSum * nextSum), (nextSum * nextSum) / (side * side * low));

                if (row.length > 0 && nextWorst > worst) {
                    break;
                }

                row.push(items.shift());
                sum = nextSum;
                worst = nextWorst;
            }

            const thickness = sum / side;
            let along = 0;

            for (const item of row) {
                const length = item.area / thickness;
                placed.push(w >= h
                    ? { node: item.node, x, y: y + along, w: thickness, h: length }
                    : { node: item.node, x: x + along, y, w: length, h: thickness });
                along += length;
            }

            if (w >= h) {
                x += thickness;
                w -= thickness;
            } else {
                y += thickness;
                h -= thickness;
            }
        }

        return placed;
    }

    function treeView(body) {
        const surface = canvasFor(body);
        const { canvas, context } = surface;
        let panel = null;
        let width = 0;
        let height = 0;
        let leaves = [];

        function label(box, text, value) {
            if (box.w > 52 && box.h > 30) {
                context.fillStyle = "#10131a";
                context.fillText(text, box.x + 4, box.y + 10, box.w - 8);
                context.fillText(value, box.x + 4, box.y + 24, box.w - 8);
            } else if (box.w > 40 && box.h > 14) {
                context.fillStyle = "#10131a";
                context.fillText(text, box.x + 3, box.y + box.h / 2, box.w - 6);
            }
        }

        function draw() {
            const nodes = panel?.tree ?? [];

            leaves = [];

            if (nodes.length === 0 || width < 40 || height < 40) {
                return;
            }

            surface.size(width, height);
            context.textAlign = "left";

            for (const group of squarify(nodes, { x: 1, y: 1, w: width - 2, h: height - 2 })) {
                const color = colorOf(group.node.color);
                const value = withUnit(group.node.value, panel.unit, panel.decimals);

                if (group.node.children.length === 0) {
                    context.fillStyle = color;
                    context.fillRect(group.x, group.y, Math.max(group.w - 2, 0.5), Math.max(group.h - 2, 0.5));
                    leaves.push({ ...group, group: null });
                    label(group, group.node.name, value);
                    continue;
                }

                const header = group.h > 34 ? 18 : 0;

                context.fillStyle = "#22252b";
                context.fillRect(group.x, group.y, Math.max(group.w - 2, 0.5), Math.max(group.h - 2, 0.5));

                if (header > 0) {
                    context.fillStyle = "#d8d9da";
                    context.fillText(`${group.node.name}  ${value}`, group.x + 4, group.y + 9, Math.max(group.w - 10, 1));
                }

                const inner = { x: group.x + 1, y: group.y + header + 1, w: Math.max(group.w - 4, 1), h: Math.max(group.h - header - 4, 1) };
                const count = group.node.children.length;

                squarify(group.node.children, inner).forEach((leaf, index) => {
                    context.globalAlpha = 1 - (index / count) * 0.55;
                    context.fillStyle = color;
                    context.fillRect(leaf.x, leaf.y, Math.max(leaf.w - 1, 0.5), Math.max(leaf.h - 1, 0.5));
                    context.globalAlpha = 1;
                    leaves.push({ ...leaf, group: group.node.name });
                    label(leaf, leaf.node.name, withUnit(leaf.node.value, panel.unit, panel.decimals));
                });
            }
        }

        function move(event) {
            const box = canvas.getBoundingClientRect();
            const x = event.clientX - box.left;
            const y = event.clientY - box.top;
            const leaf = leaves.find(item => x >= item.x && x < item.x + item.w && y >= item.y && y < item.y + item.h);

            if (!leaf) {
                hideTip();
                return;
            }

            showTip((leaf.group ? `<b>${escape(leaf.group)}</b>` : "") + tipRow(colorOf(leaf.node.color), leaf.node.name, withUnit(leaf.node.value, panel.unit, panel.decimals)));
        }

        canvas.addEventListener("mousemove", move);
        canvas.addEventListener("mouseleave", hideTip);

        return {
            update(next) {
                panel = next;
                setEmpty(body, (next.tree ?? []).length === 0);
                draw();
            },
            resize(nextWidth, nextHeight) {
                width = nextWidth;
                height = nextHeight;
                draw();
            },
            destroy() {},
        };
    }

    // ---- Tables

    function tableView(element, body) {
        const table = document.createElement("table");
        table.className = "board-table";
        body.appendChild(table);

        function cell(item) {
            const color = item.color ? ` style="color:${colorOf(item.color)}"` : "";

            if (item.server) {
                return `<td><a href="${escape(linkTo(item.server))}" target="_blank" rel="noopener">${escape(item.text)}</a></td>`;
            }

            return `<td${color}>${escape(item.time ? when(item.time) : item.text)}</td>`;
        }

        return {
            update(panel) {
                table.innerHTML = `<thead><tr>${panel.table.columns.map(name => `<th>${escape(name)}</th>`).join("")}</tr></thead>`
                    + `<tbody>${panel.table.rows.map(row => `<tr>${row.map(cell).join("")}</tr>`).join("")}</tbody>`;
            },
            resize() {},
            destroy() {},
        };
    }

    // ---- The page around the panels

    function setEmpty(body, empty) {
        let note = body.querySelector(".empty");

        if (empty && !note) {
            note = document.createElement("div");
            note.className = "empty";
            note.textContent = "No data in this range";
            body.appendChild(note);
        } else if (!empty && note) {
            note.remove();
        }
    }

    function linkTo(server) {
        const params = new URLSearchParams({ server, minutes: state.minutes });
        return `${location.pathname}?${params}`;
    }

    const bar = document.createElement("header");
    bar.className = "bar";
    bar.innerHTML = `
        <h1 id="title">Monitor</h1>
        <label id="serverPicker" hidden>Server <select id="server"></select></label>
        <span class="spacer"></span>
        <span id="note" role="status"></span>
        <button type="button" id="reset" hidden>Reset zoom</button>
        <label>Range <select id="range"></select></label>
        <label title="Turn off to hold the charts still while you study them."><input type="checkbox" id="live" checked> Live</label>`;
    root.appendChild(bar);

    const content = document.createElement("main");
    root.appendChild(content);

    const titleElement = bar.querySelector("#title");
    const serverPicker = bar.querySelector("#serverPicker");
    const serverSelect = bar.querySelector("#server");
    const rangeSelect = bar.querySelector("#range");
    const liveBox = bar.querySelector("#live");
    const resetButton = bar.querySelector("#reset");
    const note = bar.querySelector("#note");

    const sizes = new ResizeObserver(entries => {
        for (const entry of entries) {
            const view = views.get(entry.target.dataset.panel);
            const { clientWidth, clientHeight } = entry.target;

            if (view && clientWidth > 0 && clientHeight > 0) {
                view.view.resize(clientWidth, clientHeight);
            }
        }
    });

    function sectionFor(title) {
        let section = sections.get(title);

        if (section) {
            return section;
        }

        const element = document.createElement("section");
        const heading = document.createElement("button");
        const grid = document.createElement("div");

        heading.type = "button";
        heading.className = "section-title";
        heading.textContent = title;
        heading.setAttribute("aria-expanded", String(!closed.has(title)));
        grid.className = "grid";
        grid.hidden = closed.has(title);

        heading.addEventListener("click", () => {
            grid.hidden = !grid.hidden;
            heading.setAttribute("aria-expanded", String(!grid.hidden));

            if (grid.hidden) {
                closed.add(title);
            } else {
                closed.delete(title);
            }

            try {
                localStorage.setItem(CLOSED_KEY, JSON.stringify([...closed]));
            } catch {
                // Not saved, so it opens as it was next time.
            }
        });

        element.append(heading, grid);
        content.appendChild(element);
        section = { element, grid };
        sections.set(title, section);
        return section;
    }

    function makeView(panel, element, body, legend) {
        switch (panel.kind) {
            case "stat": return statView(body);
            case "gauge": return gaugeView(body);
            case "donut": return donutView(body, legend);
            case "heatmap": return heatView(element, body);
            case "treemap": return treeView(body);
            case "table": return tableView(element, body);
            default: return timeView(element, body, legend);
        }
    }

    function panelFor(panel, section) {
        const existing = views.get(panel.id);

        if (existing) {
            return existing;
        }

        const element = document.createElement("article");
        const title = document.createElement("div");
        const body = document.createElement("div");
        let legend = null;

        element.className = `panel ${panel.kind}`;
        element.style.setProperty("--span", panel.span);
        element.style.minHeight = `${panel.height}px`;
        title.className = "panel-title";
        title.textContent = panel.title;

        if (panel.hint) {
            title.title = panel.hint;
            title.classList.add("has-hint");
        }

        body.className = "panel-body";
        body.dataset.panel = panel.id;
        element.append(title, body);

        if (panel.kind === "time" || panel.kind === "donut") {
            legend = document.createElement("div");
            legend.className = "legend";
            element.appendChild(legend);
        }

        section.grid.appendChild(element);

        const entry = { element, body, view: makeView(panel, element, body, legend) };
        views.set(panel.id, entry);
        sizes.observe(body);
        return entry;
    }

    function removeStale(ids, titles) {
        for (const [id, entry] of views) {
            if (!ids.has(id)) {
                entry.view.destroy();
                sizes.unobserve(entry.body);
                entry.element.remove();
                views.delete(id);
            }
        }

        for (const [title, section] of sections) {
            if (!titles.has(title)) {
                section.element.remove();
                sections.delete(title);
            }
        }
    }

    function fillHeader(board) {
        document.title = `Monitor: ${board.title}`;
        titleElement.textContent = board.title;

        const options = board.servers.map(server => `<option value="${escape(server.id)}">${escape(server.name)}</option>`);

        if (board.servers.length > 1) {
            options.push('<option value="">All servers</option>');
        }

        const html = options.join("");

        if (serverSelect.dataset.html !== html) {
            serverSelect.innerHTML = html;
            serverSelect.dataset.html = html;
        }

        serverSelect.value = board.server;
        serverPicker.hidden = board.servers.length < 2;

        const ranges = RANGES.filter(range => range[1] <= board.retentionMinutes);
        const rangeHtml = ranges.map(range => `<option value="${range[1]}">${range[0]}</option>`).join("");

        if (rangeSelect.dataset.html !== rangeHtml) {
            rangeSelect.innerHTML = rangeHtml;
            rangeSelect.dataset.html = rangeHtml;
        }

        rangeSelect.value = state.minutes;
    }

    function drawBoard(board) {
        const ids = new Set();
        const titles = new Set();

        state.markers = board.markers;
        fillHeader(board);

        for (const section of board.sections) {
            const holder = sectionFor(section.title);
            titles.add(section.title);

            for (const panel of section.panels) {
                ids.add(panel.id);
                panelFor(panel, holder).view.update(panel);
            }
        }

        removeStale(ids, titles);
    }

    function address() {
        const params = new URLSearchParams();

        if (state.server !== null) {
            params.set("server", state.server);
        }

        if (state.from !== null) {
            params.set("from", Math.floor(state.from));
            params.set("to", Math.ceil(state.to));
        } else {
            params.set("minutes", state.minutes);
        }

        return `${api}?${params}`;
    }

    function delay() {
        if (!state.live || state.from !== null) {
            return 0;
        }

        // A long range barely changes in a minute, and it means reading a lot of history.
        if (state.minutes > MIDDLE_RANGE_MINUTES) {
            return SLOWEST_MS;
        }

        return state.server && state.minutes <= SHORT_RANGE_MINUTES ? LIVE_MS : SLOW_MS;
    }

    function schedule() {
        clearTimeout(state.timer);

        const wait = delay();

        if (wait > 0) {
            state.timer = setTimeout(refresh, wait);
        }
    }

    async function refresh() {
        if (document.hidden) {
            return;
        }

        const request = ++state.request;

        try {
            const response = await fetch(address(), { credentials: "same-origin" });
            const type = response.headers.get("content-type") ?? "";

            if (request !== state.request) {
                return;
            }

            if (!response.ok || !type.includes("json")) {
                note.textContent = response.status === 401 || response.redirected
                    ? "Signed out. Reload to sign in."
                    : response.status === 429
                        ? "Busy with other long ranges. Trying again."
                        : response.status === 404
                            ? "Monitor has no record of that server."
                            : "Monitor didn't answer. Trying again.";
            } else {
                const board = await response.json();

                if (request !== state.request) {
                    return;
                }

                state.server = board.server;
                drawBoard(board);
                note.textContent = state.from === null ? "" : "Zoomed. Double-click a chart to go back.";
            }
        } catch {
            if (request === state.request) {
                note.textContent = "Can't reach Monitor. Trying again.";
            }
        }

        if (request === state.request) {
            schedule();
        }
    }

    function zoom(start, end) {
        state.from = start;
        state.to = end;
        resetButton.hidden = false;
        hideTip();
        refresh();
    }

    function resetZoom() {
        if (state.from === null) {
            return;
        }

        state.from = null;
        state.to = null;
        resetButton.hidden = true;
        refresh();
    }

    function changed() {
        const params = new URLSearchParams();

        if (state.server !== null) {
            params.set("server", state.server);
        }

        params.set("minutes", state.minutes);
        history.replaceState(null, "", `${location.pathname}?${params}`);
        refresh();
    }

    serverSelect.addEventListener("change", () => {
        state.server = serverSelect.value;
        resetZoom();
        changed();
    });

    rangeSelect.addEventListener("change", () => {
        state.minutes = Number(rangeSelect.value);
        state.from = null;
        state.to = null;
        resetButton.hidden = true;
        changed();
    });

    liveBox.addEventListener("change", () => {
        state.live = liveBox.checked;
        schedule();

        if (state.live) {
            refresh();
        }
    });

    resetButton.addEventListener("click", resetZoom);

    document.addEventListener("visibilitychange", () => {
        if (!document.hidden && state.live) {
            refresh();
        }
    });

    refresh();
})();
