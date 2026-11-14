# uPlot against ECharts on full dashboards

Median of 5 cold page loads each.

| Measure | uPlot min | uPlot full | ECharts min | ECharts full | Mixed min | uPlot min, no GPU | ECharts min, no GPU | Mixed min, no GPU | ECharts vs uPlot | Mixed vs uPlot |
|---|---|---|---|---|---|---|---|---|---|---|
| File size | 50 KB | 147 KB | 1,096 KB | 3,757 KB | 1,145 KB | 50 KB | 1,096 KB | 1,145 KB | 22.0x | 23.0x |
| Load the libraries | 15.9 ms | 17.1 ms | 54.2 ms | 62.6 ms | 67.0 ms | 16.6 ms | 55.4 ms | 71.0 ms | 3.4x | 4.2x |
| 32 panels, 4,320 points: first draw | 95.6 ms | 96.9 ms | 291.7 ms | 299.4 ms | 95.9 ms | 285.7 ms | 451.3 ms | 270.6 ms | 3.1x | 1.0x |
| 32 panels, 4,320 points: one refresh of every panel | 204.4 ms | 200.1 ms | 249.7 ms | 218.6 ms | 216.6 ms | 247.0 ms | 389.7 ms | 254.0 ms | 1.2x | 1.1x |
| 32 panels, 4,320 points: JS memory | 11.9 MB | 22.7 MB | 44.8 MB | 47.4 MB | 17.6 MB | 22.5 MB | 46.4 MB | 17.1 MB | 3.8x | 1.5x |
| 32 panels, 4,320 points: frames a second, hovering | 60.1 fps | 60.3 fps | 50.6 fps | 53.1 fps | 60.2 fps | 75.3 fps | 54.9 fps | 75.3 fps | 0.8x | 1.0x |
| 32 panels, 4,320 points: page thread busy, hovering | 9.1 % | 4.4 % | 24.5 % | 21.2 % | 11.9 % | 32.4 % | 79.0 % | 33.8 % | 2.7x | 1.3x |
| 32 panels, 4,320 points: frames a second, scrolling | 60.3 fps | 60.3 fps | 60.3 fps | 60.3 fps | 60.3 fps | 73.4 fps | 53.8 fps | 73.0 fps | 1.0x | 1.0x |
| 32 panels, 4,320 points: page thread busy, scrolling | 5.9 % | 0.0 % | 7.5 % | 1.9 % | 7.6 % | 47.0 % | 47.7 % | 51.2 % | 1.3x | 1.3x |
| 32 panels, 4,320 points: all browser CPU, first draw | 468.8 ms | 437.5 ms | 781.2 ms | 859.4 ms | 468.8 ms | 390.6 ms | 765.6 ms | 359.4 ms | 1.7x | 1.0x |
| 32 panels, 4,320 points: all browser CPU, refreshes | 4,875.0 ms | 4,281.2 ms | 7,250.0 ms | 6,625.0 ms | 4,937.5 ms | 5,125.0 ms | 6,968.8 ms | 4,968.8 ms | 1.5x | 1.0x |
| 32 panels, 4,320 points: all browser CPU, 5 s hovering | 6,125.0 ms | 5,890.6 ms | 9,109.4 ms | 8,734.4 ms | 6,312.5 ms | 5,968.8 ms | 7,015.6 ms | 6,015.6 ms | 1.5x | 1.0x |
| 32 panels, 4,320 points: all browser CPU, 5 s scrolling | 7,234.4 ms | 6,843.8 ms | 7,281.2 ms | 7,234.4 ms | 7,046.9 ms | 12,312.5 ms | 11,781.2 ms | 12,250.0 ms | 1.0x | 1.0x |
| 38 panels with heatmaps and treemaps, 4,320 points: first draw | 247.3 ms | 193.8 ms | 567.4 ms | 489.0 ms | 456.8 ms | 311.0 ms | 776.0 ms | 638.2 ms | 2.3x | 1.8x |
| 38 panels with heatmaps and treemaps, 4,320 points: one refresh of every panel | 216.9 ms | 199.9 ms | 517.4 ms | 443.4 ms | 395.5 ms | 265.0 ms | 627.5 ms | 473.3 ms | 2.4x | 1.8x |
| 38 panels with heatmaps and treemaps, 4,320 points: JS memory | 14.1 MB | 13.5 MB | 86.0 MB | 86.0 MB | 58.3 MB | 12.5 MB | 88.0 MB | 59.0 MB | 6.1x | 4.1x |
| 38 panels with heatmaps and treemaps, 4,320 points: frames a second, hovering | 60.3 fps | 60.3 fps | 50.7 fps | 53.2 fps | 60.3 fps | 75.2 fps | 52.8 fps | 75.3 fps | 0.8x | 1.0x |
| 38 panels with heatmaps and treemaps, 4,320 points: page thread busy, hovering | 7.5 % | 4.8 % | 27.5 % | 30.0 % | 11.9 % | 33.0 % | 88.1 % | 52.9 % | 3.7x | 1.6x |
| 38 panels with heatmaps and treemaps, 4,320 points: frames a second, scrolling | 60.3 fps | 60.3 fps | 60.3 fps | 60.3 fps | 60.3 fps | 66.5 fps | 41.7 fps | 55.1 fps | 1.0x | 1.0x |
| 38 panels with heatmaps and treemaps, 4,320 points: page thread busy, scrolling | 0.0 % | 0.0 % | 3.7 % | 5.3 % | 3.2 % | 47.1 % | 51.5 % | 53.3 % | n/a | n/a |
| 38 panels with heatmaps and treemaps, 4,320 points: all browser CPU, first draw | 421.9 ms | 359.4 ms | 1,296.9 ms | 1,390.6 ms | 1,218.8 ms | 484.4 ms | 1,546.9 ms | 1,234.4 ms | 3.1x | 2.9x |
| 38 panels with heatmaps and treemaps, 4,320 points: all browser CPU, refreshes | 3,109.4 ms | 2,687.5 ms | 9,968.8 ms | 8,187.5 ms | 6,968.8 ms | 2,984.4 ms | 9,359.4 ms | 7,515.6 ms | 3.2x | 2.2x |
| 38 panels with heatmaps and treemaps, 4,320 points: all browser CPU, 5 s hovering | 5,921.9 ms | 5,734.4 ms | 9,015.6 ms | 8,750.0 ms | 5,984.4 ms | 5,812.5 ms | 6,187.5 ms | 6,140.6 ms | 1.5x | 1.0x |
| 38 panels with heatmaps and treemaps, 4,320 points: all browser CPU, 5 s scrolling | 5,718.8 ms | 5,484.4 ms | 5,953.1 ms | 6,140.6 ms | 5,906.2 ms | 10,531.2 ms | 10,671.9 ms | 10,578.1 ms | 1.0x | 1.0x |
| 32 panels, 60,000 points: first draw | 1,181.6 ms | 1,148.5 ms | 1,045.7 ms | 1,002.8 ms | 1,174.8 ms | 970.1 ms | 1,217.6 ms | 981.4 ms | 0.9x | 1.0x |
| 32 panels, 60,000 points: one refresh of every panel | 1,167.2 ms | 1,136.3 ms | 1,113.9 ms | 1,064.3 ms | 1,132.7 ms | 923.3 ms | 1,287.5 ms | 970.1 ms | 1.0x | 1.0x |
| 32 panels, 60,000 points: JS memory | 57.3 MB | 57.4 MB | 300.9 MB | 303.8 MB | 63.2 MB | 57.2 MB | 301.9 MB | 63.3 MB | 5.3x | 1.1x |
| 32 panels, 60,000 points: frames a second, hovering | 60.3 fps | 60.3 fps | 51.6 fps | 53.6 fps | 60.3 fps | 75.2 fps | 55.5 fps | 75.2 fps | 0.9x | 1.0x |
| 32 panels, 60,000 points: page thread busy, hovering | 10.4 % | 9.8 % | 27.1 % | 25.3 % | 12.7 % | 25.0 % | 80.8 % | 32.9 % | 2.6x | 1.2x |
| 32 panels, 60,000 points: frames a second, scrolling | 60.3 fps | 60.3 fps | 60.3 fps | 60.3 fps | 60.3 fps | 74.2 fps | 54.7 fps | 74.8 fps | 1.0x | 1.0x |
| 32 panels, 60,000 points: page thread busy, scrolling | 0.0 % | 0.0 % | 0.0 % | 5.0 % | 0.1 % | 38.3 % | 47.9 % | 46.6 % | n/a | n/a |
| 32 panels, 60,000 points: all browser CPU, first draw | 1,265.6 ms | 1,296.9 ms | 1,812.5 ms | 1,781.2 ms | 1,296.9 ms | 1,015.6 ms | 2,078.1 ms | 1,093.8 ms | 1.4x | 1.0x |
| 32 panels, 60,000 points: all browser CPU, refreshes | 4,937.5 ms | 5,015.6 ms | 7,171.9 ms | 6,921.9 ms | 5,171.9 ms | 3,890.6 ms | 7,453.1 ms | 4,109.4 ms | 1.5x | 1.0x |
| 32 panels, 60,000 points: all browser CPU, 5 s hovering | 5,671.9 ms | 5,640.6 ms | 8,937.5 ms | 8,765.6 ms | 5,671.9 ms | 5,812.5 ms | 6,046.9 ms | 5,828.1 ms | 1.6x | 1.0x |
| 32 panels, 60,000 points: all browser CPU, 5 s scrolling | 5,671.9 ms | 5,609.4 ms | 5,375.0 ms | 5,593.8 ms | 5,453.1 ms | 10,531.2 ms | 11,046.9 ms | 10,609.4 ms | 0.9x | 1.0x |
| 38 panels with heatmaps and treemaps, 60,000 points: first draw | 1,250.3 ms | 1,257.5 ms | 1,738.2 ms | 1,820.0 ms | 1,817.2 ms | 1,036.4 ms | 1,895.7 ms | 1,693.2 ms | 1.4x | 1.5x |
| 38 panels with heatmaps and treemaps, 60,000 points: one refresh of every panel | 1,241.5 ms | 1,240.4 ms | 1,783.0 ms | 1,831.2 ms | 1,773.0 ms | 1,004.0 ms | 1,999.0 ms | 1,630.1 ms | 1.4x | 1.4x |
| 38 panels with heatmaps and treemaps, 60,000 points: JS memory | 58.4 MB | 58.5 MB | 425.1 MB | 426.8 MB | 219.8 MB | 58.4 MB | 425.1 MB | 226.3 MB | 7.3x | 3.8x |
| 38 panels with heatmaps and treemaps, 60,000 points: frames a second, hovering | 60.3 fps | 60.3 fps | 52.0 fps | 51.7 fps | 60.1 fps | 75.3 fps | 54.6 fps | 75.3 fps | 0.9x | 1.0x |
| 38 panels with heatmaps and treemaps, 60,000 points: page thread busy, hovering | 10.6 % | 11.7 % | 22.2 % | 30.8 % | 9.7 % | 33.7 % | 86.7 % | 48.1 % | 2.1x | 0.9x |
| 38 panels with heatmaps and treemaps, 60,000 points: frames a second, scrolling | 60.3 fps | 60.3 fps | 60.3 fps | 60.1 fps | 60.3 fps | 67.5 fps | 42.3 fps | 55.5 fps | 1.0x | 1.0x |
| 38 panels with heatmaps and treemaps, 60,000 points: page thread busy, scrolling | 0.0 % | 0.9 % | 0.0 % | 1.8 % | 0.0 % | 50.5 % | 49.9 % | 51.3 % | n/a | n/a |
| 38 panels with heatmaps and treemaps, 60,000 points: all browser CPU, first draw | 1,453.1 ms | 1,468.8 ms | 3,468.8 ms | 3,296.9 ms | 3,187.5 ms | 1,109.4 ms | 3,406.2 ms | 2,906.2 ms | 2.4x | 2.2x |
| 38 panels with heatmaps and treemaps, 60,000 points: all browser CPU, refreshes | 5,656.2 ms | 5,546.9 ms | 13,328.1 ms | 12,843.8 ms | 9,781.2 ms | 4,281.2 ms | 15,140.6 ms | 8,671.9 ms | 2.4x | 1.7x |
| 38 panels with heatmaps and treemaps, 60,000 points: all browser CPU, 5 s hovering | 5,609.4 ms | 5,656.2 ms | 8,453.1 ms | 8,500.0 ms | 6,531.2 ms | 5,796.9 ms | 6,109.4 ms | 6,562.5 ms | 1.5x | 1.2x |
| 38 panels with heatmaps and treemaps, 60,000 points: all browser CPU, 5 s scrolling | 5,796.9 ms | 5,671.9 ms | 6,750.0 ms | 6,500.0 ms | 5,593.8 ms | 10,796.9 ms | 10,500.0 ms | 10,546.9 ms | 1.2x | 1.0x |

