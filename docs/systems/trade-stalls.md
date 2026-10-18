# Trade, stalls and settlement mail

Client evidence is static Lua, configuration and protobuf. Behavior in the original client has not been verified. Bot tests exercise the actual TCP/protobuf and micro/msgpack interfaces. This is not a claim of captured original-server parity.

## Implemented interfaces

| Area | Messages / datasets | Behavior |
| --- | --- | --- |
| Trading house | 32602-32610, 32612-32621, 32623, 32625-32626, 32628-32630; 5033 `DBUserTradeInfo` | Catalogue, prices, listing slots, instance escrow, ordinary aggregate and premium SKU purchases, publicity reservations, cancellation, repricing, follows, refresh, history, expiry and reminders. 32623 returns an empty penalty list; no penalty engine is claimed. |
| Player stalls | 33401, 33403-33411, 33413-33418; 5058 `DBStallsInfo` | Open, put/remove/edit stock, authoritative purchase, close, seller/buyer views, rename, leave, price bands, settlement, item ordering and share-content validation. |
| Stall scene display | 31037 AOI and 31049 entity updates | Entity type 9, `NpcStallsData`, seller appearance, stable world actor, browsing subscriptions and closure/removal. Persisted stalls return even while the seller is offline. |
| System stalls | 33409 / 33406 | Persistent shared stock from `StallSystemItem` and `StallSystemTrigger`, per-role purchase counters, season/day filtering, restock and lifetime. |
| Delivery dependency | 31602; micro `MailService` | The canonical `Game/MailService` ([mail.md](mail.md)) owns list/content/read/delete/syncGroupMail, notices and claims for both ordinary and market mail. Templates 1027-1032 and 1063-1067 carry actual items/currency; a purchase never also grants its attachment directly. |

All 820 `TradeConfig` rows are consumed as configuration. The client wrapper accepts `_iTrade == 1` for market and `== 2` for stalls; it does not accept enum 3 in these predicates. Season checks use `SeasonService.PlayBook`; server-day checks use `SeasonService.Start`. No `MultiverseSchedule` dependency was introduced. Stall catalogue contains 54 item rows and 10 trigger rows; triggers without goods do not create an empty shop.

`MarketStore` is the shared ownership boundary. A single SQLite transaction includes escrow, bag rows, currency, role metadata, reservation state, history and settlement mail. Every instance transfer starts from `InvItem.Clone()`. Attributes, string attributes, enchantments, binding and other instance fields survive. Detached live state is published only after commit. Rollback sends a refusal without granting, charging, consuming stock or creating mail. Post-commit hook/notification failures are logged and cannot turn a committed grant into a failed claim. Claims allocate destination bag GUIDs.

## Evidence and inferred policies

Protocol schemas are in `src/Ro3Emu.Server/Protos/romsg/`. Lua paths below are relative to `re/clientB/lua-src/`.

The listing-fee formula is supported by `UI/Trade_Stall/Controllers/UI_Sys_TradeSalePanelCtrl.lua:724-791`: currency 1 uses 700 basis points clamped to 700-50000; currency 2 uses 800 basis points clamped to 800-10000; round upward and pay the displayed fee in numeric currency 2. These numeric IDs take precedence over inconsistent translated currency names.

The following decisions remain **(inferred)** and are not original-server captures:

