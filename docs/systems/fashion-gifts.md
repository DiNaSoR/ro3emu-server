# Fashion gifts

A role can buy a Fashion-tab look (ShopConfig type 7, a ShopCommodityConfig row with `_iAppearanceId`) for a friend.
The price is paid at once and a gift order waits for the friend, who claims it from the gift card in their private
chat. An order nobody claims within a day gives the price back to the sender by mail. Server: `ShopService.Gifts`,
routes in `EconomyMessages`, tests in `GiftTests`.

This is separate from the friend item gifts (Give Gift, the Gift Record panel), which are `FriendService` gifts.

## Client sources

| Piece | Client |
|---|---|
| Requests | 82335 `CommodityPresentReq {targetId, buyReq, giftContent}` → `CommodityPresentResp {result, orderId, commodityId, targetId, giftContent}`; 82336 `CommodityPickReq {orderId}` → `{result}` (`MG_Shop.proto`) |
| Pushes | 82337 `CommodityPresentListNtf` (orders to me) and 82339 `SentGiftsListNtf` (orders from me); each replaces the whole list (`LC_DAL_Shop`) |
| Panels | Fashion shop gift button (`UI_Sys_AppearanceShopWidgetCtrl`), friend picker (`UI_Sys_Shop_GiftWidgetCtrl`), message and confirm (`UI_Sys_FriendGiftlWidgetCtrl`, `UI_Sys_Shop_GiftConfirmWidgetCtrl`), the Give Gift Appearance page (`UI_Sys_Friend_GiveGiftWidgetCtrl`) |
| Chat cards | `UI_Sys_Friend_ChatListLeftWidgetCtrl` (receiver, Pick button) and `...RightWidgetCtrl` (sender) read the order state from the two lists |
| Config | `PublicParamConfig` `AppearanceMessage` 20, `OrderExpireTime` 86400, `PresentRefundMailId` 1115; `ErrorCodeConfig` 2312-2328, 6901-6905 |

## Sending (82335)

The sender's own client posts the gift card in the private chat from the answer (`orderId`, `commodityId`,
`targetId`, `giftContent`), so a refusal never shows a card. Refused: not a friend 2312, yourself 2325, not a Fashion
look 2317, the friend already owns it 2316, an unclaimed order of the same look already waits for them 2318, a message
over 20 characters 2327, then the shop's own gates (function, buy conditions, season) and the price.

The price is the commodity's `_kItemPerPrice` in its own currency (regular crystals, item 3), never the bound-crystal
split of a normal purchase, because the order and its refund carry one currency *(inferred)*. Paying, and the sender's
order in the role blob `present_sent`, commit together (`cd13a/shop-gift`). The gift counts against no purchase limit
and unlocks nothing for the sender.

## Claiming (82336)

Only the receiver, only while the order is pending and less than a day old (6902, 6903, 6905; unknown order 6901). The
look, its suit pieces and its `_iAppearanceAwardId` bonus unlock through the same wardrobe path as a purchase, at no
cost, and the order is saved in the receiver's `present_received` in that commit. The client's answer handler is empty:
the card turns "claimed" with the 82337/82339 pushes, and the wardrobe sync and unlock notice show the look. If the
receiver unlocked the look some other way meanwhile, the claim answers 2316 and the order later refunds *(inferred)*.

## Expiry and lists

- One process deadline waits for the earliest pending order. At `OrderExpireTime` each expired order sends mail 1115
  ("Appearance Gift Expiration Return") with the price to the sender, once (delivery key `present-refund:{order}`), in
  the commit that marks the sender's copy Refunded.
- Both lists go out at login and to whichever side is online after every change. Nothing in the client asks for them.
- A card whose order is missing shows "refunded", so finished orders are kept 30 days (the refund letter's lifetime,
  *inferred*).
- The realm's order index is built at startup from every role's `present_sent` and `present_received`.

## Not done

- The friend picker greys out friends who already own the look from the UserData "Appearance" field, which the server
  does not serve yet: every friend shows as not owning it (the server still refuses with 2316).
- The message is trimmed and length-checked, not word-filtered (2326).
