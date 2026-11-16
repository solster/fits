# Repository guidance

This repository contains Solster.Fits, a dependency-free .NET 11 / C# 15 FITS image reader.
Preserve the AGPLv3 LICENSE. Keep application metadata, statistics, calibration and display outside this library.

- `src/Solster.Fits`: public FITS API and implementation.
- `tests/Solster.Fits.Tests`: portable synthetic FITS regression tests.
- `benchmarks/Solster.Fits.Benchmarks`: warmed throughput/allocation harness.
- `README.md`: supported standard scope, ownership, numeric semantics and build/publishing commands.

Use file-scoped namespaces, four-space indentation, PascalCase public members and `_camelCase` fields.
Match the surrounding use of Int32/Int64/Double/String/Boolean. Use unions for distinct reference-type
outcomes, not boxed numeric pixel values. Keep decoding bounded-memory and caller-owned output reusable.
Do not add app dependencies or silently narrow/scale pixel values. Preserve stream ownership and explicit
sequential-versus-seek validation guarantees. Extend synthetic tests for parsing/numeric changes.

Run `dotnet build Fits.slnx`, `dotnet test Fits.slnx --no-build`, and relevant Release benchmarks.
Pack with `dotnet pack src/Solster.Fits -c Release -o artifacts`. Report failures and unavailable checks.
Do not commit generated packages or credentials. Publication uses the manual Publish workflow.
