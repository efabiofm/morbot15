using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Indicators;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class BARSignalsTrader_CloseBar_Buffer : Robot
    {
        // --- Parámetros ---
        [Parameter("Riesgo %", DefaultValue = 2.0, MinValue = 0.1)]
        public double RiskPercent { get; set; }

        [Parameter("Offset SL (pips)", DefaultValue = 20, MinValue = 0)]
        public int ExtraSLPips { get; set; }

        [Parameter("TP en múltiplos de R", DefaultValue = 2.0, MinValue = 0.1)]
        public double TpRR { get; set; }

        [Parameter("Distancia 1er parcial (R)", DefaultValue = 0.0, MinValue = 0.0)]
        public double PartialRR { get; set; }

        [Parameter("Cerrar al fin de sesión", DefaultValue = true)]
        public bool FlatAtSessionEnd { get; set; }

        [Parameter("Límite de trades por día", DefaultValue = 1, MinValue = 0)]
        public int DailyTradeLimit { get; set; }

        [Parameter("Hora límite sin trades (ET, HH:mm)", DefaultValue = "10:30")]
        public string NoTradeAfterEtStr { get; set; }

        // --- Control de DD dinámico ---
        [Parameter("Umbral DD Equity (%)", DefaultValue = 0.0, MinValue = 0.0)]
        public double DdThresholdPct { get; set; }

        [Parameter("Riesgo % en defensa", DefaultValue = 1.0, MinValue = 0.1)]
        public double DefensiveRiskPercent { get; set; }

        // --- NUEVO: Tramos RR vía string ---
        // Formato: "trigger1,to1,trigger2,to2,..."
        // Ej: "2,0,3,2,3.5,3"
        [Parameter("RR Tracts (trigger,to,...)", DefaultValue = "")]
        public string RrStepsStr { get; set; }

        // --- Estado ---
        private string _labelName = "BARSignalsTrader_CloseBar_Buffer";
        private int _closeBufferSeconds = 60;
        private BARSignals _sig;
        private DateTime _currentEtDate = DateTime.MinValue;
        private DateTime _orStartEt, _sessEndEt, _cutoffEt;
        private DateTime _orStartUtc, _sessEndUtc, _flattenUtc, _cutoffUtc;
        private DateTime _startedUtc;

        private readonly HashSet<long> _partialDone = new HashSet<long>();
        private readonly HashSet<long> _slMovedByRR = new HashSet<long>(); // legacy 1 tramo
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

        protected override void OnStart()
        {
            _sig = Indicators.GetIndicator<BARSignals>();
            _startedUtc = Server.Time;
            _maxEquity = Account.Equity;
            _ddPct = 0;
            _enableDefensiveMode = DdThresholdPct > 0.0;
            _defensiveMode = false;

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

            if (FlatAtSessionEnd && nowUtc >= _flattenUtc && HasOpenPosition())
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

            bool buy  = _sig.BuySignal.Last(1)  > 0;
            bool sell = _sig.SellSignal.Last(1) > 0;
            if (!buy && !sell) return;

            double barOpen  = Bars.OpenPrices.Last(1);
            double barHigh  = Bars.HighPrices.Last(1);
            double barLow   = Bars.LowPrices.Last(1);

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
                var res = ExecuteMarketOrder(TradeType.Buy, SymbolName, vol, _labelName, stopPips, tpPips);
                if (res.IsSuccessful && res.Position != null)
                {
                    var p = res.Position;
                    StoreInitialRisk(p);
                    _rrStepAppliedIndex[p.Id] = -1;
                    _partialDone.Remove(p.Id);
                    _slMovedByRR.Remove(p.Id);
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
                var res = ExecuteMarketOrder(TradeType.Sell, SymbolName, vol, _labelName, stopPips, tpPips);
                if (res.IsSuccessful && res.Position != null)
                {
                    var p = res.Position;
                    StoreInitialRisk(p);
                    _rrStepAppliedIndex[p.Id] = -1;
                    _partialDone.Remove(p.Id);
                    _slMovedByRR.Remove(p.Id);
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

            var positions = Positions.FindAll(_labelName, SymbolName);

            // --- NUEVO: múltiples tramos desde string ---
            if (_rrSteps.Count > 0)
            {
                foreach (var p in positions)
                {
                    if (p == null || !p.StopLoss.HasValue) continue;

                    double rrNow = CurrentRR(p);
                    if (!_rrStepAppliedIndex.ContainsKey(p.Id)) _rrStepAppliedIndex[p.Id] = -1;

                    int lastApplied = _rrStepAppliedIndex[p.Id];
                    int targetIdx = lastApplied;

                    // encuentra el tramo más alto alcanzado
                    for (int i = lastApplied + 1; i < _rrSteps.Count; i++)
                    {
                        if (rrNow >= _rrSteps[i].Trigger) targetIdx = i;
                        else break;
                    }

                    if (targetIdx > lastApplied)
                    {
                        double newSl = SlPriceAtRR(p, Math.Max(0.0, _rrSteps[targetIdx].To));

                        bool improves = p.TradeType == TradeType.Buy
                            ? newSl > p.StopLoss.Value
                            : newSl < p.StopLoss.Value;

                        if (improves)
                        {
                            double? keepTp = p.TakeProfit.HasValue ? p.TakeProfit.Value : (double?)null;
                            var mod = ModifyPosition(p, newSl, keepTp);
                            if (!mod.IsSuccessful)
                                Print("ModifyPosition (RR tramo idx {0}) falló: {1}", targetIdx, mod.Error);
                        }

                        // Marcar como aplicado aunque no mejore (por redondeo), para no reintentar cada tick
                        _rrStepAppliedIndex[p.Id] = targetIdx;
                    }
                }
            }

            // --- Parcial en 1R (si aplica) ---
            if (PartialRR > 0.0)
            {
                foreach (var p in positions)
                {
                    if (p == null || !p.StopLoss.HasValue) continue;
                    if (_partialDone.Contains(p.Id)) continue;

                    if (ReachedRR(p, PartialRR))
                    {
                        double toClose = Symbol.NormalizeVolumeInUnits(p.VolumeInUnits * 0.5, RoundingMode.ToNearest);
                        if (toClose >= Symbol.VolumeInUnitsMin)
                            ClosePosition(p, toClose);

                        double tpKeep = p.TakeProfit.HasValue ? p.TakeProfit.Value : p.EntryPrice;
                        double slBE   = p.EntryPrice;

                        var mod = ModifyPosition(p, slBE, tpKeep);
                        if (!mod.IsSuccessful) Print("ModifyPosition (parcial) falló: {0}", mod.Error);

                        _partialDone.Add(p.Id);
                    }
                }
            }

            if (!FlatAtSessionEnd) return;
            var nowUtc = Server.Time;
            if (nowUtc >= _flattenUtc && HasOpenPosition())
                CloseAllPositions();
        }

        protected override void OnTimer()
        {
            if (!FlatAtSessionEnd) return;
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
            _partialDone.Remove(position.Id);
            _slMovedByRR.Remove(position.Id);
        }

        // --- Utilidades ---
        private void UpdateDrawdown()
        {
            double eq = Account.Equity;
            if (eq > _maxEquity) _maxEquity = eq;
            if (_maxEquity > 0)
                _ddPct = (_maxEquity - eq) / _maxEquity * 100.0;
        }

        // Todas estas usan riesgo inicial si existe
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

        // SL a +RR desde la ENTRADA (lado de ganancia). rr=0 => BE. Usa riesgo inicial si existe.
        private double SlPriceAtRR(Position p, double rr)
        {
            double risk = BaseRisk(p);
            if (risk <= 0) return p.StopLoss ?? p.EntryPrice;

            if (p.TradeType == TradeType.Buy)
                return p.EntryPrice + rr * risk;   // por encima de la entrada
            else
                return p.EntryPrice - rr * risk;   // por debajo de la entrada
        }

        // Riesgo “base”: inicial si lo tenemos; si no, cae al SL actual (último recurso)
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
            var positions = Positions.FindAll(_labelName, SymbolName);
            return positions != null && positions.Length > 0;
        }

        private void CloseAllPositions()
        {
            foreach (var p in Positions.FindAll(_labelName, SymbolName))
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

                _cutoffEt  = _currentEtDate + cutoffSpan;

                _orStartUtc = EtToUtc(_orStartEt);
                _sessEndUtc = EtToUtc(_sessEndEt);
                _flattenUtc = EtToUtc(_sessEndEt.AddSeconds(-_closeBufferSeconds));
                _cutoffUtc  = EtToUtc(_cutoffEt);

                _tradesToday = 0;
                _partialDone.Clear();
                _slMovedByRR.Clear();
                // No limpiamos _initRiskByPos/_rrStepAppliedIndex aquí porque puede haber posiciones abiertas que sobrevivan al día si FlatAtSessionEnd=false
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
            var end   = NthWeekdayOfMonth(y, 11, DayOfWeek.Sunday, 1).AddHours(2);
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
    }
}
