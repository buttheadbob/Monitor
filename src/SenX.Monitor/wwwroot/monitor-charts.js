// Draws Monitor's charts in the browser with uPlot on a canvas. The page only says what to show. Data
// comes over plain HTTP, so it never touches the page's live connection, and fetching stops while the
// tab is hidden. Quasar already loads uPlot on every page, so Monitor's own copy is only the fallback.

const SYNC_KEY = "senx-monitor";
const HEIGHT = 220;
const boards = new Map();
let nextBoard = 1;

function addScript(url) {
    return new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = url;
        script.onload = resolve;
        script.onerror = reject;
        document.head.appendChild(script);
    });
}

async function ensureLibrary(assetBase) {
    if (window.uPlot) {
        return;
    }

    const style = document.createElement("link");
    style.rel = "stylesheet";
    style.href = assetBase + "lib/uplot/uPlot.min.css";
    document.head.appendChild(style);

    await addScript(assetBase + "lib/uplot/uPlot.iife.min.js");
}

// Colours come from the host's palette, so a chart follows the theme. A hidden element resolves them.
function resolveColor(root, className) {
    const probe = document.createElement("span");
    probe.className = className;
    probe.style.color = "var(--monitor-color)";
    probe.hidden = true;
    root.appendChild(probe);

    const color = getComputedStyle(probe).color;
    probe.remove();
    return color;
}

function axisColors(root) {
    const style = getComputedStyle(root);
    return {
        text: style.getPropertyValue("--quasar-palette-text-secondary").trim() || "#9aa4b2",
        grid: style.getPropertyValue("--quasar-palette-table-lines").trim() || "rgba(128,128,128,0.24)",
    };
}

function format(value, unit) {
    if (value === null || value === undefined) {
        return "-";
    }

    const size = Math.abs(value);
    const decimals = size >= 100 ? 0 : size >= 10 ? 1 : 2;
    return value.toLocaleString(undefined, { maximumFractionDigits: decimals, minimumFractionDigits: decimals }) + unit;
}

function shownLines(chart) {
    return chart.lines.filter(line => line.overlay || line.values.some(value => (value ?? 0) >= chart.minimumToShow));
}

function isBand(chart, line) {
    return chart.stacked && !line.overlay;
}

// Bands sit on top of each other, so each line is the running total of those before it.
function stack(lines) {
    const totals = [];
    let below = null;

    for (const line of lines) {
        const total = line.values.map((value, index) => {
            if (value === null) {
                return null;
            }

            return value + (below === null ? 0 : below[index] ?? 0);
        });

        totals.push(total);
        below = total;
    }

    return totals;
}

function bandsFor(count) {
    const bands = [];

    for (let series = 1; series < count; series++) {
        bands.push({ series: [series + 1, series] });
    }

    return bands;
}

// Fades a line's colour down to nothing beneath it, the way Grafana's panels do.
function gradient(plot, color) {
    // uPlot asks once while it starts, before the plot has a size.
    if (!Number.isFinite(plot.bbox.top)) {
        return color;
    }

    const fill = plot.ctx.createLinearGradient(0, plot.bbox.top, 0, plot.bbox.top + plot.bbox.height);
    fill.addColorStop(0, `color-mix(in srgb, ${color} 32%, transparent)`);
    fill.addColorStop(1, `color-mix(in srgb, ${color} 2%, transparent)`);
    return fill;
}

// One row per name, drawn wherever a row has a value. The rows share the crosshair with other charts.
function timelineOptions(options, chart, axes) {
    const rows = chart.rows;

    options.height = heightOf(chart);
    options.legend = { show: false };
    options.scales.y = { range: [-0.6, rows.length - 0.4] };
    options.series = options.series.map((series, index) => index === 0 ? series : {
        ...series,
        paths: undefined,
        fill: undefined,
        width: 10,
        points: { show: true, size: 9, fill: series.stroke, stroke: series.stroke },
    });
    options.axes[1] = {
        stroke: axes.text,
        grid: { show: false },
        ticks: { show: false },
        size: 130,
        splits: () => rows.map((name, index) => index),
        values: (plot, ticks) => ticks.map(tick => rows[tick] ?? ""),
    };
}

