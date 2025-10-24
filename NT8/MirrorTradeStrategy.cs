// NinjaTrader 8 Strategy: MirrorTradeStrategy
// - Listens for executions on the main instrument (manual or otherwise) on the strategy's account
// - Places opposite-direction managed orders on a mirror instrument with OCO SL/TP matching dollar targets
// - Closes any pre-existing mirror position before placing a new one
// - Displays basic status on the chart

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.NinjaScript;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies
{
	public class MirrorTradeStrategy : Strategy
	{
		// --------------------
		// User-configurable inputs
		// --------------------
		[Gui.CategoryOrder("Mirror Settings", 0)]
		[Gui.CategoryOrder("Risk Settings ($)", 1)]
		[Gui.CategoryOrder("Control", 2)]

		[NinjaScriptProperty]
		[Display(Name = "Mirror Instrument (e.g., MNQ 12-25)", GroupName = "Mirror Settings", Order = 0)]
		public string MirrorInstrumentName { get; set; } = "MNQ 12-25";

		[NinjaScriptProperty]
		[Display(Name = "Contract Multiplier Z", GroupName = "Mirror Settings", Order = 1, Description = "Mirror contracts = Z * main quantity.")]
		public int ContractMultiplierZ { get; set; } = 1;

		[NinjaScriptProperty]
		[Display(Name = "Main SL ($ loss)", GroupName = "Risk Settings ($)", Order = 0)]
		public double MainStopLossDollars { get; set; } = 200;

		[NinjaScriptProperty]
		[Display(Name = "Main TP ($ profit)", GroupName = "Risk Settings ($)", Order = 1)]
		public double MainTakeProfitDollars { get; set; } = 100;

		[NinjaScriptProperty]
		[Display(Name = "Enable Opposite Direction", GroupName = "Control", Order = 0)]
		public bool OppositeDirection { get; set; } = true;

		[NinjaScriptProperty]
		[Display(Name = "Enable Strategy", GroupName = "Control", Order = 1)]
		public bool EnableCopy { get; set; } = true;

		[NinjaScriptProperty]
		[Display(Name = "Force Simulation Mode", GroupName = "Control", Order = 2, Description = "Only allow trading in simulation accounts")]
		public bool ForceSimulationMode { get; set; } = true;

		// --------------------
		// Internal state
		// --------------------
		private Instrument _mirrorInstrument;
		private int _mirrorBarsInProgress = -1;
		private string _entrySignalTag = "MirrorEntry";
		private Account _account;

		// Track last mirrored entry time to rate-limit duplicates
		private DateTime _lastMirrorActionTime = DateTime.MinValue;
		private TimeSpan _minMirrorInterval = TimeSpan.FromSeconds(1);

		// UI Controls
		private Button _toggleButton;
		private TextBlock _statusText;
		private bool _isMainPositionOpen = false;
		private bool _isMirrorPositionOpen = false;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "MirrorTradeStrategy";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsUnmanaged = false; // using managed orders with multi-instrument support
				IsOverlay = true;
				MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
				TraceOrders = false;
				IsInstantiatedOnEachOptimizationIteration = false;
			}
			else if (State == State.Configure)
			{
				if (string.IsNullOrWhiteSpace(MirrorInstrumentName))
					throw new ArgumentException("MirrorInstrumentName cannot be empty.");

				_mirrorInstrument = Instrument.GetInstrument(MirrorInstrumentName);
				if (_mirrorInstrument == null)
					throw new ArgumentException($"Could not resolve mirror instrument: {MirrorInstrumentName}");

				// Add mirror instrument as secondary series to route managed orders
				AddDataSeries(_mirrorInstrument, BarsPeriod.BarsPeriodType, BarsPeriod.Value);
			}
			else if (State == State.DataLoaded)
			{
				// Identify BarsInProgress index for mirror series
				for (int i = 0; i < BarsArray.Length; i++)
				{
					if (BarsArray[i].Instrument == _mirrorInstrument)
					{
						_mirrorBarsInProgress = i;
						break;
					}
				}

				if (_mirrorBarsInProgress < 0)
					throw new Exception("Mirror BarsInProgress index not found after AddDataSeries.");
			}
			else if (State == State.Realtime)
			{
				// Safety check: Force simulation mode if enabled
				if (ForceSimulationMode && Account != null && !Account.IsSimulationAccount)
				{
					Print($"[MirrorTradeStrategy] SAFETY: Force Simulation Mode is enabled but account is LIVE. Strategy disabled.");
					EnableCopy = false;
					Alert("SAFETY ALERT", "Strategy disabled - Force Simulation Mode enabled but account is LIVE!", AlertPriority.High);
					return;
				}

				// Subscribe to account-level execution events to capture manual trades on the main instrument
				_account = Account;
				if (_account != null)
				{
					_account.ExecutionUpdate += OnAccountExecutionUpdate;
					Print($"[MirrorTradeStrategy] Started on account: {_account.Name} (Simulation: {_account.IsSimulationAccount})");
				}

				// Create UI controls
				CreateChartControls();
			}
			else if (State == State.Terminated)
			{
				if (_account != null)
				{
					_account.ExecutionUpdate -= OnAccountExecutionUpdate;
					_account = null;
				}
			}
		}

		protected override void OnBarUpdate()
		{
			// Update position tracking and check for desync
			if (CurrentBars[0] < 1)
				return;

			if (BarsInProgress == 0)
			{
				var mainPos = Position;
				var mirrorPos = Positions[_mirrorBarsInProgress];
				
				// Track position states
				bool wasMainOpen = _isMainPositionOpen;
				bool wasMirrorOpen = _isMirrorPositionOpen;
				
				_isMainPositionOpen = (mainPos.MarketPosition != MarketPosition.Flat);
				_isMirrorPositionOpen = (mirrorPos.MarketPosition != MarketPosition.Flat);
				
				// Check for desync alerts
				CheckForDesyncAlerts(wasMainOpen, wasMirrorOpen);
				
				// Update UI status
				UpdateStatusDisplay(mainPos, mirrorPos);
			}
		}

		private void OnAccountExecutionUpdate(object sender, ExecutionEventArgs e)
		{
			try
			{
				if (!EnableCopy)
					return;

				// Only mirror executions on our main instrument and account
				if (e == null || e.Execution == null || e.Execution.Instrument == null)
					return;

				// Ignore our own mirror instrument executions to avoid recursion
				if (e.Execution.Instrument.MasterInstrument.Name == _mirrorInstrument.MasterInstrument.Name)
					return;

				// Only respond to executions on the strategy's primary instrument
				if (e.Execution.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name)
					return;

				// Filled executions only
				if (e.Order == null || e.Order.OrderState != OrderState.Filled)
					return;

				// Rate-limit duplicate triggers from partial fills
				if (DateTime.UtcNow - _lastMirrorActionTime < _minMirrorInterval)
					return;

				_lastMirrorActionTime = DateTime.UtcNow;

				// Determine mirror side and quantity
				int mainFilledQty = Math.Abs(e.Execution.Quantity);
				int mirrorQty = Math.Max(1, ContractMultiplierZ * mainFilledQty);

				OrderAction mirrorAction;
				if (OppositeDirection)
				{
					mirrorAction = (e.Execution.MarketPosition == MarketPosition.Long) ? OrderAction.SellShort : OrderAction.Buy;
				}
				else
				{
					mirrorAction = (e.Execution.MarketPosition == MarketPosition.Long) ? OrderAction.Buy : OrderAction.SellShort;
				}

				// Close any existing mirror position first
				var mirrorPosition = Positions[_mirrorBarsInProgress];
				if (mirrorPosition != null && mirrorPosition.MarketPosition != MarketPosition.Flat)
				{
					int qtyToClose = Math.Abs(mirrorPosition.Quantity);
					if (qtyToClose > 0)
					{
						if (mirrorPosition.MarketPosition == MarketPosition.Long)
							ExitLong(_mirrorBarsInProgress, qtyToClose, "", "");
						else if (mirrorPosition.MarketPosition == MarketPosition.Short)
							ExitShort(_mirrorBarsInProgress, qtyToClose, "", "");
					}
				}

				// Calculate SL/TP in ticks for mirror
				var tickValueMirror = _mirrorInstrument.MasterInstrument.PointValue * _mirrorInstrument.MasterInstrument.TickSize;
				if (tickValueMirror <= 0)
					return;

				int tpTicks = DollarsToTicks(MainTakeProfitDollars, tickValueMirror, mirrorQty);
				int slTicks = DollarsToTicks(MainStopLossDollars, tickValueMirror, mirrorQty);

				// Attach OCO-style managed orders via Set methods bound to the entry signal
				string entrySignal = _entrySignalTag + ":" + Guid.NewGuid().ToString("N").Substring(0, 8);
				SetStopLoss(_mirrorBarsInProgress, entrySignal, CalculationMode.Ticks, slTicks, false);
				SetProfitTarget(_mirrorBarsInProgress, entrySignal, CalculationMode.Ticks, tpTicks);

				// Submit the mirror entry managed order
				if (mirrorAction == OrderAction.Buy)
					EnterLong(_mirrorBarsInProgress, mirrorQty, entrySignal);
				else if (mirrorAction == OrderAction.SellShort)
					EnterShort(_mirrorBarsInProgress, mirrorQty, entrySignal);
			}
			catch (Exception ex)
			{
				Print($"[MirrorTradeStrategy] Error in OnAccountExecutionUpdate: {ex.Message}");
			}
		}

		private int DollarsToTicks(double dollars, double tickValue, int quantity)
		{
			if (dollars <= 0 || tickValue <= 0 || quantity <= 0)
				return 0;
			double ticksExact = dollars / (tickValue * quantity);
			int ticks = Math.Max(1, (int)Math.Round(ticksExact, MidpointRounding.AwayFromZero));
			return ticks;
		}

		private void CreateChartControls()
		{
			try
			{
				// Create toggle button
				_toggleButton = new Button
				{
					Content = EnableCopy ? "DISABLE MIRROR" : "ENABLE MIRROR",
					Width = 120,
					Height = 30,
					Background = EnableCopy ? Brushes.Red : Brushes.Green,
					Foreground = Brushes.White,
					FontWeight = FontWeights.Bold
				};
				_toggleButton.Click += ToggleButton_Click;

				// Create status text
				_statusText = new TextBlock
				{
					Foreground = Brushes.White,
					FontSize = 12,
					FontFamily = new FontFamily("Arial"),
					Margin = new Thickness(5)
				};

				// Add controls to chart
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
				Print($"[MirrorTradeStrategy] Error creating chart controls: {ex.Message}");
			}
		}

		private void ToggleButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				EnableCopy = !EnableCopy;
				_toggleButton.Content = EnableCopy ? "DISABLE MIRROR" : "ENABLE MIRROR";
				_toggleButton.Background = EnableCopy ? Brushes.Red : Brushes.Green;
				
				Print($"[MirrorTradeStrategy] Mirror copying {(EnableCopy ? "ENABLED" : "DISABLED")}");
			}
			catch (Exception ex)
			{
				Print($"[MirrorTradeStrategy] Error toggling mirror: {ex.Message}");
			}
		}

		private void CheckForDesyncAlerts(bool wasMainOpen, bool wasMirrorOpen)
		{
			try
			{
				// Alert if main position closed while mirror is still open
				if (wasMainOpen && !_isMainPositionOpen && _isMirrorPositionOpen)
				{
					Alert("DESYNC ALERT", $"Main {Instrument.FullName} position closed, but Mirror {_mirrorInstrument.FullName} position still open!", AlertPriority.High);
					Print($"[MirrorTradeStrategy] DESYNC: Main position closed, Mirror position still open");
				}
				
				// Alert if mirror position closed while main is still open
				if (wasMirrorOpen && !_isMirrorPositionOpen && _isMainPositionOpen)
				{
					Alert("DESYNC ALERT", $"Mirror {_mirrorInstrument.FullName} position closed, but Main {Instrument.FullName} position still open!", AlertPriority.High);
					Print($"[MirrorTradeStrategy] DESYNC: Mirror position closed, Main position still open");
				}
			}
			catch (Exception ex)
			{
				Print($"[MirrorTradeStrategy] Error checking desync alerts: {ex.Message}");
			}
		}

		private void UpdateStatusDisplay(Position mainPos, Position mirrorPos)
		{
			try
			{
				if (_statusText != null)
				{
					string status = $"Mirror Copy: {(EnableCopy ? "ON" : "OFF")}\n" +
								   $"Main: {Instrument.FullName}\n" +
								   $"  Qty: {mainPos.Quantity} | Avg: {mainPos.AveragePrice:0.00} | PnL: ${mainPos.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]):0.00}\n" +
								   $"Mirror: {_mirrorInstrument.FullName}\n" +
								   $"  Qty: {mirrorPos.Quantity} | Avg: {mirrorPos.AveragePrice:0.00} | PnL: ${mirrorPos.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Closes[_mirrorBarsInProgress][0]):0.00}";
					
					_statusText.Text = status;
				}
				
				// Also draw on chart for backup
				string chartStatus = $"Mirror: {(EnableCopy ? "ON" : "OFF")} | Main: {mainPos.Quantity} | Mirror: {mirrorPos.Quantity}";
				Draw.TextFixed(this, "MirrorStatus", chartStatus, TextPosition.TopLeft, Brushes.White, 
					new Gui.Tools.SimpleFont("Arial", 12), Brushes.Transparent, Brushes.Transparent, 0);
			}
			catch (Exception ex)
			{
				Print($"[MirrorTradeStrategy] Error updating status display: {ex.Message}");
			}
		}
	}
}


