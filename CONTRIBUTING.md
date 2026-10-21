# Contributing to RO3Emu

Thanks for helping improve the Ragnarok Online 3 server emulator. Bug reports, client evidence, documentation fixes
and focused code changes are welcome. Start with an [issue](https://github.com/akreao/ro3emu-server/issues/new/choose)
for behavior that needs discussion, especially when the original server's rule is unknown.

## Set up and check a change

Use the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From a PowerShell terminal in the repository
root, these checks work without the game client:

```powershell
dotnet build src/Ro3Emu.Server/Ro3Emu.Server.csproj -c Release
dotnet build tools/LuaDecode/LuaDecode.csproj -c Release
dotnet build tools/ConfigExport/ConfigExport.csproj -c Release
dotnet test tests/Ro3Emu.Patcher.Tests/Ro3Emu.Patcher.Tests.csproj -c Release
dotnet test tests/Ro3Emu.Setup.Tests/Ro3Emu.Setup.Tests.csproj -c Release
```

The full solution includes `Ro3Emu.ClientHooks` and `Ro3Emu.Loc`, which compile against DLLs from your own RO3 PC
client. Their built DLLs are not committed: the `client DLLs` workflow builds them for each pull request (a
`client-dlls` artifact) and publishes main's build to the `client-dlls` release. The server tests run without the
client: tests of rules use an invented data set, and tests of real content are skipped unless you have
[exported game data](README.md#running-from-source) ([testing guide](docs/guide/testing.md)):

```powershell
dotnet test tests/Ro3Emu.Tests/Ro3Emu.Tests.csproj -c Release --filter "Category!=Slow"
```

Compiler and analyzer warnings fail the build (`Directory.Build.props`), except in `Ro3Emu.ClientHooks`,
`Ro3Emu.Loc` and `Ro3Emu.ClientTools`, which only build against a local client. CI checks the C# layout set in
`.editorconfig`. Before you push, run:

```powershell
dotnet format whitespace --folder
```

Any .NET 10 SDK builds and tests the code, including the 10.0.1xx band that distributions such as Arch and
CachyOS package (`global.json` asks for 10.0.100 or later). CI formats with the 10.0.4xx band (`dotnet-version` in
`.github/workflows`), and other bands lay out some statements differently: 10.0.1xx would reformat about a dozen
unrelated files. Run `dotnet format` with a 10.0.4xx SDK, for example one from
[dotnet-install](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script) next to the distribution's, or
leave the layout check to CI.
CI checks the analyzer baseline in `.editorconfig` with `dotnet run --project tools/AnalyzerBaseline -c Release` on
that band, which also counts some rules differently from other bands; when an SDK update changes a count, the check
prints the new numbers to write.

Package versions live in `Directory.Packages.props`: a `PackageReference` names the package without a version.

Report which checks you ran and which you could not run. Do not treat the client-independent CI check as proof of a
playable client or complete game behavior.

## Evidence and changes

- Use the client's Lua, protobuf messages and config tables as references. Point to the relevant client path and
  symbol or message ID; `docs/research/` and `docs/systems/` show the project's existing evidence format. Mark
  original-server behavior that the client cannot establish as *(inferred)*.
- Keep game state changes on the server's game loop. Add focused tests for new behavior and, when the exported data
  is available, an integration test covering the affected client/server flow.
- Keep pull requests focused. Explain the player-visible or developer-visible change, the evidence for it and the
  validation you performed. Use the pull request template.
- Do not commit the PC client, proprietary DLLs, exported `data/` or `re/`, account credentials or logs containing
  private information. The repository's `.gitignore` excludes the export directories; check your diff before sending.

For a local setup and an overview of current systems and known limits, see the [README](README.md) and
[documentation index](docs/README.md).
