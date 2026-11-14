# Chart libraries: uPlot and ECharts

Monitor's charts need to reach the level of a Grafana dashboard, and heatmaps and treemaps would help break a cost down into its parts. uPlot has no heatmap or treemap, so this tests what each way of getting them costs, on the kind of page that would actually ship.

Grafana's own time series panels use uPlot, the same version Monitor ships (1.6.32), and ECharts isn't a Grafana dependency. So the quality of Grafana's line charts comes from panel design, not from a different library.

**Decision:** uPlot for time series, and Monitor's own canvas code for heatmaps and treemaps, with gauges and donuts staying as own SVG. ECharts isn't used. See [Why](#why).

## What was tested

Three versions of one dashboard, modelled on a Grafana server dashboard and [Charts.md](Charts.md):

| App | Time series | Gauges and donuts | Heatmaps and treemaps |
|---|---|---|---|
| **uPlot** | uPlot 1.6.32 | Own SVG, as Monitor has now | Own canvas code |
| **ECharts** | ECharts 6.1.0 | ECharts | ECharts |
| **Mixed** | uPlot 1.6.32 | Own SVG | ECharts 6.1.0 |

Every panel has what a real one carries: a title, a legend with min, max, avg and current, units, threshold lines, gradient fills, smooth curves and a tooltip on hover. The page has 32 panels: 6 stat tiles with sparklines, 4 threshold gauges, 14 line charts, 3 stacked areas, 2 bar charts and 3 donuts. The extras add 3 labelled heatmaps and 3 nested treemaps, for 38.

Each page ran at two sizes:

- **4,320 points a series:** 3 days at one point a minute, which is Monitor's default history.
- **60,000 points a series:** a stress test. The heatmaps also grow from 14,400 cells to 60,000, and the treemaps from 200 parts to 2,000.

Screenshots of the same page, with generated data, were compared too. They stay out of the repository, because they're large and show only generated data.

ECharts got its documented best case for large data: no animation, no point symbols, LTTB sampling, large mode for bars, and columns in a dataset. Both libraries drew smooth curves.

### How it was measured

- Real charts in a real, visible Chrome 156 window, 1920 by 1040, on one cold page load each.
- 5 repeats of each configuration, taking turns so neither got a warmer machine. Tables show the median. All runs finished.
- **Page timings** cover the page's own thread up to a painted frame.
- **All browser CPU** adds up every Chrome process, including the GPU process, because the page can't see what happens after it hands off its drawing.
- **No GPU** runs turn hardware drawing off, which puts all of it on the CPU. That's also what a server or VM with no graphics card does.
- A separate run for each configuration read the canvases back to prove real pixels were drawn.

The benchmark's source is kept outside this repository, in `ChartBench`, because it needs both libraries beside it.

The machine was an AMD Ryzen 9 5950X with an AMD Radeon RX 7900 XTX and 64 GB of RAM, running Windows 11. That's much faster than a typical admin's laptop, so absolute times would be higher elsewhere.

## Results

Every figure is from [the full results](chart-benchmark/full-results.md).

### File size

| | uPlot | ECharts | Mixed |
|---|---|---|---|
| Minified | 50 KB | 1,096 KB | 1,145 KB |
| Compressed as sent to a browser (Brotli) | 20 KB | 299 KB | 319 KB |
| Time to load | 16 ms | 54 ms | 67 ms |

The unminified builds were also tested, at 147 KB and 3,757 KB. Apart from size, they weren't meaningfully different from the minified ones, so they aren't shown again here.

### A full page of 32 panels at 4,320 points

This is the case closest to real use.

| | uPlot | ECharts | ECharts is |
|---|---|---|---|
| First draw | 96 ms | 292 ms | 3.1× slower |
| Refresh every panel | 204 ms | 250 ms | 1.2× slower |
| JS memory | 12 MB | 45 MB | 3.8× more |
| Hovering, frames a second | 60 | 51 | 16% fewer |
| Hovering, page thread busy | 9% | 25% | 2.7× more |
| Hovering for 5 s, all browser CPU | 6,125 ms | 9,109 ms | 1.5× more |
| Scrolling, frames a second | 60 | 60 | The same |

### A stress page of 32 panels at 60,000 points

| | uPlot | ECharts | ECharts is |
|---|---|---|---|
| First draw | 1,182 ms | 1,046 ms | **12% faster** |
| Refresh every panel | 1,167 ms | 1,114 ms | **5% faster** |
| First draw, all browser CPU | 1,266 ms | 1,813 ms | 1.4× more |
| JS memory | 57 MB | 301 MB | 5.3× more |
| Hovering, frames a second | 60 | 52 | 14% fewer |

ECharts draws slightly faster here, because its sampling thins each line before drawing while uPlot's smooth curves work through every point. It costs far more memory to do it, and more CPU overall.

### With no graphics card

| 38 panels at 4,320 points | uPlot | ECharts | Mixed |
|---|---|---|---|
| Scrolling, frames a second | 67 | 42 | 55 |
| Hovering, frames a second | 75 | 53 | 75 |
| Hovering, page thread busy | 33% | 88% | 53% |

### What the heatmaps and treemaps cost

The 38-panel page less the 32-panel page, at 60,000 points.

| Added by the 6 extra panels | Own canvas | ECharts app | Mixed |
|---|---|---|---|
| First draw | +69 ms | +692 ms | +642 ms |
| One refresh | +74 ms | +669 ms | +640 ms |
| JS memory | +1 MB | +124 MB | +157 MB |
| First draw, all browser CPU | +188 ms | +1,656 ms | +1,891 ms |
| Refreshes, all browser CPU | +719 ms | +6,156 ms | +4,609 ms |

Own canvas code costs about a tenth of what ECharts does for the same 6 panels. Part of that is real work ECharts does that the own code doesn't: see the limits below.

## Limits of this test

- **It doesn't compare looks or features.** How a chart looks is a judgement, so it's left to the pictures. ECharts' heatmaps and treemaps come with more built in, such as zooming and drilling down, and the own code has only drawing and a tooltip. Adding those would cost more, in code as well as time.
- **Hand-written drawing has to be written and looked after.** That's a cost these numbers don't show.
- **The 4,320 point extras figures aren't used above.** Each run's first page is drawn with the JavaScript engine still cold, and that's always the 32-panel page, so its figures read high and the difference reads low, even negative. The 60,000 point pages come later, so that comparison is fair.
- **Work on the graphics card itself isn't counted.** Only the CPU side of drawing is, which is why the no GPU runs are there.
- **Windows reports CPU time in 15.6 ms steps,** so very small differences mean nothing.
- **The data is generated:** slow waves with a few rounded peaks. Real server data may draw differently.
- **One machine, one browser.** Chrome only, on a fast desktop, with 5 repeats. The spread between repeats was small, with first draw and memory within about 10%.
- **Real Grafana asks for about as many points as a panel has pixels,** which is cheaper than either size here. Monitor could do the same.

## Why

- **uPlot for time series.** It's what Grafana uses, it's cheaper on a page like Monitor's, and the look comes from panel design, which is independent of the library.
- **Own canvas code for heatmaps and treemaps.** It costs about a tenth of ECharts and adds nothing to download. The tradeoff is that anything beyond drawing and a tooltip, such as zooming or drilling down, has to be written here.
- **None of this runs on the game server.** Every cost here is in the admin's browser.
