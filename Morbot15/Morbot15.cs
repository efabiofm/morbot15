using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Indicators;
using System;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class BARSignalsTrader_CloseBar_Buffer : Robot
    {
        // --- Parámetros ---
        [Parameter("Riesgo %", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Offset SL (pips)", DefaultValue = 20, MinValue = 0)]
        public int ExtraSLPips { get; set; }

        [Parameter("TP en múltiplos de R", DefaultValue = 2.0, MinValue = 0.1)]
        public double TpRR { get; set; }

        [Parameter("Tomar parcial en 1er objetivo", DefaultValue = true)]
        public bool EnablePartial { get; set; }

        [Parameter("Distancia 1er parcial (R)", DefaultValue = 1.0, MinValue = 0.1)]
        public double PartialRR { get; set; }

        [Parameter("Operar solo sesión NY", DefaultValue = true)]
        public bool OnlyNySession { get; set; }

        [Parameter("Cerrar al fin de sesión", DefaultValue = true)]
        public bool FlatAtSessionEnd { get; set; }

        [Parameter("Buffer cierre (seg)", DefaultValue = 60, MinValue = 5, MaxValue = 900)]
        public int CloseBufferSeconds { get; set; }

        [Parameter("Límite de trades por día", DefaultValue = 2, MinValue = 0, MaxValue = 20)]
        public int DailyTradeLimit { get; set; }

        [Parameter("Hora límite sin trades (ET, HH:mm)", DefaultValue = "11:00")]
        public string NoTradeAfterEtStr { get; set; }

        [Parameter("Etiqueta", DefaultValue = "BARSignalsTrader_CloseBar_Buffer")]
        public string LabelName { get; set; }

        // --- Estado ---
        private BARSignals _sig;
        private DateTime _currentEtDate = DateTime.MinValue;
        private DateTime _orStartEt, _sessEndEt, _cutoffEt;
        private DateTime _orStartUtc, _sessEndUtc, _flattenUtc, _cutoffUtc;
        private DateTime _startedUtc;

        private readonly HashSet<long> _partialDone = new HashSet<long>();
        private int _tradesToday = 0;

        protected override void OnStart()
        {
            _sig = Indicators.GetIndicator<BARSignals>();
            _startedUtc = Server.Time;
            RecomputeSessionBounds(_startedUtc);
            Timer.Start(1);
        }

        protected override void OnBar()
        {
            var nowUtc = Server.Time;

            RecomputeSessionBounds(nowUtc);

            if (FlatAtSessionEnd && nowUtc >= _flattenUtc && HasOpenPosition())
                CloseAllPositions();

            var barCloseUtc = Bars.OpenTimes.Last(0); // cierre de la vela previa
            if (barCloseUtc < _startedUtc) return;

            // Ventana sesión: [09:30 ET, 16:00 ET - buffer)
            if (OnlyNySession && (barCloseUtc < _orStartUtc || barCloseUtc >= _flattenUtc))
                return;

            // Cutoff: si no hubo trades antes de la hora límite, no abrir
            if (_tradesToday == 0 && barCloseUtc >= _cutoffUtc)
                return;

            // Límite diario
            if (DailyTradeLimit >= 0 && _tradesToday >= DailyTradeLimit)
                return;

            // Una sola posición
            if (HasOpenPosition()) return;

            // Señal de la vela que acaba de cerrar
            bool buy  = _sig.BuySignal.Last(1)  == 1.0;
            bool sell = _sig.SellSignal.Last(1) == -1.0;
            if (!buy && !sell) return;

            // Datos de la vela de señal (la que cerró)
            double barOpen  = Bars.OpenPrices.Last(1);
            double barHigh  = Bars.HighPrices.Last(1);
            double barLow   = Bars.LowPrices.Last(1);

            if (buy)
            {
                double entry = Symbol.Ask;
                double stopPrice = Math.Min(barLow, barOpen) - ExtraSLPips * Symbol.PipSize;
                int stopPips = PipsFrom(entry, stopPrice, TradeType.Buy);
                if (stopPips <= 0) return;

                double vol = VolumeForRisk(RiskPercent, stopPips);
                if (vol < Symbol.VolumeInUnitsMin) return;

                int tpPips = (int)Math.Ceiling(stopPips * TpRR);
                var res = ExecuteMarketOrder(TradeType.Buy, SymbolName, vol, LabelName, stopPips, tpPips);
                if (res.IsSuccessful && res.Position != null)
                {
                    _partialDone.Remove(res.Position.Id);
                    _tradesToday++;
                }
            }
            else if (sell)
            {
                double entry = Symbol.Bid;
                double stopPrice = Math.Max(barHigh, barOpen) + ExtraSLPips * Symbol.PipSize;
                int stopPips = PipsFrom(entry, stopPrice, TradeType.Sell);
                if (stopPips <= 0) return;

                double vol = VolumeForRisk(RiskPercent, stopPips);
                if (vol < Symbol.VolumeInUnitsMin) return;

                int tpPips = (int)Math.Ceiling(stopPips * TpRR);
                var res = ExecuteMarketOrder(TradeType.Sell, SymbolName, vol, LabelName, stopPips, tpPips);
                if (res.IsSuccessful && res.Position != null)
                {
                    _partialDone.Remove(res.Position.Id);
                    _tradesToday++;
                }
            }
        }

        protected override void OnTick()
        {
            // Parcial configurable al tick + mover SL a BE conservando TP
            if (EnablePartial)
            {
                var positions = Positions.FindAll(LabelName, SymbolName);
                foreach (var p in positions)
                {
                    if (p == null || !p.StopLoss.HasValue) continue;
                    if (_partialDone.Contains(p.Id)) continue;

                    if (ReachedRR(p, PartialRR))
                    {
                        // cerrar 50%
                        double toClose = Symbol.NormalizeVolumeInUnits(p.VolumeInUnits * 0.5, RoundingMode.ToNearest);
                        if (toClose >= Symbol.VolumeInUnitsMin)
                            ClosePosition(p, toClose);

                        // mover SL a BE y conservar TP existente
                        double tpKeep = p.TakeProfit.HasValue ? p.TakeProfit.Value : p.EntryPrice; // fallback
                        double slBE   = p.EntryPrice;

                        var mod = ModifyPosition(p, slBE, tpKeep);
                        if (!mod.IsSuccessful) Print("ModifyPosition fallo: {0}", mod.Error);

                        _partialDone.Add(p.Id);
                    }
                }
            }

            // Flatten por buffer si aplica
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

        // --- Utilidades ---
        private bool ReachedRR(Position p, double rr)
        {
            double risk = RiskDistancePrice(p);
            if (risk <= 0) return false;

            double target = p.TradeType == TradeType.Buy
                ? p.EntryPrice + rr * risk
                : p.EntryPrice - rr * risk;

            return p.TradeType == TradeType.Buy
                ? Symbol.Bid >= target
                : Symbol.Ask <= target;
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

                // Parse hora límite desde string (HH:mm o HH:mm:ss). Default 11:00 si falla.
                TimeSpan cutoffSpan;
                if (!TimeSpan.TryParse(NoTradeAfterEtStr, out cutoffSpan))
                    cutoffSpan = new TimeSpan(11, 0, 0);

                _cutoffEt  = _currentEtDate + cutoffSpan;

                _orStartUtc = EtToUtc(_orStartEt);
                _sessEndUtc = EtToUtc(_sessEndEt);
                _flattenUtc = EtToUtc(_sessEndEt.AddSeconds(-CloseBufferSeconds));
                _cutoffUtc  = EtToUtc(_cutoffEt);

                // reset diario
                _tradesToday = 0;
                _partialDone.Clear();
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
            var start = NthWeekdayOfMonth(y, 3, DayOfWeek.Sunday, 2).AddHours(2); // 2º dom marzo 02:00
            var end   = NthWeekdayOfMonth(y, 11, DayOfWeek.Sunday, 1).AddHours(2); // 1º dom nov 02:00
            return etLocal >= start && etLocal < end;
        }

        private DateTime NthWeekdayOfMonth(int year, int month, DayOfWeek dow, int n)
        {
            var first = new DateTime(year, month, 1);
            int offset = ((int)dow - (int)first.DayOfWeek + 7) % 7;
            int day = 1 + offset + (n - 1) * 7;
            return new DateTime(year, month, day);
        }
    }
}