function heightOf(chart) {
    return chart.rows ? 44 + (chart.rows.length * 20) : HEIGHT;
}

// Dashed lines for saves, restarts and plugin or mod changes, the same on every chart on the board.
function drawMarkers(board, plot) {
    const ratio = window.uPlot.pxRatio;
    const { left, top, width, height } = plot.bbox;
    const context = plot.ctx;

    context.save();
    context.lineWidth = ratio;
    context.setLineDash([4 * ratio, 4 * ratio]);

    for (const marker of board.markers) {
        const x = plot.valToPos(marker.time, "x", true);

        if (x >= left && x <= left + width) {
            context.strokeStyle = marker.stroke;
            context.beginPath();
            context.moveTo(x, top);
            context.lineTo(x, top + height);
            context.stroke();
        }
    }

    context.restore();
}

// Resolving a colour touches the page, so each one is done once.
function markerLines(board, markers) {
    const strokes = new Map();

    return markers.map(marker => {
        if (!strokes.has(marker.color)) {
            strokes.set(marker.color, resolveColor(board.root, marker.color));
        }

        return { time: marker.time, stroke: strokes.get(marker.color) };
    });
}

function createPlot(board, entry, container, chart, lines, colors) {
    const axes = axisColors(board.root);
    const raw = lines.map(line => line.values);
    const secondScale = lines.some(line => line.secondScale);

    const series = [{}].concat(lines.map((line, index) => ({
        label: line.name,
        scale: line.secondScale ? "ratio" : "y",
        stroke: colors[index],
        width: isBand(chart, line) ? 1 : 1.5,
        dash: line.dashed ? [6, 4] : line.dotted ? [2, 3] : undefined,
        points: { show: false },
        paths: line.dashed || line.dotted ? undefined : window.uPlot.paths.spline(),
        fill: isBand(chart, line) ? colors[index] : line.overlay ? undefined : plot => gradient(plot, colors[index]),
        alpha: isBand(chart, line) ? 0.85 : 1,
        // A band holds a running total, so the legend reads the real value.
        value: (plot, value, seriesIndex, dataIndex) =>
            format(isBand(chart, line) && dataIndex !== null ? raw[seriesIndex - 1][dataIndex] : value, line.unit ?? chart.unit),
    })));

    const options = {
        width: Math.max(container.clientWidth, 200),
        height: HEIGHT,
        cursor: { sync: { key: SYNC_KEY }, drag: { x: true, y: false } },
        scales: {
            x: { time: true },
            y: {
                range: (plot, min, max) => [0, chart.maximum ?? (max > 0 ? max * 1.1 : 1)],
            },
            ratio: { range: [0, 1.05] },
        },
        axes: [
            { stroke: axes.text, grid: { stroke: axes.grid }, ticks: { stroke: axes.grid } },
            {
                stroke: axes.text,
                grid: { stroke: axes.grid },
                ticks: { stroke: axes.grid },
                size: 56,
                values: (plot, ticks) => ticks.map(tick => format(tick, "")),
            },
        ].concat(secondScale ? [{
            scale: "ratio",
            side: 1,
            stroke: axes.text,
            grid: { show: false },
            ticks: { stroke: axes.grid },
            size: 44,
            values: (plot, ticks) => ticks.map(tick => tick.toFixed(1)),
        }] : []),
        series,
        bands: chart.stacked ? bandsFor(lines.filter(line => isBand(chart, line)).length) : undefined,
        hooks: {
            setSelect: [plot => {
                if (plot.select.width > 0) {
                    entry.zoomed = true;
                }
            }],
            draw: [plot => drawMarkers(board, plot)],
        },
    };

    if (chart.rows) {
        timelineOptions(options, chart, axes);
    }

    const plot = new window.uPlot(options, dataFor(chart, lines), container);

    // uPlot's double click zooms back out, and live updates follow the latest data again.
    plot.over.addEventListener("dblclick", () => {
        entry.zoomed = false;
    });

    entry.raw = raw;
    return plot;
}

