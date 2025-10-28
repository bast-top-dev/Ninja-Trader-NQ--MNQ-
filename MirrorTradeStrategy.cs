using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class MirrorTradeStrategy : Strategy
    {
        #region Properties
        
        // Main Instrument Configuration
        [NinjaScriptProperty]
        [Description("Account where main trades are executed (Father account)")]
        public string MainAccountName { get; set; } = "Sim101";
        
        [NinjaScriptProperty]
        [Description("How to define Main Instrument SL/TP")]
        public SLTPInputMethod MainSLTPMethod { get; set; } = SLTPInputMethod.Dollars;
        
        [NinjaScriptProperty]
        [Description("Stop Loss in dollars for Main Instrument")]
        public double MainStopLossDollars { get; set; } = 200;
        
        [NinjaScriptProperty]
        [Description("Take Profit in dollars for Main Instrument")]
        public double MainTakeProfitDollars { get; set; } = 100;
        
        [NinjaScriptProperty]
        [Description("Stop Loss in ticks for Main Instrument")]
        public int MainStopLossTicks { get; set; } = 20;
        
        [NinjaScriptProperty]
        [Description("Take Profit in ticks for Main Instrument")]
        public int MainTakeProfitTicks { get; set; } = 10;

        // Mirror Instruments Configuration
        [NinjaScriptProperty]
        [Description("First mirror instrument")]
        public string MirrorInstrument1 { get; set; } = "MNQ 12-25";
        
        [NinjaScriptProperty]
        [Description("Account for first mirror instrument")]
        public string MirrorAccount1 { get; set; } = "Sim101";
        
        [NinjaScriptProperty]
        [Description("Direction for first mirror instrument")]
        public MirrorDirection MirrorDirection1 { get; set; } = MirrorDirection.Opposite;
        
        [NinjaScriptProperty]
        [Description("Contract multiplier for first mirror instrument")]
        public int MirrorMultiplier1 { get; set; } = 1;

        [NinjaScriptProperty]
        [Description("Second mirror instrument")]
        public string MirrorInstrument2 { get; set; } = "";
        
        [NinjaScriptProperty]
        [Description("Account for second mirror instrument")]
        public string MirrorAccount2 { get; set; } = "";
        
        [NinjaScriptProperty]
        [Description("Direction for second mirror instrument")]
        public MirrorDirection MirrorDirection2 { get; set; } = MirrorDirection.Opposite;
        
        [NinjaScriptProperty]
        [Description("Contract multiplier for second mirror instrument")]
        public int MirrorMultiplier2 { get; set; } = 1;

        [NinjaScriptProperty]
        [Description("Third mirror instrument")]
        public string MirrorInstrument3 { get; set; } = "";
        
        [NinjaScriptProperty]
        [Description("Account for third mirror instrument")]
        public string MirrorAccount3 { get; set; } = "";
        
        [NinjaScriptProperty]
        [Description("Direction for third mirror instrument")]
        public MirrorDirection MirrorDirection3 { get; set; } = MirrorDirection.Opposite;
        
        [NinjaScriptProperty]
        [Description("Contract multiplier for third mirror instrument")]
        public int MirrorMultiplier3 { get; set; } = 1;

        // Global Settings
        [NinjaScriptProperty]
        [Description("Enable/disable trade copying")]
        public bool EnableCopy { get; set; } = true;

        [NinjaScriptProperty]
        [Description("Show real-time information panel")]
        public bool ShowInfoPanel { get; set; } = true;
        
        [NinjaScriptProperty]
        [Description("Alert when positions close independently")]
        public bool AlertOnUnsynchronizedClose { get; set; } = true;

        #endregion

        #region Enums
        
        public enum SLTPInputMethod
        {
            Dollars,
            Ticks
        }
        
        public enum MirrorDirection
        {
            Opposite,
            Same
        }
        
        #endregion

        #region Private Fields
        
        private Account mainAccount;
        private List<MirrorInstrument> mirrorInstruments = new List<MirrorInstrument>();
        public List<MirrorInstrument> MirrorInstruments => mirrorInstruments;
        private DateTime lastActionTime = DateTime.MinValue;
        private TimeSpan minInterval = TimeSpan.FromSeconds(1);
        private bool infoPanelCreated = false;
        private Dictionary<string, Position> lastKnownPositions = new Dictionary<string, Position>();
        
        #endregion

        #region Mirror Instrument Class
        
        public class MirrorInstrument
        {
            public string Name { get; set; }
            public string AccountName { get; set; }
            public Account Account { get; set; }
            public Instrument Instrument { get; set; }
            public MirrorDirection Direction { get; set; }
            public int Multiplier { get; set; }
            public int BarsInProgress { get; set; }
            public bool IsActive { get; set; }
            
            public MirrorInstrument(string name, string accountName, MirrorDirection direction, int multiplier)
            {
                Name = name;
                AccountName = accountName;
                Direction = direction;
                Multiplier = multiplier;
                IsActive = !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(accountName);
            }
        }
        
        #endregion

        #region Strategy Lifecycle

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "MirrorTradeStrategy";
                Description = "Multi-Account Order Copier with Proportional SL/TP Management";
                IsUnmanaged = true; // Required for cross-account OCO logic
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                BarsRequiredToTrade = 1;
            }
            else if (State == State.Configure)
            {
                // Initialize mirror instruments
                InitializeMirrorInstruments();
                
                // Add data series for each mirror instrument
                foreach (var mirror in mirrorInstruments.Where(m => m.IsActive))
                {
                    try
                    {
                        AddDataSeries(mirror.Instrument.MasterInstrument.Name, BarsPeriod.BarsPeriodType, BarsPeriod.Value);
                    }
                    catch (Exception ex)
                    {
                        Print($"Error adding data series for {mirror.Name}: {ex.Message}");
                    }
                }
            }
            else if (State == State.DataLoaded)
            {
                // Map bars in progress for each mirror instrument
                MapMirrorBarsInProgress();
            }
            else if (State == State.Realtime)
            {
                // Initialize accounts and event handlers
                InitializeAccounts();
                
                // Initialize information display if requested
                if (ShowInfoPanel)
                {
                    InitializeInformationDisplay();
                }
                
                Print($"[MirrorTradeStrategy] Initialized with {mirrorInstruments.Count(m => m.IsActive)} active mirror instruments");
            }
            else if (State == State.Terminated)
            {
                // Clean up event handlers
                CleanupEventHandlers();
                
                // Clean up information display
                infoPanelCreated = false;
            }
        }
        
        #endregion

        #region Initialization Methods
        
        private void InitializeMirrorInstruments()
        {
            mirrorInstruments.Clear();
            
            // Add mirror instruments from properties
            AddMirrorInstrument(MirrorInstrument1, MirrorAccount1, MirrorDirection1, MirrorMultiplier1);
            AddMirrorInstrument(MirrorInstrument2, MirrorAccount2, MirrorDirection2, MirrorMultiplier2);
            AddMirrorInstrument(MirrorInstrument3, MirrorAccount3, MirrorDirection3, MirrorMultiplier3);
            
            Print($"[MirrorTradeStrategy] Initialized {mirrorInstruments.Count(m => m.IsActive)} mirror instruments");
        }
        
        private void AddMirrorInstrument(string instrumentName, string accountName, MirrorDirection direction, int multiplier)
        {
            if (string.IsNullOrEmpty(instrumentName) || string.IsNullOrEmpty(accountName))
                return;
                
            try
            {
                var instrument = Instrument.GetInstrument(instrumentName);
                if (instrument == null)
                {
                    Print($"Warning: Could not resolve mirror instrument: {instrumentName}");
                    return;
                }
                
                var mirror = new MirrorInstrument(instrumentName, accountName, direction, multiplier)
                {
                    Instrument = instrument
                };
                
                mirrorInstruments.Add(mirror);
                Print($"[MirrorTradeStrategy] Added mirror instrument: {instrumentName} -> {accountName} ({direction}, {multiplier}x)");
            }
            catch (Exception ex)
            {
                Print($"Error adding mirror instrument {instrumentName}: {ex.Message}");
            }
        }
        
        private void MapMirrorBarsInProgress()
        {
            foreach (var mirror in mirrorInstruments.Where(m => m.IsActive))
            {
                for (int i = 0; i < BarsArray.Length; i++)
                {
                    if (BarsArray[i].Instrument.MasterInstrument.Name == mirror.Instrument.MasterInstrument.Name)
                    {
                        mirror.BarsInProgress = i;
                        Print($"[MirrorTradeStrategy] Mapped {mirror.Name} to BarsInProgress[{i}]");
                        break;
                    }
                }
            }
        }
        
        private void InitializeAccounts()
        {
            // Initialize main account
            mainAccount = Account.All.FirstOrDefault(a => a.Name == MainAccountName);
            if (mainAccount == null)
            {
                Print($"Warning: Main account '{MainAccountName}' not found. Using current account.");
                mainAccount = Account;
            }
            
            // Initialize mirror accounts
            foreach (var mirror in mirrorInstruments.Where(m => m.IsActive))
            {
                mirror.Account = Account.All.FirstOrDefault(a => a.Name == mirror.AccountName);
                if (mirror.Account == null)
                {
                    Print($"Warning: Mirror account '{mirror.AccountName}' not found for {mirror.Name}");
                    mirror.IsActive = false;
                }
            }
            
            // Subscribe to execution updates for main account
            if (mainAccount != null)
            {
                mainAccount.ExecutionUpdate += OnMainAccountExecutionUpdate;
                Print($"[MirrorTradeStrategy] Subscribed to main account: {mainAccount.Name}");
            }
            
            // Subscribe to execution updates for mirror accounts
            foreach (var mirror in mirrorInstruments.Where(m => m.IsActive && m.Account != null))
            {
                mirror.Account.ExecutionUpdate += OnMirrorAccountExecutionUpdate;
                Print($"[MirrorTradeStrategy] Subscribed to mirror account: {mirror.Account.Name}");
            }
        }
        
        private void CleanupEventHandlers()
        {
            if (mainAccount != null)
            {
                mainAccount.ExecutionUpdate -= OnMainAccountExecutionUpdate;
            }
            
            foreach (var mirror in mirrorInstruments.Where(m => m.IsActive && m.Account != null))
            {
                mirror.Account.ExecutionUpdate -= OnMirrorAccountExecutionUpdate;
            }
        }
        
        #endregion

        #region Bar Update and Position Monitoring

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 1)
                return;

            if (!EnableCopy)
                return;

            // Monitor positions for synchronized closing alerts
            MonitorPositions();
            
            // Update information display
            UpdateInformationDisplay();
        }
        
        private void MonitorPositions()
        {
            if (!AlertOnUnsynchronizedClose)
                return;
                
            var mainPos = GetMainPosition();
            
            foreach (var mirror in mirrorInstruments.Where(m => m.IsActive))
            {
                var mirrorPos = GetMirrorPosition(mirror);
                var positionKey = $"{mirror.AccountName}_{mirror.Name}";
                
                // Check for unsynchronized closing
                    if (mainPos.MarketPosition == MarketPosition.Flat && mirrorPos.MarketPosition != MarketPosition.Flat)
                {
                    if (!lastKnownPositions.ContainsKey(positionKey) || lastKnownPositions[positionKey].MarketPosition == MarketPosition.Flat)
                    {
                        Print($"ALERT: Main trade closed, but {mirror.Name} on {mirror.AccountName} still has position!");
                        PlaySound("Alert1.wav");
                    }
                }

                    if (mainPos.MarketPosition != MarketPosition.Flat && mirrorPos.MarketPosition == MarketPosition.Flat)
                {
                    if (lastKnownPositions.ContainsKey(positionKey) && lastKnownPositions[positionKey].MarketPosition != MarketPosition.Flat)
                    {
                        Print($"ALERT: {mirror.Name} on {mirror.AccountName} closed while main trade is still open!");
                        PlaySound("Alert1.wav");
                    }
                }
                
                // Update last known position
                lastKnownPositions[positionKey] = mirrorPos;
            }
        }
        
        #endregion

        #region Execution Event Handlers
        
        private void OnMainAccountExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (!EnableCopy || e?.Execution == null)
                    return;

                // Only process orders on the main instrument
                if (e.Execution.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name)
                    return;

                // Only process filled orders
                if (e.Execution.Order?.OrderState != OrderState.Filled)
                    return;

                // Rate limiting
                if (DateTime.UtcNow - lastActionTime < minInterval)
                    return;

                lastActionTime = DateTime.UtcNow;

                Print($"[MirrorTradeStrategy] Main account execution detected: {e.Execution.MarketPosition} {Math.Abs(e.Execution.Quantity)} {e.Execution.Instrument.MasterInstrument.Name}");

                // Create mirror trades for each active mirror instrument
                foreach (var mirror in mirrorInstruments.Where(m => m.IsActive))
                {
                    CreateMirrorTrade(e.Execution, mirror);
                }
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Error in main account execution update: {ex.Message}");
            }
        }
        
        private void OnMirrorAccountExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (!EnableCopy || e?.Execution == null)
                    return;

                // Find the mirror instrument for this account
                var mirror = mirrorInstruments.FirstOrDefault(m => m.IsActive && m.Account == sender as Account);
                if (mirror == null)
                    return;

                // Only process orders on the mirror instrument
                if (e.Execution.Instrument.MasterInstrument.Name != mirror.Instrument.MasterInstrument.Name)
                    return;

                // Only process filled orders
                if (e.Execution.Order?.OrderState != OrderState.Filled)
                    return;

                Print($"[MirrorTradeStrategy] Mirror execution on {mirror.Name}: {e.Execution.MarketPosition} {Math.Abs(e.Execution.Quantity)}");
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Error in mirror account execution update: {ex.Message}");
            }
        }
        
        #endregion

        #region Mirror Trade Creation
        
        private void CreateMirrorTrade(Execution execution, MirrorInstrument mirror)
        {
            try
            {
                // Calculate mirror quantity
                int mainQty = Math.Abs(execution.Quantity);
                int mirrorQty = Math.Max(1, mirror.Multiplier * mainQty);

                // Determine mirror direction
                OrderAction mirrorAction = DetermineMirrorAction(execution.MarketPosition, mirror.Direction);

                // Close existing position on mirror instrument
                CloseExistingMirrorPosition(mirror);

                // Calculate SL/TP values with proper inversion for face-to-face trading
                var sltpValues = CalculateMirrorSLTP(mirror, mirrorQty);

                // Create OCO orders
                CreateMirrorOCOOrders(mirror, mirrorAction, mirrorQty, sltpValues);

                Print($"[MirrorTradeStrategy] Created mirror trade: {mirrorAction} {mirrorQty} {mirror.Name} on {mirror.AccountName}");
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Error creating mirror trade for {mirror.Name}: {ex.Message}");
            }
        }
        
        private OrderAction DetermineMirrorAction(MarketPosition mainPosition, MirrorDirection direction)
        {
            if (direction == MirrorDirection.Opposite)
            {
                return mainPosition == MarketPosition.Long ? OrderAction.SellShort : OrderAction.Buy;
            }
            else
            {
                return mainPosition == MarketPosition.Long ? OrderAction.Buy : OrderAction.SellShort;
            }
        }
        
        private void CloseExistingMirrorPosition(MirrorInstrument mirror)
        {
            try
            {
                var existingPos = GetMirrorPosition(mirror);
                if (existingPos.MarketPosition != MarketPosition.Flat)
                {
                    Print($"[MirrorTradeStrategy] Closing existing position on {mirror.Name}: {existingPos.MarketPosition}");
                    
                    if (existingPos.MarketPosition == MarketPosition.Long)
                    {
                        SubmitOrderUnmanaged(mirror.BarsInProgress, OrderAction.Sell, OrderType.Market, existingPos.Quantity, 0, 0, "", "CloseLong_" + mirror.Name);
                    }
                    else if (existingPos.MarketPosition == MarketPosition.Short)
                    {
                        SubmitOrderUnmanaged(mirror.BarsInProgress, OrderAction.BuyToCover, OrderType.Market, Math.Abs(existingPos.Quantity), 0, 0, "", "CloseShort_" + mirror.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Error closing existing position on {mirror.Name}: {ex.Message}");
            }
        }
        
        private (double tpPrice, double slPrice, int tpTicks, int slTicks) CalculateMirrorSLTP(MirrorInstrument mirror, int mirrorQty)
        {
            // Get current price for mirror instrument
            double entryPrice = Close[mirror.BarsInProgress];
            
            // Calculate tick value for mirror instrument
            double tickValue = mirror.Instrument.MasterInstrument.PointValue * mirror.Instrument.MasterInstrument.TickSize;
            
            // Get main SL/TP values based on input method
            double mainSLDollars, mainTPDollars;
            
            if (MainSLTPMethod == SLTPInputMethod.Dollars)
            {
                mainSLDollars = MainStopLossDollars;
                mainTPDollars = MainTakeProfitDollars;
            }
            else
            {
                // Convert ticks to dollars using main instrument
                double mainTickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
                mainSLDollars = MainStopLossTicks * mainTickValue;
                mainTPDollars = MainTakeProfitTicks * mainTickValue;
            }
            
            // FACE-TO-FACE LOGIC: Swap SL/TP values for opposite direction
            double mirrorTPDollars, mirrorSLDollars;
            
            if (mirror.Direction == MirrorDirection.Opposite)
            {
                // Main's SL becomes Mirror's TP, Main's TP becomes Mirror's SL
                mirrorTPDollars = mainSLDollars;  // Main SL ($200) -> Mirror TP ($200)
                mirrorSLDollars = mainTPDollars;  // Main TP ($100) -> Mirror SL ($100)
            }
            else
            {
                // Same direction: use same values
                mirrorTPDollars = mainTPDollars;
                mirrorSLDollars = mainSLDollars;
            }
            
            // Convert dollars to ticks for mirror instrument
            int tpTicks = DollarsToTicks(mirrorTPDollars, tickValue, mirrorQty);
            int slTicks = DollarsToTicks(mirrorSLDollars, tickValue, mirrorQty);
            
            // Calculate prices
            double tpPrice = entryPrice + tpTicks * mirror.Instrument.MasterInstrument.TickSize;
            double slPrice = entryPrice - slTicks * mirror.Instrument.MasterInstrument.TickSize;
            
            Print($"[MirrorTradeStrategy] {mirror.Name} SL/TP: TP=${mirrorTPDollars:F2} ({tpTicks} ticks), SL=${mirrorSLDollars:F2} ({slTicks} ticks)");
            
            return (tpPrice, slPrice, tpTicks, slTicks);
        }
        
        private void CreateMirrorOCOOrders(MirrorInstrument mirror, OrderAction entryAction, int quantity, (double tpPrice, double slPrice, int tpTicks, int slTicks) sltpValues)
        {
            string ocoId = Guid.NewGuid().ToString("N").Substring(0, 10);
            string entrySignal = $"MirrorEntry_{mirror.Name}_{ocoId}";
            
            // Entry order
            SubmitOrderUnmanaged(mirror.BarsInProgress, entryAction, OrderType.Market, quantity, 0, 0, ocoId, entrySignal);
            
            // TP and SL orders with OCO
            if (entryAction == OrderAction.Buy)
            {
                // Long position: TP above entry, SL below entry
                SubmitOrderUnmanaged(mirror.BarsInProgress, OrderAction.Sell, OrderType.Limit, quantity, sltpValues.tpPrice, 0, ocoId, $"TP_{mirror.Name}_{ocoId}");
                SubmitOrderUnmanaged(mirror.BarsInProgress, OrderAction.Sell, OrderType.StopMarket, quantity, 0, sltpValues.slPrice, ocoId, $"SL_{mirror.Name}_{ocoId}");
            }
            else
            {
                // Short position: TP below entry, SL above entry
                SubmitOrderUnmanaged(mirror.BarsInProgress, OrderAction.BuyToCover, OrderType.Limit, quantity, sltpValues.tpPrice, 0, ocoId, $"TP_{mirror.Name}_{ocoId}");
                SubmitOrderUnmanaged(mirror.BarsInProgress, OrderAction.BuyToCover, OrderType.StopMarket, quantity, 0, sltpValues.slPrice, ocoId, $"SL_{mirror.Name}_{ocoId}");
            }
            
            Print($"[MirrorTradeStrategy] Created OCO orders for {mirror.Name}: TP={sltpValues.tpTicks} ticks, SL={sltpValues.slTicks} ticks, OCO={ocoId}");
        }
        
        #endregion

        #region Helper Methods

        private int DollarsToTicks(double dollars, double tickValue, int qty)
        {
            if (tickValue <= 0 || qty <= 0) return 0;
            double ticks = dollars / (tickValue * qty);
            return Math.Max(1, (int)Math.Round(ticks, MidpointRounding.AwayFromZero));
        }
        
        private Position GetMainPosition()
        {
            return mainAccount?.Positions.FirstOrDefault(p => p.Instrument.MasterInstrument.Name == Instrument.MasterInstrument.Name) ?? new Position();
        }
        
        private Position GetMirrorPosition(MirrorInstrument mirror)
        {
            return mirror.Account?.Positions.FirstOrDefault(p => p.Instrument.MasterInstrument.Name == mirror.Instrument.MasterInstrument.Name) ?? new Position();
        }
        
        #endregion

        #region Information Display
        
        private void InitializeInformationDisplay()
        {
            try
            {
                infoPanelCreated = true;
                Print("[MirrorTradeStrategy] Information display initialized");
                PrintStatusInfo();
                PrintControlInstructions();
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Error initializing information display: {ex.Message}");
            }
        }
        
        private void UpdateInformationDisplay()
        {
            if (infoPanelCreated && ShowInfoPanel)
            {
                // Update every 10 bars to avoid spam
                if (CurrentBars[0] % 10 == 0)
                {
                    PrintStatusInfo();
                }
            }
        }
        
        private void PrintControlInstructions()
        {
            Print("=== MirrorTradeStrategy Control Instructions ===");
            Print("QUICK CONTROLS:");
            Print("• Enable Copy: Set 'EnableCopy' property to true");
            Print("• Disable Copy: Set 'EnableCopy' property to false");
            Print("• Close All: Manually close positions on all accounts");
            Print("• Close Group: Manually close positions on mirror accounts");
            Print("• Status Updates: Watch this output window for real-time info");
            Print("================================================");
        }
        
        private void PrintStatusInfo()
        {
            try
            {
                Print($"=== MirrorTradeStrategy Status: {(EnableCopy ? "ACTIVE" : "INACTIVE")} ===");
                Print($"Main Account: {mainAccount?.Name ?? "Not Found"}");
                Print($"Active Mirrors: {mirrorInstruments.Count(m => m.IsActive)}");
                
                // Main position info
                var mainPos = GetMainPosition();
                Print($"Main Position: {mainPos.MarketPosition} {mainPos.Quantity} {Instrument.MasterInstrument.Name}");
                Print($"Main P&L: ${mainPos.GetUnrealizedProfitLoss(PerformanceUnit.Currency):F2}");
                
                // Mirror positions info
                foreach (var mirror in mirrorInstruments.Where(m => m.IsActive))
                {
                    var mirrorPos = GetMirrorPosition(mirror);
                    Print($"{mirror.Name} ({mirror.AccountName}): {mirrorPos.MarketPosition} {mirrorPos.Quantity}, P&L: ${mirrorPos.GetUnrealizedProfitLoss(PerformanceUnit.Currency):F2}, {mirror.Direction} {mirror.Multiplier}x");
                }
                Print("==========================================");
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Error printing status: {ex.Message}");
            }
        }
        
        #endregion
    }
}
