# Trade Exchange for solo play

The Trade Exchange (交易行, `TradeService`, [trade-stalls.md](../systems/trade-stalls.md)) only shows items that
characters have listed. On a realm with few or no other players it is empty. Two optional aids fix that. Neither is
original behaviour: TradeConfig's own copy/recycle triggers (`_iCopyTrigger*`, `_iRecycleTriggerPrice`) are switched
off on every row of this client.

Code: `src/Ro3Emu.Server/Game/TradeService.System.cs`, options in `src/Ro3Emu.Server/ServerOptions.cs`
(`ExchangeOptions`).

```json
"Ro3Emu": {
  "Exchange": {
    "SystemSeller": true, "Stock": 10, "UniqueStock": 2, "RestockMinutes": 30, "SellPrice": 1.0,
    "BuyBack": true, "BuyBackMinutes": 10, "BuyBackPrice": 1.0
  }
}
```

Both are off by default. `appsettings.Development.json` turns both on, so local play (`RO3Emu.exe` without hosting)
has them, and so does `server.sh --dev` ([development mode](development-mode.md)). A hosted server can turn them on with `--Ro3Emu:Exchange:SystemSeller=true --Ro3Emu:Exchange:BuyBack=true`.

## System seller

`SystemSeller` lists every market item in TradeConfig (`_iTrade` 1, the current season's rows: 450 cards,
equipment, enchant materials, blueprints and appearances) from a built-in seller.

| Option | Meaning |
| --- | --- |
| `Stock` | Units kept listed of each stackable item, as one listing |
| `UniqueStock` | Single pieces kept listed of each equipment or other unstackable item, one listing each |
| `RestockMinutes` | How often the seller tops its stock back up (1 to 1440) |
| `SellPrice` | Price as a multiple of the item's recommended price (`_iBasePrice`), kept in its price range |

- Its listings show seller role 0, have no public listing period and never expire into mail.
- Buying works as with a player listing: the goods arrive by mail (1064). The price goes to nobody.
- Equipment, totems and emblems roll their attributes as a newly granted item does.
- Goods carry TradeConfig `_kIsBindGive` from the moment they are listed: for example cards are bound for 14 days
  (`[2, 1209600]`) and can't be resold to the exchange until then.
- Turning `SystemSeller` off removes its listings on the next pass.

## Buy-back

`BuyBack` lets the built-in seller buy a player's whole listing once it is past any public listing period and
`BuyBackMinutes` old, when its unit price is at most `BuyBackPrice` times the recommended price. The seller is paid
by the usual sale mail (1063), less the usual trade tax. Listings with a reservation (preorder) are left alone.

So listing an item at the recommended price sells it within `BuyBackMinutes` plus up to a minute. A higher price waits
for a real buyer, as before.

## Economy ledger

Restock is a `SystemRestock` source and retired stock a `SystemRetire` sink, in the `TradeListing` compartment of
system owner `trade-exchange`. A purchase from it is an `NpcShopPayment` sink of the buyer's currency. A buy-back is an
`NpcShopBuyback` source of the seller's proceeds and a `SystemRetire` sink of the listed goods.
