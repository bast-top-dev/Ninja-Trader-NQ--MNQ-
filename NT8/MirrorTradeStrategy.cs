// ======================================================
// MirrorTradeStrategy for NinjaTrader 8
// Description:
//   Mirrors trades from a main instrument to a mirror instrument
//   in the opposite direction with adjustable SL/TP values in dollars.
//   Designed for use cases like NQ ↔ MNQ, ES ↔ MES, etc.
// ======================================================

#region Using declarations
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class MirrorTradeStrategy : Strategy
    {
        #region === User Inputs ===

        [NinjaScriptProperty]
        [Display(Name = "Mirror Instrument (e.g., MNQ 12-25)", GroupName = "Mirror Settings", Order = 0)]
        public string MirrorInstrumentName { get; set; } = "MNQ 12-25";

        [NinjaScriptProperty]
        [Display(Name = "Contract Multiplier Z", GroupName = "Mirror Settings", Order = 1)]
        public int ContractMultiplierZ { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "Main Stop Loss ($)", GroupName = "Risk Settings", Order = 0)]
        public double MainStopLossDollars { get; set; } = 200;

        [NinjaScriptProperty]
        [Display(Name = "Main Take Profit ($)", GroupName = "Risk Settings", Order = 1)]
        public double MainTakeProfitDollars { get; set; } = 100;

        [NinjaScriptProperty]
        [Display(Name = "Opposite Direction", GroupName = "Mirror Settings", Order = 2)]
        public bool OppositeDirection { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Enable Copy", GroupName = "Control", Order = 3)]
        public bool EnableCopy { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Force Simulation Only", GroupName = "Control", Order = 4)]
        public bool ForceSimulationMode { get; set; } = true;

        #endregion

        #region === Internal Fields ===
        private Instrument _mirrorInstrument;
        private int _mirrorBarsInProgress = -1;
        private string _entrySignalTag = "MirrorEntry";
        private Account _account;
        private Button _toggleButton;
        private TextBlock _statusText;
        private bool _isMainPositionOpen = false;
        private bool _isMirrorPositionOpen = false;
        private DateTime _lastMirrorActionTime = DateTime.MinValue;
        private TimeSpan _minMirrorInterval = TimeSpan.FromSeconds(1);
        #endregion

        #region === Strategy Setup ===
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "MirrorTradeStrategy";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsUnmanaged = false;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
            }
            else if (State == State.Configure)
            {
                if (string.IsNullOrWhiteSpace(MirrorInstrumentName))
                    throw new ArgumentException("MirrorInstrumentName cannot be empty.");

                _mirrorInstrument = Instrument.GetInstrument(MirrorInstrumentName);
                if (_mirrorInstrument == null)
                    throw new ArgumentException($"Could not resolve mirror instrument: {MirrorInstrumentName}");

                AddDataSeries(_mirrorInstrument, BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                for (int i = 0; i < BarsArray.Length; i++)
                {
                    if (BarsArray[i].Instrument == _mirrorInstrument)
                    {
                        _mirrorBarsInProgress = i;
                        break;
                    }
                }
            }
            else if (State == State.Realtime)
            {
                if (ForceSimulationMode && Account != null && !Account.IsSimulationAccount)
                {
                    Print("[MirrorTradeStrategy] Live account detected. Disabled due to ForceSimulationMode.");
                    EnableCopy = false;
                    ShowAlert("Safety", "Live account disabled (Sim only mode).", Brushes.Red, Brushes.White);
                    return;
                }

                _account = Account;
                _account.ExecutionUpdate += OnAccountExecutionUpdate;

                CreateChartControls();
                Print($"[MirrorTradeStrategy] Started on {Account.Name}. Mirror: {_mirrorInstrument.FullName}");
            }
            else if (State == State.Terminated)
            {
                if (_account != null)
                    _account.ExecutionUpdate -= OnAccountExecutionUpdate;

                if (ChartControl != null)
                {
                    try { ChartControl.Dispatcher.InvokeAsync(() => ChartControl.CustomControls.Clear()); }
                    catch { }
                }
            }
        }
        #endregion

        #region === Core Logic ===

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBars[0] < 1)
                return;

            var mainPos = Position;
            var mirrorPos = Account?.Positions?.FirstOrDefault(p => p.Instrument == _mirrorInstrument);

            bool wasMain = _isMainPositionOpen;
            bool wasMirror = _isMirrorPositionOpen;

            _isMainPositionOpen = (mainPos.MarketPosition != MarketPosition.Flat);
            _isMirrorPositionOpen = (mirrorPos != null && mirrorPos.MarketPosition != MarketPosition.Flat);

            if (wasMain && !_isMainPositionOpen && _isMirrorPositionOpen)
                ShowAlert("Desync", "Main closed but Mirror still open!", Brushes.Yellow, Brushes.Black);
            if (wasMirror && !_isMirrorPositionOpen && _isMainPositionOpen)
                ShowAlert("Desync", "Mirror closed but Main still open!", Brushes.Yellow, Brushes.Black);

            UpdateStatusDisplay(mainPos, mirrorPos);
        }

        private void OnAccountExecutionUpdate(object sender, ExecutionEventArgs args)
        {
            try
            {
                if (!EnableCopy || args == null || args.Execution == null)
                    return;

                if (args.Execution.Instrument == null || args.Execution.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name)
                    return; // Only mirror main instrument

                if (args.Order == null || args.Order.OrderState != OrderState.Filled)
                    return;

                if (DateTime.UtcNow - _lastMirrorActionTime < _minMirrorInterval)
                    return;
                _lastMirrorActionTime = DateTime.UtcNow;

                int mainQty = Math.Abs(args.Execution.Quantity);
                int mirrorQty = Math.Max(1, ContractMultiplierZ * mainQty);

                OrderAction mirrorAction = OppositeDirection
                    ? (args.Execution.MarketPosition == MarketPosition.Long ? OrderAction.SellShort : OrderAction.Buy)
                    : (args.Execution.MarketPosition == MarketPosition.Long ? OrderAction.Buy : OrderAction.SellShort);

                // Close existing mirror position
                var mirrorPosition = Account.Positions.FirstOrDefault(p => p.Instrument == _mirrorInstrument);
                if (mirrorPosition != null && mirrorPosition.MarketPosition != MarketPosition.Flat)
                {
                    int qtyToClose = Math.Abs(mirrorPosition.Quantity);
                    if (mirrorPosition.MarketPosition == MarketPosition.Long)
                        ExitLong(_mirrorBarsInProgress, qtyToClose, "", "");
                    else
                        ExitShort(_mirrorBarsInProgress, qtyToClose, "", "");
                }

                // Tick calculations
                double tickValueMirror = _mirrorInstrument.MasterInstrument.PointValue * _mirrorInstrument.MasterInstrument.TickSize;
                int tpTicks = DollarsToTicks(MainTakeProfitDollars, tickValueMirror, mirrorQty);
                int slTicks = DollarsToTicks(MainStopLossDollars, tickValueMirror, mirrorQty);

                string signal = _entrySignalTag + Guid.NewGuid().ToString("N").Substring(0, 6);
                SetStopLoss(_mirrorBarsInProgress, signal, CalculationMode.Ticks, slTicks, false);
                SetProfitTarget(_mirrorBarsInProgress, signal, CalculationMode.Ticks, tpTicks);

                if (mirrorAction == OrderAction.Buy)
                    EnterLong(_mirrorBarsInProgress, mirrorQty, signal);
                else
                    EnterShort(_mirrorBarsInProgress, mirrorQty, signal);

                Print($"[MirrorTradeStrategy] {mirrorAction} {mirrorQty} {_mirrorInstrument.MasterInstrument.Name} | SL={slTicks} ticks | TP={tpTicks} ticks");
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Error: {ex.Message}");
            }
        }

        private int DollarsToTicks(double dollars, double tickValue, int qty)
        {
            if (tickValue <= 0 || qty <= 0) return 0;
            double ticks = dollars / (tickValue * qty);
            return Math.Max(1, (int)Math.Round(ticks, MidpointRounding.AwayFromZero));
        }

        #endregion

        #region === UI / Display ===

        private void CreateChartControls()
        {
            try
            {
                _toggleButton = new Button
                {
                    Content = EnableCopy ? "DISABLE MIRROR" : "ENABLE MIRROR",
                    Width = 130,
                    Height = 30,
                    Background = EnableCopy ? Brushes.Red : Brushes.Green,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold
                };
                _toggleButton.Click += ToggleButton_Click;

                _statusText = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(5)
                };

                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        var panel = new StackPanel
                        {
                            Orientation = Orientation.Vertical,
                            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                            Margin = new Thickness(10)
                        };
                        panel.Children.Add(_toggleButton);
                        panel.Children.Add(_statusText);
                        ChartControl.CustomControls.Add(panel);
                    });
                }
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] UI error: {ex.Message}");
            }
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            EnableCopy = !EnableCopy;
            _toggleButton.Content = EnableCopy ? "DISABLE MIRROR" : "ENABLE MIRROR";
            _toggleButton.Background = EnableCopy ? Brushes.Red : Brushes.Green;
            Print($"[MirrorTradeStrategy] Mirror {(EnableCopy ? "ENABLED" : "DISABLED")}");
        }

        private void UpdateStatusDisplay(Position mainPos, Position mirrorPos)
        {
            try
            {
                string msg =
                    $"Mirror Copy: {(EnableCopy ? "ON" : "OFF")}\n" +
                    $"Main: {Instrument.FullName} | Qty: {mainPos.Quantity} | PnL: {mainPos.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]):C2}\n" +
                    $"Mirror: {_mirrorInstrument.FullName} | Qty: {(mirrorPos != null ? mirrorPos.Quantity : 0)} | " +
                    $"PnL: {(mirrorPos != null ? mirrorPos.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Closes[_mirrorBarsInProgress][0]).ToString("C2") : "$0.00")}";

                if (_statusText != null)
                    _statusText.Text = msg;

                Draw.TextFixed(this, "MirrorStatus", msg, TextPosition.TopLeft, Brushes.White, new SimpleFont("Arial", 12), Brushes.Transparent, Brushes.Transparent, 0);
            }
            catch (Exception ex)
            {
                Print($"[MirrorTradeStrategy] Status error: {ex.Message}");
            }
        }

        private void ShowAlert(string id, string message, Brush background, Brush foreground)
        {
            Alert(id, Priority.High, message, new SimpleFont("Arial", 12, FontStyle.Bold), background, foreground);
        }

        #endregion
    }
}
