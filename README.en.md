# BalancePet Usage Analytics

`balancepet.ext.feature.usage-analytics` is an independent, read-only usage dashboard for
BalancePet. It provides a dark dashboard, overview cards, provider/model breakdowns, a
seven-day token trend, and recent requests based on locally recorded metadata: token
counts, cache reads, request success, time to first token, throughput, provider, account,
and model.

## 📊 Metric definitions

The cache metric follows the relay-console convention: `Cache Hit Rate = Cache Read / Input`.
`Input` is the complete input total, while the chart's uncached input series is
`Input - Cache Read`. The chart also shows `Output`, `Cache Creation`, and `Cache Read`,
so the four token categories can be compared.

## 🧭 Dashboard and navigation

The sidebar contains two views:

- **Dashboard**: overview cards, provider split, model distribution, and token trend. The account selector in the header switches between the combined view and a single account, and the balance snapshot, cumulative change and balance-based estimate all follow it, so the three figures always describe one scope. Names come from the host settings; an unknown account falls back to a shortened id.
- **Recent usage**: recent requests, duration, model, and reported token data. Client chips above the list (`全部` / `Codex` / `DeepSeek Harness` / …) each carry that client's record count and filter only this list — the dashboard aggregates stay unchanged. A filter whose client no longer reports falls back to "all" so the list can never be stuck empty.

If the host has only recorded task-lifecycle events, the status bar says so
explicitly instead of treating a zero Token value as a parser failure.

## 🔒 Data source and privacy

The extension never receives or reads API tokens, prompts, replies, cookies, or provider
websites. It reads only the local Usage Event v1 files and accepts an optional `--data-dir`
argument for testing or portable setups. Only sanitized counters and timings are used.
Client-reported Usage Event v1 data arrives over the local named pipe.

By default it reads only:

```text
%LOCALAPPDATA%\\BalancePet\\usage-events.ndjson
%LOCALAPPDATA%\\BalancePet\\csharp-settings.json   (monitors[].id and monitors[].name only)
```

The second file maps account ids to display names. It also holds DPAPI-protected access
tokens, so it is parsed through a DOM and only the `monitors[].id` and `monitors[].name`
strings are ever materialized; the document is never deserialized into a model that could
carry credentials, and its contents are never logged or forwarded.

The dashboard refreshes when the usage, balance or server-usage files change, and the header's Refresh button
re-reads the data directory on demand. There is no polling timer: the host writes a file for every new record,
so polling only repeated work. Opening the window sends the host one credential-free refresh signal (not a poll);
the host still honours its own minimum refresh interval. Token, cache and time-to-first-token cards
are populated only when the client reports those counters; the “Balance-change estimate” card
is populated from the host's balance-usage summary.
The “Balance-change estimate” card reads the credential-free
`%LOCALAPPDATA%\\BalancePet\\balance-usage.v1.json` summary published by the host.
It uses the same local balance-change ledger as the core usage window: only decreases
between successful balance observations count, so it is not a relay invoice or an exact
per-request price. It shows “No data” until a successful balance observation exists.
The total balance card reads the host's credential-free balance snapshot and sums accounts only
when their currencies match; no provider token is included in the snapshot. Its “Total spending”
value is the cumulative balance decrease tracked by the host for the current currency.
The accumulation restarts when the currency changes.

For New API-compatible relays, when the same site also exposes a read-only log endpoint, the
host writes the server's `quota` back to the matching request using the site's published quota
conversion rules. Other relays, log queries the endpoint refuses, and records that cannot be
matched reliably still show “unreported”; no value is guessed from public model pricing.

For relays that expose only the OpenAI-compatible `/v1/usage` endpoint, the dashboard also shows
a credential-free “server usage summary” read by the host from `daily_usage.actual_cost` (falling
back to `cost`). It is a daily aggregate, not an exact per-request charge; individual history rows
remain “unreported” when no request log is available.

Codex lifecycle hooks do not expose provider usage counters on every client version. Request
counts and locally measured duration remain valid, while token/model/cache fields stay unreported.

## 🧪 Local testing

When BalancePet is running, use the sender shipped with the core package to write a complete
local usage event:

```powershell
.\\tools\\balancepet-usage.ps1 -Provider Codex -Model gpt-5.6-sol `
  -InputTokens 64100 -OutputTokens 904 -CacheReadTokens 9000 `
  -DurationMs 98000 -TimeToFirstTokenMs 18500 -ToolCalls 0 -Steps 5
```

The sender transmits counters only over the local named pipe and never reads
or accepts prompts, replies, cookies, API keys, or other credentials.

The independent extension ZIP intentionally excludes executable scripts. The repository source
contains `tools/emit-usage-event.ps1` for local protocol testing, while the distributable core
package provides `tools/balancepet-usage.ps1` in its `tools` directory.

## 🧩 Compatibility and extension contract

This package implements the shipped **BalancePet Feature Extension API v1** and requires
BalancePet `1.4.0` or newer for Codex token and cache counters. Older cores can still show
request metadata but cannot populate those counters. The extension version is independent from
the core version; the current package version is `0.4.0`.

The feature-extension contract covers the manifest, out-of-process launch, capabilities,
Usage Event v1, local data directory, update behavior, and package safety. It does not require
a shared UI toolkit, theme, logo, typography, language, or window layout.

Full contract:
[BalancePet Feature Extension Specification v1](https://github.com/GoldenMoon-cell/BalancePet/blob/main/docs/extension-spec/feature-v1/README.md)

## 🔨 Build

Run from the repository root:

```powershell
.\\tools\\package.ps1
```

The resulting ZIP can be released independently from BalancePet core updates.

## 📜 License

This extension is licensed under the MIT License; see [LICENSE](LICENSE).