| Area | Inferred decision / evidence boundary |
| --- | --- |
| Storage and ordering | SQLite JSON escrow rows and the canonical per-role mailbox blob, stable generated SKU/SPU and mail IDs, per-session locking, commit before success/sync, fresh claimed GUIDs. Wire shape is proven; original persistence/allocator/secondary message ordering is not. |
| Trade initial state | Six free listing slots and credit-score ratio 1; credit default comes from `PublicParamConfig`. `tradeTimes` counts successfully listed units, with no invented reset or quota. |
| Trade prices | Stable range is base price times `_kStableRange / 10`, ladder increments base/10; no recovered supply-driven pricing algorithm. Unknown query filters fail closed. Page sizes are bounded. |
| Trade eligibility | The decomposition lock is also an escrow lock. Appraised totems require an explicit skill roll as a proxy until a complete rarity/talent classifier is recovered. Ownership, quantity and request mode are authoritative. Other card/emblem/appraisal restrictions follow `LC_TradeHelper.CheckCanTransactionByItem`. |
| Transfer binding | Preserve the source instance's binding for player trades/stalls. `TradeConfig._kIsBindGive` exists but its application on player transfer is not established by the extracted Lua; exact new-binding assignment remains unproven. System-generated stock uses its own `StallSystemItem._kIsBindGive` at creation. |
| Trade fee/tax | Listing fee is debited at successful listing; 32613 `tax` echoes it. Seller settlement uses `TradeConfigTax` with floor rounding and zero tax for numeric currency 1, whose original settlement rule is absent. |
| Publicity | Premium listings use the first `TradeItemHideTime` minimum (1800 seconds); authoritative server-level selection is unavailable. Preorders reserve full payment; a durable sequence settles FIFO, including same-timestamp orders. Partial/losing amounts refund by mail. No speculative credit-point cost is charged. |
| Preorder collection | Cancel hides an active order and refunds once. Receive All acknowledges already-settled `NotSelected`/`Offline` rows without another payout; hidden terminal rows stay absent from 32628. Additional original Offline transitions are unproven. |
| Listing lifecycle | Configured 24/168 display values are hours. A one-second maintenance callback also processes expiry/publicity; after a cold start with no sessions, overdue work catches up on the first login. Return/settlement templates 1063-1067 and substitutions follow available mail config but remain uncaptured. Repricing a reserved listing is refused; manual delisting returns the remaining item and refunds active reservations. |
| Penalties | No recovered review, credit adjustment, recycling/copying or punishment engine; 32623 is an empty state. |
| Stall placement | `StallsMapID=[10100]` wins over conflicting all-open-world localization; finite coordinates and configured spacing are checked. Existing emulator world has one line; scene instance and line are represented as 1. No additional navigability/authorized-area polygon is inferred. |
| Stall identity | Stall ID equals seller role ID; separate stable AOI IDs and model 25000002. Career, sex, level and appearance come from the seller. Template/UUID relationship needs original capture. |
| Stall prices | Top items use 10/10; ordinary stable min/max comes from config. The DAL proves `floor(base*step/10+0.5)` for selectable prices. Hidden dynamic pricing is absent. |
| Stall fee/tax | Fixed opening fee is debited once. Weekly gross aggregates stored numeric currency amounts without an unproven conversion; Monday 00:00 UTC resets it. Tier before the sale determines ceiling-rounded tax; `curTax.key` is basis points. Tier boundary equality and accounting period are unproven. |
| Stall lifetime | Seven-day lifetime uses `StallsTime`; empty stock auto-closes after six minutes from opening, following localization 37825 rather than silently treating `StallsSafeFirstTime=3` as minutes. Remaining escrow returns by the configured close/timeout/empty template; sold proceeds were already mailed. |
| Stall access/share | Remote links may be browsed; buying requires the same map/line and `BoothRange`. Share validation enforces text shape/length/cooldown, with Unicode rune length; no recovered profanity dictionary. Accepting 33418 does not send chat. |
| System availability | Empty or `[1,1,day]` item conditions are interpreted as server day from `SeasonService.Start`; unknown array conditions fail closed. Position and sale lists come from trigger config. |
| System scheduling | Instantiate on scene use; choose first configured career/level and fixed sex; lower initial/restock stock bounds, upper initial stock cap, configured 600-second cadence and three-day lifetime. `_iBuyLimit` is cumulative per role per generated stall lifetime. Original dynamic target/launch/cooldown/forbid/random-selection behavior is not reconstructed. |
| Mail protocol policies | Authenticated active role owns its mailbox; decimal string IDs, Unix-second creation time, newest-first pagination capped at 150. Market deliveries use system category 1 alongside ordinary mail; the canonical service retains guild category 3 and reconnect reconciliation. Content parameters use `LanguageData` strings. |
| Mail ownership | Claims follow the canonical mail service's deduplicated, partial bulk semantics: only successfully committed IDs are echoed. Each letter grants its complete attachment set atomically, with bag-capacity and currency-overflow checks. Delete refuses unclaimed attachments. Market text-only mail expires after 30 days; market asset mail stays claimable without expiry. Ordinary mail keeps its configured expiry. Claimed display attachments remain as history and cannot grant again. |

Further concrete Lua evidence: `Logic/Asset/LC_DAL_Trade.lua` (5033, queries, commodity bytes, preorder receive), `Logic/Trade/LC_TradeServer.lua` (requests), `Config/CFG/Lua_CFG_TradeConfig.lua` (eligibility), `Config/CFG/Lua_CFG_TradeConfigTax.lua` (tax thresholds), `Logic/Asset/LC_DAL_Stall.lua` (5058, views, price rounding, closure), `Logic/Stall/LC_StallHelper.lua` (item gates and merchant slots), and `Logic/Unit/LC_Unit_Stall.lua` / `Utility/Lua_DB_Helper.lua` (AOI consumption). Configuration keys are cited beside their implementations.

## Canonical mailbox integration

