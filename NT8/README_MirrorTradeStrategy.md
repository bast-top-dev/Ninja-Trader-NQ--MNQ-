# MirrorTradeStrategy - Installation & Usage Guide

## 📋 Prerequisites

- **NinjaTrader 8** (latest version recommended)
- **Simulation Account** (for testing - strategy defaults to simulation mode for safety)
- **Live Data Feed** (for real-time price updates)

## 🚀 Installation Steps

### Step 1: Copy the Strategy File
1. Copy `MirrorTradeStrategy.cs` to your NinjaTrader 8 strategies folder:
   - **Default Location**: `Documents\NinjaTrader 8\bin\Custom\Strategies\`
   - Or use NinjaTrader's File → Utilities → Import → NinjaScript

### Step 2: Compile the Strategy
1. Open NinjaTrader 8
2. Go to **Tools → NinjaScript Editor**
3. Right-click in the editor → **Compile**
4. Check for any compilation errors in the Output window
5. If successful, close the editor

### Step 3: Verify Installation
1. Open a chart for your main instrument (e.g., NQ)
2. Right-click on chart → **Strategies**
3. Look for **"MirrorTradeStrategy"** in the list
4. If not visible, restart NinjaTrader and try again

## ⚙️ Configuration

### Strategy Parameters
When you add the strategy to a chart, configure these settings:

#### Mirror Settings
- **Mirror Instrument**: `MNQ 12-25` (or your target mirror instrument)
- **Contract Multiplier Z**: `1` (how many mirror contracts per main contract)

#### Risk Settings ($)
- **Main SL ($ loss)**: `200` (stop loss dollar amount)
- **Main TP ($ profit)**: `100` (take profit dollar amount)

#### Control
- **Enable Opposite Direction**: `True` (mirror trades opposite direction)
- **Enable Strategy**: `True` (start enabled)
- **Force Simulation Mode**: `True` (safety - only works in simulation)

## 🎯 How to Use

### Step 1: Add Strategy to Chart
1. Open a chart for your **main instrument** (e.g., NQ)
2. Right-click → **Strategies** → **Add Strategy**
3. Select **MirrorTradeStrategy**
4. Configure parameters (see above)
5. Click **OK**

### Step 2: Enable Mirror Copying
1. Look for the **toggle button** on your chart:
   - **Red "DISABLE MIRROR"** = Currently copying trades
   - **Green "ENABLE MIRROR"** = Currently disabled
2. Click the button to toggle on/off

### Step 3: Start Trading
1. **Place manual trades** on your main instrument (NQ)
2. **Watch automatic mirror trades** appear on MNQ:
   - If you **BUY NQ** → Strategy **SELLS MNQ**
   - If you **SELL NQ** → Strategy **BUYS MNQ**
3. **Monitor the status panel** showing:
   - Copy status (ON/OFF)
   - Position quantities and PnL for both instruments

## 🔔 Alerts & Monitoring

### Desync Alerts
The strategy will alert you if positions become desynchronized:
- **Main position closes** while mirror is still open
- **Mirror position closes** while main is still open

### Status Display
Real-time information panel shows:
- Mirror copy status
- Main instrument: quantity, average price, unrealized PnL
- Mirror instrument: quantity, average price, unrealized PnL

## ⚠️ Safety Features

### Simulation Mode Protection
- **Force Simulation Mode** is enabled by default
- Strategy will **disable itself** if you try to run it on a live account
- Always test in simulation first!

### Position Management
- **Automatically closes** existing mirror positions before opening new ones
- **Rate limiting** prevents duplicate triggers from partial fills
- **Error handling** with detailed logging

## 🧪 Testing Procedure

### Step 1: Simulation Testing
1. Ensure you're on a **Simulation Account**
2. Add strategy to NQ chart
3. Place a small test trade on NQ
4. Verify mirror trade appears on MNQ
5. Check that SL/TP levels are calculated correctly

### Step 2: Verify Calculations
Example test:
- **Main Trade**: BUY 1 NQ with $200 SL, $100 TP
- **Expected Mirror**: SELL 1 MNQ with equivalent dollar SL/TP
- **Check**: MNQ SL/TP distances should equal $200/$100 in MNQ terms

### Step 3: Test Desync Alerts
1. Open positions on both instruments
2. Manually close one position
3. Verify alert appears for desync

## 🐛 Troubleshooting

### Strategy Not Appiling
- **Check compilation errors** in NinjaScript Editor
- **Restart NinjaTrader** after copying files
- **Verify file location** in Custom/Strategies folder

### No Mirror Trades Appearing
- **Check EnableCopy** is true (red button = enabled)
- **Verify Mirror Instrument** name is correct
- **Check account** is simulation (if Force Simulation Mode enabled)
- **Look at Output window** for error messages

### Incorrect SL/TP Levels
- **Verify tick values** for both instruments
- **Check Contract Multiplier Z** setting
- **Review dollar amounts** in Risk Settings

### UI Controls Not Showing
- **Wait for strategy to start** (may take a few seconds)
- **Check chart permissions** for custom controls
- **Restart strategy** if controls don't appear

## 📊 Example Setup

### Typical Configuration
```
Main Instrument: NQ 12-25
Mirror Instrument: MNQ 12-25
Contract Multiplier Z: 1
Main SL: $200
Main TP: $100
Opposite Direction: True
Force Simulation Mode: True
```

### Expected Behavior
- **BUY 1 NQ** → **SELL 1 MNQ** with $200 SL, $100 TP
- **SELL 1 NQ** → **BUY 1 MNQ** with $200 SL, $100 TP
- **OCO orders** ensure only one SL or TP executes

## 📞 Support

If you encounter issues:
1. **Check the Output window** for error messages
2. **Review strategy parameters** for correct configuration
3. **Test in simulation** before live trading
4. **Verify instrument names** and data feeds

## ⚖️ Disclaimer

- **Test thoroughly** in simulation before live trading
- **Understand the risks** of automated trading
- **Monitor positions** regularly
- **Use appropriate position sizing**

---

**Happy Trading! 🚀**
