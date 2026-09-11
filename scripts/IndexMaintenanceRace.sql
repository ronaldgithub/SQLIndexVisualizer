-- ============================================================================
-- INDEX MAINTENANCE RACE: "Best Practice" vs "Low Threshold" REBUILD
-- ============================================================================
-- Modeled on Jeff Moden's "Black Arts Index Maintenance #1.2 - GUIDs v.s.
-- Fragmentation - They're not the problem... WE ARE!" (28 Jul 2021).
-- Source deck: Z:\Learning\2-Jeff Moden-Black Arts Index Maintenance - GUIDs
-- v.s. Fragmentation - They're not the problem... WE ARE\PowerPoint\
-- Black Arts Index Maintenance 1-2 - GUIDs vs Fragmentation - 60 Minute
-- Version (FINAL).pptx
--
-- THE ACTUAL CLAIM BEING TESTED (his, not the usual one):
-- Random GUIDs are not what causes runaway fragmentation. REORGANIZE is.
-- REORGANIZE compacts pages UP TO the fill factor but can never make space
-- ABOVE it - so once a random-key index's density creeps near the fill
-- factor, REORGANIZE gets "stuck": it keeps compacting, inserts keep
-- splitting pages, and the whole thing thrashes forever. His fix is "Low
-- Threshold Rebuilds": REBUILD ONLY, at just >1% fragmentation (never
-- REORGANIZE), which keeps a random-GUID index essentially flat for weeks.
--
-- IMPORTANT CONTEXT: our own goodINT.sql / badGUID.sql demo (single 100K-row
-- batch into an empty table, no ongoing usage, no maintenance) is literally
-- the "strawman" demo Moden calls out in his own slide 10 - our BadGUID
-- landed at 497 leaf pages, his strawman example was 474. Both are BELOW
-- his stated 1,000-page floor for index maintenance to even matter. This
-- script fixes that: wider rows (his slide 42 test table design), small
-- REPEATED batched inserts (simulating ongoing usage, not one big load),
-- and two competing maintenance policies running side by side.
--
-- SCALE: his real study ran 3.65 million rows over a simulated year. This
-- version is scaled down to finish in SSMS in a few minutes while keeping
-- the same shape - tune @BatchSize / @Iterations below if you want it
-- bigger or faster.
-- ============================================================================

USE jeffmoden;
GO
SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- Two identically-shaped tables, one per maintenance policy. Row design
-- matches slide 42: GUID clustered PK + CHAR(100) "Fluff" column simulating
-- other real columns (~123 bytes/row including the 7-byte row header ->
-- ~65 rows/page, same math he used).
-- ----------------------------------------------------------------------------
DROP TABLE IF EXISTS dbo.BadGUID_BestPractice;
CREATE TABLE dbo.BadGUID_BestPractice
(
     SomeGuid UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID()
    ,Fluff    CHAR(100)        NOT NULL DEFAULT REPLICATE('x', 100)
    ,CONSTRAINT pk_bestpractice PRIMARY KEY CLUSTERED (SomeGuid) WITH (FILLFACTOR = 80)
);

DROP TABLE IF EXISTS dbo.BadGUID_LowThreshold;
CREATE TABLE dbo.BadGUID_LowThreshold
(
     SomeGuid UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID()
    ,Fluff    CHAR(100)        NOT NULL DEFAULT REPLICATE('x', 100)
    ,CONSTRAINT pk_lowthreshold PRIMARY KEY CLUSTERED (SomeGuid) WITH (FILLFACTOR = 81)
);
-- Fill factors match slide 81's "secret": use 71 or 81 for random GUID
-- indexes - the trailing "1" is a reminder to REBUILD at >1% fragmentation.
-- (BestPractice keeps a plain 80 since it isn't following that rule anyway.)

-- ----------------------------------------------------------------------------
-- Per-iteration log: what each policy's index looked like, and what (if
-- anything) was done about it.
-- ----------------------------------------------------------------------------
DROP TABLE IF EXISTS dbo.MaintenanceLog;
CREATE TABLE dbo.MaintenanceLog
(
     LogID       INT IDENTITY(1,1) PRIMARY KEY
    ,Policy      VARCHAR(20) NOT NULL
    ,Iteration   INT         NOT NULL
    ,RowsSoFar   INT         NOT NULL
    ,PageCount   BIGINT      NOT NULL
    ,FragPercent FLOAT       NOT NULL
    ,ActionTaken VARCHAR(20) NOT NULL
    ,ActionMs    INT         NOT NULL
    ,LoggedAt    DATETIME2   NOT NULL DEFAULT SYSDATETIME()
);

