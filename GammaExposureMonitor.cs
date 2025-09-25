// Copyright 2025. Gamma Exposure Monitor for Quantower Trading Platform
// Based on official Quantower GetOptionsInfo.cs example

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Options;

namespace GammaExposureIndicator
{
    /// <summary>
    /// Gamma Exposure Monitor - Identifies key gamma levels for day trading
    /// Shows where Market Makers will hedge and creates support/resistance levels
    /// </summary>
    public class GammaExposureMonitor : Indicator
    {
        #region Parameters
        [Parameter("Strike Range", DefaultValue = 10)]
        public int StrikeRange = 10;

        [Parameter("GEX Threshold (Million)", DefaultValue = 1.0)]
        public double GexThreshold = 1000000;

        [Parameter("Update Frequency (ms)", DefaultValue = 2000)]
        public int UpdateFrequency = 2000;

        [Parameter("Show Gamma Levels", DefaultValue = true)]
        public bool ShowGammaLevels = true;

        [Parameter("Show GEX Values", DefaultValue = true)]
        public bool ShowGexValues = true;

        [Parameter("Alert on Critical Levels", DefaultValue = true)]
        public bool AlertOnCriticalLevels = true;
        #endregion

        #region Private Variables
        private IList<Symbol> callStrikes;
        private IList<Symbol> putStrikes;
        private Dictionary<double, double> gammaExposure;
        private Dictionary<double, double> netGamma;
        private BlackScholesPriceModel bsModel;
        private System.Threading.Timer updateTimer;
        private bool dataLoaded = false;
        private double currentSpotPrice;
        private double riskFreeRate = 0.05; // 5% default
        private Font textFont;
        private Brush positiveGammaBrush;
        private Brush negativeGammaBrush;
        private Brush neutralBrush;
        private Pen positiveGammaPen;
        private Pen negativeGammaPen;
        #endregion

        public GammaExposureMonitor() : base()
        {
            Name = "Gamma Exposure Monitor";
            Description = "Real-time Gamma Exposure levels for options day trading";
            SeparateWindow = false;

            // Initialize collections
            gammaExposure = new Dictionary<double, double>();
            netGamma = new Dictionary<double, double>();
            bsModel = new BlackScholesPriceModel();

            // Initialize drawing objects
            textFont = new Font("Arial", 9, FontStyle.Regular);
            positiveGammaBrush = new SolidBrush(Color.FromArgb(120, 0, 255, 0)); // Transparent green
            negativeGammaBrush = new SolidBrush(Color.FromArgb(120, 255, 0, 0)); // Transparent red
            neutralBrush = new SolidBrush(Color.LightGray);
            positiveGammaPen = new Pen(Color.Green, 2);
            negativeGammaPen = new Pen(Color.Red, 2);
        }

        protected override void OnInit()
        {
            // Load options data asynchronously (following Quantower pattern)
            Task.Run(() => LoadOptionsData());

            // Setup update timer
            updateTimer = new System.Threading.Timer(UpdateGammaData);
            updateTimer.Change(TimeSpan.FromMilliseconds(UpdateFrequency), 
                              TimeSpan.FromMilliseconds(UpdateFrequency));
        }

        private void LoadOptionsData()
        {
            try
            {
                // Get option series for current symbol (Quantower official pattern)
                var series = Core.Instance.GetOptionSeries(this.Symbol);
                if (series == null || series.Count == 0)
                {
                    Core.Instance.Loggers.Log("No option series found for symbol: " + Symbol.Name);
                    return;
                }

                // Get nearest expiration series
                var nearestSeries = series.OrderBy(s => s.ExpirationDate).First();
                
                // Load all strikes for the series
                var allStrikes = Core.Instance.GetStrikes(nearestSeries);
                if (allStrikes == null)
                {
                    Core.Instance.Loggers.Log("No strikes found for nearest series");
                    return;
                }

                currentSpotPrice = this.Symbol.Last;

                // Filter strikes around ATM ± StrikeRange
                var atmStrikes = allStrikes.Where(s => 
                    Math.Abs(s.StrikePrice - currentSpotPrice) <= (currentSpotPrice * 0.1)) // 10% range around ATM
                    .OrderBy(s => s.StrikePrice)
                    .ToList();

                // Separate calls and puts
                callStrikes = atmStrikes.Where(s => s.OptionType == OptionType.Call).ToList();
                putStrikes = atmStrikes.Where(s => s.OptionType == OptionType.Put).ToList();

                // Subscribe to real-time updates (Quantower pattern)
                foreach (var strike in callStrikes.Concat(putStrikes))
                {
                    strike.NewLast += OnStrikeUpdate;
                    strike.NewQuote += OnStrikeUpdate;
                }

                dataLoaded = true;
                Core.Instance.Loggers.Log($"Loaded {callStrikes.Count} calls and {putStrikes.Count} puts for GEX calculation");

                // Initial calculation
                CalculateGammaExposure();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"Error loading options data: {ex.Message}");
            }
        }

