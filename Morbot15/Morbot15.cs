using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Indicators;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace cAlgo.Robots
{
    public enum StructureFilterMode { Disabled, Fixed, Auto }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class BARSignalsTrader_CloseBar_Buffer : Robot
    {
        // --- Parámetros ---
        [Parameter("Risk Per Trade (%)", DefaultValue = 2.0, MinValue = 0.1)]
        public double RiskPercent { get; set; }

        [Parameter("SL Offset (pips)", DefaultValue = 20, MinValue = 0)]
        public int ExtraSLPips { get; set; }

        [Parameter("TP Distance (R)", DefaultValue = 2.0, MinValue = 0.1)]
        public double TpRR { get; set; }

        [Parameter("DD Threshold (%)", DefaultValue = 0.0, MinValue = 0.0)]
        public double DdThresholdPct { get; set; }

        [Parameter("Defensive Risk (%)", DefaultValue = 1.0, MinValue = 0.1)]
        public double DefensiveRiskPercent { get; set; }

        [Parameter("Market Structure Filter", DefaultValue = true)]
        public StructureFilterMode UseStructureFilter { get; set; }

        [Parameter("RR Tracts", DefaultValue = "")]
        public string RrStepsStr { get; set; }

        // Constantes
        private const string LabelName = "BARSignalsTrader_CloseBar_Buffer";
        private const string NoTradeAfterEtStr = "11:00";
        private const bool CloseAtSessionEnd = true;
        private const int DailyTradeLimit = 1;
        private const int CloseBufferSeconds = 60;
        private const int StructureTfMin = 1;
        private const int SwingPivot = 5;
        private const int StructureMaxLookback = 100;
        private const int AtrDays = 20;
        private const double AtrPctThreshold = 1.5;
        private const int AtrPctSmaPeriod = 60;
        private const double AtrRatioThreshold = 0.9;

        // --- Estado ---
        private BARSignals _sig;
        private DateTime _currentEtDate = DateTime.MinValue;
        private DateTime _orStartEt, _sessEndEt, _cutoffEt;
        private DateTime _orStartUtc, _sessEndUtc, _flattenUtc, _cutoffUtc;
        private DateTime _startedUtc;
        private Bars _d1Bars;
        private int _tradesToday = 0;

        // DD tracking
        private bool _enableDefensiveMode;
        private double _maxEquity;
        private double _ddPct;
        private bool _defensiveMode;

        // Riesgo inicial y progreso de tramos
        private readonly Dictionary<long, double> _initRiskByPos = new Dictionary<long, double>();
        private readonly Dictionary<long, int> _rrStepAppliedIndex = new Dictionary<long, int>(); // -1 si ninguno

        // Tramos parseados
        private struct RrStep { public double Trigger; public double To; }
        private List<RrStep> _rrSteps = new List<RrStep>();
        private string _rrStepsRaw = null;

        // Series para estructura ---
        private Bars _structBars;

        protected override void OnStart()
        {
            _sig = Indicators.GetIndicator<BARSignals>();
            _startedUtc = Server.Time;
            _maxEquity = Account.Equity;
            _ddPct = 0;
            _enableDefensiveMode = DdThresholdPct > 0.0;
            _defensiveMode = false;
            _d1Bars = MarketData.GetBars(TimeFrame.Daily);

            // Timeframe para estructura
            _structBars = MarketData.GetBars(TfFromMinutes(StructureTfMin));

            RecomputeSessionBounds(_startedUtc);
            ParseRrStepsIfChanged();
            Timer.Start(1);
        }

        protected override void OnBar()
        {
            var nowUtc = Server.Time;
            UpdateDrawdown();

            if (_enableDefensiveMode && !_defensiveMode && _ddPct >= DdThresholdPct)
                _defensiveMode = true;

            RecomputeSessionBounds(nowUtc);

            if (CloseAtSessionEnd && nowUtc >= _flattenUtc && HasOpenPosition())
                CloseAllPositions();

            var barCloseUtc = Bars.OpenTimes.Last(0);
            if (barCloseUtc < _startedUtc) return;

            if (barCloseUtc < _orStartUtc || barCloseUtc >= _flattenUtc)
                return;

            if (_tradesToday == 0 && barCloseUtc >= _cutoffUtc)
                return;

            if (DailyTradeLimit >= 0 && _tradesToday >= DailyTradeLimit)
                return;

            if (HasOpenPosition()) return;

            bool buy = _sig.BuySignal.Last(1) > 0;
            bool sell = _sig.SellSignal.Last(1) > 0;
            if (!buy && !sell) return;

            // --- Filtro estructura MTF ---
            bool applyStruct = ShouldUseStructureFilter();
            if (applyStruct)
            {
                bool up, down;
                if (!GetStructureTrend(out up, out down))
                    return; // no hay datos suficientes de estructura

                if (buy && !up) return;
                if (sell && !down) return;
            }

            double barOpen = Bars.OpenPrices.Last(1);
            double barHigh = Bars.HighPrices.Last(1);
            double barLow = Bars.LowPrices.Last(1);

            double riskToUse = (_defensiveMode && _enableDefensiveMode) ? DefensiveRiskPercent : RiskPercent;

            if (buy)
            {
                double entry = Symbol.Ask;
                double stopPrice = Math.Min(barLow, barOpen) - ExtraSLPips * Symbol.PipSize;
                int stopPips = PipsFrom(entry, stopPrice, TradeType.Buy);
                if (stopPips <= 0) return;

                double vol = VolumeForRisk(riskToUse, stopPips);
                if (vol < Symbol.VolumeInUnitsMin) return;

                int tpPips = (int)Math.Ceiling(stopPips * TpRR);
                var res = ExecuteMarketOrder(TradeType.Buy, SymbolName, vol, LabelName, stopPips, tpPips);
                if (res.IsSuccessful && res.Position != null)
                {
                    var p = res.Position;
                    StoreInitialRisk(p);
                    _rrStepAppliedIndex[p.Id] = -1;
                    _tradesToday++;
                }
            }
            else if (sell)
            {
                double entry = Symbol.Bid;
                double stopPrice = Math.Max(barHigh, barOpen) + ExtraSLPips * Symbol.PipSize;
                int stopPips = PipsFrom(entry, stopPrice, TradeType.Sell);
                if (stopPips <= 0) return;

                double vol = VolumeForRisk(riskToUse, stopPips);
                if (vol < Symbol.VolumeInUnitsMin) return;

                int tpPips = (int)Math.Ceiling(stopPips * TpRR);
                var res = ExecuteMarketOrder(TradeType.Sell, SymbolName, vol, LabelName, stopPips, tpPips);
                if (res.IsSuccessful && res.Position != null)
                {
                    var p = res.Position;
                    StoreInitialRisk(p);
                    _rrStepAppliedIndex[p.Id] = -1;
                    _tradesToday++;
                }
            }

            if (_enableDefensiveMode && _defensiveMode && _ddPct < Math.Max(0.0, DdThresholdPct * 0.5))
                _defensiveMode = false;
        }

        protected override void OnTick()
        {
            UpdateDrawdown();

            if (_enableDefensiveMode && !_defensiveMode && _ddPct >= DdThresholdPct)
                _defensiveMode = true;

            ParseRrStepsIfChanged();

            var positions = Positions.FindAll(LabelName, SymbolName);

            // --- múltiples tramos desde string ---
            if (_rrSteps.Count > 0)
            {
                foreach (var p in positions)
                {
                    if (p == null || !p.StopLoss.HasValue) continue;

                    double rrNow = CurrentRR(p);
                    if (!_rrStepAppliedIndex.ContainsKey(p.Id)) _rrStepAppliedIndex[p.Id] = -1;

                    int lastApplied = _rrStepAppliedIndex[p.Id];
                    int targetIdx = lastApplied;

                    for (int i = lastApplied + 1; i < _rrSteps.Count; i++)
                    {
                        if (rrNow >= _rrSteps[i].Trigger) targetIdx = i;
                        else break;
                    }

                    if (targetIdx > lastApplied)
                    {
                        double newSl = SlPriceAtRR(p, Math.Max(0.0, _rrSteps[targetIdx].To));
                        bool improves = p.TradeType == TradeType.Buy ? newSl > p.StopLoss.Value : newSl < p.StopLoss.Value;

                        if (improves)
                        {
                            double? keepTp = p.TakeProfit.HasValue ? p.TakeProfit.Value : (double?)null;
                            var mod = ModifyPosition(p, newSl, keepTp);
                            if (!mod.IsSuccessful)
                                Print("ModifyPosition (RR tramo idx {0}) falló: {1}", targetIdx, mod.Error);
                        }
                        _rrStepAppliedIndex[p.Id] = targetIdx;
                    }
                }
            }

            if (!CloseAtSessionEnd) return;
            var nowUtc = Server.Time;
            if (nowUtc >= _flattenUtc && HasOpenPosition())
                CloseAllPositions();
        }

        protected override void OnTimer()
        {
            if (!CloseAtSessionEnd) return;
            var nowUtc = Server.Time;
            if (nowUtc >= _flattenUtc && HasOpenPosition())
                CloseAllPositions();
        }

        // Limpieza de estado por posición cerrada
        protected override void OnPositionClosed(Position position)
        {
            if (position == null) return;
            _initRiskByPos.Remove(position.Id);
            _rrStepAppliedIndex.Remove(position.Id);
        }

        // --- Utilidades ---
        private void UpdateDrawdown()
        {
            double eq = Account.Equity;
            if (eq > _maxEquity) _maxEquity = eq;
            if (_maxEquity > 0)
                _ddPct = (_maxEquity - eq) / _maxEquity * 100.0;
        }

        private bool ReachedRR(Position p, double rr)
        {
            double risk = BaseRisk(p);
            if (risk <= 0) return false;

            double target = p.TradeType == TradeType.Buy
                ? p.EntryPrice + rr * risk
                : p.EntryPrice - rr * risk;

            return p.TradeType == TradeType.Buy
                ? Symbol.Bid >= target
                : Symbol.Ask <= target;
        }

        private double CurrentRR(Position p)
        {
            double risk = BaseRisk(p);
            if (risk <= 0) return 0;

            double cur = p.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            double reward = p.TradeType == TradeType.Buy ? (cur - p.EntryPrice) : (p.EntryPrice - cur);
            return reward / risk;
        }

        private double SlPriceAtRR(Position p, double rr)
        {
            double risk = BaseRisk(p);
            if (risk <= 0) return p.StopLoss ?? p.EntryPrice;

            if (p.TradeType == TradeType.Buy)
                return p.EntryPrice + rr * risk;
            else
                return p.EntryPrice - rr * risk;
        }

        private double BaseRisk(Position p)
        {
            double r;
            if (_initRiskByPos.TryGetValue(p.Id, out r) && r > 0) return r;
            return RiskDistancePrice(p);
        }

        private void StoreInitialRisk(Position p)
        {
            if (p == null || !p.StopLoss.HasValue) return;
            double r = p.TradeType == TradeType.Buy
                ? p.EntryPrice - p.StopLoss.Value
                : p.StopLoss.Value - p.EntryPrice;

            if (r > 0)
                _initRiskByPos[p.Id] = r;
        }

        private double RiskDistancePrice(Position p)
        {
            if (!p.StopLoss.HasValue) return 0;
            return p.TradeType == TradeType.Buy
                ? p.EntryPrice - p.StopLoss.Value
                : p.StopLoss.Value - p.EntryPrice;
        }

        private int PipsFrom(double entry, double stopPrice, TradeType side)
        {
            double distance = side == TradeType.Buy ? entry - stopPrice : stopPrice - entry;
            if (distance <= 0) return 0;
            int stopPips = (int)Math.Ceiling(distance / Symbol.PipSize);
            return stopPips;
        }

        private double VolumeForRisk(double riskPercent, int stopPips)
        {
            double riskMoney = Account.Balance * (riskPercent / 100.0);
            if (stopPips <= 0 || Symbol.PipValue <= 0) return 0;

            double rawUnits = riskMoney / (stopPips * Symbol.PipValue);
            double vol = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.ToNearest);

            if (vol < Symbol.VolumeInUnitsMin) return 0;
            if (vol > Symbol.VolumeInUnitsMax) vol = Symbol.VolumeInUnitsMax;
            return vol;
        }

        private bool HasOpenPosition()
        {
            var positions = Positions.FindAll(LabelName, SymbolName);
            return positions != null && positions.Length > 0;
        }

        private void CloseAllPositions()
        {
            foreach (var p in Positions.FindAll(LabelName, SymbolName))
                ClosePosition(p);
        }

        private void RecomputeSessionBounds(DateTime nowUtc)
        {
            var nowEt = UtcToEt(nowUtc);
            if (nowEt.Date != _currentEtDate)
            {
                _currentEtDate = nowEt.Date;

                _orStartEt = _currentEtDate.AddHours(9).AddMinutes(30);
                _sessEndEt = _currentEtDate.AddHours(16);

                TimeSpan cutoffSpan;
                if (!TimeSpan.TryParse(NoTradeAfterEtStr, out cutoffSpan))
                    cutoffSpan = new TimeSpan(11, 0, 0);

                _cutoffEt = _currentEtDate + cutoffSpan;

                _orStartUtc = EtToUtc(_orStartEt);
                _sessEndUtc = EtToUtc(_sessEndEt);
                _flattenUtc = EtToUtc(_sessEndEt.AddSeconds(-CloseBufferSeconds));
                _cutoffUtc = EtToUtc(_cutoffEt);

                _tradesToday = 0;
            }
        }

        // UTC <-> ET con DST EE. UU.
        private DateTime UtcToEt(DateTime utc)
        {
            return IsEtDstByLocalDate(utc.AddHours(-5)) ? utc.AddHours(-4) : utc.AddHours(-5);
        }

        private DateTime EtToUtc(DateTime etLocal)
        {
            int offset = IsEtDstByLocalDate(etLocal) ? -4 : -5;
            return etLocal.AddHours(-offset);
        }

        private bool IsEtDstByLocalDate(DateTime etLocal)
        {
            int y = etLocal.Year;
            var start = NthWeekdayOfMonth(y, 3, DayOfWeek.Sunday, 2).AddHours(2);
            var end = NthWeekdayOfMonth(y, 11, DayOfWeek.Sunday, 1).AddHours(2);
            return etLocal >= start && etLocal < end;
        }

        private DateTime NthWeekdayOfMonth(int year, int month, DayOfWeek dow, int n)
        {
            var first = new DateTime(year, month, 1);
            int offset = ((int)dow - (int)first.DayOfWeek + 7) % 7;
            int day = 1 + offset + (n - 1) * 7;
            return new DateTime(year, month, day);
        }

        private void ParseRrStepsIfChanged()
        {
            if (_rrStepsRaw == RrStepsStr) return;
            _rrStepsRaw = RrStepsStr;
            _rrSteps.Clear();

            if (string.IsNullOrWhiteSpace(RrStepsStr)) return;

            var parts = RrStepsStr.Split(',');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                double trig, to;
                if (double.TryParse(parts[i].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out trig) &&
                    double.TryParse(parts[i + 1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out to))
                {
                    if (trig >= 0 && to >= 0)
                        _rrSteps.Add(new RrStep { Trigger = trig, To = to });
                }
            }
        }

        // =========================
        // ESTRUCTURA MTF
        // =========================
        private bool GetStructureTrend(out bool uptrend, out bool downtrend)
        {
            uptrend = false;
            downtrend = false;

            if (_structBars == null || _structBars.Count < 2 * (SwingPivot + 1) + 5)
                return false;

            int lastClosed = _structBars.Count - 2; // última barra cerrada en TF estructura
            if (lastClosed < SwingPivot + 1) return false;

            // obtener últimos 2 pivot highs y 2 pivot lows confirmados
            double h1, h2, l1, l2;
            bool okH = TryGetLastTwoPivots(_structBars, lastClosed, SwingPivot, true, out h1, out h2);
            bool okL = TryGetLastTwoPivots(_structBars, lastClosed, SwingPivot, false, out l1, out l2);
            if (!okH || !okL) return false;

            uptrend = (h1 > h2) && (l1 > l2);
            downtrend = (h1 < h2) && (l1 < l2);
            return true;
        }

        private bool TryGetLastTwoPivots(Bars b, int fromIdx, int L, bool high, out double p1, out double p2)
        {
            p1 = 0; p2 = 0;
            int found = 0;
            int start = Math.Max(L, fromIdx - StructureMaxLookback);
            int end = fromIdx - L;

            for (int i = end; i >= start; i--)
            {
                if (IsPivot(b, i, L, high))
                {
                    if (found == 0) { p1 = high ? b.HighPrices[i] : b.LowPrices[i]; found = 1; }
                    else { p2 = high ? b.HighPrices[i] : b.LowPrices[i]; return true; }
                }
            }
            return false;
        }

        private bool IsPivot(Bars b, int i, int L, bool high)
        {
            if (i - L < 0 || i + L >= b.Count) return false;

            if (high)
            {
                double val = b.HighPrices[i];
                for (int k = i - L; k <= i + L; k++)
                    if (b.HighPrices[k] > val) return false;
                return true;
            }
            else
            {
                double val = b.LowPrices[i];
                for (int k = i - L; k <= i + L; k++)
                    if (b.LowPrices[k] < val) return false;
                return true;
            }
        }

        private TimeFrame TfFromMinutes(int m)
        {
            switch (m)
            {
                case 1: return TimeFrame.Minute;
                case 2: return TimeFrame.Minute2;
                case 3: return TimeFrame.Minute3;
                case 4: return TimeFrame.Minute4;
                case 5: return TimeFrame.Minute5;
                case 10: return TimeFrame.Minute10;
                case 15: return TimeFrame.Minute15;
                case 20: return TimeFrame.Minute20;
                case 30: return TimeFrame.Minute30;
                case 45: return TimeFrame.Minute45;
                case 60: return TimeFrame.Hour;
                case 120: return TimeFrame.Hour2;
                case 180: return TimeFrame.Hour3;
                case 240: return TimeFrame.Hour4;
                case 360: return TimeFrame.Hour6;
                case 720: return TimeFrame.Hour12;
                case 1440: return TimeFrame.Daily;
                default:
                    // aproxima al más cercano soportado
                    if (m < 2) return TimeFrame.Minute;
                    if (m < 3) return TimeFrame.Minute2;
                    if (m < 4) return TimeFrame.Minute3;
                    if (m < 5) return TimeFrame.Minute4;
                    if (m < 10) return TimeFrame.Minute5;
                    if (m < 15) return TimeFrame.Minute10;
                    if (m < 20) return TimeFrame.Minute15;
                    if (m < 30) return TimeFrame.Minute20;
                    if (m < 45) return TimeFrame.Minute30;
                    if (m < 60) return TimeFrame.Minute45;
                    if (m < 120) return TimeFrame.Hour;
                    if (m < 180) return TimeFrame.Hour2;
                    if (m < 240) return TimeFrame.Hour3;
                    if (m < 360) return TimeFrame.Hour4;
                    if (m < 720) return TimeFrame.Hour6;
                    if (m < 1440) return TimeFrame.Hour12;
                    return TimeFrame.Daily;
            }
        }

        private bool ShouldUseStructureFilter()
        {
            if (UseStructureFilter == StructureFilterMode.Disabled) return false;

            // Activa el filtro si la volatilidad está por debajo de un valor absoluto
            if (UseStructureFilter == StructureFilterMode.Fixed)
            {
                double atrPct = DailyAtrPct();
                if (atrPct < 0) return true;           // sin datos suficientes: conserva filtro
                return atrPct <= AtrPctThreshold;      // baja vol -> filtra; alta vol -> no filtra
            }

            // Compara la volatilidad actual con su propio promedio histórico
            if (UseStructureFilter == StructureFilterMode.Auto)
            {
                double ratio = DailyAtrPctRatio();   // < 1 = vol actual por debajo de su media
                if (ratio < 0) return true;          // sin datos suficientes: conserva filtro
                return ratio <= AtrRatioThreshold;   // vol baja relativa => usar filtro
            }
            return false;

        }

        private double DailyAtrPct()
        {
            // ATR% = ATR_D1 / SMA_Close_D1 * 100
            if (_d1Bars == null || _d1Bars.Count < AtrDays + 2) return -1;

            int last = _d1Bars.Count - 2; // última barra D1 cerrada
            double sumTR = 0, sumClose = 0;

            for (int i = last - AtrDays + 1; i <= last; i++)
            {
                double high = _d1Bars.HighPrices[i];
                double low = _d1Bars.LowPrices[i];
                double prev = _d1Bars.ClosePrices[i - 1];

                double tr = Math.Max(high - low, Math.Max(Math.Abs(high - prev), Math.Abs(low - prev)));
                sumTR += tr;
                sumClose += _d1Bars.ClosePrices[i];
            }

            double atr = sumTR / AtrDays;
            double sma = sumClose / AtrDays;
            if (sma <= 0) return -1;

            return atr / sma * 100.0;
        }

        // ratio = ATR%(hoy) / SMA(ATR%, N)
        private double DailyAtrPctRatio()
        {
            if (_d1Bars == null) return -1;

            int need = Math.Max(AtrDays, AtrPctSmaPeriod) + 2;
            if (_d1Bars.Count < need) return -1;

            int last = _d1Bars.Count - 2; // última D1 cerrada

            // ATR% actual
            double atrNow = 0, smaCloseNow = 0;
            for (int i = last - AtrDays + 1; i <= last; i++)
            {
                double h = _d1Bars.HighPrices[i];
                double l = _d1Bars.LowPrices[i];
                double pc = _d1Bars.ClosePrices[i - 1];
                double tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                atrNow += tr;
                smaCloseNow += _d1Bars.ClosePrices[i];
            }
            atrNow /= AtrDays;
            double meanCloseNow = smaCloseNow / AtrDays;
            if (meanCloseNow <= 0) return -1;
            double atrPctNow = atrNow / meanCloseNow * 100.0;

            // SMA de ATR% (período AtrPctSmaPeriod)
            double sumAtrPct = 0;
            for (int j = last - AtrPctSmaPeriod + 1; j <= last; j++)
            {
                // ATR% día j
                double atr = 0, sumClose = 0;
                for (int i = j - AtrDays + 1; i <= j; i++)
                {
                    double h = _d1Bars.HighPrices[i];
                    double l = _d1Bars.LowPrices[i];
                    double pc = _d1Bars.ClosePrices[i - 1];
                    double tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                    atr += tr;
                    sumClose += _d1Bars.ClosePrices[i];
                }
                atr /= AtrDays;
                double meanClose = sumClose / AtrDays;
                if (meanClose <= 0) return -1;
                sumAtrPct += atr / meanClose * 100.0;
            }
            double smaAtrPct = sumAtrPct / AtrPctSmaPeriod;
            if (smaAtrPct <= 0) return -1;

            return atrPctNow / smaAtrPct;
        }
    }
}