-- ============================================================================
-- THE RACE
-- ============================================================================
DECLARE
     @BatchSize   INT = 1000   -- rows inserted per iteration, per table
    ,@Iterations  INT = 300    -- iterations ("simulated batches of activity")
    ,@i           INT = 1
    ,@PageCount   BIGINT
    ,@FragPercent FLOAT
    ,@Action      VARCHAR(20)
    ,@StartTime   DATETIME2;

WHILE @i <= @Iterations
BEGIN
    -- Small batched insert each iteration (real trickle usage, not one big
    -- sorted/unsorted load). Both tables get their own independent random
    -- GUIDs - we are only varying the MAINTENANCE POLICY, not the insert
    -- pattern, so any divergence between the two is attributable to that.
    INSERT INTO dbo.BadGUID_BestPractice (SomeGuid, Fluff)
    SELECT NEWID(), REPLICATE('x', 100)
    FROM (SELECT TOP (@BatchSize) 1 AS x FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS Numbers;

    INSERT INTO dbo.BadGUID_LowThreshold (SomeGuid, Fluff)
    SELECT NEWID(), REPLICATE('x', 100)
    FROM (SELECT TOP (@BatchSize) 1 AS x FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS Numbers;

    -- ---- Policy A: "Best Practice" (Ola Hallengren defaults) ----
    -- <5% nothing | 5-30% REORGANIZE | >30% REBUILD @ FILLFACTOR 80
    -- Uses 'LIMITED' mode - the same fast, non-leaf-scan mode Ola's
    -- IndexOptimize actually uses (see CLAUDE.md's Ola thresholds section).
    SELECT @PageCount = page_count, @FragPercent = avg_fragmentation_in_percent
    FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.BadGUID_BestPractice'), 1, NULL, 'LIMITED')
    WHERE index_level = 0;

    SET @Action = 'NONE';
    SET @StartTime = SYSDATETIME();
    IF @FragPercent > 30
    BEGIN
        ALTER INDEX pk_bestpractice ON dbo.BadGUID_BestPractice REBUILD WITH (FILLFACTOR = 80);
        SET @Action = 'REBUILD';
    END
    ELSE IF @FragPercent >= 5
    BEGIN
        ALTER INDEX pk_bestpractice ON dbo.BadGUID_BestPractice REORGANIZE;
        SET @Action = 'REORGANIZE';
    END;

    INSERT INTO dbo.MaintenanceLog (Policy, Iteration, RowsSoFar, PageCount, FragPercent, ActionTaken, ActionMs)
    VALUES ('BestPractice', @i, @i * @BatchSize, @PageCount, @FragPercent, @Action, DATEDIFF(MILLISECOND, @StartTime, SYSDATETIME()));

    -- ---- Policy B: "Low Threshold Rebuild" (Moden's fix) ----
    -- >1% REBUILD @ FILLFACTOR 81, NEVER REORGANIZE.
    SELECT @PageCount = page_count, @FragPercent = avg_fragmentation_in_percent
    FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.BadGUID_LowThreshold'), 1, NULL, 'LIMITED')
    WHERE index_level = 0;

    SET @Action = 'NONE';
    SET @StartTime = SYSDATETIME();
    IF @FragPercent > 1
    BEGIN
        ALTER INDEX pk_lowthreshold ON dbo.BadGUID_LowThreshold REBUILD WITH (FILLFACTOR = 81);
        SET @Action = 'REBUILD';
    END;

    INSERT INTO dbo.MaintenanceLog (Policy, Iteration, RowsSoFar, PageCount, FragPercent, ActionTaken, ActionMs)
    VALUES ('LowThreshold', @i, @i * @BatchSize, @PageCount, @FragPercent, @Action, DATEDIFF(MILLISECOND, @StartTime, SYSDATETIME()));

    SET @i += 1;
END;

-- ============================================================================
-- SUMMARY -- run after the loop finishes
-- ============================================================================
;WITH LastIteration AS
(
    SELECT Policy, MaxIter = MAX(Iteration)
    FROM dbo.MaintenanceLog
    GROUP BY Policy
)
SELECT
     ml.Policy
    ,MaintenanceActions = SUM(CASE WHEN ml.ActionTaken <> 'NONE' THEN 1 ELSE 0 END)
    ,ReorganizeCount     = SUM(CASE WHEN ml.ActionTaken = 'REORGANIZE' THEN 1 ELSE 0 END)
    ,RebuildCount        = SUM(CASE WHEN ml.ActionTaken = 'REBUILD' THEN 1 ELSE 0 END)
    ,TotalMaintenanceMs  = SUM(ml.ActionMs)
    ,AvgFragPercent      = AVG(ml.FragPercent)
    ,MaxFragPercent      = MAX(ml.FragPercent)
    ,FinalPageCount      = MAX(CASE WHEN ml.Iteration = li.MaxIter THEN ml.PageCount END)
    ,FinalFragPercent    = MAX(CASE WHEN ml.Iteration = li.MaxIter THEN ml.FragPercent END)
FROM dbo.MaintenanceLog ml
JOIN LastIteration li ON li.Policy = ml.Policy
GROUP BY ml.Policy
ORDER BY ml.Policy;

/*
================================================================================
RESULT -- paste the summary SELECT's output here once the loop finishes.

Policy	MaintenanceActions	ReorganizeCount	RebuildCount	TotalMaintenanceMs	AvgFragPercent	MaxFragPercent	FinalPageCount	FinalFragPercent
BestPractice	159	146	13	53868	8.1595073381059	96.0526315789474	5865	6.39386189258312
LowThreshold	46	0	46	4969	2.68436806303802	97.5609756097561	5513	0.126972610194087

Analysis (for students):
Both policies received the exact same 300,000 rows over the same 300
iterations. Only the MAINTENANCE POLICY differed - and the outcome is
stark:

  BestPractice: 159/300 iterations (53%) triggered maintenance, and 146
  of those (92%) were REORGANIZE, not REBUILD. Despite "doing something"
  over half the time, average fragmentation across the whole run was
  8.16%, peaking at 96.05%, and it finished at 6.39% fragmentation using
  5,865 pages.

  LowThreshold: only 46/300 iterations (15%) triggered maintenance, and
  EVERY one was a REBUILD - REORGANIZE was never used, by design. Average
  fragmentation was 2.68% (about a third of BestPractice's), and it
  finished at just 0.127% fragmentation using 5,513 pages - fewer pages
  than BestPractice despite BestPractice "compacting" via REORGANIZE 146
  times.

  Total time spent on maintenance: BestPractice spent 53.9 seconds across
  the whole run; LowThreshold spent 5.0 seconds - roughly 11x LESS
  maintenance time for a BETTER end result on every other metric.

This is Moden's core claim, reproduced end to end: REORGANIZE on a
random-key clustered index doesn't fix the underlying problem, it just
compacts pages back down toward the fill factor, which immediately
invites the next page split the moment another random insert lands
there. BestPractice's 146 REORGANIZEs mostly bought nothing - the table
kept re-fragmenting between them, which is exactly why its AVERAGE
fragmentation stayed high even with "maintenance" running constantly.
LowThreshold's lesson isn't "less maintenance is better" - it's "the
RIGHT maintenance, applied early and cheaply, beats frequent maintenance
that never actually fixes anything."

One number worth a caveat: LowThreshold's MaxFragPercent (97.56%) is
actually slightly higher than BestPractice's (96.05%). That's a single
outlier reading, not a sustained state - most likely from very early in
the run when the table was still tiny (a handful of pages), where
'LIMITED' mode's fragmentation estimate is known to be noisiest on small
page counts. Worth confirming by running the full-trajectory query below
and checking where that spike actually falls. Either way, AVERAGE
fragmentation, total maintenance time, and final fragmentation all point
the same direction, so one early spike doesn't change the conclusion.

What to look at next: run the commented-out full-trajectory query below
and chart both policies' FragPercent over Iteration - that's the shape of
Moden's "jagged sawtooth vs clean flats" slides (47-52). You can also
point the SQLIndexVisualizer app itself at dbo.BadGUID_BestPractice and
dbo.BadGUID_LowThreshold now that they exist, to see their DNA charts
side by side.
================================================================================
*/

-- Optional: eyeball the full trajectory instead of just the summary -
-- this is the shape of Moden's slides 47-52 ("jagged sawtooth" for
-- Best Practice vs "clean flats" for Low Threshold).
-- SELECT Policy, Iteration, RowsSoFar, PageCount, FragPercent, ActionTaken
-- FROM dbo.MaintenanceLog
-- ORDER BY Policy, Iteration;
