# Phase 0 results

Foundations, measured without a game server. Gate: zero allocations, and primitives within budget. **Passed.**

## Environment

AMD Ryzen 9 5950X, 16 cores, 32 threads, BenchmarkDotNet 0.15.8.

| Label | Platform |
|---|---|
| .NET 10 | Windows, .NET 10.0.14 |
| Framework | Windows, .NET Framework 4.8 |
| Linux | .NET 10 in Docker on WSL2, same machine, `tsc` clock source |

Linux here is a virtualized guest, not bare metal. Treat those figures as a compatibility check.

## Hot path through real Harmony output

Nanoseconds per call, including the loop and the virtual call. Every style was patched by Harmony, so inlining behaves as it will in the game.

| Style | .NET 10 | Framework | Linux | Allocated per call |
|---|---|---|---|---|
| Unpatched loop | 0.49 | 1.21 | 0.46 | 0 |
| Caller rebuilt by Harmony, no change | 1.40 | 1.19 | 1.29 | 0 |
| **Call site, gate closed** | **1.45** | **1.61** | **1.29** | **0** |
| Call site, 1 in 1024 timed | 2.73 | 3.63 | 2.42 | 0 |
| Call site, 1 in 8 timed | 8.15 | 10.87 | 8.57 | 0 |
| Call site, tree scope | 38.7 | 48.3 | 42.6 | 0 |
| Call site, every call timed | 43.7 | 57.0 | 48.0 | 0 |
| Call site, every call timed, chained clock | 25.4 | 28.9 | 26.4 | 0 |
| Method entry, static scope id | 39.2 | 48.3 | 43.0 | 0 |
| Method entry, `__originalMethod` lookup | 68.3 | 159.9 | 70.7 | 96–241 B |

## Primitives

| Operation (ns) | .NET 10 | Framework | Linux |
|---|---|---|---|
| Clock read | 18.3 | 22.0 | 20.3 |
| `DateTime.UtcNow` | 25.4 | 50.5 | 20.8 |
| Gate read | 0.13 | 0.12 | 0.16 |
| Sampler test | 0.87 | 1.75 | 0.70 |
| Thread block lookup (TLS) | 0.25 | 3.65 | 1.32 |
| Record entity, 100K entities, random order | 11.8 | 15.9 | 19.9 |
| Record key | 3.8 | 6.1 | 4.0 |
| Delegate key | 2.3 | 2.5 | 2.2 |
| Histogram record | 0.9 | 2.8 | 1.3 |
| Sampler advance | 1.1 | 1.3 | 1.3 |

## Gate

| Requirement | Target | Measured | Result |
|---|---|---|---|
| Hot path allocation | 0 | 0 on all three platforms, by test and by benchmark | Pass |
| Closed gate cost | ≤ 2 ns per wrapped call | 1.29–1.61 ns in total | Pass |
| Tree cost | ≤ 20 µs per frame | About 1.5–1.9 µs for 40 scopes, before the frame commit | On track |
| Detail memory | ≤ 96 MB | Default layout for 32 workers fits, by test | Pass |
| Tests | Pass | 73 of 73 on all three platforms | Pass |

## What this changed

- **Patch style.** The plan said method-entry patches cost more per call. Measured with the clock running, they don't: 39.2 ns against 38.7 ns, so the clock dominates. Call sites still win on closed-gate cost, inlining safety and fewer patched methods.
- **`__originalMethod` plus a dictionary** is the one style to avoid. It costs up to 3.3 times as much and allocates on every call.
- **Closed gate.** On .NET 10 nearly all of its cost comes from Harmony rebuilding the caller, which loses dynamic PGO, rather than from the gate.
- **Budget capacity.** At 1% of a frame (167 µs), the budget fits about 2,900–3,800 fully timed calls per frame, better than the 2,800 estimate.
- **Chained timestamps** cut full timing by 42–49%, raising that to about 5,800–6,600 calls. They only apply to sites timing every call. Phase 1 decides whether the accuracy holds.

## Verified risks

- **Access to internals.** The hook was executed against the real game DLLs on .NET 10 and .NET Framework. It runs with `IgnoresAccessChecksTo` in source. A control build without it fails with `FieldAccessException`.
- **Quasar build.** The companion builds with Quasar's exact arguments, and output contains only Monitor's DLLs.
- **Delegate identity.** Code pointer keys are stable across instances and after a method goes hot, on all three platforms.
- **Krafs.Publicizer 2.3.2.** Its setting for skipping its attribute source removes the wrong path, so the project uses `ExcludeAssets="contentFiles"`.

## Not covered

- These are microbenchmarks with one predictable call target. Real entity updates are polymorphic and colder.
- Real call volume is still unknown. It decides how much full timing a server can afford, and Phase 1 measures it.
- The frame commit, controller and Harmony load time aren't built or measured yet.
- Companion build was tested with `MagnetarBin` set directly, because no Magnetar install exists here for `$(Magnetar)` resolution.
