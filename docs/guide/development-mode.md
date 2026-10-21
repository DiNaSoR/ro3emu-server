# Development mode (`server.sh --dev`)

`server.sh` starts the server as **Production** by default. `--dev` starts it as **Development** instead: it sets
`ASPNETCORE_ENVIRONMENT=Development` in place of `Production`, for a run in this terminal and in the systemd unit
written by `--service`. Both scripts take it: `server.sh` in the repository and the release archive's `server.sh`
(`tools/release/server.sh`).

```bash
./server.sh --dev --host 127.0.0.1 --gm <account>    # local testing
./server.sh --dev --host <public IP> --gm <account>  # a test server others reach
```

## What Development turns on

| What | Production (default) | Development (`--dev`) | Where |
|---|---|---|---|
| `appsettings.Development.json` | not read | read on top of `appsettings.json` | ASP.NET host (`ServerHost.Create`, content root = the server's folder) |
| Trade Exchange seller and buy-back | off | on (`Exchange:SystemSeller`, `Exchange:BuyBack`) | `appsettings.Development.json`, [exchange](exchange.md) |
| Open content, battle pass, free Top Up and cash shop | on | on | `server.sh` sets these either way; `appsettings.Development.json` sets them too |
| GM with no `--gm` | nobody | nobody | `server.sh` sets `Ro3Emu__GmOpenWhenEmpty=false` in both modes; [GM access](gm-access.md) |
| GM with `--gm` | only the listed accounts | only the listed accounts | `GmCommands.Allowed` |
| Errors on the HTTP port (5380) | an empty 500 response | ASP.NET's developer exception page, with the stack trace | ASP.NET host default |
| Dependency injection checks | off | on: `ValidateScopes` and `ValidateOnBuild`, so a bad registration fails at startup | ASP.NET host default |

Settings given on the command line or as `Ro3Emu__...` environment variables still win over
`appsettings.Development.json`, as they do over `appsettings.json`. Logging levels are the same in both files.

Nothing else in the server reads the environment name: the game rules, rates and checks are the same in both modes.

## When to use it

Use `--dev` to test on your own machine or with a few testers. On a server the public can reach, leave it off:
the developer exception page shows stack traces on the HTTP port.

GM works as in Production: only the `--gm` accounts have it, and without `--gm` nobody does (the loopback
`/ro3emu/gm` tool still works). The server's own Development rule, an empty list means every account
(`ServerHost.Resolve`), applies only when the server is started without `server.sh`.
