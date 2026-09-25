# 5090 Benchmark Article — Plan

Goal: publish a benchmarking write-up for the RTX 5090 setup, in the spirit of
[pankaj uvāca's Qwen 3.8 / MLX piece](https://medium.com/@pankaj-uvacha/benchmarking-qwen-3-8-7c21d7817718)
but as the CUDA / Blackwell counterpart.

## Decisions locked

- **Angle**: three threads woven together —
  1. **NVFP4 on Blackwell** — native FP4 + 5th-gen tensor cores as the 5090 story
  2. **MTP self-speculation, characterized rather than toggled** — see below; every
     Qwen3.8-27B GGUF on this box self-speculates by default and there's no HTTP-reachable
     way to turn it off, so the thread is "how much does it help and how much does it swing,"
     not a controlled on/off pair
  3. **Dense vs MoE under the 32 GB ceiling** — Qwen3.8-27B dense vs Ornith-1.5-35B-A3B MoE,
     what fits and at what context length
- **Quality**: manual rubric grading of transcripts (no LLM-judge build)
- **Tool work**: prefill tokens/s + power draw/tokens-per-joule — DONE, verified live

## DECISION LOG

- **2026-09-15 — KV-cache-matched bridge rerun done; two of the four decomposition
  metrics FLIP once KV cache is actually controlled.** Reused
  `results/speed-20260914-234838.csv` (Qwen NVFP4, f16 KV, `--speculative-type mtp` —
  this is literally the MTP-on half of the on/off test above, no need to rerun it) and
  launched `unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M` via the same CLI method
  (`--speculative-type mtp --cache-type-k f16 --cache-type-v f16`), ran the same 5-task
  ×3-repeat speed pass + 4-step context probe (`config.udq4km-f16.json`). Zero errors.

  | | Qwen NVFP4 (f16 KV) | Qwen UD-Q4_K_M (f16 KV) | vs. the old (KV-mismatched) numbers |
  |---|---|---|---|
  | Decode tok/s avg | 117.26 | **130.32** | Same direction (UD-Q4_K_M faster), gap slightly wider |
  | Avg power | **253.7 W** | 281.0 W | Same direction (NVFP4 lower), nearly identical magnitude |
  | Tokens/joule | 0.557 | **0.590** | **FLIPPED** — was NVFP4 0.591 > UD-Q4_K_M 0.544 (~9% NVFP4 lead); now UD-Q4_K_M leads by ~6% |
  | Context ceiling | 69,120 | **85,248** | **FLIPPED, hard** — was NVFP4 180,480 > UD-Q4_K_M 145,920; now UD-Q4_K_M leads by ~23% |
  | Prefill @8K→64K | **4362→2158** | 3400→1750 | Same direction, same magnitude (~1.20–1.28× NVFP4), i.e. **this one was never a KV-cache artifact** |

  Both absolute NVFP4 numbers also moved a lot from the original (unmatched) bridge run
  — context ceiling 180,480 (unknown KV, presumably quantized) → 69,120 (explicit f16) is
  almost entirely the KV-cache-dtype effect, not a property of the model. **Takeaway**:
  prefill throughput and the power/decode-speed direction were robust to the KV-cache
  confound; tokens/joule and context ceiling were NOT — those two conclusions in the
  original Finding #3 write-up were wrong in direction, not just imprecise, and have to
  be corrected, not just caveated. **Ornith's own KV cache dtype during its original run
  is still unknown/unrecorded** — this rerun only cleans up the Qwen-vs-Qwen half of the
  decomposition; the Ornith-vs-Qwen comparison still carries that caveat and wasn't
  re-run (out of scope — Ornith's own settings weren't the thing flagged). Files:
  `results/speed-20260915-000900.csv` (+ context-probe, same timestamp).
- **2026-09-15 — Got the real MTP on/off comparison, finally, via the `unsloth` CLI
  (`unsloth studio run`).** Every prior session assumed there was no way to control
  speculative decoding on the actual server — that assumption was wrong. `unsloth
  studio run` is the real, scriptable server launcher (same server type the GUI runs),
  with `--speculative-type <off|mtp|...>` on the running server itself, and unknown
  flags pass straight through to llama-server, exposing `--cache-type-k`/
  `--cache-type-v` (KV cache dtype) too. This was found by asking the user to check
  Studio's Run Settings GUI (which surfaced `--speculative-type` and `KV Cache Dtype:
  q8_0` for the UD-Q4_K_M bridge run — an unrecorded, previously-invisible variable in
  that comparison, see below), then checking whether the CLI exposed the same controls
  — it does, and lets them be recorded exactly, not inferred from GUI screenshots.
  **Procedure**: closed the Desktop GUI (it and the CLI both want port 8888), launched
  `unsloth studio run --model esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF --gguf-variant
  Qwen3.8-27B-NVFP4-MTP-HIGH --speculative-type off --cache-type-k f16 --cache-type-v
  f16 --port 8888 --api-only`, confirmed via the model's own llama-server log that no
  `common_speculative_init_result` line appeared and the `blk.64.nextn.*` tensors were
  explicitly logged "unused — ignoring" (first `-ctv` attempt failed — Unsloth's arg
  parser truncated it to `-ct`, not a valid llama-server flag; the long-form
  `--cache-type-v` worked), ran the 5-task/3-repeat speed pass + a 4-step context probe
  (`config.mtp-off.json`), then stopped that instance, relaunched identically except
  `--speculative-type mtp`, confirmed the draft-init log line WAS present this time,
  ran the same battery (`config.mtp-on.json`). Same weights, same KV cache dtype, same
  tasks, same sampling — the only variable that changed was the flag.
  **Result — the real MTP effect, isolated**: decode tok/s **65.72 avg (off) →
  117.26 avg (on), +78.4%**; avg power **308.6 W (off) → 253.7 W (on)** — MTP uses
  *less* power while producing *more* tokens; tokens/joule **0.240 → 0.557, 2.32×
  more efficient**; context ceiling **92,672 (off) → 69,120 (on), ~25% lower** — the
  draft/NextN sidecar's own VRAM footprint eats into what would otherwise be KV cache.
  Draft acceptance on this specific matched run: 70.7–100%, mean draft length
  2.41–3.00 tokens. Files: `results/speed-20260914-234513.csv` (+ context-probe, off),
  `results/speed-20260914-234838.csv` (+ context-probe, on).
  **This supersedes Finding #2's "characterize the variance, no clean comparison
  possible" framing** — there IS a clean comparison now, and it should be the
  headline of that section, with the always-on/no-HTTP-control/session-drift material
  kept as the reason nobody gets this for free, not as the whole finding.
- **2026-09-15 — KV cache dtype was an unrecorded, uncontrolled variable in the
  UD-Q4_K_M bridge run.** The user checked Studio's Run Settings GUI for that model and
  found `KV Cache Dtype: q8_0` — confirmed active during the actual 2026-09-14/15
  bridge-point benchmark. The benchmark tool has no visibility into or control over
  Studio's Run Settings (KV cache dtype, speculative decoding, draft tokens, VRAM
  budget) — only what's sent in the OpenAI-compatible request body, which doesn't cover
  any of these. So the Finding #3 quant-format decomposition (Qwen NVFP4 vs Qwen
  UD-Q4_K_M) had KV cache dtype as a second, invisible, unrecorded variable on top of
  weight quant format — unknown whether the NVFP4 and Ornith runs also used q8_0 or the
  f16 default. **Not yet re-run with KV cache controlled and matched** — the CLI launch
  method discovered directly above (`--cache-type-k`/`--cache-type-v`) now makes this
  fixable; a follow-up bridge-point rerun with explicit, matched, recorded KV cache
  dtype should happen before the quant-format decomposition numbers are treated as final.
- **2026-09-15 — Quality regrade: the DI-refactor "quality gap" was partly a grading
  bug, and it's basically gone once fixed.** Ran the full `config.article.json` battery
  against a third model, `unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M` (the quant-format bridge
  point for Finding #3 — see the next entry down for why it exists). All 3 of its
  `csharp-refactor-di` attempts used `throw new NotImplementedException();` for the
  concrete repo/sender classes — the exact thing Ornith's weakest DI attempt was docked
  for as "dropping working logic." Re-reading the prompt: it never shows those classes'
  bodies, so there was never logic to drop for either model. Worse: Qwen NVFP4's own DI
  response (scored 90, no deduction) uses `{ /* ... */ }` placeholder bodies on
  non-`void` methods — an actual **CS0161 compile error** that the original grading
  pass missed while penalizing Ornith's compiling equivalent. **Corrected**: Qwen NVFP4
  DI 90→85, Ornith DI attempt-1 78→88 (attempts 2–3 unchanged at 82 — the "bare 2-line
  excerpt instead of a standard Program.cs" criticism is real and separate). **New
  5-task averages: Qwen NVFP4 96.0 (was 97.0), Ornith 95.5 (was 94.9) — a wash, not a
  "dense wins on quality" story.** UD-Q4_K_M leads at 98.6. Full explanation and the
  corrected table are in `docs/quality-rubric.md`'s "Regrade" section — **the article's
  quality framing needs a real rewrite, not a footnote**, in both `article-draft.md` and
  the readout artifact. This also means the TL;DR bullet and Bottom Line's "dense model
  wins on quality" line are now wrong and need to change.
- **2026-09-15 — UD-Q4_K_M full battery run, restarted once after a hang.** First
  attempt hung mid-run when a different local harness loaded/unloaded a model on the
  same Unsloth Desktop instance while the benchmark had an in-flight agent-concurrency
  request — the request never got a response and the run stalled indefinitely (no
  timeout on that path). Stopped the hung background task, confirmed the server was
  reachable and GPU was idle, reran from scratch — completed cleanly, zero errors, on
  the second attempt. **Lesson for later runs: don't drive Unsloth Desktop from two
  harnesses at once — loading/unloading a model kills whatever request the benchmark
  has in flight, and the tool doesn't currently time out or retry that case.** Results:
  126.9 tok/s avg (113.0–141.0 range), 282.2W avg power (peak 553.1W), VRAM
  27.04–27.24 GB, tokens/joule 0.544, context ceiling **145,920 tokens** (yet another
  distinct value from this same quant's two earlier readings this session — 137,728 on
  the hung attempt, 124,928/180,480 for the NVFP4 model, 179,968/262,144 for Ornith —
  reinforces the "don't treat any ceiling as fixed" caveat already in the plan). Agent
  concurrency: 47.0→78.4→99.7→119.9 tok/s aggregate across 1/2/4/8 agents. Workflow:
  ~22.5s solo → ~30.2s at 2 parallel → ~46.3s at 4 parallel. Files:
  `results/speed-20260914-230614.csv` (+ context-probe/agent-concurrency/workflow with
  the same timestamp).
- **2026-09-15 — quant-format confound acknowledged, UD-Q4_K_M added as a bridge
  point.** Finding #3 ("Dense vs MoE") was never actually quant-controlled — it compares
  Qwen at NVFP4 against Ornith at Q4_K_M, changing architecture AND quant format at
  once. The original exclusion of `UD-Q4_K_M` from the lineup ("keeps NVFP4 vs
  quant-format comparisons out of scope") didn't hold up under scrutiny: that reasoning
  only protects a "clean NVFP4 comparison" that Finding #3 never had in the first place.
  Since Ornith has no NVFP4/GGUF release to run in this stack (confirmed 2026-09-14 —
  the official `ornith-ai/Ornith-1.5-35B-A3B-NVFP4` is safetensors-only, built for
  vLLM/SGLang, no `.gguf` files), the fix is to add `UD-Q4_K_M` (same dense Qwen
  architecture as the NVFP4 entry, a much closer-to-matched k-quant vs Ornith's
  Q4_K_M) as a bridge: Qwen NVFP4 vs Qwen UD-Q4_K_M isolates the quant-format effect on
  identical architecture; Qwen UD-Q4_K_M vs Ornith Q4_K_M is then a closer (not
  perfect — UD-Q4_K_M's dynamic per-layer precision still isn't identical to plain
  Q4_K_M) dense-vs-MoE comparison. Config: `config.article-udq4km.json`.
- **2026-09-14 — DI-refactor quality gap does NOT generalize; revise the framing.**
  Added a 6th task (`bash-script-extend`, in `config.verify-completeness.json`) targeting
  the same two failure modes seen on `csharp-refactor-di` (dropping real logic, giving an
  excerpt instead of the complete artifact) but in a different domain (bash/terminal, not
  C#). Result: **both models scored 100/100, all 3 attempts each** — Ornith even added
  unrequested defensive touches (a `|| true` guard on curl, an empty-backup check) that
  Qwen didn't. So the DI gap is a specific weak spot on that one task, not a general
  "Ornith drops logic / gives excerpts" reliability pattern. **The article's quality
  section needs to say this explicitly** — don't let the 97.0-vs-94.9 aggregate imply a
  broader completeness deficit than the data supports. Full rubric + numbers in
  `docs/quality-rubric.md`'s "Follow-up" section; raw run is
  `results/quality-transcripts-20260914-213746.txt`.
  - Side effect caught while running this: `tools/ChartBuilder/Program.cs` was pulled into
    the root `LlmBenchmark.csproj`'s default compile glob (both live under the repo root),
    breaking `dotnet build`/`dotnet run` for the CLI entirely. Fixed with a
    `<Compile Remove="tools\**" />` exclusion (mirroring the existing Core/Dashboard/AppHost
    excludes) — anyone who only ever ran `dotnet run --project tools/ChartBuilder` directly
    wouldn't have noticed. Confirmed `dotnet build LlmBenchmark.slnx` is clean again.
- **2026-09-14 — OpenClaw dropped from article scope entirely.** `config.openclaw.json`
  deleted; the "third config, prepared but not runnable" paragraph removed from the draft.
  The article is now a clean two-model comparison (Qwen3.8-27B NVFP4 vs
  Ornith-1.5-35B-A3B), which is also all the full run ever actually covered — nothing here
  changes the data or findings, just drops a forward-looking mention of work that was
  never going to complete on this box.
- **2026-09-11 — Ornith also self-speculates; last week's framing is outdated.** Full-run
  llama-server logs show BOTH models drafting: Qwen NVFP4-MTP now runs a steady, high
  **93–97% draft acceptance** with short ~2.9-token drafts (up from last week's noisy
  7.8–79.2% — something in the Studio catalog/settings shifted between sessions).
  Ornith-1.5-35B-A3B, previously assumed to run with no drafter, is ALSO self-speculating —
  **51.6–100% acceptance with a mean draft length of 34–65 tokens**, far longer chains than
  Qwen's. That's what's driving Ornith's wide 187–410 tok/s spread this run, not model
  instability. Revise the dense-vs-MoE framing: it's not "dense+MTP vs MoE, no drafter" —
  both self-speculate, with visibly different drafting strategies (short/steady vs
  long/variable). The catalog also drifted: `esatapedico`'s quant tag changed from the
  short `HIGH` to the full `Qwen3.8-27B-NVFP4-MTP-HIGH` (the old id silently hangs warmup
  now), and both models' device-fit context ceilings grew (Qwen 124,928→180,480; Ornith
  179,968→262,144, now hitting full native). **Treat every number as a snapshot of that
  session's Studio state, not a fixed property of the model/quant** — re-verify the live
  catalog before reusing any id or before citing "the" ceiling/accept-rate for a model.
- **2026-09-04 — no-drafter baseline dropped.** Every GGUF of Qwen3.8-27B tried on this box
  self-speculates via its built-in NextN/MTP head, and Unsloth Studio has it ON by default for
  every catalog entry:
  - `esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF:HIGH` — draft acceptance swung **7.8%–79.2%**
    across requests, mean draft length **6–49 tokens** (highly variable)
  - `unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M` — draft acceptance **83–100%**, mean draft length
    **2.7–3.0 tokens** (steadier, shorter drafts — looks like a different speculative mode)
  There is no OpenAI-API-reachable switch for this — only Studio's own UI. So the article
  reports MTP as **always-on and variable**, not an on/off comparison. This is itself a
  finding worth a section: naive "MTP gives you 2x" claims don't hold up — the speedup is a
  function of draft-acceptance rate, which swings per-request and per-repo/quant.

## Reference article shape (target structure)

1. Hook — new models / architectures, "which should I actually run"
2. TL;DR — 3–4 takeaways
3. Lineup & Test Setup — models, quant, methodology
4. Finding #1 — NVFP4 / Blackwell throughput baseline
5. Finding #2 — MTP self-speculation: always-on, variable accept rate, what that does to tok/s
6. Finding #3 — Dense vs MoE (decode t/s, prefill t/s, VRAM, context ceiling)
7. Finding #4 (our differentiator) — agent/concurrency throughput
8. Bottom Line — recommendations
9. Methodology footer

## Model lineup (current — three models, two configs)

- **Qwen3.8-27B NVFP4** (`esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF:Qwen3.8-27B-NVFP4-MTP-HIGH`)
  — dense, MTP self-speculation always on. Primary NVFP4-on-Blackwell subject.
- **Ornith-1.5-35B-A3B** (`ornith-ai/Ornith-1.5-35B-A3B-GGUF:Q4_K_M`) — MoE, ~3B active.
  Primary dense-vs-MoE comparison subject.
- **Qwen3.8-27B UD-Q4_K_M** (`unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M`) — same dense
  architecture as the NVFP4 entry, different (k-quant) format. Added 2026-09-15 as a
  quant-format bridge point once it became clear Finding #3 was never quant-controlled
  and Ornith has no usable NVFP4 release (see decision log). Full battery, same rigor as
  the other two.
- Smaller (<8B) models: still out of scope, no thread needs them.

Server: Unsloth Desktop (`http://localhost:8888/v1`, `UNSLOTH_API_KEY` env var). Verified
live against its `/v1/models` catalog repeatedly — it drifts between sessions, always
re-check before reusing an id.

- [x] `config.article.json` — Qwen NVFP4 + Ornith, deterministic sampling, 5 quality
      tasks, context probe to 196K, agent concurrency + workflow.
- [x] `config.article-udq4km.json` — same battery, UD-Q4_K_M only. Bridge-point run,
      see below.

## Local inference tooling on this box (found 2026-09-03)

- **No standalone `llama-cli.exe`.** The CLI is `unsloth.exe`
  (`C:\Users\moono\.unsloth\studio\bin`, on PATH): `inference`, `chat`, `start <agent>`,
  `train`, `export`; carries `--speculative-type` and `--tensor-parallel` (a separate
  inference path from the Studio server the benchmark actually hits).
- llama.cpp server: `C:\Users\moono\.unsloth\llama.cpp\build\bin\Release\llama-server.exe`
  — v0.3.0-dev build **10715** (commit 92cedc867), CUDA 13, Unsloth build. Shim exe +
  `*-impl.dll`; `.unsloth\llama.cpp` is a full source checkout (build `llama-cli` from it if needed).
- Also present: Ollama (`ollama.exe` on PATH + bundled llama-server), Atomic Chat's llama.cpp
  backend. FreeToken `bin\` empty. OpenClaw Local AI **not installed yet**; gateway = Node on
  `127.0.0.1:18789`.

## Tool changes (Core/) — DONE, verified live on the 5090 (2026-09-04)

- [x] **Prefill t/s** — `PrefillTokensPerSecond` = promptTokens / TTFT, in `SpeedResult` +
      `speed-*.csv`. Blank when no first-token signal. `StreamedChatResult.FirstTokenSeen` added.
- [x] **Power sampling** — `GpuSampler` in `GpuMonitor.cs` polls
      `power.draw,utilization.gpu,clocks.sm` every 500 ms around each speed request;
      `AvgPowerW`, `PeakPowerW`, `TokensPerJoule` added to `speed-*.csv` (blank if nvidia-smi
      gives no board power). `TokensPerJoule` = completion tok / (avg W × gen seconds).
- [x] README metrics table + notes updated; dashboard gets prefill / power / tokens-per-joule charts
- [x] Sanity-checked live: AvgPowerW 179-381W across two models, PeakPowerW up to 459W
      (plausible for a 575W-TGP 5090), all columns populate correctly.
- **CAVEAT**: at short task-pass prompt sizes (43-320 tokens), TTFT is ~2.1s regardless of
  prompt size — fixed dispatch overhead, not prefill compute. Speed-pass
  `PrefillTokensPerSecond` (20-78 t/s) is NOT a meaningful prefill-throughput number at this
  scale — real prefill throughput comes from the **context probe** (large prompts amortize
  the fixed overhead). Don't use the speed-pass number for the article's headline prefill claim.

## Superseded: 2026-09-04 smoke-pass numbers

2-task/2-repeat smoke data from the first session (124,928-tok Qwen ceiling, 179,968-tok
Ornith ceiling, Ornith framed as "no drafter, steady") is **superseded by the full run
below** — the catalog and Studio's default speculative settings had visibly drifted by
2026-09-11. Kept only in git history / the earlier readout artifact version, not restated here.

## Full run results — 2026-09-11 (`config.article.json`, 5 tasks × 3 repeats × 2 models)

Server: Unsloth Desktop, same box. Zero errors across speed pass, context probe, agent
concurrency, and the 3-stage workflow. Files: `results/speed-20260911-200157.csv`,
`context-probe-20260911-200157.csv`, `agent-concurrency-20260911-200157.csv` (+`-summary`),
`agent-workflow-pipelines-20260911-200157.csv` (+`-stages`).

**Qwen3.8-27B NVFP4** (`esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF:Qwen3.8-27B-NVFP4-MTP-HIGH`
— note the quant tag changed, see decision log):
- Speed pass (15 requests): **122.6 tok/s avg** (106.3–140.5 range — steadier than last
  week). AvgPowerW 257.5 avg (109–429 range), PeakPowerW up to 503W. VRAM 26.10–26.14 GB.
  Tokens/joule avg **0.591**.
- Context probe: device ceiling **180,480 tokens** (native 262,144). Prefill throughput
  (tokens/duration): 4332 @8K, 2880 @16K, 3481 @32K, 3241 @64K, 2648 @96K, 2245 @128K
  (196K skipped, exceeds ceiling).
- Draft acceptance (llama-server log): **93–97%**, mean draft length ~2.9 tokens — steady
  and high this session.

**Ornith-1.5-35B-A3B MoE** (`ornith-ai/Ornith-1.5-35B-A3B-GGUF:Q4_K_M`):
- Speed pass (15 requests): **232.4 tok/s avg** (187.0–410.6 range — wide, see below).
  AvgPowerW 173.1 avg (84–269 range), PeakPowerW up to 349W. VRAM 28.53–28.59 GB.
  Tokens/joule avg **1.544** — ~2.6× Qwen's.
- Context probe: device ceiling **262,144 tokens** — full native, up from 179,968 last
  week. Prefill throughput: 7621 @8K, 4031 @16K, 5381 @32K, 5709 @64K, 4829 @96K, 4132
  @128K, 3480 @192K.
- Draft acceptance (llama-server log): **51.6–100%**, mean draft length **34–65 tokens**
  — self-speculating too (see decision log), with much longer/more variable draft chains
  than Qwen's. This is what's driving the wide tok/s spread, not model instability.

**Net (NVFP4 vs Q4_K_M, the original two-model comparison)**: Ornith beats Qwen on every
axis — 1.9× the avg decode tok/s, 67% of the power draw, 2.6× the tokens/joule, and
1.4–2.3× the prefill throughput across matched context sizes, plus a context ceiling 45%
higher. But see the bridge-point run below before treating this as a clean
architecture-only result — it isn't quant-controlled.

## Bridge-point run — 2026-09-15, SUPERSEDED same day by the KV-matched rerun below

**Qwen3.8-27B UD-Q4_K_M**, first attempt (`unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M`, launched
via Studio's GUI, KV cache dtype `q8_0` — confirmed only after the fact, see decision
log): 126.9 tok/s avg speed pass, 282.2W avg power, 0.544 tok/J, context ceiling 145,920
this session. **Do not cite this table** — see the corrected one below; kept here only as
the historical record of what the unmatched-KV comparison looked like.

## Bridge-point run, KV-matched — 2026-09-15 (`config.udq4km-f16.json`)

Both sides now explicit and recorded: `--cache-type-k f16 --cache-type-v f16
--speculative-type mtp`, launched via `unsloth studio run` (the NVFP4 side reuses
`results/speed-20260914-234838.csv` from the MTP on/off test, which used identical
settings). Full arithmetic and the two FLIPPED metrics are in the decision log above —
summary:

| | Qwen NVFP4 (f16 KV) | Qwen UD-Q4_K_M (f16 KV) |
|---|---|---|
| Decode tok/s avg | 117.26 | **130.32** |
| Avg power | **253.7 W** | 281.0 W |
| Tokens/joule | 0.557 | **0.590** |
| Context ceiling | 69,120 | **85,248** |
| Prefill tok/s (8K→64K) | **4362→2158** | 3400→1750 |

Two things held up from the original (unmatched) comparison: prefill throughput
(NVFP4 ~1.20–1.28× faster at every size — confirmed NOT a KV-cache artifact, since it's
consistent whether KV is matched or not) and the direction of decode-speed/power
(UD-Q4_K_M slightly faster, NVFP4 lower power). **Two flipped**: tokens/joule (was NVFP4
+9%, now UD-Q4_K_M +6%) and context ceiling (was NVFP4 +24%, now UD-Q4_K_M +23%) — both
were mostly measuring the KV-cache-dtype difference, not the weight-quant difference.

**The Ornith comparison is unaffected by this fix and still carries its own open
caveat**: Ornith's KV cache dtype during its original run is unknown/unrecorded, so any
"Ornith vs Qwen" ratio (in either the old or new table) still has one uncontrolled side.
This rerun only cleaned up the Qwen-vs-Qwen half of the decomposition. The old
"controlling for quant format widened Ornith's advantage" framing (comparing Ornith
against the *unmatched* UD-Q4_K_M numbers) should not be repeated — recompute against
matched numbers, or drop that specific claim, when writing this up.

**Agent concurrency** (Qwen model, Microsoft Agent Framework path): aggregate tok/s scales
51.7 (1 agent) → 76.3 (2) → 95.5 (4) → 111.9 (8) tok/s — sub-linear as expected
(memory-bandwidth bound), but real batching benefit. AvgTtftMs grows alongside it: ~2.1s →
2.8s → 4.1s → 6.9s. (First x1 repeat showed an anomalous 19.4s TTFT / 8.9 tok/s — a
cold-start artifact, excluded from the above; kept in the raw CSV.)

**Agent workflow** (Planner→Coder→Reviewer, Qwen model): 1 pipeline ~23.8s avg;
2 parallel pipelines ~31.2s avg wall-clock; 4 parallel ~57.7s avg — sub-linear scaling,
each additional 2x of concurrent pipeline load costs roughly +30-80% wall-clock, not +100%.

**UD-Q4_K_M agent concurrency/workflow** (for reference, same shape as above): aggregate
tok/s 47.0 (1) → 78.4 (2) → 99.7 (4) → 119.9 (8) — same sub-linear pattern, slightly
higher absolute numbers than the NVFP4 run at 4/8-way. Workflow: ~22.5s solo →
~30.2s at 2 parallel → ~46.3s at 4 parallel — also sub-linear, broadly consistent with
the NVFP4 numbers. Not a major finding on its own; included for completeness since the
full battery was run.

`GET /v1/models/{id}` on Unsloth Desktop returns per-model `context_length` /
`max_context_length` / `native_context_length` / `loaded` — confirmed live, matches what
`OpenAiClient.GetModelContextLimitAsync` already reads.

## Work remaining

### Full runs
- [x] Run `config.article.json` properly (repeatsPerTask=3, full 5-task set, agent
      concurrency + workflow) for both Qwen and Ornith — **done 2026-09-11**, see above
- [x] Fix environment for the record: driver, CUDA, power limit, resizable BAR, no other
      GPU load — **done 2026-09-14**, see Setup section content below

### Quality rubric — DONE 2026-09-11, REGRADED 2026-09-15 (see decision log)
- [x] Kept the 5 scenarios as-is (coding-focused, matches the tool's niche)
- [x] Rubric written: `docs/quality-rubric.md` — per-task pass criteria + 0–100 scoring
- [x] Graded all 30 transcripts from the full run into `results/quality-scores.csv`
- [x] Follow-up generalization check (bash-script-extend, both models 100/100)
- [x] Third model's transcripts (UD-Q4_K_M, 15 more) graded; surfaced and fixed a real
      grading inconsistency — see decision log and `docs/quality-rubric.md`'s "Regrade" section
- **CORRECTED result: Qwen NVFP4 96.0 avg, Ornith 95.5 avg, UD-Q4_K_M 98.6 avg — the
  Qwen-vs-Ornith gap is a wash, not a "dense wins on quality" story.** What's real:
  Ornith under-delivered the literal "standard Program.cs" ask in 2 of 3 DI attempts,
  a narrow completeness gap, not a general logic-dropping pattern. Everything else
  (JSON schema, cross-file bug reasoning, needle extraction, tool-call format) all three
  models scored 92-100. **This supersedes the original 97.0-vs-94.9 framing — do not
  cite that number or the "dense wins on quality" line anywhere in the article.**

### Charts — regenerated 2026-09-15 after the regrade
- [x] `quality-grouped-bar.png` regenerated — `BuildQualityGroupedBar()` reads
      `results/quality-scores.csv` unfiltered by run timestamp, so simply re-running
      `dotnet run --project tools/ChartBuilder` picked up the corrected DI scores
      (85/84 now, was 90/80.7) with no code changes needed. Verified visually: bars
      correctly near-identical now, honestly showing the wash.
- [x] Other 8 charts regenerated too (deterministic re-run) — byte-identical output,
      confirming their source data (the fixed `speed-20260911-200157.csv` etc.) never
      changed and didn't need to.
- **Decision**: kept all 9 charts as the original 2-model (Qwen NVFP4 vs Ornith)
      comparison — did NOT add UD-Q4_K_M as a third series. The tool has no UD-Q4_K_M
      model-id constant or CSV wiring for the 2026-09-15 run
      (`speed-20260914-230614.csv` etc.) and adding a third series to all 9 chart
      functions would be a real rewrite, not a re-run. The bridge-point comparison is
      carried by the markdown decomposition tables already in `article-draft.md`'s
      Finding #3 instead. Revisit if the article ends up wanting a visual for that too.
- [x] Script: `tools/ChartBuilder` (standalone C# console app, ScottPlot 5 + CsvHelper,
      not part of the shipped app). `dotnet run --project tools/ChartBuilder` reads the
      2026-09-11 run CSVs + `results/quality-scores.csv` and writes 9 PNGs to
      `docs/charts/`. Colors are consistent across all charts (blue = Qwen, orange =
      Ornith). Re-run it if the run timestamp or CSV shape ever changes — `RunTimestamp`
      is a top-of-file const in `Program.cs`.
- [x] Charts (9, one more than the original 6-item list — split watts/tokens-per-joule
      into two single-metric bars instead of one dual-axis bar, and added workflow-pipeline
      scaling alongside agent-concurrency scaling since Finding #4 covers both): decode
      t/s bar · power-draw bar · tokens-per-joule bar · MTP accept-rate range (floating
      bars — no per-request CSV exists, values are the hand-read log ranges) · prefill t/s
      grouped bar (context-probe-derived) · context-vs-VRAM line · quality grouped bar ·
      agent-concurrency scaling (dual-axis: tok/s + TTFT) · workflow-pipeline scaling.
      All 9 embedded in `docs/article-draft.md` at their matching Finding.

### Setup section content — DONE 2026-09-14 (probed live via nvidia-smi / CIM)
- [x] GPU: RTX 5090, 32 GB GDDR7 (32,607 MiB reported), driver **616.64**, CUDA UMD **13.4**,
      power limit **600 W** (== max_limit — running at the card's ceiling, no headroom left;
      this is higher than the 575 W figure assumed earlier, so use 600 W in the article).
      PCIe Gen5 x16 (link.gen.max = current = 5). Resizable BAR **on** (BAR1 total 32,768 MiB
      == full VRAM size).
- [x] CPU: AMD Ryzen 9 9950X3D, 16C/32T. RAM: 64 GB (2×32 GB) DDR5-4800, Team Group.
      Motherboard: ASUS PRIME X870-P WIFI (AM5/X870).
- [x] OS: Windows 11 Home, build 26200.
- [x] Server + version — DONE 2026-09-14, from Unsloth Desktop's own About panel: **Unsloth
      v0.1.808-beta** (package 2026.9.4), bundled **llama.cpp b10909-mix-bea84f7** (a newer
      build than the b10715/92cedc867 noted 2026-09-03 — Unsloth auto-updates its bundled
      llama.cpp, don't reuse the old build string). Unsloth reports **CUDA 13.0**; this is
      the toolkit version distinct from nvidia-smi's driver-reported UMD 13.4 — both are
      correct, not a discrepancy.
- [x] NVFP4 quant details and sampling params — DONE 2026-09-14/15. Quant lineage/layer
      mix documented in the Methodology footer (calibration dataset/block size noted as
      undocumented by either model card, not guessed at); sampling params correctly
      deferred to `config.article.json` rather than inlined/re-derived. Also fixed a stale
      `:HIGH` quant-tag reference in the footer that didn't match the real run's id.

### Draft
- [x] First full text draft written 2026-09-14 — `docs/article-draft.md`. Revised
      substantially 2026-09-15 for the quality regrade, the quant-format bridge point,
      and the real MTP on/off comparison. Now ~3200 words (was ~1850/2058/2761 across
      earlier passes) — correctness-first through all of this, not yet re-trimmed for
      length. Reference article was a ~5 min read; this is now ~14 min. Worth an editing
      pass before publishing, but not started.
- [ ] Decide publishing venue (Medium?)