// Bands come first, so overlays draw over them and series numbers match the bands.
function dataFor(chart, lines) {
    const bands = lines.filter(line => isBand(chart, line));
    const others = lines.filter(line => !isBand(chart, line)).map(line => line.values);
    return [chart.times].concat(stack(bands), others);
}

function destroy(entry) {
    entry.resize?.disconnect();
    entry.plot?.destroy();
}

function draw(board, chart) {
    const container = board.root.querySelector(`[data-chart="${chart.id}"]`);
    if (!container) {
        return;
    }

    const lines = shownLines(chart);
    const empty = lines.length === 0 || chart.times.length === 0;
    const note = board.root.querySelector(`[data-chart-empty="${chart.id}"]`);

    if (note) {
        note.hidden = !empty;
    }

    let entry = board.charts.get(chart.id);

    if (empty) {
        if (entry) {
            destroy(entry);
            board.charts.delete(chart.id);
        }

        return;
    }

    const colors = lines.map(line => resolveColor(board.root, line.color));
    const key = JSON.stringify([lines.map(line => line.name), colors, axisColors(board.root)]);

    // Same lines in the same order just get new data. Anything else needs a new chart.
    if (entry && entry.key === key) {
        entry.raw.splice(0, entry.raw.length, ...lines.map(line => line.values));
        entry.plot.setData(dataFor(chart, lines), !entry.zoomed);
        return;
    }

    if (entry) {
        destroy(entry);
    }

    entry = { key, zoomed: false, raw: [] };
    entry.plot = createPlot(board, entry, container, chart, lines, colors);
    entry.resize = new ResizeObserver(() => {
        entry.plot.setSize({ width: Math.max(container.clientWidth, 200), height: heightOf(chart) });
    });
    entry.resize.observe(container);
    board.charts.set(chart.id, entry);
}

async function refresh(board) {
    // Blazor can take the element away before it asks to stop, so a board also stops itself.
    if (!board.root.isConnected) {
        stop(board.id);
        return;
    }

    if (board.busy || !board.url || document.hidden) {
        return;
    }

    board.busy = true;
    const url = board.url;

    try {
        const response = await fetch(url, { credentials: "same-origin" });

        if (response.ok && url === board.url) {
            const data = await response.json();
            board.markers = markerLines(board, data.markers);

            for (const chart of data.charts) {
                draw(board, chart);
            }
        }
    } catch (error) {
        // A dropped connection isn't worth showing on the page, and the next refresh tries again.
        console.warn("Monitor couldn't refresh its charts.", error);
    } finally {
        board.busy = false;
    }
}

// Returns an id, because the page may no longer be able to name the element when it stops.
export async function start(root, assetBase) {
    await ensureLibrary(assetBase);

    const id = nextBoard++;
    boards.set(id, { id, root, charts: new Map(), markers: [], url: null, timer: 0, busy: false });
    return id;
}

// Called whenever what's shown changes.
export function show(id, url, refreshMs) {
    const board = boards.get(id);
    if (!board) {
        return;
    }

    if (board.url !== url) {
        for (const entry of board.charts.values()) {
            destroy(entry);
        }

        board.charts.clear();
    }

    const sameCharts = board.url === url;

    board.url = url;
    clearInterval(board.timer);

    if (refreshMs > 0) {
        board.timer = setInterval(() => refresh(board), refreshMs);
    }

    // Holding still shouldn't redraw what's being studied.
    if (refreshMs > 0 || !sameCharts) {
        refresh(board);
    }
}

export function stop(id) {
    const board = boards.get(id);
    if (!board) {
        return;
    }

    clearInterval(board.timer);

    for (const entry of board.charts.values()) {
        destroy(entry);
    }

    boards.delete(id);
}
