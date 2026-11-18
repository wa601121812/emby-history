# Retry control-flow regression tests

## Run

From the repository root:

```sh
python3 RetryTests/run-tests.py
```

The runner uses `dotnet` from `PATH` (override with the `DOTNET` environment variable).
It stores stdout in `run.log`, results and SHA-256 hashes in `verification.json`, and verifies the
linked production sources did not change during execution. A failing assertion returns exit code 1.
No production source, configuration, deployment or API is modified.

## Scope

- Links the actual `Plugin/EntryPoint.cs`: calls private `Scan`, `ProcessAsync` and event-queue
  control flow via reflection. No copied/reimplemented worker or reconciliation algorithm.
- Links real Core ledger/identity code, planner, status, configuration and poster cache.
- Uses actual `sdk-current` `ILibraryManager`/queries/entities and `DispatchProxy` for deterministic
  SDK responses. The Emby assemblies are not redistributed here: copy `MediaBrowser.Controller.dll`,
  `MediaBrowser.Common.dll`, `MediaBrowser.Model.dll` and `SkiaSharp.dll` from your own Emby install
  into `sdk-current/`, and `Emby.Naming.dll`, `Emby.Media.Model.dll`, `Emby.Extensions.dll` (present
  in Emby's `/system` directory but not in its public SDK) into `RetryTests/sdk-dependencies/`.
- Detached SDK entities have their empty GenreItems/TagItems marked loaded via reflection, so
  snapshot extraction does not try to consult a nonexistent server IDataContext.
- Only Plugin configuration host, archive writer, index publication and access sync are boundary
  doubles. Archive calls verify dirty-source propagation, not actual rendering or indexing.
- Tests use isolated directories under `TMPDIR` and delete fixtures after each successful setup.
  No live Emby API, Studio session, scheduled timer or production server is used.
- Tests fail on unexpected exceptions logged/caught by the production worker, preventing false
  positives from the error-retry path. Only the source-unavailable case expects IOException.
- Due-time execution is exercised by moving the private retry deadline into the past; there is no
  30-second sleep. Separate assertions validate the generated deadline lies between the actual
  invocation start/end plus exactly 30 seconds.

## Verified result

`run.log` / `verification.json`: **11/12 PASS, exit 1**.

The original partial-scan regression is fixed: shrinking and repeated pagination return incomplete,
retain unseen presence and container proofs, persist observations/exact deletions, mark dirty for
archive publication, and schedule a 30-second retry even for a periodic scan. Due short retries run
before the periodic deadline. Held empty libraries and unavailable mounts preserve evidence; fully
proven empty removals complete and clear retry/proofs.

### Remaining blocking case

`UNCOMMITTED cascade with complete nonempty scan must retain proof and short retry` fails:

1. Seed source 14 with series 500 episodes 1001 and 1002.
2. Queue the series-removal event; SDK lookup says 1001 is gone but 1002 is still present.
3. Return a stable, complete page containing 1002 with `TotalRecordCount=1`.
4. Real `Scan` applies exact removal of 1001, retains 1002 as active, but treats count completeness
   as cascade completion and calls `ClearContainerProofs`; `ProcessAsync` then removes its retry.
5. Commit 1002 deletion without a per-episode event; next worker performs no query and the
   persisted record still shows it active.

Actual diagnostic:

```text
CASCADE evidence: after full scan proofs=0 retries=0; next worker extraQueries=0 remainingChildRemoved=False
```

At review time: `Plugin/EntryPoint.cs:669-675` computes/returns pagination completeness and clears
proofs at line 672; caller lines 334-340 removes the short retry on `true`.

**Verdict:** partial-scan fast retry PASS; overall cascade-proof preservation/fast-retry specification
**BLOCKED**, because a complete item enumeration is not proof that an asynchronous container delete
has finished. Production code was left unchanged.
