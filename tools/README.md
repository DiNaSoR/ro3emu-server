# Tools

Everything here works on files from your own client (`RO3_CLIENT_DIR`, the `ROO_PC` folder) and writes into the
ignored `data/` and `re/` folders. The main [README](../README.md#running-from-source) has the export recipe.

| Tool | What it does |
|---|---|
| `LuaDecode` | turns the client's obfuscated Lua 5.4 chunks (`Recovery/LuaPayload/**/*.lua.bytes`) into standard bytecode |
| `unluac-batch` | decompiles a whole bytecode tree in one JVM with [unluac](https://sourceforge.net/projects/unluac/) (bring your own `unluac.jar`) |
| `ConfigExport` | exports the Lua config modules (`Config\DataConfig`, `Config\SceneEditor`) to JSON for the server |
| `ProtoDump` | rebuilds `.proto` files from the client's `LocalProto/proto.pb.bytes` |
| `msgtable.js` | extracts the message table (id, name, request and response types) from the decompiled Lua |
| `WorldData/` | reads the client's VFS: nav grids (`navexport.js`, `navparse.js`, `navquery.js`), scene editor data, single assets (`vfsget.js`) |
| `Loc/` | the localization pipeline behind `Ro3Emu.Loc`: scans the client for Chinese text, builds the translation queue and the `loc_*.tsv` maps |
| `ClientTextures/` | renders English versions of images with baked-in Chinese text from a texture dump of your client |
| `EconomyProbe` | read-only economy snapshots and ledger checks against a server database (used by load scenario S3) |
| `patch-client.ps1` | rebuilds the client hooks and patches the client in `RO3_CLIENT_DIR` against a local server |
| `run-server.ps1` | runs the server and restarts it when it exits, logging to `data/logs/` |
| `update-client-dlls.sh` | installs the client DLLs from the rolling `client-dlls` release (or a folder you give it) into RO3Emu and the game |
| `update-launcher.sh` | installs RO3Emu.exe from the rolling `launcher` release (backing up the old exe), then runs `update-client-dlls.sh` |
| `test-summary.ps1` | one line per test project from `.trx` results: executed, failed, skipped for want of client data |
| `release/` | `pack.ps1` builds the release archives, `smoke.ps1` tests the zip, `client-dlls.ps1` builds the client DLLs without a client |
| `check-site.mjs` · `render-social-preview.ps1` | checks the project website in `docs/site` and renders its social preview |
