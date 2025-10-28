using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class MirrorTradeStrategy : Strategy
    {
        [NinjaScriptProperty]
        public string MirrorInstrumentName { get; set; } = "MNQ 12-25";
        [NinjaScriptProperty]
        public int ContractMultiplierZ { get; set; } = 1;
        [NinjaScriptProperty]
        public double MainStopLossDollars { get; set; } = 200;
        [NinjaScriptProperty]
        public double MainTakeProfitDollars { get; set; } = 100;
        [NinjaScriptProperty]
        public bool OppositeDirection { get; set; } = true;
        [NinjaScriptProperty]
        public bool EnableCopy { get; set; } = true;

        private Instrument mirrorInstrument;
        private int mirrorBarsInProgress = -1;
        private Account account;
        private DateTime lastActionTime = DateTime.MinValue;
        private TimeSpan minInterval = TimeSpan.FromSeconds(1);

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "MirrorTradeStrategy";
                IsUnmanaged = true; // for true OCO logic
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
            }
            else if (State == State.Configure)
            {
                mirrorInstrument = Instrument.GetInstrument(MirrorInstrumentName);
                if (mirrorInstrument == null)
                    throw new ArgumentException($"Could not resolve mirror instrument: {MirrorInstrumentName}");
                
                AddDataSeries(mirrorInstrument.MasterInstrument.Name, BarsPeriod.BarsPeriodType, BarsPeriod.Value);
            }
            else if (State == State.DataLoaded)
            {
                for (int i = 0; i < BarsArray.Length; i++)
                    if (BarsArray[i].Instrument.MasterInstrument.Name == mirrorInstrument.MasterInstrument.Name)
                        mirrorBarsInProgress = i;
            }
            else if (State == State.Realtime)
            {
                account = Account;
                if (!account.Name.Contains("Sim"))
                {
                    Print("WARNING: This strategy is for simulation only!");
                }
                if (account != null)
                    account.ExecutionUpdate += OnAccountExecutionUpdate;
            }
            else if (State == State.Terminated)
            {
                if (account != null)
                    account.ExecutionUpdate -= OnAccountExecutionUpdate;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 1)
                return;

            if (mirrorBarsInProgress >= 0)
            {
                var mainPos = Position;
                var mirrorPos = Positions[mirrorBarsInProgress];

                if (EnableCopy)
                {
                    if (mainPos.MarketPosition == MarketPosition.Flat && mirrorPos.MarketPosition != MarketPosition.Flat)
                        Print("ALERT: Main trade closed, mirror still active!");

                    if (mainPos.MarketPosition != MarketPosition.Flat && mirrorPos.MarketPosition == MarketPosition.Flat)
                        Print("ALERT: Mirror trade closed while main is open!");
                }
            }
        }

        private void OnAccountExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (!EnableCopy || e == null || e.Execution == null)
                    return;

                // Only act for orders on the main instrument
                if (e.Execution.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name)
                    return;

                if (e.Execution.Order == null || e.Execution.Order.OrderState != OrderState.Filled)
                    return;

                if (DateTime.UtcNow - lastActionTime < minInterval)
                    return;

                lastActionTime = DateTime.UtcNow;
                int mainQty = Math.Abs(e.Execution.Quantity);
                int mirrorQty = Math.Max(1, ContractMultiplierZ * mainQty);

                OrderAction mirrorAction = OppositeDirection
                    ? (e.Execution.MarketPosition == MarketPosition.Long ? OrderAction.SellShort : OrderAction.Buy)
                    : (e.Execution.MarketPosition == MarketPosition.Long ? OrderAction.Buy : OrderAction.SellShort);

                // Flatten existing mirror position before placing new orders
                var mirrorPos = Positions[mirrorBarsInProgress];
                if (mirrorPos.MarketPosition != MarketPosition.Flat)
                {
                    ExitLong(mirrorBarsInProgress);
                    ExitShort(mirrorBarsInProgress);
                }

                double tickValue = mirrorInstrument.MasterInstrument.PointValue * mirrorInstrument.MasterInstrument.TickSize;
                int tpTicks = DollarsToTicks(MainTakeProfitDollars, tickValue, mirrorQty);
                int slTicks = DollarsToTicks(MainStopLossDollars, tickValue, mirrorQty);
                double entryPrice = Close[mirrorBarsInProgress];

                string ocoId = Guid.NewGuid().ToString("N").Substring(0, 10);
                string entrySignal = "MirrorEntry_" + ocoId;

                // Mirror entry order
                SubmitOrderUnmanaged(mirrorBarsInProgress, mirrorAction, OrderType.Market, mirrorQty, 0, 0, ocoId, entrySignal);

                // TP and SL prices
                double tpPrice, slPrice;
                if (mirrorAction == OrderAction.Buy) // going long on mirror
                {
                    tpPrice = entryPrice + tpTicks * mirrorInstrument.MasterInstrument.TickSize;
                    slPrice = entryPrice - slTicks * mirrorInstrument.MasterInstrument.TickSize;
                    SubmitOrderUnmanaged(mirrorBarsInProgress, OrderAction.Sell, OrderType.Limit, mirrorQty, tpPrice, 0, ocoId, "TP_" + ocoId);
                    SubmitOrderUnmanaged(mirrorBarsInProgress, OrderAction.Sell, OrderType.StopMarket, mirrorQty, 0, slPrice, ocoId, "SL_" + ocoId);
                }
                else // going short on mirror
                {
                    tpPrice = entryPrice - tpTicks * mirrorInstrument.MasterInstrument.TickSize;
                    slPrice = entryPrice + slTicks * mirrorInstrument.MasterInstrument.TickSize;
                    SubmitOrderUnmanaged(mirrorBarsInProgress, OrderAction.BuyToCover, OrderType.Limit, mirrorQty, tpPrice, 0, ocoId, "TP_" + ocoId);
                    SubmitOrderUnmanaged(mirrorBarsInProgress, OrderAction.BuyToCover, OrderType.StopMarket, mirrorQty, 0, slPrice, ocoId, "SL_" + ocoId);
                }

                Print($"[MirrorTradeStrategy] OCO linked: TP={tpTicks} ticks, SL={slTicks} ticks, OCO={ocoId}");
            }
            catch (Exception ex)
            {
                Print("[MirrorTradeStrategy] Error: " + ex.Message);
            }
        }

        private int DollarsToTicks(double dollars, double tickValue, int qty)
        {
            if (tickValue <= 0 || qty <= 0) return 0;
            double ticks = dollars / (tickValue * qty);
            return Math.Max(1, (int)Math.Round(ticks, MidpointRounding.AwayFromZero));
        }
    }
}