        private void OnStrikeUpdate(Symbol symbol, MessageQuote quote)
        {
            // Trigger recalculation on price updates
            if (dataLoaded)
            {
                Task.Run(() => CalculateGammaExposure());
            }
        }

        private void UpdateGammaData(object obj)
        {
            if (dataLoaded)
            {
                currentSpotPrice = this.Symbol.Last;
                Task.Run(() => CalculateGammaExposure());
            }
        }

        private void CalculateGammaExposure()
        {
            if (!dataLoaded || callStrikes == null || putStrikes == null)
                return;

            var newGammaExposure = new Dictionary<double, double>();
            var newNetGamma = new Dictionary<double, double>();

            try
            {
                // Calculate for calls
                foreach (var call in callStrikes)
                {
                    if (call.Ask > 0 && call.OpenInterest > 0)
                    {
                        // Calculate IV using Quantower's BlackScholes model
                        double iv = bsModel.IV(call, OptionPriceType.Ask, riskFreeRate, 0);
                        if (iv > 0 && iv < 3.0) // Reasonable IV range
                        {
                            // Calculate gamma using Quantower's model
                            double gamma = bsModel.Gamma(call, iv, riskFreeRate, 0);
                            
                            // GEX = Gamma × Open Interest × 100 × Spot Price × Strike Price
                            // Market makers are SHORT calls, so their gamma exposure is NEGATIVE
                            double gex = -gamma * call.OpenInterest * 100 * currentSpotPrice * call.StrikePrice;
                            
                            if (!double.IsNaN(gex) && !double.IsInfinity(gex))
                            {
                                double strike = call.StrikePrice;
                                newGammaExposure[strike] = (newGammaExposure.ContainsKey(strike) ? newGammaExposure[strike] : 0) + gex;
                                newNetGamma[strike] = (newNetGamma.ContainsKey(strike) ? newNetGamma[strike] : 0) + gamma * call.OpenInterest;
                            }
                        }
                    }
                }

                // Calculate for puts
                foreach (var put in putStrikes)
                {
                    if (put.Ask > 0 && put.OpenInterest > 0)
                    {
                        double iv = bsModel.IV(put, OptionPriceType.Ask, riskFreeRate, 0);
                        if (iv > 0 && iv < 3.0) // Reasonable IV range
                        {
                            double gamma = bsModel.Gamma(put, iv, riskFreeRate, 0);
                            
                            // Market makers are SHORT puts, so their gamma exposure is POSITIVE
                            double gex = gamma * put.OpenInterest * 100 * currentSpotPrice * put.StrikePrice;
                            
                            if (!double.IsNaN(gex) && !double.IsInfinity(gex))
                            {
                                double strike = put.StrikePrice;
                                newGammaExposure[strike] = (newGammaExposure.ContainsKey(strike) ? newGammaExposure[strike] : 0) + gex;
                                newNetGamma[strike] = (newNetGamma.ContainsKey(strike) ? newNetGamma[strike] : 0) + gamma * put.OpenInterest;
                            }
                        }
                    }
                }

                // Update thread-safe
                lock (gammaExposure)
                {
                    gammaExposure = newGammaExposure;
                    netGamma = newNetGamma;
                }

                // Check for critical levels and alert
                if (AlertOnCriticalLevels)
                {
                    CheckCriticalLevels();
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"Error calculating gamma exposure: {ex.Message}");
            }
        }

