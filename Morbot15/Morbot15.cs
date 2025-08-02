using cAlgo.API;
using cAlgo.API.Internals;
using System;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class OpeningRangeBreakout : Robot
    {
        private double rangeHigh;
        private double rangeLow;
        private double rangeMid;

        private DateTime sessionStartUtc;
        private DateTime sessionEndUtc;
        private DateTime sessionCloseUtc;
        private DateTime currentSessionDate = DateTime.MinValue;

        private bool rangeDefined = false;
        private bool tradeExecutedToday = false;

        [Parameter("Risk Percent", DefaultValue = 2)]
        public double RiskPercent { get; set; }

        [Parameter("Risk-Reward Ratio", DefaultValue = 1.5)]
        public double RiskReward { get; set; }

        // Cálculo básico para saber si el día está dentro del horario de verano en EE.UU.
        private bool IsUsDaylightSaving(DateTime date)
        {
            int year = date.Year;

            DateTime dstStart = GetNthWeekdayOfMonth(year, 3, DayOfWeek.Sunday, 2);  // 2do domingo de marzo
            DateTime dstEnd = GetNthWeekdayOfMonth(year, 11, DayOfWeek.Sunday, 1);   // 1er domingo de noviembre

            return date >= dstStart && date < dstEnd;
        }

        private DateTime GetNthWeekdayOfMonth(int year, int month, DayOfWeek dayOfWeek, int occurrence)
        {
            DateTime date = new DateTime(year, month, 1);
            int count = 0;

            while (date.Month == month)
            {
                if (date.DayOfWeek == dayOfWeek)
                {
                    count++;
                    if (count == occurrence)
                        return date;
                }
                date = date.AddDays(1);
            }

            throw new Exception("Fecha no encontrada para DST");
        }

        protected override void OnBar()
        {
            DateTime now = Bars.OpenTimes.LastValue;

            // Detectar cambio de día
            if (now.Date != currentSessionDate)
            {
                currentSessionDate = now.Date;
                tradeExecutedToday = false;
                rangeDefined = false;

                // Ajustar por horario de verano (DST)
                // Si entre marzo y noviembre → apertura NY = 13:30 UTC
                // Si entre nov y marzo → apertura NY = 14:30 UTC
                bool isDST = IsUsDaylightSaving(now.Date);

                double nyOpenUtcHour = isDST ? 13.5 : 14.5;

                sessionStartUtc = currentSessionDate.AddHours(nyOpenUtcHour);   // 9:30 NY
                sessionEndUtc = sessionStartUtc.AddMinutes(15);                 // 9:45 NY
                sessionCloseUtc = sessionStartUtc.AddHours(6).AddMinutes(20);   // 15:55 NY

                Print("Nuevo día detectado: {0} | DST: {1} | Apertura NY: {2} UTC", currentSessionDate.ToShortDateString(), isDST, nyOpenUtcHour);
            }

            // Si hay una posición abierta y ya terminó la sesión, cerrarla
            if (now >= sessionCloseUtc)
            {
                foreach (var pos in Positions)
                {
                    if (pos.SymbolName == SymbolName)
                        ClosePosition(pos);
                }
                return;
            }

            if (!rangeDefined && now >= sessionEndUtc)
            {
                rangeHigh = double.MinValue;
                rangeLow = double.MaxValue;

                // Bucle para encontrar el High/Low del rango
                for (int i = Bars.Count - 1; i >= 0; i--)
                {
                    DateTime t = Bars.OpenTimes[i];
                    if (t >= sessionStartUtc && t < sessionEndUtc)
                    {
                        rangeHigh = Math.Max(rangeHigh, Bars.HighPrices[i]);
                        rangeLow = Math.Min(rangeLow, Bars.LowPrices[i]);
                    }
                }
                
                // Verificamos si encontramos al menos una barra para definir el rango
                if (rangeHigh != double.MinValue && rangeLow != double.MaxValue)
                {
                    rangeMid = (rangeHigh + rangeLow) / 2;
                    rangeDefined = true;
                    Print("Opening Range High: {0}, Low: {1}, Mid: {2}", rangeHigh, rangeLow, rangeMid);
                }
                else
                {
                    // Si no se encontró el rango, el bot espera
                    Print("No se encontraron barras para definir el rango de apertura. Esperando al próximo día.");
                    return; // Salir de la función OnBar para no ejecutar el trade
                }
            }

            // Esperar breakout con cierre por fuera del rango
            if (rangeDefined && Positions.Count == 0 && !tradeExecutedToday)
            {
                double close = Bars.ClosePrices.Last(1); // cierre de vela anterior

                if (close > rangeHigh) 
                {
                    ExecuteTrade(TradeType.Buy);
                    tradeExecutedToday = true;
                }
                else if (close < rangeLow) 
                {
                    ExecuteTrade(TradeType.Sell);
                    tradeExecutedToday = true;
                }
            }
        }

        private void ExecuteTrade(TradeType tradeType)
        {
            // --- 1. CALCULAR LA CANTIDAD DE RIESGO EN DÓLARES ---
            double accountBalance = Account.Balance;
            double riskAmountInUSD = accountBalance * (RiskPercent / 100);

            // --- 2. DETERMINAR LA DISTANCIA DEL STOP LOSS ---
            // El precio de entrada será el precio de mercado actual.
            double entryPrice;
            if (tradeType == TradeType.Buy)
            {
                entryPrice = Symbol.Ask;
            }
            else
            {
                entryPrice = Symbol.Bid;
            }
            
            double stopLossPriceDistance = Math.Abs(entryPrice - rangeMid);
            
            // Convertimos la distancia a pips.
            double stopLossPips = stopLossPriceDistance / Symbol.PipSize;

            // --- 3. CALCULAR EL VOLUMEN DE LA ORDEN EN UNIDADES ---
            double pipValue = Symbol.PipValue;
            double volumeInUnits = riskAmountInUSD / (stopLossPips * pipValue);
            
            // --- 4. AJUSTAR EL VOLUMEN A UN VALOR VÁLIDO ---
            double normalizedVolume = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            
            // Si el volumen no es válido, detenemos la ejecución.
            if (normalizedVolume <= 0)
            {
                Print("ERROR: El volumen calculado es cero o negativo. No se puede ejecutar la orden.");
                return;
            }

            // --- 5. CALCULAR LOS PRECIOS DE SL Y TP ---
            // El precio del Stop Loss es el punto medio del rango.
            double stopLossPrice = rangeMid;

            // El precio del Take Profit es 1:1, la misma distancia que el SL.
            double takeProfitPrice;
            double takeProfitDistance = stopLossPriceDistance * RiskReward;

            if (tradeType == TradeType.Buy)
            {
                takeProfitPrice = entryPrice + takeProfitDistance;
            }
            else
            {
                takeProfitPrice = entryPrice - takeProfitDistance;
            }

            // --- VERIFICACIONES DE SEGURIDAD ANTES DE EJECUTAR ---
            if (double.IsNaN(stopLossPrice) || double.IsInfinity(stopLossPrice) || 
                double.IsNaN(takeProfitPrice) || double.IsInfinity(takeProfitPrice))
            {
                Print("ERROR CRÍTICO: SL o TP calculados como valores no válidos. Cancelando la orden.");
                return;
            }
            
            // cTrader requiere que el SL y el TP no sean idénticos al precio de entrada.
            if (Math.Abs(stopLossPrice - entryPrice) < Symbol.PipSize ||
                Math.Abs(takeProfitPrice - entryPrice) < Symbol.PipSize)
            {
                Print("ERROR CRÍTICO: El SL o TP están demasiado cerca del precio de entrada. Cancelando la orden.");
                return;
            }

            // --- 6. EJECUTAR LA ORDEN Y ESTABLECER SL/TP ---
            // Primero, ejecutamos la orden de mercado.
            var result = ExecuteMarketOrder(tradeType, SymbolName, normalizedVolume, "ORB");

            if (result.IsSuccessful)
            {
                Position position = result.Position;
                
                // Verificamos que el objeto 'position' sea válido.
                if (position == null)
                {
                    Print("ERROR: La posición se ejecutó, pero el objeto 'position' es nulo. ¡Posición en riesgo!");
                    return;
                }

                // Intentamos establecer el SL y TP.
                var modifyResult = ModifyPosition(position, stopLossPrice, takeProfitPrice);

                // Si la modificación falla, lo intentamos de nuevo.
                if (!modifyResult.IsSuccessful)
                {
                    Print("ADVERTENCIA: Primer intento de establecer SL/TP fallido. Reintentando...");
                    
                    // Reintentamos inmediatamente.
                    modifyResult = ModifyPosition(position, stopLossPrice, takeProfitPrice);

                    // Si el segundo intento falla, la posición está "desnuda" y es un riesgo.
                    if (!modifyResult.IsSuccessful)
                    {
                        Print("ALERTA CRÍTICA: Segundo intento de SL/TP fallido. La posición está en riesgo. Cerrando de emergencia.");
                        ClosePosition(position);
                    }
                    else
                    {
                        Print("SL/TP establecidos con éxito en el segundo intento.");
                    }
                }
                else
                {
                    Print("Orden de {0} ejecutada con éxito. SL en {1}, TP en {2} establecidos.", 
                        tradeType, stopLossPrice, takeProfitPrice);
                }
            }
            else
            {
                Print("Fallo al ejecutar la orden: " + result.Error);
            }
        }
    }
}