Trade and stall columns, datasets and login hooks coexist with the canonical mailbox ([mail.md](mail.md)). There is one game claim route, `MailService.ClaimMail` (31602), and one micro `MailService` registration. Market deliveries enter the role's ordinary `mailbox` blob in the same SQLite transaction that changes escrow, balances and stock. A failed transaction leaves neither a delivered letter nor an escrow transfer. Notifications follow commit and remain recoverable through mailbox login/reconnect reconciliation.

**(inferred persistence design)** A shared mailbox semaphore serializes regular delivery, micro mutations, market delivery and claims. The order is market gate, ordered session state locks, mailbox gate, then SQLite; ordinary claims start with their session lock and micro operations start with the mailbox gate. No mailbox operation acquires a session lock while holding the mailbox gate. Legacy `market_record` mail is imported atomically, preserving attachment metadata, read/claimed flags and template parameters, then removed from the old store. A retry or restart cannot grant a migrated claimed letter again.

A physically missing recipient makes the transactional mailbox write fail and rolls back the transfer. A soft-deleted role still has a durable row: its settlement and migrated mail remain there, as do its retained inventory and currency. Selecting or restoring a deleted role is a separate account lifecycle boundary. Preserving those bytes avoids discarding old assets or letting one deleted recipient block migration and unrelated settlements.

The configured stall template names are in `Config/ComplexConfig/PublicParamConfig.lua:952-968`. Exchange templates 1063-1067 have explicit sale/purchase/refund/manual-return/expiry-return text in `MailConfig.json` and `Localization_en.json`. Their `${N}` substitutions use serialized `LanguageData` in `Params.content_params`, as decoded by `Logic/Asset/LC_DAL_Mail.lua:1734-1850`. Item names, quantities, proceeds, taxes, buyer names, order IDs and transaction times come from the actual transaction. Formatting and values for unused substitution positions remain **(inferred)**; no original-server mail capture is claimed.

Market asset letters explicitly have no expiry, preserving the market's ownership guarantee. `UI/Mail/Controllers/UI_Sys_MailListWidgetCtrl.lua:187-206` handles `RemoveTime <= 0` without an expiry timer. The special timer modes on templates 1028/1029 therefore receive an explicit no-expiry boundary. This does not change expiry for ordinary raw/template mail. `StallsSystemMailId=1201` describes backpack-overflow delivery; switching every existing always-mailed system purchase to that template would assert an unimplemented overflow policy, so the existing purchase template 1028 remains.

## Verification

`TradeTests`, `StallsTests`, `MarketMailTests` and `MailTests` use bots over the real game and micro interfaces, with temporary data directories and free non-default ports. Coverage includes ordinary and premium market buying; two competing publicity buyers; duplicate reservation refunds and receive/cancel visibility; player and system stall buying with two bots; lock/binding, unlock, ownership, quantity, stale-price, currency, distance and limit refusals; full instance metadata; mail ownership/replay/capacity; concurrent delivery/read/claim; forced migration rollback and restart; injected SQLite failure after staged mail/stock changes; failing post-commit hooks; relog and a fresh host over the same database. Cold restart checks partial market/stall escrow, offline stall AOI, unclaimed mail and persistent system purchase limits. Preorders with equal timestamps settle in a durable sequence order.

The three market classes share a nonparallel xUnit collection; bot-level competing purchases remain concurrent. Targeted command:

```powershell
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~TradeTests|FullyQualifiedName~StallsTests|FullyQualifiedName~MarketMailTests"
```

Full fast suite with serialized test hosts:

```powershell
dotnet test tests/Ro3Emu.Tests --filter "Category!=Slow" --settings tests/Ro3Emu.Tests/serial.runsettings
```

## Explicit omissions

- The separate 820xx auctions are documented in [auction.md](auction.md); dormant 325xx `TM_*` messages are not implemented.
- 32601 has no recovered sender; 32611 has no extracted schema; 32622 queue notification is not emitted; no invented 32624/32627 behavior.
- 33402 line-query schemas are missing; 33422 avatar sync has no recovered active consumer. Cross-line behavior is unavailable in the current single-line world.
- Original market dynamics, anti-abuse/credit review and dynamic system-stall scheduler remain unproven as described above.
- TradeConfig copy/recycle triggers are disabled on every row and not implemented. The optional built-in seller and buy-back for solo play ([exchange.md](../guide/exchange.md)) are emulator aids, not reconstructions.
- Ordinary and guild mail use the canonical mail service ([mail.md](mail.md)); dormant legacy protocols and localization presentation are not extended here. Ordinary-client UI rendering, attachment msgpack decoding, request reattachment and original-server packet comparison have not been verified.