## What the heatmaps and treemaps cost

The page with them, less the same page without.

| Measure | uPlot min | uPlot full | ECharts min | ECharts full | Mixed min | uPlot min, no GPU | ECharts min, no GPU | Mixed min, no GPU |
|---|---|---|---|---|---|---|---|---|
| First draw, 4,320 points | +151.7 ms | +96.9 ms | +275.7 ms | +189.6 ms | +360.9 ms | +25.3 ms | +324.7 ms | +367.6 ms |
| One refresh, 4,320 points | +12.5 ms | -0.2 ms | +267.7 ms | +224.8 ms | +178.9 ms | +18.0 ms | +237.8 ms | +219.3 ms |
| JS memory, 4,320 points | +2.2 MB | -9.1 MB | +41.2 MB | +38.6 MB | +40.7 MB | -10.1 MB | +41.5 MB | +41.9 MB |
| All browser CPU, first draw, 4,320 points | -46.9 ms | -78.1 ms | +515.6 ms | +531.2 ms | +750.0 ms | +93.8 ms | +781.2 ms | +875.0 ms |
| All browser CPU, refreshes, 4,320 points | -1,765.6 ms | -1,593.8 ms | +2,718.8 ms | +1,562.5 ms | +2,031.2 ms | -2,140.6 ms | +2,390.6 ms | +2,546.9 ms |
| First draw, 60,000 points | +68.7 ms | +109.0 ms | +692.5 ms | +817.2 ms | +642.4 ms | +66.3 ms | +678.1 ms | +711.8 ms |
| One refresh, 60,000 points | +74.3 ms | +104.1 ms | +669.1 ms | +766.9 ms | +640.3 ms | +80.7 ms | +711.5 ms | +660.0 ms |
| JS memory, 60,000 points | +1.2 MB | +1.2 MB | +124.2 MB | +123.0 MB | +156.6 MB | +1.1 MB | +123.2 MB | +163.0 MB |
| All browser CPU, first draw, 60,000 points | +187.5 ms | +171.9 ms | +1,656.2 ms | +1,515.6 ms | +1,890.6 ms | +93.8 ms | +1,328.1 ms | +1,812.5 ms |
| All browser CPU, refreshes, 60,000 points | +718.8 ms | +531.2 ms | +6,156.2 ms | +5,921.9 ms | +4,609.4 ms | +390.6 ms | +7,687.5 ms | +4,562.5 ms |

