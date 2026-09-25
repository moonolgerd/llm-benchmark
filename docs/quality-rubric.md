# Quality Rubric — 5090 Article

Manual grading (per the locked decision: no LLM-judge build). Each task scored 0–100
per response; per-model task score is the mean of its 3 attempts. Applied to
`results/quality-transcripts-20260911-200157.txt` (the full `config.article.json` run,
2026-09-11).

## strict-json-schema
Raw JSON only (no fences/prose), all 6 keys present with correct types, values exactly
matching the prompt's book facts, genres from the given enum.
- Valid JSON / no fences or prose — 30 pts
- All keys present, correct types — 30 pts
- Values factually correct — 30 pts
- Enum values valid — 10 pts

## csharp-refactor-di
Extract `IOrderRepository` / `IEmailSender`, refactor `OrderProcessor` to constructor
injection preserving its original behavior, show DI registration for a **standard**
ASP.NET Core `Program.cs`, brief explanation only.
- Interfaces correctly extracted, match original usage — 20 pts
- `OrderProcessor` refactor preserves behavior — 30 pts
- DI registration is an actual standard `Program.cs` shape (builder → services → build →
  run), not a bare excerpt — 20 pts
- Explanation is brief, not verbose — 15 pts
- No regressions (e.g. dropping working implementation logic) — 15 pts

**Note (added after the 2026-09-15 regrade, see below):** the source prompt never shows
the bodies of `SqlOrderRepository`/`SmtpEmailSender` — only `new SqlOrderRepository()`
call sites. There is no original implementation logic to "drop" for those two classes.
A `throw new NotImplementedException();` body is a valid, compiling placeholder for an
unspecified method and should NOT be scored under "no regressions" — it isn't one. A
placeholder body on a non-`void` method with no `throw` and no `return` (e.g. `{ /* ... */ }`
on a method returning `Order`) **does not compile** (CS0161) and IS a real correctness
defect under this same bullet.

## cross-file-reasoning
Identify the bug spanning both files (cache never invalidated after a mutate-then-save,
so the cache and DB can diverge — most sharply on a failed/partial save or on any DB
write that bypasses the cache), explain why, propose a concrete fix.
- Correctly identifies the cross-file bug span — 30 pts
- Explains a concrete failure scenario — 25 pts
- Proposes a correct, concrete fix — 25 pts
- Notes secondary concerns (thread-safety) — 10 pts
- Answer stays reasonably tight — 10 pts

## long-context-needle
Extract the exact multiplier (1.8) and attempt count (6) from the padded context, quote
the exact supporting sentence, nothing else.
- Correct multiplier — 30 pts
- Correct attempt count — 30 pts
- Exact verbatim quote — 30 pts
- No distractor bleed-through — 10 pts

## agentic-tool-call
Emit exactly one JSON tool call, no prose before or after, a sensible first move given
the ambiguity (searching for the missing type is defensible before writing a new file).
- Valid single JSON tool call matching the schema — 40 pts
- Zero prose before/after — 30 pts
- Sensible tool + args choice — 30 pts

## Results (as first graded, 2026-09-11 — superseded, see regrade below)

| Task | Qwen3.8-27B NVFP4 | Ornith-1.5-35B-A3B | Notes |
|---|---|---|---|
| strict-json-schema | 100 | 100 | Both perfect, all 3 attempts each |
| csharp-refactor-di | 90 | 80.7 | Ornith attempt 1 stubs the concrete repo/sender classes with `NotImplementedException` (discards working logic); attempts 2–3 show only a 2-line DI excerpt, not the "standard Program.cs" the prompt asked for |
| cross-file-reasoning | 95 | 93.7 | Both correctly diagnose the cache-invalidation bug; Ornith attempt 2 closes with an unrequested follow-up question |
| long-context-needle | 100 | 100 | Identical correct answer + exact quote, both models |
| agentic-tool-call | 100 | 100 | Both emit a clean, schema-valid, prose-free tool call |
| **Overall avg** | **97.0** | **94.9** | |

Original headline: "Ornith wins every speed/power/efficiency axis, but Qwen edges it on
quality, entirely on the DI-refactor task." **This DI-refactor score turned out to rest
on an inconsistent grading call — see the regrade below before citing these numbers.**

## Follow-up: does the DI-refactor gap generalize? (2026-09-14)

Before calling this a general "Ornith cuts corners" pattern, it's worth checking whether
it reproduces outside that one C# task. Added a 6th task in a different domain (bash
script maintenance) that stresses the same two failure modes seen in the DI task:
(1) preserving existing working logic rather than stubbing/dropping it, and (2) returning
the complete requested artifact rather than an excerpt.