        private void CheckCriticalLevels()
        {
            var maxGex = gammaExposure.Values.Where(v => Math.Abs(v) > GexThreshold).OrderByDescending(Math.Abs).FirstOrDefault();
            if (Math.Abs(maxGex) > GexThreshold * 2) // Double threshold for critical alert
            {
                var criticalStrike = gammaExposure.FirstOrDefault(kvp => Math.Abs(kvp.Value) == Math.Abs(maxGex)).Key;
                var distance = Math.Abs(currentSpotPrice - criticalStrike);
                var percentage = (distance / currentSpotPrice) * 100;

                if (percentage < 2.0) // Within 2% of critical level
                {
                    Core.Instance.Loggers.Log($"CRITICAL GAMMA LEVEL ALERT: {criticalStrike:F2} (GEX: {maxGex/1000000:F1}M) - Distance: {percentage:F1}%");
                }
            }
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (!dataLoaded || gammaExposure == null || gammaExposure.Count == 0)
            {
                args.Graphics.DrawString("Loading Gamma Exposure data...", textFont, neutralBrush, 50, 50);
                return;
            }

            var graphics = args.Graphics;
            int yPosition = 30;

            // Header
            graphics.DrawString($"Gamma Exposure Monitor - Spot: {currentSpotPrice:F2}", 
                new Font("Arial", 11, FontStyle.Bold), Brushes.White, 10, 10);

            // Draw gamma exposure levels
            lock (gammaExposure)
            {
                var sortedGex = gammaExposure.OrderByDescending(kvp => Math.Abs(kvp.Value)).Take(10);

                foreach (var kvp in sortedGex)
                {
                    double strike = kvp.Key;
                    double gex = kvp.Value;
                    double gexMillions = gex / 1000000.0;

                    if (Math.Abs(gexMillions) > (GexThreshold / 1000000.0)) // Above threshold
                    {
                        // Determine color based on GEX sign
                        Brush textBrush = gex > 0 ? Brushes.LightGreen : Brushes.LightCoral;
                        string gexType = gex > 0 ? "Support" : "Resistance";
                        
                        // Distance from current price
                        double distance = ((strike - currentSpotPrice) / currentSpotPrice) * 100;
                        string distanceStr = distance >= 0 ? $"+{distance:F1}%" : $"{distance:F1}%";

                        if (ShowGexValues)
                        {
                            string text = $"{strike:F2} | {gexType} | GEX: {Math.Abs(gexMillions):F1}M | {distanceStr}";
                            graphics.DrawString(text, textFont, textBrush, 10, yPosition);
                        }

                        if (ShowGammaLevels)
                        {
                            // Draw level line on chart (simplified - would need proper price-to-pixel conversion in real implementation)
                            Pen levelPen = gex > 0 ? positiveGammaPen : negativeGammaPen;
                            int lineY = yPosition + 5;
                            graphics.DrawLine(levelPen, 200, lineY, 250, lineY);
                        }

                        yPosition += 20;
                    }
                }

                // Summary statistics
                yPosition += 10;
                var totalPositiveGex = gammaExposure.Values.Where(v => v > 0).Sum() / 1000000.0;
                var totalNegativeGex = gammaExposure.Values.Where(v => v < 0).Sum() / 1000000.0;
                var netGexMillions = (totalPositiveGex + totalNegativeGex);

                graphics.DrawString($"Net GEX: {netGexMillions:F1}M | Pos: {totalPositiveGex:F1}M | Neg: {Math.Abs(totalNegativeGex):F1}M", 
                    textFont, Brushes.Yellow, 10, yPosition);

                // Market interpretation
                yPosition += 25;
                string interpretation = netGexMillions > 5 ? "SUPPRESSED (High Pos GEX)" :
                                     netGexMillions < -5 ? "VOLATILE (High Neg GEX)" : "NEUTRAL";
                Color interpColor = netGexMillions > 5 ? Color.Orange : 
                                  netGexMillions < -5 ? Color.Red : Color.White;
                
                graphics.DrawString($"Market Regime: {interpretation}", 
                    new Font("Arial", 10, FontStyle.Bold), new SolidBrush(interpColor), 10, yPosition);
            }
        }

        public override void Dispose()
        {
            base.Dispose();

            // Cleanup timer
            updateTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            updateTimer?.Dispose();

            // Unsubscribe from events (Quantower pattern)
            if (callStrikes != null)
            {
                foreach (var strike in callStrikes)
                {
                    strike.NewLast -= OnStrikeUpdate;
                    strike.NewQuote -= OnStrikeUpdate;
                }
            }

            if (putStrikes != null)
            {
                foreach (var strike in putStrikes)
                {
                    strike.NewLast -= OnStrikeUpdate;
                    strike.NewQuote -= OnStrikeUpdate;
                }
            }

            // Cleanup drawing objects
            textFont?.Dispose();
            positiveGammaBrush?.Dispose();
            negativeGammaBrush?.Dispose();
            neutralBrush?.Dispose();
            positiveGammaPen?.Dispose();
            negativeGammaPen?.Dispose();
        }
    }
}