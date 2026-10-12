# EconomyProbe

Read-only E01 snapshots, committed receipt/ledger verification and balance comparison, using the same library as
S3. SQLite inputs include their live WAL through a pinned read transaction. Do not copy a live `.db` without its
WAL; supply the original database or a consistent backup. The tool never repairs, prunes or checkpoints it.

```powershell
dotnet build tools/EconomyProbe -c Release
$probe = 'tools/EconomyProbe/bin/Release/net10.0/EconomyProbe.dll'
dotnet $probe snapshot PATH/ro3emu.db data auto .loadtest/e01/opening
# Run the intended covered operation, then:
dotnet $probe compare PATH/ro3emu.db data auto .loadtest/e01/opening.json .loadtest/e01/comparison
dotnet $probe verify-receipts PATH/ro3emu.db data auto 0 1 .loadtest/e01/receipts
dotnet $probe restore-check RESTORED/ro3emu.db data auto .loadtest/e01/opening.json .loadtest/e01/restore
```

Each command writes JSON and concise Markdown under a new output prefix. Existing outputs are not overwritten.
`auto` reads the durable realm UUID stored in the database; an explicit UUID checks the expected realm. A relocated
backup retains its realm identity. Legacy databases without that identity/watermark remain incomplete.

`snapshot` saves holdings, catalog/schema/coverage bindings, actual committed sequence, decoder diagnostics and the
server library hash. `compare` captures a closing snapshot and reads precisely `(opening, closing]` from retained
history. `verify-receipts` diagnoses linkage/count/source/history defects without claiming balance exactness.
`restore-check` additionally runs read-only SQLite integrity checking. E01 has no archive/pruning support; backup
lifecycle, source retirement, full crash matrix and archive restore qualification belong to E06.

Exit codes: **0** covered requested exactness, **1** corruption/error, **2** incomplete evidence/coverage,
**64** invalid arguments. E01 covers the atomic core of NPC purchases only. Other writers and economic hooks
remain uncovered, so the realm and S3 cannot receive PASS even when a controlled purchase has zero residuals.
JSON snapshots are diagnostic evidence supplied by the operator, not signed/authenticated attestations.

The previous decode measurement invocation is preserved:

```powershell
dotnet $probe PATH/ro3emu.db data auto 3
```

This prints decode timings/bytes and explicitly lacks a ledger comparison. The controlled transaction microprobe
is separate from the read-only CLI:

```powershell
dotnet test tests/Ro3Emu.Tests -c Release --filter FullyQualifiedName~EconomyMeasurementTests --settings tests/Ro3Emu.Tests/serial.runsettings
```

Run the microprobe only after checking that no other worktree's Release server/load runner is active. It creates
worktree-local ignored databases and a small measurement JSON under `.loadtest/e01/probe-*`, compares the same
typed purchase children with/without the E01 envelope, and imposes no timing thresholds.
