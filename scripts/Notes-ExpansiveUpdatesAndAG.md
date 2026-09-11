# Expansive Updates, Bad Page Splits, and AG — Why a Long-Neglected Index Can Suddenly Take Down an App

Discussion note from working through Jeff Moden's "Black Arts Index Maintenance #1.2 - GUIDs v.s. Fragmentation" deck alongside the `goodINT.sql` / `badGUID.sql` / `IndexMaintenanceRace.sql` demos in this repo. Slide numbers reference that deck.

## The question

Could a production table that sat at 0% fragmentation for years suddenly cause locking/blocking bad enough to stop the application — worse in an Availability Group — the moment an update pattern changed?

## Why this is plausible

- **Bad page splits are expensive precisely because of locking, not just space.** Slide 22: a bad split is "4 to 43 times tougher on the log file" than a good one, and the pages involved aren't released until the transaction commits — so a burst of them can genuinely block other queries, not just waste disk.

- **The trigger is usually an "Expansive Update"** — a row growing in place (`NULL` → a value, a `VARCHAR` getting longer) — hitting a region of the index that's been sitting untouched and packed near 100% for a long time. Slide 72's own test: just 333 expansive updates out of 10,000 rows was enough to devastate a supposedly well-maintained IDENTITY index ("IT ALL BLOCKS SELECTS!", slide 18). A table stable for years is exactly the setup where a big batch job or a schema/data change can suddenly hit thousands of rows in the same already-full region at once.

- **AG makes it worse, but not for the reason people usually assume.** The deck actually busts the opposite myth (slides 56, 61, 62) — it argues `REORGANIZE` generates *more* cumulative log/AG traffic over time than occasional `REBUILD`s. But a sudden storm of bad splits is still a spike in heavily-logged, lock-holding activity that has to replicate/harden to the secondary — so if that spike happens on a system that's been quietly under-maintained for years, an AG (especially synchronous) would feel it as sudden replication lag and stalled commits, which matches "stopped the application."

## The takeaway

It's less "not doing maintenance caused it" and more "not doing maintenance let the table's *risk* of a bad-split storm sit unnoticed until some update pattern finally triggered it all at once" — which lines up with the deck's own conclusion (slide 84) that going without maintenance forever isn't actually safe, it's just less actively harmful than doing it wrong (`REORGANIZE`) in the short term.

## The "4 years without index maintenance" anecdote — confirmed, from the video

Not in the PPTX, but it's in the companion video (`Black Arts Index Maintenance 1.2 - Guids vs. Fragmentation | Jeff Moden [rvZwMNJxqVo].mp4`, ~19:22 mark), transcribed locally to check.

**What actually happened, per the talk:** in January 2016, Moden hit a page split that cascaded all the way up to the **root level** of the clustered index's B-tree. A root-level split blocks the entire table, not just range scans — inserts, updates, and deletes all stall, because none of the pages involved in that system transaction release until it commits. That incident is what led him to deliberately run a four-year experiment of doing *no* index maintenance at all afterward (he's explicit that he doesn't recommend doing that) — presumably the origin of the "No Index Maintenance" baseline used later in his fragmentation study.

**Correction to the original guess in this note:** the causality runs the other way from what we'd assumed. It wasn't "years of neglect quietly building risk until an update pattern triggered a blocking storm" — it was "one bad root-level-split incident → he intentionally went 4 years with zero maintenance afterward, on purpose, as an experiment." The general mechanism above (bad splits are heavily logged and lock-holding, expansive updates are a common trigger) is still accurate and still the right lens for the *general* failure mode being asked about — it just isn't literally what happened in his own story.

**On the AG angle specifically:** no direct AG connection was mentioned for this particular incident. The transcript's only AG reference is the general myth he busts elsewhere in the talk — that people avoid `REBUILD` because "it's worse for AG/the log file," which he refutes with the log-file data (see "Two Prevalent Myths," slide 56). The AG-amplification reasoning in this note is sound in general (a root-level block is still a log-heavy, lock-holding event that would need to replicate/harden to a secondary), it's just not something he explicitly ties to his own incident.

## Should Ola Hallengren's IndexOptimize branch its policy on key type (sequential vs. random)?

Given what `IndexMaintenanceRace.sql` demonstrated — that a uniform "Best Practice" REORGANIZE/REBUILD threshold is actively worse than a REBUILD-only "Low Threshold" policy on a random-key clustered index — the natural follow-up question is whether Ola's script should behave differently depending on whether an index's key is sequential (IDENTITY, ascending dates) or random (GUID, hash).

**Yes, and Ola already supports doing this — no script modification needed.** `IndexOptimize` accepts an `@Indexes` list plus per-call `@FragmentationLevel1`/`@FragmentationLevel2` thresholds, so it can simply be invoked twice in the same maintenance job:

- **Call 1** — default Best Practice thresholds (`@FragmentationLevel1 = 5`, `@FragmentationLevel2 = 30`, i.e. REORGANIZE 5-30%, REBUILD >30%) targeted at `@Indexes` = the sequential-key indexes.
- **Call 2** — `@FragmentationLevel1` set high enough that REORGANIZE never triggers (e.g. 100) and `@FragmentationLevel2 = 1`, so anything over 1% fragmentation goes straight to REBUILD, targeted at `@Indexes` = the random-key indexes. This reproduces the "Low Threshold" policy that won the race.

**The catch:** Ola's procedure has no built-in way to *detect* "this key is random" — it only reacts to measured fragmentation, with zero notion of key type. The classification (which clustered indexes are GUID/hash-keyed vs. IDENTITY/sequential) has to be built and maintained manually, and the two `@Indexes` lists need to stay in sync as tables get added or redesigned.
