# Cash shop and Top Up

The shop panel (`UI_Sys_ShopPanelCtrl`) has three top tabs, each gated by a FunctionConfig row:

- **Fashion**: type 7, function 7004.
- **Mall**: type 1, function 7001.
- **Points**: type 2, function 7002.

The left tabs are the `ShopConfig` rows of that type whose function is open, and their `_iClassID`s become the
sub-tabs. The cash shop is Fashion and Mall.

## Currencies

| Item | Name | Client enum | Use |
|---|---|---|---|
| 3 | Sacred Relic Crystal | `Miao` | Paid currency: Crystal Shop, Magic Fashion, all of Fashion |
| 16 | Sacred Relic Crystal | `ServerBound` | Bound crystal: Top Up bonuses and the first-purchase double. No commodity names it; a crystal price spends it first |
| 1 | Zeny | `Diamond` | Zeny Shop |
| 2 | Minted Coin | `Zeny` | Supply Shop |

## Buying (82332 / 82340)

`ShopService` already serves every tab:
- It prices from `_kItemPerPrice` and `_iDiscount` (basis points paid) and applies the BuyLow merchant discount.
- It enforces `_iPurchaseTimesLimit` / `_iPurchaseNum` (1 daily, 2 weekly, 3 monthly, 4 lifetime, 5 season; periods
  turn at 05:00) and the `_iIsBuy` / `_iIsLock` conditions.
- It syncs the counters in 5024.
- A Fashion commodity unlocks its wardrobe look in the same commit.

No `ShopConfig` row has a refresh (`_kRefreshType/_kRefreshTime/_kRefreshExpend` are empty everywhere).

## Top Up (2001-2004), `Ro3Emu:TopUp:Free`

The client reads the global `RechargeExternalUrl` before opening Top Up. This build's PublicParamConfig sets it to
`https://console.genesis3.is/`, so every Top Up button opens that website. The client hook `TopUpButton` empties it,
and then the buttons open the in-game `UI_Sys_TopUpPanel`.

With `TopUp:Free` on, the server answers that panel without any payment provider (`ShopService.TopUp.cs`):

| Message | Client | Server |
|---|---|---|
| 2003 GET_RECHARGE_FIRST_AWARD_RESULT | sent when the panel opens; `idList` = tiers that show the first-purchase double | the `RechargeConfig` ids this role has not bought |
| 2001 CREATE_ORDER `{chargeType, chargeId, ...}` | `chargeType` 1 = `RechargeConfig` tier, 2 = `ProductConfig` (month cards, passes) | grants at once and answers result 0, a fresh `orderId` and `orderData` `"{}"`. On PC the client would open `orderData.data.transaction_url`; there is none, so it only logs a parse error |
| 2004 UPDATE_RECHARGE_FIRST_AWARD_RESULT (push) | removes that tier's first-purchase badge | sent after a tier's first purchase |
| 2002 PAY_RESULT | logged only | result 0 (`FeatureUnavailable` when off) |

**What a tier grants:**
- `_kAward` (item 3), plus either:
  - `_kFirstChargeAward` on its first purchase, or
  - `_kAwardBonus` on later purchases.

  Both are item 16. That the double replaces the regular bonus is **(inferred)** from the tier widget, which shows one
  or the other. Tiers bought are counted per role in the shop blob (`ShopState.Recharged`).

**What a product grants:** its `_iAward`, a MultiverseGroupConfig id. For example:
- Products 4 and 5 give 701 and 702 (month cards).
- Product 6 gives both.
- Products 7 and 8 give 703 (pass premium) and 1020001 (pass gift).

NewActivityService turns those items into the card or premium track as usual.

The PC tiers are ids 13-18: $0.99 / 4.99 / 14.99 / 29.99 / 49.99 / 99.99 for 60 / 300 / 980 / 1980 / 3280 / 6480
crystals. Their bonuses are 0 / 30 / 110 / 260 / 600 / 1600 bound crystals, and the first-purchase double equals the
crystal amount. The Top Up function 7003 opens at base level 20.

With `TopUp:Free` off (the default, which keeps the original's refusals), 2001-2003 answer `FeatureUnavailable`.
`appsettings.Development.json`, `server.sh` and `RO3Emu.exe` (local and hosted) turn it on.

## Currency exchange (2005)

`UI_Sys_CoinExchangeWidget` sends `{itemId = the item paid, count}`. `ItemExchangeConfig` is keyed by the paid item:
- 3 crystal to 1 Zeny, x100, function 7005.
- 1 Zeny to 2 Minted Coin, x100, function 7006.

Both have `_iSingleLimit` 999999 and open at base level 20. This is not a payment, so it is served whatever `TopUp:Free` says.

## Bound crystals

A crystal price (item 3) takes bound crystals (item 16) first and regular crystals for the rest (`ShopService.Payment`,
every Mall and Fashion buy). No config says so: the rule is **(inferred)** from how bound currency usually works, and
was agreed with the project owner. The client's buy dialog still checks the price against regular crystals alone
(`LC_ItemHelper.GetItemCount(3)` reads only currency 3), so a role needs that many regular crystals for the client to
send the buy at all.

## Not done

- Gifting a look (82335 is refused with 2317; 82336/82337/82339 have no handler).
- `_kIsBindGive` on shop goods, and the server-only `_iGlobalPurchase` (meaning unknown).