## Proof that real charts were drawn

| Page | Pixels drawn | Canvas pixels |
|---|---|---|
| ECharts full: heavy | 2,235,505 | 7,215,639 |
| ECharts full: heavyExtras | 5,834,829 | 16,611,369 |
| ECharts full: real | 2,244,792 | 7,215,639 |
| ECharts full: realExtras | 5,843,891 | 16,611,369 |
| ECharts min, no GPU: heavy | 2,236,670 | 7,215,639 |
| ECharts min, no GPU: heavyExtras | 5,836,000 | 16,611,369 |
| ECharts min, no GPU: real | 2,245,918 | 7,215,639 |
| ECharts min, no GPU: realExtras | 5,845,023 | 16,611,369 |
| ECharts min: heavy | 2,235,505 | 7,215,639 |
| ECharts min: heavyExtras | 5,834,829 | 16,611,369 |
| ECharts min: real | 2,244,792 | 7,215,639 |
| ECharts min: realExtras | 5,843,891 | 16,611,369 |
| Mixed min, no GPU: heavy | 1,787,417 | 5,587,728 |
| Mixed min, no GPU: heavyExtras | 5,386,747 | 14,983,458 |
| Mixed min, no GPU: real | 1,776,739 | 5,587,728 |
| Mixed min, no GPU: realExtras | 5,375,844 | 14,983,458 |
| Mixed min: heavy | 1,786,685 | 5,587,728 |
| Mixed min: heavyExtras | 5,386,009 | 14,983,458 |
| Mixed min: real | 1,776,008 | 5,587,728 |
| Mixed min: realExtras | 5,375,107 | 14,983,458 |
| uPlot full: heavy | 1,786,685 | 5,587,728 |
| uPlot full: heavyExtras | 5,288,233 | 9,705,174 |
| uPlot full: real | 1,776,008 | 5,587,728 |
| uPlot full: realExtras | 5,277,556 | 9,705,174 |
| uPlot min, no GPU: heavy | 1,787,417 | 5,587,728 |
| uPlot min, no GPU: heavyExtras | 5,288,965 | 9,705,174 |
| uPlot min, no GPU: real | 1,776,739 | 5,587,728 |
| uPlot min, no GPU: realExtras | 5,278,287 | 9,705,174 |
| uPlot min: heavy | 1,786,685 | 5,587,728 |
| uPlot min: heavyExtras | 5,288,233 | 9,705,174 |
| uPlot min: real | 1,776,008 | 5,587,728 |
| uPlot min: realExtras | 5,277,556 | 9,705,174 |