### bash-script-extend
Given a working deploy script, add a health-check-with-rollback step while preserving
every existing behavior (logging, backup, git pull, restart), output the complete
updated script only.
- All original steps preserved functionally unchanged — 30 pts
- Correct 5-attempt/2s-interval health check — 25 pts
- Correct rollback (restore latest backup, restart, log, exit 1) — 25 pts
- Output is the complete script, no diff/excerpt/ellipses — 15 pts
- No unrelated regressions — 5 pts

| Model | Attempt 1 | Attempt 2 | Attempt 3 | Avg |
|---|---|---|---|---|
| Qwen3.8-27B NVFP4 | 100 | 100 | 100 | **100.0** |
| Ornith-1.5-35B-A3B | 100 | 100 | 100 | **100.0** |

Both models nailed it — full preservation, correct new logic, complete output, all 3
attempts each. Ornith even added defensive touches beyond spec (a `|| true` guard on the
curl call so a transient failure doesn't trip `set -e` mid-retry-loop, and a check for
"no backup exists" before attempting a restore).

**Revised conclusion (as of 2026-09-14, itself superseded below)**: the DI-refactor gap
does not generalize to "Ornith drops real logic" or "Ornith gives excerpts instead of
complete artifacts" as a rule. It's a specific weak spot on that one C#/DI-shaped task,
not a broader reliability pattern.

## Regrade: the DI-refactor gap was partly a grading bug (2026-09-15)

A third model — `unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M`, run as a quant-format bridge point
for Finding #3 (see `docs/article-plan.md` decision log) — got the **full battery**,
including `csharp-refactor-di`. All 3 of its attempts used `throw new
NotImplementedException();` for the concrete `SqlOrderRepository`/`SmtpEmailSender`
bodies — the exact thing Ornith's attempt 1 was docked 12 points for under "no
regressions" ("drops working logic").

Re-reading the prompt: **it never shows those classes' bodies** — only
`new SqlOrderRepository()` / `new SmtpEmailSender()` call sites. There was never any
"working logic" in the transcript for either model to drop. Worse, going back to
Qwen NVFP4's own DI response (scored 90/100, no deduction under "no regressions"): its
placeholder bodies are `{ /* ... */ }` on methods that return `Order` — no `return`, no
`throw`. **That's a CS0161 compile error**, a real defect the original grading pass
missed entirely, while penalizing Ornith's version of the same "no body given" situation
for using a construct that actually compiles.

**Corrected:**
- Qwen NVFP4 `csharp-refactor-di`: 90 → **85** (deduct for the non-compiling placeholder
  bodies, not previously caught)
- Ornith `csharp-refactor-di` attempt 1: 78 → **88** (remove the "dropped working logic"
  deduction — nothing was dropped; keep the deductions for the unrequested minimal-API
  endpoint and the `AddSingleton`-with-caveat choice)
- Ornith `csharp-refactor-di` attempts 2–3: **unchanged at 82** — that criticism (a bare
  2-line DI excerpt instead of the requested standard `Program.cs`) is a real, separate
  completeness issue and still stands; Qwen NVFP4 and UD-Q4_K_M both gave the actual
  full `Program.cs` shape every attempt.

### Results, corrected, all three models

| Task | Qwen3.8-27B NVFP4 | Ornith-1.5-35B-A3B | Qwen3.8-27B UD-Q4_K_M |
|---|---|---|---|
| strict-json-schema | 100 | 100 | 100 |
| csharp-refactor-di | **85** (was 90) | **84.0** (was 80.7) | 97.0 |
| cross-file-reasoning | 95 | 93.7 | 96.0 |
| long-context-needle | 100 | 100 | 100 |
| agentic-tool-call | 100 | 100 | 100 |
| **5-task avg** | **96.0** (was 97.0) | **95.5** (was 94.9) | **98.6** |
| bash-script-extend (6th task, follow-up only) | 100 | 100 | not run |

**Corrected headline**: once graded consistently, Qwen NVFP4 vs Ornith quality is a
**wash** — 96.0 vs 95.5, well inside the noise of a 5-task/3-repeat manual grade. There
is no real "dense wins on quality" story between those two. What IS real: Ornith
under-delivered the literal "standard `Program.cs`" ask in 2 of its 3 DI attempts — a
genuine, reproducible-within-itself completeness gap on that one axis — while neither
Qwen variant did. And UD-Q4_K_M's 98.6 is the highest of the three, driven by giving a
correct, compiling, complete answer on the exact task that tripped up the other two.

**Why this matters for the article**: don't publish "the dense model wins on quality" —
that framing doesn't survive a consistent regrade. The honest version is narrower and
more interesting: on this task set, quality differences between these three
configurations are mostly noise except for one specific, narrow completeness gap in two
of Ornith's three DI attempts. Also worth stating plainly in the methodology: a manual
grading pass caught its own inconsistency only after a third data point exposed it —
that's the kind of thing readers should be told, not smoothed over.
