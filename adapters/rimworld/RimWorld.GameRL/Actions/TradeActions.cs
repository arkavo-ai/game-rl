// Trade actions for RimWorld GameRL

using System;
using System.Linq;
using System.Text;
using GameRL.Harmony.RPC;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Trading actions for buying/selling with active traders
    /// </summary>
    [GameRLComponent]
    public static class TradeActions
    {
        /// <summary>
        /// List items available from an active trader with prices
        /// </summary>
        [GameRLAction("ListTraderGoods", Description = "List items available from an active trader with prices")]
        public static string ListTraderGoods(
            [GameRLParam("ColonistId")] Pawn negotiator,
            [GameRLParam("TraderId")] Pawn trader)
        {
            if (negotiator == null)
                throw new InvalidOperationException("ListTraderGoods: ColonistId not found. Use a ThingID from Entities.Colonists");
            if (trader == null)
                throw new InvalidOperationException("ListTraderGoods: TraderId not found. Use an ID from ActiveTraders/Entities.Visitors");

            if (trader.TraderKind == null)
                throw new InvalidOperationException($"ListTraderGoods: {trader.LabelShort} ({trader.ThingID}) is not a trader. Check ActiveTraders for valid trader IDs.");

            if (TradeSession.Active)
            {
                try { TradeSession.Close(); } catch { }
            }

            try
            {
                TradeSession.SetupWith(trader, negotiator, false);

                var result = new StringBuilder();
                result.AppendLine($"Trader: {trader.LabelShort} ({trader.Faction?.Name})");

                var silverTradeable = TradeSession.deal.AllTradeables
                    .FirstOrDefault(t => t.IsCurrency);
                if (silverTradeable != null)
                {
                    result.AppendLine($"Trader Silver: {silverTradeable.CountHeldBy(Transactor.Trader)}");
                    result.AppendLine($"Colony Silver: {silverTradeable.CountHeldBy(Transactor.Colony)}");
                }

                result.AppendLine("--- FOR SALE ---");
                foreach (var tradeable in TradeSession.deal.AllTradeables
                    .Where(t => !t.IsCurrency && t.CountHeldBy(Transactor.Trader) > 0)
                    .OrderBy(t => t.ThingDef.defName))
                {
                    int count = tradeable.CountHeldBy(Transactor.Trader);
                    float price = tradeable.GetPriceFor(TradeAction.PlayerBuys);
                    result.AppendLine($"  {tradeable.ThingDef.defName}: {count} @ {price:F0} silver");
                }

                result.AppendLine("--- YOU CAN SELL ---");
                foreach (var tradeable in TradeSession.deal.AllTradeables
                    .Where(t => !t.IsCurrency && t.CountHeldBy(Transactor.Colony) > 0)
                    .OrderBy(t => t.ThingDef.defName))
                {
                    int count = tradeable.CountHeldBy(Transactor.Colony);
                    float price = tradeable.GetPriceFor(TradeAction.PlayerSells);
                    result.AppendLine($"  {tradeable.ThingDef.defName}: {count} @ {price:F0} silver");
                }

                TradeSession.Close();
                Log.Message($"[GameRL] ListTraderGoods: Listed goods for {trader.LabelShort}");
                return result.ToString();
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                try { TradeSession.Close(); } catch { }
                throw new InvalidOperationException($"ListTraderGoods: Failed - {ex.Message}");
            }
        }

        /// <summary>
        /// Buy items from an active trader using colony silver
        /// </summary>
        [GameRLAction("BuyFromTrader", Description = "Buy items from an active trader using colony silver")]
        public static void BuyFromTrader(
            [GameRLParam("ColonistId")] Pawn negotiator,
            [GameRLParam("TraderId")] Pawn trader,
            [GameRLParam("ItemDefName")] string itemDefName,
            [GameRLParam("Count")] int count)
        {
            if (negotiator == null)
                throw new InvalidOperationException("BuyFromTrader: ColonistId not found");
            if (trader == null)
                throw new InvalidOperationException("BuyFromTrader: TraderId not found. Check ActiveTraders.");
            if (string.IsNullOrEmpty(itemDefName))
                throw new InvalidOperationException("BuyFromTrader: ItemDefName is required (e.g., ComponentIndustrial, Steel, MedicineIndustrial)");
            if (count <= 0)
                throw new InvalidOperationException("BuyFromTrader: Count must be positive");

            if (trader.TraderKind == null)
                throw new InvalidOperationException($"BuyFromTrader: {trader.LabelShort} is not a trader");

            if (TradeSession.Active)
            {
                try { TradeSession.Close(); } catch { }
            }

            try
            {
                TradeSession.SetupWith(trader, negotiator, false);

                var tradeable = TradeSession.deal.AllTradeables
                    .FirstOrDefault(t => !t.IsCurrency &&
                        t.ThingDef.defName.Equals(itemDefName, StringComparison.OrdinalIgnoreCase));

                if (tradeable == null)
                {
                    var available = string.Join(", ", TradeSession.deal.AllTradeables
                        .Where(t => !t.IsCurrency && t.CountHeldBy(Transactor.Trader) > 0)
                        .Take(15)
                        .Select(t => t.ThingDef.defName));
                    TradeSession.Close();
                    throw new InvalidOperationException($"BuyFromTrader: '{itemDefName}' not found. Trader has: {available}");
                }

                int traderHas = tradeable.CountHeldBy(Transactor.Trader);
                if (traderHas <= 0)
                {
                    TradeSession.Close();
                    throw new InvalidOperationException($"BuyFromTrader: Trader has no {itemDefName}");
                }

                int toBuy = Math.Min(count, traderHas);
                tradeable.ForceTo(toBuy);

                bool traded;
                TradeSession.deal.TryExecute(out traded);
                TradeSession.Close();

                if (traded)
                    Log.Message($"[GameRL] BuyFromTrader: Bought {toBuy} {itemDefName} from {trader.LabelShort}");
                else
                    throw new InvalidOperationException($"BuyFromTrader: Trade failed — insufficient silver. Sell items first with SellToTrader.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                try { TradeSession.Close(); } catch { }
                throw new InvalidOperationException($"BuyFromTrader: Failed - {ex.Message}");
            }
        }

        /// <summary>
        /// Sell colony items to an active trader for silver
        /// </summary>
        [GameRLAction("SellToTrader", Description = "Sell colony items to an active trader for silver")]
        public static void SellToTrader(
            [GameRLParam("ColonistId")] Pawn negotiator,
            [GameRLParam("TraderId")] Pawn trader,
            [GameRLParam("ItemDefName")] string itemDefName,
            [GameRLParam("Count")] int count)
        {
            if (negotiator == null)
                throw new InvalidOperationException("SellToTrader: ColonistId not found");
            if (trader == null)
                throw new InvalidOperationException("SellToTrader: TraderId not found. Check ActiveTraders.");
            if (string.IsNullOrEmpty(itemDefName))
                throw new InvalidOperationException("SellToTrader: ItemDefName is required (e.g., WoodLog, Steel, RawRice)");
            if (count <= 0)
                throw new InvalidOperationException("SellToTrader: Count must be positive");

            if (trader.TraderKind == null)
                throw new InvalidOperationException($"SellToTrader: {trader.LabelShort} is not a trader");

            if (TradeSession.Active)
            {
                try { TradeSession.Close(); } catch { }
            }

            try
            {
                TradeSession.SetupWith(trader, negotiator, false);

                var tradeable = TradeSession.deal.AllTradeables
                    .FirstOrDefault(t => !t.IsCurrency &&
                        t.ThingDef.defName.Equals(itemDefName, StringComparison.OrdinalIgnoreCase));

                if (tradeable == null)
                {
                    var available = string.Join(", ", TradeSession.deal.AllTradeables
                        .Where(t => !t.IsCurrency && t.CountHeldBy(Transactor.Colony) > 0)
                        .Take(15)
                        .Select(t => t.ThingDef.defName));
                    TradeSession.Close();
                    throw new InvalidOperationException($"SellToTrader: '{itemDefName}' not tradeable. You can sell: {available}");
                }

                int colonyHas = tradeable.CountHeldBy(Transactor.Colony);
                if (colonyHas <= 0)
                {
                    TradeSession.Close();
                    throw new InvalidOperationException($"SellToTrader: Colony has no {itemDefName}");
                }

                int toSell = Math.Min(count, colonyHas);
                tradeable.ForceTo(-toSell);

                bool traded;
                TradeSession.deal.TryExecute(out traded);
                TradeSession.Close();

                if (traded)
                    Log.Message($"[GameRL] SellToTrader: Sold {toSell} {itemDefName} to {trader.LabelShort}");
                else
                    throw new InvalidOperationException($"SellToTrader: Trade failed");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                try { TradeSession.Close(); } catch { }
                throw new InvalidOperationException($"SellToTrader: Failed - {ex.Message}");
            }
        }
    }
}
